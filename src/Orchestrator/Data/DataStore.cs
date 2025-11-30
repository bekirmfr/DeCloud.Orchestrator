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
                "State loaded from MongoDB in {Elapsed}ms: {Nodes} nodes, {VMs} VMs, {Users} users",
                elapsed, nodes.Count, vms.Count, users.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load state from MongoDB");
            throw;
        }
    }

    /// <summary>
    /// Save or update a node (write-through to MongoDB)
    /// </summary>
    public async Task SaveNodeAsync(Node node)
    {
        Nodes[node.Id] = node;

        if (_useMongoDB)
        {
            try
            {
                await NodesCollection!.ReplaceOneAsync(
                    n => n.Id == node.Id,
                    node,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist node {NodeId} to MongoDB", node.Id);
                // Continue - in-memory update succeeded
            }
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
            try
            {
                await NodesCollection!.DeleteOneAsync(n => n.Id == nodeId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete node {NodeId} from MongoDB", nodeId);
            }
        }
    }

    /// <summary>
    /// Save or update a VM (write-through to MongoDB)
    /// </summary>
    public async Task SaveVmAsync(VirtualMachine vm)
    {
        vm.UpdatedAt = DateTime.UtcNow;
        VirtualMachines[vm.Id] = vm;

        if (_useMongoDB)
        {
            try
            {
                await VmsCollection!.ReplaceOneAsync(
                    v => v.Id == vm.Id,
                    vm,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist VM {VmId} to MongoDB", vm.Id);
                // Continue - in-memory update succeeded
            }
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
                try
                {
                    await VmsCollection!.ReplaceOneAsync(
                        v => v.Id == vmId,
                        vm,
                        new ReplaceOptions { IsUpsert = true });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to mark VM {VmId} as deleted in MongoDB", vmId);
                }
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
            try
            {
                await UsersCollection!.ReplaceOneAsync(
                    u => u.Id == user.Id,
                    user,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist user {UserId} to MongoDB", user.Id);
            }
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
            try
            {
                await EventsCollection!.InsertOneAsync(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist event {EventId} to MongoDB", evt.Id);
            }
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
        }
    }

    /// <summary>
    /// Create MongoDB indexes for performance
    /// </summary>
    private void CreateIndexes()
    {
        try
        {
            // Node indexes
            NodesCollection?.Indexes.CreateOne(
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.Status)));
            NodesCollection?.Indexes.CreateOne(
                new CreateIndexModel<Node>(
                    Builders<Node>.IndexKeys.Ascending(n => n.WalletAddress)));

            // VM indexes
            VmsCollection?.Indexes.CreateOne(
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.OwnerId)));
            VmsCollection?.Indexes.CreateOne(
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.NodeId)));
            VmsCollection?.Indexes.CreateOne(
                new CreateIndexModel<VirtualMachine>(
                    Builders<VirtualMachine>.IndexKeys.Ascending(v => v.Status)));

            // User indexes
            UsersCollection?.Indexes.CreateOne(
                new CreateIndexModel<User>(
                    Builders<User>.IndexKeys.Ascending(u => u.WalletAddress),
                    new CreateIndexOptions { Unique = true }));

            // Event indexes
            EventsCollection?.Indexes.CreateOne(
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Descending(e => e.Timestamp)));
            EventsCollection?.Indexes.CreateOne(
                new CreateIndexModel<OrchestratorEvent>(
                    Builders<OrchestratorEvent>.IndexKeys.Ascending(e => e.ResourceId)));

            _logger.LogDebug("MongoDB indexes created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create some MongoDB indexes");
        }
    }

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
                Description = "Debian stable release",
                OsFamily = "linux",
                OsName = "debian",
                Version = "12",
                SizeGb = 2,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "fedora-40",
                Name = "Fedora 40",
                Description = "Fedora Workstation",
                OsFamily = "linux",
                OsName = "fedora",
                Version = "40",
                SizeGb = 3,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "alpine-3.19",
                Name = "Alpine Linux 3.19",
                Description = "Lightweight Linux distribution",
                OsFamily = "linux",
                OsName = "alpine",
                Version = "3.19",
                SizeGb = 1,
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
                Id = "nano",
                Name = "Nano",
                CpuCores = 1,
                MemoryMb = 512,
                StorageGb = 10,
                HourlyPriceUsd = 0.005m,
                HourlyPriceCrypto = 0.005m
            },
            new VmPricingTier
            {
                Id = "small",
                Name = "Small",
                CpuCores = 1,
                MemoryMb = 1024,
                StorageGb = 20,
                HourlyPriceUsd = 0.01m,
                HourlyPriceCrypto = 0.01m
            },
            new VmPricingTier
            {
                Id = "medium",
                Name = "Medium",
                CpuCores = 2,
                MemoryMb = 4096,
                StorageGb = 40,
                HourlyPriceUsd = 0.04m,
                HourlyPriceCrypto = 0.04m
            },
            new VmPricingTier
            {
                Id = "large",
                Name = "Large",
                CpuCores = 4,
                MemoryMb = 8192,
                StorageGb = 80,
                HourlyPriceUsd = 0.08m,
                HourlyPriceCrypto = 0.08m
            },
            new VmPricingTier
            {
                Id = "xl",
                Name = "Extra Large",
                CpuCores = 8,
                MemoryMb = 16384,
                StorageGb = 160,
                HourlyPriceUsd = 0.16m,
                HourlyPriceCrypto = 0.16m
            },
            new VmPricingTier
            {
                Id = "2xl",
                Name = "2X Large",
                CpuCores = 16,
                MemoryMb = 32768,
                StorageGb = 320,
                HourlyPriceUsd = 0.32m,
                HourlyPriceCrypto = 0.32m
            }
        };

        foreach (var tier in tiers)
        {
            PricingTiers.TryAdd(tier.Id, tier);
        }
    }

    // =====================================================
    // Legacy methods for backward compatibility
    // =====================================================

    public void AddEvent(OrchestratorEvent evt)
    {
        _ = SaveEventAsync(evt); // Fire and forget
    }

    public void AddPendingCommand(string nodeId, NodeCommand command)
    {
        var queue = PendingNodeCommands.GetOrAdd(nodeId, _ => new ConcurrentQueue<NodeCommand>());
        queue.Enqueue(command);
    }

    public List<NodeCommand> GetAndClearPendingCommands(string nodeId)
    {
        if (!PendingNodeCommands.TryRemove(nodeId, out var queue))
            return new List<NodeCommand>();

        var commands = new List<NodeCommand>();
        while (queue.TryDequeue(out var cmd))
        {
            commands.Add(cmd);
        }

        return commands;
    }
}