using System.Collections.Generic;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Interfaces.CloudInit;

/// <summary>
/// Verifies a rendered cloud-init document against the template's declared
/// variables. Throws on violations.
///
/// <para>
/// <b>Forward declaration for P1.7.</b> The full implementation
/// (<c>CloudInitValidator</c>) lands in P1.8. This interface is defined now
/// so <see cref="ICloudInitRenderer"/> can depend on it; if P1.8 refines the
/// shape (e.g., returns a result object instead of throwing), the interface
/// here gets updated and the renderer's call site adjusts. Until P1.8 lands,
/// <see cref="ICloudInitRenderer"/> takes this dependency as nullable and
/// skips validation when no implementation is registered in DI.
/// </para>
///
/// <para>
/// <b>Three intended failure modes</b> (design §3.4):
/// </para>
/// <list type="number">
///   <item><b>Unresolved static.</b> A <c>__VARNAME__</c> placeholder
///     remains after rendering. Means a declared static didn't get
///     substituted (resolver returned empty, or substitution missed).</item>
///   <item><b>Undeclared placeholder.</b> A <c>__VARNAME__</c> exists in
///     the rendered output but no matching <see cref="TemplateVariable"/>
///     declares it. Means the template has a placeholder it forgot to
///     declare in <c>Variables</c>.</item>
///   <item><b>Dynamic in wrong form.</b> A <see cref="TemplateVariable"/>
///     declared as <see cref="VariableKind.Dynamic"/> appears as
///     <c>__VARNAME__</c> in the rendered output. Dynamics must use
///     <c>$VARNAME</c> shell-source form, not render-time substitution.</item>
/// </list>
/// </summary>
public interface ICloudInitValidator
{
    /// <summary>
    /// Validate <paramref name="rendered"/> against <paramref name="declared"/>.
    /// Throws <see cref="System.InvalidOperationException"/> on violations
    /// with a message that lists the offending placeholders.
    /// </summary>
    void Validate(string rendered, IReadOnlyList<TemplateVariable> declared);
}
