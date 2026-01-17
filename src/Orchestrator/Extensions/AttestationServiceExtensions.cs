using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Extensions;

/// <summary>
/// Extension methods for registering attestation services
/// Security-first approach: Validates configuration before registration
/// </summary>
public static class AttestationServiceExtensions
{
    /// <summary>
    /// Add ephemeral attestation services to the DI container
    /// </summary>
    public static IServiceCollection AddAttestationServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // =====================================================
        // CONFIGURATION VALIDATION (Security First)
        // =====================================================
        var attestationSection = configuration.GetSection("Attestation");
        if (!attestationSection.Exists())
        {
            throw new InvalidOperationException(
                "Attestation configuration section is missing. " +
                "Add 'Attestation' section to appsettings.json");
        }

        // Bind and validate configuration
        services.Configure<AttestationConfig>(attestationSection);

        // Add configuration validation on startup
        services.AddOptions<AttestationConfig>()
            .Bind(attestationSection)
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .Validate(config =>
            {
                // Security-critical validation
                if (config.MaxResponseTimeMs <= 0 || config.MaxResponseTimeMs > 1000)
                {
                    throw new ArgumentException(
                        "MaxResponseTimeMs must be between 1-1000ms for security. " +
                        "Longer times allow node to extract ephemeral keys.");
                }

                if (config.FailureThreshold <= 0)
                {
                    throw new ArgumentException("FailureThreshold must be positive");
                }

                if (config.RecoveryThreshold <= 0)
                {
                    throw new ArgumentException("RecoveryThreshold must be positive");
                }

                if (config.StartupChallengeIntervalSeconds <= 0)
                {
                    throw new ArgumentException("StartupChallengeIntervalSeconds must be positive");
                }

                if (config.NormalChallengeIntervalSeconds <= 0)
                {
                    throw new ArgumentException("NormalChallengeIntervalSeconds must be positive");
                }

                return true;
            }, "Attestation configuration is invalid");

        // =====================================================
        // SERVICE REGISTRATION
        // =====================================================

        // Core attestation service (singleton for performance)
        services.AddSingleton<IAttestationService, AttestationService>();

        // Attestation scheduler (singleton, manually resolved as hosted service)
        services.AddSingleton<AttestationSchedulerService>();

        // Register as hosted service (background worker)
        services.AddHostedService<AttestationSchedulerService>(provider =>
            provider.GetRequiredService<AttestationSchedulerService>());

        return services;
    }
}