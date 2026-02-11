using System.Collections.Concurrent;
using Orchestrator.Models;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// Thread-safe in-memory store for obligations.
/// Provides indexed access by ID, type, resource, status, and signal key.
///
/// Design:
///   - Primary store is ConcurrentDictionary keyed by obligation ID
///   - Secondary indexes maintained on write for fast lookups
///   - Signal key index enables O(1) lookup when external signals arrive
///   - Terminal obligations pruned periodically to bound memory
/// </summary>
public class ObligationStore
{
    private readonly ILogger<ObligationStore> _logger;

    // Primary store
    private readonly ConcurrentDictionary<string, Obligation> _obligations = new();

    // Secondary indexes
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _byType = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<string>> _byResource = new();
    private readonly ConcurrentDictionary<string, string> _bySignalKey = new();

    // Retention for completed/failed obligations before pruning
    private static readonly TimeSpan TerminalRetention = TimeSpan.FromHours(24);
    private const int MaxTerminalObligations = 10_000;

    public ObligationStore(ILogger<ObligationStore> logger)
    {
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Write Operations
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Add a new obligation. Returns false if an obligation with the same ID already exists.
    /// </summary>
    public bool TryAdd(Obligation obligation)
    {
        if (string.IsNullOrEmpty(obligation.Id))
            throw new ArgumentException("Obligation ID cannot be empty");

        if (string.IsNullOrEmpty(obligation.Type))
            throw new ArgumentException("Obligation type cannot be empty");

        if (!_obligations.TryAdd(obligation.Id, obligation))
        {
            _logger.LogDebug("Obligation {Id} already exists, skipping add", obligation.Id);
            return false;
        }

        // Update indexes
        IndexByType(obligation);
        IndexByResource(obligation);

        _logger.LogDebug("Added obligation {Obligation}", obligation);
        return true;
    }

    /// <summary>
    /// Add or replace an obligation. Use for updates.
    /// </summary>
    public void AddOrUpdate(Obligation obligation)
    {
        obligation.UpdatedAt = DateTime.UtcNow;
        _obligations[obligation.Id] = obligation;

        IndexByType(obligation);
        IndexByResource(obligation);
    }

    /// <summary>
    /// Register a signal key for an obligation.
    /// When a signal with this key arrives, the obligation will be woken up.
    /// </summary>
    public void RegisterSignal(string signalKey, string obligationId)
    {
        _bySignalKey[signalKey] = obligationId;
        _logger.LogDebug("Registered signal {SignalKey} → obligation {ObligationId}",
            signalKey, obligationId);
    }

    /// <summary>
    /// Deliver a signal, returning the obligation that was waiting for it (if any).
    /// Removes the signal registration after delivery.
    /// </summary>
    public Obligation? DeliverSignal(string signalKey, Dictionary<string, string>? signalData = null)
    {
        if (!_bySignalKey.TryRemove(signalKey, out var obligationId))
            return null;

        if (!_obligations.TryGetValue(obligationId, out var obligation))
            return null;

        if (obligation.Status != ObligationStatus.WaitingForSignal)
        {
            _logger.LogWarning(
                "Signal {SignalKey} delivered to obligation {Id} but status is {Status}, not WaitingForSignal",
                signalKey, obligationId, obligation.Status);
            return null;
        }

        // Wake up the obligation
        obligation.Status = ObligationStatus.Pending;
        obligation.NextAttemptAfter = null; // Execute immediately on next tick
        obligation.StatusMessage = $"Signal received: {signalKey}";
        obligation.UpdatedAt = DateTime.UtcNow;

        // Merge signal data into obligation data
        if (signalData != null)
        {
            foreach (var (key, value) in signalData)
            {
                obligation.Data[key] = value;
            }
        }

        _logger.LogInformation("Signal {SignalKey} woke obligation {Obligation}", signalKey, obligation);
        return obligation;
    }

    /// <summary>
    /// Remove an obligation by ID
    /// </summary>
    public bool TryRemove(string obligationId)
    {
        if (!_obligations.TryRemove(obligationId, out var removed))
            return false;

        // Clean up signal index
        var signalKeys = _bySignalKey
            .Where(kvp => kvp.Value == obligationId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in signalKeys)
        {
            _bySignalKey.TryRemove(key, out _);
        }

        return true;
    }

    /// <summary>
    /// Cancel an obligation and all its pending children
    /// </summary>
    public void Cancel(string obligationId, string reason)
    {
        if (!_obligations.TryGetValue(obligationId, out var obligation))
            return;

        if (obligation.IsTerminal)
            return;

        obligation.Status = ObligationStatus.Cancelled;
        obligation.StatusMessage = reason;
        obligation.UpdatedAt = DateTime.UtcNow;

        // Recursively cancel children
        foreach (var childId in obligation.ChildObligationIds)
        {
            Cancel(childId, $"Parent {obligationId} cancelled: {reason}");
        }

        _logger.LogInformation("Cancelled obligation {Obligation}: {Reason}", obligation, reason);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Read Operations
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get an obligation by ID
    /// </summary>
    public Obligation? Get(string obligationId)
    {
        _obligations.TryGetValue(obligationId, out var obligation);
        return obligation;
    }

    /// <summary>
    /// Get all non-terminal obligations (the working set for the reconciliation loop)
    /// </summary>
    public IReadOnlyList<Obligation> GetActive()
    {
        return _obligations.Values
            .Where(o => !o.IsTerminal)
            .ToList();
    }

    /// <summary>
    /// Get obligations by type
    /// </summary>
    public IReadOnlyList<Obligation> GetByType(string type)
    {
        if (!_byType.TryGetValue(type, out var ids))
            return Array.Empty<Obligation>();

        return ids
            .Select(id => _obligations.GetValueOrDefault(id))
            .Where(o => o != null && o.Type == type)
            .ToList()!;
    }

    /// <summary>
    /// Get obligations for a specific resource
    /// </summary>
    public IReadOnlyList<Obligation> GetByResource(string resourceType, string resourceId)
    {
        var key = $"{resourceType}:{resourceId}";
        if (!_byResource.TryGetValue(key, out var ids))
            return Array.Empty<Obligation>();

        return ids
            .Select(id => _obligations.GetValueOrDefault(id))
            .Where(o => o != null && o.ResourceType == resourceType && o.ResourceId == resourceId)
            .ToList()!;
    }

    /// <summary>
    /// Check if a non-terminal obligation of the given type already exists for this resource.
    /// Used for deduplication.
    /// </summary>
    public bool HasActiveObligation(string type, string resourceType, string resourceId)
    {
        return GetByResource(resourceType, resourceId)
            .Any(o => o.Type == type && !o.IsTerminal);
    }

    /// <summary>
    /// Total count of obligations (all states)
    /// </summary>
    public int Count => _obligations.Count;

    /// <summary>
    /// Count of active (non-terminal) obligations
    /// </summary>
    public int ActiveCount => _obligations.Values.Count(o => !o.IsTerminal);

    // ════════════════════════════════════════════════════════════════════════
    // Maintenance
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Prune old terminal obligations to bound memory usage
    /// </summary>
    public int Prune()
    {
        var cutoff = DateTime.UtcNow - TerminalRetention;
        var pruned = 0;

        var terminalObligations = _obligations.Values
            .Where(o => o.IsTerminal)
            .OrderBy(o => o.UpdatedAt)
            .ToList();

        // Prune by age
        foreach (var obligation in terminalObligations)
        {
            if (obligation.UpdatedAt < cutoff)
            {
                TryRemove(obligation.Id);
                pruned++;
            }
        }

        // Prune by count if still over limit
        var remainingTerminal = _obligations.Values
            .Where(o => o.IsTerminal)
            .OrderBy(o => o.UpdatedAt)
            .ToList();

        if (remainingTerminal.Count > MaxTerminalObligations)
        {
            var excess = remainingTerminal.Count - MaxTerminalObligations;
            foreach (var obligation in remainingTerminal.Take(excess))
            {
                TryRemove(obligation.Id);
                pruned++;
            }
        }

        if (pruned > 0)
        {
            _logger.LogDebug("Pruned {Count} terminal obligations", pruned);
        }

        return pruned;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Index Maintenance
    // ════════════════════════════════════════════════════════════════════════

    private void IndexByType(Obligation obligation)
    {
        var bag = _byType.GetOrAdd(obligation.Type, _ => new ConcurrentBag<string>());
        if (!bag.Contains(obligation.Id))
            bag.Add(obligation.Id);
    }

    private void IndexByResource(Obligation obligation)
    {
        var key = $"{obligation.ResourceType}:{obligation.ResourceId}";
        var bag = _byResource.GetOrAdd(key, _ => new ConcurrentBag<string>());
        if (!bag.Contains(obligation.Id))
            bag.Add(obligation.Id);
    }
}
