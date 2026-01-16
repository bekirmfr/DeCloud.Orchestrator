using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

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

                // Track unverified runtime for reporting
                vm.BillingInfo.UnverifiedRuntimeMinutes += (int)_billingInterval.TotalMinutes;

                skippedCount++;
                continue;
            }

            // =====================================================
            // CALCULATE BILLING
            // =====================================================
            var lastBilling = vm.BillingInfo.LastBillingAt ?? vm.StartedAt.Value;
            var billableMinutes = (now - lastBilling).TotalMinutes;

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
            vm.BillingInfo.TotalChargedCrypto += cost;
            vm.BillingInfo.VerifiedRuntimeMinutes += (int)billableMinutes;

            // Calculate node payout (85% to node, 15% platform fee)
            var nodePayout = cost * 0.85m;
            var platformFee = cost * 0.15m;

            if (!string.IsNullOrEmpty(vm.NodeId) && dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
            {
                node.PendingPayout += nodePayout;
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

    private async Task StopVmForInsufficientFundsAsync(VirtualMachine vm, DataStore dataStore)
    {
        _logger.LogWarning(
            "Stopping VM {VmId} ({Name}) due to insufficient funds",
            vm.Id, vm.Name);

        vm.Status = VmStatus.Stopped;
        vm.BillingInfo.StoppedReason = "Insufficient funds";
        vm.BillingInfo.StoppedAt = DateTime.UtcNow;

        await dataStore.SaveVmAsync(vm);

        // TODO: Send stop command to node agent
        // This would integrate with the existing NodeService/CommandProcessor
    }
}

/// <summary>
/// Extension to VmBillingInfo for attestation tracking
/// </summary>
public static class VmBillingInfoExtensions
{
    public static int GetVerifiedRuntimeMinutes(this VmBillingInfo info)
    {
        return info.VerifiedRuntimeMinutes;
    }

    public static int GetUnverifiedRuntimeMinutes(this VmBillingInfo info)
    {
        return info.UnverifiedRuntimeMinutes;
    }
}