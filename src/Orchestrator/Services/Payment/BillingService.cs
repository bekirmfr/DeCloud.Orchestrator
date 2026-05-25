// src/Orchestrator/Services/Payment/BillingService.cs
// Updated: Uses BalanceService for balance validation (breaks circular dependency)

using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Models.Payment;
using Orchestrator.Persistence;
using Orchestrator.Services.Balance;
using Orchestrator.Services.Settlement;
using System.Threading.Channels;

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
    private readonly ISettlementService _settlementService;
    private readonly IBalanceService _balanceService;
    private readonly ILogger<BillingService> _logger;

    // Event-driven billing queue
    private readonly Channel<BillingEvent> _billingQueue;

    // Periodic billing timer
    private readonly TimeSpan _periodicBillingInterval = TimeSpan.FromMinutes(5);

    public BillingService(
        DataStore dataStore,
        ISettlementService settlementService,
        IBalanceService balanceService,
        ILogger<BillingService> logger)
    {
        _dataStore = dataStore;
        _settlementService = settlementService;
        _balanceService = balanceService;
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
        var runningVms = _dataStore.GetActiveVMs()
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
        var vm = await _dataStore.GetVmAsync(evt.VmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found for billing", evt.VmId);
            return;
        }

        // Skip non-running VMs (unless it's a stop event - bill final usage)
        if (vm.Status != VmStatus.Running && evt.Trigger != BillingTrigger.VmStop)
        {
            return;
        }

        // Skip system VMss
        if (vm.Spec.VmType != VmType.General)
        {
            return;
        }

        // Guard: OwnerId and NodeId must be present for billing to proceed
        if (string.IsNullOrEmpty(vm.OwnerId))
        {
            _logger.LogWarning("VM {VmId}: OwnerId is null — skipping billing.", vm.Id);
            return;
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            _logger.LogWarning("VM {VmId}: NodeId is null — skipping billing.", vm.Id);
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // HEARTBEAT-STALENESS GUARD
        // ═══════════════════════════════════════════════════════════════════════
        // Billing follows node liveness. If the host hasn't heartbeat recently,
        // we cannot confirm the VM is operating — pause until heartbeat returns.
        // Replaces the prior attestation-based pause/verified-runtime logic.

        const string PauseReasonStaleHeartbeat = "Node heartbeat stale";
        var stalenessThreshold = TimeSpan.FromSeconds(90); // 3 missed cycles at 30s cadence
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        var heartbeatAge = DateTime.UtcNow - (node?.LastHeartbeat ?? DateTime.MinValue);
        if (node == null)
        {
            _logger.LogWarning(
                "VM {VmId}: NodeId {NodeId} does not resolve to a node — skipping billing",
                vm.Id, vm.NodeId);
            return;
        }
        var heartbeatStale = heartbeatAge > stalenessThreshold;

        if (heartbeatStale && evt.Trigger != BillingTrigger.VmStop)
        {
            if (!vm.BillingInfo.IsPaused)
            {
                _logger.LogWarning(
                    "VM {VmId}: node {NodeId} heartbeat stale ({AgeSec:F0}s ago) — pausing billing",
                    vm.Id, vm.NodeId, heartbeatAge.TotalSeconds);
                vm.BillingInfo.IsPaused = true;
                vm.BillingInfo.PausedAt = DateTime.UtcNow;
                vm.BillingInfo.PauseReason = PauseReasonStaleHeartbeat;
                // Advance the period start so when the node returns we don't
                // bill the gap from now until resume.
                vm.BillingInfo.CurrentPeriodStart = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(vm);
            }
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // PAUSED-STATE GUARD
        // ═══════════════════════════════════════════════════════════════════════
        // Resume on: explicit BalanceAdded/HeartbeatResumed triggers, OR
        // automatically when paused for stale heartbeat and the heartbeat is
        // now fresh (handled by the staleness guard above falling through).

        if (vm.BillingInfo.IsPaused)
        {
            var shouldResume =
                evt.Trigger == BillingTrigger.BalanceAdded ||
                evt.Trigger == BillingTrigger.HeartbeatResumed ||
                vm.BillingInfo.PauseReason == PauseReasonStaleHeartbeat; // safe: heartbeat
                                                                         // is fresh — staleness
                                                                         // check returned early
                                                                         // above when it wasn't

            if (shouldResume)
            {
                var previousReason = vm.BillingInfo.PauseReason;
                vm.BillingInfo.IsPaused = false;
                vm.BillingInfo.PausedAt = null;
                vm.BillingInfo.PauseReason = null;
                // Start the next period at "now" so the gap is not billed.
                vm.BillingInfo.CurrentPeriodStart = DateTime.UtcNow;
                _logger.LogInformation(
                    "VM {VmId}: billing RESUMED (trigger: {Trigger}, prior reason: {Reason})",
                    vm.Id, evt.Trigger, previousReason);
                // fall through to normal billing
            }
            else if (evt.Trigger != BillingTrigger.VmStop)
            {
                _logger.LogDebug(
                    "VM {VmId}: billing paused (reason: {Reason}) — skipping cycle.",
                    vm.Id, vm.BillingInfo.PauseReason);
                return;
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // CALCULATE BILLING PERIOD
        // ═══════════════════════════════════════════════════════════════════════

        var now = DateTime.UtcNow;
        var lastBillingAt = vm.BillingInfo.LastBillingAt ?? vm.StartedAt ?? now;
        var currentPeriodStart = vm.BillingInfo.CurrentPeriodStart ?? lastBillingAt;

        // Track the period for this billing attempt
        var billingPeriod = now - currentPeriodStart;

        // All runtime is accrued under the verified counter — heartbeat-based
        // billing makes no per-cycle verified/unverified distinction.
        vm.BillingInfo.VerifiedRuntime += billingPeriod;

        // Always advance CurrentPeriodStart to now so the next billing cycle
        // measures only the NEW period — not from the original start time.
        // Without this, skipped cycles (cost < 0.01, period < 1min) cause
        // runtime to be double-counted and billing periods to grow unboundedly.
        vm.BillingInfo.CurrentPeriodStart = now;

        await _dataStore.SaveVmAsync(vm);

        // Don't bill if period < 1 minute (avoid spam)
        if (billingPeriod < TimeSpan.FromMinutes(1) && evt.Trigger != BillingTrigger.VmStop)
        {
            _logger.LogDebug(
                "VM {VmId}: runtime tracked ({Runtime:F2}min) but not billing " +
                "(period={Period:F2}min < 1min threshold)",
                vm.Id,
                vm.BillingInfo.VerifiedRuntime.TotalMinutes,
                billingPeriod.TotalMinutes);
            return;
        }

        // Calculate billing cost
        var hourlyRate = vm.BillingInfo.HourlyRateCrypto;

        if (hourlyRate <= 0)
        {
            _logger.LogWarning(
                "VM {VmId} (owner={OwnerId}): HourlyRateCrypto is 0 — billing skipped. " +
                "Rate was never assigned during VM scheduling.",
                vm.Id, vm.OwnerId);
            return;
        }

        var cost = hourlyRate * (decimal)billingPeriod.TotalHours;

        if (cost < 0.0001m) // Minimum 0.0001 USDC (supports low-rate VMs at 5-min cadence)
        {
            _logger.LogDebug(
                "VM {VmId}: cost {Cost:F6} USDC below 0.0001 minimum — skipping.",
                vm.Id, cost);
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
            "Billing VM {VmId}: Period={Period}, Cost={Cost}, " +
            "Runtime={RuntimeMs}ms",
            vm.Id,
            billingPeriod,
            cost,
            vm.BillingInfo.VerifiedRuntime.TotalMilliseconds);

        // Update billing info (CurrentPeriodStart already set to `now` above)
        vm.BillingInfo.TotalBilled += cost;
        vm.BillingInfo.TotalRuntime += billingPeriod;
        vm.BillingInfo.LastBillingAt = now;

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
            attestationVerified: true  // Legacy field — heartbeat-based billing always considers periods verified.
        );

        if (!success)
        {
            _logger.LogWarning(
                "⚠️ Failed to record usage for VM {VmId} - user was billed {Cost} USDC but usage not recorded!",
                vm.Id, cost);
            return;
        }

        _logger.LogInformation(
            "✓ Billed VM {VmId}: {Cost:F4} USDC for {Duration}, trigger={Trigger}, " +
            "lifetime_runtime={LifetimeRuntime:F2}min, total_billed={TotalBilled:F4} USDC",
            vm.Id,
            cost,
            billingPeriod,
            evt.Trigger,
            vm.BillingInfo.VerifiedRuntime.TotalMinutes,
            vm.BillingInfo.TotalBilled);
    }
}