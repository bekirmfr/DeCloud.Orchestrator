using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;

/// <summary>
/// Resolves <c>__ORCHESTRATOR_PORT__</c> — the orchestrator's WireGuard listen port.
///
/// <para>Returns <c>IConfiguration["WireGuard:OrchestratorPort"]</c> when set;
/// falls back to "51821" (the platform standard).</para>
/// </summary>
public sealed class OrchestratorWgPortResolver : IVariableResolver
{
    public string ResolverKey => "ORCHESTRATOR_PORT";
    public VariableKind Kind => VariableKind.Static;

    private readonly string _port;

    public OrchestratorWgPortResolver(IConfiguration configuration)
    {
        _port = configuration["WireGuard:OrchestratorPort"] ?? "51821";
    }

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (ctx.Obligation is null)
            throw new InvalidOperationException(
                "ORCHESTRATOR_PORT resolver: ctx.Obligation is null. System-VM-only.");

        return Task.FromResult(_port);
    }
}
