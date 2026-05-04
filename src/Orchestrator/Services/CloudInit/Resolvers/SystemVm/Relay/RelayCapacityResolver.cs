using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;
/// <summary>
/// Resolves <c>__RELAY_CAPACITY__</c> — the relay's maximum CGNAT node connections.
///
/// <para>Source: <c>ctx.Node.RelayInfo.MaxCapacity</c> when available; falls back
/// to "10". Written into relay-metadata.json at render time.</para>
/// </summary>
public sealed class RelayCapacityResolver : IVariableResolver
{
    public string ResolverKey => "RELAY_CAPACITY";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var capacity = ctx.Node.RelayInfo?.MaxCapacity > 0
            ? ctx.Node.RelayInfo.MaxCapacity.ToString()
            : "10";

        return Task.FromResult(capacity);
    }
}
