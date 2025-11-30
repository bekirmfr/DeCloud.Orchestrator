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
    public ConcurrentDictionary<string, User> Users { get; } = new();
    public ConcurrentDictionary<string, VmImage> Images { get; } = new();
    public ConcurrentDictionary<string, VmPricingTier> PricingTiers { get; } = new();

    // Node auth tokens (nodeId -> token hash)
    public ConcurrentDictionary<string, string> NodeAuthTokens { get; } = new();

    // User sessions (refresh token hash -> user id)
    public ConcurrentDictionary<string, string> UserSessions { get; } = new();

    // Pending commands for nodes (nodeId -> commands)
    public ConcurrentDictionary<string, ConcurrentQueue<NodeCommand>> PendingNodeCommands { get; } = new();

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
    /// Delete a VM (write-through to MongoDB)
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
    public SystemStats GetSystemStats()
    {
        var totalNodes = Nodes.Count;
        var onlineNodes = Nodes.Values.Count(n => n.Status == NodeStatus.Online);
        var totalVms = VirtualMachines.Count;
        var runningVms = VirtualMachines.Values.Count(v => v.Status == VmStatus.Running);
        var totalUsers = Users.Count;
        var activeUsers = Users.Values.Count(u => u.Status == UserStatus.Active);

        // Calculate total resources
        var totalCpu = Nodes.Values.Sum(n => n.TotalResources.CpuCores);
        var totalMemoryMb = Nodes.Values.Sum(n => n.TotalResources.MemoryMb);
        var totalStorageGb = Nodes.Values.Sum(n => n.TotalResources.StorageGb);

        // Calculate available resources (online nodes only)
        var availableNodes = Nodes.Values.Where(n => n.Status == NodeStatus.Online);
        var availableCpu = availableNodes.Sum(n => n.AvailableResources.CpuCores);
        var availableMemoryMb = availableNodes.Sum(n => n.AvailableResources.MemoryMb);
        var availableStorageGb = availableNodes.Sum(n => n.AvailableResources.StorageGb);

        // Calculate used resources
        var usedCpu = totalCpu - availableCpu;
        var usedMemoryMb = totalMemoryMb - availableMemoryMb;
        var usedStorageGb = totalStorageGb - availableStorageGb;

        // Calculate utilization percentages
        var cpuUtilization = totalCpu > 0 ? (double)usedCpu / totalCpu * 100 : 0;
        var memoryUtilization = totalMemoryMb > 0 ? (double)usedMemoryMb / totalMemoryMb * 100 : 0;
        var storageUtilization = totalStorageGb > 0 ? (double)usedStorageGb / totalStorageGb * 100 : 0;

        return new SystemStats
        {
            TotalNodes = totalNodes,
            OnlineNodes = onlineNodes,
            OfflineNodes = totalNodes - onlineNodes,
            TotalVms = totalVms,
            RunningVms = runningVms,
            StoppedVms = VirtualMachines.Values.Count(v => v.Status == VmStatus.Stopped),
            TotalUsers = totalUsers,
            ActiveUsers = activeUsers,
            TotalCpuCores = totalCpu,
            AvailableCpuCores = availableCpu,
            UsedCpuCores = usedCpu,
            CpuUtilizationPercent = cpuUtilization,
            TotalMemoryMb = totalMemoryMb,
            AvailableMemoryMb = availableMemoryMb,
            UsedMemoryMb = usedMemoryMb,
            MemoryUtilizationPercent = memoryUtilization,
            TotalStorageGb = totalStorageGb,
            AvailableStorageGb = availableStorageGb,
            UsedStorageGb = usedStorageGb,
            StorageUtilizationPercent = storageUtilization
        };
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

    public void AddPendingCommand(string nodeId, NodeCommand command)
    {
        var queue = PendingNodeCommands.GetOrAdd(nodeId, _ => new ConcurrentQueue<NodeCommand>());
        queue.Enqueue(command);
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