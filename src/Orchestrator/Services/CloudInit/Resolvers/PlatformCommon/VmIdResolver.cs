using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__VM_ID__</c> — the unique VM identifier (UUID).
/// Source: <c>ctx.Vm.Id</c> (tenant flow) or <c>ctx.Obligation.VmId</c> (system flow, P1.11).
/// </summary>
public sealed class VmIdResolver : IVariableResolver
{
    public string ResolverKey => "VM_ID";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var id = ctx.Vm?.Id ?? ctx.Obligation?.VmId;
        if (string.IsNullOrEmpty(id))
            throw new InvalidOperationException(
                "VM_ID resolver: neither ctx.Vm.Id nor ctx.Obligation.VmId is set. " +
                "For tenant flows, ensure VirtualMachine is constructed before render. " +
                "For system flows, P1.11 must have assigned VmId at obligation creation.");
        return Task.FromResult(id);
    }
}
