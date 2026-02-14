using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Static dependency graph for system VM roles.
///
/// DHT depends on Relay (when present on the same node) because:
///   - DHT VM needs the relay's WireGuard mesh for enrollment and correct
///     address advertisement. Without the relay Active, DHT deploys without
///     WireGuard and advertises unreachable host/libvirt IPs.
///
/// Relay has no dependencies — it boots independently, starts WireGuard,
/// and calls back to the orchestrator to register its public key.
///
/// The DHT→Relay dependency is conditional: it only applies when the node
/// also has a Relay obligation. CGNAT-only nodes (no local relay) are handled
/// by a separate guard in TryDeployAsync that defers DHT until the CGNAT
/// tunnel IP is assigned via heartbeat.
/// </summary>
public static class SystemVmDependencies
{
    private static readonly Dictionary<SystemVmRole, SystemVmRole[]> Dependencies = new()
    {
        [SystemVmRole.Dht]        = [SystemVmRole.Relay],   // needs relay's WG mesh for enrollment
        [SystemVmRole.Relay]      = [],                      // boots independently, no deps
        [SystemVmRole.BlockStore] = [SystemVmRole.Dht],      // needs DHT to announce blocks
        [SystemVmRole.Ingress]    = [SystemVmRole.Dht],      // needs DHT to discover backends
    };

    /// <summary>
    /// Check if all dependencies for a role are Active on this node.
    /// Dependencies are conditional: a role's dependency is only enforced
    /// if the node actually has an obligation for that dependency role.
    /// This allows DHT to deploy without waiting for Relay on nodes that
    /// don't have a Relay obligation (e.g., CGNAT nodes).
    /// </summary>
    public static bool AreDependenciesMet(
        SystemVmRole role,
        List<SystemVmObligation> obligations)
    {
        if (!Dependencies.TryGetValue(role, out var deps))
            return false;

        // Only enforce deps where the node actually has an obligation for that role.
        // E.g., DHT depends on Relay, but only if this node HAS a Relay obligation.
        var applicableDeps = deps.Where(dep =>
            obligations.Any(o => o.Role == dep));

        return applicableDeps.All(dep =>
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
