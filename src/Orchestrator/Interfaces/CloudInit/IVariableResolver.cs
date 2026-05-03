using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace DeCloud.Orchestrator.Interfaces.CloudInit;

/// <summary>
/// Resolves the value of one declared <see cref="TemplateVariable"/> at render
/// time (Static) or at environment-endpoint serve time (Dynamic).
///
/// <para>
/// One implementation per <c>(ResolverKey, Kind)</c> pair. The registry indexes
/// resolvers by both fields — Static and Dynamic resolvers for the same name
/// are distinct registrations, because they answer different questions:
/// </para>
///
/// <list type="bullet">
///   <item>A Static resolver answers <i>"what value should be baked into the
///     cloud-init bytes for this VM?"</i> — called once, at template render
///     time. Result is substituted into <c>__VARNAME__</c> placeholders.</item>
///   <item>A Dynamic resolver answers <i>"what is the current value for this
///     running VM right now?"</i> — called every time the node-local
///     <c>/api/obligations/{role}/environment</c> endpoint is hit. Result
///     flows to the in-VM watcher via shell-source.</item>
/// </list>
///
/// <para>
/// Resolvers that cannot produce a value should throw a clear exception. The
/// contract is "if you're in the registry and lookup matched, you produce a
/// string". Cases where a value is legitimately unavailable (user input
/// missing, default value applies) are handled by the renderer's fallback
/// chain — not by the resolver.
/// </para>
/// </summary>
public interface IVariableResolver
{
    /// <summary>
    /// The key used to look up this resolver. Matches the
    /// <see cref="TemplateVariable.ResolverKey"/> of declared variables, which
    /// defaults to <see cref="TemplateVariable.Name"/> when not overridden.
    /// </summary>
    string ResolverKey { get; }

    /// <summary>
    /// Whether this resolver answers Static (render-time) or Dynamic
    /// (runtime) questions. The same logical name may have separate Static
    /// and Dynamic resolvers.
    /// </summary>
    VariableKind Kind { get; }

    /// <summary>
    /// Compute the value for this variable in the given context. Must always
    /// return a non-null string; throw with a clear message if a required
    /// input is missing.
    /// </summary>
    Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct);
}

/// <summary>
/// All inputs a resolver may need. Built once per render call (or environment
/// endpoint hit) by the renderer / endpoint code; passed to every resolver.
///
/// <para>
/// Some fields are nullable to accommodate the asymmetry between system-VM
/// and tenant-VM flows:
/// </para>
///
/// <list type="bullet">
///   <item><see cref="Obligation"/> is non-null for system-VM flows
///     (relay/dht/blockstore) and null for tenant-VM flows.</item>
///   <item><see cref="Vm"/> is non-null for tenant-VM flows and null for
///     system-VM flows (system VMs are tracked via obligation, not via
///     <c>VirtualMachine</c> documents).</item>
/// </list>
///
/// <para>
/// Resolvers that need a particular field should defensively check and throw
/// with a clear message if it's null. The renderer doesn't validate which
/// resolvers can run in which flow — that's an emergent property of which
/// resolvers a template's <c>Variables</c> list invokes.
/// </para>
/// </summary>
public sealed record ResolutionContext(
    Node Node,
    SystemVmObligation? Obligation,
    VirtualMachine? Vm,
    VmTemplate Template,
    string OrchestratorUrl,
    string TargetArchitecture,
    IReadOnlyDictionary<string, string> UserSuppliedStatics);
