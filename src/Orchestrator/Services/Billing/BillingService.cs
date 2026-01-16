using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Billing;

/// <summary>
/// Attestation-aware billing service.
/// 
/// Key principle: Only bill for VERIFIED runtime.
/// If attestation is failing, billing is paused until attestation recovers.
/// This prevents billing fraud where a node claims a VM is running but isn't
/// providing the promised resources.
/// </summary>
public class AttestationAwareBillingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AttestationAwareBillingService> _logger;
    private readonly TimeSpan _billingInterval = TimeSpan.FromMinutes(5);

    public AttestationAwareBillingService(
        IServiceProvider serviceProvider,
        ILogger<AttestationAwareBillingService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Attestation-Aware Billing Service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();
                var attestationService = scope.ServiceProvider.GetRequiredService<IAttestationService>();
                var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

                await ProcessBillingAsync(dataStore, attestationService, userService, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing billing");
            }

            await Task.Delay(_billingInterval, stoppingToken);
        }

        _logger.LogInformation("Attestation-Aware Billing Service stopped");
    }

    private async Task ProcessBillingAsync(
        DataStore dataStore,
        IAttestationService attestationService,
        IUserService userService,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var billedCount = 0;
        var skippedCount = 0;
        var insufficientFundsCount = 0;

        foreach (var vm in dataStore.VirtualMachines.Values.ToList())
        {
            if (ct.IsCancellationRequested) break;

            // Skip non-running VMs
            if (vm.Status != VmStatus.Running || !vm.StartedAt.HasValue)
            {
                continue;
            }

            // Skip system VMs (relay, etc.)
            if (vm.VmType == VmType.Relay || string.IsNullOrEmpty(vm.OwnerId))
            {
                continue;
            }

            // =====================================================
            // CHECK ATTESTATION STATUS BEFORE BILLING
            // =====================================================
            var livenessState = attestationService.GetLivenessState(vm.Id);

            if (livenessState != null && livenessState.BillingPaused)
            {
                _logger.LogDebug(
                    "VM {VmId}: Billing SKIPPED - attestation failing ({Failures} consecutive failures). Reason: {Reason}",
                    vm.Id, livenessState.ConsecutiveFailures, livenessState.PauseReason);

                // Track unverified runtime for reporting (using extension property)
                vm.BillingInfo.UnverifiedRuntime += _billingInterval;

                skippedCount++;
                continue;
            }

            // =====================================================
            // CALCULATE BILLING
            // =====================================================
            var lastBilling = vm.BillingInfo.LastBillingAt ?? vm.StartedAt.Value;
            var billableDuration = now - lastBilling;
            var billableMinutes = billableDuration.TotalMinutes;

            if (billableMinutes < 1)
            {
                continue; // Less than a minute, skip
            }

            var hourlyRate = vm.BillingInfo.HourlyRateCrypto;
            var cost = (decimal)(billableMinutes / 60.0) * hourlyRate;

            if (cost <= 0)
            {
                continue;
            }

            // =====================================================
            // CHECK USER BALANCE
            // =====================================================
            if (!dataStore.Users.TryGetValue(vm.OwnerId, out var user))
            {
                _logger.LogWarning("VM {VmId}: Owner {OwnerId} not found", vm.Id, vm.OwnerId);
                continue;
            }

            if (user.CryptoBalance < cost)
            {
                _logger.LogWarning(
                    "VM {VmId}: Insufficient funds. Balance: {Balance} {Token}, Required: {Cost}",
                    vm.Id, user.CryptoBalance, user.BalanceToken, cost);

                // Stop the VM due to insufficient funds
                await StopVmForInsufficientFundsAsync(vm, dataStore);
                insufficientFundsCount++;
                continue;
            }

            // =====================================================
            // DEDUCT BALANCE AND RECORD
            // =====================================================
            user.CryptoBalance -= cost;

            vm.BillingInfo.LastBillingAt = now;
            vm.BillingInfo.TotalBilled += cost;
            vm.BillingInfo.TotalRuntime += billableDuration;
            vm.BillingInfo.VerifiedRuntime += billableDuration;

            // Calculate node payout (85% to node, 15% platform fee)
            var nodePayout = cost * 0.85m;
            var platformFee = cost * 0.15m;

            // Track node earnings (using the EarningsTracker if available)
            if (!string.IsNullOrEmpty(vm.NodeId) && dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
            {
                // Add to node's pending payout tracking
                TrackNodeEarnings(node.Id, nodePayout, dataStore);
            }

            await dataStore.SaveUserAsync(user);
            await dataStore.SaveVmAsync(vm);

            billedCount++;

            _logger.LogDebug(
                "VM {VmId}: Billed {Cost:F4} {Token} for {Minutes:F1} minutes (verified). " +
                "User balance: {Balance}, Node payout: {NodePayout}",
                vm.Id, cost, user.BalanceToken, billableMinutes,
                user.CryptoBalance, nodePayout);
        }

        if (billedCount > 0 || skippedCount > 0 || insufficientFundsCount > 0)
        {
            _logger.LogInformation(
                "Billing cycle complete: {Billed} billed, {Skipped} skipped (attestation), {Insufficient} stopped (no funds)",
                billedCount, skippedCount, insufficientFundsCount);
        }
    }

    /// <summary>
    /// Track node earnings for later payout
    /// </summary>
    private void TrackNodeEarnings(string nodeId, decimal amount, DataStore dataStore)
    {
        // Use a simple in-memory tracker or extend Node model
        // For now, log it - you can extend this based on your payout system
        _logger.LogDebug("Node {NodeId}: Earned {Amount} USDC", nodeId, amount);

        // Option 1: Store in a separate earnings collection
        // dataStore.NodeEarnings.AddOrUpdate(nodeId, amount, (k, v) => v + amount);

        // Option 2: If you add PendingPayout to Node model, uncomment:
        // if (dataStore.Nodes.TryGetValue(nodeId, out var node))
        // {
        //     node.PendingPayout += amount;
        // }
    }

    private async Task StopVmForInsufficientFundsAsync(VirtualMachine vm, DataStore dataStore)
    {
        _logger.LogWarning(
            "Stopping VM {VmId} ({Name}) due to insufficient funds",
            vm.Id, vm.Name);

        vm.Status = VmStatus.Stopped;

        // Store stop reason in Labels or a dedicated field if you add one
        vm.Labels["_stopped_reason"] = "Insufficient funds";
        vm.Labels["_stopped_at"] = DateTime.UtcNow.ToString("o");

        await dataStore.SaveVmAsync(vm);

        // TODO: Send stop command to node agent
        // This would integrate with the existing NodeService/CommandProcessor
    }
}