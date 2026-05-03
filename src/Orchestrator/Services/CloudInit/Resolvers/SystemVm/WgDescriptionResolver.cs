using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm;

/// <summary>
/// Resolves <c>__WG_DESCRIPTION__</c> — the WireGuard peer description
/// stamped into <c>/etc/decloud/wg-mesh.env</c> for mesh-participant
/// system VMs (DHT, BlockStore). Read by <c>wg-mesh-enroll.sh</c> when
/// registering the peer with the relay's API.
///
/// <para>
/// Format: <c>{canonical-role}-{vm-id}</c>, e.g.
/// <c>"dht-a1b2c3d4-1234-5678-90ab-cdef12345678"</c>. Matches the
/// convention in <c>system-vms/shared/assets/wg-config-fetch.sh</c>:
/// <c>WG_DESCRIPTION=${ROLE}-${VM_ID}</c>.
/// </para>
///
/// <para>
/// Source: <c>ctx.Obligation.Role</c> (canonical name) and
/// <c>ctx.Obligation.VmId</c> (assigned by P1.11 at obligation creation).
/// </para>
///
/// <para>
/// System-VM-only. Tenant flows have no obligation; if this placeholder
/// appears in a tenant template (it shouldn't — currently only in
/// <c>base-system-mesh.yaml</c>), this resolver throws with a clear
/// message naming the misconfiguration.
/// </para>
///
/// <para>
/// Note: <c>wg-config-fetch.sh</c> rewrites this file at runtime when
/// the relay's WireGuard config becomes available. The cloud-init
/// rendered value is the initial state for the boot window before
/// that script runs. Pre-F4, the initial state was a literal
/// <c>__WG_DESCRIPTION__</c> string; post-F4, it's a meaningful
/// description that's at least syntactically valid.
/// </para>
/// </summary>
public sealed class WgDescriptionResolver : IVariableResolver
{
    public string ResolverKey => "WG_DESCRIPTION";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "WG_DESCRIPTION resolver: ctx.Obligation is null. " +
                "This placeholder is system-VM-only — appearing in a tenant template " +
                "is a configuration error.");

        var role = SystemVmRoleMap.ToCanonicalName(ctx.Obligation.Role);
        if (role is null)
            throw new InvalidOperationException(
                $"WG_DESCRIPTION resolver: role {ctx.Obligation.Role} has no canonical mapping. " +
                "Only Relay, Dht, BlockStore have system-VM deployments — and Relay does not " +
                "use base-system-mesh.yaml so should not declare WG_DESCRIPTION.");

        var vmId = ctx.Obligation.VmId;
        if (string.IsNullOrEmpty(vmId))
            throw new InvalidOperationException(
                "WG_DESCRIPTION resolver: ctx.Obligation.VmId is empty. " +
                "P1.11 must have assigned VmId at obligation creation. " +
                "Pre-P1.11 obligations carry null VmId; this resolver fails until the " +
                "obligation is recreated (typically at next reconciliation cycle).");

        return Task.FromResult($"{role}-{vmId}");
    }
}