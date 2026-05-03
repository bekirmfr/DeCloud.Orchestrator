using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Implementation of <see cref="ICloudInitValidator"/>. Stateless; safe as a
/// DI singleton.
///
/// <para>
/// <b>Three failure modes</b> (design §3.4):
/// </para>
/// <list type="number">
///   <item><b>Unresolved static.</b> A placeholder <c>__VARNAME__</c>
///     remains after rendering AND the template declared <c>VARNAME</c> as
///     <c>Static</c>. Means Pass 1 missed substituting it — usually because
///     the resolver returned a value containing the same placeholder, or a
///     resolver was registered against a different <c>ResolverKey</c> than
///     the variable's <c>Name</c>.</item>
///   <item><b>Dynamic in wrong form.</b> A placeholder <c>__VARNAME__</c>
///     exists in the rendered output AND the template declared
///     <c>VARNAME</c> as <c>Dynamic</c>. Dynamics must use <c>$VARNAME</c>
///     shell-source form (resolved at boot via the environment endpoint),
///     not <c>__VARNAME__</c> render-time form. Catches author error in the
///     template body.</item>
///   <item><b>Undeclared placeholder.</b> A placeholder <c>__VARNAME__</c>
///     exists in the rendered output AND no <see cref="TemplateVariable"/>
///     declares <c>VARNAME</c> in either form. Means the template body
///     references a variable that <c>Variables</c> forgot to list. Catches
///     author error in the declaration list.</item>
/// </list>
///
/// <para>
/// <b>All three checks run</b> before any throw — the exception message lists
/// every offender across all three buckets. A template with one of each kind
/// gets fixed in one round trip rather than playing whack-a-mole.
/// </para>
///
/// <para>
/// <b>Known limitation:</b> the regex matches any <c>__NAME__</c> token in
/// the rendered text, including inside YAML comments (<c># See __FOO__</c>).
/// This is rare in real templates and the cure (YAML-aware scanning) is
/// disproportionate. If a comment legitimately needs to mention a placeholder,
/// rephrase or split the underscores (<c>FOO</c>, not <c>__FOO__</c>).
/// </para>
/// </summary>
public sealed class CloudInitValidator : ICloudInitValidator
{
    /// <summary>
    /// Matches <c>__VARNAME__</c> placeholders. First char uppercase, rest
    /// uppercase/digit/underscore. Matches the substitution patterns the
    /// renderer applies; if the renderer's pattern ever changes, this regex
    /// must change in lockstep.
    /// </summary>
    private static readonly Regex PlaceholderRegex = new(
        @"__([A-Z][A-Z0-9_]*?)__",
        RegexOptions.Compiled);

    public void Validate(string rendered, IReadOnlyList<TemplateVariable> declared)
    {
        if (rendered is null) throw new ArgumentNullException(nameof(rendered));
        if (declared is null) throw new ArgumentNullException(nameof(declared));

        var declaredStatic = declared
            .Where(v => v.Kind == VariableKind.Static)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        var declaredDynamic = declared
            .Where(v => v.Kind == VariableKind.Dynamic)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.Ordinal);

        // Distinct + sorted for deterministic error messages.
        var foundPlaceholders = PlaceholderRegex
            .Matches(rendered)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        if (foundPlaceholders.Count == 0) return;  // happy path

        var unresolvedStatics = foundPlaceholders
            .Where(p => declaredStatic.Contains(p))
            .ToList();

        var dynamicsInWrongForm = foundPlaceholders
            .Where(p => declaredDynamic.Contains(p))
            .ToList();

        var undeclared = foundPlaceholders
            .Where(p => !declaredStatic.Contains(p) && !declaredDynamic.Contains(p))
            .ToList();

        if (unresolvedStatics.Count == 0 &&
            dynamicsInWrongForm.Count == 0 &&
            undeclared.Count == 0)
            return;

        // Build a single error message covering all three buckets.
        var sb = new StringBuilder();
        sb.AppendLine("CloudInitValidator: rendered output failed validation.");

        if (unresolvedStatics.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Unresolved statics] These variables are declared as Static but their");
            sb.AppendLine("__VARNAME__ placeholders survived rendering. Either no resolver is");
            sb.AppendLine("registered for the (ResolverKey, Static) pair, or the resolver returned");
            sb.AppendLine("a value that itself contains the same placeholder.");
            foreach (var name in unresolvedStatics)
                sb.AppendLine($"  - __{name}__");
        }

        if (dynamicsInWrongForm.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Dynamics in wrong form] These variables are declared as Dynamic but");
            sb.AppendLine("appear as __VARNAME__ in the template. Dynamics resolve at boot via");
            sb.AppendLine("the environment endpoint and must be referenced as $VARNAME (shell");
            sb.AppendLine("variable), not __VARNAME__ (render-time placeholder).");
            foreach (var name in dynamicsInWrongForm)
                sb.AppendLine($"  - __{name}__  →  use $${name} instead");
        }

        if (undeclared.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[Undeclared placeholders] These __VARNAME__ tokens appear in the");
            sb.AppendLine("rendered output but no TemplateVariable declares them. Either add a");
            sb.AppendLine("matching entry to template.Variables, or remove the placeholder from");
            sb.AppendLine("the cloud-init body.");
            foreach (var name in undeclared)
                sb.AppendLine($"  - __{name}__");
        }

        throw new InvalidOperationException(sb.ToString().TrimEnd());
    }
}
