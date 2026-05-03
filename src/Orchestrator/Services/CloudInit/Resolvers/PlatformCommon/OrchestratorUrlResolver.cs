using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__ORCHESTRATOR_URL__</c> — the URL the VM reaches the orchestrator on.
/// Source: <c>ctx.OrchestratorUrl</c>, populated from configuration at render call time.
///
/// <para>
/// <b>Co-located guard (design §11 Q1):</b> An empty orchestrator URL silently
/// produces an unusable cloud-init. The error mode is: VM boots, attestation/
/// heartbeat scripts try to <c>curl</c> a blank URL, every call fails. Bad
/// failure shape — looks like a network problem, not a configuration one.
/// This resolver fails loudly at render time so the misconfiguration surfaces
/// before any VM is created.
/// </para>
/// </summary>
public sealed class OrchestratorUrlResolver : IVariableResolver
{
    public string ResolverKey => "ORCHESTRATOR_URL";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ctx.OrchestratorUrl))
            throw new InvalidOperationException(
                "ORCHESTRATOR_URL resolver: ctx.OrchestratorUrl is empty. " +
                "Check the OrchestratorUrl configuration value (appsettings.json or env var). " +
                "An empty value silently produces unusable cloud-init — refusing to render.");
        return Task.FromResult(ctx.OrchestratorUrl.Trim());
    }
}
