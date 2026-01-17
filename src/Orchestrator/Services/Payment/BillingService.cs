using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Attestation-aware billing service.
/// 
/// Key principle: Only bill for VERIFIED runtime.
/// If attestation is failing, billing is paused until attestation recovers.
/// This prevents billing fraud where a node claims a VM is running but isn't
/// providing the promised resources.
/// </summary>
public class BillingService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BillingService> _logger;
    private readonly TimeSpan _billingInterval = TimeSpan.FromMinutes(5);

    public BillingService(
        IServiceProvider serviceProvider,
        ILogger<BillingService> logger)
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
                using var scope = _serviceProvider.CreateScope();
                var vmService = scope.ServiceProvider.GetRequiredService<IVmService>();

                await StopVmForInsufficientFundsAsync(vm, dataStore, vmService);
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


            // Calculate node payout (95% to node, 5% platform fee)
            var platformBPS = 0.05m; // TO-DO: Make this dynamic. Get from deployed web3 contract
            var nodeBPS = 1 - platformBPS;

            var nodePayout = cost * nodeBPS; 
            var platformFee = cost * platformBPS;

            // Track node earnings (using the EarningsTracker if available)
            if (!string.IsNullOrEmpty(vm.NodeId) && dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
            {
                // Add to node's pending payout tracking
                await TrackNodeEarnings(node.Id, nodePayout, dataStore);
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
    private async Task TrackNodeEarnings(string nodeId, decimal amount, DataStore dataStore)
    {
        if (dataStore.Nodes.TryGetValue(nodeId, out var node))
        {
            node.PendingPayout += amount;
            node.TotalEarned += amount;
            await dataStore.SaveNodeAsync(node); // Persist immediately

            _logger.LogInformation(
                "Node {NodeId}: Earned {Amount:F4} USDC (Pending: {Pending:F4}, Lifetime: {Total:F4})",
                nodeId, amount, node.PendingPayout, node.TotalEarned);
        }
    }

    private async Task StopVmForInsufficientFundsAsync(
    VirtualMachine vm,
    DataStore dataStore,
    IVmService vmService) // Inject this
    {
        vm.Status = VmStatus.Stopped;
        vm.Labels["_stopped_reason"] = "Insufficient funds";
        vm.Labels["_stopped_at"] = DateTime.UtcNow.ToString("o");
        await dataStore.SaveVmAsync(vm);

        // Send actual stop command to node
        await vmService.PerformVmActionAsync(vm.Id, VmAction.Stop);

        _logger.LogWarning(
            "VM {VmId} stopped and shutdown command sent to node {NodeId} - insufficient funds",
            vm.Id, vm.NodeId);
    }
}