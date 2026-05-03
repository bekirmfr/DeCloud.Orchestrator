using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__PUBLIC_IP__</c> — the host node's reachable public IP address.
/// Source: <c>ctx.Node.PublicIp</c>.
///
/// <para>
/// Used by relay VMs to advertise their reachable endpoint
/// (<c>{public_ip}:51820</c>) to mesh peers. The orchestrator stamps this from
/// the <c>NodeRegistrationRequest.PublicIp</c> at registration time; the node
/// itself discovers it via an external IP-echo service (e.g.,
/// <c>api.ipify.org</c>) at startup.
/// </para>
/// </summary>
public sealed class PublicIpResolver : IVariableResolver
{
    public string ResolverKey => "PUBLIC_IP";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.Node.PublicIp))
            throw new InvalidOperationException(
                $"PUBLIC_IP resolver: ctx.Node.PublicIp is empty for node {ctx.Node.Id}. " +
                "PublicIp is required at node registration; an empty value here indicates the " +
                "node registered with no IP (NAT discovery failed at the node side?).");
        return Task.FromResult(ctx.Node.PublicIp);
    }
}
