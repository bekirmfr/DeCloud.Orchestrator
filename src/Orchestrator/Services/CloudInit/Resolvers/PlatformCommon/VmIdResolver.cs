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
        // System VM flow (BLOCKSTORE-FIX §6): VmId is minted on the node at
        // deploy time, so it's null in ResolutionContext at orchestrator render
        // time. Emit the placeholder literal — SystemVmReconciler.ActCreateAsync
        // performs a single-pass __VM_ID__ substitution before DeployAsync.
        // Tenant flows always have ctx.Vm.Id set; this branch never fires for
        // tenants. The earlier "throw on null" was correct for the pre-Phase 3
        // pre-assigned VmId pattern but blocked the design migration.
        return Task.FromResult(string.IsNullOrEmpty(id) ? "__VM_ID__" : id);
    }
}
