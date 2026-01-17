using Microsoft.Extensions.Options;
using Orchestrator.Services;
using Orchestrator.Interfaces.Blockchain;
using Orchestrator.Models;
using Orchestrator.Services;
using Orchestrator.Services.Payment;
using Orchestrator.Services.Settlement;

namespace Orchestrator.Extensions;

/// <summary>
/// Extension methods for registering payment and billing services
/// Security-first approach with blockchain integration
/// </summary>
public static class PaymentExtensions
{
    /// <summary>
    /// Register payment and billing services
    /// </summary>
    public static IServiceCollection AddPaymentServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // =====================================================
        // CONFIGURATION VALIDATION (Security First)
        // =====================================================
        var paymentSection = configuration.GetSection("Payment");
        if (!paymentSection.Exists())
        {
            throw new InvalidOperationException(
                "Payment configuration section is missing. " +
                "Add 'Payment' section to appsettings.json");
        }

        // Bind and validate configuration
        services.Configure<PaymentConfig>(paymentSection);

        // Add configuration validation on startup
        services.AddOptions<PaymentConfig>()
            .Bind(paymentSection)
            .ValidateDataAnnotations()
            .ValidateOnStart()
            .Validate(config =>
            {
                // Validate blockchain configuration
                if (string.IsNullOrWhiteSpace(config.ChainId))
                {
                    throw new ArgumentException("ChainId is required for blockchain integration");
                }

                if (string.IsNullOrWhiteSpace(config.RpcUrl))
                {
                    throw new ArgumentException("RpcUrl is required for blockchain integration");
                }

                if (string.IsNullOrWhiteSpace(config.EscrowContractAddress))
                {
                    throw new ArgumentException("EscrowContractAddress is required");
                }

                if (string.IsNullOrWhiteSpace(config.UsdcTokenAddress))
                {
                    throw new ArgumentException("UsdcTokenAddress is required");
                }

                // Validate orchestrator wallet (critical for settlements)
                if (string.IsNullOrWhiteSpace(config.OrchestratorWalletAddress) ||
                    config.OrchestratorWalletAddress == "0x0000000000000000000000000000000000000000")
                {
                    throw new ArgumentException(
                        "Valid OrchestratorWalletAddress is required. " +
                        "Cannot use null address (0x000...000)");
                }

                // SECURITY: Never commit private key, must use environment variable
                if (string.IsNullOrWhiteSpace(config.OrchestratorPrivateKey))
                {
                    throw new ArgumentException(
                        "OrchestratorPrivateKey is required for signing settlement transactions. " +
                        "MUST be set via environment variable, NEVER commit to source control!");
                }

                if (config.OrchestratorPrivateKey == "_NEVER_COMMIT_THIS_USE_ENV_VAR_")
                {
                    throw new ArgumentException(
                        "OrchestratorPrivateKey placeholder detected. " +
                        "Set actual private key via environment variable.");
                }

                // Validate confirmation requirements
                if (config.RequiredConfirmations <= 0)
                {
                    throw new ArgumentException("RequiredConfirmations must be positive");
                }

                // Validate settlement configuration
                if (config.MinSettlementAmount <= 0)
                {
                    throw new ArgumentException("MinSettlementAmount must be positive");
                }

                if (config.SettlementInterval <= TimeSpan.Zero)
                {
                    throw new ArgumentException("SettlementInterval must be positive");
                }

                // Validate fee structure
                if (config.PlatformFeePercent < 0 || config.PlatformFeePercent > 100)
                {
                    throw new ArgumentException(
                        "PlatformFeePercent must be between 0-100");
                }

                return true;
            }, "Payment configuration is invalid");

        // Create singleton instance with validated configuration
        var paymentConfig = new PaymentConfig();
        paymentSection.Bind(paymentConfig);
        services.AddSingleton(paymentConfig);

        // =====================================================
        // SERVICE REGISTRATION
        // =====================================================

        // Blockchain Service - handles ALL Web3 interactions
        services.AddSingleton<IBlockchainService, BlockchainService>();

        // Settlement Service - handles usage tracking and settlements
        services.AddSingleton<ISettlementService, SettlementService>();

        // =====================================================
        // BACKGROUND SERVICES (Order matters for dependency resolution)
        // =====================================================

        // 2. Attestation-Aware Billing Service - bills users based on verified runtime
        //    NOTE: This integrates with IAttestationService to pause billing when attestation fails
        services.AddHostedService<Services.BillingService>();

        // 3. Settlement Service - batches payments to nodes (commented out for now)
        // services.AddHostedService<SettlementService>();

        return services;
    }
}