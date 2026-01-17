using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Background service that periodically syncs in-memory state to MongoDB.
/// Provides eventual consistency and backup in case write-through fails.
/// </summary>
public class MongoDBSyncService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly ILogger<MongoDBSyncService> _logger;
    private readonly TimeSpan _syncInterval;

    public MongoDBSyncService(
        DataStore dataStore,
        IConfiguration configuration,
        ILogger<MongoDBSyncService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;

        // Default to 60 seconds, configurable
        var intervalSeconds = configuration.GetValue("MongoDB:SyncIntervalSeconds", 60);
        _syncInterval = TimeSpan.FromSeconds(intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if MongoDB is configured
        var mongoUri = _dataStore.GetType()
            .GetField("_useMongoDB", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(_dataStore) as bool?;

        if (mongoUri != true)
        {
            _logger.LogInformation("MongoDB sync service disabled - MongoDB not configured");
            return;
        }

        _logger.LogInformation(
            "MongoDB sync service started with interval {Interval}s",
            _syncInterval.TotalSeconds);

        // Initial delay to let the system start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _dataStore.SyncAllToMongoDBAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during periodic MongoDB sync");
            }

            try
            {
                await Task.Delay(_syncInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when shutting down
                break;
            }
        }

        _logger.LogInformation("MongoDB sync service stopped");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Performing final sync to MongoDB before shutdown...");

        try
        {
            await _dataStore.SyncAllToMongoDBAsync();
            _logger.LogInformation("Final sync completed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform final sync to MongoDB");
        }

        await base.StopAsync(cancellationToken);
    }
}