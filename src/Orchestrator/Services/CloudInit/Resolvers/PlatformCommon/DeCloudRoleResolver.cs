using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__DECLOUD_ROLE__</c> — the canonical role identifier
/// (<c>"dht"</c>, <c>"blockstore"</c>, <c>"relay"</c>).
/// Source: <c>ctx.Obligation.Role</c> mapped via
/// <see cref="SystemVmRoleMap.ToCanonicalName"/> — system VM flows only.
///
/// <para>
/// Tenant flows have no obligation; if the placeholder appears in a tenant
/// template (it shouldn't — currently only in <c>base-system-mesh.yaml</c>),
/// this resolver will throw with a clear message.
/// </para>
///
/// <para>
/// <see cref="SystemVmRole.Ingress"/> has no canonical name (no system-VM
/// deployment mapping); a template using this resolver against an Ingress
/// obligation throws — Ingress doesn't render through this pipeline.
/// </para>
/// </summary>
public sealed class DeCloudRoleResolver : IVariableResolver
{
    public string ResolverKey => "DECLOUD_ROLE";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "DECLOUD_ROLE resolver: ctx.Obligation is null. " +
                "This placeholder is system-VM-only — appearing in a tenant template is a configuration error.");

        var canonical = SystemVmRoleMap.ToCanonicalName(ctx.Obligation.Role);
        if (canonical is null)
            throw new InvalidOperationException(
                $"DECLOUD_ROLE resolver: role {ctx.Obligation.Role} has no canonical mapping " +
                "(only Relay, Dht, BlockStore have system-VM deployments).");

        return Task.FromResult(canonical);
    }
}