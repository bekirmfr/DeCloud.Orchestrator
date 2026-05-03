using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__DECLOUD_ROLE__</c> — the canonical role identifier
/// (<c>"dht"</c>, <c>"blockstore"</c>, <c>"relay"</c>).
/// Source: <c>ctx.Obligation.Role</c> — system VM flows only.
///
/// <para>
/// Tenant flows have no role; if the placeholder appears in a tenant template
/// (it shouldn't — currently only in <c>base-system-mesh.yaml</c>), this
/// resolver will throw with a clear message.
/// </para>
/// </summary>
public sealed class DeCloudRoleResolver : IVariableResolver
{
    public string ResolverKey => "DECLOUD_ROLE";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var role = ctx.Obligation?.Role;
        if (string.IsNullOrEmpty(role))
            throw new InvalidOperationException(
                "DECLOUD_ROLE resolver: ctx.Obligation is null or has no Role. " +
                "This placeholder is system-VM-only — appearing in a tenant template is a configuration error.");
        return Task.FromResult(role);
    }
}
