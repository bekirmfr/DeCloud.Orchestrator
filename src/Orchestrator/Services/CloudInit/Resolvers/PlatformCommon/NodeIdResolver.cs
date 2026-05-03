using System;
using System.Threading;
using System.Threading.Tasks;
using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__NODE_ID__</c> — identifier of the node hosting this VM.
/// Source: <c>ctx.Node.Id</c> (always present; <c>Node</c> is non-nullable in <see cref="ResolutionContext"/>).
/// </summary>
public sealed class NodeIdResolver : IVariableResolver
{
    public string ResolverKey => "NODE_ID";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ctx.Node.Id))
            throw new InvalidOperationException(
                "NODE_ID resolver: ctx.Node.Id is empty — this should never happen for a registered node.");
        return Task.FromResult(ctx.Node.Id);
    }
}
