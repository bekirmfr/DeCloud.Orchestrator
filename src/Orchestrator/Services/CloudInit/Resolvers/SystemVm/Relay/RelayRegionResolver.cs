using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;
/// <summary>
/// Resolves <c>__RELAY_REGION__</c> — the node's geographic region.
///
/// <para>Source: <c>ctx.Node.Region</c>. Falls back to "default" when not set.
/// Value is written into relay-metadata.json at render time and read by relay-api.py
/// from that file. Declared Static (not Dynamic) — relay-api.py reads a static file,
/// not an env var; converting relay-api.py to env-var sourcing is out of scope for
/// Phase 3 (see design §2.3 and P3.1.4 note).</para>
/// </summary>
public sealed class RelayRegionResolver : IVariableResolver
{
    public string ResolverKey => "RELAY_REGION";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct) =>
        Task.FromResult(ctx.Node.Locality.Region);
}
