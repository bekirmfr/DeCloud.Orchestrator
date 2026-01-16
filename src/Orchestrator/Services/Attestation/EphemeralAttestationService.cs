using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Ephemeral Key Attestation Service
/// 
/// Security model:
/// - Each challenge triggers fresh Ed25519 keypair generation INSIDE the VM
/// - Private key exists for ~20ms, then is zeroed
/// - Response must arrive within 100ms (wall-clock, measured by Orchestrator)
/// - Node cannot extract the ephemeral key fast enough to forge responses
/// 
/// The node CAN read VM memory, but reading takes TIME.
/// If the key exists for only milliseconds, there's no time to extract it.
/// </summary>
public interface IAttestationService
{
    /// <summary>
    /// Send a challenge to a VM and verify the response
    /// </summary>
    Task<AttestationVerificationResult> ChallengeVmAsync(
        string vmId,
        CancellationToken ct = default);

    /// <summary>
    /// Get liveness state for a VM
    /// </summary>
    VmLivenessState? GetLivenessState(string vmId);

    /// <summary>
    /// Check if billing is paused for a VM
    /// </summary>
    bool IsBillingPaused(string vmId);

    /// <summary>
    /// Get attestation statistics for a VM
    /// </summary>
    Task<VmAttestationStats?> GetVmStatsAsync(string vmId);
}

public class VmAttestationStats
{
    public string VmId { get; set; } = string.Empty;
    public int TotalChallenges { get; set; }
    public int SuccessfulChallenges { get; set; }
    public double SuccessRate { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public DateTime? LastAttestation { get; set; }
    public bool BillingPaused { get; set; }
}

public class EphemeralAttestationService : IAttestationService
{
    private readonly ILogger<EphemeralAttestationService> _logger;
    private readonly AttestationConfig _config;
    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;

    // Liveness state per VM
    private readonly Dictionary<string, VmLivenessState> _livenessStates = new();
    private readonly object _stateLock = new();

    // Response time tracking for statistics
    private readonly Dictionary<string, List<double>> _responseTimes = new();

    public EphemeralAttestationService(
        ILogger<EphemeralAttestationService> logger,
        IOptions<AttestationConfig> config,
        DataStore dataStore)
    {
        _logger = logger;
        _config = config.Value;
        _dataStore = dataStore;

        // HTTP client with strict timeout
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMilliseconds(_config.MaxResponseTimeMs + 100)
        };
    }

    public async Task<AttestationVerificationResult> ChallengeVmAsync(
        string vmId,
        CancellationToken ct = default)
    {
        var result = new AttestationVerificationResult();

        // Get VM details
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            result.Errors.Add("VM not found");
            return result;
        }

        if (vm.Status != VmStatus.Running)
        {
            result.Errors.Add($"VM is not running (status: {vm.Status})");
            return result;
        }

        // Get VM's network address
        var vmAddress = GetVmAttestationAddress(vm);
        if (string.IsNullOrEmpty(vmAddress))
        {
            result.Errors.Add("VM address not available");
            await RecordFailureAsync(vmId, "No network address");
            return result;
        }

        // Create challenge
        var challenge = CreateChallenge(vm);

        _logger.LogDebug(
            "Sending attestation challenge to VM {VmId} at {Address}, nonce: {Nonce}",
            vmId, vmAddress, challenge.Nonce[..8] + "...");

        try
        {
            // Send challenge and measure response time (CRITICAL: wall-clock timing)
            var sendTime = DateTime.UtcNow;

            var response = await SendChallengeAsync(vmAddress, challenge, ct);

            var receiveTime = DateTime.UtcNow;
            result.ResponseTimeMs = (receiveTime - sendTime).TotalMilliseconds;

            if (response == null)
            {
                result.Errors.Add("No response received (timeout or error)");
                await RecordFailureAsync(vmId, "No response");
                return result;
            }

            // Verify the response
            result = VerifyResponse(challenge, response, vm, result.ResponseTimeMs);

            // Record result
            if (result.Success)
            {
                await RecordSuccessAsync(vmId, response.Metrics.BootId, response.Metrics.MachineId);
                TrackResponseTime(vmId, result.ResponseTimeMs);
            }
            else
            {
                await RecordFailureAsync(vmId, string.Join("; ", result.Errors));
            }

            // Save attestation record for audit
            await SaveAttestationRecordAsync(vm, challenge, result, response?.Metrics);

            return result;
        }
        catch (TaskCanceledException)
        {
            result.Errors.Add($"Response timeout (>{_config.MaxResponseTimeMs}ms)");
            result.TimingValid = false;
            await RecordFailureAsync(vmId, "Timeout");
            return result;
        }
        catch (HttpRequestException ex)
        {
            result.Errors.Add($"Network error: {ex.Message}");
            await RecordFailureAsync(vmId, $"Network: {ex.Message}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attestation challenge failed for VM {VmId}", vmId);
            result.Errors.Add($"Exception: {ex.Message}");
            await RecordFailureAsync(vmId, ex.Message);
            return result;
        }
    }

    private AttestationChallenge CreateChallenge(VirtualMachine vm)
    {
        var nonce = GenerateNonce();

        return new AttestationChallenge
        {
            ChallengeId = Guid.NewGuid().ToString(),
            VmId = vm.Id,
            NodeId = vm.NodeId ?? string.Empty,
            Nonce = nonce,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ExpectedCores = vm.Spec.VirtualCpuCores,
            ExpectedMemoryMb = vm.Spec.MemoryBytes / 1024 / 1024,
            SentAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddSeconds(5)
        };
    }

    private async Task<AttestationResponse?> SendChallengeAsync(
        string vmAddress,
        AttestationChallenge challenge,
        CancellationToken ct)
    {
        var url = $"http://{vmAddress}:9999/challenge";

        var payload = new
        {
            nonce = challenge.Nonce,
            timestamp = challenge.Timestamp,
            vmId = challenge.VmId,
            expectedCores = challenge.ExpectedCores,
            expectedMemoryMb = challenge.ExpectedMemoryMb
        };

        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.PostAsync(url, content, ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "VM {VmId} attestation returned status {Status}",
                challenge.VmId, response.StatusCode);
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AttestationResponse>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    private AttestationVerificationResult VerifyResponse(
        AttestationChallenge challenge,
        AttestationResponse response,
        VirtualMachine vm,
        double responseTimeMs)
    {
        var result = new AttestationVerificationResult
        {
            ResponseTimeMs = responseTimeMs
        };

        var expectedMemoryKb = vm.Spec.MemoryBytes / 1024;

        // =============================================
        // CHECK 1: Response Time (CRITICAL SECURITY CHECK)
        // This is the KEY defense against key extraction attacks
        // =============================================
        if (responseTimeMs > _config.MaxResponseTimeMs)
        {
            result.Errors.Add($"Response too slow: {responseTimeMs:F1}ms (max: {_config.MaxResponseTimeMs}ms)");
            result.TimingValid = false;
        }
        else
        {
            result.TimingValid = true;
        }

        // =============================================
        // CHECK 2: Nonce Match (prevents replay attacks)
        // =============================================
        if (response.Nonce != challenge.Nonce)
        {
            result.Errors.Add("Nonce mismatch - possible replay attack");
            result.NonceValid = false;
        }
        else
        {
            result.NonceValid = true;
        }

        // =============================================
        // CHECK 3: Signature Verification
        // Response is signed with ephemeral key generated inside VM
        // =============================================
        try
        {
            result.SignatureValid = VerifyEd25519Signature(challenge, response);
            if (!result.SignatureValid)
            {
                result.Errors.Add("Invalid signature");
            }
        }
        catch (Exception ex)
        {
            result.Errors.Add($"Signature verification error: {ex.Message}");
            result.SignatureValid = false;
        }

        // =============================================
        // CHECK 4: CPU Cores
        // =============================================
        if (response.Metrics.CpuCores < challenge.ExpectedCores)
        {
            result.Errors.Add($"CPU cores: got {response.Metrics.CpuCores}, expected {challenge.ExpectedCores}");
            result.CpuValid = false;
        }
        else
        {
            result.CpuValid = true;
            if (response.Metrics.CpuCores > challenge.ExpectedCores)
            {
                result.Warnings.Add($"VM reports more cores than expected: {response.Metrics.CpuCores} vs {challenge.ExpectedCores}");
            }
        }

        // =============================================
        // CHECK 5: Memory Amount
        // =============================================
        var memoryRatio = (double)response.Metrics.MemoryKb / expectedMemoryKb;
        if (memoryRatio < _config.MemoryToleranceLow)
        {
            result.Errors.Add($"Memory too low: {response.Metrics.MemoryKb / 1024}MB vs expected {expectedMemoryKb / 1024}MB");
            result.MemoryValid = false;
        }
        else if (memoryRatio > _config.MemoryToleranceHigh)
        {
            result.Warnings.Add($"Memory higher than expected: {response.Metrics.MemoryKb / 1024}MB vs {expectedMemoryKb / 1024}MB");
            result.MemoryValid = true;
        }
        else
        {
            result.MemoryValid = true;
        }

        // =============================================
        // CHECK 6: Memory Touch Test (detects swap/overcommit)
        // =============================================
        if (response.MemoryTouch.TotalMs > _config.MaxMemoryTouchMs)
        {
            result.Errors.Add($"Memory touch too slow: {response.MemoryTouch.TotalMs:F1}ms (max: {_config.MaxMemoryTouchMs}ms) - possible swap");
            result.MemoryTouchValid = false;
        }
        else if (response.MemoryTouch.MaxPageMs > _config.MaxSinglePageTouchMs)
        {
            result.Errors.Add($"Single page touch too slow: {response.MemoryTouch.MaxPageMs:F1}ms - possible swap thrashing");
            result.MemoryTouchValid = false;
        }
        else
        {
            result.MemoryTouchValid = true;
        }

        // =============================================
        // CHECK 7: Identity Consistency
        // =============================================
        result.IdentityValid = VerifyIdentityConsistency(
            challenge.VmId,
            response.Metrics.BootId,
            response.Metrics.MachineId,
            result);

        // =============================================
        // Overall Success
        // =============================================
        result.Success = result.TimingValid
                      && result.SignatureValid
                      && result.NonceValid
                      && result.CpuValid
                      && result.MemoryValid
                      && result.MemoryTouchValid
                      && result.IdentityValid;

        _logger.LogInformation(
            "VM {VmId} attestation {Result} in {ResponseTime:F1}ms: " +
            "timing={Timing}, sig={Sig}, nonce={Nonce}, cpu={Cpu}, mem={Mem}, touch={Touch}, id={Id}",
            challenge.VmId,
            result.Success ? "PASSED" : "FAILED",
            responseTimeMs,
            result.TimingValid, result.SignatureValid, result.NonceValid,
            result.CpuValid, result.MemoryValid, result.MemoryTouchValid, result.IdentityValid);

        return result;
    }

    private bool VerifyEd25519Signature(AttestationChallenge challenge, AttestationResponse response)
    {
        try
        {
            var pubKeyBytes = Convert.FromHexString(response.EphemeralPubKey);
            var signatureBytes = Convert.FromHexString(response.Signature);

            if (pubKeyBytes.Length != 32 || signatureBytes.Length != 64)
            {
                _logger.LogWarning("Invalid key/signature length");
                return false;
            }

            // Reconstruct canonical message (must match agent exactly)
            var canonicalMsg = string.Format(
                "{0}|{1}|{2}|{3}|{4}|{5}|{6}|{7}|{8:F3}|{9}",
                challenge.Nonce,
                challenge.Timestamp,
                challenge.VmId,
                response.Metrics.CpuCores,
                response.Metrics.MemoryKb,
                response.MemoryTouch.PagesTouched,
                response.MemoryTouch.ContentHash,
                response.Metrics.BootId,
                response.Metrics.UptimeSeconds,
                response.EphemeralPubKey
            );

            var messageBytes = Encoding.UTF8.GetBytes(canonicalMsg);

            // Use NSec for Ed25519 verification
            // Add package: NSec.Cryptography
            var algorithm = NSec.Cryptography.SignatureAlgorithm.Ed25519;
            var publicKey = NSec.Cryptography.PublicKey.Import(
                algorithm,
                pubKeyBytes,
                NSec.Cryptography.KeyBlobFormat.RawPublicKey);

            return algorithm.Verify(publicKey, messageBytes, signatureBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature verification failed");
            return false;
        }
    }

    private bool VerifyIdentityConsistency(
        string vmId,
        string bootId,
        string machineId,
        AttestationVerificationResult result)
    {
        lock (_stateLock)
        {
            if (!_livenessStates.TryGetValue(vmId, out var state))
            {
                // First attestation - record identity
                return true;
            }

            // Machine ID should NEVER change
            if (state.LastMachineId != null && state.LastMachineId != machineId)
            {
                result.Errors.Add($"Machine ID changed unexpectedly - VM may have been replaced");
                return false;
            }

            // Boot ID changes on reboot - expected but we track it
            if (state.LastBootId != null && state.LastBootId != bootId)
            {
                result.Warnings.Add("VM has rebooted since last attestation");
            }

            return true;
        }
    }

    private Task RecordSuccessAsync(string vmId, string bootId, string machineId)
    {
        lock (_stateLock)
        {
            if (!_livenessStates.TryGetValue(vmId, out var state))
            {
                state = new VmLivenessState { VmId = vmId };
                _livenessStates[vmId] = state;
            }

            state.LastBootId = bootId;
            state.LastMachineId = machineId;
            state.LastSuccessfulAttestation = DateTime.UtcNow;
            state.ConsecutiveSuccesses++;
            state.ConsecutiveFailures = 0;
            state.TotalChallenges++;
            state.TotalSuccesses++;

            // Resume billing if paused and recovery threshold met
            if (state.BillingPaused && state.ConsecutiveSuccesses >= _config.RecoveryThreshold)
            {
                state.BillingPaused = false;
                state.PauseReason = null;
                state.PausedAt = null;

                _logger.LogInformation(
                    "VM {VmId}: Billing RESUMED after {Count} successful attestations",
                    vmId, state.ConsecutiveSuccesses);
            }
        }

        return Task.CompletedTask;
    }

    private Task RecordFailureAsync(string vmId, string reason)
    {
        lock (_stateLock)
        {
            if (!_livenessStates.TryGetValue(vmId, out var state))
            {
                state = new VmLivenessState { VmId = vmId };
                _livenessStates[vmId] = state;
            }

            state.ConsecutiveFailures++;
            state.ConsecutiveSuccesses = 0;
            state.TotalChallenges++;

            // Pause billing after consecutive failures
            if (!state.BillingPaused && state.ConsecutiveFailures >= _config.FailureThreshold)
            {
                state.BillingPaused = true;
                state.PauseReason = reason;
                state.PausedAt = DateTime.UtcNow;

                _logger.LogWarning(
                    "VM {VmId}: Billing PAUSED after {Count} consecutive failures. Reason: {Reason}",
                    vmId, state.ConsecutiveFailures, reason);
            }
        }

        return Task.CompletedTask;
    }

    private async Task SaveAttestationRecordAsync(
        VirtualMachine vm,
        AttestationChallenge challenge,
        AttestationVerificationResult result,
        AttestationMetrics? metrics)
    {
        var record = new AttestationRecord
        {
            VmId = vm.Id,
            NodeId = vm.NodeId ?? string.Empty,
            UserId = vm.OwnerId ?? string.Empty,
            ChallengeId = challenge.ChallengeId,
            Success = result.Success,
            ResponseTimeMs = result.ResponseTimeMs,
            ReportedMetrics = metrics,
            Errors = result.Errors,
            Timestamp = DateTime.UtcNow
        };

        await _dataStore.SaveAttestationRecordAsync(record);
    }

    private string? GetVmAttestationAddress(VirtualMachine vm)
    {
        // Try direct IP first
        if (!string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
        {
            // If VM is on a CGNAT node, we need to go through the node
            if (!string.IsNullOrEmpty(vm.NodeId) &&
                _dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
            {
                // Use node's public IP to reach the VM via port forwarding
                if (!string.IsNullOrEmpty(node.PublicIp))
                {
                    return node.PublicIp;
                }
            }

            return vm.NetworkConfig.PrivateIp;
        }

        return null;
    }

    private void TrackResponseTime(string vmId, double responseTimeMs)
    {
        lock (_stateLock)
        {
            if (!_responseTimes.TryGetValue(vmId, out var times))
            {
                times = new List<double>();
                _responseTimes[vmId] = times;
            }

            times.Add(responseTimeMs);

            // Keep only last 100 samples
            if (times.Count > 100)
            {
                times.RemoveAt(0);
            }
        }
    }

    public VmLivenessState? GetLivenessState(string vmId)
    {
        lock (_stateLock)
        {
            return _livenessStates.TryGetValue(vmId, out var state) ? state : null;
        }
    }

    public bool IsBillingPaused(string vmId)
    {
        lock (_stateLock)
        {
            return _livenessStates.TryGetValue(vmId, out var state) && state.BillingPaused;
        }
    }

    public Task<VmAttestationStats?> GetVmStatsAsync(string vmId)
    {
        lock (_stateLock)
        {
            if (!_livenessStates.TryGetValue(vmId, out var state))
            {
                return Task.FromResult<VmAttestationStats?>(null);
            }

            var avgResponseTime = 0.0;
            if (_responseTimes.TryGetValue(vmId, out var times) && times.Count > 0)
            {
                avgResponseTime = times.Average();
            }

            return Task.FromResult<VmAttestationStats?>(new VmAttestationStats
            {
                VmId = vmId,
                TotalChallenges = state.TotalChallenges,
                SuccessfulChallenges = state.TotalSuccesses,
                SuccessRate = state.SuccessRate,
                AverageResponseTimeMs = avgResponseTime,
                LastAttestation = state.LastSuccessfulAttestation,
                BillingPaused = state.BillingPaused
            });
        }
    }

    private static string GenerateNonce()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLower();
    }
}