using Microsoft.Extensions.Options;
using Nethereum.Util;

namespace Orchestrator.Services;

/// <summary>
/// Hosted service that ensures an admin user exists on startup
/// </summary>
public class AdminUserInitializer : IHostedService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AdminUserInitializer> _logger;

    public AdminUserInitializer(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        ILogger<AdminUserInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Initializing admin user...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var userService = scope.ServiceProvider.GetRequiredService<IUserService>();

            // Get admin wallet from configuration
            var adminWalletAddress = _configuration["Admin:WalletAddress"];

            if (string.IsNullOrWhiteSpace(adminWalletAddress))
            {
                _logger.LogWarning("No admin wallet address configured. Skipping admin user creation.");
                _logger.LogWarning("To configure an admin user, add 'Admin:WalletAddress' to appsettings.json");
                return;
            }

            // Normalize to checksum format
            var normalizedAddress = new AddressUtil().ConvertToChecksumAddress(adminWalletAddress);
            _logger.LogInformation("Admin wallet address (checksum): {Address}", normalizedAddress);

            // Check if admin user exists
            var existingUser = await userService.GetUserByWalletAsync(normalizedAddress);

            if (existingUser != null)
            {
                // User exists - ensure they have admin role
                if (!existingUser.Roles.Contains("Admin"))
                {
                    _logger.LogInformation("Adding Admin role to existing user: {Wallet}", normalizedAddress);
                    existingUser.Roles.Add("Admin");
                    await userService.UpdateUserAsync(existingUser);
                    _logger.LogInformation("✅ Admin role added to user: {Wallet}", normalizedAddress);
                }
                else
                {
                    _logger.LogInformation("✅ Admin user already exists: {Wallet}", normalizedAddress);
                }
            }
            else
            {
                // Create new admin user
                _logger.LogInformation("Creating new admin user: {Wallet}", normalizedAddress);
                var adminUser = await userService.CreateUserAsync(normalizedAddress);
                
                // Add admin role
                adminUser.Roles.Clear();
                adminUser.Roles.Add("Admin");
                adminUser.Roles.Add("User");
                adminUser.DisplayName = "System Administrator";
                
                await userService.UpdateUserAsync(adminUser);
                
                _logger.LogInformation("✅ Admin user created successfully: {Wallet}", normalizedAddress);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize admin user");
            // Don't throw - allow application to start even if admin initialization fails
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
