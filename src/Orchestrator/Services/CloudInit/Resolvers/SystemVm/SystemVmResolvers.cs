using DeCloud.Orchestrator.Interfaces.CloudInit;
using DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm;
using DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.BlockStore;
using DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Dht;
using DeCloud.Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;
using Orchestrator.Services.CloudInit.Resolvers.SystemVm.Relay;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers;

/// <summary>
/// Registers all system-vm <see cref="IVariableResolver"/> implementations
/// as DI singletons in one named call — keeps <c>Program.cs</c> tidy.
///
/// <para>
/// The list is explicit (each resolver named here) rather than reflection-based
/// assembly scanning. Reasoning: a future reader of <c>Program.cs</c> should be
/// able to find which resolvers exist by following the call chain. Assembly
/// scanning would hide registrations behind reflection. The plan §10 risk
/// register echoes this preference for explicitness over magic.
/// </para>
/// </summary>
public static class SystemVmResolvers
{
    public static IServiceCollection AddSystemVmResolvers(this IServiceCollection services)
    {
        services.AddSingleton<IVariableResolver, WgDescriptionResolver>();
        services.AddSingleton<IVariableResolver, WgPublicKeyResolver>();

        //Relay-related resolvers:
        services.AddSingleton<IVariableResolver, OrchestratorPublicKeyResolver>();
        services.AddSingleton<IVariableResolver, OrchestratorIpResolver>();
        services.AddSingleton<IVariableResolver, OrchestratorWgPortResolver>();
        services.AddSingleton<IVariableResolver, RelaySubnetResolver>();
        services.AddSingleton<IVariableResolver, RelayRegionResolver>();
        services.AddSingleton<IVariableResolver, RelayCapacityResolver>();

        //DHT-related resolvers:
        services.AddSingleton<IVariableResolver, DhtRegionResolver>();

        //BlockStore-related resolvers:
        services.AddSingleton<IVariableResolver, BlockStoreRegionResolver>();
        return services;
    }
}
