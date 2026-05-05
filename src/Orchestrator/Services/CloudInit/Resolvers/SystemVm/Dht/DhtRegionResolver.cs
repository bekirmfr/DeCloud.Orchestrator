using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Dht;

/// <summary>
/// Resolves <c>__DHT_REGION__</c> — the node's region label, baked into
/// <c>dht-metadata.json</c> and <c>dht.env</c>'s <c>DECLOUD_REGION</c>
/// at orchestrator render time.
///
/// <para>
/// Static (not Dynamic) per P3.2.6 scope audit: dht-metadata.json is
/// written once at boot; the running DHT binary has no mechanism to
/// observe runtime region changes. The variable is effectively
/// deploy-time-fixed, matching the Static contract. See P3.2.4 patch
/// for full rationale.
/// </para>
///
/// <para>
/// Source: <c>ctx.Node.Region</c>. Falls back to "default" if the node
/// has no region set (development/test environments). Same conventions
/// as <c>RelayRegionResolver</c>.
/// </para>
/// </summary>
public sealed class DhtRegionResolver : IVariableResolver
{
    public string ResolverKey => "DHT_REGION";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var region = ctx.Node?.Region;
        return Task.FromResult(string.IsNullOrEmpty(region) ? "default" : region);
    }
}