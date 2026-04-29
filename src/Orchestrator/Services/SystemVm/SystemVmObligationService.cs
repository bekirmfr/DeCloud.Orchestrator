using DeCloud.Shared.Models;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Background service that converges each online node toward its desired system VM state.
///
/// Per-cycle steps (every 30 seconds):
///   1. Ensure obligations exist (backfill legacy nodes, detect new eligible roles)
///   2. Pending obligations with met dependencies → deploy
///   3. Deploying obligations → check if VM is Running + healthy → mark Active
///   4. Active obligations → verify VM still exists (self-heal if gone)
///   5. Failed obligations → retry with exponential backoff
///
/// This is the Kubernetes controller pattern: declare desired state, let a loop
/// converge toward it. Registration is fast (compute + store obligations, deploy
/// what's immediately ready), and the loop handles everything else.
///
/// Self-healing covers:
///   - Legacy nodes registered before the obligation system (empty obligations list)
///   - Capability drift (node gains public IP → now eligible for Relay/Ingress)
///   - Stale Active obligations whose VMs disappeared (node restart, agent update)
///   - Lost role info (DhtInfo/RelayInfo null after crash) with VM still running
/// </summary>
public class SystemVmObligationService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IObligationEligibility _eligibility;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemVmObligationService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CloudInitReadyTimeout = TimeSpan.FromMinutes(20); // Deploying → reset if not ready
    private static readonly TimeSpan ActiveVmGracePeriod = TimeSpan.FromMinutes(5);  // Active absence before reset
    private static readonly TimeSpan HeartbeatSnapshotTtl = TimeSpan.FromMinutes(2);  // vm.UpdatedAt freshness window
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan StuckDeletingTimeout = TimeSpan.FromMinutes(15);

    public SystemVmObligationService(
        DataStore dataStore,
        IObligationEligibility eligibility,
        IServiceProvider serviceProvider,
        ILogger<SystemVmObligationService> logger)
    {
        _dataStore = dataStore;
        _eligibility = eligibility;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("SystemVmReconciliationService started (interval: {Interval}s)", Interval.TotalSeconds);

        // Wait for startup to complete
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var nodes = await _dataStore.GetAllNodesAsync();

                // FIX 1: Per-node exception isolation.
                // Previously, a single bad node (corrupt document, transient DB error, etc.)
                // would abort the entire foreach, leaving all subsequent nodes unreconciled
                // for the full 30-second cycle. Now each node is independently guarded.
                foreach (var node in nodes.Where(n => n.Status == NodeStatus.Online))
                {
                    try
                    {
                        await EnsureObligationsAsync(node, ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Error reconciling obligations for node {NodeId} — skipping this cycle",
                            node.Id);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SystemVmReconciliationService outer loop");
            }

            await Task.Delay(Interval, ct);
        }
    }

    /// <summary>
    /// Sync role-specific service info to reflect the current Active obligation.
    /// Only sets VmId, Status, and LastHealthCheck — service-specific fields
    /// (ListenAddress, PeerId, CapacityBytes) come from /join callbacks and
    /// must not be overwritten here.
    /// </summary>
    private static void SyncRoleServiceInfo(Node node, SystemVmObligation obligation)
    {
        switch (obligation.Role)
        {
            case SystemVmRole.Relay:
                node.RelayInfo ??= new RelayNodeInfo();
                node.RelayInfo.RelayVmId = obligation.VmId ?? string.Empty;
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
                break;

            case SystemVmRole.Dht:
                node.DhtInfo ??= new DhtNodeInfo();
                node.DhtInfo.DhtVmId = obligation.VmId ?? string.Empty;
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
                // Do NOT set ListenAddress or PeerId — set by /api/dht/join callback
                break;

            case SystemVmRole.BlockStore:
                node.BlockStoreInfo ??= new BlockStoreInfo();
                node.BlockStoreInfo.BlockStoreVmId = obligation.VmId ?? string.Empty;
                node.BlockStoreInfo.Status = BlockStoreStatus.Active;
                node.BlockStoreInfo.LastHealthCheck = DateTime.UtcNow;
                // Do NOT set ListenAddress, PeerId, or CapacityBytes — set by /api/blockstore/join callback
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Obligation backfill & drift detection
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Ensure a node's obligation list reflects its current capabilities.
    /// Called from NodeService after registration to seed initial obligations.
    /// VM deployment is handled by the node's SystemVmReconciler (P6).
    /// </summary>
    public async Task EnsureObligationsForNodeAsync(Node node, CancellationToken ct = default)
    {
        await EnsureObligationsAsync(node, ct);
    }

    /// <summary>
    /// Ensure a node's obligation list reflects its current capabilities.
    /// Handles three cases:
    ///   1. Legacy nodes with an empty obligations list (registered before the obligation system)
    ///   2. Capability drift (e.g., node gained a public IP and is now eligible for Relay)
    ///   3. Adopting existing VMs — legacy nodes may already have running VMs (via RelayInfo/DhtInfo)
    ///      that were deployed before the obligation system. These are adopted as Active instead
    ///      of creating duplicate deployments.
    /// Existing obligations are never removed (removal would require draining VMs).
    /// </summary>
    private async Task EnsureObligationsAsync(Node node, CancellationToken ct)
    {
        // Skip nodes registered within the last 60 seconds — registration
        // seeds and reconciles obligations atomically. Racing with it causes
        // duplicate system VM deployments.
        if ((DateTime.UtcNow - node.RegisteredAt).TotalSeconds < 60)
        {
            _logger.LogDebug(
                "Skipping EnsureObligations for recently registered node {NodeId} (age: {Age:F0}s)",
                node.Id, (DateTime.UtcNow - node.RegisteredAt).TotalSeconds);
            return;
        }

        var requiredRoles = _eligibility.ComputeObligations(node);
        var existingRoles = new HashSet<SystemVmRole>(
            node.SystemVmObligations.Select(o => o.Role));

        var missingRoles = requiredRoles.Where(r => !existingRoles.Contains(r)).ToList();

        if (missingRoles.Count == 0)
            return;

        foreach (var role in missingRoles)
        {
            var adopted = await TryAdoptExistingVmAsync(node, role, ct);

            // Stamp TemplateId — stable _id of the assigned template.
            var slug = SystemVmRoleMap.ToTemplateSlug(role);
            if (slug is not null)
            {
                var tpl = await _dataStore.GetTemplateBySlugAsync(slug);
                adopted.TemplateId = tpl?.Id;
            }

            node.SystemVmObligations.Add(adopted);

            if (adopted.Status == SystemVmStatus.Active)
            {
                _logger.LogInformation(
                    "Adopted existing {Role} VM {VmId} on node {NodeId} as Active obligation",
                    role, adopted.VmId, node.Id);
            }
            else if (adopted.Status == SystemVmStatus.Deploying)
            {
                _logger.LogInformation(
                    "Adopted existing {Role} VM {VmId} on node {NodeId} as Deploying (VM status: not yet Running)",
                    role, adopted.VmId, node.Id);
            }
        }

        // Back-fill TemplateId on existing obligations that predate this field.
        foreach (var obligation in node.SystemVmObligations.Where(o => o.TemplateId is null))
        {
            var slug = SystemVmRoleMap.ToTemplateSlug(obligation.Role);
            if (slug is null) continue;
            var tpl = await _dataStore.GetTemplateBySlugAsync(slug);
            if (tpl is not null)
            {
                obligation.TemplateId = tpl.Id;
                _logger.LogInformation(
                    "Back-filled TemplateId {Id} on {Role} obligation for node {NodeId}",
                    tpl.Id, obligation.Role, node.Id);
            }
        }

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "Backfilled obligations on node {NodeId}: [{Roles}] " +
            "(total obligations: {Total})",
            node.Id,
            string.Join(", ", missingRoles),
            node.SystemVmObligations.Count);
    }

    /// <summary>
    /// Check if a node already has a VM for a role (deployed before the obligation
    /// system existed). If so, adopt it — but only mark Active if the VM actually
    /// exists in the datastore and is Running. VMs in other states are adopted as
    /// Deploying so they flow through CheckDeploymentProgressAsync normally.
    /// If the referenced VM no longer exists, fall back to Pending.
    /// </summary>
    private async Task<SystemVmObligation> TryAdoptExistingVmAsync(
        Node node, SystemVmRole role, CancellationToken ct)
    {
        // Check role-specific info for an existing VM ID
        string? existingVmId = role switch
        {
            SystemVmRole.Relay => node.RelayInfo?.RelayVmId,
            SystemVmRole.Dht => node.DhtInfo?.DhtVmId,
            SystemVmRole.BlockStore => node.BlockStoreInfo?.BlockStoreVmId,
            _ => null
        };

        // Fallback: search the datastore for a healthy VM of the correct type
        if (existingVmId == null)
        {
            existingVmId = await TryDiscoverHealthySystemVmAsync(node, role);
        }

        if (existingVmId == null)
        {
            return new SystemVmObligation
            {
                Role = role,
                Status = SystemVmStatus.Pending
            };
        }

        // Verify the VM actually exists before adopting
        var vm = await _dataStore.GetVmAsync(existingVmId);
        if (vm == null)
        {
            _logger.LogWarning(
                "Node {NodeId} has {Role} VM ID {VmId} in role info but VM not found in datastore — creating Pending obligation",
                node.Id, role, existingVmId);

            // Clear stale role info pointing to a non-existent VM
            if (role == SystemVmRole.Relay) node.RelayInfo = null;
            if (role == SystemVmRole.Dht) node.DhtInfo = null;

            return new SystemVmObligation
            {
                Role = role,
                Status = SystemVmStatus.Pending
            };
        }

        // VM exists — adopt based on actual status
        if (vm.Status == VmStatus.Running)
        {
            // Sync role-specific status to Active
            if (role == SystemVmRole.Relay && node.RelayInfo != null)
            {
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            }
            else if (role == SystemVmRole.Dht && node.DhtInfo != null)
            {
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
            }

            return new SystemVmObligation
            {
                Role = role,
                VmId = existingVmId,
                Status = SystemVmStatus.Active,
                ActiveAt = DateTime.UtcNow
            };
        }

        if (vm.Status == VmStatus.Error)
        {
            _logger.LogWarning(
                "Node {NodeId} has {Role} VM {VmId} in Error state — adopting as Failed",
                node.Id, role, existingVmId);

            return new SystemVmObligation
            {
                Role = role,
                VmId = existingVmId,
                Status = SystemVmStatus.Failed,
                FailureCount = 1,
                LastError = vm.StatusMessage ?? "VM in Error state at adoption"
            };
        }

        // Provisioning, Stopped, or other non-terminal state — adopt as Deploying
        // so CheckDeploymentProgressAsync handles the transition
        return new SystemVmObligation
        {
            Role = role,
            VmId = existingVmId,
            Status = SystemVmStatus.Deploying,
            DeployedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Fallback discovery when role info (DhtInfo/RelayInfo) is lost but a system VM
    /// may still be running on the node. Searches the datastore for a Running VM of
    /// the correct type that is confirmed alive via recent service health checks.
    /// Reconstructs role info from the discovered VM so the system self-heals.
    ///
    /// Only DHT and BlockStore VMs can be fully reconstructed — Relay VMs require
    /// WireGuard keys that aren't stored on the VM record, so they fall through to
    /// fresh deployment.
    /// </summary>
    private async Task<string?> TryDiscoverHealthySystemVmAsync(Node node, SystemVmRole role)
    {
        if (role != SystemVmRole.Dht && role != SystemVmRole.BlockStore) return null;

        // BlockStore VM recovery: reconstruct BlockStoreInfo from a running VM
        if (role == SystemVmRole.BlockStore)
        {
            var bsVms = await _dataStore.GetVmsByNodeAsync(node.Id);
            var bsCandidate = bsVms.FirstOrDefault(v =>
                v.Spec.VmType == VmType.BlockStore &&
                v.Status == VmStatus.Running &&
                v.IsFullyReady &&
                v.Services.Any(s => s.LastCheckAt.HasValue &&
                    (DateTime.UtcNow - s.LastCheckAt.Value).TotalMinutes <= 5));

            if (bsCandidate == null) return null;

            _logger.LogInformation(
                "Discovered healthy BlockStore VM {VmId} on node {NodeId} via datastore fallback",
                bsCandidate.Id, node.Id);

            var bsAdvertiseIp = bsCandidate.Labels?.GetValueOrDefault("blockstore-advertise-ip")
                             ?? DhtNodeService.GetAdvertiseIp(node);

            node.BlockStoreInfo = new BlockStoreInfo
            {
                BlockStoreVmId = bsCandidate.Id,
                ListenAddress = $"{bsAdvertiseIp}:{BlockStoreVmSpec.BitswapPort}",
                ApiPort = BlockStoreVmSpec.ApiPort,
                Status = BlockStoreStatus.Active,
                LastHealthCheck = DateTime.UtcNow,
            };

            if (long.TryParse(bsCandidate.Labels?.GetValueOrDefault("blockstore-storage-bytes"), out var bsCap))
                node.BlockStoreInfo.CapacityBytes = bsCap;

            return bsCandidate.Id;
        }

        // DHT VM recovery
        var nodeVms = await _dataStore.GetVmsByNodeAsync(node.Id);
        var candidate = nodeVms.FirstOrDefault(v =>
            v.Spec.VmType == VmType.Dht &&
            v.Status == VmStatus.Running &&
            v.IsFullyReady &&
            v.Services.Any(s => s.LastCheckAt.HasValue &&
                (DateTime.UtcNow - s.LastCheckAt.Value).TotalMinutes <= 5));

        if (candidate == null) return null;

        _logger.LogInformation(
            "Discovered healthy DHT VM {VmId} on node {NodeId} via datastore fallback — " +
            "DhtInfo was lost but VM is alive and passing health checks",
            candidate.Id, node.Id);

        // Reconstruct DhtInfo from the discovered VM.
        // Prefer the advertise IP baked into the VM's labels (which may be a WG tunnel IP)
        // over GetAdvertiseIp(node) which only handles CGNAT tunnel IPs, not the WG mesh
        // override that DhtNodeService.DeployDhtVmAsync applies for co-located relay nodes.
        var advertiseIp = candidate.Labels?.GetValueOrDefault("dht-advertise-ip")
            ?? DhtNodeService.GetAdvertiseIp(node);

        node.DhtInfo = new DhtNodeInfo
        {
            DhtVmId = candidate.Id,
            ListenAddress = $"{advertiseIp}:{DhtNodeService.DhtListenPort}",
            ApiPort = DhtNodeService.DhtApiPort,
            Status = DhtStatus.Active,
            LastHealthCheck = DateTime.UtcNow,
        };

        return candidate.Id;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Obligation state hydration — pre-populate node fields from stored state
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads identity from <paramref name="obligation"/>.StateJson and writes it
    /// onto the node model so deployment services pick it up without changing
    /// their own signatures.
    ///
    /// Relay  → node.RelayInfo.WireGuardPrivateKey / PublicKey / TunnelIp / RelaySubnet
    ///           The existing reuse guard in RelayNodeService.DeployRelayVmAsync
    ///           already checks node.RelayInfo?.WireGuardPrivateKey, so hydrating
    ///           it here is all that is required.
    ///
    /// Dht    → obligation.AuthToken
    ///           DhtNodeService reads node.SystemVmObligations to find this value.
    ///
    /// BlockStore → obligation.AuthToken
    ///           BlockStoreService reads node.SystemVmObligations to find this value.
    ///
    /// Safe to call on first deploy (StateJson null/empty → method returns immediately,
    /// deployment services generate fresh credentials as before).
    /// </summary>
    private void HydrateNodeFromObligationState(Node node, SystemVmObligation obligation)
    {
        if (string.IsNullOrEmpty(obligation.StateJson))
            return;

        var jsonOptions = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        try
        {
            switch (obligation.Role)
            {
                // ── Relay ────────────────────────────────────────────────
                case SystemVmRole.Relay:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<RelayObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        node.RelayInfo ??= new RelayNodeInfo();

                        // These are the exact three fields the existing
                        // RelayNodeService reuse guard reads.
                        node.RelayInfo.WireGuardPrivateKey = state.WireGuardPrivateKey;
                        node.RelayInfo.WireGuardPublicKey = state.WireGuardPublicKey;
                        node.RelayInfo.TunnelIp = state.TunnelIp;

                        // Subnet slot: "10.20.5.0/24" → 5
                        var slot = ParseRelaySubnetSlot(state.RelaySubnet);
                        if (slot > 0)
                            node.RelayInfo.RelaySubnet = slot;

                        _logger.LogDebug(
                            "Hydrated Relay state v{Version} onto node {NodeId} " +
                            "(pubKey: {PubKey}, subnet: {Subnet})",
                            state.Version, node.Id,
                            state.WireGuardPublicKey.Length > 12
                                ? state.WireGuardPublicKey[..12] + "..."
                                : state.WireGuardPublicKey,
                            state.RelaySubnet);
                        break;
                    }

                // ── DHT ──────────────────────────────────────────────────
                case SystemVmRole.Dht:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<DhtObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        // AuthToken lives on the obligation itself — DhtNodeService
                        // reads node.SystemVmObligations to get it.
                        if (!string.IsNullOrEmpty(state.AuthToken))
                            obligation.AuthToken = state.AuthToken;

                        _logger.LogDebug(
                            "Hydrated DHT state v{Version} onto obligation for node {NodeId} " +
                            "(peerId: {PeerId})",
                            state.Version, node.Id,
                            state.PeerId.Length > 12
                                ? state.PeerId[..12] + "..."
                                : state.PeerId);
                        break;
                    }

                // ── BlockStore ───────────────────────────────────────────
                case SystemVmRole.BlockStore:
                    {
                        var state = System.Text.Json.JsonSerializer
                            .Deserialize<BlockStoreObligationState>(obligation.StateJson, jsonOptions);

                        if (state is null) return;

                        if (!string.IsNullOrEmpty(state.AuthToken))
                            obligation.AuthToken = state.AuthToken;

                        _logger.LogDebug(
                            "Hydrated BlockStore state v{Version} onto obligation for node {NodeId} " +
                            "(peerId: {PeerId})",
                            state.Version, node.Id,
                            state.PeerId.Length > 12
                                ? state.PeerId[..12] + "..."
                                : state.PeerId);
                        break;
                    }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal — deployment proceeds with freshly generated credentials.
            // Logs at Warning so operators can investigate if identity drift occurs.
            _logger.LogWarning(ex,
                "Could not deserialise obligation state for {Role} on node {NodeId} " +
                "(StateVersion: {Version}) — deploying with freshly generated identity",
                obligation.Role, node.Id, obligation.StateVersion);
        }
    }

    /// <summary>
    /// Extract the /24 slot integer from a relay subnet CIDR string.
    /// "10.20.5.0/24" → 5.  Returns 0 on any parse failure.
    /// </summary>
    private static int ParseRelaySubnetSlot(string? relaySubnet)
    {
        if (string.IsNullOrEmpty(relaySubnet))
            return 0;

        // "10.20.{slot}.0/24" — the slot is the third octet
        var withoutCidr = relaySubnet.Split('/')[0];
        var octets = withoutCidr.Split('.');
        return octets.Length >= 3 && int.TryParse(octets[2], out var slot) ? slot : 0;
    }
}