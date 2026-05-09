using Orchestrator.Services.Locality;

namespace Orchestrator.Extensions;

/// <summary>
/// Service-collection extensions for the locality service.
/// Keeps <c>Program.cs</c> tidy by giving the wire-up a single named entry
/// point. Same pattern as <see cref="AttestationServiceExtensions"/> and
/// the cloud-init resolver registration helpers.
/// </summary>
public static class LocalityServiceExtensions
{
    /// <summary>
    /// Registers <see cref="ILocalityService"/> as a singleton.
    ///
    /// <para>
    /// The implementation loads four embedded JSON resources at construction
    /// (<c>countries.json</c>, <c>regions.json</c>, <c>region-adjacency.json</c>,
    /// <c>country-region-defaults.json</c>) and validates referential
    /// integrity. If any file is missing, malformed, or inconsistent, the
    /// orchestrator fails to start with a clear error — by design.
    /// </para>
    ///
    /// <para>
    /// Call this once in <c>Program.cs</c>:
    /// <code>builder.Services.AddLocalityServices();</code>
    /// </para>
    /// </summary>
    public static IServiceCollection AddLocalityServices(
        this IServiceCollection services)
    {
        services.AddSingleton<ILocalityService, LocalityService>();
        return services;
    }
}
