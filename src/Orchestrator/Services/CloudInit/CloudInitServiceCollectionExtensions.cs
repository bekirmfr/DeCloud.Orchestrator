using DeCloud.Orchestrator.Interfaces.CloudInit;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Service-collection extensions for the cloud-init resolver pipeline.
/// Keeps <c>Program.cs</c> tidy by giving the wire-up a single named entry point.
/// </summary>
public static class CloudInitServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IVariableResolverRegistry"/> singleton.
    /// Individual resolvers (P1.6 onwards) register themselves with
    /// <c>AddSingleton&lt;IVariableResolver, FooResolver&gt;()</c>; the
    /// registry's constructor receives them via
    /// <c>IEnumerable&lt;IVariableResolver&gt;</c>.
    ///
    /// <para>
    /// Call this once in <c>Program.cs</c>. Resolvers added afterwards
    /// (in any order) will be picked up at registry construction time.
    /// </para>
    /// </summary>
    public static IServiceCollection AddVariableResolverRegistry(this IServiceCollection services)
    {
        services.AddSingleton<IVariableResolverRegistry, VariableResolverRegistry>();
        return services;
    }

    /// <summary>
    /// Registers the <see cref="ICloudInitRenderer"/> singleton.
    /// Depends on <see cref="IVariableResolverRegistry"/> (call
    /// <see cref="AddVariableResolverRegistry"/> first or alongside) and
    /// optionally <see cref="ICloudInitValidator"/> (P1.8 — when registered,
    /// the renderer validates; when not, it skips).
    /// </summary>
    public static IServiceCollection AddCloudInitRenderer(this IServiceCollection services)
    {
        services.AddSingleton<ICloudInitRenderer, CloudInitRenderer>();
        return services;
    }
}
