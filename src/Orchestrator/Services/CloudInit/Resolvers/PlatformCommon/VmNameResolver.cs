using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__VM_NAME__</c> — the human-readable VM name.
/// Source: <c>ctx.Vm.Name</c> (tenant flow) or <c>ctx.Obligation.VmName</c> (system flow, P1.10).
/// </summary>
public sealed class VmNameResolver : IVariableResolver
{
    public string ResolverKey => "VM_NAME";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var name = ctx.Vm?.Name ?? ctx.Obligation?.VmName;
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException(
                "VM_NAME resolver: neither ctx.Vm.Name nor ctx.Obligation.VmName is set. " +
                "For tenant flows, ensure VirtualMachine is constructed before render. " +
                "For system flows, P1.11 must have assigned VmName at obligation creation.");
        return Task.FromResult(name);
    }
}
