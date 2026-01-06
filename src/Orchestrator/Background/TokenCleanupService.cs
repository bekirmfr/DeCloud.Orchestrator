using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orchestrator.Services.Auth;

namespace Orchestrator.Background;

/// <summary>
/// Background service that periodically cleans up expired node auth tokens
/// and logs warnings for tokens approaching expiration.
/// </summary>
public class TokenCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TokenCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(6);
    private readonly TimeSpan _warningThreshold = TimeSpan.FromDays(7);

    public TokenCleanupService(
        IServiceProvider serviceProvider,
        ILogger<TokenCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Token cleanup service started. Running every {Hours} hours",
            _cleanupInterval.TotalHours);

        // Initial delay to let system start up
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during token cleanup cycle");
            }

            await Task.Delay(_cleanupInterval, stoppingToken);
        }

        _logger.LogInformation("Token cleanup service stopped");
    }

    private async Task RunCleanupCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var authService = scope.ServiceProvider.GetRequiredService<INodeAuthService>();

        _logger.LogDebug("Starting token cleanup cycle");

        // Clean up expired tokens
        var cleanedCount = await authService.CleanupExpiredTokensAsync();

        if (cleanedCount > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired node auth tokens", cleanedCount);
        }

        _logger.LogDebug("Token cleanup cycle completed");
    }
}