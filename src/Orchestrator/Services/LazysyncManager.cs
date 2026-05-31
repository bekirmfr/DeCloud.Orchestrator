using DeCloud.Shared.Enums;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Collections.Concurrent;
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

    // Audit every 5 minutes. All unconfirmed CIDs are checked each cycle by a fixed
    // worker pool (Parallel.ForEachAsync, MaxConcurrentProviderChecks wide). A provider
    // check is a lightweight Kademlia FindProviders walk — the same class as the
    // blockstore's reannounce, so the degree mirrors ReannounceWorkers (16) rather than
    // the data-transfer fetch pool (4). Walks self-throttle under DHT stress because the
    // HTTP call blocks. The audit loop processes manifests serially, so this degree is
    // also the system-wide bound on concurrent DHT walks. MaxReVerifyPerCycle confirmed
    // CIDs are spot-checked each cycle to catch a silently wiped/redeployed remote.
    private static readonly TimeSpan AuditInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
    private const int MaxConcurrentProviderChecks = 16;
    private const int MaxReVerifyPerCycle = 20;

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

    private async Task AuditManifestAsync(ManifestRecord manifest, DataStore dataStore, CancellationToken ct)
    {
        // Migration fetches the FULL ConfirmedChunkMap, so confirmation must verify the FULL
        // current block set — not a sample of the recently-changed CumulativeBlockCids window.
        // Auditing the capped (200) changed-CID pool let early-seeded, never-re-changed blocks
        // (e.g. the offset-0 boot block) ride into a "confirmed" manifest with zero remote
        // replicas. Content addressing means a confirmed CID stays confirmed across version
        // bumps, so confirmation is tracked incrementally and ConfirmedVersion advances only
        // when ConfirmedCids covers every CID in CurrentChunkMap.
        var currentCids = manifest.CurrentChunkMap.Values
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();

        if (currentCids.Count == 0)
        {
            _logger.LogDebug(
                "VM {VmId} v{Version}: empty chunk map — skipping",
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

        // Drop confirmations for CIDs no longer in the map (block changed → new CID at offset).
        manifest.ConfirmedCids.IntersectWith(currentCids);
        manifest.LastAuditedAt = DateTime.UtcNow;

        // Check ALL unconfirmed CIDs this cycle — no per-cycle cap — plus a random sample of
        // confirmed ones so a silently wiped/redeployed remote is caught. The worker pool pulls
        // a CID, runs the walk, pulls the next; degree is fixed at MaxConcurrentProviderChecks.
        var unconfirmed = currentCids
            .Where(c => !manifest.ConfirmedCids.Contains(c))
            .ToList();
        var reVerify = currentCids
            .Where(manifest.ConfirmedCids.Contains)
            .OrderBy(_ => Random.Shared.Next())
            .Take(MaxReVerifyPerCycle)
            .ToList();
        var toCheck = unconfirmed.Concat(reVerify).ToList();

        // Workers write results to a thread-safe sink; ConfirmedCids (a HashSet) is mutated
        // serially afterwards, never from inside the pool.
        var results = new ConcurrentBag<(string Cid, bool Confirmed, bool Failed)>();
        try
        {
            await Parallel.ForEachAsync(
                toCheck,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxConcurrentProviderChecks,
                    CancellationToken = ct
                },
                async (cid, token) =>
                {
                    try
                    {
                        // Count only REMOTE providers — the local blockstore on the hosting
                        // node provides zero node-failure resilience and is never counted.
                        var providers = await GetProvidersAsync(dhtApiUrl, cid, token);
                        var remoteCount = string.IsNullOrEmpty(localPeerId)
                            ? providers.Count
                            : providers.Count(p => p != localPeerId);
                        results.Add((cid, remoteCount >= manifest.ReplicationFactor, false));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw; // cancel the batch; the outer catch saves and returns
                    }
                    catch (Exception ex)
                    {
                        // INDETERMINATE (host DHT FindProviders timing out) — NOT confirmed
                        // under-replication. Do not erode an existing confirmation; a transient
                        // DHT outage must not drain ConfirmedCids and trigger a spurious reseed.
                        // Count it toward firing repair (re-announce helps exactly here).
                        _logger.LogDebug(ex,
                            "Provider check indeterminate for CID {Cid} (VM {VmId}) — will nudge repair",
                            cid[..Math.Min(12, cid.Length)], manifest.VmId);
                        results.Add((cid, false, true));
                    }
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await dataStore.SaveManifestAsync(manifest);
            return;
        }

        // Fold results into ConfirmedCids serially — HashSet is not thread-safe.
        var underReplicated = new List<string>();
        var checkFailures = 0;

        foreach (var (cid, confirmed, failed) in results)
        {
            if (failed)
                checkFailures++;
            else if (confirmed)
                manifest.ConfirmedCids.Add(cid);
            else
            {
                manifest.ConfirmedCids.Remove(cid); // regression: was confirmed, now isn't
                underReplicated.Add(cid);
            }
        }

        // Total loss: previously confirmed, but the confirmed set has fully drained.
        if (manifest.ConfirmedVersion > 0 && manifest.ConfirmedCids.Count == 0)
        {
            _logger.LogWarning(
                "VM {VmId}: confirmed set drained — total loss detected, triggering full reseed",
                manifest.VmId);
            await TriggerReseedAsync(manifest, dataStore, ct);
            return;
        }

        // Not yet fully replicated — persist progress, nudge repair, wait for next cycle.
        if (!currentCids.All(manifest.ConfirmedCids.Contains))
        {
            var confirmed = currentCids.Count(manifest.ConfirmedCids.Contains);
            if (checkFailures > 0)
                _logger.LogWarning(
                    "VM {VmId}: {Failures}/{Checked} provider lookups failed this cycle — host DHT may be " +
                    "unreachable or overloaded; confirmation will stall until it recovers",
                    manifest.VmId, checkFailures, toCheck.Count);
            _logger.LogInformation(
                "VM {VmId} v{Version}: {Confirmed}/{Total} blocks ≥{N}x remote — not yet migration-ready",
                manifest.VmId, manifest.Version, confirmed, currentCids.Count, manifest.ReplicationFactor);

            await dataStore.SaveManifestAsync(manifest);

            // Phase D: no orchestrator-side reseed on partial coverage. The blockstore
            // mesh handles repair signaling reactively (Phase B GC eviction → needs-replica;
            // Phase C presence-loss → needs-replica; Phase C survey → new-blocks). The
            // orchestrator simply advances ConfirmedCids each cycle as the mesh
            // re-publishes and other peers absorb the blocks. If the mesh has failed
            // catastrophically (all replicas gone), the drain detection above triggers
            // TriggerReseedAsync — the orchestrator's irreducible escape hatch.

            return;
        }

        // Full coverage — every block in the current map has ≥RF remote providers.
        // Defensive copy: ConfirmedChunkMap is a snapshot at the moment of confirmation
        // and must not alias the live CurrentChunkMap. Currently CurrentChunkMap is
        // only replaced (not mutated) by RegisterManifestAsync, so a shared reference
        // would survive — but the snapshot semantics belong here, not as an invariant
        // maintained in another file.
        manifest.ConfirmedVersion = manifest.Version;
        manifest.ConfirmedRootCid = manifest.RootCid;
        manifest.ConfirmedChunkMap = new Dictionary<long, string>(manifest.CurrentChunkMap);
        await dataStore.SaveManifestAsync(manifest);

        var vm = await dataStore.GetVmAsync(manifest.VmId);

        // Fire-and-forget: push confirmed CID list to the hosting node's blockstore.
        // The blockstore binary uses this list to prioritise eviction of already-safe
        // blocks before falling back to LRU, freeing space for blocks still scattering.
        // Non-fatal — if the push fails the binary falls back to pure LRU as before.
        if (manifest.ConfirmedChunkMap.Count > 0)
        {
            var hostNode = await dataStore.GetNodeAsync(vm?.NodeId ?? "");
            if (hostNode != null)
            {
                _ = PushConfirmedBlocksAsync(
                        hostNode, manifest.VmId,
                        manifest.ConfirmedChunkMap.Values.ToList(),
                        ct);
            }
        }

        // Sync block count to VM record for accurate billing
        
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

    /// <summary>
    /// POST the confirmed CID list to the hosting NodeAgent → NodeAgent forwards
    /// to the local BlockStore VM → binary writes confirmed/{vmId}.cids.
    /// GC reads that file and evicts confirmed blocks before the LRU pass.
    /// Non-fatal: failure is logged at Debug and the binary falls back to LRU.
    /// </summary>
    private async Task PushConfirmedBlocksAsync(
        Node node, string vmId, List<string> confirmedCids, CancellationToken ct)
    {
        try
        {
            var agentUrl = GetNodeAgentUrl(node);
            if (agentUrl == null)
            {
                _logger.LogDebug(
                    "VM {VmId}: skipping confirmed-blocks push — node {NodeId} has no reachable URL",
                    vmId, node.Id);
                return;
            }

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                vmId,
                cids = confirmedCids
            });

            using var content = new StringContent(
                payload, System.Text.Encoding.UTF8, "application/json");

            // Short timeout — this is best-effort, not on the critical path.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _httpClient.PostAsync(
                $"{agentUrl}/api/blockstore/confirmed", content, cts.Token);

            if (response.IsSuccessStatusCode)
                _logger.LogDebug(
                    "VM {VmId}: pushed {Count} confirmed CIDs to node {NodeId}",
                    vmId, confirmedCids.Count, node.Id);
            else
                _logger.LogDebug(
                    "VM {VmId}: confirmed-blocks push returned {Status} from node {NodeId}",
                    vmId, response.StatusCode, node.Id);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "VM {VmId}: confirmed-blocks push to node {NodeId} failed (non-fatal)",
                vmId, node.Id);
        }
    }

    /// <summary>Returns the NodeAgent HTTP base URL for a node, handling CGNAT.</summary>
    private static string? GetNodeAgentUrl(Node node)
    {
        if (string.IsNullOrEmpty(node.PublicIp)) return null;
        var port = node.AgentPort > 0 ? node.AgentPort : 5100;

        // CGNAT nodes: route via WireGuard tunnel IP
        if (node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
            return $"http://{node.CgnatInfo.TunnelIp}:{port}";

        return $"http://{node.PublicIp}:{port}";
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
            // Register before dispatch so ProcessCommandAcknowledgmentAsync can
            // match the ack to a tracked command. Aligns this path with the other
            // two ReseedVm dispatch sites (MaybeTriggerReannounceAsync and the
            // post-migration reseed in NodeService) — every RequiresAck command
            // must call RegisterCommand first or the ack arrives orphaned.
            var reseedId = Guid.NewGuid().ToString();
            dataStore.RegisterCommand(reseedId, manifest.VmId, node.Id, NodeCommandType.ReseedVm);
            var command = new NodeCommand(
                reseedId,
                NodeCommandType.ReseedVm,
                JsonSerializer.Serialize(new { vmId = manifest.VmId }),
                RequiresAck: true,
                TargetResourceId: manifest.VmId
            );
            await commandService.DeliverCommandAsync(node.Id, command, ct);
            _logger.LogInformation(
                "VM {VmId}: ReseedVm command {CommandId} delivered to node {NodeId}",
                manifest.VmId, reseedId, node.Id);
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