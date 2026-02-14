using MongoDB.Bson;
using MongoDB.Driver;
using Orchestrator.Models;
using Orchestrator.Models.Growth;
using System.Collections.Concurrent;

namespace Orchestrator.Persistence;

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

    // ════════════════════════════════════════════════════════════════════════
    // Configuration Constants
    // ════════════════════════════════════════════════════════════════════════

    private static readonly TimeSpan NodeOnlineThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RecentUsageThreshold = TimeSpan.FromDays(30);

    // Thread-safe in-memory collections for fast access
    public ConcurrentDictionary<string, Node> ActiveNodes { get; } = new();
    public ConcurrentDictionary<string, VirtualMachine> ActiveVMs { get; } = new();
    // Pending commands for nodes (nodeId -> commands)
    /// <summary>
    /// Command registry for reliable command→VM tracking.
    /// Key: commandId, Value: CommandRegistration
    /// This provides a reliable way to look up which VM a command belongs to,
    /// independent of the VM's StatusMessage field.
    /// </summary>
    public ConcurrentDictionary<string, CommandRegistration> CommandRegistry { get; } = new();
    public ConcurrentDictionary<string, ConcurrentQueue<NodeCommand>> PendingCommands { get; } = new();
    public ConcurrentDictionary<string, NodeCommand> PendingCommandAcks { get; } = new();
    public ConcurrentDictionary<string, User> Users { get; } = new();
    public ConcurrentDictionary<string, UsageRecord> UnsettledUsage { get; } = new();
    public ConcurrentDictionary<string, VmImage> Images { get; } = new();
    public ConcurrentDictionary<string, VmPricingTier> PricingTiers { get; } = new();

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
    private IMongoCollection<Attestation>? AttestationsCollection =>
        _database?.GetCollection<Attestation>("attestations");
    private IMongoCollection<UsageRecord>? UsageRecordsCollection =>
        _database?.GetCollection<UsageRecord>("usageRecords");
    private IMongoCollection<VmTemplate>? TemplatesCollection =>
        _database?.GetCollection<VmTemplate>("vmTemplates");
    private IMongoCollection<TemplateCategory>? CategoriesCollection =>
        _database?.GetCollection<TemplateCategory>("templateCategories");
    private IMongoCollection<MarketplaceReview>? ReviewsCollection =>
        _database?.GetCollection<MarketplaceReview>("marketplaceReviews");

    // Growth Engine Collections
    private IMongoCollection<Referral>? ReferralsCollection =>
        _database?.GetCollection<Referral>("referrals");
    private IMongoCollection<CreditGrant>? CreditGrantsCollection =>
        _database?.GetCollection<CreditGrant>("creditGrants");
    private IMongoCollection<PromoCampaign>? CampaignsCollection =>
        _database?.GetCollection<PromoCampaign>("promoCampaigns");

    // In-memory referral code mappings (userId -> code, code -> userId)
    private readonly ConcurrentDictionary<string, string> _userToReferralCode = new();
    private readonly ConcurrentDictionary<string, string> _referralCodeToUser = new();

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
    /// Create MongoDB indexes for optimal query performance.
    /// Uses safe index creation that handles existing indexes properly.
    /// </summary>
    private void CreateIndexes()
    {
        if (!_useMongoDB) return;

        try
        {
            _logger.LogInformation("Creating MongoDB indexes...");

            // Nodes indexes
            var nodeIndexes = new[]
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
        };
            TryCreateIndexesAsync(NodesCollection!, "nodes", nodeIndexes).Wait();

            // VMs indexes
            var vmIndexes = new[]
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
        };
            TryCreateIndexesAsync(VmsCollection!, "vms", vmIndexes).Wait();

            // Users indexes
            var userIndexes = new[]
            {
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.WalletAddress),
                new CreateIndexOptions { Name = "idx_wallet", Unique = true }),
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.Email),
                new CreateIndexOptions
                {
                    Name = "idx_email",
                    Sparse = true // Allow null emails
                }),
            new CreateIndexModel<User>(
                Builders<User>.IndexKeys.Ascending(u => u.Status),
                new CreateIndexOptions { Name = "idx_status" })
        };
            TryCreateIndexesAsync(UsersCollection!, "users", userIndexes).Wait();

            // Events indexes
            var eventIndexes = new[]
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
        };
            TryCreateIndexesAsync(EventsCollection!, "events", eventIndexes).Wait();

            // Attestations indexes
            var attestationIndexes = new[]
            {
            new CreateIndexModel<Attestation>(
                Builders<Attestation>.IndexKeys
                    .Ascending(r => r.VmId)
                    .Descending(r => r.Timestamp),
                new CreateIndexOptions { Name = "idx_vm_timestamp" }),
            new CreateIndexModel<Attestation>(
                Builders<Attestation>.IndexKeys.Ascending(r => r.NodeId),
                new CreateIndexOptions { Name = "idx_node" }),
            new CreateIndexModel<Attestation>(
                Builders<Attestation>.IndexKeys.Ascending(r => r.Success),
                new CreateIndexOptions { Name = "idx_success" })
        };
            TryCreateIndexesAsync(AttestationsCollection!, "attestations", attestationIndexes).Wait();

            // UsageRecords indexes
            var usageIndexes = new[]
            {
            new CreateIndexModel<UsageRecord>(
                Builders<UsageRecord>.IndexKeys
                    .Ascending(r => r.UserId)
                    .Descending(r => r.CreatedAt),
                new CreateIndexOptions { Name = "idx_user_timestamp" }),
            new CreateIndexModel<UsageRecord>(
                Builders<UsageRecord>.IndexKeys.Ascending(r => r.VmId),
                new CreateIndexOptions { Name = "idx_vm" })
        };
            TryCreateIndexesAsync(UsageRecordsCollection!, "usageRecords", usageIndexes).Wait();

            // Template indexes
            var templateIndexes = new[]
            {
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.Slug),
                new CreateIndexOptions { Name = "idx_slug", Unique = true }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.Category),
                new CreateIndexOptions { Name = "idx_category" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.Status),
                new CreateIndexOptions { Name = "idx_status" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Descending(t => t.DeploymentCount),
                new CreateIndexOptions { Name = "idx_deployment_count" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.IsFeatured),
                new CreateIndexOptions { Name = "idx_featured" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Descending(t => t.CreatedAt),
                new CreateIndexOptions { Name = "idx_created" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.AuthorId),
                new CreateIndexOptions { Name = "idx_author" }),
            new CreateIndexModel<VmTemplate>(
                Builders<VmTemplate>.IndexKeys.Ascending(t => t.Visibility),
                new CreateIndexOptions { Name = "idx_visibility" })
        };
            TryCreateIndexesAsync(TemplatesCollection!, "vmTemplates", templateIndexes).Wait();

            // Category indexes
            var categoryIndexes = new[]
            {
            new CreateIndexModel<TemplateCategory>(
                Builders<TemplateCategory>.IndexKeys.Ascending(c => c.Slug),
                new CreateIndexOptions { Name = "idx_slug", Unique = true }),
            new CreateIndexModel<TemplateCategory>(
                Builders<TemplateCategory>.IndexKeys.Ascending(c => c.DisplayOrder),
                new CreateIndexOptions { Name = "idx_display_order" })
        };
            TryCreateIndexesAsync(CategoriesCollection!, "templateCategories", categoryIndexes).Wait();

            // MarketplaceReview indexes
            var reviewIndexes = new[]
            {
            new CreateIndexModel<MarketplaceReview>(
                Builders<MarketplaceReview>.IndexKeys
                    .Ascending(r => r.ResourceType)
                    .Ascending(r => r.ResourceId),
                new CreateIndexOptions { Name = "idx_resource" }),
            new CreateIndexModel<MarketplaceReview>(
                Builders<MarketplaceReview>.IndexKeys.Ascending(r => r.ReviewerId),
                new CreateIndexOptions { Name = "idx_reviewer" }),
            new CreateIndexModel<MarketplaceReview>(
                Builders<MarketplaceReview>.IndexKeys.Descending(r => r.CreatedAt),
                new CreateIndexOptions { Name = "idx_created" }),
            new CreateIndexModel<MarketplaceReview>(
                Builders<MarketplaceReview>.IndexKeys
                    .Ascending(r => r.ResourceType)
                    .Ascending(r => r.ResourceId)
                    .Ascending(r => r.ReviewerId),
                new CreateIndexOptions { Name = "idx_unique_review", Unique = true })
        };
            TryCreateIndexesAsync(ReviewsCollection!, "marketplaceReviews", reviewIndexes).Wait();

            _logger.LogInformation("✓ MongoDB indexes created successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create MongoDB indexes");
            // Don't throw - indexes are optimization, not critical
            // The application can still function without indexes, just slower
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
            // Load ONLY online nodes (heartbeat within last 5 minutes)
            var onlineNodesCutoff = DateTime.UtcNow - NodeOnlineThreshold;
            var nodes = await NodesCollection!
                .Find(n => n.LastHeartbeat > onlineNodesCutoff)
                .ToListAsync();

            foreach (var node in nodes)
            {
                ActiveNodes.TryAdd(node.Id, node);
            }

            // Load VMs (exclude deleted)
            var vms = await VmsCollection!
                .Find(vm => vm.Status != VmStatus.Deleted)
                .ToListAsync();
            foreach (var vm in vms)
            {
                ActiveVMs.TryAdd(vm.Id, vm);
            }

            var recentUsageCutoff = DateTime.UtcNow - RecentUsageThreshold;
            var usageRecords = await UsageRecordsCollection!
                .Find(u => !u.SettledOnChain && 
                            u.CreatedAt > recentUsageCutoff)
                .ToListAsync();

            foreach (var record in usageRecords)
            {
                UnsettledUsage.TryAdd(record.Id, record);
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
                "✓ Active state loaded in {Elapsed}ms: {Nodes} online nodes, {VMs} active VMs, " +
                "{Users} users, {Usage} recent usage, {Images} images, {Tiers} pricing tiers",
                elapsed, nodes.Count, vms.Count, users.Count, usageRecords.Count,
                images.Count, tiers.Count);

            // Log memory estimate
            var estimatedMemoryMB = (nodes.Count * 5 + vms.Count * 10 +
                                    usageRecords.Count * 2 + users.Count * 2) / 1024;
            _logger.LogInformation(
                "📊 Estimated memory usage: ~{Memory}MB (hot data only)",
                estimatedMemoryMB);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load state from MongoDB");
            throw;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE METHODS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get node by ID - checks hot cache first, then queries MongoDB
    /// </summary>
    public async Task<Node?> GetNodeAsync(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId))
            return null;

        // Try hot cache first (fast path)
        if (ActiveNodes.TryGetValue(nodeId, out var cachedNode))
        {
            return cachedNode;
        }

        // Not in cache - query MongoDB (cold path)
        if (!_useMongoDB) return null;

        var node = await NodesCollection!
            .Find(n => n.Id == nodeId)
            .FirstOrDefaultAsync();

        return node;
    }

    /// <summary>
    /// Get all online nodes - returns in-memory collection (fast)
    /// </summary>
    public IEnumerable<Node> GetActiveNodes()
    {
        return ActiveNodes.Values;
    }

    /// <summary>
    /// Get all nodes (including offline) - queries MongoDB
    /// </summary>
    public async Task<List<Node>> GetAllNodesAsync(NodeStatus? statusFilter = null)
    {
        if (!_useMongoDB) return ActiveNodes.Values.ToList();

        var filter = statusFilter.HasValue
            ? Builders<Node>.Filter.Eq(n => n.Status, statusFilter.Value)
            : Builders<Node>.Filter.Empty;

        return await NodesCollection!
            .Find(filter)
            .ToListAsync();
    }

    public async Task<List<Node>?> GetNodesByUser(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return null;

        if (!Users.TryGetValue(userId, out var user))
        {
            user = UsersCollection.Find(u => u.Id == userId).FirstOrDefault();
        }

        if (user == null) return null;

        return ActiveNodes.Values
            .Where(vm => vm.WalletAddress == user?.WalletAddress)
            .ToList();
    }

    /// <summary>
    /// Save or update a node (write-through to MongoDB with retry)
    /// </summary>
    /// <summary>
    /// Save node - writes to MongoDB and updates hot cache if online
    /// </summary>
    public async Task SaveNodeAsync(Node node)
    {
        // Determine if node is "hot" (online)
        var isOnline = (DateTime.UtcNow - node.LastHeartbeat) < NodeOnlineThreshold;

        if (isOnline)
        {
            // Hot node - keep in memory
            ActiveNodes[node.Id] = node;
        }
        else
        {
            // Cold node - remove from memory if present
            ActiveNodes.TryRemove(node.Id, out _);
        }

        // Always persist to MongoDB
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
        if (!ActiveNodes.TryRemove(nodeId, out _))
            throw new Exception($"Node {nodeId} not found in active nodes");

        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await NodesCollection!.DeleteOneAsync(n => n.Id == nodeId);
            }, $"delete node {nodeId}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // VM OPERATIONS - Hot/Cold Separation
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get VM by ID - checks hot cache first, then queries MongoDB
    /// </summary>
    public async Task<VirtualMachine?> GetVmAsync(string vmId)
    {
        // Try hot cache first (active VMs)
        if (ActiveVMs.TryGetValue(vmId, out var cachedVm))
        {
            return cachedVm;
        }

        // Not in cache - query MongoDB (stopped/deleted VMs)
        if (!_useMongoDB) return null;

        var vm = await VmsCollection!
            .Find(v => v.Id == vmId)
            .FirstOrDefaultAsync();

        return vm;
    }

    public async Task<List<VirtualMachine>> GetAllVMsAsync()
    {
        if (!_useMongoDB) return ActiveVMs.Values.ToList();

        return await VmsCollection!
            .Find(_ => true)
            .ToListAsync();
    }

    /// <summary>
    /// Get all active VMs - returns in-memory collection (fast)
    /// </summary>
    public IEnumerable<VirtualMachine> GetActiveVMs()
    {
        return ActiveVMs.Values;
    }

    /// <summary>
    /// Get VMs by owner - queries in-memory collection (fast)
    /// </summary>
    public async Task<List<VirtualMachine>> GetVmsByUserAsync(string ownerId)
    {
        return ActiveVMs.Values
            .Where(vm => vm.OwnerId == ownerId)
            .ToList();
    }

    /// <summary>
    /// Get VMs by owner - queries MongoDB (may include stopped VMs)
    /// </summary>
    public async Task<List<VirtualMachine>> GetVmsByUserAsync(string ownerId, VmStatus statusFilter)
    {
        return await VmsCollection!
            .Find(vm => vm.OwnerId == ownerId && vm.Status == statusFilter)
            .ToListAsync();
    }

    /// <summary>
    /// Get all VMs by node, excluding deleted.
    /// </summary>

    public async Task<List<VirtualMachine>> GetVmsByNodeAsync(string nodeId)
    {
        return await VmsCollection!
            .Find(vm => vm.NodeId == nodeId && vm.Status != VmStatus.Deleted)
            .ToListAsync();
    }

    /// <summary>
    /// Save VM - writes to MongoDB and updates hot cache if active
    /// </summary>
    public async Task SaveVmAsync(VirtualMachine vm)
    {
        // Determine if VM is "hot" (active)
        var isActive = vm.Status == VmStatus.Running ||
                      vm.Status == VmStatus.Scheduling ||
                      vm.Status == VmStatus.Provisioning ||
                      vm.Status == VmStatus.Stopping;

        if (isActive)
        {
            // Hot VM - keep in memory
            ActiveVMs[vm.Id] = vm;
        }
        else
        {
            // Cold VM (Stopped/Deleted) - remove from memory
            ActiveVMs.TryRemove(vm.Id, out _);
        }

        // Always persist to MongoDB
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

    // ═══════════════════════════════════════════════════════════════════
    // ATTESTATION METHODS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Save an attestation record for audit trail
    /// </summary>
    public async Task SaveAttestationAsync(Attestation record)
    {
        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await AttestationsCollection!.InsertOneAsync(record);
            }, $"persist attestation record {record.Id}", maxRetries: 2);
        }
    }

    /// <summary>
    /// Get attestation records for a VM
    /// </summary>
    public async Task<List<Attestation>> GetAttestationsAsync(
        string vmId,
        int limit = 100,
        DateTime? since = null)
    {
        if (!_useMongoDB || AttestationsCollection == null)
        {
            return new List<Attestation>();
        }

        var filterBuilder = Builders<Attestation>.Filter;
        var filter = filterBuilder.Eq(r => r.VmId, vmId);

        if (since.HasValue)
        {
            filter = filterBuilder.And(filter, filterBuilder.Gte(r => r.Timestamp, since.Value));
        }

        return await AttestationsCollection
            .Find(filter)
            .SortByDescending(r => r.Timestamp)
            .Limit(limit)
            .ToListAsync();
    }

    // ════════════════════════════════════════════════════════════════════════
    // USAGE RECORDS - Database-First Queries
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get unpaid usage for user - queries MongoDB directly (scalable)
    /// Uses MongoDB aggregation for optimal performance
    /// </summary>
    public async Task<decimal> GetUnpaidUsageAsync(string userId)
    {
        if (!_useMongoDB)
        {
            // Fallback to in-memory for development
            return UnsettledUsage.Values
                .Where(u => u.UserId == userId && !u.SettledOnChain)
                .Sum(u => u.TotalCost);
        }

        // MongoDB aggregation (scales to millions of records)
        var filter = Builders<UsageRecord>.Filter;
        var query = filter.And(
            filter.Eq(u => u.UserId, userId),
            filter.Eq(u => u.SettledOnChain, false)
        );

        var result = await UsageRecordsCollection!
            .Aggregate()
            .Match(query)
            .Group(new BsonDocument {
                { "_id", BsonNull.Value },
                { "total", new BsonDocument("$sum", "$TotalCost") }
            })
            .FirstOrDefaultAsync();

        return result?["total"].AsDecimal ?? 0;
    }

    /// <summary>
    /// Get usage history for user - queries MongoDB with pagination
    /// </summary>
    public async Task<List<UsageRecord>> GetUsageHistoryAsync(
        string userId,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int skip = 0,
        int take = 100)
    {
        if (!_useMongoDB)
        {
            var query = UnsettledUsage.Values.Where(u => u.UserId == userId);
            if (fromDate.HasValue) query = query.Where(u => u.CreatedAt >= fromDate.Value);
            if (toDate.HasValue) query = query.Where(u => u.CreatedAt <= toDate.Value);
            return query.OrderByDescending(u => u.CreatedAt).Skip(skip).Take(take).ToList();
        }

        // MongoDB query with proper indexes
        var filterBuilder = Builders<UsageRecord>.Filter;
        var filters = new List<FilterDefinition<UsageRecord>>
        {
            filterBuilder.Eq(u => u.UserId, userId)
        };

        if (fromDate.HasValue)
            filters.Add(filterBuilder.Gte(u => u.CreatedAt, fromDate.Value));
        if (toDate.HasValue)
            filters.Add(filterBuilder.Lte(u => u.CreatedAt, toDate.Value));

        var combinedFilter = filterBuilder.And(filters);

        return await UsageRecordsCollection!
            .Find(combinedFilter)
            .SortByDescending(u => u.CreatedAt)
            .Skip(skip)
            .Limit(take)
            .ToListAsync();
    }

    /// <summary>
    /// Save usage record - always to MongoDB, optionally cache if recent
    /// </summary>
    public async Task SaveUsageRecordAsync(UsageRecord record)
    {
        // Cache recent unsettled usage in memory
        if (!record.SettledOnChain &&
            (DateTime.UtcNow - record.CreatedAt) < RecentUsageThreshold)
        {
            UnsettledUsage[record.Id] = record;
        }
        else
        {
            UnsettledUsage.TryRemove(record.Id, out _);
        }

        // Always persist to MongoDB
        if (_useMongoDB)
        {
            await RetryMongoOperationAsync(async () =>
            {
                await UsageRecordsCollection!.ReplaceOneAsync(
                    u => u.Id == record.Id,
                    record,
                    new ReplaceOptions { IsUpsert = true });
            }, $"persist usage record {record.Id}");
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
                var nodeUpdates = ActiveNodes.Values.Select(node =>
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
                var vmUpdates = ActiveVMs.Values.Select(vm =>
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
                    syncResults.NodesSuccess, ActiveNodes.Count,
                    syncResults.VmsSuccess, ActiveVMs.Count,
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
    /// Get system statistics
    /// </summary>

    public async Task<SystemStats> GetSystemStatsAsync()
    {
        var nodes = ActiveNodes.Values.ToList();
        var onlineNodes = nodes.Where(n => n.Status == NodeStatus.Online).ToList();
        var vms = GetActiveVMs().ToList();

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
        for (int i = 0; i < maxRetries; i++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex)
            {
                if (i == maxRetries - 1)
                {
                    _logger.LogError(ex, "Failed to {Operation} after {Retries} retries",
                        operationName, maxRetries);
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(100 * Math.Pow(2, i));
                _logger.LogWarning(ex, "Failed to {Operation}, retrying in {Delay}ms...",
                    operationName, delay.TotalMilliseconds);
                await Task.Delay(delay);
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
        if (!PendingCommands.TryGetValue(nodeId, out var queue))
        {
            queue = new ConcurrentQueue<NodeCommand>();
            PendingCommands[nodeId] = queue;
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
    /// Check if node has pending commands without removing them
    /// Used by hybrid push-pull to determine if queue is empty
    /// </summary>
    public bool HasPendingCommands(string nodeId)
    {
        return PendingCommands.TryGetValue(nodeId, out var queue) && !queue.IsEmpty;
    }

    public List<NodeCommand> GetAndClearPendingCommands(string nodeId)
    {
        if (!PendingCommands.TryRemove(nodeId, out var queue))
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
            },
            // System VM images — role-specific config injected via cloud-init at deploy time.
            // DHT uses Debian 12 (~2 GiB) instead of Ubuntu 24.04 (~3.5 GiB) to avoid
            // overlay-smaller-than-backing boot failures and reduce download/storage overhead.
            new VmImage
            {
                Id = "debian-12-dht",
                Name = "Debian 12 (DHT Node)",
                Description = "Base image for DHT system VMs — libp2p/Kademlia node deployed via cloud-init",
                OsFamily = "linux",
                OsName = "debian",
                Version = "12",
                SizeGb = 2,
                IsPublic = false,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "debian-12-relay",
                Name = "Debian 12 (Relay Node)",
                Description = "Base image for Relay system VMs — WireGuard relay deployed via cloud-init",
                OsFamily = "linux",
                OsName = "debian",
                Version = "12",
                SizeGb = 2,
                IsPublic = false,
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

    /// <summary>
    /// Safely creates indexes for a collection.
    /// If an index exists with different properties, it will be dropped and recreated.
    /// This prevents startup errors from index conflicts.
    /// </summary>
    private async Task<bool> TryCreateIndexesAsync<T>(
        IMongoCollection<T> collection,
        string collectionName,
        IEnumerable<CreateIndexModel<T>> indexModels)
    {
        try
        {
            // Get existing indexes
            var existingIndexes = new Dictionary<string, BsonDocument>();
            using (var cursor = await collection.Indexes.ListAsync())
            {
                var indexes = await cursor.ToListAsync();
                foreach (var index in indexes)
                {
                    var indexName = index["name"].AsString;
                    if (indexName != "_id_") // Skip default _id index
                    {
                        existingIndexes[indexName] = index;
                    }
                }
            }

            foreach (var indexModel in indexModels)
            {
                var indexName = indexModel.Options?.Name;
                if (string.IsNullOrEmpty(indexName))
                {
                    // MongoDB will auto-generate name, just create it
                    _logger.LogWarning("Index model for {Collection} has no explicit name, using auto-generated name",
                        collectionName);
                    await collection.Indexes.CreateOneAsync(indexModel);
                    continue;
                }

                // Check if index exists
                if (existingIndexes.TryGetValue(indexName, out var existingIndex))
                {
                    // Compare index properties
                    var needsRecreation = false;

                    // Check if unique constraint matches
                    var requestedUnique = indexModel.Options?.Unique ?? false;
                    var existingUnique = existingIndex.Contains("unique") && existingIndex["unique"].AsBoolean;

                    if (requestedUnique != existingUnique)
                    {
                        _logger.LogInformation(
                            "Index '{IndexName}' on {Collection} has different unique constraint (existing: {Existing}, requested: {Requested})",
                            indexName, collectionName, existingUnique, requestedUnique);
                        needsRecreation = true;
                    }

                    // Check if sparse constraint matches
                    var requestedSparse = indexModel.Options?.Sparse ?? false;
                    var existingSparse = existingIndex.Contains("sparse") && existingIndex["sparse"].AsBoolean;

                    if (requestedSparse != existingSparse)
                    {
                        _logger.LogInformation(
                            "Index '{IndexName}' on {Collection} has different sparse constraint (existing: {Existing}, requested: {Requested})",
                            indexName, collectionName, existingSparse, requestedSparse);
                        needsRecreation = true;
                    }

                    // If properties differ, drop and recreate
                    if (needsRecreation)
                    {
                        _logger.LogInformation("Dropping index '{IndexName}' on {Collection} to recreate with new properties",
                            indexName, collectionName);
                        await collection.Indexes.DropOneAsync(indexName);
                        await collection.Indexes.CreateOneAsync(indexModel);
                        _logger.LogInformation("✓ Recreated index '{IndexName}' on {Collection}",
                            indexName, collectionName);
                    }
                    else
                    {
                        _logger.LogDebug("Index '{IndexName}' on {Collection} already exists with correct properties",
                            indexName, collectionName);
                    }
                }
                else
                {
                    // Index doesn't exist, create it
                    await collection.Indexes.CreateOneAsync(indexModel);
                    _logger.LogInformation("✓ Created new index '{IndexName}' on {Collection}",
                        indexName, collectionName);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create indexes for collection {Collection}", collectionName);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Operations (Marketplace)
    // ════════════════════════════════════════════════════════════════════════

    public async Task<VmTemplate?> GetTemplateByIdAsync(string templateId)
    {
        if (!_useMongoDB) return null;
        
        try
        {
            return await TemplatesCollection!
                .Find(t => t.Id == templateId)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template by ID: {TemplateId}", templateId);
            return null;
        }
    }

    public async Task<VmTemplate?> GetTemplateBySlugAsync(string slug)
    {
        if (!_useMongoDB) return null;
        
        try
        {
            return await TemplatesCollection!
                .Find(t => t.Slug == slug && t.Status == TemplateStatus.Published)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get template by slug: {Slug}", slug);
            return null;
        }
    }

    public async Task<List<VmTemplate>> GetTemplatesAsync(
        string? category = null,
        bool? requiresGpu = null,
        List<string>? tags = null,
        bool featuredOnly = false,
        string sortBy = "popular")
    {
        if (!_useMongoDB) return new List<VmTemplate>();

        try
        {
            // Build filter - marketplace queries only show Public + Published templates
            var filterBuilder = Builders<VmTemplate>.Filter;
            var filters = new List<FilterDefinition<VmTemplate>>
            {
                filterBuilder.Eq(t => t.Status, TemplateStatus.Published),
                filterBuilder.Eq(t => t.Visibility, TemplateVisibility.Public)
            };

            if (!string.IsNullOrEmpty(category))
                filters.Add(filterBuilder.Eq(t => t.Category, category));
            
            if (requiresGpu.HasValue)
                filters.Add(filterBuilder.Eq(t => t.RequiresGpu, requiresGpu.Value));
            
            if (featuredOnly)
                filters.Add(filterBuilder.Eq(t => t.IsFeatured, true));
            
            if (tags != null && tags.Count > 0)
                filters.Add(filterBuilder.AnyIn(t => t.Tags, tags));

            var filter = filterBuilder.And(filters);

            // Build sort
            SortDefinition<VmTemplate> sort = sortBy.ToLower() switch
            {
                "popular" => Builders<VmTemplate>.Sort.Descending(t => t.DeploymentCount),
                "newest" => Builders<VmTemplate>.Sort.Descending(t => t.CreatedAt),
                "name" => Builders<VmTemplate>.Sort.Ascending(t => t.Name),
                _ => Builders<VmTemplate>.Sort.Descending(t => t.DeploymentCount)
            };

            return await TemplatesCollection!
                .Find(filter)
                .Sort(sort)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get templates");
            return new List<VmTemplate>();
        }
    }

    public async Task<VmTemplate> SaveTemplateAsync(VmTemplate template)
    {
        if (!_useMongoDB)
        {
            _logger.LogWarning("Cannot save template - MongoDB not configured");
            return template;
        }
        
        try
        {
            template.UpdatedAt = DateTime.UtcNow;
            
            await TemplatesCollection!.ReplaceOneAsync(
                t => t.Id == template.Id,
                template,
                new ReplaceOptions { IsUpsert = true });
            
            _logger.LogInformation("Saved template: {TemplateName} ({TemplateId})", 
                template.Name, template.Id);
            
            return template;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save template: {TemplateName}", template.Name);
            throw;
        }
    }

    public async Task<bool> IncrementTemplateDeploymentCountAsync(string templateId)
    {
        if (!_useMongoDB) return false;
        
        try
        {
            var update = Builders<VmTemplate>.Update
                .Inc(t => t.DeploymentCount, 1)
                .Set(t => t.LastDeployedAt, DateTime.UtcNow)
                .Set(t => t.UpdatedAt, DateTime.UtcNow);

            var result = await TemplatesCollection!.UpdateOneAsync(
                t => t.Id == templateId,
                update);

            return result.ModifiedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment deployment count for template: {TemplateId}", 
                templateId);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Category Operations
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<TemplateCategory>> GetCategoriesAsync()
    {
        if (!_useMongoDB) return new List<TemplateCategory>();
        
        try
        {
            return await CategoriesCollection!
                .Find(_ => true)
                .SortBy(c => c.DisplayOrder)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get categories");
            return new List<TemplateCategory>();
        }
    }

    public async Task<TemplateCategory> SaveCategoryAsync(TemplateCategory category)
    {
        if (!_useMongoDB)
        {
            _logger.LogWarning("Cannot save category - MongoDB not configured");
            return category;
        }
        
        try
        {
            category.UpdatedAt = DateTime.UtcNow;
            
            await CategoriesCollection!.ReplaceOneAsync(
                c => c.Id == category.Id,
                category,
                new ReplaceOptions { IsUpsert = true });
            
            _logger.LogInformation("Saved category: {CategoryName} ({CategoryId})", 
                category.Name, category.Id);
            
            return category;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save category: {CategoryName}", category.Name);
            throw;
        }
    }

    public async Task UpdateCategoryCountsAsync()
    {
        if (!_useMongoDB) return;

        try
        {
            var categories = await GetCategoriesAsync();

            foreach (var category in categories)
            {
                var count = await TemplatesCollection!
                    .CountDocumentsAsync(t =>
                        t.Category == category.Slug &&
                        t.Status == TemplateStatus.Published &&
                        t.Visibility == TemplateVisibility.Public);

                category.TemplateCount = (int)count;
                category.UpdatedAt = DateTime.UtcNow;

                await CategoriesCollection!.ReplaceOneAsync(
                    c => c.Id == category.Id,
                    category);
            }

            _logger.LogInformation("Updated template counts for {Count} categories",
                categories.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update category counts");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Template Author Queries
    // ════════════════════════════════════════════════════════════════════════

    public async Task<List<VmTemplate>> GetTemplatesByAuthorAsync(string authorId)
    {
        if (!_useMongoDB) return new List<VmTemplate>();

        try
        {
            return await TemplatesCollection!
                .Find(t => t.AuthorId == authorId)
                .SortByDescending(t => t.UpdatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get templates for author: {AuthorId}", authorId);
            return new List<VmTemplate>();
        }
    }

    public async Task<bool> DeleteTemplateAsync(string templateId)
    {
        if (!_useMongoDB) return false;

        try
        {
            var result = await TemplatesCollection!.DeleteOneAsync(t => t.Id == templateId);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete template: {TemplateId}", templateId);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Marketplace Review Operations
    // ════════════════════════════════════════════════════════════════════════

    public async Task<MarketplaceReview> SaveReviewAsync(MarketplaceReview review)
    {
        if (!_useMongoDB)
        {
            _logger.LogWarning("Cannot save review - MongoDB not configured");
            return review;
        }

        try
        {
            review.UpdatedAt = DateTime.UtcNow;

            await ReviewsCollection!.ReplaceOneAsync(
                r => r.Id == review.Id,
                review,
                new ReplaceOptions { IsUpsert = true });

            _logger.LogInformation("Saved review {ReviewId} for {ResourceType}/{ResourceId}",
                review.Id, review.ResourceType, review.ResourceId);

            return review;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review: {ReviewId}", review.Id);
            throw;
        }
    }

    public async Task<List<MarketplaceReview>> GetReviewsAsync(
        string resourceType,
        string resourceId,
        int limit = 50,
        int skip = 0)
    {
        if (!_useMongoDB) return new List<MarketplaceReview>();

        try
        {
            return await ReviewsCollection!
                .Find(r => r.ResourceType == resourceType
                        && r.ResourceId == resourceId
                        && r.Status == ReviewStatus.Active)
                .SortByDescending(r => r.CreatedAt)
                .Skip(skip)
                .Limit(limit)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get reviews for {ResourceType}/{ResourceId}",
                resourceType, resourceId);
            return new List<MarketplaceReview>();
        }
    }

    public async Task<MarketplaceReview?> GetReviewByReviewerAsync(
        string resourceType,
        string resourceId,
        string reviewerId)
    {
        if (!_useMongoDB) return null;

        try
        {
            return await ReviewsCollection!
                .Find(r => r.ResourceType == resourceType
                        && r.ResourceId == resourceId
                        && r.ReviewerId == reviewerId
                        && r.Status == ReviewStatus.Active)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get review by reviewer {ReviewerId} for {ResourceType}/{ResourceId}",
                reviewerId, resourceType, resourceId);
            return null;
        }
    }

    public async Task<(double averageRating, int totalReviews, int[] distribution)> GetRatingAggregateAsync(
        string resourceType,
        string resourceId)
    {
        if (!_useMongoDB) return (0, 0, new int[5]);

        try
        {
            var reviews = await ReviewsCollection!
                .Find(r => r.ResourceType == resourceType
                        && r.ResourceId == resourceId
                        && r.Status == ReviewStatus.Active)
                .ToListAsync();

            if (reviews.Count == 0)
                return (0, 0, new int[5]);

            var distribution = new int[5];
            foreach (var review in reviews)
            {
                if (review.Rating >= 1 && review.Rating <= 5)
                    distribution[review.Rating - 1]++;
            }

            var average = reviews.Average(r => r.Rating);
            return (Math.Round(average, 2), reviews.Count, distribution);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get rating aggregate for {ResourceType}/{ResourceId}",
                resourceType, resourceId);
            return (0, 0, new int[5]);
        }
    }

    public async Task<bool> DeleteReviewAsync(string reviewId)
    {
        if (!_useMongoDB) return false;

        try
        {
            var result = await ReviewsCollection!.DeleteOneAsync(r => r.Id == reviewId);
            return result.DeletedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete review: {ReviewId}", reviewId);
            return false;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Growth Engine — Referrals, Credits, Promotions
    // ════════════════════════════════════════════════════════════════════════

    public Task<string?> GetReferralCodeForUserAsync(string userId)
    {
        _userToReferralCode.TryGetValue(userId, out var code);
        return Task.FromResult(code);
    }

    public Task<string?> GetUserByReferralCodeAsync(string code)
    {
        _referralCodeToUser.TryGetValue(code.ToUpper(), out var userId);
        return Task.FromResult(userId);
    }

    public Task SaveReferralCodeAsync(string userId, string code)
    {
        var upperCode = code.ToUpper();
        _userToReferralCode[userId] = upperCode;
        _referralCodeToUser[upperCode] = userId;
        return Task.CompletedTask;
    }

    public async Task SaveReferralAsync(Referral referral)
    {
        if (_useMongoDB)
        {
            try
            {
                await ReferralsCollection!.ReplaceOneAsync(
                    r => r.Id == referral.Id,
                    referral,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save referral: {ReferralId}", referral.Id);
            }
        }
    }

    public async Task<Referral?> GetReferralByReferredUserAsync(string userId)
    {
        if (!_useMongoDB) return null;

        try
        {
            return await ReferralsCollection!
                .Find(r => r.ReferredUserId == userId)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get referral for referred user: {UserId}", userId);
            return null;
        }
    }

    public async Task<List<Referral>> GetReferralsByReferrerAsync(string userId)
    {
        if (!_useMongoDB) return new List<Referral>();

        try
        {
            return await ReferralsCollection!
                .Find(r => r.ReferrerUserId == userId)
                .SortByDescending(r => r.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get referrals by referrer: {UserId}", userId);
            return new List<Referral>();
        }
    }

    public async Task SaveCreditGrantAsync(CreditGrant grant)
    {
        if (_useMongoDB)
        {
            try
            {
                await CreditGrantsCollection!.ReplaceOneAsync(
                    g => g.Id == grant.Id,
                    grant,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save credit grant: {GrantId}", grant.Id);
            }
        }
    }

    public async Task<List<CreditGrant>> GetCreditGrantsForUserAsync(string userId)
    {
        if (!_useMongoDB) return new List<CreditGrant>();

        try
        {
            return await CreditGrantsCollection!
                .Find(g => g.UserId == userId)
                .SortByDescending(g => g.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get credit grants for user: {UserId}", userId);
            return new List<CreditGrant>();
        }
    }

    public async Task SaveCampaignAsync(PromoCampaign campaign)
    {
        if (_useMongoDB)
        {
            try
            {
                await CampaignsCollection!.ReplaceOneAsync(
                    c => c.Id == campaign.Id,
                    campaign,
                    new ReplaceOptions { IsUpsert = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save campaign: {CampaignId}", campaign.Id);
            }
        }
    }

    public async Task<PromoCampaign?> GetCampaignByCodeAsync(string promoCode)
    {
        if (!_useMongoDB) return null;

        try
        {
            return await CampaignsCollection!
                .Find(c => c.PromoCode == promoCode && c.IsActive)
                .FirstOrDefaultAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get campaign by code: {Code}", promoCode);
            return null;
        }
    }

    public async Task<List<PromoCampaign>> GetActiveCampaignsAsync()
    {
        if (!_useMongoDB) return new List<PromoCampaign>();

        try
        {
            return await CampaignsCollection!
                .Find(c => c.IsActive)
                .SortByDescending(c => c.CreatedAt)
                .ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get active campaigns");
            return new List<PromoCampaign>();
        }
    }
}