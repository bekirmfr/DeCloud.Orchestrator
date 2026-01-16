// src/Orchestrator/Extensions/PaymentExtensions.cs
// Dependency injection registration for payment and attestation services

using Orchestrator.Background;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Extensions;

public static class PaymentExtensions
{
    /// <summary>
    /// Register payment and attestation services
    /// </summary>
    public static IServiceCollection AddPaymentServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Load configuration
        var paymentConfig = new PaymentConfig();
        configuration.GetSection("Payment").Bind(paymentConfig);
        services.AddSingleton(paymentConfig);
        
        // Register background services
        services.AddHostedService<DepositMonitorService>();
        services.AddHostedService<BillingService>();
        //services.AddHostedService<SettlementService>();
        
        return services;
    }
}
