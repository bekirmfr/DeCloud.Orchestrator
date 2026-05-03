using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Implementation of <see cref="ICloudInitRenderer"/>. Singleton DI lifetime —
/// stateless; all per-render data flows through the
/// <see cref="ResolutionContext"/> argument.
///
/// <para>
/// <b>Two passes:</b>
/// </para>
/// <list type="number">
///   <item><b>Pass 1 — declared statics.</b> Iterates
///     <c>template.Variables.Where(v =&gt; v.Kind == Static)</c>. For each,
///     resolves a value via the <see cref="IVariableResolverRegistry"/>;
///     falls back to <c>UserSuppliedStatics</c>, then <c>DefaultValue</c>,
///     then throws. Substitutes <c>__VARNAME__</c> placeholders with the
///     resolved values via <see cref="string.Replace(string, string)"/>.</item>
///   <item><b>Pass 2 — artifacts.</b> Substitutes
///     <c>${ARTIFACT_URL:name}</c> and <c>${ARTIFACT_SHA256:name}</c> from
///     <c>template.Artifacts</c>, filtered by <c>ctx.TargetArchitecture</c>.
///     Lifted from <c>SystemVmReconciler.SubstituteArtifactVariables</c>.</item>
/// </list>
///
/// <para>
/// <b>Why two passes:</b> the parameterized artifact syntax
/// (<c>${ARTIFACT_URL:dht-node}</c>) doesn't fit
/// <see cref="IVariableResolver"/>'s single-key shape, so artifacts get a
/// dedicated pass. See §2 P1.6 decision-log entry.
/// </para>
///
/// <para>
/// <b>Order matters:</b> pass 1 first. If a resolver's value happens to
/// contain a literal <c>${ARTIFACT_URL:foo}</c>, pass 2 picks it up.
/// Reverse order would let an artifact's URL collide with pass 1's
/// placeholder syntax — the existing artifact pattern doesn't, but the
/// invariant is worth preserving.
/// </para>
/// </summary>
public sealed class CloudInitRenderer : ICloudInitRenderer
{
    /// <summary>
    /// Local node-agent artifact cache endpoint. Every node serves on virbr0
    /// at this address; baking it in at orchestrator render time produces a
    /// cloud-init that any node can run.
    /// </summary>
    private const string ArtifactBaseUrl = "http://192.168.122.1:5100/api/artifacts";

    private readonly IVariableResolverRegistry _registry;
    private readonly ICloudInitValidator? _validator;

    public CloudInitRenderer(
        IVariableResolverRegistry registry,
        ICloudInitValidator? validator = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = validator;  // null until P1.8 lands; renderer skips validation
    }

    public async Task<string> RenderAsync(
        VmTemplate template,
        ResolutionContext ctx,
        CancellationToken ct)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));

        if (string.IsNullOrEmpty(template.CloudInitTemplate))
            throw new InvalidOperationException(
                $"Template '{template.Slug}' has empty CloudInitTemplate — nothing to render.");

        // ── Pass 1: declared statics ──────────────────────────────────────
        var substitutions = await ResolveStaticsAsync(template, ctx, ct);

        var rendered = template.CloudInitTemplate;
        foreach (var (placeholder, value) in substitutions)
        {
            rendered = rendered.Replace(placeholder, value, StringComparison.Ordinal);
        }

        // ── Pass 2: artifacts ─────────────────────────────────────────────
        rendered = SubstituteArtifacts(rendered, template.Artifacts, ctx.TargetArchitecture);

        // ── Pass 3: validation ────────────────────────────────────────────
        // No-op when P1.8 hasn't landed yet (validator is null).
        _validator?.Validate(rendered, template.Variables);

        return rendered;
    }

    /// <summary>
    /// Build the {placeholder → value} dictionary by walking declared statics
    /// and applying the resolution chain: resolver → user input → default →
    /// throw.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveStaticsAsync(
        VmTemplate template,
        ResolutionContext ctx,
        CancellationToken ct)
    {
        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var v in template.Variables.Where(v => v.Kind == VariableKind.Static))
        {
            if (!seen.Add(v.Name))
                throw new InvalidOperationException(
                    $"Template '{template.Slug}' declares static variable '{v.Name}' more than once. " +
                    "Each variable Name must be unique within a template.");

            var lookupKey = string.IsNullOrEmpty(v.ResolverKey) ? v.Name : v.ResolverKey;
            var resolver = _registry.Lookup(lookupKey, VariableKind.Static);

            string value;
            if (resolver is not null)
            {
                value = await resolver.ResolveAsync(ctx, ct);
            }
            else if (ctx.UserSuppliedStatics.TryGetValue(v.Name, out var userValue))
            {
                value = userValue;
            }
            else if (v.DefaultValue is not null)
            {
                value = v.DefaultValue;
            }
            else if (v.Required)
            {
                throw new InvalidOperationException(
                    $"Template '{template.Slug}' requires static variable '{v.Name}' " +
                    $"(ResolverKey='{lookupKey}'), but no resolver is registered, no user-supplied " +
                    "value was provided, and no DefaultValue is set. Either register an " +
                    $"IVariableResolver for ('{lookupKey}', Static), supply a value via " +
                    "ResolutionContext.UserSuppliedStatics, or set Variables[].DefaultValue.");
            }
            else
            {
                throw new InvalidOperationException(
                    $"Template '{template.Slug}' has unresolvable static variable '{v.Name}' " +
                    $"(ResolverKey='{lookupKey}', not Required). No resolver, no user input, no " +
                    "default. Falling back to empty string would silently corrupt rendering — " +
                    "set DefaultValue (even to '') to make the empty intent explicit.");
            }

            substitutions[$"__{v.Name}__"] = value;
        }

        return substitutions;
    }

    /// <summary>
    /// Substitute <c>${ARTIFACT_URL:name}</c> and <c>${ARTIFACT_SHA256:name}</c>
    /// for each artifact whose architecture matches the target (or is universal).
    /// Lifted from <c>SystemVmReconciler.SubstituteArtifactVariables</c>; keeps
    /// the same semantics so node-side and orchestrator-side rendering produce
    /// identical output during the cutover.
    /// </summary>
    private static string SubstituteArtifacts(
        string cloudInit,
        IReadOnlyList<TemplateArtifact> artifacts,
        string targetArchitecture)
    {
        if (artifacts is null || artifacts.Count == 0) return cloudInit;

        var result = cloudInit;

        foreach (var artifact in artifacts)
        {
            // Skip arch-specific artifacts that don't match the target.
            // Universal artifacts (Architecture == null) match every target.
            if (artifact.Architecture is not null &&
                !string.Equals(artifact.Architecture, targetArchitecture,
                    StringComparison.OrdinalIgnoreCase))
                continue;

            var urlKey = $"${{ARTIFACT_URL:{artifact.Name}}}";
            var sha256Key = $"${{ARTIFACT_SHA256:{artifact.Name}}}";
            var localUrl = $"{ArtifactBaseUrl}/{artifact.Sha256}";

            result = result
                .Replace(urlKey, localUrl, StringComparison.Ordinal)
                .Replace(sha256Key, artifact.Sha256, StringComparison.Ordinal);
        }

        return result;
    }
}
