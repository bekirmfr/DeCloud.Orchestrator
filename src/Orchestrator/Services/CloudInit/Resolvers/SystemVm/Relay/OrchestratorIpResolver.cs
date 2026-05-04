using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Microsoft.Extensions.Configuration;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;

/// <summary>
/// Resolves <c>__ORCHESTRATOR_IP__</c> — the orchestrator's WireGuard endpoint IP.
///
/// <para>Derived from <c>ctx.OrchestratorUrl</c> host. When the orchestrator URL uses
/// localhost/127.0.0.1 (co-located deployment), substitutes with <c>ctx.Node.PublicIp</c>
/// because the relay VM cannot reach localhost from inside its libvirt network.
/// Mirrors the logic in <c>LibvirtVmManager</c> STEP 5.5.</para>
/// </summary>
public sealed class OrchestratorIpResolver : IVariableResolver
{
    public string ResolverKey => "ORCHESTRATOR_IP";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "ORCHESTRATOR_IP resolver: ctx.Obligation is null. System-VM-only.");

        var host = new Uri(ctx.OrchestratorUrl).Host;
        var resolved = host is "localhost" or "127.0.0.1" or "::1"
            ? (ctx.Node.PublicIp ?? host)
            : host;

        return Task.FromResult(resolved);
    }
}