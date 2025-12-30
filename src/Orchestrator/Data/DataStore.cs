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
    /// <summary>
    /// Command registry for reliable command→VM tracking.
    /// Key: commandId, Value: CommandRegistration
    /// This provides a reliable way to look up which VM a command belongs to,
    /// independent of the VM's StatusMessage field.
    /// </summary>
    public ConcurrentDictionary<string, CommandRegistration> CommandRegistry { get; } = new();
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
        var syncResults = new
        {
            NodesSuccess = 0,
            NodesFailed = 0,
            VmsSuccess = 0,
            VmsFailed = 0,
            UsersSuccess = 0,
            UsersFailed = 0
        };

        try
        {
            // Sync nodes - bulk operation is safe here
            try
            {
                var nodeUpdates = Nodes.Values.Select(node =>
                    new ReplaceOneModel<Node>(
                        Builders<Node>.Filter.Eq(n => n.Id, node.Id),
                        node)
                    {
                        IsUpsert = true
                    }).ToList();

                if (nodeUpdates.Any())
                {
                    var result = await NodesCollection!.BulkWriteAsync(nodeUpdates,
                        new BulkWriteOptions { IsOrdered = false });
                    syncResults = syncResults with { NodesSuccess = (int)result.ModifiedCount + (int)result.InsertedCount };
                }
            }
            catch (MongoBulkWriteException ex)
            {
                syncResults = syncResults with { NodesFailed = ex.WriteErrors.Count };
                _logger.LogWarning(ex, "Partial failure syncing nodes: {FailedCount} errors",
                    ex.WriteErrors.Count);
            }

            // Sync VMs - bulk operation is safe here
            try
            {
                var vmUpdates = VirtualMachines.Values.Select(vm =>
                    new ReplaceOneModel<VirtualMachine>(
                        Builders<VirtualMachine>.Filter.Eq(v => v.Id, vm.Id),
                        vm)
                    {
                        IsUpsert = true
                    }).ToList();

                if (vmUpdates.Any())
                {
                    var result = await VmsCollection!.BulkWriteAsync(vmUpdates,
                        new BulkWriteOptions { IsOrdered = false });
                    syncResults = syncResults with { VmsSuccess = (int)result.ModifiedCount + (int)result.InsertedCount };
                }
            }
            catch (MongoBulkWriteException ex)
            {
                syncResults = syncResults with { VmsFailed = ex.WriteErrors.Count };
                _logger.LogWarning(ex, "Partial failure syncing VMs: {FailedCount} errors",
                    ex.WriteErrors.Count);
            }

            // Sync users - INDIVIDUAL OPERATIONS to avoid bulk write constraint violations
            // This is necessary because even with sparse indexes, bulk operations can fail
            // if there are transient constraint violations or replication lag
            var usersList = Users.Values.ToList();
            foreach (var user in usersList)
            {
                try
                {
                    await UsersCollection!.ReplaceOneAsync(
                        u => u.Id == user.Id,
                        user,
                        new ReplaceOptions { IsUpsert = true });

                    syncResults = syncResults with { UsersSuccess = syncResults.UsersSuccess + 1 };
                }
                catch (MongoWriteException ex) when (ex.WriteError.Code == 11000)
                {
                    // Duplicate key error - log details
                    syncResults = syncResults with { UsersFailed = syncResults.UsersFailed + 1 };

                    if (ex.WriteError.Message.Contains("idx_email"))
                    {
                        _logger.LogWarning(
                            "User {UserId} ({Wallet}) sync failed due to email index constraint. " +
                            "Email: {Email}. This may indicate a database/memory state mismatch. " +
                            "Consider restarting the orchestrator to reload state from MongoDB.",
                            user.Id, user.WalletAddress, user.Email ?? "null");
                    }
                    else if (ex.WriteError.Message.Contains("idx_wallet"))
                    {
                        _logger.LogWarning(
                            "User {UserId} ({Wallet}) sync failed due to wallet index constraint. " +
                            "This indicates a duplicate wallet address in memory vs database.",
                            user.Id, user.WalletAddress);
                    }
                    else
                    {
                        _logger.LogWarning(ex,
                            "User {UserId} ({Wallet}) sync failed with duplicate key error: {Error}",
                            user.Id, user.WalletAddress, ex.WriteError.Message);
                    }
                }
                catch (Exception ex)
                {
                    syncResults = syncResults with { UsersFailed = syncResults.UsersFailed + 1 };
                    _logger.LogError(ex,
                        "Failed to sync user {UserId} ({Wallet})",
                        user.Id, user.WalletAddress);
                }
            }

            var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

            if (syncResults.NodesFailed > 0 || syncResults.VmsFailed > 0 || syncResults.UsersFailed > 0)
            {
                _logger.LogWarning(
                    "Full sync completed with errors in {Elapsed}ms. " +
                    "Nodes: {NodesOk}/{NodesTotal} | VMs: {VmsOk}/{VmsTotal} | Users: {UsersOk}/{UsersTotal}",
                    elapsed,
                    syncResults.NodesSuccess, Nodes.Count,
                    syncResults.VmsSuccess, VirtualMachines.Count,
                    syncResults.UsersSuccess, Users.Count);
            }
            else
            {
                _logger.LogDebug(
                    "Full sync completed successfully in {Elapsed}ms. " +
                    "Nodes: {Nodes} | VMs: {Vms} | Users: {Users}",
                    elapsed,
                    syncResults.NodesSuccess,
                    syncResults.VmsSuccess,
                    syncResults.UsersSuccess);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical failure during MongoDB sync");
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
    // src/Orchestrator/Data/DataStore.cs

    public async Task<SystemStats> GetSystemStatsAsync()
    {
        var nodes = Nodes.Values.ToList();
        var onlineNodes = nodes.Where(n => n.Status == NodeStatus.Online).ToList();
        var vms = VirtualMachines.Values.ToList();

        // ========================================
        // CALCULATE ACTUAL RESOURCE USAGE FROM VMs
        // This provides self-healing against reservation drift
        // ========================================
        var activeVms = vms.Where(v =>
            v.Status == VmStatus.Running ||
            v.Status == VmStatus.Provisioning).ToList();

        var totalComputePoints = onlineNodes.Sum(n => n.TotalResources.ComputePoints);
        var actualUsedPoints = activeVms.Sum(v => v.Spec.ComputePointCost);

        var actualUsedMemory = activeVms.Sum(v => (long)v.Spec.MemoryBytes);
        var actualUsedStorage = activeVms.Sum(v => (long)v.Spec.DiskBytes);
        var actualUsedCores = activeVms.Sum(v => v.Spec.VirtualCpuCores);

        var stats = new SystemStats
        {
            TotalNodes = nodes.Count,
            TotalCpuCores = onlineNodes.Sum(n => n.HardwareInventory.Cpu.PhysicalCores),
            OnlineNodes = onlineNodes.Count,
            OfflineNodes = nodes.Count(n => n.Status == NodeStatus.Offline),
            MaintenanceNodes = nodes.Count(n => n.Status == NodeStatus.Maintenance),

            TotalVms = vms.Count,
            RunningVms = vms.Count(v => v.Status == VmStatus.Running),
            PendingVms = vms.Count(v => v.Status == VmStatus.Pending),
            StoppedVms = vms.Count(v => v.Status == VmStatus.Stopped),

            // ========================================
            // POINT-BASED CPU STATISTICS (SELF-HEALING)
            // ========================================
            TotalComputePoints = totalComputePoints,
            UsedComputePoints = actualUsedPoints,  // From actual VMs
            AvailableComputePoints = totalComputePoints - actualUsedPoints,

            // ========================================
            // MEMORY & STORAGE STATISTICS (SELF-HEALING)
            // ========================================
            TotalMemoryBytes = onlineNodes.Sum(n => n.TotalResources.MemoryBytes),
            UsedMemoryBytes = actualUsedMemory,  // From actual VMs
            AvailableMemoryBytes = (onlineNodes.Sum(n => n.TotalResources.MemoryBytes) - actualUsedMemory),

            TotalStorageBytes = onlineNodes.Sum(n => n.TotalResources.StorageBytes),
            UsedStorageBytes = actualUsedStorage,  // From actual VMs
            AvailableStorageBytes = (onlineNodes.Sum(n => n.TotalResources.StorageBytes) - actualUsedStorage),
        };

        // Calculate utilization percentages
        stats.ComputePointUtilizationPercent = stats.TotalComputePoints > 0
            ? (double)stats.UsedComputePoints / stats.TotalComputePoints * 100
            : 0;

        stats.MemoryUtilizationPercent = stats.TotalMemoryBytes > 0
            ? (double)stats.UsedMemoryBytes / stats.TotalMemoryBytes * 100
            : 0;

        stats.StorageUtilizationPercent = stats.TotalStorageBytes > 0
            ? (double)stats.UsedStorageBytes / stats.TotalStorageBytes * 100
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

    /// <summary>
    /// Register a command for tracking.
    /// Call this when issuing any command that needs acknowledgement.
    /// </summary>
    public void RegisterCommand(string commandId, string vmId, string nodeId, NodeCommandType commandType)
    {
        var registration = new CommandRegistration(
            commandId,
            vmId,
            nodeId,
            commandType,
            DateTime.UtcNow
        );

        CommandRegistry[commandId] = registration;

        _logger.LogDebug(
            "Registered command {CommandId} for VM {VmId} on node {NodeId} (type: {Type})",
            commandId, vmId, nodeId, commandType);
    }

    /// <summary>
    /// Try to get and remove a command registration.
    /// Returns true if found and removed.
    /// </summary>
    public bool TryCompleteCommand(string commandId, out CommandRegistration? registration)
    {
        var result = CommandRegistry.TryRemove(commandId, out registration);

        if (result)
        {
            _logger.LogDebug(
                "Completed command {CommandId} for VM {VmId}",
                commandId, registration!.VmId);
        }

        return result;
    }

    /// <summary>
    /// Get all stale commands (older than specified timeout).
    /// </summary>
    public List<CommandRegistration> GetStaleCommands(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;

        return CommandRegistry.Values
            .Where(r => r.IssuedAt < cutoff)
            .ToList();
    }

    /// <summary>
    /// Remove stale command registrations.
    /// Returns the count of removed registrations.
    /// </summary>
    public int CleanupStaleCommands(TimeSpan timeout)
    {
        var cutoff = DateTime.UtcNow - timeout;
        var staleIds = CommandRegistry
            .Where(kvp => kvp.Value.IssuedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in staleIds)
        {
            CommandRegistry.TryRemove(id, out _);
        }

        if (staleIds.Count > 0)
        {
            _logger.LogWarning(
                "Cleaned up {Count} stale command registrations (timeout: {Timeout})",
                staleIds.Count, timeout);
        }

        return staleIds.Count;
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