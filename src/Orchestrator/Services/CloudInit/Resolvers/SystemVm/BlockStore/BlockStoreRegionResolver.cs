using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.BlockStore;

/// <summary>
/// Resolves <c>__BLOCKSTORE_REGION__</c> — the node's region label, baked into
/// <c>blockstore-metadata.json</c> at orchestrator render time.
///
/// <para>
/// Static (not Dynamic) per P3.2.6 scope audit logic: blockstore-metadata.json
/// is written once at boot; the running BlockStore binary has no mechanism to
/// observe runtime region changes. The variable is effectively
/// deploy-time-fixed, matching the Static contract. See P3.2.4 patch on the
/// DHT side for full rationale (same logic applies here).
/// </para>
///
/// <para>
/// Source: <c>ctx.Node.Region</c>. Falls back to "default" if the node has
/// no region set.
/// </para>
/// </summary>
public sealed class BlockStoreRegionResolver : IVariableResolver
{
    public string ResolverKey => "BLOCKSTORE_REGION";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var region = ctx.Node?.Locality.Region;
        return Task.FromResult(string.IsNullOrEmpty(region) ? "default" : region);
    }
}