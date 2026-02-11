using Orchestrator.Models;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// The core reconciliation loop — a background service that continuously evaluates
/// obligations, resolves their dependency graph, and dispatches ready obligations
/// to type-specific handlers.
///
/// Modeled after the Kubernetes controller pattern:
///   1. Observe: Collect all active (non-terminal) obligations
///   2. Analyze: Resolve dependency graph, find ready obligations
///   3. Act: Dispatch ready obligations to handlers
///   4. Update: Apply handler results (status, spawned children, signals)
///   5. Maintain: Expire stale obligations, prune terminal ones, detect cycles
///
/// The loop runs at a configurable interval (default: 5 seconds).
/// Each tick processes all ready obligations concurrently (within a concurrency limit).
///
/// Thread safety:
///   - ObligationStore handles concurrent access internally
///   - Each obligation is processed by at most one handler at a time
///   - Handlers are responsible for their own internal thread safety
/// </summary>
public class ReconciliationLoop : BackgroundService
{
    private readonly ObligationStore _store;
    private readonly ObligationDispatcher _dispatcher;
    private readonly ILogger<ReconciliationLoop> _logger;

    // Configuration
    private readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(5);
    private readonly int _maxConcurrentHandlers = 10;
    private readonly TimeSpan _pruneInterval = TimeSpan.FromMinutes(30);

    private DateTime _lastPruneAt = DateTime.UtcNow;

    // Metrics
    private long _totalTicks;
    private long _totalDispatched;
    private long _totalCompleted;
    private long _totalFailed;

    public ReconciliationLoop(
        ObligationStore store,
        ObligationDispatcher dispatcher,
        ILogger<ReconciliationLoop> logger)
    {
        _store = store;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Reconciliation loop started (interval: {Interval}s, max concurrency: {Concurrency})",
            _tickInterval.TotalSeconds, _maxConcurrentHandlers);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in reconciliation tick");
            }

            await Task.Delay(_tickInterval, stoppingToken);
        }

        _logger.LogInformation(
            "Reconciliation loop stopped. Stats: ticks={Ticks}, dispatched={Dispatched}, " +
            "completed={Completed}, failed={Failed}",
            _totalTicks, _totalDispatched, _totalCompleted, _totalFailed);
    }

    /// <summary>
    /// Single reconciliation tick: observe → analyze → act → update → maintain
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        _totalTicks++;

        // ════════════════════════════════════════════════════════════════
        // Step 1: Observe — collect active obligations
        // ════════════════════════════════════════════════════════════════

        var active = _store.GetActive();
        if (active.Count == 0)
            return;

        // ════════════════════════════════════════════════════════════════
        // Step 2: Expire — mark overdue obligations before graph resolution
        // ════════════════════════════════════════════════════════════════

        var expired = ExpireOverdueObligations(active);

        // ════════════════════════════════════════════════════════════════
        // Step 3: Analyze — resolve dependency graph
        // ════════════════════════════════════════════════════════════════

        var resolution = ObligationGraph.Resolve(active);

        if (resolution.HasCycles)
        {
            _logger.LogError(
                "Dependency cycle detected among {Count} obligations: [{Ids}]",
                resolution.CycleParticipants!.Count,
                string.Join(", ", resolution.CycleParticipants));

            // Mark cycle participants as failed
            foreach (var id in resolution.CycleParticipants)
            {
                var ob = _store.Get(id);
                if (ob != null && !ob.IsTerminal)
                {
                    ob.Status = ObligationStatus.Failed;
                    ob.StatusMessage = "Dependency cycle detected";
                    ob.FailedAt = DateTime.UtcNow;
                    ob.UpdatedAt = DateTime.UtcNow;
                    _store.AddOrUpdate(ob);
                    _totalFailed++;
                }
            }
        }

        if (resolution.Ready.Count == 0)
        {
            // Log periodically when there are blocked obligations but nothing is ready
            if (_totalTicks % 12 == 0 && resolution.Blocked.Count > 0) // Every ~60s
            {
                _logger.LogDebug(
                    "Reconciliation: {Active} active, {Blocked} blocked, 0 ready",
                    active.Count, resolution.Blocked.Count);
            }
            return;
        }

        _logger.LogDebug(
            "Reconciliation tick #{Tick}: {Active} active, {Ready} ready, {Blocked} blocked",
            _totalTicks, active.Count, resolution.Ready.Count, resolution.Blocked.Count);

        // ════════════════════════════════════════════════════════════════
        // Step 4: Act — dispatch ready obligations (with concurrency limit)
        // ════════════════════════════════════════════════════════════════

        using var semaphore = new SemaphoreSlim(_maxConcurrentHandlers);
        var tasks = new List<Task>(resolution.Ready.Count);

        foreach (var obligation in resolution.Ready)
        {
            await semaphore.WaitAsync(ct);

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await ProcessObligationAsync(obligation, ct);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks);

        // ════════════════════════════════════════════════════════════════
        // Step 5: Maintain — periodic pruning of terminal obligations
        // ════════════════════════════════════════════════════════════════

        if (DateTime.UtcNow - _lastPruneAt > _pruneInterval)
        {
            _store.Prune();
            _lastPruneAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Process a single obligation: dispatch to handler and apply result
    /// </summary>
    private async Task ProcessObligationAsync(Obligation obligation, CancellationToken ct)
    {
        // Guard: check if obligation is still eligible (may have been cancelled/completed
        // by another obligation's handler in this same tick)
        var fresh = _store.Get(obligation.Id);
        if (fresh == null || fresh.IsTerminal)
            return;

        // Mark as InProgress
        fresh.Status = ObligationStatus.InProgress;
        fresh.AttemptCount++;
        fresh.LastAttemptAt = DateTime.UtcNow;
        fresh.StartedAt ??= DateTime.UtcNow;
        fresh.UpdatedAt = DateTime.UtcNow;
        _store.AddOrUpdate(fresh);
        _totalDispatched++;

        // Dispatch to handler
        var result = await _dispatcher.DispatchAsync(fresh, ct);

        if (result == null)
        {
            // No handler registered — permanent failure
            fresh.Status = ObligationStatus.Failed;
            fresh.StatusMessage = $"No handler registered for type '{fresh.Type}'";
            fresh.FailedAt = DateTime.UtcNow;
            fresh.UpdatedAt = DateTime.UtcNow;
            _store.AddOrUpdate(fresh);
            _totalFailed++;
            return;
        }

        // Apply result
        ApplyResult(fresh, result);
    }

    /// <summary>
    /// Apply an ObligationResult to the obligation, updating status,
    /// spawning children, registering signals, etc.
    /// </summary>
    private void ApplyResult(Obligation obligation, ObligationResult result)
    {
        // Merge updated data
        if (result.UpdatedData != null)
        {
            foreach (var (key, value) in result.UpdatedData)
            {
                obligation.Data[key] = value;
            }
        }

        obligation.StatusMessage = result.Message;
        obligation.UpdatedAt = DateTime.UtcNow;

        switch (result.Outcome)
        {
            case ObligationOutcome.Completed:
                obligation.Status = ObligationStatus.Completed;
                obligation.CompletedAt = DateTime.UtcNow;
                _totalCompleted++;

                _logger.LogInformation("Obligation completed: {Obligation}", obligation);

                // Spawn children
                if (result.SpawnedObligations is { Count: > 0 })
                {
                    SpawnChildren(obligation, result.SpawnedObligations);
                }
                break;

            case ObligationOutcome.InProgress:
                obligation.Status = ObligationStatus.InProgress;
                // Will be re-evaluated next tick
                break;

            case ObligationOutcome.WaitingForSignal:
                obligation.Status = ObligationStatus.WaitingForSignal;

                if (!string.IsNullOrEmpty(result.SignalKey))
                {
                    _store.RegisterSignal(result.SignalKey, obligation.Id);
                }
                else
                {
                    _logger.LogWarning(
                        "Obligation {Id} returned WaitingForSignal but no SignalKey provided",
                        obligation.Id);
                }
                break;

            case ObligationOutcome.Retry:
                if (obligation.IsExhausted)
                {
                    obligation.Status = ObligationStatus.Failed;
                    obligation.FailedAt = DateTime.UtcNow;
                    obligation.StatusMessage = $"Exhausted {obligation.MaxAttempts} attempts. Last: {result.Message}";
                    _totalFailed++;

                    _logger.LogWarning("Obligation exhausted retries: {Obligation}", obligation);

                    // Cascade-cancel dependents
                    CascadeCancelDependents(obligation);
                }
                else
                {
                    obligation.Status = ObligationStatus.Pending;
                    var backoff = obligation.GetNextBackoff();
                    obligation.NextAttemptAfter = DateTime.UtcNow + backoff;

                    _logger.LogDebug(
                        "Obligation {Id} will retry in {Backoff}s (attempt {Attempt}/{Max}): {Message}",
                        obligation.Id, backoff.TotalSeconds,
                        obligation.AttemptCount, obligation.MaxAttempts,
                        result.Message);
                }
                break;

            case ObligationOutcome.PermanentFailure:
                obligation.Status = ObligationStatus.Failed;
                obligation.FailedAt = DateTime.UtcNow;
                _totalFailed++;

                _logger.LogError("Obligation permanently failed: {Obligation}: {Message}",
                    obligation, result.Message);

                // Cascade-cancel dependents
                CascadeCancelDependents(obligation);
                break;
        }

        _store.AddOrUpdate(obligation);
    }

    /// <summary>
    /// Create child obligations that were spawned by a completed handler
    /// </summary>
    private void SpawnChildren(Obligation parent, List<Obligation> children)
    {
        foreach (var child in children)
        {
            child.ParentId = parent.Id;

            // Auto-add parent as dependency if not already specified
            if (!child.DependsOn.Contains(parent.Id))
            {
                child.DependsOn.Add(parent.Id);
            }

            if (_store.TryAdd(child))
            {
                parent.ChildObligationIds.Add(child.Id);
                _logger.LogDebug(
                    "Spawned child obligation {ChildId} ({ChildType}) from parent {ParentId}",
                    child.Id, child.Type, parent.Id);
            }
        }
    }

    /// <summary>
    /// When an obligation fails permanently, cancel all obligations that depend on it
    /// </summary>
    private void CascadeCancelDependents(Obligation failed)
    {
        var active = _store.GetActive();
        var dependentIds = ObligationGraph.GetTransitiveDependents(failed.Id, active);

        if (dependentIds.Count == 0)
            return;

        _logger.LogWarning(
            "Cascade-cancelling {Count} dependents of failed obligation {Id}",
            dependentIds.Count, failed.Id);

        foreach (var depId in dependentIds)
        {
            _store.Cancel(depId, $"Dependency {failed.Id} ({failed.Type}) failed: {failed.StatusMessage}");
        }
    }

    /// <summary>
    /// Mark obligations that have exceeded their deadline
    /// </summary>
    private int ExpireOverdueObligations(IReadOnlyList<Obligation> active)
    {
        var expired = 0;

        foreach (var ob in active)
        {
            if (ob.IsExpired && !ob.IsTerminal)
            {
                ob.Status = ObligationStatus.Expired;
                ob.StatusMessage = $"Deadline exceeded: {ob.Deadline:O}";
                ob.UpdatedAt = DateTime.UtcNow;
                _store.AddOrUpdate(ob);
                expired++;

                _logger.LogWarning("Obligation expired: {Obligation}", ob);

                CascadeCancelDependents(ob);
            }
        }

        return expired;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Public API (for external callers to interact with the loop)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get current reconciliation metrics
    /// </summary>
    public ReconciliationMetrics GetMetrics() => new()
    {
        TotalTicks = _totalTicks,
        TotalDispatched = _totalDispatched,
        TotalCompleted = _totalCompleted,
        TotalFailed = _totalFailed,
        ActiveObligations = _store.ActiveCount,
        TotalObligations = _store.Count
    };
}

/// <summary>
/// Metrics from the reconciliation loop
/// </summary>
public class ReconciliationMetrics
{
    public long TotalTicks { get; init; }
    public long TotalDispatched { get; init; }
    public long TotalCompleted { get; init; }
    public long TotalFailed { get; init; }
    public int ActiveObligations { get; init; }
    public int TotalObligations { get; init; }
}
