using System.Collections.Concurrent;
using MongoDB.Driver;
using Orchestrator.Models;

namespace Orchestrator.Data;

/// <summary>
/// Hybrid data store for the orchestrator.
/// Uses in-memory dictionaries for fast access with optional MongoDB persistence.
/// Implements write-through caching for production deployments.
/// </summary>
public class DataStore
{
    private readonly IMongoDatabase? _database;
    private readonly ILogger<DataStore> _logger;
    private readonly bool _useMongoDB;

    // Thread-safe in-memory collections for fast access
    public ConcurrentDictionary<string, Node> Nodes { get; } = new();
    public ConcurrentDictionary<string, VirtualMachine> VirtualMachines { get; } = new();
    // Pending commands for nodes (nodeId -> commands)
    public ConcurrentDictionary<string, ConcurrentQueue<NodeCommand>> PendingNodeCommands { get; } = new();
    // Add tracking dictionary
    public ConcurrentDictionary<string, NodeCommand> PendingCommandAcks { get; } = new();
    public ConcurrentDictionary<string, User> Users { get; } = new();
    public ConcurrentDictionary<string, VmImage> Images { get; } = new();
    public ConcurrentDictionary<string, VmPricingTier> PricingTiers { get; } = new();

    // Node auth tokens (nodeId -> token hash)
    public ConcurrentDictionary<string, string> NodeAuthTokens { get; } = new();

    // User sessions (refresh token hash -> user id)
    public ConcurrentDictionary<string, string> UserSessions { get; } = new();

    // Event history (for debugging/auditing)
    public ConcurrentQueue<OrchestratorEvent> EventHistory { get; } = new();
    private const int MaxEventHistory = 10000;

    // MongoDB Collections (null if not configured)
    private IMongoCollection<Node>? NodesCollection =>
        _database?.GetCollection<Node>("nodes");
    private IMongoCollection<VirtualMachine>? VmsCollection =>
        _database?.GetCollection<VirtualMachine>("vms");
    private IMongoCollection<User>? UsersCollection =>
        _database?.GetCollection<User>("users");
    private IMongoCollection<VmImage>? ImagesCollection =>
        _database?.GetCollection<VmImage>("images");
    private IMongoCollection<VmPricingTier>? PricingTiersCollection =>
        _database?.GetCollection<VmPricingTier>("pricingTiers");
    private IMongoCollection<OrchestratorEvent>? EventsCollection =>
        _database?.GetCollection<OrchestratorEvent>("events");

    public DataStore(
        IMongoDatabase? database,
        ILogger<DataStore> logger)
    {
        _database = database;
        _logger = logger;
        _useMongoDB = database != null;

        if (_useMongoDB)
        {
            _logger.LogInformation("DataStore configured with MongoDB persistence");
            CreateIndexes();
        }
        else
        {
            _logger.LogWarning("DataStore running in-memory only - data will not persist!");
        }

        SeedDefaultData();
    }

    /// <summary>
    /// Create MongoDB indexes for optimal query performance
    /// </summary>
    private void CreateIndexes()
    {
        if (!_useMongoDB) return;

        try
        {
            // Nodes indexes
            NodesCollection!.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.WalletAddress),
                    new CreateIndexOptions { Name = "idx_wallet", Unique = true }),
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.Status),
                    new CreateIndexOptions { Name = "idx_status" }),
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.LastHeartbeat),
                    new CreateIndexOptions { Name = "idx_heartbeat" }),
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.Region).Ascending(n => n.Zone),
                    new CreateIndexOptions { Name = "idx_region_zone" })
            });

            // VMs indexes
            VmsCollection!.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.OwnerId),
                    new CreateIndexOptions { Name = "idx_owner" }),
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.NodeId),
                    new CreateIndexOptions { Name = "idx_node" }),
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.Status),
                    new CreateIndexOptions { Name = "idx_status" }),
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.OwnerWallet),
                    new CreateIndexOptions { Name = "idx_wallet" }),
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Descending(v => v.CreatedAt),
                    new CreateIndexOptions { Name = "idx_created" }),
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys
                        .Ascending(v => v.OwnerId)
                        .Descending(v => v.CreatedAt),
                    new CreateIndexOptions { Name = "idx_owner_created" })
            });

            // Users indexes
            UsersCollection!.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(u => u.WalletAddress),
                    new CreateIndexOptions { Name = "idx_wallet", Unique = true }),
                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(u => u.Email),
                    new CreateIndexOptions
                    {
                        Name = "idx_email",
                        Unique = true,
                        Sparse = true // Allow null emails
                    }),
                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(u => u.Status),
                    new CreateIndexOptions { Name = "idx_status" })
            });

            // Events indexes
            EventsCollection!.Indexes.CreateMany(new[]
            {
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Descending(e => e.Timestamp),
                    new CreateIndexOptions { Name = "idx_timestamp" }),
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Ascending(e => e.Type),
                    new CreateIndexOptions { Name = "idx_type" }),
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Ascending(e => e.ResourceId),
                    new CreateIndexOptions { Name = "idx_resource" }),
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Ascending(e => e.UserId),
                    new CreateIndexOptions { Name = "idx_user" })
            });

            _logger.LogInformation("✓ MongoDB indexes created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MongoDB indexes");
            // Don't throw - indexes are optimization, not critical
        }
    }

    /// <summary>
    /// Load state from MongoDB on startup
    /// </summary>
    public async Task LoadStateFromDatabaseAsync()
    {
        if (!_useMongoDB)
        {
            _logger.LogInformation("Skipping database load - MongoDB not configured");
            return;
        }

        _logger.LogInformation("Loading state from MongoDB...");
        var startTime = DateTime.UtcNow;

        try
        {
            // Load Nodes
            var nodes = await NodesCollection!.Find(_ => true).ToListAsync();
            foreach (var node in nodes)
            {
                Nodes.TryAdd(node.Id, node);
            }

            // Load VMs (exclude deleted)
            var vms = await VmsCollection!
                .Find(vm => vm.Status != VmStatus.Deleted)
                .ToListAsync();
            foreach (var vm in vms)
            {
                VirtualMachines.TryAdd(vm.Id, vm);
            }

            // Load Users
            var users = await UsersCollection!.Find(_ => true).ToListAsync();
            foreach (var user in users)
            {
                Users.TryAdd(user.Id, user);
            }

            // Load Images
            var images = await ImagesCollection!.Find(_ => true).ToListAsync();
            foreach (var image in images)
            {
                Images.TryAdd(image.Id, image);
            }

            // Load Pricing Tiers
            var tiers = await PricingTiersCollection!.Find(_ => true).ToListAsync();
            foreach (var tier in tiers)
            {
                PricingTiers.TryAdd(tier.Id, tier);
            }

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogInformation(
                "✓ State loaded from MongoDB in {Elapsed}ms: {Nodes} nodes, {VMs} VMs, {Users} users",
                elapsed, nodes.Count, vms.Count, users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load state from MongoDB");
            throw;
        }
    }

    /// <summary>
    /// Save or update a node (write-through to MongoDB with retry)
    /// </summary>
    public async Task SaveNodeAsync(Node node)
    {
        Nodes[node.Id] = node;

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await NodesCollection!.ReplaceOneAsync(
                    n => n.Id == node.Id,
                    node,
                    new ReplaceOptions { IsUpsert = true });
            }, $"persist node {node.Id}");
        }
    }

    /// <summary>
    /// Delete a node (write-through to MongoDB)
    /// </summary>
    public async Task DeleteNodeAsync(string nodeId)
    {
        Nodes.TryRemove(nodeId, out _);
        NodeAuthTokens.TryRemove(nodeId, out _);

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await NodesCollection!.DeleteOneAsync(n => n.Id == nodeId);
            }, $"delete node {nodeId}");
        }
    }

    /// <summary>
    /// Save or update a VM (write-through to MongoDB with retry)
    /// </summary>
    public async Task SaveVmAsync(VirtualMachine vm)
    {
        vm.UpdatedAt = DateTime.UtcNow;
        VirtualMachines[vm.Id] = vm;

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await VmsCollection!.ReplaceOneAsync(
                    v => v.Id == vm.Id,
                    vm,
                    new ReplaceOptions { IsUpsert = true });
            }, $"persist VM {vm.Id}");
        }
    }

    /// <summary>
    /// Marks a VM state as Deleted (write-through to MongoDB)
    /// </summary>
    public async Task DeleteVmAsync(string vmId)
    {
        if (VirtualMachines.TryGetValue(vmId, out var vm))
        {
            vm.Status = VmStatus.Deleted;
            vm.UpdatedAt = DateTime.UtcNow;

            if (_useMongoDB)
            {
                await RetryMongoOperationAsync(async () =>
                {
                    await VmsCollection!.ReplaceOneAsync(
                        v => v.Id == vmId,
                        vm,
                        new ReplaceOptions { IsUpsert = true });
                }, $"mark VM {vmId} as deleted");
            }
        }
    }

    /// <summary>
    /// Asynchronously and permanently removes the virtual machine identified by the specified ID from the database and
    /// in-memory cache.
    /// </summary>
    /// <remarks>This method deletes the virtual machine from both persistent storage and the in-memory cache.
    /// Once removed, the virtual machine cannot be recovered through this API.</remarks>
    /// <param name="vmId">The unique identifier of the virtual machine to remove. Cannot be null or empty.</param>
    /// <returns>A task that represents the asynchronous remove operation.</returns>
    public async Task RemoveVmAsync(string vmId)
    {
        var filter = Builders<VirtualMachine>.Filter.Eq(v => v.Id, vmId);
        var result = await VmsCollection!.DeleteOneAsync(filter);

        // Remove from in-memory cache
        VirtualMachines.TryRemove(vmId, out _);

        _logger.LogInformation(
            "VM {VmId} permanently removed from database (DeletedCount: {Count})",
            vmId, result.DeletedCount);
    }

    /// <summary>
    /// Save or update a user (write-through to MongoDB)
    /// </summary>
    public async Task SaveUserAsync(User user)
    {
        Users[user.Id] = user;

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await UsersCollection!.ReplaceOneAsync(
                    u => u.Id == user.Id,
                    user,
                    new ReplaceOptions { IsUpsert = true });
            }, $"persist user {user.Id}");
        }
    }

    /// <summary>
    /// Save an event (append-only to MongoDB)
    /// </summary>
    public async Task SaveEventAsync(OrchestratorEvent evt)
    {
        EventHistory.Enqueue(evt);

        // Keep history bounded in memory
        while (EventHistory.Count > MaxEventHistory)
        {
            EventHistory.TryDequeue(out _);
        }

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await EventsCollection!.InsertOneAsync(evt);
            }, $"persist event {evt.Id}", maxRetries: 2); // Events are less critical
        }
    }

    /// <summary>
    /// Bulk sync all in-memory state to MongoDB (for background sync service)
    /// </summary>
    public async Task SyncAllToMongoDBAsync()
    {
        if (!_useMongoDB) return;

        _logger.LogDebug("Starting full sync to MongoDB...");
        var startTime = DateTime.UtcNow;

        try
        {
            // Sync nodes
            var nodeUpdates = Nodes.Values.Select(node =>
                new ReplaceOneModel<Node>(
                    Builders<Node>.Filter.Eq(n => n.Id, node.Id),
                    node)
                {
                    IsUpsert = true
                }).ToList();

            if (nodeUpdates.Any())
            {
                await NodesCollection!.BulkWriteAsync(nodeUpdates);
            }

            // Sync VMs
            var vmUpdates = VirtualMachines.Values.Select(vm =>
                new ReplaceOneModel<VirtualMachine>(
                    Builders<VirtualMachine>.Filter.Eq(v => v.Id, vm.Id),
                    vm)
                {
                    IsUpsert = true
                }).ToList();

            if (vmUpdates.Any())
            {
                await VmsCollection!.BulkWriteAsync(vmUpdates);
            }

            // Sync users
            var userUpdates = Users.Values.Select(user =>
                new ReplaceOneModel<User>(
                    Builders<User>.Filter.Eq(u => u.Id, user.Id),
                    user)
                {
                    IsUpsert = true
                }).ToList();

            if (userUpdates.Any())
            {
                await UsersCollection!.BulkWriteAsync(userUpdates);
            }

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;
            _logger.LogDebug("Full sync to MongoDB completed in {Elapsed}ms", elapsed);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync state to MongoDB");
            throw;
        }
    }

    /// <summary>
    /// Add an event to history (alias for SaveEventAsync for backwards compatibility)
    /// </summary>
    public async Task AddEvent(OrchestratorEvent evt)
    {
        await SaveEventAsync(evt);
    }

    /// <summary>
    /// Get system statistics
    /// </summary>
    public async Task<SystemStats> GetSystemStatsAsync()
    {
        // Only count VMs that are "active" (not Deleting or Deleted)
        var activeVms = VirtualMachines.Values
            .Where(v => v.Status != VmStatus.Deleting &&
                        v.Status != VmStatus.Deleted)
            .ToList();

        var nodes = Nodes.Values.ToList();
        var onlineNodes = nodes.Where(n => n.Status == NodeStatus.Online).ToList();

        var stats = new SystemStats
        {
            // Node statistics
            TotalNodes = nodes.Count,
            OnlineNodes = onlineNodes.Count,
            OfflineNodes = nodes.Count(n => n.Status == NodeStatus.Offline),

            // VM statistics - ONLY ACTIVE VMs
            TotalVms = activeVms.Count,
            RunningVms = activeVms.Count(v => v.Status == VmStatus.Running),
            StoppedVms = activeVms.Count(v => v.Status == VmStatus.Stopped),

            // User statistics
            TotalUsers = Users.Count,
            ActiveUsers = Users.Values
                .Count(u => u.LastLoginAt > DateTime.UtcNow.AddDays(-30)),

            // Resource statistics (from online nodes)
            TotalCpuCores = nodes.Sum(n => n.TotalResources.CpuCores),
            AvailableCpuCores = onlineNodes.Sum(n => n.AvailableResources.CpuCores),
            UsedCpuCores = nodes.Sum(n =>
                n.TotalResources.CpuCores - n.AvailableResources.CpuCores),

            TotalMemoryMb = nodes.Sum(n => n.TotalResources.MemoryMb),
            AvailableMemoryMb = onlineNodes.Sum(n => n.AvailableResources.MemoryMb),
            UsedMemoryMb = nodes.Sum(n =>
                n.TotalResources.MemoryMb - n.AvailableResources.MemoryMb),

            TotalStorageGb = nodes.Sum(n => n.TotalResources.StorageGb),
            AvailableStorageGb = onlineNodes.Sum(n => n.AvailableResources.StorageGb),
            UsedStorageGb = nodes.Sum(n =>
                n.TotalResources.StorageGb - n.AvailableResources.StorageGb),
        };

        // Calculate utilization percentages
        stats.CpuUtilizationPercent = stats.TotalCpuCores > 0
            ? (double)stats.UsedCpuCores / stats.TotalCpuCores * 100
            : 0;

        stats.MemoryUtilizationPercent = stats.TotalMemoryMb > 0
            ? (double)stats.UsedMemoryMb / stats.TotalMemoryMb * 100
            : 0;

        stats.StorageUtilizationPercent = stats.TotalStorageGb > 0
            ? (double)stats.UsedStorageGb / stats.TotalStorageGb * 100
            : 0;

        return stats;
    }

    /// <summary>
    /// Retry MongoDB operations with exponential backoff
    /// </summary>
    private async Task RetryMongoOperationAsync(
        Func<Task> operation,
        string operationName,
        int maxRetries = 3)
    {
        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (MongoException ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1));
                _logger.LogWarning(ex,
                    "MongoDB operation '{Operation}' failed (attempt {Attempt}/{Max}) - retrying in {Delay}ms",
                    operationName, attempt, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to {Operation} to MongoDB (attempt {Attempt}/{Max})",
                    operationName, attempt, maxRetries);

                if (attempt == maxRetries)
                {
                    // Log but don't throw - in-memory update succeeded
                    _logger.LogWarning(
                        "MongoDB persistence failed for {Operation} - continuing with in-memory state only",
                        operationName);
                }
            }
        }
    }

    // =====================================================
    // Pending Commands Queue Management
    // =====================================================

    // <summary>
    /// Add a command to node's pending queue
    /// Automatically tracks acknowledgment if RequiresAck=true
    /// </summary>
    public void AddPendingCommand(string nodeId, NodeCommand command)
    {
        if (!PendingNodeCommands.TryGetValue(nodeId, out var queue))
        {
            queue = new ConcurrentQueue<NodeCommand>();
            PendingNodeCommands[nodeId] = queue;
        }

        queue.Enqueue(command);

        // Automatic acknowledgment tracking - store FULL command
        if (command.RequiresAck && !string.IsNullOrEmpty(command.TargetResourceId))
        {
            PendingCommandAcks[command.CommandId] = command;  // ← Store full object!

            _logger.LogDebug(
                "Command {CommandId} ({Type}) queued for node {NodeId} - tracking ack for resource {ResourceId}",
                command.CommandId, command.Type, nodeId, command.TargetResourceId);
        }
        else if (command.RequiresAck)
        {
            _logger.LogWarning(
                "Command {CommandId} requires ack but has no TargetResourceId - cannot track!",
                command.CommandId);
        }
    }

    // <summary>
    /// Complete command acknowledgment and return the command
    /// </summary>
    public NodeCommand? CompleteCommandAck(string commandId)
    {
        PendingCommandAcks.TryRemove(commandId, out var command);
        return command;
    }

    /// <summary>
    /// Get all commands waiting for acknowledgment
    /// </summary>
    public IReadOnlyCollection<NodeCommand> GetPendingAcks()
    {
        return PendingCommandAcks.Values.ToList();
    }

    public List<NodeCommand> GetAndClearPendingCommands(string nodeId)
    {
        if (!PendingNodeCommands.TryRemove(nodeId, out var queue))
        {
            return new List<NodeCommand>();
        }

        var commands = new List<NodeCommand>();
        while (queue.TryDequeue(out var cmd))
        {
            commands.Add(cmd);
        }

        return commands;
    }

    // =====================================================
    // Default Data Seeding
    // =====================================================

    private void SeedDefaultData()
    {
        // Default VM images
        var images = new[]
        {
            new VmImage
            {
                Id = "ubuntu-24.04",
                Name = "Ubuntu 24.04 LTS",
                Description = "Ubuntu Noble Numbat - Long Term Support",
                OsFamily = "linux",
                OsName = "ubuntu",
                Version = "24.04",
                SizeGb = 4,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "ubuntu-22.04",
                Name = "Ubuntu 22.04 LTS",
                Description = "Ubuntu Jammy Jellyfish - Long Term Support",
                OsFamily = "linux",
                OsName = "ubuntu",
                Version = "22.04",
                SizeGb = 3,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "debian-12",
                Name = "Debian 12 (Bookworm)",
                Description = "Debian Bookworm - Stable Release",
                OsFamily = "linux",
                OsName = "debian",
                Version = "12",
                SizeGb = 3,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var image in images)
        {
            Images.TryAdd(image.Id, image);
        }

        // Default pricing tiers
        var tiers = new[]
        {
            new VmPricingTier
            {
                Id = "standard-small",
                Name = "Standard Small",
                CpuCores = 1,
                MemoryMb = 2048,
                DiskGb = 20,
                HourlyRateCrypto = 0.01m,
                CryptoSymbol = "USDC"
            },
            new VmPricingTier
            {
                Id = "standard-medium",
                Name = "Standard Medium",
                CpuCores = 2,
                MemoryMb = 4096,
                DiskGb = 40,
                HourlyRateCrypto = 0.02m,
                CryptoSymbol = "USDC"
            }
        };

        foreach (var tier in tiers)
        {
            PricingTiers.TryAdd(tier.Id, tier);
        }
    }
}