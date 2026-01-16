using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Background service that schedules attestation challenges to all running VMs.
/// 
/// Challenge schedule:
/// - First 5 minutes after VM start: Every 60 seconds (catch fraud early)
/// - After startup period: Every 60 minutes (reduce overhead)
/// 
/// This service coordinates with BillingService - billing is paused for VMs
/// that fail attestation.
/// </summary>
public class AttestationSchedulerService : BackgroundService
{
    private readonly ILogger<AttestationSchedulerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly AttestationConfig _config;

    // Track when each VM was last challenged
    private readonly Dictionary<string, DateTime> _lastChallengeTime = new();
    private readonly object _lock = new();

    public AttestationSchedulerService(
        ILogger<AttestationSchedulerService> logger,
        IServiceProvider serviceProvider,
        IOptions<AttestationConfig> config)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _config = config.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Attestation Scheduler started. Startup interval: {StartupInterval}s, Normal interval: {NormalInterval}s",
            _config.StartupChallengeIntervalSeconds,
            _config.NormalChallengeIntervalSeconds);

        // Check every 30 seconds for VMs that need attestation
        var checkInterval = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessPendingAttestationsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in attestation scheduler loop");
            }

            await Task.Delay(checkInterval, stoppingToken);
        }

        _logger.LogInformation("Attestation Scheduler stopped");
    }

    private async Task ProcessPendingAttestationsAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();
        var attestationService = scope.ServiceProvider.GetRequiredService<IAttestationService>();

        // Get all running VMs
        var runningVms = dataStore.VirtualMachines.Values
            .Where(vm => vm.Status == VmStatus.Running)
            .ToList();

        if (runningVms.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Checking {Count} running VMs for attestation", runningVms.Count);

        var challengedCount = 0;
        var passedCount = 0;
        var failedCount = 0;

        foreach (var vm in runningVms)
        {
            if (ct.IsCancellationRequested) break;

            // Skip relay VMs (system VMs)
            if (vm.VmType == VmType.Relay)
            {
                continue;
            }

            // Determine if this VM needs attestation
            if (!ShouldChallenge(vm))
            {
                continue;
            }

            challengedCount++;

            // Send challenge
            var result = await attestationService.ChallengeVmAsync(vm.Id, ct);

            // Record challenge time
            lock (_lock)
            {
                _lastChallengeTime[vm.Id] = DateTime.UtcNow;
            }

            if (result.Success)
            {
                passedCount++;
                _logger.LogDebug(
                    "✓ VM {VmId} attestation passed in {ResponseTime:F1}ms",
                    vm.Id, result.ResponseTimeMs);
            }
            else
            {
                failedCount++;
                _logger.LogWarning(
                    "✗ VM {VmId} attestation FAILED: {Errors}",
                    vm.Id, string.Join(", ", result.Errors));
            }

            // Small delay between VMs to avoid thundering herd
            await Task.Delay(50, ct);
        }

        if (challengedCount > 0)
        {
            _logger.LogInformation(
                "Attestation round complete: {Challenged} challenged, {Passed} passed, {Failed} failed",
                challengedCount, passedCount, failedCount);
        }
    }

    private bool ShouldChallenge(VirtualMachine vm)
    {
        var now = DateTime.UtcNow;
        var vmAge = now - vm.CreatedAt;

        // Determine challenge interval based on VM age
        var challengeInterval = vmAge.TotalMinutes < _config.StartupPeriodMinutes
            ? TimeSpan.FromSeconds(_config.StartupChallengeIntervalSeconds)
            : TimeSpan.FromSeconds(_config.NormalChallengeIntervalSeconds);

        // Check when we last challenged this VM
        DateTime lastChallenge;
        lock (_lock)
        {
            if (!_lastChallengeTime.TryGetValue(vm.Id, out lastChallenge))
            {
                // Never challenged - challenge now
                return true;
            }
        }

        var timeSinceLastChallenge = now - lastChallenge;
        return timeSinceLastChallenge >= challengeInterval;
    }

    /// <summary>
    /// Trigger an immediate attestation for a specific VM
    /// (e.g., after a user complaint or suspicious activity)
    /// </summary>
    public async Task<AttestationVerificationResult> TriggerImmediateAttestationAsync(
        string vmId,
        CancellationToken ct = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var attestationService = scope.ServiceProvider.GetRequiredService<IAttestationService>();

        _logger.LogInformation("Triggering immediate attestation for VM {VmId}", vmId);

        var result = await attestationService.ChallengeVmAsync(vmId, ct);

        lock (_lock)
        {
            _lastChallengeTime[vmId] = DateTime.UtcNow;
        }

        return result;
    }
}