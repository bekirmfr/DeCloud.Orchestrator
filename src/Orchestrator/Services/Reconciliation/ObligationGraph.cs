using Orchestrator.Models;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// Resolves the dependency graph of obligations and returns them in execution order.
/// Uses Kahn's algorithm (BFS topological sort) for deterministic ordering.
///
/// Given obligations A→B→C (A depends on B, B depends on C):
///   - Resolve() returns [C, B, A] — execute leaves first
///   - Only obligations whose dependencies are ALL Completed are "ready"
///   - Cycle detection prevents infinite loops
///
/// The graph is rebuilt from scratch each reconciliation tick (stateless).
/// This is O(V+E) where V=obligations, E=dependency edges — fast for our scale.
/// </summary>
public static class ObligationGraph
{
    /// <summary>
    /// Resolve obligations into execution order.
    /// Returns only the obligations that are ready to execute (all deps completed).
    /// Obligations are sorted by priority (descending) within each dependency level.
    /// </summary>
    public static ObligationResolution Resolve(IReadOnlyList<Obligation> obligations)
    {
        if (obligations.Count == 0)
            return ObligationResolution.Empty;

        // Build lookup and adjacency
        var lookup = new Dictionary<string, Obligation>(obligations.Count);
        var inDegree = new Dictionary<string, int>(obligations.Count);
        var dependents = new Dictionary<string, List<string>>(obligations.Count);

        foreach (var ob in obligations)
        {
            lookup[ob.Id] = ob;
            inDegree[ob.Id] = 0;
            dependents[ob.Id] = new List<string>();
        }

        // Count in-degrees and build reverse adjacency
        foreach (var ob in obligations)
        {
            foreach (var depId in ob.DependsOn)
            {
                // Only count dependencies that are in the active set
                if (lookup.ContainsKey(depId))
                {
                    inDegree[ob.Id]++;
                    dependents[depId].Add(ob.Id);
                }
                // If dependency is not in active set, it's either completed (good)
                // or missing (check separately)
            }
        }

        // Kahn's algorithm — BFS from nodes with in-degree 0
        var queue = new PriorityQueue<string, int>();
        foreach (var (id, degree) in inDegree)
        {
            if (degree == 0)
            {
                // Use negative priority so higher priority values come first
                queue.Enqueue(id, -lookup[id].Priority);
            }
        }

        var sorted = new List<Obligation>(obligations.Count);
        var visited = 0;

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            sorted.Add(lookup[id]);
            visited++;

            foreach (var depId in dependents[id])
            {
                inDegree[depId]--;
                if (inDegree[depId] == 0)
                {
                    queue.Enqueue(depId, -lookup[depId].Priority);
                }
            }
        }

        // Cycle detection
        List<string>? cycleParticipants = null;
        if (visited < obligations.Count)
        {
            cycleParticipants = inDegree
                .Where(kvp => kvp.Value > 0)
                .Select(kvp => kvp.Key)
                .ToList();
        }

        // Determine which obligations are ready to execute
        var ready = new List<Obligation>();
        var blocked = new List<Obligation>();

        foreach (var ob in sorted)
        {
            if (ob.IsTerminal || !ob.IsReadyForAttempt)
                continue;

            if (ob.Status == ObligationStatus.WaitingForSignal)
                continue;

            var allDepsCompleted = AreDependenciesMet(ob, lookup);
            if (allDepsCompleted)
            {
                ready.Add(ob);
            }
            else
            {
                blocked.Add(ob);
            }
        }

        return new ObligationResolution
        {
            ExecutionOrder = sorted,
            Ready = ready,
            Blocked = blocked,
            CycleParticipants = cycleParticipants
        };
    }

    /// <summary>
    /// Check if all dependencies of an obligation are satisfied.
    /// A dependency is "met" if:
    ///   - It exists in the active set and is Completed, OR
    ///   - It does NOT exist in the active set (assumed completed and pruned)
    /// </summary>
    private static bool AreDependenciesMet(
        Obligation obligation,
        Dictionary<string, Obligation> lookup)
    {
        foreach (var depId in obligation.DependsOn)
        {
            if (lookup.TryGetValue(depId, out var dep))
            {
                // Dependency is in active set — must be Completed
                if (dep.Status != ObligationStatus.Completed)
                    return false;
            }
            // If not in active set, it's been pruned → was completed
        }

        return true;
    }

    /// <summary>
    /// Find all obligations that would be affected if a given obligation fails.
    /// Returns the transitive closure of dependents.
    /// Used for cascade-cancel decisions.
    /// </summary>
    public static List<string> GetTransitiveDependents(
        string obligationId,
        IReadOnlyList<Obligation> obligations)
    {
        var result = new List<string>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(obligationId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var ob in obligations)
            {
                if (ob.DependsOn.Contains(current) && visited.Add(ob.Id))
                {
                    result.Add(ob.Id);
                    queue.Enqueue(ob.Id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Validate that adding a dependency would not create a cycle.
    /// Returns true if the dependency is safe to add.
    /// </summary>
    public static bool CanAddDependency(
        string fromId,
        string toId,
        IReadOnlyList<Obligation> obligations)
    {
        // fromId depends on toId.
        // This creates a cycle if toId transitively depends on fromId.
        var transitiveDeps = GetTransitiveDependents(fromId, obligations);
        return !transitiveDeps.Contains(toId);
    }
}

/// <summary>
/// Result of resolving the obligation dependency graph
/// </summary>
public class ObligationResolution
{
    /// <summary>
    /// All obligations in topological (execution) order
    /// </summary>
    public IReadOnlyList<Obligation> ExecutionOrder { get; init; } = Array.Empty<Obligation>();

    /// <summary>
    /// Obligations whose dependencies are all met and are ready to execute
    /// </summary>
    public IReadOnlyList<Obligation> Ready { get; init; } = Array.Empty<Obligation>();

    /// <summary>
    /// Obligations that have unmet dependencies
    /// </summary>
    public IReadOnlyList<Obligation> Blocked { get; init; } = Array.Empty<Obligation>();

    /// <summary>
    /// Obligation IDs that participate in a dependency cycle (if any).
    /// Null if no cycles detected.
    /// </summary>
    public List<string>? CycleParticipants { get; init; }

    /// <summary>
    /// True if the dependency graph contains cycles
    /// </summary>
    public bool HasCycles => CycleParticipants is { Count: > 0 };

    public static readonly ObligationResolution Empty = new();
}
