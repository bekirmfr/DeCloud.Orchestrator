using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__HOSTNAME__</c> — Linux hostname for the VM.
///
/// <para>
/// Hostname equals VM name by platform convention. This is a separate resolver
/// (rather than a TemplateVariable with <c>ResolverKey = "VM_NAME"</c>) because:
/// (1) callers reading the rendered cloud-init shouldn't have to chase ResolverKey
///     overrides to know what hostname maps to;
/// (2) future requirements may diverge hostname from VM name (e.g., DNS-safe
///     transformation, prefix scheme) — having a dedicated resolver gives that
///     change a clean home.
/// </para>
/// </summary>
public sealed class HostnameResolver : IVariableResolver
{
    public string ResolverKey => "HOSTNAME";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var name = ctx.Vm?.Name ?? ctx.Obligation?.VmName;
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException(
                "HOSTNAME resolver: neither ctx.Vm.Name nor ctx.Obligation.VmName is set.");
        return Task.FromResult(name);
    }
}
