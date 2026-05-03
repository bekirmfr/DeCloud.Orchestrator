using System.Text.Json;
using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm;

/// <summary>
/// Resolves <c>__WIREGUARD_PUBLIC_KEY__</c> — the base64 WireGuard public key
/// for the system VM being deployed. Read from the obligation's StateJson,
/// which is the orchestrator's authoritative copy of the identity state.
///
/// <para>
/// <b>Currently used in:</b>
/// <c>system-vms/relay/cloud-init.yaml</c> — <c>/etc/decloud/relay-metadata.json</c>.
/// The relay role layer's v6.0 design comment classifies this metadata file as
/// "documentary metadata, not source of truth" — the runtime authoritative file
/// is <c>/etc/wireguard/public.key</c>, written by the identity-fetch runcmd
/// from obligation state. This resolver fills the documentary field so it
/// carries a real value rather than leaking the literal placeholder string.
/// </para>
///
/// <para>
/// <b>Source:</b> <c>ctx.Obligation.StateJson</c>, deserialized into the
/// role-specific state class. All three role state classes
/// (<see cref="RelayObligationState"/>, <see cref="DhtObligationState"/>,
/// <see cref="BlockStoreObligationState"/>) expose <c>WireGuardPublicKey</c>.
/// The resolver dispatches on <c>ctx.Obligation.Role</c> to pick the right
/// deserializer. DHT and BlockStore templates do not currently reference
/// <c>__WIREGUARD_PUBLIC_KEY__</c>, but the resolver works for all three
/// roles in case a future role layer references it.
/// </para>
///
/// <para>
/// <b>System-VM-only.</b> Throws if <c>ctx.Obligation</c> is null
/// (i.e., used in tenant flow) — same convention as <see cref="WgDescriptionResolver"/>.
/// </para>
///
/// <para>
/// <b>Public key only.</b> This resolver never reads or returns the private
/// key. Its output is safe to log. The private key never leaves the
/// orchestrator and is delivered to the VM only via the obligation-state
/// fetch endpoint over the local libvirt bridge.
/// </para>
/// </summary>
public sealed class WgPublicKeyResolver : IVariableResolver
{
    public string ResolverKey => "WIREGUARD_PUBLIC_KEY";
    public VariableKind Kind => VariableKind.Static;

    /// <summary>
    /// Mirrors the deserialization conventions used by RelayController.cs and
    /// NodeService.GenerateAndAttachObligationStates so behaviour is consistent
    /// with how StateJson is read elsewhere in the orchestrator.
    /// </summary>
    private static readonly JsonSerializerOptions DeserializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "WIREGUARD_PUBLIC_KEY resolver: ctx.Obligation is null. " +
                "This placeholder is system-VM-only — appearing in a tenant template " +
                "is a configuration error.");

        if (string.IsNullOrEmpty(ctx.Obligation.StateJson))
            throw new InvalidOperationException(
                $"WIREGUARD_PUBLIC_KEY resolver: ctx.Obligation.StateJson is empty for " +
                $"role {ctx.Obligation.Role}. ObligationStateGenerator must run before " +
                "template render — StateVersion stays 0 until first registration generates " +
                "state. If this fires, the render call site is rendering before state " +
                "generation in the request flow, or for an obligation that was created " +
                "without state.");

        // Dispatch by role. All three system-VM role state classes expose a
        // WireGuardPublicKey property; the only difference is the type to
        // deserialize into.
        var publicKey = ctx.Obligation.Role switch
        {
            SystemVmRole.Relay => Deserialize<RelayObligationState>(ctx.Obligation.StateJson)?.WireGuardPublicKey,
            SystemVmRole.Dht => Deserialize<DhtObligationState>(ctx.Obligation.StateJson)?.WireGuardPublicKey,
            SystemVmRole.BlockStore => Deserialize<BlockStoreObligationState>(ctx.Obligation.StateJson)?.WireGuardPublicKey,
            _ => throw new InvalidOperationException(
                $"WIREGUARD_PUBLIC_KEY resolver: role {ctx.Obligation.Role} has no " +
                "WireGuard identity state class. Only Relay, Dht, BlockStore have " +
                "WG keypairs (Ingress is the only other system VM role and does not " +
                "use WireGuard).")
        };

        if (string.IsNullOrEmpty(publicKey))
            throw new InvalidOperationException(
                $"WIREGUARD_PUBLIC_KEY resolver: deserialized {ctx.Obligation.Role} state " +
                "but WireGuardPublicKey field is empty. ObligationStateGenerator produced " +
                "incomplete state, or StateJson is corrupted. Inspect the obligation in " +
                "MongoDB: db.nodes.findOne({_id: '<nodeId>'}, " +
                "{'systemVmObligations.$': 1}).");

        return Task.FromResult(publicKey);
    }

    private static T? Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, DeserializerOptions);
}