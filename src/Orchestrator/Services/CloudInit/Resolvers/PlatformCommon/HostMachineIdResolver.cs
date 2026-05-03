using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__HOST_MACHINE_ID__</c> — the host node's hardware fingerprint.
/// Source: <c>ctx.Node.MachineId</c>.
///
/// <para>
/// Used by system VMs to authenticate to the node-local API (HMAC-keyed by
/// machineId in some endpoints) and to identify which physical host the VM
/// runs on. Survives node re-registration; tied to hardware, not to the
/// generated <see cref="NodeIdResolver">NodeId</see>.
/// </para>
/// </summary>
public sealed class HostMachineIdResolver : IVariableResolver
{
    public string ResolverKey => "HOST_MACHINE_ID";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.Node.MachineId))
            throw new InvalidOperationException(
                $"HOST_MACHINE_ID resolver: ctx.Node.MachineId is empty for node {ctx.Node.Id}. " +
                "MachineId is set at registration; an empty value indicates a corrupted or " +
                "partially-registered node record.");
        return Task.FromResult(ctx.Node.MachineId);
    }
}
