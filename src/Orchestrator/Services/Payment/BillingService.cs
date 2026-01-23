// src/Orchestrator/Services/Payment/BillingService.cs
// Updated: Uses BalanceService for balance validation (breaks circular dependency)

using System.Threading.Channels;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Balance;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Event-driven billing service with queue
/// UPDATED: Uses BalanceService for balance validation (no circular dependency)
/// 
/// Triggers:
/// 1. Periodic timer (every 5 minutes) - bills all running VMs
/// 2. VM stop event - immediate billing
/// 3. Manual trigger - for admin/testing
/// 
/// Benefits over polling:
/// - No wasted cycles when no VMs running
/// - Immediate billing on VM stop
/// - Queue prevents overload
/// - Backpressure handling
/// </summary>
public class BillingService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IAttestationService _attestationService;
    private readonly ISettlementService _settlementService;
    private readonly IBalanceService _balanceService; // NEW: Use BalanceService instead
    private readonly ILogger<BillingService> _logger;

    // Event-driven billing queue
    private readonly Channel<BillingEvent> _billingQueue;

    // Periodic billing timer
    private readonly TimeSpan _periodicBillingInterval = TimeSpan.FromMinutes(5);

    public BillingService(
        DataStore dataStore,
        IAttestationService attestationService,
        ISettlementService settlementService,
        IBalanceService balanceService, // NEW: Added BalanceService
        ILogger<BillingService> logger)
    {
        _dataStore = dataStore;
        _attestationService = attestationService;
        _settlementService = settlementService;
        _balanceService = balanceService; // NEW
        _logger = logger;

        // Create bounded channel (max 1000 pending events)
        _billingQueue = Channel.CreateBounded<BillingEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Event-Driven Billing Service started");

        // Start periodic billing timer
        _ = Task.Run(() => PeriodicBillingTimerAsync(stoppingToken), stoppingToken);

        // Process billing events from queue
        await ProcessBillingQueueAsync(stoppingToken);


        _logger.LogInformation("Event-Driven Billing Service stopped");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PUBLIC API - Enqueue Billing Events
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Enqueue a VM for billing
    /// Called when VM stops or needs immediate billing
    /// </summary>
    public async Task EnqueueBillingAsync(string vmId, BillingTrigger trigger, string? reason = null)
    {
        var evt = new BillingEvent
        {
            VmId = vmId,
            Trigger = trigger,
            Reason = reason,
            Timestamp = DateTime.UtcNow
        };

        await _billingQueue.Writer.WriteAsync(evt);

        _logger.LogDebug(
            "Billing queued: VM={VmId}, Trigger={Trigger}, Reason={Reason}",
            vmId, trigger, reason);
    }

    /// <summary>
    /// Enqueue all running VMs for billing
    /// Called by periodic timer
    /// </summary>
    public async Task EnqueueAllRunningVmsAsync()
    {
        var runningVms = _dataStore.VirtualMachines.Values
            .Where(vm => vm.Status == VmStatus.Running)
            .ToList();

        foreach (var vm in runningVms)
        {
            await EnqueueBillingAsync(vm.Id, BillingTrigger.Periodic);
        }

        _logger.LogDebug("Enqueued {Count} running VMs for periodic billing", runningVms.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PERIODIC BILLING TIMER
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task PeriodicBillingTimerAsync(CancellationToken ct)
    {
        // Wait 1 minute before first run
        await Task.Delay(TimeSpan.FromMinutes(1), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EnqueueAllRunningVmsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enqueuing periodic billing");
            }

            await Task.Delay(_periodicBillingInterval, ct);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // QUEUE PROCESSOR
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task ProcessBillingQueueAsync(CancellationToken ct)
    {
        var totalProcessed = 0;
        await foreach (var evt in _billingQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessVmBillingAsync(evt, ct);
                totalProcessed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing billing for VM {VmId}", evt.VmId);
            }
        }
        _logger.LogInformation("Processed {Count} billing events", totalProcessed);
    }

    private async Task ProcessVmBillingAsync(BillingEvent evt, CancellationToken ct)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(evt.VmId, out var vm))
        {
            _logger.LogWarning("VM {VmId} not found for billing", evt.VmId);
            return;
        }

        // Skip non-running VMs (unless it's a stop event - bill final usage)
        if (vm.Status != VmStatus.Running && evt.Trigger != BillingTrigger.VmStop)
        {
            return;
        }

        // Skip system VMs
        if (vm.Spec.VmType != VmType.General)
        {
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // ATTESTATION CHECK - Core security feature
        // ═══════════════════════════════════════════════════════════════════════

        var attestationStatus = _attestationService.GetLivenessState(vm.Id);

        if (attestationStatus?.BillingPaused == true && evt.Trigger != BillingTrigger.VmStop)
        {
            _logger.LogWarning(
                "⚠️ Billing PAUSED for VM {VmId} - attestation failing. Skipping billing.",
                vm.Id);
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CALCULATE BILLING PERIOD
        // ═══════════════════════════════════════════════════════════════════════

        var now = DateTime.UtcNow;
        var lastBillingAt = vm.BillingInfo.LastBillingAt ?? vm.StartedAt ?? now;
        var currentPeriodStart = vm.BillingInfo.CurrentPeriodStart ?? lastBillingAt;

        // Track the period for this billing attempt
        var billingPeriod = now - currentPeriodStart;

        var isVerified = attestationStatus?.ConsecutiveSuccesses > 0 || attestationStatus == null;

        if (isVerified)
        {
            vm.BillingInfo.VerifiedRuntime += billingPeriod;
        }
        else
        {
            vm.BillingInfo.UnverifiedRuntime += billingPeriod;
        }

        // Initialize CurrentPeriodStart if this is the first billing attempt
        if (vm.BillingInfo.CurrentPeriodStart == null)
        {
            vm.BillingInfo.CurrentPeriodStart = vm.StartedAt ?? now;
        }

        await _dataStore.SaveVmAsync(vm);

        // Don't bill if period < 1 minute (avoid spam)
        if (billingPeriod < TimeSpan.FromMinutes(1) && evt.Trigger != BillingTrigger.VmStop)
        {
            _logger.LogDebug(
                "VM {VmId}: Runtime tracked (verified={Verified:F2}min, unverified={Unverified:F2}min) " +
                "but not billing (period={Period:F2}min < 1min threshold)",
                vm.Id,
                vm.BillingInfo.VerifiedRuntime.TotalMinutes,
                vm.BillingInfo.UnverifiedRuntime.TotalMinutes,
                billingPeriod.TotalMinutes);
            return;
        }

        // Calculate billing cost
        var hourlyRate = vm.BillingInfo.HourlyRateCrypto;
        var cost = hourlyRate * (decimal)billingPeriod.TotalHours;

        if (cost < 0.01m) // Minimum 0.01 USDC
        {
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // BALANCE CHECK
        // ═══════════════════════════════════════════════════════════════════════

        var hasSufficientBalance = await _balanceService.HasSufficientBalanceAsync(vm.OwnerId, cost);

        if (!hasSufficientBalance)
        {
            _logger.LogWarning(
                "⚠️ Insufficient balance for user {UserId}, pausing billing for VM {VmId}",
                vm.OwnerId, vm.Id);

            vm.BillingInfo.IsPaused = true;
            vm.BillingInfo.PausedAt = now;
            vm.BillingInfo.PauseReason = "Insufficient balance";

            await _dataStore.SaveVmAsync(vm);
            return;
        }

        _logger.LogInformation(
            "Billing VM {VmId}: Period={Period}, Cost={Cost}, IsVerified={Verified}, " +
            "VerifiedRuntime={VerifiedMs}ms, UnverifiedRuntime={UnverifiedMs}ms",
            vm.Id,
            billingPeriod,
            cost,
            isVerified,
            vm.BillingInfo.VerifiedRuntime.TotalMilliseconds,
            vm.BillingInfo.UnverifiedRuntime.TotalMilliseconds);

        // Update billing info
        vm.BillingInfo.TotalBilled += cost;
        vm.BillingInfo.TotalRuntime += billingPeriod;
        vm.BillingInfo.LastBillingAt = now;
        vm.BillingInfo.CurrentPeriodStart = now;

        // ═══════════════════════════════════════════════════════════════════════
        // PERSIST BILLING UPDATE & RECORD USAGE
        // ═══════════════════════════════════════════════════════════════════════

        await _dataStore.SaveVmAsync(vm);

        var success = await _settlementService.RecordUsageAsync(
            userId: vm.OwnerId!,
            vmId: vm.Id,
            nodeId: vm.NodeId!,
            amount: cost,
            periodStart: currentPeriodStart,
            periodEnd: now,
            attestationVerified: isVerified
        );

        if (!success)
        {
            _logger.LogWarning(
                "⚠️ Failed to record usage for VM {VmId} - user was billed {Cost} USDC but usage not recorded!",
                vm.Id, cost);
            return;
        }

        _logger.LogInformation(
            "✓ Billed VM {VmId}: {Cost:F4} USDC for {Duration}, trigger={Trigger}, verified={Verified}, " +
            "lifetime_verified={LifetimeVerified:F2}min, lifetime_unverified={LifetimeUnverified:F2}min, " +
            "total_billed={TotalBilled:F4} USDC",
            vm.Id,
            cost,
            billingPeriod,
            evt.Trigger,
            isVerified,
            vm.BillingInfo.VerifiedRuntime.TotalMinutes,
            vm.BillingInfo.UnverifiedRuntime.TotalMinutes,
            vm.BillingInfo.TotalBilled);
    }
}