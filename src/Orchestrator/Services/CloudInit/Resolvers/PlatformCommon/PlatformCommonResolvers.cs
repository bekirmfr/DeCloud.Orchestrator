using DeCloud.Orchestrator.Interfaces.CloudInit;

namespace DeCloud.Orchestrator.Services.CloudInit.Resolvers.PlatformCommon;

/// <summary>
/// Registers all platform-common <see cref="IVariableResolver"/> implementations
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
public static class PlatformCommonResolvers
{
    public static IServiceCollection AddPlatformCommonResolvers(this IServiceCollection services)
    {
        services.AddSingleton<IVariableResolver, VmIdResolver>();
        services.AddSingleton<IVariableResolver, VmNameResolver>();
        services.AddSingleton<IVariableResolver, HostnameResolver>();
        services.AddSingleton<IVariableResolver, NodeIdResolver>();
        services.AddSingleton<IVariableResolver, OrchestratorUrlResolver>();
        services.AddSingleton<IVariableResolver, CaPublicKeyResolver>();
        services.AddSingleton<IVariableResolver, SshAuthorizedKeysBlockResolver>();
        services.AddSingleton<IVariableResolver, PasswordConfigBlockResolver>();
        services.AddSingleton<IVariableResolver, SshPasswordAuthResolver>();
        services.AddSingleton<IVariableResolver, AdminPasswordResolver>();
        services.AddSingleton<IVariableResolver, TimestampResolver>();
        services.AddSingleton<IVariableResolver, VariableScopesBlockResolver>();
        services.AddSingleton<IVariableResolver, DeCloudRoleResolver>();
        services.AddSingleton<IVariableResolver, HostMachineIdResolver>();
        services.AddSingleton<IVariableResolver, PublicIpResolver>();
        return services;
    }
}
