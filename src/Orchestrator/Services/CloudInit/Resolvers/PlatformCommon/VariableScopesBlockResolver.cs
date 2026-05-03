using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using System.Text;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Resolves <c>__VARIABLE_SCOPES_BLOCK__</c> — the contents of
/// <c>/etc/decloud-{role}/variable-scopes.conf</c> that the in-VM watcher
/// reads to know how to react when a dynamic variable's value changes.
///
/// <para>
/// Iterates <c>ctx.Template.Variables</c> for entries with
/// <c>Kind == Dynamic</c> and emits one <c>NAME=scope</c> line per entry,
/// with scope as lowercase string (<c>noop</c>/<c>reload</c>/<c>restart</c>) —
/// matches the watcher's parser (see <c>decloud-env-watcher.sh</c>):
/// </para>
/// <code>
/// while IFS='=' read -r k v; do SCOPES[$k]=${v%%#*}; done &lt; &lt;(grep -v '^#' "$SCOPE_FILE")
/// </code>
///
/// <para>
/// <b>Indent assumption:</b> 6 spaces, matching the standard write_files
/// content placement. If a base-file template uses a different indent, override
/// via a custom resolver. See P1.5 IndentForYaml semantics (line 1 bare,
/// lines 2+ indented).
/// </para>
///
/// <para>
/// <b>Forward-compatibility note:</b> No base file currently emits this
/// placeholder. The resolver works correctly when no template requests it,
/// and produces the right output when a future base-file change adds the
/// scope-file write_files entry.
/// </para>
/// </summary>
public sealed class VariableScopesBlockResolver : IVariableResolver
{
    private const int ScopeBlockIndentSpaces = 6;

    public string ResolverKey => "VARIABLE_SCOPES_BLOCK";
    public VariableKind Kind => VariableKind.Static;

    public Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct)
    {
        var dynamics = ctx.Template.Variables
            .Where(v => v.Kind == VariableKind.Dynamic)
            .OrderBy(v => v.Name)  // deterministic ordering — same input → same output
            .ToList();

        if (dynamics.Count == 0)
            return Task.FromResult("# No dynamic variables declared");

        var sb = new StringBuilder();
        for (var i = 0; i < dynamics.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            var scope = dynamics[i].Scope.ToString().ToLowerInvariant();
            sb.Append(dynamics[i].Name).Append('=').Append(scope);
        }

        return Task.FromResult(
            CloudInitFormatting.IndentForYaml(sb.ToString(), ScopeBlockIndentSpaces));
    }
}
