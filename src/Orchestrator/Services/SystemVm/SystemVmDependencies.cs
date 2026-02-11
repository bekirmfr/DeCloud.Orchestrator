using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Static dependency graph for system VM roles.
/// Each role declares what other roles must be Active before it can deploy.
///
/// Today: everything depends on DHT (the discovery layer).
/// Tomorrow: just add entries — the reconciliation loop doesn't change.
///
/// Example future dependency:
///   [SystemVmRole.Ingress] = [SystemVmRole.Dht, SystemVmRole.BlockStore]
/// </summary>
public static class SystemVmDependencies
{
    private static readonly Dictionary<SystemVmRole, SystemVmRole[]> Dependencies = new()
    {
        [SystemVmRole.Dht]        = [],                         // no deps, always first
        [SystemVmRole.Relay]      = [SystemVmRole.Dht],         // needs DHT to register itself
        [SystemVmRole.BlockStore] = [SystemVmRole.Dht],         // needs DHT to announce blocks
        [SystemVmRole.Ingress]    = [SystemVmRole.Dht],         // needs DHT to discover backends
    };

    /// <summary>
    /// Check if all dependencies for a role are Active on this node.
    /// </summary>
    public static bool AreDependenciesMet(
        SystemVmRole role,
        List<SystemVmObligation> obligations)
    {
        if (!Dependencies.TryGetValue(role, out var deps))
            return false;

        return deps.All(dep =>
            obligations.Any(o => o.Role == dep && o.Status == SystemVmStatus.Active));
    }

    /// <summary>
    /// Get the direct dependencies for a role.
    /// </summary>
    public static IReadOnlyList<SystemVmRole> GetDependencies(SystemVmRole role)
    {
        return Dependencies.TryGetValue(role, out var deps) ? deps : [];
    }
}
