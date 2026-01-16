using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Extensions;

/// <summary>
/// Extension methods for registering attestation services
/// </summary>
public static class AttestationServiceExtensions
{
    /// <summary>
    /// Add ephemeral attestation services to the DI container
    /// </summary>
    public static IServiceCollection AddEphemeralAttestation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind configuration
        services.Configure<AttestationConfig>(
            configuration.GetSection("Attestation"));

        // Register services
        services.AddSingleton<IAttestationService, EphemeralAttestationService>();
        services.AddSingleton<AttestationSchedulerService>();

        // Register background services
        services.AddHostedService<AttestationSchedulerService>(provider =>
            provider.GetRequiredService<AttestationSchedulerService>());

        // Replace the basic billing service with attestation-aware billing
        services.AddHostedService<AttestationAwareBillingService>();

        return services;
    }
}