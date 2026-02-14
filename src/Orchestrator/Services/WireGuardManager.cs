using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Minimal WireGuard management for orchestrator connectivity to relay network.
/// Handles orchestrator's WireGuard client configuration to reach CGNAT nodes.
/// </summary>
public interface IWireGuardManager
{
    /// <summary>
    /// Get orchestrator's WireGuard public key (generates keypair if not exists)
    /// </summary>
    Task<string> GetOrchestratorPublicKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// Add relay as peer to orchestrator's WireGuard client
    /// </summary>
    Task<bool> AddRelayPeerAsync(Node relayNode, CancellationToken ct = default);

    /// <summary>
    /// Remove relay peer from orchestrator's WireGuard client
    /// </summary>
    Task<bool> RemoveRelayPeerAsync(string relayPublicKey, CancellationToken ct = default);

    /// <summary>
    /// Add orchestrator as peer to relay VM via relay management API
    /// </summary>
    Task<bool> RegisterWithRelayAsync(Node relayNode, string cgnatNodeId, string tunnelIp, CancellationToken ct = default);
    Task<bool> HasRelayPeerAsync(Node relayNode, CancellationToken ct = default);
    Task<string> DerivePublicKeyAsync(string privateKey, CancellationToken ct);
    string? ExtractPrivateKeyFromConfig(string wgConfig);
    Task<string?> ExtractPublicKeyFromConfigAsync(string wgConfig, CancellationToken ct = default);
    Task<(string privateKey, string publicKey)> GenerateKeypairAsync(CancellationToken ct = default);
    bool IsValidWireGuardKey(string key);

    /// <summary>
    /// Cross-peer a newly-registered relay with all other active relays.
    /// Calls each relay's add-relay-peer API to create bidirectional peering.
    /// </summary>
    Task CrossPeerRelaysAsync(Node newRelay, CancellationToken ct = default);
}

public class WireGuardManager : IWireGuardManager
{
    private const string INTERFACE_NAME = "wg-relay-client";
    private const string PRIVATE_KEY_PATH = "/etc/wireguard/orchestrator-private.key";
    private const string PUBLIC_KEY_PATH = "/etc/wireguard/orchestrator-public.key";
    private const string TUNNEL_IP = "10.20.0.1";

    private readonly ILogger<WireGuardManager> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DataStore _dataStore;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _cachedPublicKey;

    public WireGuardManager(
        ILogger<WireGuardManager> logger,
        IHttpClientFactory httpClientFactory,
        DataStore dataStore)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dataStore = dataStore;
    }

    public async Task<string> GetOrchestratorPublicKeyAsync(CancellationToken ct = default)
    {
        if (_cachedPublicKey != null)
        {
            return _cachedPublicKey;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_cachedPublicKey != null)
            {
                return _cachedPublicKey;
            }

            // Check if keys already exist
            if (File.Exists(PUBLIC_KEY_PATH))
            {
                _cachedPublicKey = (await File.ReadAllTextAsync(PUBLIC_KEY_PATH, ct)).Trim();
                _logger.LogInformation("Loaded existing orchestrator WireGuard public key");
                return _cachedPublicKey;
            }

            // Generate keypair
            _logger.LogInformation("Generating orchestrator WireGuard keypair...");

            Directory.CreateDirectory(Path.GetDirectoryName(PRIVATE_KEY_PATH)!);

            // Generate private key
            var genKeyProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "genkey",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            genKeyProcess.Start();
            var privateKey = (await genKeyProcess.StandardOutput.ReadToEndAsync(ct)).Trim();
            await genKeyProcess.WaitForExitAsync(ct);

            if (genKeyProcess.ExitCode != 0)
            {
                throw new Exception("Failed to generate WireGuard private key");
            }

            // Save private key
            await File.WriteAllTextAsync(PRIVATE_KEY_PATH, privateKey, ct);
            File.SetUnixFileMode(PRIVATE_KEY_PATH, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Generate public key
            var pubKeyProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "pubkey",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            pubKeyProcess.Start();
            await pubKeyProcess.StandardInput.WriteLineAsync(privateKey);
            await pubKeyProcess.StandardInput.FlushAsync();
            pubKeyProcess.StandardInput.Close();

            var publicKey = (await pubKeyProcess.StandardOutput.ReadToEndAsync(ct)).Trim();
            await pubKeyProcess.WaitForExitAsync(ct);

            if (pubKeyProcess.ExitCode != 0)
            {
                throw new Exception("Failed to generate WireGuard public key");
            }

            // Save public key
            await File.WriteAllTextAsync(PUBLIC_KEY_PATH, publicKey, ct);

            _cachedPublicKey = publicKey;
            _logger.LogInformation("Generated orchestrator WireGuard public key: {PublicKey}", publicKey);

            return publicKey;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<bool> AddRelayPeerAsync(Node relayNode, CancellationToken ct = default)
    {
        if (relayNode.RelayInfo == null || string.IsNullOrEmpty(relayNode.RelayInfo.WireGuardPublicKey))
        {
            _logger.LogError("Cannot add relay peer: missing WireGuard public key");
            return false;
        }

        try
        {
            // Use relay-specific subnet for allowed-ips
            var relaySubnet = relayNode.RelayInfo.RelaySubnet;
            var allowedIps = relaySubnet > 0
                ? $"10.20.{relaySubnet}.0/24"  // Relay-specific subnet
                : "10.20.0.0/16";               // Fallback for old relays

            _logger.LogInformation(
                "Adding relay {RelayId} as WireGuard peer " +
                "(endpoint: {Endpoint}, allowed-ips: {AllowedIps}, subnet: {Subnet})",
                relayNode.Id, relayNode.RelayInfo.WireGuardEndpoint, allowedIps, relaySubnet);

            // Add peer using wg command
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = $"set {INTERFACE_NAME} " +
                               $"peer {relayNode.RelayInfo.WireGuardPublicKey} " +
                               $"endpoint {relayNode.RelayInfo.WireGuardEndpoint} " +
                               $"allowed-ips {allowedIps} " +  // ✅ Relay-specific subnet
                               $"persistent-keepalive 25",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(ct);
                _logger.LogError("Failed to add relay peer: {Error}", error);
                return false;
            }

            // Save configuration
            await ExecuteCommandAsync("wg-quick", $"save {INTERFACE_NAME}", ct);

            _logger.LogInformation(
                "✓ Added relay {RelayId} as WireGuard peer (subnet 10.20.{Subnet}.0/24)",
                relayNode.Id, relaySubnet);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding relay peer");
            return false;
        }
    }

    public async Task<bool> RemoveRelayPeerAsync(string relayPublicKey, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Removing relay peer: {PublicKey}", relayPublicKey[..16] + "...");

            await ExecuteCommandAsync("wg", $"set {INTERFACE_NAME} peer {relayPublicKey} remove", ct);
            await ExecuteCommandAsync("wg-quick", $"save {INTERFACE_NAME}", ct);

            _logger.LogInformation("✓ Removed relay peer");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing relay peer");
            return false;
        }
    }

    public async Task<bool> RegisterWithRelayAsync(
        Node relayNode,
        string cgnatNodeId,
        string tunnelIp,
        CancellationToken ct = default)
    {
        try
        {
            var orchestratorPublicKey = await GetOrchestratorPublicKeyAsync(ct);

            // Call relay VM's management API to add orchestrator as peer
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var relayApiUrl = $"http://{relayNode.PublicIp}:8080/api/relay/add-peer";

            var payload = new
            {
                public_key = orchestratorPublicKey,
                tunnel_ip = TUNNEL_IP,
                description = "orchestrator"
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            _logger.LogInformation(
                "Registering orchestrator with relay {RelayId} at {ApiUrl}",
                relayNode.Id, relayApiUrl);

            var response = await client.PostAsync(relayApiUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Failed to register with relay {RelayId}: HTTP {Status} - {Error}",
                    relayNode.Id, response.StatusCode, error);
                return false;
            }

            _logger.LogInformation(
                "✓ Registered orchestrator with relay {RelayId}",
                relayNode.Id);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error registering orchestrator with relay {RelayId}",
                relayNode.Id);
            return false;
        }
    }

    private async Task<(int ExitCode, string Output, string Error)> ExecuteCommandAsync(
        string command,
        string arguments,
        CancellationToken ct)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();

        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        var output = await outputTask;
        var error = await errorTask;

        return (process.ExitCode, output, error);
    }

    public async Task<bool> HasRelayPeerAsync(Node relayNode, CancellationToken ct = default)
    {
        if (relayNode.RelayInfo == null ||
            string.IsNullOrEmpty(relayNode.RelayInfo.WireGuardPublicKey))
        {
            return false;
        }

        try
        {
            var (exitCode, output, _) = await ExecuteCommandAsync(
                "wg",
                $"show {INTERFACE_NAME} peers",
                ct);

            if (exitCode == 0)
            {
                return output.Contains(relayNode.RelayInfo.WireGuardPublicKey);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    // Add these methods to src/Orchestrator/Services/WireGuardManager.cs

    /// <summary>
    /// Derive WireGuard public key from private key
    /// Uses 'wg pubkey' command to perform Curve25519 key derivation
    /// </summary>
    /// <param name="privateKey">Base64-encoded WireGuard private key</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Base64-encoded WireGuard public key</returns>
    public async Task<string> DerivePublicKeyAsync(string privateKey, CancellationToken ct = default)
    {
        try
        {
            // Use stdin piping instead of shell interpolation to prevent injection
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "pubkey",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            await process.StandardInput.WriteLineAsync(privateKey);
            await process.StandardInput.FlushAsync();
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to derive WireGuard public key: {error}");
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to derive WireGuard public key");
            throw;
        }
    }

    /// <summary>
    /// Extract private key from WireGuard configuration text
    /// Searches for "PrivateKey = ..." line in config
    /// </summary>
    /// <param name="wgConfig">WireGuard configuration text</param>
    /// <returns>Base64-encoded private key, or null if not found</returns>
    public string? ExtractPrivateKeyFromConfig(string wgConfig)
    {
        if (string.IsNullOrEmpty(wgConfig))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            wgConfig,
            @"PrivateKey\s*=\s*([A-Za-z0-9+/=]+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        if (match.Success)
        {
            return match.Groups[1].Value.Trim();
        }

        return null;
    }

    /// <summary>
    /// Extract public key from WireGuard configuration by deriving it from private key
    /// Convenience method that combines ExtractPrivateKeyFromConfig + DerivePublicKeyAsync
    /// </summary>
    /// <param name="wgConfig">WireGuard configuration text</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Base64-encoded public key, or null if private key not found in config</returns>
    public async Task<string?> ExtractPublicKeyFromConfigAsync(string wgConfig, CancellationToken ct = default)
    {
        var privateKey = ExtractPrivateKeyFromConfig(wgConfig);

        if (string.IsNullOrEmpty(privateKey))
        {
            _logger.LogWarning("Could not extract private key from WireGuard config");
            return null;
        }

        try
        {
            return await DerivePublicKeyAsync(privateKey, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not derive public key from extracted private key");
            return null;
        }
    }

    /// <summary>
    /// Generate a new WireGuard keypair (private + public)
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Tuple of (privateKey, publicKey)</returns>
    public async Task<(string privateKey, string publicKey)> GenerateKeypairAsync(CancellationToken ct = default)
    {
        try
        {
            // Generate private key
            var genProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "wg",
                    Arguments = "genkey",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            genProcess.Start();
            var privateKey = await genProcess.StandardOutput.ReadToEndAsync(ct);
            await genProcess.WaitForExitAsync(ct);

            if (genProcess.ExitCode != 0)
            {
                throw new InvalidOperationException("Failed to generate WireGuard private key");
            }

            privateKey = privateKey.Trim();

            // Derive public key from private key
            var publicKey = await DerivePublicKeyAsync(privateKey, ct);

            return (privateKey, publicKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate WireGuard keypair");
            throw;
        }
    }

    /// <summary>
    /// Validate that a string is a valid WireGuard key (base64, correct length)
    /// WireGuard keys are 32-byte Curve25519 keys encoded as 44-character base64 strings
    /// </summary>
    /// <param name="key">Key to validate</param>
    /// <returns>True if key format is valid</returns>
    public bool IsValidWireGuardKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        // WireGuard keys are base64-encoded 32-byte values
        // Base64(32 bytes) = 44 characters (with padding)
        if (key.Length != 44)
        {
            return false;
        }

        // Check if valid base64
        try
        {
            var bytes = Convert.FromBase64String(key);
            return bytes.Length == 32;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Cross-peer a newly-registered relay with all other active relays.
    /// For each existing active relay:
    ///   1. Tell existing relay about the new relay (add-relay-peer)
    ///   2. Tell new relay about the existing relay (add-relay-peer)
    /// Uses HMAC authentication via relay's WireGuard private key.
    /// </summary>
    public async Task CrossPeerRelaysAsync(Node newRelay, CancellationToken ct = default)
    {
        if (newRelay.RelayInfo == null)
        {
            _logger.LogWarning("Cannot cross-peer: new relay {RelayId} has no RelayInfo", newRelay.Id);
            return;
        }

        try
        {
            // Get all active relay nodes (excluding the new one)
            var allNodes = await _dataStore.GetAllNodesAsync();
            var existingRelays = allNodes
                .Where(n => n.RelayInfo != null
                         && n.RelayInfo.Status == RelayStatus.Active
                         && n.Id != newRelay.Id
                         && !string.IsNullOrEmpty(n.RelayInfo.WireGuardPublicKey)
                         && !string.IsNullOrEmpty(n.RelayInfo.WireGuardEndpoint)
                         && n.RelayInfo.RelaySubnet > 0)
                .ToList();

            if (existingRelays.Count == 0)
            {
                _logger.LogInformation(
                    "No existing active relays to cross-peer with relay {RelayId} (subnet {Subnet})",
                    newRelay.Id, newRelay.RelayInfo.RelaySubnet);
                return;
            }

            _logger.LogInformation(
                "Cross-peering relay {RelayId} (subnet {Subnet}) with {Count} existing relay(s)",
                newRelay.Id, newRelay.RelayInfo.RelaySubnet, existingRelays.Count);

            var successCount = 0;
            var failureCount = 0;

            foreach (var existingRelay in existingRelays)
            {
                try
                {
                    // 1. Tell existing relay about the new relay
                    var addedOnExisting = await CallAddRelayPeerAsync(
                        existingRelay, newRelay, ct);

                    // 2. Tell new relay about the existing relay
                    var addedOnNew = await CallAddRelayPeerAsync(
                        newRelay, existingRelay, ct);

                    if (addedOnExisting && addedOnNew)
                    {
                        successCount++;
                        _logger.LogInformation(
                            "✓ Cross-peered relay {NewRelay} ↔ {ExistingRelay}",
                            newRelay.Id, existingRelay.Id);
                    }
                    else
                    {
                        failureCount++;
                        _logger.LogWarning(
                            "Partial cross-peer failure: {NewRelay} ↔ {ExistingRelay} " +
                            "(onExisting={OnExisting}, onNew={OnNew})",
                            newRelay.Id, existingRelay.Id, addedOnExisting, addedOnNew);
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogError(ex,
                        "Error cross-peering relay {NewRelay} ↔ {ExistingRelay}",
                        newRelay.Id, existingRelay.Id);
                }
            }

            _logger.LogInformation(
                "Cross-peering complete for relay {RelayId}: {Success} succeeded, {Failed} failed",
                newRelay.Id, successCount, failureCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during cross-peering for relay {RelayId}", newRelay.Id);
        }
    }

    /// <summary>
    /// Call a relay's add-relay-peer endpoint to add another relay as its peer.
    /// </summary>
    private async Task<bool> CallAddRelayPeerAsync(
        Node targetRelay, Node peerRelay, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var apiUrl = $"http://{targetRelay.PublicIp}:8080/api/relay/add-relay-peer";

            var payload = new
            {
                public_key = peerRelay.RelayInfo!.WireGuardPublicKey,
                endpoint = peerRelay.RelayInfo.WireGuardEndpoint,
                allowed_ips = $"10.20.{peerRelay.RelayInfo.RelaySubnet}.0/24",
                relay_id = peerRelay.Id
            };

            var content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync(apiUrl, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning(
                    "Failed to add relay peer on {TargetRelay}: HTTP {Status} - {Error}",
                    targetRelay.Id, response.StatusCode, error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error calling add-relay-peer on {TargetRelay} for peer {PeerRelay}",
                targetRelay.Id, peerRelay.Id);
            return false;
        }
    }
}