using System.Text.Json;
using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;

/// <summary>
/// Resolves <c>__RELAY_SUBNET__</c> — the relay's allocated subnet octet.
///
/// <para>Source: <c>RelayObligationState.RelaySubnet</c> from <c>ctx.Obligation.StateJson</c>.
/// RelaySubnet is stored as a CIDR string (e.g. "10.20.10.0/24") by
/// <see cref="ObligationStateGenerator"/>. This resolver extracts the third octet ("10")
/// for use in relay-metadata.json (<c>"relay_subnet": 10</c>, <c>"tunnel_ip": "10.20.10.254"</c>)
/// and in the wg-relay-server.conf [Interface] Address field.</para>
/// </summary>
public sealed class RelaySubnetResolver : IVariableResolver
{
    public string ResolverKey => "RELAY_SUBNET";
    public VariableKind Kind => VariableKind.Static;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "RELAY_SUBNET resolver: ctx.Obligation is null. System-VM-only.");

        var state = Deserialize(ctx.Obligation.StateJson);

        if (string.IsNullOrEmpty(state?.RelaySubnet))
            throw new InvalidOperationException(
                $"RELAY_SUBNET resolver: RelayObligationState.RelaySubnet is empty for " +
                $"obligation on node {ctx.Node.Id}. ObligationStateGenerator must run before render.");

        // State stores "10.20.X.0/24" — extract the X.
        // Also handle legacy case where state already stored just the octet as a string.
        var octet = ExtractOctet(state.RelaySubnet);
        return Task.FromResult(octet);
    }

    private static string ExtractOctet(string subnet)
    {
        // "10.20.10.0/24" → "10"  (third octet)
        // "10"            → "10"  (already an octet — legacy or test fixture)
        if (!subnet.Contains('.'))
            return subnet; // already an octet or raw integer

        var parts = subnet.TrimEnd('/').Split('.');
        return parts.Length >= 3 ? parts[2].Split('/')[0] : subnet;
    }

    private static RelayObligationState? Deserialize(string? json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize<RelayObligationState>(json, JsonOpts);
}