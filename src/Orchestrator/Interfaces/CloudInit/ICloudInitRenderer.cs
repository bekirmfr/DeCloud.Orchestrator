using System.Threading;
using System.Threading.Tasks;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Interfaces.CloudInit;

/// <summary>
/// Renders a <see cref="VmTemplate"/>'s <c>CloudInitTemplate</c> to a fully
/// substituted cloud-init document, ready to ship to a node as
/// <c>VmSpec.UserData</c> (tenant) or <c>SystemVmTemplate.CloudInitContent</c>
/// (system).
///
/// <para>
/// <b>Two substitution passes</b> (see design §3.2 + §2 P1.6 decision log):
/// </para>
/// <list type="number">
///   <item><b>Pass 1 — declared statics.</b> Iterates
///     <c>template.Variables</c> where <c>Kind == Static</c>. For each,
///     looks up an <see cref="IVariableResolver"/> by
///     <c>(ResolverKey ?? Name, Static)</c>. Falls back to
///     <c>UserSuppliedStatics[Name]</c>, then <c>DefaultValue</c>, then
///     throws. Substitutes <c>__VARNAME__</c> placeholders with the
///     resolved values.</item>
///   <item><b>Pass 2 — artifacts.</b> Substitutes
///     <c>${ARTIFACT_URL:name}</c> and <c>${ARTIFACT_SHA256:name}</c>
///     using <c>template.Artifacts</c>, filtered to
///     <c>ctx.TargetArchitecture</c>. Architecture-universal artifacts
///     (no <c>Architecture</c> field set) match every target.</item>
/// </list>
///
/// <para>
/// After both passes, hands off to <see cref="ICloudInitValidator"/> if
/// registered. The validator detects unresolved statics, undeclared
/// placeholders, and dynamics-in-wrong-form (P1.8). If no validator is
/// registered, the renderer returns the rendered string as-is.
/// </para>
/// </summary>
public interface ICloudInitRenderer
{
    /// <summary>
    /// Render <paramref name="template"/>'s cloud-init bytes to a fully
    /// substituted document, using <paramref name="ctx"/> as the resolution
    /// context for declared static variables.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">
    /// Thrown when a required variable can't be resolved (no resolver, no
    /// user input, no default), when a resolver throws, when a duplicate
    /// variable name is declared, or when validation (if enabled) fails.
    /// Exception message names the offending variable / placeholder.
    /// </exception>
    Task<string> RenderAsync(VmTemplate template, ResolutionContext ctx, CancellationToken ct);
}
