using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Interfaces.CloudInit;

/// <summary>
/// Dispatch surface for the resolver pool. The renderer (and the environment
/// endpoint, on the node side) calls <see cref="Lookup"/> for each declared
/// variable to find the resolver responsible for producing its value.
///
/// <para>
/// Returns <c>null</c> on miss — the registry doesn't decide what to do when
/// no resolver is registered. The caller (renderer or endpoint code) decides:
/// for statics, may fall back to user input or default value; for dynamics,
/// missing resolver is a programming error and the caller throws.
/// </para>
/// </summary>
public interface IVariableResolverRegistry
{
    /// <summary>
    /// Look up the resolver responsible for the given <paramref name="key"/>
    /// and <paramref name="kind"/>. Returns <c>null</c> if no resolver is
    /// registered for that combination.
    /// </summary>
    IVariableResolver? Lookup(string key, VariableKind kind);
}
