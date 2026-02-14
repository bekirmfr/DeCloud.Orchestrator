using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Background service that converges each node toward its desired system VM state.
///
/// Every 30 seconds, for each online node:
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
public class SystemVmReconciliationService : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IDhtNodeService _dhtNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SystemVmReconciliationService> _logger;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    public SystemVmReconciliationService(
        DataStore dataStore,
        IRelayNodeService relayNodeService,
        IDhtNodeService dhtNodeService,
        IServiceProvider serviceProvider,
        ILogger<SystemVmReconciliationService> logger)
    {
        _dataStore = dataStore;
        _relayNodeService = relayNodeService;
        _dhtNodeService = dhtNodeService;
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
                foreach (var node in nodes.Where(n => n.Status == NodeStatus.Online))
                {
                    await EnsureObligationsAsync(node, ct);
                    await ReconcileNodeAsync(node, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SystemVmReconciliationService");
            }

            await Task.Delay(Interval, ct);
        }
    }

    /// <summary>
    /// Reconcile a single node: process each obligation based on its current status.
    /// Called during the background loop AND immediately after registration
    /// (to deploy obligations with no dependencies right away).
    /// </summary>
    public async Task ReconcileNodeAsync(Node node, CancellationToken ct = default)
    {
        var changed = false;

        foreach (var obligation in node.SystemVmObligations)
        {
            var before = obligation.Status;

            switch (obligation.Status)
            {
                case SystemVmStatus.Pending:
                    await TryDeployAsync(node, obligation, ct);
                    break;

                case SystemVmStatus.Deploying:
                    await CheckDeploymentProgressAsync(node, obligation, ct);
                    break;

                case SystemVmStatus.Active:
                    await VerifyActiveAsync(node, obligation, ct);
                    break;

                case SystemVmStatus.Failed:
                    await TryRetryAsync(node, obligation, ct);
                    break;
            }

            if (obligation.Status != before)
                changed = true;
        }

        if (changed)
            await _dataStore.SaveNodeAsync(node);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Pending → Deploy (if dependencies are met)
    // ════════════════════════════════════════════════════════════════════════

    private async Task TryDeployAsync(Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (!SystemVmDependencies.AreDependenciesMet(obligation.Role, node.SystemVmObligations))
            return; // Dependencies not met yet — will try again next cycle

        // CGNAT prerequisite: DHT deployment requires a relay tunnel IP so
        // GetAdvertiseIp() returns the overlay address, not the unreachable
        // public IP. The heartbeat's SyncCgnatStateFromHeartbeatAsync assigns
        // relays within one cycle (15s) — just hold DHT in Pending until then.
        if (obligation.Role == SystemVmRole.Dht
            && node.IsBehindCgnat
            && string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
        {
            _logger.LogDebug(
                "Deferring DHT deploy on CGNAT node {NodeId} — relay tunnel not assigned yet",
                node.Id);
            return;
        }

        // Guard: skip if another obligation for the same role is already Deploying or Active.
        // This prevents duplicate VMs when registration and the background loop race.
        var alreadyDeployed = node.SystemVmObligations.Any(o =>
            o != obligation &&
            o.Role == obligation.Role &&
            (o.Status == SystemVmStatus.Deploying || o.Status == SystemVmStatus.Active));

        if (alreadyDeployed)
        {
            _logger.LogDebug(
                "Skipping {Role} deploy on node {NodeId} — another obligation for this role is already Deploying/Active",
                obligation.Role, node.Id);
            return;
        }

        // Belt-and-suspenders: check the datastore for an existing VM of the same
        // type on this node. Covers orphaned VMs not tracked by obligations (e.g.,
        // after a failed redeployment where the node agent rejected the duplicate
        // but the old VM was already transitioned to Deleting). Without this, the
        // reconciliation loop creates new VM records that the node agent rejects,
        // leaving orphaned ghost records in the orchestrator DB.
        if (obligation.Role is SystemVmRole.Dht or SystemVmRole.Relay)
        {
            var vmType = obligation.Role == SystemVmRole.Dht ? VmType.Dht : VmType.Relay;
            var nodeVms = await _dataStore.GetVmsByNodeAsync(node.Id);
            var existingVm = nodeVms.FirstOrDefault(v =>
                v.Spec.VmType == vmType &&
                v.Status is VmStatus.Running or VmStatus.Provisioning or VmStatus.Deleting);

            if (existingVm != null)
            {
                _logger.LogDebug(
                    "Skipping {Role} deploy on node {NodeId} — existing VM {VmId} in state {Status}",
                    obligation.Role, node.Id, existingVm.Id, existingVm.Status);

                // If the existing VM is Running, adopt it instead of deploying a new one
                if (existingVm.Status == VmStatus.Running)
                {
                    obligation.VmId = existingVm.Id;
                    obligation.Status = SystemVmStatus.Active;
                    obligation.ActiveAt = DateTime.UtcNow;

                    _logger.LogInformation(
                        "Re-adopted existing {Role} VM {VmId} on node {NodeId} instead of deploying duplicate",
                        obligation.Role, existingVm.Id, node.Id);
                }
                return;
            }
        }

        _logger.LogInformation(
            "Deploying {Role} system VM on node {NodeId} (dependencies met)",
            obligation.Role, node.Id);

        try
        {
            var vmId = await DeploySystemVmAsync(node, obligation.Role, ct);

            if (vmId != null)
            {
                obligation.VmId = vmId;
                obligation.Status = SystemVmStatus.Deploying;
                obligation.DeployedAt = DateTime.UtcNow;

                _logger.LogInformation(
                    "{Role} VM {VmId} deploying on node {NodeId}",
                    obligation.Role, vmId, node.Id);
            }
            else
            {
                obligation.FailureCount++;
                obligation.Status = SystemVmStatus.Failed;
                obligation.LastError = "Deployment returned null";

                _logger.LogWarning(
                    "Failed to deploy {Role} VM on node {NodeId} (attempt {Count})",
                    obligation.Role, node.Id, obligation.FailureCount);
            }
        }
        catch (Exception ex)
        {
            obligation.FailureCount++;
            obligation.Status = SystemVmStatus.Failed;
            obligation.LastError = ex.Message;

            _logger.LogError(ex,
                "Exception deploying {Role} VM on node {NodeId} (attempt {Count})",
                obligation.Role, node.Id, obligation.FailureCount);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deploying → Active (or Failed)
    // ════════════════════════════════════════════════════════════════════════

    private async Task CheckDeploymentProgressAsync(
        Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(obligation.VmId))
        {
            // VM ID missing — reset to Pending
            obligation.Status = SystemVmStatus.Pending;
            obligation.VmId = null;
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        if (vm == null)
        {
            // VM disappeared — reset to Pending for redeployment
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} disappeared — resetting to Pending",
                obligation.Role, obligation.VmId, node.Id);
            obligation.Status = SystemVmStatus.Pending;
            obligation.VmId = null;
            return;
        }

        if (vm.Status == VmStatus.Running)
        {
            obligation.Status = SystemVmStatus.Active;
            obligation.ActiveAt = DateTime.UtcNow;

            _logger.LogInformation(
                "{Role} VM {VmId} on node {NodeId} is Active",
                obligation.Role, obligation.VmId, node.Id);

            // Sync role-specific info to Active now that VM is Running
            if (obligation.Role == SystemVmRole.Relay && node.RelayInfo != null)
            {
                node.RelayInfo.Status = RelayStatus.Active;
                node.RelayInfo.LastHealthCheck = DateTime.UtcNow;
            }
            else if (obligation.Role == SystemVmRole.Dht && node.DhtInfo != null)
            {
                node.DhtInfo.Status = DhtStatus.Active;
                node.DhtInfo.LastHealthCheck = DateTime.UtcNow;
            }
        }
        else if (vm.Status == VmStatus.Error)
        {
            obligation.Status = SystemVmStatus.Failed;
            obligation.FailureCount++;
            obligation.LastError = vm.StatusMessage;

            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} entered Error state: {Error}",
                obligation.Role, obligation.VmId, node.Id, vm.StatusMessage);
        }
        // else: still Provisioning — wait for next cycle
    }

    // ════════════════════════════════════════════════════════════════════════
    // Failed → Retry (with exponential backoff)
    // ════════════════════════════════════════════════════════════════════════

    private async Task TryRetryAsync(Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        // Exponential backoff: 30s, 60s, 120s, 240s, cap at 5 min
        var backoffSeconds = 30 * Math.Pow(2, Math.Min(obligation.FailureCount - 1, 4));
        var backoff = TimeSpan.FromSeconds(backoffSeconds);

        var lastAttempt = obligation.DeployedAt ?? DateTime.MinValue;
        if (DateTime.UtcNow - lastAttempt < backoff)
            return; // Too soon to retry

        _logger.LogInformation(
            "Retrying {Role} VM on node {NodeId} (attempt {Count}, backoff: {Backoff}s)",
            obligation.Role, node.Id, obligation.FailureCount + 1, backoffSeconds);

        // Clean up the old failed VM before deploying a replacement.
        // Without this, the old Error-state VM lingers with reserved resources,
        // causing resource drift and duplicate VMs.
        if (!string.IsNullOrEmpty(obligation.VmId))
        {
            try
            {
                var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
                await lifecycle.TransitionAsync(obligation.VmId, VmStatus.Deleting,
                    TransitionContext.Manual("Cleaning up failed system VM before retry"));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to clean up old {Role} VM {VmId} on node {NodeId} — proceeding with retry",
                    obligation.Role, obligation.VmId, node.Id);
            }
        }

        // Reset and try again
        obligation.Status = SystemVmStatus.Pending;
        obligation.VmId = null;
        await TryDeployAsync(node, obligation, ct);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Obligation backfill & drift detection
    // ════════════════════════════════════════════════════════════════════════

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
        var requiredRoles = ObligationEligibility.ComputeObligations(node);
        var existingRoles = new HashSet<SystemVmRole>(
            node.SystemVmObligations.Select(o => o.Role));

        var missingRoles = requiredRoles.Where(r => !existingRoles.Contains(r)).ToList();

        if (missingRoles.Count == 0)
            return;

        foreach (var role in missingRoles)
        {
            var adopted = await TryAdoptExistingVmAsync(node, role, ct);
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
        string? existingVmId = role switch
        {
            SystemVmRole.Relay when node.RelayInfo != null
                && !string.IsNullOrEmpty(node.RelayInfo.RelayVmId)
                => node.RelayInfo.RelayVmId,

            SystemVmRole.Dht when node.DhtInfo != null
                && !string.IsNullOrEmpty(node.DhtInfo.DhtVmId)
                => node.DhtInfo.DhtVmId,

            _ => null
        };

        // ════════════════════════════════════════════════════════════════════════
        // Fallback: role info lost (crash, DB corruption) but the system VM may
        // still be running on the node. Search the datastore for a healthy VM of
        // the correct type — only adopt if it's Running AND passing health checks.
        // ════════════════════════════════════════════════════════════════════════
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
    /// Only DHT VMs can be fully reconstructed — Relay VMs require WireGuard keys
    /// that aren't stored on the VM record, so they fall through to fresh deployment.
    /// </summary>
    private async Task<string?> TryDiscoverHealthySystemVmAsync(Node node, SystemVmRole role)
    {
        if (role != SystemVmRole.Dht) return null;

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

        // Reconstruct DhtInfo from the discovered VM
        var advertiseIp = DhtNodeService.GetAdvertiseIp(node);

        node.DhtInfo = new DhtNodeInfo
        {
            DhtVmId = candidate.Id,
            ListenAddress = $"{advertiseIp}:{DhtNodeService.DhtListenPort}",
            ApiPort = 5080,
            Status = DhtStatus.Active,
            LastHealthCheck = DateTime.UtcNow,
        };

        return candidate.Id;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Active → verify VM still exists (self-heal if gone)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verify that an Active obligation still has a running VM.
    /// Handles node restarts, agent updates, or orphaned obligation state
    /// where the database says Active but the VM no longer exists.
    /// </summary>
    private async Task VerifyActiveAsync(
        Node node, SystemVmObligation obligation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(obligation.VmId))
        {
            // Active with no VM ID — impossible state, reset
            _logger.LogWarning(
                "{Role} obligation on node {NodeId} is Active with no VM ID — resetting to Pending",
                obligation.Role, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        var vm = await _dataStore.GetVmAsync(obligation.VmId);

        if (vm == null)
        {
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} no longer exists — resetting to Pending for redeployment",
                obligation.Role, obligation.VmId, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        if (vm.Status == VmStatus.Error)
        {
            _logger.LogWarning(
                "{Role} VM {VmId} on node {NodeId} is in Error state — resetting to Pending",
                obligation.Role, obligation.VmId, node.Id);
            ResetObligation(node, obligation);
            return;
        }

        // ════════════════════════════════════════════════════════════════════════
        // Self-healing: redeploy isolated DHT VMs once bootstrap peers exist.
        //
        // DHT VMs deployed with 0 bootstrap peers are network-isolated — they
        // can't discover other nodes via Kademlia (requires ≥1 peer) and mDNS
        // only works same-subnet. Once other DHT nodes come online and their
        // PeerIds are captured, redeploy the isolated VM so it boots with real
        // bootstrap peers and joins the network.
        //
        // Safeguards:
        //   - Only triggers when BootstrapPeerCount == 0 (won't touch connected VMs)
        //   - Requires ≥2 min Active age (gives PeerId extraction time to work)
        //   - GetBootstrapPeersAsync excludes this node, so the genesis node
        //     won't redeploy until a second node's PeerId is captured
        //   - ResetObligation clears DhtInfo, removing this node from the peer
        //     list while it's being redeployed — prevents cascade (other nodes
        //     won't see stale peers and won't themselves redeploy simultaneously)
        // ════════════════════════════════════════════════════════════════════════
        if (obligation.Role == SystemVmRole.Dht
            && node.DhtInfo != null
            && node.DhtInfo.BootstrapPeerCount == 0
            && obligation.ActiveAt.HasValue
            && (DateTime.UtcNow - obligation.ActiveAt.Value).TotalMinutes >= 2)
        {
            var availablePeers = await _dhtNodeService.GetBootstrapPeersAsync(excludeNodeId: node.Id);
            if (availablePeers.Count > 0)
            {
                _logger.LogInformation(
                    "DHT VM {VmId} on node {NodeId} was deployed with 0 bootstrap peers but " +
                    "{PeerCount} peer(s) are now available — redeploying to join the network",
                    obligation.VmId, node.Id, availablePeers.Count);

                await RedeployDhtVmAsync(node, obligation, "Redeploying isolated DHT VM — bootstrap peers now available");
                return;
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // Self-healing: redeploy DHT VMs deployed with wrong advertise IP.
        //
        // If a CGNAT node's DHT VM was deployed before relay assignment, it got
        // the public IP as dht-advertise-ip instead of the WireGuard tunnel IP.
        // The VM itself must be redeployed because the advertise IP is baked into
        // cloud-init — other peers in the routing table have the wrong address.
        //
        // The heartbeat path corrects DhtInfo.ListenAddress, but the running VM
        // still advertises the wrong IP in the libp2p DHT. Only a redeploy fixes it.
        //
        // Safeguards:
        //   - Only triggers when ListenAddress doesn't match GetAdvertiseIp()
        //   - Requires ≥3 min Active age (gives CGNAT relay assignment time)
        //   - Won't fire for nodes without CgnatInfo (public nodes always match)
        // ════════════════════════════════════════════════════════════════════════
        if (obligation.Role == SystemVmRole.Dht
            && node.DhtInfo != null
            && obligation.ActiveAt.HasValue
            && (DateTime.UtcNow - obligation.ActiveAt.Value).TotalMinutes >= 3)
        {
            var expectedIp = DhtNodeService.GetAdvertiseIp(node);
            var expectedAddr = $"{expectedIp}:{DhtNodeService.DhtListenPort}";
            if (node.DhtInfo.ListenAddress != expectedAddr)
            {
                _logger.LogWarning(
                    "DHT VM {VmId} on node {NodeId} has wrong advertise IP: " +
                    "current={Current}, expected={Expected} — redeploying with correct address",
                    obligation.VmId, node.Id, node.DhtInfo.ListenAddress, expectedAddr);

                await RedeployDhtVmAsync(node, obligation,
                    $"Redeploying DHT VM — wrong advertise IP ({node.DhtInfo.ListenAddress} → {expectedAddr})");
                return;
            }
        }

        // NOTE: A "0 connected peers despite having bootstrap peers" self-healing
        // check was removed here. It caused a destructive loop: ConnectedPeers
        // defaults to 0 and is only populated when the node agent StatusMessage
        // includes "connectedPeers=N" — which many deployments never report.
        // Each false-positive redeployment transitioned the running VM to Deleting
        // (stripping ingress domains), then created a new VM record that the node
        // agent rejected as a duplicate. The new DhtInfo also had ConnectedPeers=0,
        // re-triggering the loop every 5 minutes.

        // VM exists and is not in error — still converged
    }

    /// <summary>
    /// Delete a DHT VM and reset its obligation for redeployment.
    /// Shared by all DHT self-healing paths.
    /// </summary>
    private async Task RedeployDhtVmAsync(Node node, SystemVmObligation obligation, string reason)
    {
        try
        {
            var lifecycle = _serviceProvider.GetRequiredService<IVmLifecycleManager>();
            await lifecycle.TransitionAsync(obligation.VmId, VmStatus.Deleting,
                TransitionContext.Manual(reason));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete old DHT VM {VmId} on node {NodeId} — will retry next cycle",
                obligation.VmId, node.Id);
            return;
        }

        ResetObligation(node, obligation);
    }

    /// <summary>
    /// Reset an obligation back to Pending so the reconciliation loop
    /// will redeploy it. Clears role-specific info that is stale.
    /// </summary>
    private void ResetObligation(Node node, SystemVmObligation obligation)
    {
        obligation.Status = SystemVmStatus.Pending;
        obligation.VmId = null;
        obligation.ActiveAt = null;
        obligation.DeployedAt = null;

        // Clear stale role-specific info so deployment creates fresh state
        switch (obligation.Role)
        {
            case SystemVmRole.Dht:
                node.DhtInfo = null;
                break;
            case SystemVmRole.Relay:
                node.RelayInfo = null;
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Deployment dispatch (role → service)
    // ════════════════════════════════════════════════════════════════════════

    private async Task<string?> DeploySystemVmAsync(
        Node node, SystemVmRole role, CancellationToken ct)
    {
        return role switch
        {
            SystemVmRole.Relay => await DeployRelayVmAsync(node, ct),
            SystemVmRole.Dht => await DeployDhtVmAsync(node, ct),
            SystemVmRole.BlockStore => await DeployBlockStoreVmAsync(node, ct),
            SystemVmRole.Ingress => await DeployIngressVmAsync(node, ct),
            _ => throw new ArgumentException($"Unknown system VM role: {role}")
        };
    }

    private async Task<string?> DeployRelayVmAsync(Node node, CancellationToken ct)
    {
        // Delegate to existing RelayNodeService
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _relayNodeService.DeployRelayVmAsync(node, vmService, ct);
    }

    private async Task<string?> DeployDhtVmAsync(Node node, CancellationToken ct)
    {
        var vmService = _serviceProvider.GetRequiredService<IVmService>();
        return await _dhtNodeService.DeployDhtVmAsync(node, vmService, ct);
    }

    private Task<string?> DeployBlockStoreVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement BlockStore VM deployment when BlockStoreService is available
        _logger.LogDebug("BlockStore VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }

    private Task<string?> DeployIngressVmAsync(Node node, CancellationToken ct)
    {
        // TODO: Implement Ingress VM deployment when IngressVmService is available
        _logger.LogDebug("Ingress VM deployment not yet implemented for node {NodeId}", node.Id);
        return Task.FromResult<string?>(null);
    }
}
