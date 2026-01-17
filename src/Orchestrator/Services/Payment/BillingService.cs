using System.Threading.Channels;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Services.Payment;

/// <summary>
/// Event-driven billing service with queue
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
    private readonly ILogger<BillingService> _logger;

    // Event-driven billing queue
    private readonly Channel<BillingEvent> _billingQueue;

    // Periodic billing timer
    private readonly TimeSpan _periodicBillingInterval = TimeSpan.FromMinutes(5);

    public BillingService(
        DataStore dataStore,
        IAttestationService attestationService,
        ISettlementService settlementService,
        ILogger<BillingService> logger)
    {
        _dataStore = dataStore;
        _attestationService = attestationService;
        _settlementService = settlementService;
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
        await foreach (var evt in _billingQueue.Reader.ReadAllAsync(ct))
        {
            try
            {
                await ProcessVmBillingAsync(evt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing billing for VM {VmId}", evt.VmId);
            }
        }
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

        var billingPeriod = now - currentPeriodStart;

        // Don't bill if period < 1 minute (avoid spam)
        if (billingPeriod < TimeSpan.FromMinutes(1) && evt.Trigger != BillingTrigger.VmStop)
        {
            return;
        }

        // Calculate cost
        var hourlyRate = vm.BillingInfo.HourlyRateCrypto;
        var cost = hourlyRate * (decimal)billingPeriod.TotalHours;

        if (cost < 0.01m) // Minimum 0.01 USDC
        {
            return;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // RECORD USAGE
        // ═══════════════════════════════════════════════════════════════════════

        var success = await _settlementService.RecordUsageAsync(
            userId: vm.OwnerId,
            vmId: vm.Id,
            nodeId: vm.NodeId ?? string.Empty,
            amount: cost,
            periodStart: currentPeriodStart,
            periodEnd: now,
            attestationVerified: attestationStatus?.ConsecutiveSuccesses > 0);

        if (!success)
        {
            _logger.LogWarning(
                "⚠️ Insufficient balance for VM {VmId}, pausing billing",
                vm.Id);

            vm.BillingInfo.IsPaused = true;
            vm.BillingInfo.PausedAt = now;
            vm.BillingInfo.PauseReason = "Insufficient balance";

            await _dataStore.SaveVmAsync(vm);
            return;
        }

        // Update billing info
        vm.BillingInfo.LastBillingAt = now;
        vm.BillingInfo.CurrentPeriodStart = now;
        vm.BillingInfo.TotalBilled += cost;

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "✓ Billed VM {VmId}: {Cost} USDC for {Duration}, trigger={Trigger}",
            vm.Id, cost, billingPeriod, evt.Trigger);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// SUPPORTING TYPES
// ═══════════════════════════════════════════════════════════════════════════

public class BillingEvent
{
    public string VmId { get; set; } = string.Empty;
    public BillingTrigger Trigger { get; set; }
    public string? Reason { get; set; }
    public DateTime Timestamp { get; set; }
}

public enum BillingTrigger
{
    Periodic,       // Periodic timer (every 5 min)
    VmStop,         // VM stopped - bill final usage
    Manual,         // Admin trigger
    BalanceAdded    // User added balance - resume billing
}