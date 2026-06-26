using MongoDB.Driver;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Singleton gate-storage layer for the enforcement core. Owns "blocked_wallets" and
/// "enforcement_actions"; reads suspension from the in-memory DataStore.Users. No
/// IVmService/IUserService dependency, so it is safe to inject into the singleton
/// VmService. Factory-registered so the nullable IMongoDatabase passes through.
/// </summary>
public sealed class WalletBlocklistService : IWalletBlocklistService
{
    private readonly IMongoCollection<BlockedWallet>? _blocks;
    private readonly IMongoCollection<EnforcementAction>? _actions;
    private readonly DataStore _dataStore;
    private readonly ILogger<WalletBlocklistService> _logger;
    private readonly AddressUtil _addr = new();

    public WalletBlocklistService(
        IMongoDatabase? database, DataStore dataStore, ILogger<WalletBlocklistService> logger)
    {
        _blocks = database?.GetCollection<BlockedWallet>("blocked_wallets");
        _actions = database?.GetCollection<EnforcementAction>("enforcement_actions");
        _dataStore = dataStore;
        _logger = logger;

        // Best-effort index — denylist membership is queried by WalletAddress and an
        // imported sanctions list can be large.
        try
        {
            _blocks?.Indexes.CreateOne(new CreateIndexModel<BlockedWallet>(
                Builders<BlockedWallet>.IndexKeys.Ascending(b => b.WalletAddress)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure blocked_wallets index");
        }
    }

    public async Task<bool> IsWalletBlockedAsync(string walletAddress, CancellationToken ct = default)
    {
        var wallet = Normalize(walletAddress);

        // Boundary 1: internal suspension (already-loaded in-memory user state).
        if (_dataStore.Users.TryGetValue(wallet, out var user) && user.Status == UserStatus.Suspended)
            return true;

        // Boundary 2: provenance-bearing denylist (blocked by any source).
        if (_blocks == null) return false;
        return await _blocks.Find(b => b.WalletAddress == wallet).AnyAsync(ct);
    }

    public async Task AddBlockAsync(string walletAddress, BlockSource source, string reason,
        string? reference, string addedBy, CancellationToken ct = default)
    {
        var wallet = Normalize(walletAddress);
        var entry = new BlockedWallet
        {
            Id = $"{wallet}:{source}",
            WalletAddress = wallet,
            Source = source,
            Reason = reason,
            Reference = reference,
            AddedBy = addedBy,
            AddedAt = DateTime.UtcNow
        };

        if (_blocks != null)
            await _blocks.ReplaceOneAsync(b => b.Id == entry.Id, entry,
                new ReplaceOptions { IsUpsert = true }, ct);

        await RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Block,
            Reason = reason,
            Reference = reference,
            ActorWallet = addedBy,
            Metadata = new() { ["source"] = source.ToString() }
        }, ct);
    }

    public async Task<int> BulkImportAsync(IEnumerable<string> wallets, BlockSource source,
        string reason, string addedBy, CancellationToken ct = default)
    {
        if (_blocks == null) return 0;

        var models = new List<WriteModel<BlockedWallet>>();
        foreach (var w in wallets)
        {
            string wallet;
            try { wallet = Normalize(w); }
            catch { continue; } // skip malformed addresses

            var entry = new BlockedWallet
            {
                Id = $"{wallet}:{source}",
                WalletAddress = wallet,
                Source = source,
                Reason = reason,
                AddedBy = addedBy,
                AddedAt = DateTime.UtcNow
            };
            models.Add(new ReplaceOneModel<BlockedWallet>(
                Builders<BlockedWallet>.Filter.Eq(b => b.Id, entry.Id), entry) { IsUpsert = true });
        }

        if (models.Count == 0) return 0;
        await _blocks.BulkWriteAsync(models, cancellationToken: ct);

        await RecordActionAsync(new EnforcementAction
        {
            WalletAddress = "(bulk)",
            Type = EnforcementActionType.Block,
            Reason = reason,
            ActorWallet = addedBy,
            Metadata = new() { ["source"] = source.ToString(), ["count"] = models.Count.ToString() }
        }, ct);

        return models.Count;
    }

    public async Task<bool> RemoveBlockAsync(string walletAddress, BlockSource source,
        string actor, string reason, CancellationToken ct = default)
    {
        if (_blocks == null) return false;
        var wallet = Normalize(walletAddress);
        var id = $"{wallet}:{source}";

        var res = await _blocks.DeleteOneAsync(b => b.Id == id, ct);
        if (res.DeletedCount == 0) return false;

        await RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Unblock,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new() { ["source"] = source.ToString() }
        }, ct);
        return true;
    }

    public async Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default)
    {
        if (_blocks == null) return new();
        if (!string.IsNullOrWhiteSpace(walletAddress))
        {
            var wallet = Normalize(walletAddress);
            return await _blocks.Find(b => b.WalletAddress == wallet).ToListAsync(ct);
        }
        return await _blocks.Find(Builders<BlockedWallet>.Filter.Empty).Limit(1000).ToListAsync(ct);
    }

    public async Task RecordActionAsync(EnforcementAction action, CancellationToken ct = default)
    {
        if (_actions == null) return;
        await _actions.InsertOneAsync(action, cancellationToken: ct);
        _logger.LogInformation("Enforcement: {Type} {Wallet} by {Actor} — {Reason}",
            action.Type, action.WalletAddress, action.ActorWallet, action.Reason);
    }

    public async Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null,
        int limit = 200, CancellationToken ct = default)
    {
        if (_actions == null) return new();
        var filter = string.IsNullOrWhiteSpace(walletAddress)
            ? Builders<EnforcementAction>.Filter.Empty
            : Builders<EnforcementAction>.Filter.Eq(a => a.WalletAddress, Normalize(walletAddress));
        return await _actions.Find(filter).SortByDescending(a => a.Timestamp).Limit(limit).ToListAsync(ct);
    }

    private string Normalize(string w) => _addr.ConvertToChecksumAddress(w);
}
