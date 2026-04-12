using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Text.Json;

namespace Orchestrator.Services;

/// <summary>
/// Background service that audits chunk replication for VM overlay manifests.
///
/// Cycle (every 5 minutes):
///   1. Load manifests where Version > ConfirmedVersion (pending audit)
///   2. For each manifest, call the hosting node's DHT VM /providers/{cid}
///      for each changed block CID
///   3. If all changed CIDs have ≥ ReplicationFactor providers:
///      → advance ConfirmedVersion = Version, ConfirmedRootCid = RootCid
///      → update VirtualMachine.CurrentManifestBlockCount for billing
///   4. Log under-replicated CIDs for diagnostics (no active push —
///      Kademlia re-publication handles recovery naturally)
///
/// DHT VM API is reachable at the WG tunnel IP via the orchestrator's
/// wg-relay-client interface (10.20.0.1 → 10.20.1.x via relay).
/// The DHT binary binds HTTP to 0.0.0.0 so the WG interface is covered.
/// </summary>
public class LazysyncManager : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<LazysyncManager> _logger;
    private readonly HttpClient _httpClient;

    // Audit every 5 minutes. Each /providers/{cid} call does a 30s DHT walk —
    // a manifest with 500 changed blocks would take ~2.5h to audit serially,
    // so we cap at 20 CIDs per manifest per cycle and revisit next cycle.
    private static readonly TimeSpan AuditInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private const int MaxCidsPerManifestPerCycle = 20;

    public LazysyncManager(
        IServiceProvider serviceProvider,
        ILogger<LazysyncManager> logger,
        IHttpClientFactory httpClientFactory)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("lazysync-audit");
        _httpClient.Timeout = TimeSpan.FromSeconds(35); // > 30s DHT walk
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LazysyncManager started — first audit in {Delay}", StartupDelay);
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAuditCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LazysyncManager audit cycle failed");
            }

            await Task.Delay(AuditInterval, stoppingToken);
        }

        _logger.LogInformation("LazysyncManager stopped");
    }

    private async Task RunAuditCycleAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var dataStore = scope.ServiceProvider.GetRequiredService<DataStore>();

        var pending = await dataStore.GetPendingAuditManifestsAsync(limit: 50);
        if (pending.Count == 0)
        {
            _logger.LogDebug("LazysyncManager: no pending manifests");
            return;
        }

        _logger.LogInformation("LazysyncManager: auditing {Count} pending manifest(s)", pending.Count);

        foreach (var manifest in pending)
        {
            if (ct.IsCancellationRequested) break;
            await AuditManifestAsync(manifest, dataStore, ct);
        }
    }

    private async Task AuditManifestAsync(
            ManifestRecord manifest, DataStore dataStore, CancellationToken ct)
    {
        // Audit the cumulative sample — covers full history, not just the latest delta.
        // Falls back to ChangedBlockCids for manifests registered before this field existed.
        var auditPool = manifest.CumulativeBlockCids.Count > 0
            ? manifest.CumulativeBlockCids
            : manifest.ChangedBlockCids;

        if (auditPool.Count == 0)
        {
            _logger.LogDebug(
                "VM {VmId} manifest v{Version}: no CIDs to audit — skipping",
                manifest.VmId, manifest.Version);
            return;
        }

        var (dhtApiUrl, localPeerId) = await ResolveDhtApiUrlAndLocalPeerAsync(manifest.VmId, dataStore);
        if (dhtApiUrl == null)
        {
            _logger.LogWarning(
                "VM {VmId}: no DHT VM API reachable — skipping audit",
                manifest.VmId);
            return;
        }

        // Sample randomly from the cumulative pool so every audit cycle
        // checks a different cross-section of the full block history.
        var cidsToCheck = auditPool
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxCidsPerManifestPerCycle)
            .ToList();

        var underReplicated = new List<string>();

        foreach (var cid in cidsToCheck)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // Count only REMOTE providers — local blockstore on the
                // hosting node is never counted as replication. A block
                // sitting only on the same machine as the VM it protects
                // provides zero node-failure resilience.
                var providers = await GetProvidersAsync(dhtApiUrl, cid, ct);
                var remoteCount = string.IsNullOrEmpty(localPeerId)
                    ? providers.Count
                    : providers.Count(p => p != localPeerId);

                if (remoteCount == 0)
                    underReplicated.Add(cid); // treat zero as under-replicated for counting

                else if (remoteCount < manifest.ReplicationFactor)
                    underReplicated.Add(cid);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Provider check failed for CID {Cid} (VM {VmId}) — skipping cycle",
                    cid[..Math.Min(12, cid.Length)], manifest.VmId);
                return;
            }
        }

        // Total loss: all sampled CIDs have zero or insufficient providers AND
        // blocks were previously confirmed (ConfirmedVersion > 0).
        // Guards against false positives during initial seeding and transient
        // DHT routing failures affecting only a subset of CIDs.
        if (manifest.ConfirmedVersion > 0
            && underReplicated.Count == cidsToCheck.Count
            && cidsToCheck.Count > 0)
        {
            _logger.LogWarning(
                "VM {VmId}: all {Count} sampled CIDs under-replicated or missing " +
                "(ConfirmedVersion={Version}) — total loss detected, triggering reseed",
                manifest.VmId, cidsToCheck.Count, manifest.ConfirmedVersion);
            await TriggerReseedAsync(manifest, dataStore, ct);
            return;
        }

        if (underReplicated.Count > 0)
        {
            _logger.LogDebug(
                "VM {VmId} v{Version}: {Under}/{Total} CIDs under-replicated " +
                "(need {N} remote providers, local excluded) — will retry next cycle",
                manifest.VmId, manifest.Version,
                underReplicated.Count, cidsToCheck.Count,
                manifest.ReplicationFactor);
            return;
        }

        // All sampled CIDs confirmed — advance ConfirmedVersion
        manifest.ConfirmedVersion = manifest.Version;
        manifest.ConfirmedRootCid = manifest.RootCid;
        manifest.ConfirmedChunkMap = manifest.CurrentChunkMap;
        await dataStore.SaveManifestAsync(manifest);

        // Sync block count to VM record for accurate billing
        var vm = await dataStore.GetVmAsync(manifest.VmId);
        if (vm != null)
        {
            vm.CurrentManifestBlockCount = manifest.BlockCount;
            vm.CurrentManifestBlockSizeKb = manifest.BlockSizeKb;
            vm.LastLazysyncAt = DateTime.UtcNow;
            vm.LazysyncStatus = LazysyncStatus.Protected;
            await dataStore.SaveVmAsync(vm);
        }

        _logger.LogInformation(
            "VM {VmId} ConfirmedVersion → {Version} " +
            "({Blocks} × {BlockSizeKb} KB, replication={Factor}x confirmed)",
            manifest.VmId, manifest.ConfirmedVersion,
            manifest.BlockCount, manifest.BlockSizeKb, manifest.ReplicationFactor);
    }

    private async Task TriggerReseedAsync(
        ManifestRecord manifest, DataStore dataStore, CancellationToken ct)
    {
        // Reset manifest so the audit loop re-confirms from scratch once
        // the daemon has re-pushed all blocks after the reseed.
        manifest.Version = 0;
        manifest.ConfirmedVersion = 0;
        manifest.ConfirmedRootCid = null;
        manifest.ChangedBlockCids.Clear();
        manifest.CumulativeBlockCids.Clear();
        await dataStore.SaveManifestAsync(manifest);

        // Update VM status so the dashboard reflects recovery in progress
        var vm = await dataStore.GetVmAsync(manifest.VmId);
        if (vm != null)
        {
            vm.LazysyncStatus = LazysyncStatus.Replicating;
            await dataStore.SaveVmAsync(vm);
        }

        // Push ReseedVm command to the hosting NodeAgent.
        // NodeAgent deletes lazysync.json; LazysyncDaemon reseeds on next cycle.
        if (vm?.NodeId == null) return;
        var node = await dataStore.GetNodeAsync(vm.NodeId);
        if (node == null) return;

        try
        {
            var commandService = _serviceProvider.GetRequiredService<INodeCommandService>();
            var command = new NodeCommand(
                Guid.NewGuid().ToString(),
                NodeCommandType.ReseedVm,
                JsonSerializer.Serialize(new { vmId = manifest.VmId }),
                RequiresAck: true
            );
            await commandService.DeliverCommandAsync(node.Id, command, ct);
            _logger.LogInformation(
                "VM {VmId}: ReseedVm command delivered to node {NodeId}",
                manifest.VmId, node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "VM {VmId}: failed to deliver ReseedVm to node {NodeId}",
                manifest.VmId, vm.NodeId);
        }
    }

    private async Task<(string? dhtApiUrl, string? localPeerId)> ResolveDhtApiUrlAndLocalPeerAsync(
        string vmId, DataStore dataStore)
    {
        var vm = await dataStore.GetVmAsync(vmId);
        if (vm == null || string.IsNullOrEmpty(vm.NodeId)) return (null, null);

        var node = await dataStore.GetNodeAsync(vm.NodeId);
        if (node?.DhtInfo == null || string.IsNullOrEmpty(node.DhtInfo.ListenAddress))
            return (null, null);

        var ip = node.DhtInfo.ListenAddress.Split(':')[0];
        var port = 8080; // dht-dashboard.py proxy, binds 0.0.0.0, proxies /providers/*
        var dhtApiUrl = $"http://{ip}:{port}";

        // Local blockstore peer ID — excluded from remote provider count
        var localPeerId = node.BlockStoreInfo?.PeerId;

        return (dhtApiUrl, localPeerId);
    }

    private async Task<List<string>> GetProvidersAsync(
        string dhtApiUrl, string cid, CancellationToken ct)
    {
        var url = $"{dhtApiUrl}/providers/{Uri.EscapeDataString(cid)}";
        using var response = await _httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(
            cancellationToken: ct);

        if (json.TryGetProperty("providers", out var providersEl) &&
            providersEl.ValueKind == JsonValueKind.Array)
        {
            return providersEl.EnumerateArray()
                .Select(p => p.GetString() ?? string.Empty)
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
        }

        return [];
    }
}