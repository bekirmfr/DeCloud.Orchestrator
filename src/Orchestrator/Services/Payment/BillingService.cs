using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Attestation-aware billing service.
/// 
/// Key principle: Only bill for VERIFIED runtime.
/// If attestation is failing, billing is paused until attestation recovers.
/// 
/// Integration: Uses SettlementService for all usage recording.
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
        _logger.LogInformation("Attestation-Aware Billing Service started (5-minute cycle)");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();
                var attestationService = scope.ServiceProvider.GetRequiredService<IAttestationService>();
                var settlementService = scope.ServiceProvider.GetRequiredService<ISettlementService>();

                await ProcessBillingAsync(dataStore, attestationService, settlementService, stoppingToken);
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
        ISettlementService settlementService,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var billedCount = 0;
        var skippedCount = 0;
        var insufficientFundsCount = 0;
        var attestationFailedCount = 0;

        foreach (var vm in dataStore.VirtualMachines.Values.ToList())
        {
            if (ct.IsCancellationRequested) break;

            // Skip non-running VMs
            if (vm.Status != VmStatus.Running || !vm.StartedAt.HasValue)
            {
                continue;
            }

            // Skip system VMs (relay, DHT, storage, etc.)
            if (vm.Spec.VmType != VmType.General)
            {
                continue;
            }

            try
            {
                // ═══════════════════════════════════════════════════════════════
                // ATTESTATION CHECK - Core security feature
                // ═══════════════════════════════════════════════════════════════

                var attestationStatus = attestationService.GetLivenessState(vm.Id);

                if (attestationStatus == null || attestationStatus.BillingPaused)
                {
                    _logger.LogWarning(
                        "⚠️ Billing PAUSED for VM {VmId} - attestation failing. " +
                        "Node may be compromised or VM not running as claimed.",
                        vm.Id);

                    attestationFailedCount++;

                    // Don't bill for potentially fraudulent usage
                    // VM will be marked for shutdown if attestation stays failed
                    continue;
                }

                // ═══════════════════════════════════════════════════════════════
                // CALCULATE BILLING PERIOD
                // ═══════════════════════════════════════════════════════════════

                var lastBillingAt = vm.BillingInfo.LastBillingAt ?? vm.StartedAt.Value;
                var billingPeriod = now - lastBillingAt;

                // Skip if already billed recently (within 1 minute to avoid duplicates)
                if (billingPeriod < TimeSpan.FromMinutes(1))
                {
                    continue;
                }

                // ═══════════════════════════════════════════════════════════════
                // CALCULATE COST
                // ═══════════════════════════════════════════════════════════════

                var hourlyRate = vm.BillingInfo.HourlyRateCrypto;
                var hours = (decimal)billingPeriod.TotalHours;
                var cost = hours * hourlyRate;

                // Round to 6 decimals (USDC precision)
                cost = Math.Round(cost, 6);

                if (cost == 0)
                {
                    // Skip zero-cost billing periods
                    continue;
                }

                // ═══════════════════════════════════════════════════════════════
                // RECORD USAGE VIA SETTLEMENT SERVICE
                // ═══════════════════════════════════════════════════════════════

                var success = await settlementService.RecordUsageAsync(
                    userId: vm.OwnerId,
                    vmId: vm.Id,
                    nodeId: vm.NodeId,
                    amount: cost,
                    periodStart: lastBillingAt,
                    periodEnd: now,
                    attestationVerified: true);

                if (success)
                {
                    // Update VM billing info
                    vm.BillingInfo.LastBillingAt = now;
                    vm.BillingInfo.TotalBilled += cost;
                    await dataStore.SaveVmAsync(vm);

                    billedCount++;

                    _logger.LogDebug(
                        "✓ Billed {Cost} USDC for VM {VmId} ({Hours:F2}h at {Rate}/h)",
                        cost, vm.Id, hours, hourlyRate);
                }
                else
                {
                    // Insufficient balance
                    insufficientFundsCount++;

                    _logger.LogWarning(
                        "⚠️ Insufficient balance for VM {VmId}, user {UserId}. " +
                        "Cost: {Cost} USDC. VM will be stopped if balance not topped up.",
                        vm.Id, vm.OwnerId, cost);

                    // TODO: Mark VM for shutdown if balance stays insufficient for > 15 minutes
                    // For now, just log - don't shut down immediately to avoid false positives
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error billing VM {VmId}", vm.Id);
                skippedCount++;
            }
        }

        if (billedCount > 0 || insufficientFundsCount > 0 || attestationFailedCount > 0)
        {
            _logger.LogInformation(
                "Billing cycle complete: Billed={Billed}, Insufficient={Insufficient}, " +
                "AttestationFailed={AttestationFailed}, Skipped={Skipped}",
                billedCount, insufficientFundsCount, attestationFailedCount, skippedCount);
        }
    }
}