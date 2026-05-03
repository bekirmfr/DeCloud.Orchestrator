using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__TIMESTAMP__</c> — render time in ISO 8601 round-trip format.
///
/// <para>
/// Useful for marking when a cloud-init was generated (e.g., embedded in a
/// motd or final_message). <c>"O"</c> format yields
/// <c>2026-05-03T12:34:56.7890123Z</c> — round-trippable, sortable,
/// timezone-explicit.
/// </para>
/// </summary>
public sealed class TimestampResolver : IVariableResolver
{
    public string ResolverKey => "TIMESTAMP";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        return Task.FromResult(DateTimeOffset.UtcNow.ToString("O"));
    }
}
