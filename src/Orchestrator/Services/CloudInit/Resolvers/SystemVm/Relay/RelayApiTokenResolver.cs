using System.Text.Json;
using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;

/// <summary>
/// Resolves <c>__RELAY_API_TOKEN__</c> — the per-relay control-plane token
/// (<see cref="RelayObligationState.AuthToken"/>) written into relay-metadata.json.
/// relay-api.py requires it as a Bearer token on all mutating endpoints; the
/// orchestrator presents it on add-peer/remove-peer calls. Same trust boundary
/// as the DHT/BlockStore join tokens.
///
/// <para>Source: <c>RelayObligationState.AuthToken</c> from
/// <c>ctx.Obligation.StateJson</c>, populated by <see cref="ObligationStateGenerator"/>.</para>
/// </summary>
public sealed class RelayApiTokenResolver : IVariableResolver
{
    public string ResolverKey => "RELAY_API_TOKEN";
    public VariableKind Kind => VariableKind.Static;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "RELAY_API_TOKEN resolver: ctx.Obligation is null. System-VM-only.");

        var state = string.IsNullOrEmpty(ctx.Obligation.StateJson)
            ? null
            : JsonSerializer.Deserialize<RelayObligationState>(ctx.Obligation.StateJson, JsonOpts);

        if (string.IsNullOrEmpty(state?.AuthToken))
            throw new InvalidOperationException(
                $"RELAY_API_TOKEN resolver: RelayObligationState.AuthToken is empty for " +
                $"obligation on node {ctx.Node.Id}. ObligationStateGenerator must run before render.");

        return Task.FromResult(state.AuthToken);
    }
}