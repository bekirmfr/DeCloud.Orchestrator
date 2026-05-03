using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Shared.Models;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Singleton implementation of <see cref="IVariableResolverRegistry"/>.
/// Built once at startup from DI's <c>IEnumerable&lt;IVariableResolver&gt;</c>;
/// read-only thereafter.
///
/// <para>
/// Each resolver is indexed by <c>(ResolverKey, Kind)</c>. Two resolvers
/// claiming the same key+kind are a programming error — caught at construction
/// with a clear exception (rather than the cryptic
/// <c>ArgumentException</c> from <c>ToDictionary</c>).
/// </para>
///
/// <para>
/// Thread-safe for read access without locking — the dictionary is populated
/// during construction and never mutated after. Registry lookups happen on
/// every render call and every environment-endpoint hit; locking would be
/// pure overhead.
/// </para>
/// </summary>
public sealed class VariableResolverRegistry : IVariableResolverRegistry
{
    private readonly Dictionary<(string Key, VariableKind Kind), IVariableResolver> _resolvers;

    public VariableResolverRegistry(IEnumerable<IVariableResolver> resolvers)
    {
        _resolvers = new Dictionary<(string, VariableKind), IVariableResolver>();

        foreach (var resolver in resolvers)
        {
            var key = (resolver.ResolverKey, resolver.Kind);
            if (_resolvers.TryGetValue(key, out var existing))
            {
                throw new InvalidOperationException(
                    $"Duplicate resolver registration for ({resolver.ResolverKey}, " +
                    $"{resolver.Kind}): both {existing.GetType().FullName} and " +
                    $"{resolver.GetType().FullName} claim this key. " +
                    $"A given (ResolverKey, Kind) must have exactly one resolver.");
            }
            _resolvers[key] = resolver;
        }
    }

    public IVariableResolver? Lookup(string key, VariableKind kind)
        => _resolvers.GetValueOrDefault((key, kind));

    /// <summary>
    /// Diagnostic accessor — exposes the registered keys for startup logging.
    /// Not used for dispatch.
    /// </summary>
    public IReadOnlyList<(string Key, VariableKind Kind)> RegisteredKeys
        => _resolvers.Keys.OrderBy(k => k.Kind).ThenBy(k => k.Key).ToArray();
}
