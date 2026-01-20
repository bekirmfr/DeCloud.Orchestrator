using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Orchestrator.Models;

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
}

public class WireGuardManager : IWireGuardManager
{
    private const string INTERFACE_NAME = "wg-relay-client";
    private const string PRIVATE_KEY_PATH = "/etc/wireguard/orchestrator-private.key";
    private const string PUBLIC_KEY_PATH = "/etc/wireguard/orchestrator-public.key";
    private const string TUNNEL_IP = "10.20.0.1";

    private readonly ILogger<WireGuardManager> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private string? _cachedPublicKey;

    public WireGuardManager(
        ILogger<WireGuardManager> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
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
}