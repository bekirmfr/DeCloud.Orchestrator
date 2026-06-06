# Block Store System VM — Design & Implementation Plan

**Date:** 2026-02-16 (Updated: 2026-06-05)
**Status:** Phase A–J complete. Mesh-driven self-healing replication active. Phase J (2026-06-05) supersedes Phase I — replacing `drive-backup` with `blockdev-add` + `blockdev-snapshot` as the snapshot primitive on QEMU 8.2.2/libvirt. Phase I's `drive-backup sync=full` was structurally incapable of producing temporally coherent snapshots on active guests (confirmed via live debugfs/e2fsck diagnostics on msi-1867 — see §6.1.8). Phase J captures a coherent point-in-time snapshot atomically inside a sub-second freeze window; every confirmed manifest version is an application-consistent snapshot with no COW race possible. Planned and unplanned recovery produce identical correctness guarantees, differing only in RPO. The reconstruction path on the target does not depend on the base image being present. Items 39–41 (network fencing, assignment version, integration test) optional. **See §6.1.8 for the Phase J model; §6.1.7 documents the superseded Phase I model; §6.1.6 documents the original overlay-only model for historical context.**
## Implementation Status

| Phase | Status | Notes |
|-------|--------|-------|
| A — Orchestrator core | ✅ Complete | BlockStoreService, BlockStoreController, labels, eligibility |
| B — NodeAgent + Go binary | ✅ Complete | cloud-init, blockstore-node, scripts, callback controller |
| C — Integration | ✅ Production-verified 2026-03-20 | Dashboard live, binary build pipeline stable |
| D — Lazysync & Migration | ✅ Production-verified 2026-04-20 | Items 25–37: lazysync daemon, LazysyncManager, migration planner, source-offline alerting, disk reconstruction, end-to-end test confirmed. Item 32 (DHT proximity endpoint) implemented. All items complete. |
| D-fixes — Replication reliability | ✅ Production-verified 2026-04-08 | Overlay-only fix, bitswap peer targeting, concurrency cap, port firewall, retry queue |
| E — Split-Brain Prevention | ✅ Complete | Items 34–38 done. InvalidVmIds push model + TargetNodeId pre-check + owner block cleanup + manifest POST NodeId fence. Items 39–41 optional. |
| F — Self-Healing Replication | ✅ Complete 2026-05-31 | Phases F-A through F-D. RF on the wire (owner meta), reactive repair via `needs-replica` topic, proactive presence-loss + survey loops, orchestrator audit demoted to 6h sentinel, migration pre-flight DHT walk. Mesh handles eviction, replica-offline, and silent-wipe failures without orchestrator polling. |
| G — Overlay Snapshot Consistency | ✅ Complete 2026-06-03 | Application-consistent capture via `guest-fsfreeze-freeze`/`thaw` around qcow2 reads. Closes the torn-write failure mode exposed 2026-05-31 (`.pyc` zero-tail). Validated: msi-a4b5 migration — `libnetplan.cpython-311.pyc` byte-identical source-to-target, cloud-init `init-local` completed cleanly. See §6.1.4. |
| H — Bounded-Freeze Capture + Reconstruction Hardening | ✅ Complete 2026-06-03 | Three sub-items: (1) Replace `qemu-img convert --force-share` with `drive-backup sync=top` — bounded freeze window, lazysync trails 9–15 s instead of pausing for full convert; (2) Remove `RunFsckOnOverlayAsync` — `fsck.ext4 -fy` on crash-consistent overlays caused inode-corruption-by-resolution; ext4 journal replay at mount time is the correct mechanism; (3) Blockstore pre-flight and owner-indexing hardened — `/blocks/has` endpoint replaces `/owners/{vmId}` ownership-index check, owner indexing added to all four block-arrival paths. See §6.1.5. |
| I — Full-Disk Snapshot Replication | ⚠️ Superseded by Phase J | `drive-backup sync=full` inside per-cycle guest-fsfreeze. Confirmed structurally incapable of producing temporally coherent snapshots on active guests: COW reads clusters sequentially over tens of seconds unfrozen; coupled ext4 metadata structures captured at different wall-clock moments produce temporal incoherence. Diagnosed live on msi-1867 (2026-06-04): missing journal transactions, corrupt inode bitmaps, unbootable VM. See §6.1.7, §6.1.8. |
| J — blockdev-snapshot-sync (Correct Coherence Model) | ✅ Complete 2026-06-05 | Replaces `drive-backup` with `blockdev-add` + `blockdev-snapshot`. Atomically redirects guest writes to a new overlay inside a sub-second freeze — original disk is immutable and coherent at the exact freeze moment, no COW race possible. Confirmed working sequence on QEMU 8.2.2/libvirt established via live QMP testing. End-to-end migration verified: clean boot, no EXT4 errors, user data intact, browser terminal functional, stale-copy cleanup correct. Bootcmd D-Bus hang fixed in migration cloud-init. See §6.1.8. |
**Depends on:** DHT system VMs (production-verified 2026-02-15)**Depends on:** DHT system VMs (production-verified 2026-02-15)
**Follows patterns from:** Relay VMs, DHT VMs

---

## 1. Vision & Purpose

The Block Store system VM creates the **distributed storage backbone** for the DeCloud network. Every node with ≥100 GB storage contributes **5% of its total storage** as a network duty — forming a collective, content-addressed storage medium across the platform.

The primary purpose is **VM resilience and live migration**. When a node goes offline, its VMs can be rescheduled to another node because VM disk state is continuously replicated across the block store network via **lazysync** — a background process that streams dirty disk chunks to the network as they change. No single node failure causes data loss.

Each Block Store VM:

1. **Stores replicated blocks** — Holds content-addressed chunks of OTHER nodes' VM disk state
2. **Announces blocks** — Publishes provider records to the DHT ("I have block X")
3. **Transfers blocks** — Serves blocks to other nodes via libp2p bitswap protocol
4. **Pulls blocks autonomously** — Nodes close to a block's CID in Kademlia XOR space pull and store it via bitswap, with no orchestrator direction
5. **Garbage collects** — Local LRU eviction within the 5% budget; orchestrator audits provider counts

This enables continuous VM disk replication, live migration on node failure, template image distribution, and eventually a full decentralized filesystem — all without centralized cloud storage. Replication is fully decentralized via Kademlia's natural scatter properties; the orchestrator acts as an auditor, not a coordinator.

---

## 2. Architecture Overview

```
┌──────────────────────────────────────────────────────────────────┐
│                    Orchestrator                                    │
│  ┌──────────────────────┐  ┌───────────────────────────────────┐ │
│  │ BlockStoreService     │  │ BlockStoreController              │ │
│  │ - DeployBlockStoreVm  │  │ POST /api/blockstore/join         │ │
│  │ - ScheduleMigration() │  │ POST /api/blockstore/announce     │ │
│  │ - AuditReplication()  │  │ GET  /api/blockstore/locate/{cid} │ │
│  └──────────────────────┘  │ POST /api/blockstore/manifest      │ │
│                              │ GET  /api/blockstore/audit/{vmId}  │ │
│  ┌──────────────────────┐  │ GET  /api/blockstore/stats         │ │
│  │ LazysyncManager       │  └───────────────────────────────────┘ │
│  │ - Manifest versioning  │                                        │
│  │ - Version audit        │  Node.BlockStoreInfo:                 │
│  │ - Provider count check │  { VmId, PeerId, Capacity,           │
│  └──────────────────────┘    Used, Status }                      │
└───────────────────┬──────────────────────────────────────────────┘
                    │ CreateVm command (labels)
                    ▼
┌──────────────────────────────────────────────────────────────────┐
│                        Node Agent                                  │
│  ┌─────────────────────────┐  ┌────────────────────────────────┐ │
│  │ CommandProcessorService  │  │ BlockStoreCallbackController   │ │
│  │ - Renders cloud-init     │  │ POST /api/blockstore/ready     │ │
│  │ - Substitutes labels     │  │ (backup registration)          │ │
│  └─────────────────────────┘  └────────────────────────────────┘ │
└───────────────────┬──────────────────────────────────────────────┘
                    │ VM boots
                    ▼
┌──────────────────────────────────────────────────────────────────┐
│              Block Store VM (Debian 12 minimal)                    │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │  blockstore-node (Go binary)                                  │ │
│  │  - libp2p host (reuses DHT identity pattern)                  │ │
│  │  - Bitswap protocol for block exchange                        │ │
│  │  - Content-addressed FlatFS block storage                     │ │
│  │  - DAG/manifest support (Merkle DAGs over block collections)  │ │
│  │  - HTTP API on 127.0.0.1:5090                                 │ │
│  │    GET  /health                                                │ │
│  │    POST /blocks         (put block → returns CID)              │ │
│  │    GET  /blocks/{cid}   (get block)                            │ │
│  │    DELETE /blocks/{cid} (delete block)                         │ │
│  │    GET  /blocks         (list local blocks)                    │ │
│  │    POST /dag            (put manifest + blocks atomically)     │ │
│  │    GET  /dag/{cid}      (resolve DAG, return manifest)         │ │
│  │    GET  /stats          (storage usage stats)                  │ │
│  │    POST /gc             (run garbage collection, LRU eviction) │ │
│  └──────────────────────────────────────────────────────────────┘ │
│                                                                    │
│  ┌──────────────┐  ┌──────────────────┐  ┌───────────────────┐   │
│  │ WireGuard     │  │ Bootstrap Poll   │  │ Dashboard         │   │
│  │ wg-mesh       │  │ (orchestrator    │  │ (Python, port     │   │
│  │ (same mesh    │  │  peer discovery) │  │  8080 → Nginx 80) │   │
│  │  as DHT VM)   │  │                  │  │                   │   │
│  └──────────────┘  └──────────────────┘  └───────────────────┘   │
└──────────────────────────────────────────────────────────────────┘
```

### How It Connects to the DHT

The Block Store VM does **not** run its own DHT. Instead, it:

1. **Connects to the co-located DHT VM** via the WireGuard mesh
2. Uses **libp2p bitswap** for block exchange between block store peers
3. Uses the **DHT's provider records** to announce "I have CID X" (via the DHT's Kademlia routing)
4. Discovers other block store peers through the DHT network
5. **Subscribes to GossipSub** topic `decloud/blockstore/new-blocks` for near-instant notification of new blocks
6. **Pulls blocks autonomously** — evaluates XOR distance to announced CIDs, pulls via bitswap if close enough (adaptive threshold based on local capacity)
7. **Queries DHT proximity** — uses `GET /proximity/{cid}` on the co-located DHT VM for neighborhood scans and diagnostics

This keeps the DHT VM lightweight (just routing + discovery) while the Block Store VM handles heavy storage I/O. The DHT's Kademlia routing naturally scatters blocks across the network — no central coordinator decides which node stores which block.

---

## 3. System VM Lifecycle (Following Existing Patterns)

### 3.1 Obligation & Eligibility — 5% Storage Duty

Every node with sufficient resources has a **storage duty** — a mandatory contribution to the network's distributed block store. This is the same obligation pattern as relay and DHT duties.

Already stubbed in `ObligationEligibility.cs`:

```
Eligible if:
  - Total storage >= 100 GB (MinBlockStoreStorage)
  - Total RAM >= 2 GB (MinBlockStoreRam)
  - DHT obligation Active (dependency in SystemVmDependencies)
```

Storage allocated to the Block Store VM: **5% of the node's total storage**. This is a fixed duty — not configurable per node. The 5% forms a baseline storage medium across the entire platform.

```
Examples:
  Node with 100 GB storage  → 5 GB block store duty
  Node with 1 TB storage    → 50 GB block store duty
  Node with 4 TB storage    → 200 GB block store duty

  Network of 1,000 nodes averaging 1 TB each → 50 TB aggregate block store
```

The block store on each node holds **replicated overlay chunks from OTHER nodes' VMs** — not the node's own data. A node's own VM overlay blocks are pushed to its local block store by lazysync, then scatter across the network via Kademlia's natural propagation.

### 3.2 Deployment Flow

```
1. Orchestrator: ObligationEligibility computes BlockStore obligation
2. Reconciliation loop: TryDeployAsync checks DHT dependency is Active
3. BlockStoreService.DeployBlockStoreVmAsync():
   a. Calculate storage allocation: 5% of node.TotalStorage
   b. Resolve WireGuard mesh labels (same as DHT)
   c. Generate auth token (32-byte random)
   d. Collect bootstrap block store peers
   e. Create VM with VmType.BlockStore + labels
   f. Store BlockStoreInfo on node
4. NodeAgent: Receives CreateVm, renders blockstore-vm-cloudinit.yaml
5. VM boots: WireGuard mesh enrollment, binary starts, calls /api/blockstore/join
6. Orchestrator: Registers PeerId, returns bootstrap peers
```

### 3.3 Labels (Orchestrator → NodeAgent → VM)

```
role                    = "blockstore"
blockstore-listen-port  = "5001"          # libp2p bitswap port
blockstore-api-port     = "5090"          # localhost HTTP API
blockstore-storage-bytes = "53687091200"  # 5% of 1 TB node = ~50 GB
blockstore-auth-token   = "<base64>"      # HMAC secret
blockstore-advertise-ip = "10.20.1.202"   # WireGuard tunnel IP
node-region             = "us-east"
node-id                 = "node-abc123"
architecture            = "x86_64"
# WireGuard labels (same as DHT):
wg-relay-endpoint       = "..."
wg-relay-pubkey         = "..."
wg-tunnel-ip            = "..."
wg-relay-api            = "..."
```

### 3.4 Authentication (Same Pattern as DHT)

```
Orchestrator → VM:  auth token via labels → cloud-init
VM → Orchestrator:  HMAC-SHA256(authToken, nodeId:vmId) via X-BlockStore-Token header
VM → NodeAgent:     HMAC-SHA256(machineId, vmId:peerId) via X-BlockStore-Token header
```

---

## 4. Changes Required

### 4.1 Orchestrator Changes

#### NEW: `src/Orchestrator/Models/BlockStoreVmSpec.cs`

```csharp
public static class BlockStoreVmSpec
{
    /// <summary>
    /// Block Store node — lightweight VM for the 5% storage duty.
    /// libp2p + bitswap + FlatFS uses ~150-250 MB RAM at steady state.
    /// Disk is the primary resource — fixed at 5% of node's total storage.
    /// </summary>
    public const double StorageDutyFraction = 0.05;  // 5% of total node storage

    public static VmSpec Create(long nodeStorageTotalBytes) => new()
    {
        VmType = VmType.BlockStore,
        VirtualCpuCores = 1,
        MemoryBytes = 512L * 1024 * 1024,                             // 512 MB
        DiskBytes = (long)(nodeStorageTotalBytes * StorageDutyFraction), // 5% duty
        QualityTier = QualityTier.Burstable,
        ImageId = "debian-12-blockstore",
        ComputePointCost = 1,
    };

    public const long MinNodeStorageBytes = 100L * 1024 * 1024 * 1024;  // 100 GB eligibility threshold
}
```

#### NEW: `src/Orchestrator/Services/BlockStoreService.cs`

Interface & service following `IDhtNodeService` / `DhtNodeService` pattern:

```csharp
public interface IBlockStoreService
{
    // Deployment
    Task<string?> DeployBlockStoreVmAsync(Node node, IVmService vmService, CancellationToken ct = default);
    Task<List<string>> GetBootstrapPeersAsync(string? excludeNodeId = null);

    // Lazysync manifest lifecycle
    Task<ManifestRecord> RegisterManifestAsync(string vmId, string rootCid, int version,
        List<string> changedBlockCids, long totalBytes, CancellationToken ct = default);

    // Replication audit (not coordination — the DHT handles replication)
    Task<ReplicationAudit> AuditManifestReplicationAsync(string vmId, int version, CancellationToken ct = default);

    // Migration support
    Task<MigrationPlan> PlanMigrationAsync(string vmId, List<string> candidateNodeIds, CancellationToken ct = default);

    // Stats
    Task<BlockStoreStats> GetNetworkStatsAsync();
}
```

Key responsibilities:
- Calculate storage allocation: 5% of node's total storage
- Resolve WireGuard mesh labels (reuse DhtNodeService pattern)
- Generate auth token + labels, deploy VM via VmService
- Store `BlockStoreInfo` on Node model
- **Manifest lifecycle**: register evolving manifests from lazysync, track confirmed vs current version
- **Replication audit**: query the DHT for provider counts per chunk CID; advance `confirmedVersion` when all chunks in a manifest version have ≥N providers. The orchestrator does not direct replication — Kademlia handles scatter natively
- **Migration planning**: given a VM to migrate, rank candidate nodes by scheduling fit (CPU, RAM, GPU, region, affinity) and resource headroom. Block locality is not a factor — the target node fetches overlay blocks via bitswap from scattered providers

#### NEW: `src/Orchestrator/Controllers/BlockStoreController.cs`

Following `DhtController.cs` pattern:

```
POST /api/blockstore/join
  Request:  { nodeId, vmId, peerId, capacityBytes, usedBytes }
  Auth:     X-BlockStore-Token: HMAC-SHA256(authToken, nodeId:vmId)
  Response: { success, bootstrapPeers, peerIdRegistered }

POST /api/blockstore/announce
  Request:  { nodeId, vmId, cids: ["bafk..."], action: "add"|"remove" }
  Auth:     X-BlockStore-Token
  Response: { success, recorded: count }
  Purpose:  Batch announce/withdraw CIDs (orchestrator maintains secondary index;
            the DHT is the primary source of truth for provider records)

GET /api/blockstore/locate/{cid}
  Response: { cid, providers: [{ nodeId, peerId, multiaddr }],
              replication: count }
  Purpose:  Find which nodes have a specific block. Queries the DHT's
            provider records. Used for audit and diagnostics.

POST /api/blockstore/manifest
  Request:  { vmId, nodeId, rootCid, version, changedBlockCids: ["bafk..."], totalBytes }
  Auth:     Internal (nodeId must match vm.NodeId — see Section 7 for fencing)
  Response: { success, manifestVersion }
  Purpose:  Register an updated VM manifest from a lazysync cycle.
            Orchestrator records the new version. No replication plan is
            returned — blocks replicate via Kademlia scatter autonomously.

GET /api/blockstore/manifest/{vmId}
  Response: { vmId, currentVersion, confirmedVersion, confirmedRootCid,
              timestamp, replicationFactor: 3,
              replicationStatus: { providerCounts: { "bafk...": 12, ... } } }
  Purpose:  Query manifest version and replication status for a VM.
            replicationFactor = per-VM configurable N (default 3, 0 = ephemeral).
            confirmedVersion = latest version where ALL chunks have ≥N
            providers in the DHT.
            currentVersion = latest registered (may still be propagating).

GET /api/blockstore/audit/{vmId}
  Response: { vmId, version, totalChunks, chunksWithSufficientProviders,
              underReplicatedChunks: [{ cid, providerCount }] }
  Purpose:  Detailed replication health for a specific VM's manifest.
            Queries FindProviders for each chunk CID. Used by the
            orchestrator's background audit loop to advance confirmedVersion
            and detect under-replication.

GET /api/blockstore/stats
  Response: { totalNodes, totalCapacity, totalUsed, totalBlocks,
              avgReplication, manifestCount, totalReplicatedBytes }
  Purpose:  Network-wide storage metrics
```

#### MODIFY: `src/Orchestrator/Models/Node.cs`

Add `BlockStoreInfo` class (alongside existing `DhtNodeInfo`, `RelayNodeInfo`):

```csharp
public class BlockStoreInfo
{
    public string BlockStoreVmId { get; set; }
    public string? PeerId { get; set; }                 // libp2p peer ID
    public string ListenAddress { get; set; }           // IP:5001
    public int ApiPort { get; set; } = 5090;
    public long CapacityBytes { get; set; }             // Allocated storage (5% of node)
    public long UsedBytes { get; set; }                 // Currently used
    public int BlockCount { get; set; }                 // Local blocks stored (determined by Kademlia proximity)
    public BlockStoreStatus Status { get; set; } = BlockStoreStatus.Initializing;
    public DateTime? LastHealthCheck { get; set; }
}

public enum BlockStoreStatus { Initializing, Active, Degraded, Full, Offline }
```

Add `BlockStoreInfo? BlockStoreInfo` property to the Node class.

Note: Nodes no longer receive pinning commands from the orchestrator. Which blocks a node stores is determined by Kademlia XOR proximity — nodes pull and serve blocks whose CIDs are close to their peer ID in XOR space. GC is local LRU eviction within the 5% budget.

#### MODIFY: `src/Orchestrator/Models/VirtualMachine.cs`

Add `BlockStore` to VmType enum:

```csharp
public enum VmType
{
    General, Compute, Memory, Storage, Gpu, Relay, Dht, Inference,
    BlockStore    // NEW
}
```

#### MODIFY: `src/Orchestrator/Services/SystemVm/SystemVmReconciliationService.cs`

Wire up `DeployBlockStoreVmAsync`:

```csharp
private async Task<string?> DeployBlockStoreVmAsync(Node node, CancellationToken ct)
{
    var vmService = _serviceProvider.GetRequiredService<IVmService>();
    return await _blockStoreService.DeployBlockStoreVmAsync(node, vmService, ct);
}
```

#### MODIFY: `src/Orchestrator/Services/SystemVm/ObligationEligibility.cs`

Uncomment the BlockStore eligibility block.

#### MODIFY: `src/Orchestrator/Services/SystemVm/SystemVmLabelSchema.cs`

Add required labels for `VmType.BlockStore`:

```csharp
[VmType.BlockStore] = [
    "role",
    "blockstore-listen-port",
    "blockstore-api-port",
    "blockstore-storage-bytes",
    "blockstore-auth-token",
    "blockstore-advertise-ip",
    "node-region",
    "node-id",
],
```

#### MODIFY: `src/Orchestrator/Services/VmService.cs`

Add `VmType.BlockStore` to system VM detection:

```csharp
var isSystemVm = request.VmType is VmType.Relay or VmType.Dht or VmType.BlockStore;
```

#### MODIFY: `src/Orchestrator/Program.cs`

Register BlockStoreService in DI.

---

### 4.2 Node Agent Changes

#### MODIFY: `VmType` enum in `VmModels.cs`

Add `BlockStore` variant to match Orchestrator.

#### NEW: `src/DeCloud.NodeAgent/CloudInit/Templates/blockstore-vm-cloudinit.yaml`

Cloud-init template following `dht-vm-cloudinit.yaml` pattern:

**Key components:**
- `blockstore` unprivileged user
- WireGuard mesh enrollment (same as DHT VM)
- `blockstore-node` Go binary (gzip+base64 injected)
- Systemd service for the binary
- Bootstrap poll service (polls `POST /api/blockstore/join`)
- Ready callback service (backup path via NodeAgent)
- Nginx reverse proxy for dashboard
- Health check script
- qemu-guest-agent for orchestrator monitoring

**Environment file** (`/etc/decloud-blockstore/blockstore.env`):
```bash
BLOCKSTORE_LISTEN_PORT=__BLOCKSTORE_LISTEN_PORT__
BLOCKSTORE_API_PORT=__BLOCKSTORE_API_PORT__
BLOCKSTORE_ADVERTISE_IP=__WG_TUNNEL_IP__
BLOCKSTORE_STORAGE_BYTES=__BLOCKSTORE_STORAGE_BYTES__
BLOCKSTORE_DATA_DIR=/var/lib/decloud-blockstore
DECLOUD_NODE_ID=__NODE_ID__
DECLOUD_REGION=__DHT_REGION__
```

#### NEW: `src/DeCloud.NodeAgent/CloudInit/Templates/blockstore-vm/`

Directory containing:

```
blockstore-vm/
├── blockstore-node-src/
│   ├── main.go                  # Block store binary
│   ├── go.mod
│   └── go.sum
├── blockstore-bootstrap-poll.sh # Polls orchestrator for peers
├── blockstore-notify-ready.sh   # Callback to NodeAgent
├── blockstore-health-check.sh   # Health check for monitoring
└── blockstore-dashboard.py      # Dashboard server (Python)
```

#### NEW: `blockstore-node-src/main.go` — Block Store Binary

Go binary using libp2p with bitswap:

```go
// Key dependencies:
// - github.com/libp2p/go-libp2p          (networking)
// - github.com/ipfs/go-bitswap           (block exchange)
// - github.com/ipfs/go-datastore         (block storage backend)
// - github.com/ipfs/go-ds-flatfs         (flat file storage)
// - github.com/ipfs/go-cid               (content identifiers)
// - github.com/multiformats/go-multihash (hashing)

Features:
1. libp2p host with persistent Ed25519 identity
2. Bitswap client+server for block exchange
3. FlatFS backend for block storage (content-addressed flat files)
4. Storage quota enforcement (refuse writes when full)
5. Garbage collection (LRU eviction within 5% budget)
6. GossipSub subscription (`decloud/blockstore/new-blocks`) for near-instant block discovery
7. Adaptive XOR threshold pull logic (closer distance + more free space → more aggressive pulling)
8. Periodic DHT neighborhood scan (durable fallback for missed GossipSub messages)
9. Localhost HTTP API on port 5090
10. Provider record announcement via DHT connection
```

**HTTP API (localhost only, port 5090):**

```
GET  /health
  → { status, peerId, connectedPeers, blockCount, usedBytes,
      capacityBytes, usagePercent }

POST /blocks
  Body: raw bytes (Content-Type: application/octet-stream)
  → { cid: "bafk...", size: 1234, stored: true }

GET  /blocks/{cid}
  → raw bytes (Content-Type: application/octet-stream)
  Note: If not local, attempts bitswap retrieval from network

DELETE /blocks/{cid}
  → { cid, deleted: true }

GET  /blocks
  Query: ?offset=0&limit=100
  → { blocks: [{ cid, size, lastAccess, createdAt }], total: 1234 }

POST /dag
  Body: JSON { manifest: { type, vmId?, parentCid?, chunks: [{ offset, cid }] },
               blocks: { "cid": "<base64 bytes>", ... } }
  → { rootCid: "bafk...", blockCount: N, totalBytes: M, stored: true }
  Note: Stores a Merkle DAG manifest + referenced blocks atomically.
        Used for VM overlay manifests.

GET  /dag/{cid}
  → { rootCid, manifest: { type, vmId, chunks: [...] },
      localBlocks: N, totalBlocks: M, complete: bool }
  Note: Returns the manifest and reports how many of its referenced
        blocks are available locally (complete=true when all present).

GET  /stats
  → { capacityBytes, usedBytes, usagePercent, blockCount,
      connectedPeers, bitswapSent, bitswapReceived }

POST /gc
  → { freedBytes, freedBlocks, remainingBytes }
  Note: Evicts least-recently-used blocks. Local LRU eviction —
        no orchestrator coordination needed.

POST /connect
  Body: { peers: ["/ip4/.../p2p/..."] }
  → { results, connected, total }
```

#### NEW: `src/DeCloud.NodeAgent/Controllers/BlockStoreCallbackController.cs`

Following `DhtCallbackController.cs` pattern:

```
POST /api/blockstore/ready
  Body: { vmId, peerId }
  Auth: X-BlockStore-Token: HMAC-SHA256(machineId, vmId:peerId)
```

#### MODIFY: `CommandProcessorService.cs`

Add `VmType.BlockStore` handling in `HandleCreateVmAsync`:
- Load `blockstore-vm-cloudinit.yaml` template
- Substitute labels into cloud-init variables
- Same WireGuard mesh enrollment pattern as DHT

---

### 4.3 Go Binary Design Details

#### Storage Backend: FlatFS

```
/var/lib/decloud-blockstore/
├── identity.key           # Persistent libp2p identity
├── blocks/                # FlatFS content-addressed storage
│   ├── _README            # Sharding info
│   ├── BA/                # First 2 chars of CID
│   │   └── FKREIG...      # Block files named by CID
│   └── QM/
│       └── ...
├── dags/                  # DAG manifests (JSON, indexed by root CID)
├── dynamic-peers          # Runtime peer injection (same as DHT)
└── datastore/             # LevelDB for metadata (block index, stats)
```

#### Block Lifecycle

```
1. POST /blocks (raw bytes)
   → SHA-256 hash → CIDv1 (raw codec, SHA2-256)
   → Write to FlatFS
   → Announce provider record to DHT (via connected DHT node)
   → Return CID

2. GET /blocks/{cid}
   → Check local FlatFS
   → If not found: bitswap request to network
   → Timeout after 30 seconds
   → Return bytes or 404

3. DELETE /blocks/{cid}
   → Delete from FlatFS
   → Withdraw provider record from DHT

4. POST /dag (manifest + blocks)
   → Store each block in FlatFS (deduplicated by CID)
   → Store manifest in dags/ directory
   → Manifest root CID = SHA-256 of manifest JSON
   → Return root CID

5. GC (local LRU eviction)
   → Sort blocks by last access time (LRU)
   → Delete least-recently-used blocks until usage is under 85% of capacity
   → Hard refuse writes at 95% capacity
   → Withdraw provider records for deleted blocks
   → Kademlia naturally re-replicates if provider count drops —
     other close nodes discover the gap and pull from remaining providers
```

#### DAG / Manifest Structure

A DAG manifest describes the current state of a VM's **overlay disk only** — not the full virtual disk. VMs use a qcow2 backing chain: a read-only base image (shared, downloadable from the image registry) plus a per-VM writable overlay that captures all writes. The base image and cloud-init configuration are reconstructible artifacts — only the overlay carries unique state.

The manifest is a single evolving document where chunk CIDs are replaced in-place as the lazysync daemon detects dirty blocks. There is no full/delta distinction. Just one manifest per VM that evolves over time:

```json
{
  "type": "vm-overlay",
  "vmId": "vm-abc123",
  "replicationFactor": 3,
  "version": 47,
  "timestamp": "2026-02-16T12:05:00Z",
  "baseImageId": "debian-12-generic",
  "baseImageHash": "sha256:a1b2c3...",
  "overlayVirtualSizeBytes": 107374182400,
  "blockSizeBytes": 1048576,
  "blockCount": 3,
  "chunks": [
    { "offset": 0,       "cid": "bafk...aaa" },
    { "offset": 1048576, "cid": "bafk...bbb" },
    { "offset": 2097152, "cid": "bafk...ccc" }
  ]
}
```

**Key properties:**
- **`type: "vm-overlay"`** — not `vm-disk`. This manifest represents the overlay layer only.
- **`baseImageId` + `baseImageHash`** — identifies the backing image. The target node downloads this from the image registry (or from block store peers that have it cached). The orchestrator knows the URL.
- **Sparse chunks** — only offsets with allocated clusters appear. A 100 GB virtual disk with 3 GB of overlay writes has ~3,000 chunks, not 100,000. Offsets not in the manifest read through to the base image.
- Cloud-init ISO is not in the manifest — the orchestrator regenerates it from the VM's labels during migration.
- **`blockCount`** — number of chunks in this manifest version. The billing unit:
  `storage_cost = blockCount × replicationFactor × costPerMbPerHour × (blockSizeBytes / 1_048_576)`.
  Updated each lazysync cycle. The orchestrator passes `blockCount` and
  `blockSizeKb` to `settleCycle()` — the contract applies the rate formula
  on-chain so billing math is fully trustless.
- **Tail block** — the final chunk of an overlay is almost never exactly
  `blockSizeBytes`. It is stored at its real size and billed as one full block.
  Maximum over-billing per VM: one block worth of storage. This avoids
  zero-padding waste while keeping block count as a clean billing integer.

The `version` field is a monotonically increasing integer, incremented on each lazysync cycle that produces changes. When the lazysync daemon detects dirty blocks, it:
1. Reads the dirty chunks (crash-consistent via QEMU incremental backup)
2. Hashes each chunk → CIDv1
3. Replaces the corresponding offset's CID in the manifest (if it actually changed)
4. Increments version
5. Pushes only the new blocks to the block store
6. Registers the updated manifest with the orchestrator

The orchestrator tracks two versions per VM:
- **currentVersion**: latest manifest registered (may be partially replicated)
- **confirmedVersion**: latest manifest where ALL referenced blocks have ≥N providers in the DHT (verified by audit loop)

Blocks referenced by a current manifest naturally maintain high provider counts (nodes keep re-announcing them, and they get recent bitswap access which protects them from LRU eviction). Blocks no longer in any current manifest stop being re-announced, their provider records expire, and they naturally disappear via LRU GC. This eliminates delta chains, delta consolidation, and the full/delta manifest type distinction entirely.

Content addressing provides automatic deduplication: if two VMs use the same base image and write the same packages, those overlay chunks share the same CID and are stored once. Unchanged overlay chunks across lazysync cycles share the same CID and are never re-stored or re-transferred.

#### Chunk Sizes by Manifest Type

`blockSizeBytes` is a **per-manifest-type constant** declared in the manifest and
enforced by the block store binary at write time. Different content types have
different optimal chunk sizes based on their access patterns, update frequency,
and scale. The billing unit is always **one block**, with cost normalized to
`costPerMbPerHour × (blockSizeBytes / 1_048_576)` — so all manifest types pay
the same rate per MB of replicated data.

| Manifest Type | `blockSizeBytes` | Rationale |
|---|---|---|
| `vm-overlay` | **1 MB** (1,048,576) | Aligns with QEMU dirty bitmap granularity. A VM with 5 GB overlay → ~5,120 blocks. Manageable manifest size. Unchanged regions produce identical CIDs — never re-transferred. |
| `model-shard` | **64 MB** (67,108,864) | Aligns with transformer layer boundaries for pipeline-parallel inference. Llama-3 70B Q4 (~40 GB) → 640 blocks. Bitswap transfers of 64 MB amortize connection overhead efficiently. |
| `lora-adapter` | **256 KB** (262,144) | Fine-grained deduplication across fine-tuned variants of the same base model — many adapters share base layer weights. |
| `image-template` | **4 MB** (4,194,304) | Base OS images (2–4 GB) → 500–1,000 blocks. Good deduplication across Ubuntu/Debian versions that share many 4 MB regions. |

**Chunk count reference for model-shard type:**

| Model | Precision | Size | 64 MB Chunks |
|---|---|---|---|
| Llama-3 8B | FP16 | ~16 GB | 256 |
| Llama-3 70B | Q4_K_M | ~40 GB | 640 |
| Llama-3 70B | FP16 | ~140 GB | 2,240 |
| Mistral 7B | Q4 | ~4 GB | 64 |
| GPT-4 scale | Q4 (est.) | ~1.8 TB | ~28,800 |

For the distributed AI inference vision: an inference VM needing layers 16–31
of a 70B model requests the ~7–8 specific 64 MB chunk CIDs covering those
layers. The block store network fetches them via bitswap from the nearest
providers. No full model download required before inference can begin.

**Block store enforcement:**
```go
// Enforced in blockstore-node main.go
// Each manifest type declares its block size; the binary validates on write
var ManifestTypeBlockSize = map[ResourceType]int64{
    ResourceTypeVMOverlay:     1 * 1024 * 1024,    // 1 MB
    ResourceTypeModelShard:    64 * 1024 * 1024,   // 64 MB
    ResourceTypeLoRAAdapter:   256 * 1024,          // 256 KB
    ResourceTypeImageTemplate: 4 * 1024 * 1024,    // 4 MB
}

// Tail block exception: final chunk may be smaller than blockSizeBytes.
// Billed as one full block. All other chunks must be exactly blockSizeBytes.
```

#### Bitswap Integration

The Go binary connects to other Block Store peers via libp2p and uses the bitswap protocol for block exchange:

```go
// Bitswap network: blocks flow between peers automatically
// When a peer requests a block we have → serve it
// When we need a block we don't have → request from peers
bswap := bitswap.New(ctx, network, blockstore)
```

Bitswap is the **transfer mechanism**; GossipSub + Kademlia provide the **discovery mechanism**. When a new block is pushed to a block store, the source publishes the CID to the `decloud/blockstore/new-blocks` GossipSub topic. Block store nodes that are close to the CID in Kademlia XOR space (and have capacity) add it to their bitswap want list and pull it automatically. For nodes that missed the GossipSub message, periodic DHT neighborhood scans serve as the durable fallback. No orchestrator commands are needed — blocks scatter across the network autonomously.

#### DHT Provider Records

The Block Store binary announces which blocks it holds to the DHT network. This uses the standard IPFS provider record mechanism — any DHT node can answer "who has CID X?".

```go
// Announce that we provide a block
routing.Provide(ctx, cid, true)

// Find providers for a block we need
providers := routing.FindProviders(ctx, cid)
```

---

## 5. Resource Specifications

### Block Store VM Sizing

| Tier | vCPUs | RAM | Disk | Eligibility |
|------|-------|-----|------|-------------|
| Standard | 1 | 512 MB | 5% of node storage | ≥100 GB total storage, ≥2 GB RAM |

Disk allocation formula:
```
allocatedStorage = node.TotalStorageBytes * 0.05    // 5% duty, fixed
```

No min/max caps needed — the 100 GB eligibility threshold ensures the smallest allocation is 5 GB (sufficient for the VM + a meaningful number of replicated blocks), and 5% naturally scales with larger nodes.

| Node Storage | Block Store Allocation | Typical Use |
|-------------|----------------------|-------------|
| 100 GB | 5 GB | ~5,000 blocks (1 MB each) |
| 500 GB | 25 GB | ~25,000 blocks |
| 1 TB | 50 GB | ~50,000 blocks |
| 4 TB | 200 GB | ~200,000 blocks |

### Network Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 5001 | TCP (libp2p) | Bitswap block exchange |
| 5090 | TCP (HTTP) | Localhost API (internal only) |
| 8080 | TCP (HTTP) | Dashboard (proxied via Nginx port 80) |

---

## 5.5 Replication Factor

### Default: All Tenant VMs at N=3

Every non-system VM is created with `replicationFactor=3` by default. System
VMs (Relay, DHT, BlockStore) use `replicationFactor=0` — they are ephemeral by
design, reconstructed from their cloud-init template on redeploy, not from disk
state.

On Phase D launch, every tenant VM is automatically enrolled in lazysync with no
user configuration required. The platform's resilience guarantee is uniform and
unconditional by default. Users shouldn't have to opt into data protection.

### Phase D+1: User-Configurable Replication Factor

When exposed as a VM creation parameter, the supported tiers are:

| Factor | Meaning | Durability | Use case |
|--------|---------|-----------|---------|
| 0 | Ephemeral | None — data lost on node failure | Stateless workloads, batch jobs, CI runners. User explicitly accepts data loss. |
| 1 | Single replica | Survives if ≥1 provider holds the blocks | Dev environments, cost-sensitive non-critical workloads |
| 3 | Standard (default) | Survives loss of 2 provider nodes simultaneously | All production workloads |
| 5 | High availability | Survives loss of 4 provider nodes simultaneously | Databases, ML training checkpoints, critical stateful services |

Factor 2 is valid but not exposed — the meaningful resilience jump is from 1 to 3
(tolerates 2 simultaneous failures). Exposing 2 creates confusion without adding
real value.

`replicationFactor` is **immutable after VM creation**. Changing it mid-life would
require seeding or withdrawing blocks across the network (a Phase D+2 feature). For
now: set at creation, honored by lazysync, used in billing.

### Model fields
```csharp
// VirtualMachine or VmSpec:
public int ReplicationFactor { get; set; } = 3;   // 0=ephemeral, 1/3/5=replicated

// Updated each lazysync cycle (for accurate billing):
public long LastKnownOverlayBytes { get; set; }
public DateTime? LastLazysyncAt { get; set; }
```

### Pricing Impact

Replication factor drives two cost components:

**1. Storage replication cost** — N=3 means 3× the overlay bytes consumed across the
block store network. The VM owner pays for the storage duty they impose on the
network. In practice, overlay is sparse: a 100 GB virtual disk typically has 2–8 GB
of allocated overlay after a few hours of use, so N=3 costs ~6–24 GB of distributed
storage, not 300 GB.

**2. Compute cost modifier** — lazysync I/O (dirty bitmap export + chunk push) on the
source node is small enough to fold into the storage cost rather than price separately.

**Formula:**
VM hourly cost = compute_cost + storage_replication_cost
compute_cost =
vCPUs × cpu_rate + memory_gb × memory_rate + disk_gb × storage_rate
storage_replication_cost =
blockCount × replicationFactor × costPerMbPerHour × (blockSizeKb / 1024)
Where:
costPerMbPerHour = platform constant (e.g. $0.000001/MB/hour)
blockCount       = number of chunks in the current confirmed manifest
(updated by LazysyncManager each audit cycle)
blockSizeKb      = chunk size for this manifest type (1024 for vm-overlay,
65536 for model-shard, 256 for lora-adapter, 4096 for image-template)
replicationFactor = 0 / 1 / 3 / 5 (immutable after VM creation)

**Why block-based pricing is better than GB-based:**

- **Deterministic** — `blockCount` is exact, not estimated. The manifest records
  exactly how many chunks exist at the current version.
- **Trustless on-chain** — `settleCycle()` receives `blockCounts[]`,
  `blockSizeKb[]`, and `replicationFactors[]` as raw inputs. The contract
  applies `costPerMbPerHour` itself. The orchestrator cannot inflate the
  storage amount — only the block count, which is verifiable from the manifest.
- **User-legible** — "Your VM has 3,847 blocks at 1 MB each, N=3 replicas,
  $0.000001/MB/hour = $0.01155/hour for replication" is actionable.
  "Your overlay is 3.7 GB" is not.
- **Scales correctly to AI workloads** — A 70B model shard at 640 × 64 MB
  blocks bills at the same rate-per-MB as a 5,120 × 1 MB VM overlay.
  No special-casing needed.

**Example costs at $0.000001/MB/hour:**

| Workload | Blocks | Block Size | N | Storage cost/hr |
|---|---|---|---|---|
| Typical VM, light use | 512 | 1 MB | 3 | $0.001536 |
| Typical VM, active | 4,096 | 1 MB | 3 | $0.012288 |
| Llama-3 70B Q4 | 640 | 64 MB | 3 | $0.122880 |
| Llama-3 70B FP16 | 2,240 | 64 MB | 5 | $0.716800 |
| Fine-tune adapter | 200 | 256 KB | 3 | $0.000015 |

`replicationFactor=0` is priced noticeably lower — enough to make it a genuine
option for stateless workloads, but not so cheap that users accidentally choose it
for stateful workloads without understanding the tradeoff.

### Scheduler Impact

When `replicationFactor > 0`, the scheduler adds one filter condition:
candidate node must have BlockStoreInfo.Status == Active
A node without an Active BlockStore VM cannot participate in lazysync for tenant VMs
running on it — there is nowhere to push dirty blocks locally before they scatter
across the network.

This means the scheduler naturally steers replicated workloads toward nodes with
≥100 GB storage (the BlockStore eligibility threshold). Nodes below this threshold
can only host ephemeral (`replicationFactor=0`) VMs.

### Implementation touchpoints (Phase D)

| Component | Change |
|-----------|--------|
| `VmService.CreateVmAsync` | Set `ReplicationFactor = 3` as default for all non-system VMs |
| `CreateVmRequest` | Add optional `ReplicationFactor` field (defaults to 3, validated: 0/1/3/5) |
| `VmSpec` / `VirtualMachine` | Add `ReplicationFactor`, `LastKnownOverlayBytes`, `LastLazysyncAt` |
| `VmSchedulingService` | Add BlockStore Active filter when `ReplicationFactor > 0` |
| `LazysyncDaemon` (NodeAgent) | Skip VM entirely if `replicationFactor == 0`; use factor as provider count threshold |
| `LazysyncManager` (Orchestrator) | Use `replicationFactor` as N in `confirmedVersion` audit loop |
| `CalculateHourlyRate` | Add `storage_replication_cost` component |
| Source-offline alerting | `replicationFactor=0` → LOST (by design, no alert); `>0` → UNRECOVERABLE / RECOVERING / MIGRATING |
| Dashboard | Show replication factor + "Ephemeral" badge when factor=0 |

### Phase D+1: Replication-Aware GC (Priority Eviction of Confirmed Blocks) — ✅ Implemented (extended in Phase F)

**Status:** The priority-eviction model below is implemented as designed. Phase F extended both GC paths
with `needs-replica` GossipSub signaling immediately before each eviction, and aligned the LRU path with
the confirmed-evict path by adding the previously-missing `dht.Provide(c, false)` provider-record
withdrawal. See §6.8 "Mesh-Driven Self-Healing" for the signaling layer.

**Context:** The local blockstore on the VM's hosting node is a transit buffer, not a
durable store. The VM overlay disk itself is the primary copy. The local blockstore
exists to stage blocks before they scatter to remote nodes via Kademlia. Once blocks
are confirmed replicated remotely (`remoteCount >= replicationFactor`), the local
copy is redundant from a durability standpoint.

**Current behavior:** Local GC is pure LRU — evicts by last access time regardless of
whether remote copies exist. This wastes the 5% duty on blocks that are already safe.

**Proposed GC priority order:**

```
1. Evict blocks confirmed remote (remoteCount >= RF, per last audit)
   → Safe: redundant copies exist on ≥N other nodes
   → Frees space for blocks still needing replication

2. Evict blocks not referenced by any current manifest
   → Orphaned chunks from old manifest versions — safe to evict anywhere

3. Evict by LRU (fallback)
   → May trigger Kademlia re-replication if provider count drops below RF
   → Current behavior
```

**Effect:** The local blockstore becomes a true **write-through cache** — it holds
blocks long enough for them to scatter, then evicts them to make room for new ones.
This maximises the useful throughput of the 5% duty rather than filling it with
already-safe data.

**Implementation touchpoints:**

| Component | Change |
|-----------|--------|
| `LazysyncManager` | After advancing `ConfirmedVersion`, write confirmed CID set to a per-VM confirmed-blocks file on the node (via orchestrator → node agent callback or node agent polling) |
| `blockstore-node main.go` | `runGC()`: before LRU pass, evict all CIDs present in the confirmed-blocks set for any VM. Withdraw provider record after eviction |
| `POST /api/blockstore/confirmed` | New NodeAgent endpoint: receives confirmed CID list from orchestrator after each audit cycle |
| Confirmed CIDs storage | Simple newline-delimited file per VM: `/var/lib/decloud-blockstore/confirmed/{vmId}.cids` — updated after each `ConfirmedVersion` advance |

**Note:** The local blockstore still serves bitswap requests for evicted blocks —
remote peers can fetch them from the remaining ≥N providers. Eviction only removes
the local copy; provider records are withdrawn so peers route requests elsewhere.

**Dependency:** Requires stable `ConfirmedVersion` audit loop (Phase D) and the
`reannounceExistingBlocks` startup fix (Phase D+1 — see item below).

### Phase D+1: Startup Re-announce (Provider Record Recovery) — ✅ Implemented

**Problem:** When the blockstore binary restarts, `dht.Provide()` is only called for
newly written blocks. Existing blocks on FlatFS are loaded into the LRU cache but
never re-announced. Provider records in the DHT expire after 24 hours — a restarted
blockstore becomes invisible to `FindProvidersAsync` until new blocks are written.

**Affected scenarios:**
- Binary restart / systemctl restart
- DHT VM redeployment (fresh LevelDB, all provider records wiped)
- Node agent update triggering blockstore restart
- 24h provider record TTL expiry with no new writes

**Fix:** Add `reannounceExistingBlocks()` goroutine to `main.go`, called after
`connectBootstrapPeers()` with a 15s delay to allow the Kademlia routing table
to populate first.

```go
func (n *BlockNode) reannounceExistingBlocks(ctx context.Context) {
    time.Sleep(15 * time.Second) // routing table warmup
    ch, err := n.bstore.AllKeysChan(ctx)
    if err != nil { return }
    count := 0
    for c := range ch {
        if ctx.Err() != nil { return }
        announceCtx, cancel := context.WithTimeout(ctx, 10*time.Second)
        _ = n.dht.Provide(announceCtx, c, true)
        cancel()
        count++
        if count%100 == 0 {
            log.Printf("reannounce: %d blocks announced", count)
        }
    }
    log.Printf("reannounce: complete — %d blocks re-announced", count)
}
```

**Insertion point in `main()`:**
```go
node.connectBootstrapPeers(ctx)
go node.reannounceExistingBlocks(ctx)  // ← add this line
```

### Phase D+1: DHT Neighborhood Scan (General Block Recovery) — ⚠ Superseded by Phase F Survey

**Resolution:** The neighborhood-scan cold path was superseded by Phase F's **owner-index survey**
(§6.8). The survey achieves the same goal — discovering under-replicated blocks the GossipSub
notification missed — but uses presence-mesh owner-index diffs instead of DHT walks. The owner index
is per-VM authoritative, locally cached, and refreshed on each 5-minute survey cycle, so the survey
runs at constant cost (1 HTTP call per peer per local VM) rather than scaling with DHT keyspace
breadth. The DHT proximity endpoint (item 32) is retained for diagnostics; it is no longer on a
durability code path.

The original neighborhood-scan plan is kept below for historical context.

**Problem:** The GossipSub fetch retry queue (item 31b) recovers blocks where the
notification arrived but the bitswap fetch failed. It cannot recover blocks where the
GossipSub message was missed entirely — node was offline, network partition, restart
during a seeding cycle, or the message was dropped by the ephemeral pub/sub layer.
In those cases, under-replicated blocks remain permanently undetected by the binary.

**Scope:** This is the general/cold recovery path. The retry queue is the hot path.
Together they eliminate both failure modes:

```
GossipSub notification received, fetch failed  → retry queue    (hot path)
GossipSub notification missed entirely         → neighborhood scan (cold path)
```

**Mechanism:** Periodically walk the local Kademlia neighborhood to discover blocks
that belong on this node (based on XOR proximity) but aren't present locally.

```
Every ScanInterval (e.g. 10 minutes):
  1. Walk the local Kademlia keyspace neighborhood
     → Use kadDHT.GetClosestPeers(ctx, myPeerId) to identify the keyspace
       range this node is responsible for
  2. For each CID in the neighborhood's provider records:
     → Call FindProviders(ctx, cid) — returns who has it
     → Check local bstore.Has(ctx, cid)
     → If not local AND within XOR threshold AND capacity allows:
         → GetBlock via bitswap from a provider
         → Store locally, announce to DHT
         → Log as neighborhood_pull
  3. Skip CIDs already in the retry queue (avoid double-fetching)
  4. Rate-limit: max N blocks per scan cycle to avoid I/O bursts
```

**Why the DHT alone doesn't do this:** The DHT is a passive lookup service. It
answers "who has block X?" but never decides "I should have block X." That decision
requires knowing the XOR proximity threshold and local capacity — context only the
block store binary has. The neighborhood scan is the bridge between the DHT's passive
records and the binary's active pull behavior.

**Implementation touchpoints (`main.go`):**
- Add `ScanInterval = 10 * time.Minute` constant
- Add `startNeighborhoodScanLoop(ctx)` goroutine launched from `main()`
- Uses `n.dht.GetClosestPeers()` + `n.dht.FindProvidersAsync()` for discovery
- Shares `n.fetchSem` with GossipSub fetches to bound total concurrency
- Logs `neighborhood_pull` and `neighborhood_scan_complete` diag events
- Skip CIDs present in `n.retryQueue` (check via a shadow `sync.Map`)

**Dependency:** Requires item 32 (DHT proximity endpoint) for the keyspace walk.

---

## 6. VM Disk Replication & Migration (Lazysync)

This is the **primary use case** for the block store — not a future feature.

### 6.1 Lazysync Model — Continuous Dirty Block Replication

Every user VM has its **disk continuously replicated** via a background lazysync process. There are no "snapshot events." Dirty disk clusters flow to the block store network as they change, and a single evolving manifest per VM tracks the current disk state.

Phase I (2026-06-04) captures **the full merged disk** at each cycle — the qcow2 backing chain is dereferenced inline by QEMU's block layer, producing a self-contained raw image at the QMP snapshot point. Reconstruction on the target needs only the manifest CIDs; no backing-file dependency, no base image download required on the migration path. The cloud-init ISO is still regenerated from the VM's labels. See §6.1.7 for the Phase I model in full and §6.1.6 for the superseded overlay-only model that this section's mechanism description originally targeted.

```
Node A runs VM-1 (100 GB virtual disk):

  Disk structure:
    base image:   debian-12-generic.qcow2  (read-only, shared, ~2 GB)
    overlay:      disk.qcow2               (writable, per-VM, sparse)
    cloud-init:   cloud-init.iso           (regenerable from labels)

  Only disk.qcow2 (the overlay) is replicated via lazysync.

Background lazysync daemon (runs on the node agent, cycles every ~5 minutes):

  For each running VM on this node:

  1. QEMU incremental backup via QMP:
     → drive-backup sync=incremental
     → Exports dirty overlay blocks since last sync cycle
     → Crash-consistent: QEMU uses copy-on-write for blocks
       written during export — VM is NOT paused
     → QEMU's Changed Block Tracking (CBT) via persistent dirty
       bitmaps tracks which blocks have been written (since 4.0+)

  2. Chunk the exported dirty data into 1 MB blocks → CIDv1 per block

  3. Compare each chunk's CID against current manifest:
     → Skip any block whose CID matches (written but not actually changed)
     → Only genuinely new/changed blocks proceed

  4. Push new blocks to Node A's local block store VM:
     → POST /blocks for each new block
     → Block store announces provider records to DHT
     → Block store publishes new CIDs to GossipSub topic
       "decloud/blockstore/new-blocks" (fast notification path)

  5. Update manifest in-place:
     → manifest.chunks[offset] = newCID for each changed offset
     → manifest.version++
     → manifest.timestamp = now

  6. Register updated manifest with orchestrator:
     → POST /api/blockstore/manifest { vmId, nodeId, rootCid, version,
         changedBlockCids, totalBytes }
     → Orchestrator validates nodeId matches vm.NodeId (Section 7 fencing)
     → Orchestrator records the new version (no replication plan returned)

  7. Blocks replicate via Kademlia scatter (autonomous, no orchestrator action):
     → Block store announced provider records + GossipSub notification in step 4
     → Other block store nodes receive GossipSub message with new CIDs
     → Each receiving node computes XOR distance: distance(myPeerId, cid)
     → If close enough (adaptive threshold based on local free space) → pull via bitswap
     → Blocks scatter across many nodes (not limited to scheduling-eligible ones)
     → GossipSub is the fast path; DHT provider records are the durable fallback
       for nodes that missed the GossipSub message (offline, network partition)

  8. Orchestrator audits replication asynchronously:
     → Background audit loop queries DHT: FindProviders(chunkCid)
       for each chunk in the latest manifest
     → When all chunks have ≥N providers → advance confirmedVersion
     → Under-replicated chunks are flagged but NOT actively pushed —
       Kademlia's re-publication handles recovery naturally

  9. VMs with replicationFactor=0 (ephemeral) skip lazysync entirely — no
     overlay replication, no manifest tracking. Dashboard shows ephemeral status.
```

### 6.1.1 Initial Overlay Seeding

New VMs start with a minimal overlay (just cloud-init writes + first-boot setup). The lazysync daemon begins pushing overlay blocks on the first cycle after the VM is provisioned. The allocated overlay clusters (not the full virtual disk) are chunked and replicated:

```
New VM:
  - Virtual disk size: 100 GB
  - Overlay allocated clusters after first boot: ~500 MB
  - Initial seed: ~500 blocks (1 MB each)
  - Time to seed: ~2-5 minutes over typical node bandwidth

Sparse overlay allocation via `qemu-img map`:
  - Only allocated clusters are chunked and replicated — zero/unallocated
    regions are not included in the manifest.
```

The orchestrator tracks seeding state via its audit loop. VMs with `confirmedVersion == 0` (no chunks have ≥N providers yet) are flagged as "unprotected" in the dashboard. Seeding is rate-limited to avoid saturating the node's block store VM or network.

Compare this to full-disk seeding: a 100 GB disk would require 100,000 chunks regardless of actual usage. Overlay-only seeding typically handles 1-10% of that volume.

### 6.1.2 QEMU Integration

The lazysync daemon requires integration with the hypervisor layer via QEMU's QMP (QEMU Machine Protocol) and, for application-consistent capture, the QEMU Guest Agent (QGA):

```
QEMU QMP commands used (qemu-monitor-command):

1. block-dirty-bitmap-add
   → Creates a persistent dirty bitmap on the VM's drive
   → Called once when a VM is enrolled in lazysync

2. drive-backup sync=incremental bitmap=lazysync-N
   → Exports dirty blocks to a temp file
   → VM continues running — QEMU handles CoW internally
   → Returns the dirty regions for chunking

3. block-dirty-bitmap-clear
   → Resets the bitmap after successful export
   → Called after blocks are confirmed pushed to block store

QEMU Guest Agent commands used (qemu-agent-command, Phase G):

4. guest-ping
   → Cheap (~2 s timeout) liveness check on the agent
   → Used to decide whether to attempt fsfreeze at all

5. guest-fsfreeze-freeze
   → Drives FIFREEZE into ext4/xfs inside the guest
   → Forces journal commit + dirty-page writeback through virtio
   → Returns once the guest filesystem state is committed

6. guest-fsfreeze-thaw
   → Releases the freeze. MUST be called in a finally block —
     libvirt's own fsfreeze-timeout is the last-resort safety net
```

The node agent already manages VMs via libvirt/QEMU and has access to both sockets (QMP via `virsh qemu-monitor-command`, agent via `virsh qemu-agent-command`). The lazysync daemon runs as a background service on the node agent, cycling through running VMs on a configurable interval. The QMP and guest-agent commands flow through the same `IQmpClient` wrapper.

### 6.1.3 Manifest Versioning & Consistency

The orchestrator tracks manifest versions and audits replication via DHT provider counts:

```
Manifest v1: [chunk0=A, chunk1=B, chunk2=C]  → all chunks have ≥N providers (confirmed)
Lazysync: chunk1 changed → CID D
Manifest v2: [chunk0=A, chunk1=D, chunk2=C]  → chunk D propagating via Kademlia...
Lazysync: chunk2 changed → CID E
Manifest v3: [chunk0=A, chunk1=D, chunk2=E]  → chunk E propagating...

Orchestrator state for this VM:
  currentVersion:   3   (latest registered)
  confirmedVersion: 1   (latest where ALL chunks have ≥N providers in DHT)

Rules:
  - confirmedVersion advances when the audit loop verifies provider counts
  - Old blocks (e.g., chunk B from v1) naturally lose providers as nodes
    GC them via LRU eviction — no explicit "unpin" needed
  - If v2's chunks all reach ≥N providers before v3 is registered →
    confirmedVersion advances to 2
  - Recovery always uses confirmedVersion's manifest
```

### 6.1.4 Application-Consistent Snapshots via Guest fsfreeze (Phase G)

#### Why this exists

Lazysync's capture paths read the qcow2 file directly — `qemu-img map` + `qemu-img convert` on the first cycle, `drive-backup` with a dirty bitmap on subsequent cycles. Both reads see whatever has been flushed to the qcow2 by QEMU's block layer at the moment of the call. They do **not** coordinate with the guest's kernel page cache.

This produces *crash-consistent* snapshots: the captured state is what the guest filesystem would look like after a power cut. ext4/xfs journal recovery can repair *filesystem-level* inconsistency from that state (the journal exists for exactly this purpose), but it cannot repair *file-content-level* inconsistency where the file's metadata says one thing and the file's data extents say another.

#### The failure mode (observed 2026-05-31)

A migrated tenant VM (`504e68ab-…`) failed cloud-init's `init-local` on the target with `ValueError: bad marshal data (unknown type code)` while loading `/usr/share/netplan/netplan/__pycache__/libnetplan.cpython-311.pyc`. `guestmount` against the migrated overlay showed:

```
size:         33338 bytes (canonical for Debian 12 python3-netplan)
file magic:   a70d 0d0a 0000 0000 b6ab e463 5348 0000  (Python 3.11 valid)
tail bytes:   0000 0000 0000 ... 0000 0000 0000        (zero-padded, ~32 bytes)
dpkg status:  netplan.io — install ok installed
file (1):     "data" (structurally invalid — not "Python 3.11 byte-compiled")
```

The base-image content-addressing system (PR1+PR2) held its guarantees end-to-end during this test: orchestrator shipped `BaseImageHash=5ed2aa78…`, the target hit cache, verified bytes, and booted byte-identical base. The defect was one layer down — the **overlay itself** was captured in an internally inconsistent state on the source node:

1. On the source (MSI), `apt-get install netplan.io` finished from cloud-init's perspective: dpkg moved the file into place, updated `/var/lib/dpkg/status`, and recorded `installed`.
2. The guest kernel's page cache held the `.pyc` file's data extents. Linux's default `vm.dirty_writeback_centisecs=500` only **schedules** writeback every 5 s; cold data like a once-written `.pyc` can sit dirty for far longer.
3. Cloud-init exited (touched `/var/lib/cloud/instance/boot-finished`), the System service in `VmReadinessMonitor` flipped to Ready, and `VmInstance.IsFullyReady` became `true`.
4. Lazysync's eligibility filter admitted the VM (`Spec.ReplicationFactor > 0 && IsFullyReady`).
5. `qemu-img map` + `qemu-img convert` read the qcow2. The file's **inode metadata** had been written through to qcow2 (size=33338) but the trailing **data extent** had not yet been flushed by the guest. The host saw size=33338 with zeros at the tail.
6. Replication faithfully shipped this state. The target reconstructed it byte-identically.
7. On target boot, cloud-init's `init-local` ran `netplan generate`, which imported `netplan.libnetplan`, which loaded the `.pyc`, which hit the zero region. `marshal.loads` threw `ValueError: bad marshal data`. cloud-init aborted, netplan never rendered, `networkd-wait-online` timed out, the VM ran in isolation.

The IsFullyReady gate is necessary (don't replicate a half-built system) but not sufficient. "cloud-init done" is not the same as "guest page cache flushed."

#### Why crash-consistent isn't enough here

For a sudden power loss inside a running VM, ext4's journal replay handles the recovery: pending metadata is rolled back to its last consistent state and the affected files are either intact (data committed before the metadata) or absent (metadata committed but data hadn't been written — equivalent to the file never existing). Either way the application sees a coherent filesystem.

For a *snapshot* of a running guest taken by the host without guest cooperation, the same crash-consistent state is captured — but **on the source the guest never crashed**, so the source kept running and eventually flushed the data. The snapshot reflects an instant that, on the source, was followed by the missing flush; the target reconstructs the snapshot and then *never gets the flush*. The file ends up in a state that, on the source, was transient and recoverable, but on the target is permanent and broken.

This asymmetry is the entire reason application-consistent backups exist.

#### Solution: guest-fsfreeze bracket

The QEMU Guest Agent exposes `guest-fsfreeze-freeze`, which drives `FIFREEZE` ioctls into every mounted filesystem inside the guest. `FIFREEZE` forces a journal commit, flushes dirty pages through the block layer, and blocks all subsequent writes until `FITHAW` is called. After freeze returns, the qcow2 reflects a state that the guest filesystem itself considers fully committed.

Lazysync wraps the read step — and *only* the read step — in a freeze/thaw bracket:

```
For each replication-eligible VM:
  guest-ping           (cheap precheck, ~2s)
  guest-fsfreeze-freeze  →  N filesystems frozen
  try:
    qemu-img map + convert  (first cycle)        // host-side qcow2 read
    -- OR --
    drive-backup sync=incremental                 // QMP-side qcow2 read
  finally:
    guest-fsfreeze-thaw
  push captured blocks to blockstore     // network I/O, NO freeze here
```

The freeze brackets only the read from qcow2. The downstream work — CID computation, block push to the local BlockStore VM, manifest registration with the orchestrator — runs **outside** the freeze window. The guest is paused only as long as it takes to scan / export the changed regions, which is sub-second for incremental cycles and 1–30 s for first-cycle full captures depending on overlay size.

This is the standard application-consistent backup pattern used by VMware (VSS-quiesced snapshots), Hyper-V (VSS via the integration services), Proxmox (`vzdump --mode snapshot --quiesce`), and libvirt's own `virsh domfsfreeze` / `domfsthaw`. We are not inventing anything — we are aligning to the boundary the industry already settled on.

#### Best-effort, graceful degradation

The fix must not weaken the platform's existing guarantees for tenant VMs whose guest agent is missing, broken, or unreachable. Three rules:

1. **If `guest-ping` fails**, skip the freeze attempt entirely and run the capture body as before. Log a warning; replication proceeds crash-consistent (the pre-Phase-G default).
2. **If `guest-fsfreeze-freeze` throws** (agent installed but freeze itself fails — e.g. fsfreeze unsupported by an unusual guest filesystem), log a warning and run the capture body without freeze. Crash-consistent fallback.
3. **Thaw runs in a `finally`**, regardless of whether the capture body succeeded, threw, or was cancelled. `CancellationToken.None` is passed to thaw so a cancelled operation still thaws. If thaw itself fails after a successful freeze, that's logged but not rethrown — the only correctness-critical invariant is "freeze must be followed by thaw," and libvirt's own `domfsfreeze` timeout (a built-in safety net in QEMU 4.2+) auto-thaws the guest if the host fails to within a bounded interval.

A bug in the freeze/thaw bracketing must never leave a guest frozen indefinitely. Three independent layers prevent that: the `finally`, the unconditional thaw token, and libvirt's timeout-driven auto-thaw.

#### What this fix doesn't claim to do

- **It does not provide transactional consistency for in-guest applications** (databases, message brokers, anything with its own write-ordering invariants). Those need either the application's own backup hooks or QGA's `fsfreeze-hook` scripts (operator-installed inside the guest, fired at freeze time). Out of scope for the platform default.
- **It does not eliminate the need for `IsFullyReady`.** The readiness gate still serves "don't replicate a VM that's still installing packages." Freeze is a complement, not a replacement.
- **It does not switch to qcow2 internal snapshots** to shrink the freeze window. That's the next lever if first-cycle freeze durations on large overlays cause customer-visible write stalls; not needed today.
- **It does not change wire formats or persistent state.** No new fields on the manifest, no new fields on `LazysyncState`, no schema migration. The change is entirely local to the node-agent's capture path.

#### Touchpoints

| File | Change |
|------|--------|
| `src/DeCloud.NodeAgent.Core/Interfaces/Qmp/IQmpClient.cs` | Add `FsFreezeAsync`, `FsThawAsync`, `GuestAgentPingAsync`. Update interface summary to acknowledge guest-agent commands alongside QMP. |
| `src/DeCloud.NodeAgent.Infrastructure/Services/Qmp/QmpClient.cs` | Implement the three new methods. Add a private `SendAgentAsync` helper mirroring `SendAsync` but targeting `virsh qemu-agent-command` with a tunable per-call timeout (2 s for ping, 30 s for freeze/thaw). |
| `src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs` | Add private `RunUnderGuestFreezeAsync(vmId, captureBody, ct)` helper enforcing the ping → freeze → body → finally-thaw sequence with all fallback rules. Wrap `ExportOverlayAsync` (first cycle) and `DriveBackupIncrementalAsync` (incremental) calls in it. |

No orchestrator changes. No wire-protocol changes. No data-store changes.

#### Verification

A diagnostic-positive test reruns the live migration that exposed the bug:

1. Deploy a fresh tenant VM with default cloud-init (includes `qemu-guest-agent`).
2. Wait for `IsFullyReady`. Wait for first lazysync cycle (logs should show `froze N filesystem(s)` then `thawed N filesystem(s)` at DEBUG).
3. Shut the source node down to trigger migration.
4. After migration, `guestmount` the target's overlay and inspect `/usr/share/netplan/netplan/__pycache__/libnetplan.cpython-311.pyc`. Expected: `file` reports "Python 3.11 byte-compiled", no zero-tail, structurally valid.
5. Cloud-init `init-local` should complete without `ValueError`, netplan should render, network should come up, the VM should reach Ready on the target.

A regression test confirms the graceful-degradation path:

6. Deploy a tenant VM whose cloud-init explicitly removes `qemu-guest-agent`.
7. Verify lazysync still produces blocks (logs show `guest agent unresponsive — capturing crash-consistent`).
8. Manifest reaches Protected. VM is replicable. No regression on agentless workloads.

### 6.1.5 Phase H: Bounded-Freeze Capture and Reconstruction Hardening

Phase H (2026-06-03) closes three gaps identified during extended migration testing after Phase G shipped. See §8 Phase H for the full implementation record. The key outcomes:

**`drive-backup sync=top` replaces `qemu-img convert --force-share`** on the first lazysync cycle. `sync=top` exports only the topmost BDS (the overlay) via QEMU's live block layer. The freeze brackets only the QMP job-queue call — sub-second — not the data export. The guest is unfrozen before data copying begins. Lazysync trails the live disk by 9–15 s (async export duration) rather than pausing it for the full convert. The `qemu-img map` whitelist computation (`GetOverlayChunkOffsetsAsync`) is deleted because `sync=top` handles the overlay-only filter natively.

**`RunFsckOnOverlayAsync` is removed.** `fsck.ext4 -fy` applied crash-recovery heuristics that corrupted crash-consistent overlays: it resolved "duplicate block" conflicts between journal entries and normal inodes by clearing extent mappings, producing inodes with correct size metadata but zeroed data blocks. The kernel's own ext4 journal replay at mount time is the correct mechanism — it replays committed transactions and discards uncommitted ones without touching data blocks. The boot log signature for a correctly handled crash-consistent overlay is `EXT4-fs (vda1): recovery complete` followed by a normal mount, not fsck output.

**Owner indexing is complete across all four block-arrival paths:** gossipsub `new-blocks` receive (existing), `POST /blocks?owner=` (existing), `GET /blocks/{cid}?owner=` (new), and `performCatchupFromPeer` (new). The pre-flight check uses `POST /blocks/has` (raw blockstore presence) instead of `GET /owners/{vmId}` (ownership index) — eliminating false negatives when blocks are present but the ownership index is incomplete.

### 6.1.6 Overlay-Only Replication: Structural Limits for Unplanned Migration

**[SUPERSEDED by Phase I — see §6.1.7]** This section documents the design analysis that motivated Phase I. Retained for historical context and as the evidence basis for "why full-disk snapshot replication exists." The platform no longer uses the overlay-only model described below.

**Context (reached 2026-06-03 after extended testing):**

The system distinguishes two migration scenarios with different guarantees:

#### Planned migration (source online, Phase G+H path)

Guest-fsfreeze + `drive-backup sync=top` produces an **application-consistent** overlay snapshot. The guest filesystem considers all captured state fully committed. Reconstruction on the target produces a disk that is coherent: ext4 journal replay at mount time succeeds, cloud-init runs, network comes up. This is the primary validated path.

#### Unplanned migration (source offline, node died)

When the source node dies abruptly, there is no opportunity to freeze the guest. Migration proceeds from the `ConfirmedChunkMap` at `ConfirmedVersion` — the last snapshot that LazysyncManager confirmed had all CIDs with ≥RF remote providers. This snapshot is:

- **Coherent from the block perspective:** every block is content-addressed and byte-verified. No torn blocks.
- **Crash-consistent from the filesystem perspective:** reflects a point in time when the source's guest was running. The ext4 journal contains in-flight transactions from that moment.
- **Potentially not journal-replayable:** the crash-consistent state captured by lazysync is taken at an arbitrary instant during guest operation. Unlike an actual VM crash (where the journal represents the honest last state), a lazysync snapshot may capture filesystem metadata structures (inode bitmaps, block bitmaps, the journal itself) that were partially updated across multiple lazysync cycles. If the inode bitmap was captured in one cycle and the journal entry that atomically updates it was captured in a different cycle, the combination is internally inconsistent in a way that neither journal replay nor fsck can fully repair.

**Observed 2026-06-03:** VM `msi-0d04` migrated successfully (pre-flight passed, reconstruction completed) but booted into emergency mode with `EXT4-fs error: ext4_validate_inode_bitmap — Corrupt inode bitmap, block_group = 0`. The journal aborted, the filesystem remounted read-only, cloud-init failed with `OSError: [Errno 30] Read-only file system`. The VM was alive (qemu-guest-agent started) but not usable.

**Root cause analysis:**

ext4's metadata structures are not semantically independent 1 MiB blocks. The inode bitmap at block_group 0 is updated by every file creation and deletion. Its content at any moment depends on the state of the journal and the inode table — both of which may have been captured at different lazysync versions. Content-addressing guarantees each individual block is byte-perfect, but does not guarantee temporal coherence across blocks that form a logically coupled metadata structure.

This is the fundamental property of the overlay-only replication model: it captures the overlay as a set of content-addressed blocks over time, not as a single coherent point-in-time image of the disk.

**What the platform guarantees for unplanned migration:**

| Property | Status |
|----------|--------|
| No data loss for committed application data | ✅ Guaranteed — committed writes are in confirmed blocks |
| Filesystem metadata coherence | ⚠️ Best-effort — depends on lazysync cycle alignment with guest metadata flushes |
| Successful boot without manual intervention | ⚠️ Likely for lightly-loaded VMs with infrequent metadata churn; not guaranteed for active VMs |
| Recovery to last ConfirmedVersion state | ✅ Guaranteed — reconstruction is deterministic |

**Mitigations in place:**

1. **Phase G+H for planned migrations** — eliminates this class of issue entirely when the source is available. The platform should prefer planned migrations (user-initiated, rolling node maintenance) over relying on unplanned recovery.

2. **`systemd-fsck-root.service`** — runs e2fsck at boot for minor journal inconsistencies. Cannot recover corrupt inode bitmaps that fail checksum validation; requires `e2fsck -c -y` (full inode table check) which is not available in the initramfs of the tested Debian 12 image.

3. **ConfirmedVersion window minimization** — the shorter the lazysync interval and the faster `ConfirmedVersion` advances, the smaller the delta between the confirmed snapshot and the live disk state. This reduces the probability of metadata skew but does not eliminate it.

**Architectural consensus:**

Overlay-only replication is the **correct and optimal** model for planned migrations where guest-fsfreeze is available. It is structurally insufficient to guarantee bootable filesystem state for unplanned recovery of filesystems with active metadata churn.

For unplanned recovery to be reliable, one of the following is required:
- **Full-disk snapshot replication** — captures base image + overlay as a single coherent point-in-time image, at higher storage cost (partially offset by content-addressed base image sharing across VMs on the same image)
- **In-guest consistency hooks** — periodic `guest-fsfreeze-freeze` → snapshot → `thaw` cycles by the lazysync daemon, so every committed manifest version corresponds to an application-consistent (not merely crash-consistent) overlay
- **Acceptance of the current behavior** — unplanned recovery produces a best-effort result; the operator or platform may need to run `e2fsck` inside the VM post-boot for heavily-loaded VMs

The current platform default is the third option. The first two are future work items.

**This does not change the primary value proposition.** The majority of real-world migrations are planned (node maintenance, rebalancing, user-initiated). For those, Phase G+H provides full application consistency. Unplanned recovery from a sudden node death is the tail case, and even there the platform delivers the best available state (last ConfirmedVersion) rather than total data loss.

**Update (Phase I, 2026-06-04):** Phase I implements option 1 (full-disk snapshot replication) combined with option 2 (per-cycle in-guest consistency hooks). The "current platform default" referenced above is no longer the third option; it is the combined first-and-second option. Planned and unplanned migration now produce identical correctness guarantees. See §6.1.7.

### 6.1.7 Phase I: Full-Disk Snapshot Replication

**Status:** Implemented 2026-06-04. Superseded by Phase J (§6.1.8) on 2026-06-05. Retained for historical context and as the evidence basis for why `drive-backup` was abandoned.

**Why Phase I was superseded.** `drive-backup sync=full` installs a COW snapshot point at job creation (one QMP round-trip inside the freeze), then reads clusters sequentially over tens of seconds unfrozen. QEMU's COW notifier ensures each cluster is captured at T=0 — but coupled ext4 metadata structures (journal, inode bitmap, inode table) are read at different wall-clock moments, producing temporal incoherence. Changing `sync=full` vs `sync=top` changes which blocks are included but not when they are read. The failure is structural to `drive-backup`. Confirmed via live debugfs/e2fsck diagnostics on msi-1867 (2026-06-04). See §6.1.8 for the root cause analysis and Phase J's resolution.

**Capture primitive.** Lazysync captures the full merged disk via QMP `drive-backup sync=full`, bracketed by a `guest-fsfreeze-freeze` / `thaw` envelope. The freeze brackets only the QMP Start call (one round-trip, sub-second); drive-backup's copy-on-write notifier fixes the snapshot point at job creation, and the copy itself runs unfrozen in the background. First cycle uses `sync=full`; subsequent cycles use `sync=incremental` against a drive-level dirty bitmap that tracks any write the guest issues regardless of which qcow2 layer absorbs it.

**Why Phase I did not close §6.1.6's failure mode.** Phase I claimed temporal coherence because `drive-backup` installs a COW snapshot point at T=0. This claim was wrong. The COW notifier captures each cluster at T=0 on a per-cluster basis, but clusters are read at different wall-clock moments during the unfrozen background copy. ext4 metadata structures (inode bitmap, journal commit block, inode table) read at different moments are temporally incoherent even when each individual cluster is individually correct. Phase J (§6.1.8) is what actually closes the msi-0d04 failure mode — by performing no data movement during the freeze and making disk.qcow2 truly immutable at the snapshot point.

**Reconstruction path.** `ReconstructDiskAsync` in `LibvirtVmManager` produces a self-contained qcow2 with no backing file. The pre-Phase-I `qemu-img rebase` step that attached a backing-file pointer is gone — the captured image already contains every populated cluster the guest could read. Consequences:

- The migration target does **not** need the base image present. If the orchestrator's image registry has rotated the URL since the source's original deploy, the migration still succeeds; the manifest CIDs are the contract.
- `CreateOverlayDiskAsync` is bypassed on the migration path. Only fresh deploys take the deploy-side branch that downloads and verifies the base image.
- `VmSpec.BaseImageUrl` and `VmSpec.BaseImageHash` remain populated on the migration payload as **provenance only** (audit record of what the source ran). They are not used to fetch or verify anything on the target.
- `CommandProcessorService.HandleCreateVmAsync`'s guard `if (string.IsNullOrEmpty(req.BaseImageUrl))` was refined to apply only when `req.ChunkMap` is also empty (fresh deploys). Migration payloads with empty `BaseImageUrl` are now legitimate.

**Field renames in `VmSpec`:** `OverlayChunkMap` → `DiskChunkMap`, `OverlayRootCid` → `DiskRootCid`. The post-rename names self-document that the chunk set reconstructs the full disk, not just an overlay layer.

**Storage cost.** Aggregate network growth is bounded by `K × BaseImage × RF`, where K is the number of distinct base images on the network. Content-addressed dedup means each base image's CIDs appear once regardless of how many VMs share that image. Numerical: at platform scale (N ≥ 1000 VMs, K ≤ 20 distinct base images, RF = 3, average overlay churn O = 500 MB/VM), aggregate storage growth vs the prior overlay-only model is **under 2%**. The per-VM MongoDB manifest document grows ~4× (~120 KB for a small VM, two orders of magnitude under the 16 MB BSON limit).

**Per-cycle freeze impact.** Every lazysync cycle (default 5 min) issues one `guest-fsfreeze-freeze` / `thaw` pair around the QMP Start call. Total freeze duration per cycle is bounded by one QMP round-trip — sub-second on the same host. `RunUnderGuestFreezeAsync`'s existing best-effort fallback (crash-consistent capture when the guest agent is unreachable) is unchanged.

**What is unchanged in Phase I.** The block store mesh, GossipSub fast path, DHT-native scatter replication, Kademlia XOR threshold, Phase E split-brain prevention (TargetNodeId pre-check, manifest NodeId fence, InvalidVmIds push), Phase F self-healing (`needs-replica` topic, presence-loss monitor, replication survey), `LazysyncManager` orchestrator audit (5-min + 6-h sentinel), `ManifestRecord` schema in MongoDB, the 1 MB block size for VM manifests, `ManifestType.VmOverlay` enum value (durable identifier — not renamed), and the entire deploy path (`ImageManager.EnsureImageAvailableAsync` + `CreateOverlayDiskAsync` still authoritative for fresh deploys).

**Implementation references.** See `PHASE_I_FULL_DISK_REPLICATION.md` for the design analysis, `PHASE_I_IMPLEMENTATION_PROPOSAL.md` for the file-by-file change inventory, and the six Phase I commits in the codebase for the actual diffs.

---

### 6.1.8 Phase J: blockdev-snapshot-sync — Correct Coherence Model

**Status:** Implemented 2026-06-05. Supersedes Phase I (§6.1.7).

#### Root cause of Phase I's failure (confirmed on msi-1867, 2026-06-04)

`drive-backup sync=full` installs a COW snapshot point at job creation — one QMP round-trip inside the freeze. The actual copy then runs unfrozen over tens of seconds. During those seconds QEMU's COW notifier ensures each individual cluster is captured at its T=0 state — but clusters are read at different wall-clock moments. Coupled ext4 metadata structures (journal commit block, inode bitmap, inode table) read at different moments produce temporal incoherence.

Confirmed on msi-1867 via `debugfs logdump` and `e2fsck -fn`:

- Journal transactions 9331 and 9332 absent from the captured image — bitmap updates for those inodes were not committed at capture time on all blocks
- Fast Commit Area contained a garbage value (`0x6a0a946c`)
- Dozens of directory entries pointing to "deleted/unused" inodes across `/`, `/var/lib`, `/var/cache`, `/etc`
- `qemu-img check` reported "No errors" — the qcow2 was byte-perfect; corruption was temporal, not structural
- VM booted to empty console, virsh console unresponsive, SSH refused, EXT4-fs error at initramfs pivot

**Conclusion:** `drive-backup sync=full` has identical coherence properties to `sync=top`. Changing `sync=` changes which blocks are included but not when they are read. Phase I was structurally wrong. No fix exists within the `drive-backup` model.

#### Phase J design

**Correct primitive:** `blockdev-snapshot` (not `blockdev-snapshot-sync` — that command has no working call path on QEMU 8.2.2/libvirt; see QMP testing below). The key insight: `blockdev-snapshot` performs **no data movement** at snapshot time. The original disk.qcow2 becomes immutable at the exact freeze moment. The guest is frozen for one QMP round-trip only; no writes can occur; every byte in the original file is at the same filesystem transaction boundary. The COW race that broke Phase I cannot exist — there is nothing to race against.

**QEMU 8.2.2 / libvirt — confirmed working sequence (established via live QMP testing):**

```
1. qemu-img create -f qcow2 {newOverlayPath} {virtualSize}
   — NO -b flag: backing-file in the qcow2 header causes blockdev-add to attempt
     opening disk.qcow2 as a backing file, which fails because QEMU already holds
     an exclusive write lock on it.

2. chown libvirt-qemu:libvirt-qemu {newOverlayPath}
   — QEMU opens the file via blockdev-add; requires libvirt-qemu ownership.

3. blockdev-add: {"driver":"qcow2","node-name":"ls-ov-{vmId[..8]}",
                   "file":{"driver":"file","filename":"{newOverlayPath}"}}
   — Registers the overlay as a named block node in QEMU's block graph.

4. blockdev-snapshot: {"node":"libvirt-2-format","overlay":"ls-ov-{vmId[..8]}"}
   — Inside fsfreeze. Atomically redirects guest writes to the new overlay node.
     disk.qcow2 (libvirt-2-format) is now immutable.

   [Guest thaws. All writes go to the new overlay. disk.qcow2 untouched.]

5. qemu-img convert --force-share disk.qcow2 → tmpRawPath
   — Safe: disk.qcow2 has no in-flight writes. Produces flat raw for ScanChunksAsync.

6. ScanChunksAsync(tmpRawPath): skip zero chunks, compute CIDs, diff against state.Chunks

7. PushBlocksAsync: push changed CIDs to local blockstore

8. Update state.Chunks, Version, TotalBytes, LastSyncAt; RegisterManifestAsync

9. DeleteSnapshotNodeAsync teardown:
   a. block-commit: {"device":"ls-ov-{vmId[..8]}","top-node":"ls-ov-{vmId[..8]}",
                     "base-node":"libvirt-2-format","job-id":"ls-commit-{vmId[..8]}"}
      — Merges overlay content into disk.qcow2 (background job).
   b. Poll query-block-jobs until ready=true (1s interval, 10min timeout)
   c. job-complete: {"id":"ls-commit-{vmId[..8]}"}
      — Finalizes commit; disk.qcow2 is the live disk again.
   d. blockdev-del: {"node-name":"ls-ov-{vmId[..8]}"}
   e. File.Delete(newOverlayPath)

10. finally: RevokeScratchAppArmorAsync, delete newOverlayPath and tmpRawPath (idempotent)
```

**QMP argument discovery — what failed and why:**

The QEMU 8.2.2 libvirt-managed `query-block` output has `"device": ""` (always empty for virtio drives). The primary disk is identified by `backing_file_depth >= 1` and `ro=false` — this returns `inserted.node-name = "libvirt-2-format"`. `blockdev-snapshot-sync` was rejected under all argument combinations:

- `"device":"libvirt-2-format"` → `Cannot find device='libvirt-2-format' nor node-name=''` (node-name passed as device value)
- `"node":"libvirt-2-format"` → `Parameter 'node' is unexpected` (this QEMU version doesn't support the `node` argument variant)
- `"device":"virtio-disk0"` → `Cannot find device='virtio-disk0' nor node-name=''` (QOM path doesn't work as device name)

`blockdev-add` + `blockdev-snapshot` was confirmed working directly via QMP on the running VM. `blockdev-snapshot` requires `overlay` to reference an already-registered block node — which `blockdev-add` provides. Teardown via `block-commit` + `job-complete` + `blockdev-del` was also confirmed working, with the commit job completing nearly instantly (851968 bytes written in <3s for a freshly created overlay).

**Incremental tracking.** The dirty bitmap mechanism (Phases D+1 through I) is removed entirely. Incremental tracking is already provided by `ScanChunksAsync`'s CID diff against `state.Chunks` — only chunks whose content changed since the last cycle enter `changedChunks`. This was always true; the dirty bitmap was a redundant second mechanism. Every Phase J cycle is identical (no first-cycle vs subsequent-cycle distinction).

**Crash safety.** If the node crashes between `blockdev-add`/`blockdev-snapshot` and `DeleteSnapshotNodeAsync`, `newOverlayPath` survives and disk.qcow2 retains the backing-file relationship in QEMU's block graph. The VM cannot start until resolved. Crash-recovery in `LibvirtVmManager` (detecting orphaned `lazysync-overlay-*.qcow2` files and running `qemu-img commit` at startup) is a follow-on item.

**Migration cloud-init bootcmd fix.** The migration cloud-init `bootcmd` previously contained `systemctl start qemu-guest-agent`. At `bootcmd` time systemd's D-Bus socket is not guaranteed available; `systemctl start` blocks indefinitely waiting for D-Bus, hanging cloud-init before SSH or the guest agent can start. Fix: remove `systemctl start qemu-guest-agent` from `bootcmd`. The `qemu-guest-agent.service` symlink already exists in `multi-user.target.wants` on the disk (placed at original deploy time) — systemd starts it automatically at the correct point in the boot sequence without any `bootcmd` intervention.

#### Verification (msi-7825, 2026-06-05)

- **Disk reconstruction:** ✅ Clean — `ReconstructDiskAsync` fetched 1713 blocks
- **Kernel boot:** ✅ Full boot, all partitions (vda1/vda14/vda15), EXT4 mounted clean
- **No EXT4 errors:** ✅ No `ext4_validate_inode_bitmap`, no emergency mode
- **Journal coherence:** ✅ Phase J closure confirmed — §6.1.6 failure mode does not recur
- **Networking:** ✅ `192.168.122.217` assigned
- **cloud-init:** ✅ Completed all stages without hanging — `modules:final` at Up 64s
- **qemu-guest-agent:** ✅ Connected — `Status: 2` (Ready) in MongoDB
- **SSH:** ✅ Working, root login successful
- **Welcome page:** ✅ Shows pre-migration content (user data survived)
- **Browser terminal + file browser:** ✅ Working
- **Stale VM cleanup:** ✅ When MSI node came back online, correctly detected VM was stale and removed local copy

#### What does not change

`ScanChunksAsync`, `PushBlocksAsync`, `ComputeCidV1`, `ComputeRootCid`, `FindBlockstoreApiAsync`, `GetDiskVirtualSizeAsync`, `RunUnderGuestFreezeAsync`, `LoadStateAsync`, `SaveStateAsync`, `NotifyBlockstoreManifestAsync` — all unchanged. `ReconstructDiskAsync` (migration reconstruction path) — unchanged; it reconstructs from manifest CIDs, which are now produced by reading a coherent snapshot. The orchestrator, blockstore Go binary, Phase F self-healing, Phase E split-brain prevention, DHT/GossipSub, `ManifestRecord` schema, `ManifestType.VmOverlay` enum, 1 MB block size — zero changes.

#### Files changed

```
src/DeCloud.NodeAgent.Core/Interfaces/Qmp/IQmpClient.cs
src/DeCloud.NodeAgent.Infrastructure/Services/Qmp/QmpClient.cs
src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs
src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs
  (remove systemctl start qemu-guest-agent from migration bootcmd)
```

---

### 6.2 Migration on Node Failure

```
Node A goes offline (shutdown / blocked / hardware failure):

For each VM on Node A:
  1. Orchestrator retrieves VM's scheduling requirements
     (CPU, RAM, GPU, region, affinity rules, etc.)

  2. Orchestrator retrieves VM's confirmedVersion manifest
     (root CID + all referenced block CIDs — a single flat manifest,
      no delta chain to walk)

  3. For each candidate node, evaluate:
     scheduling_fit:      does the node meet VM's requirements? (filter)
     available_resources: enough CPU/RAM/disk headroom? (rank)

     Block locality is NOT a factor — the target node fetches
     overlay blocks via bitswap from scattered providers.

  4. Select best candidate based on scheduling fit + resource headroom

  5. Orchestrator updates vm.NodeId from A → B (Section 7 fencing)

  6. Orchestrator sends CreateVm command to Node B with:
     - VM spec (CPU, RAM, disk, GPU mode, etc.)
     - Manifest root CID (points to confirmed overlay state)
     - Base image ID (Node B downloads or uses cached)
     - Labels (for cloud-init regeneration)

  7. Node B:
     a. Downloads base image (if not cached) from image registry
     b. Creates empty overlay qcow2 backed by base image
     c. Fetches overlay chunks via bitswap from scattered providers
        (many sources → parallelized → fast)
     d. Writes chunks into overlay at correct offsets
     e. Regenerates cloud-init ISO from labels
     f. Boots VM

  8. VM resumes from confirmed state
     (data loss = changes between confirmedVersion and currentVersion)
```

#### Why This Is Fast

- **Base image**: probably cached on Node B (it's a standard image). If not, it's a single download of a well-known artifact, not a block-by-block fetch.
- **Overlay**: only the allocated clusters — typically 1-10% of the virtual disk size. Blocks are scattered across many providers — bitswap fetches from the closest/fastest ones in parallel. More providers = faster reconstruction than pulling from 3 pre-selected replicas.
- **Cloud-init**: regenerated in milliseconds from labels. Zero network transfer.
- **No chain traversal**: the manifest IS the complete overlay state. Read chunks, write file, done.

### 6.3 DHT-Native Scatter Replication

Replication is **decoupled from placement**. These are independent concerns:

- **Replication = durability.** Scatter blocks as widely as possible across the network.
- **Scheduling = placement.** The orchestrator picks an eligible node when migration is needed.

The block store does NOT replicate to specific "target nodes." Instead, Kademlia's XOR metric naturally scatters blocks across the network. Every node with a block store (≥100 GB storage) participates in replication regardless of its scheduling capabilities.

```
Example: VM-1 requires 4 vCPUs, 8 GB RAM, us-east region

Lazysync pushes overlay blocks to Node A's local block store.
Block store announces provider records to the DHT.
Kademlia naturally scatters blocks to the K closest nodes in XOR space:

  Node B: us-east, 8 cores, 32 GB RAM     ← could host VM-1
  Node C: ap-south, 2 cores, 4 GB RAM     ← can't host VM-1, CAN store its blocks
  Node D: eu-west, 16 cores, 64 GB RAM    ← could host VM-1
  Node E: us-east, 1 core, 2 GB RAM       ← Raspberry Pi, CAN store blocks
  Node F: eu-west, 4 cores, 8 GB RAM      ← could host VM-1
  ...12 more nodes scattered across regions

Durability: blocks survive even if an entire region goes down.
Candidate pool: ALL nodes with storage contribute, not just scheduling-eligible ones.
Migration: orchestrator picks from {B, D, F, ...} based on scheduling fit.
           Target fetches overlay via bitswap from {B, C, D, E, F, ...} in parallel.
```

**Why this is better than placement-aware replication:**

1. **Larger replication pool** — A 2-core Raspberry Pi can store chunks for a 16-core GPU VM. This expands the candidate pool dramatically compared to restricting replicas to scheduling-eligible nodes.

2. **Better durability** — Kademlia's XOR metric distributes blocks across the ID space, which is statistically independent of geographic or failure domain clustering. More diverse placement = more resilient.

3. **Simpler replication logic** — No scheduling requirements evaluation in the replication path. No failure domain reasoning. The block store only asks: "Am I close to this CID in XOR space? Do I have room?"

4. **Faster migration** — With N scattered providers (potentially 10-20+ nodes), bitswap parallelizes the fetch across many sources. Placement-aware replication with 3 pre-selected replicas means 3 sources. Scatter gives you many more.

5. **No orchestrator bottleneck** — Replication happens autonomously via Kademlia. The orchestrator only audits provider counts.

### 6.4 Adaptive XOR Threshold

Block store nodes decide whether to pull a block based on two factors:

1. **XOR distance**: `distance(myPeerId, blockCid)` — closer = higher priority
2. **Local free space**: more room = more aggressive pulling

```go
// Pseudocode for pull decision
func shouldPull(cid CID) bool {
    distance := xorDistance(myPeerId, cid)
    freePercent := (capacity - used) / capacity
    
    // Base threshold: only pull if we're in the K closest
    // Adaptive: relax threshold when we have more space
    threshold := baseThreshold * (1.0 + freePercent)
    
    return distance < threshold
}
```

This naturally balances the network:
- Nodes with lots of free space pull more aggressively → fill up → slow down
- Nodes near capacity only pull blocks they're very close to in XOR space
- No central coordinator needed

### 6.5 GossipSub Fast Path

Standard Kademlia provider records are passive — they answer queries but don't push notifications. Waiting for periodic DHT scans to discover new blocks would add minutes of latency to replication.

GossipSub provides near-instant notification:

```
Source block store (Node A) pushes new block:
  1. Store block locally
  2. Announce provider record to DHT
  3. Publish CID to GossipSub topic "decloud/blockstore/new-blocks"
     (carries replicationFactor + manifestVersion — Phase F)

Receiving block stores (Nodes B, C, D, ...):
  1. Receive GossipSub message with new CID
  2. Compute XOR distance: distance(myPeerId, cid)
  3. If close enough (adaptive threshold) → add to bitswap want list
  4. Bitswap fetches block from Node A (or any other provider)
  5. Store locally, announce provider record
  6. Persist replicationFactor → owners/{vmId}.meta for future self-healing (Phase F)

Time from push to replication: seconds (not minutes)
```

**Phase F additions** — three further GossipSub topics carry repair signals:

- `decloud/blockstore/needs-replica` (Phase F): GC publishes here before evicting (both LRU and confirmed-evict paths); the presence-loss monitor publishes here when a peer goes silent. Recipients holding the CID re-publish on `new-blocks`, triggering XOR-close peers to absorb.
- `decloud/blockstore/presence` (existing, Phase F use): heartbeats now feed a shared `presenceState` consumed by the presence-loss monitor and the replication survey.
- `decloud/blockstore/vm-deleted` (existing Phase E): retained unchanged.

For nodes that missed a GossipSub message (offline, network partition), the **replication survey** (§6.8) serves as the durable fallback: every 5 minutes it fetches each presence peer's owner index and compares against local CIDs to identify under-replicated blocks. No orchestrator commands are needed — blocks scatter and self-heal across the network autonomously.

### 6.6 Handling "Scatter Window" Data Loss

There's an inherent window between when a block is pushed to the source node's block store and when it scatters to other providers. If the source node dies in this window, the block may be lost.

```
Timeline:
  T=0:  Lazysync pushes block X to Node A's block store
  T=1s: GossipSub notification sent
  T=3s: Nodes B, C start pulling via bitswap
  T=5s: Block X replicated to B, C (provider count = 3)
  ---
  T=2s: Node A crashes (before B, C finish pulling)
        → Block X may be lost (only provider was A)
```

**Mitigation:**

1. **confirmedVersion tracking**: The orchestrator doesn't advance `confirmedVersion` until all chunks have ≥N providers. Recovery uses `confirmedVersion`, not `currentVersion`. This means:
   - If Node A dies at T=2s, the VM migrates using `confirmedVersion` (before block X)
   - Block X (in `currentVersion` but not `confirmedVersion`) is lost
   - Data loss = changes between confirmed and current

2. **Source-offline alerting**: When a node goes offline, the orchestrator checks each VM's manifest state:

```
For each VM on the dead node:

  Case 1: confirmedVersion == 0
    → No confirmed state exists. VM cannot be recovered.
    → Alert: "VM-X has no confirmed state. Data may be lost.
       The VM cannot be migrated."
    → Dashboard status: UNRECOVERABLE

  Case 2: confirmedVersion > 0 but < currentVersion
    → Alert: "VM-X will recover from confirmed version N.
       Approximately M minutes of recent state may be lost."
    → Proceed with migration from confirmed manifest
    → Dashboard status: RECOVERING (partial data loss)

  Case 3: confirmedVersion == currentVersion (fully caught up)
    → No data loss. Proceed with migration normally.
    → Dashboard status: MIGRATING

  Case 4: replicationFactor == 0 (ephemeral)
    → VM is gone. No alert about data loss (user opted in).
    → Dashboard status: LOST (ephemeral)
```

This is the honest approach: the scatter window is small (seconds to minutes) and cannot be eliminated without synchronous replication (which would add latency to every write). The mitigation is visibility — the orchestrator tracks the gap between `currentVersion` and `confirmedVersion` and alerts when it matters.

**Phase F note:** mesh-driven repair (§6.8) shortens the recovery latency for blocks that have already
scattered past `confirmedVersion`. If a replica peer dies AFTER a manifest version has been confirmed,
the presence-loss monitor + survey loop restore `RF` within minutes — without orchestrator
intervention. The scatter-window hazard itself (the gap between push and first scatter) is unchanged,
because no amount of post-hoc repair can help a block that never left the source node before it died.
The `confirmedVersion` discipline remains the right mitigation for that window.

### 6.7 DHT Proximity Endpoint

The block store binary needs to evaluate its position in Kademlia XOR space relative to a given CID. The block store has its own persistent peer ID and can compute `xor(myPeerId, cid)` locally — this is a trivial bitwise operation.

However, for diagnostics, debugging, and the periodic neighborhood scan, the **DHT VM exposes a proximity endpoint**:

```
GET /proximity/{cid}
  → {
      distance: "0x1a3f...",          // XOR distance from this DHT node's peer ID
      closerKnownPeers: 7,            // how many peers in the routing table are closer
      estimatedRank: 8,               // approximate position (1 = closest known peer)
      kValue: 20                       // Kademlia K parameter
    }

  Purpose: Allows the block store to make informed pull decisions
  beyond simple XOR distance. "Am I in the K closest?" is more useful
  than "what is my raw distance?" for deciding whether to store a block.
```

For the **normal fast path** (GossipSub announce → pull decision), the block store uses local XOR distance + adaptive threshold (Section 6.4). The DHT proximity endpoint is used for:
- Periodic neighborhood scan (durable fallback)
- Diagnostic queries ("why did/didn't this node store block X?")
- Dashboard visualization of block distribution

This endpoint is added to the DHT VM's existing localhost HTTP API (port 5080), alongside `/health`, `/peers`, `/connect`, and `/publish`.

### 6.8 Mesh-Driven Self-Healing (Phase F)

Pre-Phase-F durability relied on the orchestrator's `LazysyncManager` audit loop running every 5 minutes
and dispatching `ReseedVm` commands when provider counts dropped below `replicationFactor`. This put the
orchestrator on the durability critical path: a block evicted by GC was un-replicated until the next
audit cycle, a peer going silent surfaced as under-replication 5+ minutes later, and every repair was a
round-trip through MongoDB and the command channel.

Phase F moves all routine repair onto the blockstore mesh itself, leaving the orchestrator with only
the roles its centralised authority requires: advancing `ConfirmedVersion` from zero on initial seeding,
escalating to `TriggerReseedAsync` when every replica is genuinely gone, and authority-transferring
`NodeId` on migration.

#### Failure classes and where they're handled

| Failure | Detector | Repairer | Latency |
|---------|----------|----------|---------|
| LRU / capacity eviction | GC pre-delete signal | XOR-close peers via `needs-replica` response | seconds |
| Replica node offline | Presence heartbeat timeout (180 s) | Cached owner-index → `needs-replica` fanout | minutes |
| Silent FlatFS wipe / TTL drift | Survey owner-index diff | Surveying peer publishes `new-blocks` directly | minutes |
| TTL provider record expiry | `reannounceExistingBlocks` (every 10 min, existing) | Self via `dht.Provide` | minutes |
| Catastrophic total loss (mesh fully failed) | Orchestrator sentinel audit | `TriggerReseedAsync` (irreducible escape hatch) | up to 6 h |
| Source node offline (migration) | Orchestrator heartbeat timeout | Authority transfer + pre-flight DHT check | minutes |

Each failure class has exactly one primary handler. Pathways do not contend.

#### Wire format: replication factor on every announcement

The orchestrator's `vm.Spec.ReplicationFactor` flows through three places so the mesh can act on it
without round-trips:

1. `ResourceManifest` (HTTP `POST /manifests` body): `replicationFactor int json:"replicationFactor,omitempty"`.
2. `NewBlockAnnouncement` (GossipSub `decloud/blockstore/new-blocks` payload): same field.
3. `owners/{vmId}.meta` (local atomic JSON file, tmp+rename): `{replicationFactor, manifestVersion, lastUpdated}`.

Wire backward compatibility is preserved via `omitempty` — pre-Phase-F peers omit the field, which
post-Phase-F peers treat as "unknown RF" (skipped by RF-aware logic, same as ephemeral).

`LazysyncDaemon.NotifyBlockstoreManifestAsync` passes `vm.Spec.ReplicationFactor` on every push. The
blockstore's `handleManifests POST` writes `owners/{vmId}.meta` for VMOverlay manifests. Receivers of
`new-blocks` announcements write meta on first pull (under a version guard to avoid rewrite churn).
`publishNewBlock` reads meta when publishing, so each announcement carries the publisher's current view
of RF.

#### Phase F-B: Reactive repair via `needs-replica`

A new GossipSub topic `decloud/blockstore/needs-replica` carries advance notice of evictions:

```go
type NeedsReplicaAnnouncement struct {
    CID          string `json:"cid"`
    VMId         string `json:"vmId,omitempty"`
    Reason       string `json:"reason,omitempty"` // "lru_evict" | "confirmed_evict" | "presence_loss"
    SourcePeerID string `json:"sourcePeerId,omitempty"`
    Timestamp    string `json:"timestamp"`
}
```

`runGC()` builds a one-shot CID→vmIds inverted index (`buildCIDOwnerIndex`) at the top of each pass,
then for every block it's about to evict:

1. **Before** calling `DeleteBlock`, publish `needs-replica` per owning VM (cross-VM dedup handled by
   iterating the inverted index entry).
2. Call `DeleteBlock`.
3. Call `dht.Provide(c, false)` to withdraw the provider record.

Step 3 closes a long-standing bug: the LRU path previously did not withdraw, leaving stranded provider
records that lied about block availability for up to 24 h (the DHT record TTL).

Receivers (`handleNeedsReplica`) check `bstore.Has(cid)`. If they hold it and a per-CID 60-second
response cooldown has elapsed, they re-publish on `new-blocks` — at which point the existing XOR-
threshold pull machinery scatters the block to XOR-close peers via bitswap. The cooldown prevents
thundering-herd responses when many peers observe the same eviction signal.

#### Phase F-C: Proactive repair via presence-loss and survey

The presence subscription was refactored to populate a shared `presenceState` rather than a per-
goroutine map. Two background loops consume it:

**Presence-loss monitor** (`startPresenceLossMonitor`, 30 s tick):

- Identifies peers with `lastHeartbeat > 180 s ago` (3× heartbeat interval).
- Removes them from `presenceState` and, for each `(vmId, cid)` in their cached owner index, publishes
  `needs-replica` with `reason="presence_loss"`.
- The cache is populated by the replication survey below, so a peer that joined and died within a
  single survey cycle yields no signals — the next survey detects the gap via `cidCount` instead.

**Replication survey** (`startReplicationSurvey`, 5 min cadence, 60 s startup delay):

- For every local VM with `owners/{vmId}.meta.replicationFactor > 0`:
  - Fetch `/owners/{vmId}` from every presence peer (parallel, capped at `SurveyConcurrency = 16`).
  - Cache results in `presenceState.peers[*].ownerCids` — this feeds the presence-loss monitor.
  - Build a per-CID count of remote peers holding the block.
  - For each local CID with `cidCount < RF`, call `publishNewBlock` directly (subject to per-CID
    publisher cooldown).

Survey uses `publishNewBlock` rather than `needs-replica` because the surveyor itself holds the block —
direct advertise is one mesh hop shorter than the needs-replica indirection. The publisher-side
cooldown (`PublishCooldown = 15 min`) prevents the 5-minute survey from re-publishing the same CID on
every cycle while the network catches up. A separate hourly sweep loop bounds memory growth of the
cooldown sync.Maps.

#### Phase F-D: Orchestrator demotion

With reactive (F-B) and proactive (F-C) repair active, `LazysyncManager` no longer needs to drive
repair. Three changes implement the demotion:

1. **Audit cadence split** (`DataStore.GetPendingAuditManifestsAsync`):
   - `Version > ConfirmedVersion`: always audit (the orchestrator is the sole writer of
     `ConfirmedVersion`; initial confirmation must progress on the 5-minute audit cadence).
   - `ConfirmedVersion == 0 && stale > 30 min`: tight cadence retained — VMs cannot migrate until
     `ConfirmedVersion > 0`.
   - `ConfirmedVersion > 0 && stale > 6 h`: sentinel backstop. Was 30 min pre-Phase-F.

2. **`MaybeTriggerReannounceAsync` removed**: its call site in `AuditManifestAsync`, the function
   definition, and the `ReannounceCooldown` constant are all deleted. Partial coverage no longer
   triggers any orchestrator action — the mesh handles it. The `ConfirmedCids.Count == 0` drain
   detection (and the resulting `TriggerReseedAsync`) is retained as the catastrophic-loss escape
   hatch.

3. **Migration pre-flight** (`MigrationPreflightAsync` in `VmSchedulerService`): inserted between
   target selection and authority transfer in `MigrateVmAsync`. Samples 50 random CIDs from
   `manifest.ChunkMap`, walks the **target node's** DHT via `/providers/{cid}` (port 8080), and aborts
   the migration cycle (no authority transfer, retry on next 5-minute scan) if fewer than 90 % of
   sampled CIDs have at least one provider. HTTP failures (including 503 cold-DHT) are excluded from
   both numerator and denominator — same indeterminate-result semantics as `LazysyncManager`'s audit
   math. The pre-flight closes the gap created by the 6 h sentinel cadence: the ConfirmedChunkMap
   snapshot can be up to 6 h stale at migration time, and the pre-flight verifies the chunks are
   still actually fetchable from the target's view before committing to authority transfer.

#### Steady-state load profile

| Metric | Pre-Phase-F | Post-Phase-F |
|--------|-------------|--------------|
| Orchestrator DHT walks per VM per hour (steady state) | ~2 (30 min cadence) | ~0.17 (6 h cadence) |
| `MaybeTriggerReannounceAsync` log entries | per-VM, on every partial-coverage cycle | zero (function removed) |
| Repair latency, LRU eviction | next 5-min audit | seconds (mesh) |
| Repair latency, replica offline | 5–10 min (audit + reseed) | 3.5 min worst case (180 s timeout + 30 s tick + propagation) |
| Repair latency, silent wipe | next audit detects via DHT walk | next 5-min survey detects via owner-index diff |
| Repair latency, total mesh loss | 5–10 min | up to 6 h (sentinel) — fallback only |
| Migration safety against stale ConfirmedChunkMap | none | pre-flight DHT walk |

The 6 h sentinel cadence is the price paid for moving orchestrator DHT load off the steady-state
critical path. The migration pre-flight compensates by re-validating immediately before authority
transfer, the moment it actually matters.

---

## 7. Split-Brain / Zombie VM Prevention

When a node fails and a VM is migrated to a new host, the original node may come back online with the VM still present locally. Without proper fencing, this creates a **split-brain** scenario: two instances of the same VM running simultaneously.

### 7.1 The Problem

```
Timeline of a split-brain event:

T=0        T=5min      T=8min       T=15min      T=20min
 │           │           │            │            │
 ▼           ▼           ▼            ▼            ▼

Node A     Node A      Orchestrator  Node B      Node A
running    fails       declares A    boots       comes back!
VM-1       (crash,     dead,         VM-1        VM-1 resumes
           network)    migrates      from        automatically
                       to Node B     storage
                                      │            │
                                      ▼            ▼
                                 ┌─────────┐  ┌─────────┐
                                 │  VM-1   │  │  VM-1   │
                                 │ (new)   │  │ (zombie)│
                                 │ Node B  │  │ Node A  │
                                 └─────────┘  └─────────┘
                                      │            │
                                      └─────┬──────┘
                                            │
                                            ▼
                                 💥 TWO INSTANCES RUNNING
```

**Consequences of split-brain:**

| Issue | Impact |
|-------|--------|
| **IP Conflict** | Both VMs have same WireGuard IP → routing chaos |
| **State Divergence** | Node A has pre-crash state, Node B has migrated state + new writes |
| **Identity Collision** | Same VM ID, same wallet association, same credentials |
| **Data Corruption** | If both write to external services (DBs, APIs), inconsistent state |
| **Lazysync Conflict** | Both nodes attempt to push manifest updates for the same VM |

### 7.2 Solution: NodeId as Authority (KISS Approach)

The `VirtualMachine` model already has a `NodeId` field that tracks which node is authorized to run the VM. Rather than creating a separate lease service, we use this existing field as the **single source of truth**.

**Rule: Only the node whose ID matches `vm.NodeId` may run the VM.**

```
┌───────────────────────────────────────────────────────────────┐
│                      ORCHESTRATOR DB                          │
│                                                               │
│   VM-123:                                                     │
│     NodeId: "node-B"    ← Authoritative assignment           │
│     TargetNodeId: null                                        │
│     Status: Running                                           │
│                                                               │
└───────────────────────────────────────────────────────────────┘
                        │
          ┌─────────────┴─────────────┐
          │                           │
          ▼                           ▼
 ┌─────────────────┐         ┌─────────────────┐
 │     Node A      │         │     Node B      │
 │                 │         │                 │
 │  "Am I node-B?" │         │  "Am I node-B?" │
 │   NO → STOP VM  │         │   YES → RUN OK  │
 │                 │         │                 │
 └─────────────────┘         └─────────────────┘
```

This approach requires **no new services, no lease renewals, no fencing tokens** — just enforcement of an existing field.

### 7.3 Node Agent Implementation

#### 7.3.1 VM Reconciliation on Startup and Heartbeat

The Node Agent reconciles local VMs against the orchestrator's authoritative state:

```csharp
// In HeartbeatService or a dedicated VmReconciliationService

private async Task ReconcileLocalVmsAsync(CancellationToken ct)
{
    // Get all VMs running locally via libvirt
    var localVms = await _libvirtManager.GetAllVmsAsync();
    
    foreach (var localVm in localVms)
    {
        // Skip system VMs (relay, DHT, blockstore) — managed separately
        if (IsSystemVm(localVm.VmId))
            continue;
            
        // Query orchestrator for authoritative VM state
        var vmInfo = await _orchestratorClient.GetVmInfoAsync(localVm.VmId, ct);
        
        if (vmInfo == null)
        {
            // VM deleted from orchestrator — destroy locally
            _logger.LogWarning(
                "VM {VmId} not found in orchestrator, destroying orphan",
                localVm.VmId);
            await DestroyAndCleanupVmAsync(localVm.VmId);
            continue;
        }
        
        if (vmInfo.NodeId != _nodeId)
        {
            // We are NOT the authorized host — stop immediately
            _logger.LogWarning(
                "VM {VmId} assigned to node {AuthorizedNode}, not us ({OurNode}). " +
                "Destroying zombie VM.",
                localVm.VmId, vmInfo.NodeId, _nodeId);
            
            await DestroyAndCleanupVmAsync(localVm.VmId);
        }
    }
}

private async Task DestroyAndCleanupVmAsync(string vmId)
{
    // Force destroy the VM
    await _libvirtManager.DestroyVmAsync(vmId);
    
    // Optionally clean up local disk to free space
    // (the authoritative copy is now on the new node or in block store)
    await _diskManager.CleanupVmDiskAsync(vmId);
    
    _logger.LogInformation("Zombie VM {VmId} destroyed and cleaned up", vmId);
}
```

#### 7.3.2 Reconciliation Triggers

Reconciliation runs at multiple points to ensure quick detection:

| Trigger | Purpose |
|---------|---------|
| **Node Agent startup** | Catch VMs that were migrated while node was offline |
| **Every heartbeat cycle** | Continuous verification (every 30-60 seconds) |
| **On network reconnection** | After recovering from network partition |

#### 7.3.3 Fail-Safe Behavior

If the Node Agent cannot reach the orchestrator to verify VM ownership:

```csharp
private async Task ReconcileLocalVmsAsync(CancellationToken ct)
{
    foreach (var localVm in localVms)
    {
        try
        {
            var vmInfo = await _orchestratorClient.GetVmInfoAsync(localVm.VmId, ct);
            // ... normal reconciliation logic
        }
        catch (HttpRequestException ex) when (IsTransientError(ex))
        {
            // Cannot reach orchestrator — what to do?
            
            // Option A: Keep running (availability over consistency)
            // Suitable if network partitions are brief
            _logger.LogWarning(
                "Cannot verify VM {VmId} ownership — keeping running. " +
                "Will retry on next heartbeat.",
                localVm.VmId);
            
            // Option B: Stop after grace period (consistency over availability)
            // if (localVm.LastVerifiedAt < DateTime.UtcNow.AddMinutes(-5))
            // {
            //     await DestroyAndCleanupVmAsync(localVm.VmId);
            // }
        }
    }
}
```

**Recommendation:** Use Option A (keep running) for most cases. Brief network partitions are more common than actual migrations. The zombie will be cleaned up when connectivity resumes.

### 7.4 Migration Sequence with NodeId Update

```
BEFORE MIGRATION:
┌─────────────────────────────────────────────────────────────┐
│  VM-123:                                                     │
│    NodeId: "node-A"        ← A is authorized                │
│    TargetNodeId: null                                        │
│    Status: Running                                           │
└─────────────────────────────────────────────────────────────┘

STEP 1: Orchestrator detects Node A failure (missed heartbeats)

STEP 2: Orchestrator sets migration target
┌─────────────────────────────────────────────────────────────┐
│  VM-123:                                                     │
│    NodeId: "node-A"                                          │
│    TargetNodeId: "node-B"  ← Migration target set           │
│    Status: Migrating                                         │
└─────────────────────────────────────────────────────────────┘

STEP 3: Orchestrator atomically transfers authority
┌─────────────────────────────────────────────────────────────┐
│  VM-123:                                                     │
│    NodeId: "node-B"        ← B is NOW authorized            │
│    TargetNodeId: null      ← Cleared                        │
│    Status: Migrating                                         │
└─────────────────────────────────────────────────────────────┘

STEP 4: Orchestrator sends CreateVm command to Node B
  - Node B downloads overlay from block store (confirmed manifest)
  - Node B regenerates cloud-init from VM labels
  - Node B boots VM

STEP 5: Node B reports VM running → Status: Running

STEP 6: Node A comes back online
  - Heartbeat triggers reconciliation
  - For each local VM: query orchestrator
  - VM-123.NodeId = "node-B", but we're "node-A"
  - Result: destroy local VM-123
```

### 7.5 Lazysync Fencing

The lazysync daemon must also respect NodeId authority. A zombie VM should not push manifest updates:

```csharp
// In LazysyncDaemon on Node Agent

private async Task SyncVmAsync(string vmId, CancellationToken ct)
{
    // Verify we're still the authoritative host before pushing
    var vmInfo = await _orchestratorClient.GetVmInfoAsync(vmId, ct);
    
    if (vmInfo?.NodeId != _nodeId)
    {
        _logger.LogWarning(
            "Skipping lazysync for VM {VmId} — we're not the authorized host",
            vmId);
        return;
    }
    
    // Proceed with dirty block export and manifest push
    // ...
}
```

The orchestrator also validates manifest updates:

```csharp
// In BlockStoreController.cs

[HttpPost("manifest")]
public async Task<IActionResult> RegisterManifest([FromBody] ManifestUpdateRequest request)
{
    var vm = await _dataStore.GetVmAsync(request.VmId);
    
    if (vm == null)
        return NotFound();
    
    // Verify the reporting node is the authorized host
    if (vm.NodeId != request.NodeId)
    {
        _logger.LogWarning(
            "Rejecting manifest update for VM {VmId} from node {ReportingNode} — " +
            "authorized node is {AuthorizedNode}",
            request.VmId, request.NodeId, vm.NodeId);
        
        return StatusCode(403, new { error = "Not authorized to update this VM's manifest" });
    }
    
    // Process manifest update
    // ...
}
```

### 7.6 Network Fencing (Defense in Depth)

As an additional layer, the orchestrator can revoke the zombie VM's network access:

```
On migration:
1. Orchestrator updates vm.NodeId from A → B (primary fencing)
2. Orchestrator notifies relay VMs: revoke WireGuard peer for Node A's VM-1 subnet
3. Even if zombie VM keeps running, it cannot reach the network

Result: Zombie VM is isolated — can run locally but cannot:
- Communicate with users
- Access external services
- Push lazysync updates
- Cause harm
```

This is implemented by extending the relay peer management:

```csharp
// In RelayNodeService.cs

public async Task RevokeVmNetworkAccessAsync(string vmId, string oldNodeId, CancellationToken ct)
{
    var vm = await _dataStore.GetVmAsync(vmId);
    if (vm == null) return;
    
    // Find the relay serving the old node
    var oldNode = await _dataStore.GetNodeAsync(oldNodeId);
    if (oldNode?.CgnatInfo?.RelayNodeId == null) return;
    
    var relay = await _dataStore.GetNodeAsync(oldNode.CgnatInfo.RelayNodeId);
    if (relay?.RelayNodeInfo?.RelayVmId == null) return;
    
    // Remove the VM's WireGuard peer from the relay
    // (The VM's tunnel IP was associated with the old node)
    await RemoveWireGuardPeerAsync(relay, vm.NetworkConfig.TunnelIp, ct);
    
    _logger.LogInformation(
        "Revoked network access for VM {VmId} on old node {OldNode}",
        vmId, oldNodeId);
}
```

### 7.7 Optional Enhancement: Assignment Version

For extra protection against race conditions (e.g., NodeId bouncing due to flapping), add a version counter:

```csharp
public class VirtualMachine
{
    public string NodeId { get; set; }
    public string? TargetNodeId { get; set; }
    
    /// <summary>
    /// Incremented on every NodeId change. Used as fencing token
    /// to detect stale assignments.
    /// </summary>
    public long NodeAssignmentVersion { get; set; } = 0;
}
```

The Node Agent stores the version when it receives a CreateVm command. On reconciliation:

```csharp
if (localAssignmentVersion < orchestratorAssignmentVersion)
{
    // Our assignment is stale — we were migrated away and back
    // but our local state is from a previous incarnation
    await DestroyAndCleanupVmAsync(vmId);
}
```

This handles edge cases where:
- VM migrates A → B → A (same NodeId, but different incarnation)
- Node has stale state from before migration

### 7.8 Orchestrator Heartbeat Response Enhancement

To reduce per-VM queries, the orchestrator can proactively inform nodes about invalid VMs:

```csharp
public class HeartbeatResponse
{
    // Existing fields...
    public List<NodeCommand>? PendingCommands { get; set; }
    public CgnatConfigResponse? CgnatConfig { get; set; }
    
    /// <summary>
    /// VM IDs that this node should NOT be running.
    /// Includes VMs that were migrated away, deleted, or never assigned to this node.
    /// Node agent should destroy any of these found locally.
    /// </summary>
    public List<string>? InvalidVmIds { get; set; }
}
```

The orchestrator populates this by comparing the node's reported running VMs against its authoritative records:

```csharp
// In HeartbeatService on Orchestrator

private List<string> GetInvalidVmsForNode(string nodeId, List<string> reportedRunningVmIds)
{
    var invalidVms = new List<string>();
    
    foreach (var vmId in reportedRunningVmIds)
    {
        var vm = _dataStore.GetVm(vmId);
        
        // VM doesn't exist or belongs to different node
        if (vm == null || vm.NodeId != nodeId)
        {
            invalidVms.Add(vmId);
        }
    }
    
    return invalidVms;
}
```

### 7.9 Summary

| Layer | Mechanism | Purpose |
|-------|-----------|---------|
| **Primary** | `NodeId` field check | Only authorized node runs VM |
| **Reconciliation** | Startup + heartbeat checks | Detect and destroy zombies |
| **Lazysync** | NodeId validation | Reject manifest updates from zombies |
| **Network** | WireGuard peer revocation | Isolate zombies from network |
| **Optional** | `NodeAssignmentVersion` | Handle edge cases with flapping |

**Key principle:** The existing `NodeId` field is the authoritative lease. No separate lease service needed — just enforcement of what already exists.

---

## 8. Implementation Order

### Phase A: Orchestrator — Core Block Store

1. **BlockStoreVmSpec.cs** — Resource specification (5% duty, 512 MB RAM)
2. **BlockStoreInfo** on Node.cs — Model for tracking state (capacity, usage, status)
3. **VmType.BlockStore** — Add to enum
4. **IBlockStoreService / BlockStoreService** — Deployment, manifest lifecycle, replication audit, migration planning
5. **BlockStoreController.cs** — `/join`, `/announce`, `/locate`, `/manifest`, `/audit`, `/stats`
6. **SystemVmLabelSchema** — Add required labels
7. **ObligationEligibility** — Uncomment BlockStore eligibility, lower RAM threshold to 2 GB
8. **SystemVmReconciliationService** — Wire up deployment method
9. **VmService** — Add BlockStore to isSystemVm check
10. **Program.cs** — Register DI

### Phase B: NodeAgent — VM Template & Binary

11. **VmType.BlockStore** — Add to enum
12. **blockstore-vm-cloudinit.yaml** — Cloud-init template
13. **blockstore-node Go binary** — Core storage engine with DAG/manifest support, GossipSub subscription for `decloud/blockstore/new-blocks`, adaptive XOR threshold pull logic
14. **blockstore-bootstrap-poll.sh** — Orchestrator peer discovery
15. **blockstore-notify-ready.sh** — Callback to NodeAgent
16. **blockstore-health-check.sh** — Health monitoring
17. **blockstore-dashboard.py** — Status dashboard
18. **BlockStoreCallbackController.cs** — NodeAgent callback endpoint
19. **CommandProcessorService** — Handle VmType.BlockStore in CreateVm

### Phase C: Basic Integration Testing

20. Build Go binary, encode as gzip+base64
21. Test deployment on single node
22. Test multi-node block exchange via bitswap
23. Test local LRU GC (eviction under capacity pressure, provider record withdrawal)
24. Test self-healing (reconciliation redeploy)

### Phase D: Lazysync & Migration — ✅ Production-verified 2026-04-20

**End-to-end confirmed:** VM created on MSI (Turkey, CGNAT) → versions 1–9 replicated incrementally → MSI shutdown → overlay reconstructed on dedixlabvm (US East) from chunk map → user files preserved, browser terminal and ingress functional post-migration.

**Items implemented beyond the plan (discovered during testing):**
- **MAC-pinned netplan fix** — migration `bootcmd` overwrites `/etc/netplan/50-cloud-init.yaml` before networkd starts; source node MAC address in the overlay caused `network-online.target` to hang forever on the receiving node
- **Migration cloud-init minimal config** — `package_update: false`, `package_upgrade: false`, no apt operations to prevent cloud-init config stage blocking
- **`RemoveDirtyBitmapAsync`** — cleans the overlay-carried `lazysync` bitmap on the receiving node before re-adding; without this, `AddDirtyBitmap` fails every cycle causing fallback to full export
- **Ingress registered before `WaitForPrivateIpAsync`** — domain live immediately after migration boot completes
- **Post-migration reseed** — `ConfirmedVersion` reset to 0 + `ReseedVm` command sent to receiving node; re-enters manifest into audit queue and pushes all blocks to new local blockstore

**Items not yet implemented (gaps in Phase D):**
- Item 32 — DHT proximity endpoint `GET /proximity/{cid}`

25. **ReplicationFactor on VmSpec/VirtualMachine** — add field (default 3), add to
    `CreateVmRequest` (optional, validated: 0/1/3/5), set in `VmService.CreateVmAsync`
    for non-system VMs, add `LastKnownOverlayBytes` + `LastLazysyncAt` fields
26. **Scheduler BlockStore filter** — `VmSchedulingService` rejects nodes without Active
    BlockStore when `replicationFactor > 0`; ephemeral VMs (factor=0) bypass filter
27. **Block-based storage pricing** — replace overlay_gb estimate with exact
    block-count billing: `blockCount × blockSizeKb / 1024 × replicationFactor × costPerMbPerHour`.
    Add `StoragePerMbPerHour` to `PricingConfig`. Add `BlockCount` + `BlockSizeKb` to
    `ManifestRecord`. Update `CalculateHourlyRate` to use block count once available
    (falls back to `DiskBytes × 5% / blockSizeBytes` estimate before first lazysync cycle).
    `replicationFactor=0` VMs pay zero storage cost.
28. **LazysyncManager** in orchestrator — manifest version tracking, confirmed vs current,
    provider count audit loop using `replicationFactor` as N; MongoDB persistence for
    ManifestRecord (replacing in-memory stub from Phase A); updates `BlockCount` on
    `ManifestRecord` and `CurrentManifestBlockCount` on `VirtualMachine` each cycle
    for accurate billing
29. **Lazysync daemon** on node agent — QEMU QMP integration (dirty bitmaps, incremental
    backup), overlay chunking via `qemu-img map` for sparse cluster discovery, block push,
    manifest update cycle. Skip VMs with `replicationFactor=0` entirely
30. **Initial overlay seeding** — push allocated overlay clusters on first enrollment,
    progress tracking, rate limiting
31. **GossipSub scatter propagation** — block store publishes new CIDs to
    `decloud/blockstore/new-blocks`; receiving nodes evaluate XOR distance + adaptive
    threshold; pull via bitswap if close enough
31a. ✅ **settleCycle() integration** — `OnChainSettlementService.ProcessBatchChunkAsync`
     calls `BuildSettleCycleRequestAsync` to assemble per-VM arrays (`blockCounts`,
     `blockSizeKbs`, `replicationFactors`, `vmIds`) from `ManifestRecord` and per-storage-node
     arrays (`storageNodeWallets`, `storageBytes`) from all active `BlockStoreInfo` nodes.
     Routes to `BlockchainService.ExecuteSettleCycleAsync` (calls `settleCycle()`) when VMs
     are present; falls back to `ExecuteBatchSettlementAsync` (`batchReportUsage`) only for
     empty batches. `DeCloudEscrow.sol` `_distributionPhase` distributes the storage pool
     proportionally to node `storageBytes`. ABI loaded from embedded `DeCloudEscrow.abi.json`.
     `replicationFactor=0` VMs pass 0 — contract computes zero storage billing correctly.
31b. **GossipSub fetch retry queue** — block store binary maintains an in-memory
     retry queue (`chan cid.Cid`, capacity 500) fed by two failure paths:
     (a) semaphore skip (`bitswap_fetch_skip`) — CID received via GossipSub but all
     concurrent fetch slots were occupied at arrival;
     (b) fetch timeout (`bitswap_fetch_fail`) — slot acquired but `GetBlock` exceeded
     30s deadline, typically on the first cross-region DHT walk before the Kademlia
     routing table is warm.
     A `startRetryLoop` background goroutine ticks every 60 seconds (after a 30s
     startup delay for routing table warmup) and drains the queue **sequentially**
     (concurrency=1). Sequential processing is intentional: the first retry performs
     the DHT walk and caches the provider record; all subsequent retries in the same
     batch resolve via the warm cache in under 8ms each.
     Per-CID attempt tracking (sync.Map, string→int) caps retries at 3. On max
     retries exceeded the CID is logged (`retry_give_up`) and abandoned; the DHT
     neighborhood scan (item 32) handles anything that genuinely cannot be reached.
     Retry fetches use a 60s timeout (vs 30s for the initial GossipSub path) since
     the routing table is warm at retry time and the longer window handles cross-region
     high-latency links. Successfully recovered blocks are announced to the DHT with
     role `retry_pull` and are eligible for GC like any other block.
     New diag events: `retry_fetch`, `retry_fetch_fail`, `retry_give_up`,
     `retry_queue_full`.
     **Relation to DHT:** The retry queue is the narrow (failure-triggered) recovery
     path. It only recovers blocks where GossipSub delivery was confirmed but bitswap
     failed. The DHT neighborhood scan (item 32) is the general recovery path for
     blocks where GossipSub was missed entirely. The two are complementary and
     non-redundant — GossipSub → retry queue is the hot path, neighborhood scan is
     the cold path.
32. **DHT proximity endpoint** — `GET /proximity/{cid}` on DHT VM for neighborhood scan
    and diagnostics
33. **Migration planner** — PlanMigrationAsync with scheduling fit + resource headroom;
    requires candidate node to have Active BlockStore when migrating replicated VMs
34. **Source-offline alerting** — detect under-replicated VMs on node failure:
    `replicationFactor=0` → LOST (ephemeral, no alert);
    `confirmedVersion=0` → UNRECOVERABLE;
    `confirmedVersion < currentVersion` → RECOVERING (partial data loss);
    `confirmedVersion == currentVersion` → MIGRATING (no data loss)
35. **Disk reconstruction** — fetch overlay chunks from block store via bitswap + write to
    new qcow2 at correct offsets + regenerate cloud-init ISO from VM labels
36. **End-to-end migration test** — simulate node failure, verify VM rescheduling from
    confirmed manifest (base image + overlay from bitswap + cloud-init)
37. ✅ **Owner-indexed block deletion** — blockstore binary maintains a per-VM owner index
    (`/var/lib/decloud-blockstore/owners/{vmId}.cids`, append-only, newline-delimited)
    updated on every `POST /blocks?cid={cid}&owner={vmId}` write. On VM deletion,
    NodeAgent calls `DELETE /owners/{vmId}` — blockstore reads the owner file, deletes
    each referenced block from FlatFS, withdraws DHT provider records, and removes the
    owner file. Single HTTP call regardless of block count (3,420 blocks = 1 call).
    Reference counting: a block shared by multiple owners (rare in practice — VM overlays
    are unique) is only evicted from FlatFS when its last owner is deleted.
    Orchestrator hook: `VmLifecycleManager.OnVmDeletedAsync` calls
    `DataStore.DeleteManifestAsync(vmId)` to remove the MongoDB manifest record and
    publishes `decloud/blockstore/vm-deleted` via GossipSub — all remote blockstore nodes
    evict the VM's blocks on receipt.
    NodeAgent hook: `LibvirtVmManager.DeleteVmAsync` calls `NotifyBlockstoreOwnerDeleteAsync`
    after virsh teardown → `DELETE http://{blockstoreVm}:5090/owners/{vmId}`.
    `LazysyncDaemon` appends `?owner={vmId}` to every `POST /blocks` call.
    Files: `blockstore-node main.go` (`handleOwnerOps`, `deleteOwnerBlocks`, owner append
    in `handleBlocks`), `LazysyncDaemon.cs`, `LibvirtVmManager.cs`,
    `VmLifecycleManager.cs` (`OnVmDeletedAsync`, `PublishVmDeletedEventAsync`).

### Phase E: Split-Brain / Zombie VM Prevention — ✅ Complete

**Implementation note:** The PLAN originally proposed a `VmReconciliationService` making per-VM `GetVmInfoAsync` queries (pull model). The actual implementation uses a more efficient push model — the orchestrator proactively identifies invalid VMs during the existing heartbeat exchange. No separate reconciliation service or new endpoint is required. The push model is strictly better: lower latency, zero extra HTTP calls per heartbeat cycle.

34. ✅ **Zombie VM detection and destruction** — implemented via two complementary paths:
    - **Pre-heartbeat `TargetNodeId` check** in `HeartbeatService.cs`: before each heartbeat
      round-trip, scans all locally running VMs for `TargetNodeId != myNodeId` and fires
      `Task.Run(() => _vmManager.DeleteVmAsync(vmId))` immediately. Catches migrations the
      instant the source node reconnects, without waiting for the orchestrator response.
    - **`InvalidVmIds` in `HeartbeatResponse`** (item 36): orchestrator cross-checks every
      `ActiveVm` in the heartbeat against `vm.NodeId` and returns mismatches as `invalidVmIds`.
      `OrchestratorClient.cs` processes the list and calls `DeleteVmAsync` for each.
    - `LibvirtVmManager.DeleteVmAsync` handles full teardown: virsh destroy → undefine →
      `NotifyBlockstoreOwnerDeleteAsync` (local block cleanup) → directory cleanup.
    Both paths are active every heartbeat cycle (~30–60 s). Zombie lifetime ≤ one heartbeat
    interval after node reconnection.

35. ✅ **`GetVmInfoAsync` endpoint** — not needed. The `InvalidVmIds` push model in item 36
    replaces the originally planned per-VM pull queries. The orchestrator already has the
    authoritative state; pushing it in the heartbeat response is more efficient than the
    NodeAgent polling each VM separately.

36. ✅ **`InvalidVmIds` in `HeartbeatResponse`** — implemented in `NodeService.cs`
    (orchestrator) and `OrchestratorClient.cs` (NodeAgent). For each VM reported in the
    heartbeat, the orchestrator checks `vm == null || vm.NodeId != nodeId` and returns
    mismatches. NodeAgent destroys them on receipt.

37. ✅ **Lazysync NodeId validation** — resolved by item 38. The `BlockStoreController`
    NodeId fence rejects any stale manifest push server-side before it can be persisted,
    closing the narrow window that existed between zombie creation and `DeleteVmAsync` firing.

38. ✅ **BlockStoreController NodeId check on manifest POST** — `NodeId` added to
    `BlockStoreManifestRequest` DTO (was silently dropped by deserializer before). Guard
    added at the top of `RegisterManifest`: fetches VM, returns HTTP 403 if
    `request.NodeId != vm.NodeId`. Skipped when `request.NodeId` is empty for backward
    compatibility with older daemon versions. `OrchestratorClient.RegisterManifestAsync`
    already sent `nodeId` in the payload — no NodeAgent changes required.

39. 🔲 **Network fencing** (optional) — WireGuard peer revocation on migration to isolate
    zombie VMs from the network before `DeleteVmAsync` fires. Defense-in-depth only;
    the `InvalidVmIds` path already destroys zombies within one heartbeat cycle.

40. 🔲 **NodeAssignmentVersion** (optional) — fencing token for the edge case where a VM
    migrates A → B → A (same `NodeId`, different incarnation). Low probability in practice.

41. 🔲 **Integration test** — simulate node failure, migration, node return, verify zombie
    destroyed and blockstore blocks cleaned up end-to-end.

### Phase F: Self-Healing Replication — ✅ Complete 2026-05-31

**Goal:** Move routine replication repair off the orchestrator's audit critical path and onto the
blockstore mesh, leaving the orchestrator with only the responsibilities its centralised authority
requires (advancing `ConfirmedVersion`, catastrophic-loss escape hatch, migration authority transfer).

**Foundation:** All four sub-phases are wire-compatible with pre-Phase-F nodes. Mixed-version meshes
are safe during rollout — pre-Phase-F peers neither publish nor subscribe to `needs-replica`, do not
run the survey loop, and their `replicationFactor` field is `omitempty`-omitted on the wire.
Self-healing improves monotonically as the upgraded fraction grows.

**End-to-end:** With Phases F-A+F-B+F-C+F-D deployed, the orchestrator's `MaybeTriggerReannounceAsync`
log entries vanish entirely from steady-state operation (the function is removed); the mesh handles
all three pre-existing failure classes (LRU eviction, replica offline, silent wipe) within seconds
to minutes; and the orchestrator's audit DHT load drops ~12× at steady state (30 min → 6 h cadence
for confirmed manifests).

#### Phase F-A: Replication factor on the wire — ✅ Complete

42. ✅ **`ResourceManifest.ReplicationFactor` field** — `int json:"replicationFactor,omitempty"`
    added to the manifest struct in `system-vms/blockstore/src/main.go`. Carried in `POST /manifests`
    payloads.

43. ✅ **`NewBlockAnnouncement.ReplicationFactor` field** — same field added to the GossipSub
    `new-blocks` payload. Lets receivers persist the RF policy locally on first pull.

44. ✅ **`ownerMetadata` + `loadOwnerMeta` + `saveOwnerMeta`** — new per-VM JSON file
    `owners/{vmId}.meta` storing `{replicationFactor, manifestVersion, lastUpdated}`. Atomic write
    via tmp+rename. Read path returns `(meta, false)` for missing files so callers conflate
    "ephemeral" and "pre-Phase-F" safely.

45. ✅ **`handleManifests POST` writes meta** — on every `POST /manifests` for
    `ResourceType=VMOverlay`, `saveOwnerMeta` persists the RF policy. No-op when RF ≤ 0 (avoids
    stale meta files for ephemeral VMs).

46. ✅ **`publishNewBlock` reads meta** — block announcements now look up RF from
    `owners/{vmId}.meta` and populate `NewBlockAnnouncement.ReplicationFactor`. Backward compatible:
    if no meta exists, the field stays zero and `omitempty` omits it from the wire.

47. ✅ **`handleNewBlockAnnouncement` persists meta on receiver side** — when an inbound announcement
    has `ReplicationFactor > 0` and `ManifestVersion > existing`, the receiver writes its own
    `owners/{vmId}.meta`. Version guard avoids rewrite churn during a large seeding burst.

48. ✅ **`LazysyncDaemon.NotifyBlockstoreManifestAsync` signature** — accepts a new
    `int replicationFactor` parameter and includes it in the manifest payload. Call site passes
    `vm.Spec.ReplicationFactor`. File:
    `src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs`.

#### Phase F-B: Reactive repair (GC eviction → `needs-replica`) — ✅ Complete

49. ✅ **`GossipSubNeedsReplicaTopic = "decloud/blockstore/needs-replica"`** — new constant in
    `main.go` alongside existing topic constants.

50. ✅ **`NeedsReplicaAnnouncement` struct** — wire format documented in §6.8. `Reason` is one of
    `lru_evict | confirmed_evict | presence_loss` (the `survey` case uses `new-blocks` directly).

51. ✅ **`needsReplicaTopic` + `needsReplicaCooldown` struct fields on `BlockNode`** — topic
    reference held on struct so `publishNeedsReplica` (called from `runGC`, not a goroutine) can
    publish without re-joining. Cooldown is a `sync.Map` (per-CID timestamps) preventing receive-
    side thundering-herd responses.

52. ✅ **`publishNeedsReplica` + `shouldRespondToNeedsReplica` + `startNeedsReplicaSubscription` +
    `handleNeedsReplica`** — full publisher/subscriber pair. Receive-side flow: decode CID,
    `bstore.Has(cid)`, check per-CID 60 s cooldown, re-publish on `new-blocks`. Topic is joined
    synchronously in `main()` before the GC loop starts (otherwise the first eviction burst would
    publish to a nil topic).

53. ✅ **`buildCIDOwnerIndex` helper** — single O(total CIDs) inverted-index scan at the top of
    every `runGC` pass. Replaces what would otherwise be O(evicted × VMs) repeated owner-file
    scans during bulk eviction.

54. ✅ **`runGC` Priority 1 (confirmed evict) publishes needs-replica before delete** — per-owning-
    VM fanout from `buildCIDOwnerIndex` lookup. Empty owners slice (cross-VM dedup race) → publish
    with `vmId=""`.

55. ✅ **`runGC` Priority 2 (LRU evict) publishes needs-replica + withdraws DHT provider** — same
    signaling pattern as Priority 1, PLUS the previously-missing `dht.Provide(c, false)` call.
    Pre-Phase-F LRU left stranded provider records until 24 h TTL, causing `FindProviders` to lie
    about block availability — this gap is closed alongside the self-healing addition.

#### Phase F-C: Proactive repair (presence loss + survey) — ✅ Complete

56. ✅ **`presencePeer` + `presenceState` types** — shared mesh-liveness view. `presencePeer`
    carries `{apiURL, lastHeartbeat, ownerCids map[vmId]map[cid]}`. `presenceState` is a
    `sync.RWMutex`-protected map of peer IDs to `*presencePeer`.

57. ✅ **`BlockNode.presenceState + publishCooldown` fields** — `presenceState` populated by the
    presence subscription on every heartbeat. `publishCooldown` is a `sync.Map` of per-CID
    publisher-side timestamps (15 min window) used by both presence-loss and survey publishes.

58. ✅ **`startPresenceTopic` refactored** — receiver goroutine now writes to the shared
    `presenceState` instead of a per-goroutine `seenPeerURLs` map. Catchup is still triggered only
    on new-peer or URL-changed events; repeat heartbeats just refresh the timestamp silently.

59. ✅ **`startPresenceLossMonitor` (30 s tick)** — `PresenceTimeout = 180 s` (3× heartbeat
    interval), `PresenceCheckInterval = 30 s`. Peers timing out are removed under write lock; their
    cached `ownerCids` are then signaled via `publishNeedsReplica` with `reason="presence_loss"`
    outside the lock. Cache-empty case (peer joined and died inside one survey cycle) is the
    survey's responsibility — see item 60.

60. ✅ **`startReplicationSurvey` (5 min cadence, 60 s startup delay)** — for each local VM with
    `owners/{vmId}.meta.replicationFactor > 0`:
    - Snapshot presence peers under read lock to avoid holding the lock during HTTP fetches.
    - Parallel-fetch `/owners/{vmId}` from every presence peer (`SurveyConcurrency = 16`,
      `fetchPeerOwnerCIDs`).
    - Cache fetched CID sets in `presenceState.peers[*].ownerCids` (`cacheOwnerCIDs`) — feeds the
      presence-loss monitor.
    - Build per-CID remote count; for each local CID with `cidCount < RF`, call `publishNewBlock`
      directly (subject to the publisher cooldown).

61. ✅ **`startCooldownCleanup` (hourly sweep)** — `sweepCooldown` walks each `sync.Map` via
    `Range`, deletes entries with `timestamp < now − 2 × window`. Bounded memory on long-uptime
    nodes; no functional effect (entries past 2× window already cannot suppress anything).

62. ✅ **`main()` wires all three goroutines** — `startPresenceLossMonitor`,
    `startReplicationSurvey`, `startCooldownCleanup` launched after the synchronous
    `startNeedsReplicaSubscription` join.

#### Phase F-D: Orchestrator demotion — ✅ Complete

63. ✅ **`GetPendingAuditManifestsAsync` predicate split** — initial-confirmation 30 min vs sentinel
    6 h. See §6.8 for the exact branch logic. File: `src/Orchestrator/Persistence/DataStore.cs`.

64. ✅ **`MaybeTriggerReannounceAsync` removed** — call site deleted from `AuditManifestAsync`,
    function definition deleted, `ReannounceCooldown` constant deleted. The mesh handles partial-
    coverage signaling now. `TriggerReseedAsync` (drain-detection escape hatch) is retained
    unchanged. File: `src/Orchestrator/Services/LazysyncManager.cs`.

    `ManifestRecord.LastReannounceAt` field is dormant post-Phase-F (no code writes it). Left in
    the schema for forward compatibility; removable in a future cleanup once live records have
    aged out.

65. ✅ **`MigrationPreflightAsync` added** — `private async Task<bool>` helper on
    `VmSchedulerService`, inserted into `MigrateVmAsync` as Step 2.5 between target selection and
    atomic authority transfer. Sample size 50, pass threshold 90 %, per-CID timeout 10 s, total
    concurrency cap 8. Returns:
    - `true` (allow) when target has no DHT VM — degrade to existing behaviour.
    - `false` (defer) when every sampled CID returns indeterminate — target DHT unreachable.
    - `passRate >= 0.90` otherwise.

    On defer, pushes a status message to the VM and returns without authority transfer; next scan
    cycle retries. File: `src/Orchestrator/Background/BackgroundServices.cs`.

#### Phase F file inventory

- **`system-vms/blockstore/src/main.go`** — all blockstore-side changes (F-A wire fields, F-A meta
  helpers, F-B `needs-replica` subsystem, F-B GC signaling, F-C presenceState refactor, F-C
  presence-loss monitor, F-C survey, F-C cooldown sweep, main goroutine wiring).
- **`src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs`** —
  `NotifyBlockstoreManifestAsync` signature + payload (F-A).
- **`src/Orchestrator/Persistence/DataStore.cs`** — `GetPendingAuditManifestsAsync` predicate (F-D).
- **`src/Orchestrator/Services/LazysyncManager.cs`** — `MaybeTriggerReannounceAsync` removal (F-D).
- **`src/Orchestrator/Background/BackgroundServices.cs`** — `MigrationPreflightAsync` helper and
  Step 2.5 call site in `MigrateVmAsync` (F-D).

### Phase G: Overlay Snapshot Consistency — 🟡 Pending

**Goal:** Upgrade lazysync capture from crash-consistent to application-consistent by bracketing
the qcow2 read step in a `guest-fsfreeze-freeze` / `guest-fsfreeze-thaw` pair. Eliminates the
torn-write failure mode exposed by live-migration testing on 2026-05-31 (full diagnostic in §6.1.4).

**Why this is its own phase, not a D-fix:** Phase D was declared complete after end-to-end migration
was confirmed for the *base-image + overlay reconstruction* path. The 2026-05-31 test re-validated
that path after PR1+PR2 shipped content-addressed base images. PR1+PR2 held; the new failure mode
sits at a different boundary (overlay capture consistency) and warrants explicit scoping rather
than being folded into the older phase's history. Future readers should be able to trace exactly
which boundary failed and which boundary fixed it.

**Foundation:** Wire-compatible with all prior phases. No orchestrator changes, no wire format
changes, no data-store changes. Pure node-agent local concern.

66. **`IQmpClient` extension** — add `Task<int> FsFreezeAsync(vmId, ct)`,
    `Task<int> FsThawAsync(vmId, ct)`, `Task<bool> GuestAgentPingAsync(vmId, ct)` to the interface.
    Update the interface summary to acknowledge that the wrapper now handles both
    `virsh qemu-monitor-command` (QMP) and `virsh qemu-agent-command` (guest agent). File:
    `src/DeCloud.NodeAgent.Core/Interfaces/Qmp/IQmpClient.cs`.

67. **`QmpClient` implementation** — implement the three new methods. Add a private
    `SendAgentAsync(vmId, execute, arguments, timeout, ct)` helper mirroring the existing
    `SendAsync` but targeting `qemu-agent-command` and accepting a per-call timeout (2 s for ping,
    30 s for freeze/thaw — matching libvirt's `virsh domfsfreeze` default). Same JSON escaping,
    same error surface. File: `src/DeCloud.NodeAgent.Infrastructure/Services/Qmp/QmpClient.cs`.

68. **`LazysyncDaemon.RunUnderGuestFreezeAsync` helper** — private method taking
    `(string vmId, Func<Task> captureBody, CancellationToken ct)`. Implements the full
    sequence: `GuestAgentPingAsync` precheck → `FsFreezeAsync` (catches and logs failures,
    proceeds without freeze) → invoke `captureBody` → `finally`: `FsThawAsync` with
    `CancellationToken.None` so cancellation still thaws. Thaw failures logged as ERROR but not
    rethrown — libvirt's own `fsfreeze-timeout` is the last-resort safety net. File:
    `src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs`.

69. **Wrap first-cycle capture** — in `SyncVmAsync`'s `!state.BitmapCreated` branch, replace the
    direct call to `ExportOverlayAsync(diskPath, tmpPath, ct)` with
    `RunUnderGuestFreezeAsync(vm.VmId, () => ExportOverlayAsync(diskPath, tmpPath, ct), ct)`.
    The freeze brackets only the host-side `qemu-img convert` read, not the chunk scan,
    CID computation, or block push that follow. Same file as item 68.

70. **Wrap incremental capture** — in the `else` branch (BitmapCreated == true), replace the direct
    call to `DriveBackupIncrementalAsync(...)` with
    `RunUnderGuestFreezeAsync(vm.VmId, () => _qmpClient.DriveBackupIncrementalAsync(vm.VmId, driveNode, BitmapName, tmpPath, ct), ct)`.
    Freezing before `drive-backup` ensures any just-committed application data lands in this cycle's
    bitmap export instead of waiting for the next cycle to catch up. Same file as item 68.

71. **Verification: positive path** — re-run the 2026-05-31 migration test. Deploy a fresh tenant
    VM with default cloud-init on the offline-prone source node, wait for `IsFullyReady`, wait for
    the first lazysync cycle (logs must show `froze N filesystem(s)` then `thawed N filesystem(s)`).
    Force migration. On the target, `guestmount` the overlay and inspect
    `libnetplan.cpython-311.pyc`: must report "Python 3.11 byte-compiled" via `file`, no zero-tail,
    structurally valid. cloud-init `init-local` on the target must complete without `ValueError`.

72. **Verification: graceful degradation** — deploy a tenant VM whose cloud-init removes
    `qemu-guest-agent`. Lazysync must still produce blocks (logs show
    `guest agent unresponsive — capturing crash-consistent`), manifest reaches Protected, VM
    remains replicable. No regression on agentless workloads.

73. **Verification: thaw safety** — induce a freeze failure mid-cycle (kill the in-guest agent
    between `FsFreezeAsync` and the body, or force `FsThawAsync` to throw). Confirm: (a) the body
    exception (if any) propagates, (b) thaw is still attempted, (c) on thaw failure the ERROR is
    logged but does not propagate, (d) libvirt's `fsfreeze-timeout` auto-thaws the guest within
    its bounded window. Guest must remain writable after the dust settles.

#### Phase G file inventory

- **`src/DeCloud.NodeAgent.Core/Interfaces/Qmp/IQmpClient.cs`** — interface additions (item 66).
- **`src/DeCloud.NodeAgent.Infrastructure/Services/Qmp/QmpClient.cs`** — implementation + `SendAgentAsync`
  helper (item 67).
- **`src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs`** —
  `RunUnderGuestFreezeAsync` helper, wrapping of both capture paths (items 68–70).

No orchestrator-side files. No `Shared` contract changes. No data-store schema changes.

---

### Phase H: Bounded-Freeze Capture + Reconstruction Hardening — ✅ Complete 2026-06-03

**Goal:** Three concrete fixes identified by extended migration testing after Phase G shipped.

#### H-1: Replace `qemu-img convert --force-share` with `drive-backup sync=top`

**Problem (identified 2026-06-03):** Phase G wrapped the first-cycle capture (`qemu-img convert`) in a guest freeze. On a 500 MB overlay the freeze held for the full duration of the convert — 5–30 seconds depending on I/O. For incremental cycles, `drive-backup sync=incremental` uses QEMU's internal dirty-bitmap machinery and is sub-second; but first-cycle captures wrote the entire overlay, pausing every guest filesystem for the convert duration. A 10 GB active overlay could pause a guest for minutes.

**Fix:** Replace the first-cycle full-overlay `qemu-img convert --force-share` path with `drive-backup sync=top`. `sync=top` instructs QEMU to export only the blocks in the topmost BDS (the overlay), not the full merged disk. This is the overlay's allocated data only — the same blocks lazysync was already targeting — but fetched from QEMU's live block layer rather than via a host-side `qemu-img` invocation.

Consequences:
- The freeze brackets only the QMP call that starts the `drive-backup` job, not the data export itself. QEMU unfreezes the guest as soon as the backup job is queued (sub-second), then exports asynchronously.
- `GetOverlayChunkOffsetsAsync` (the `qemu-img map` whitelist computation) is **deleted**. `sync=top` filters to the topmost BDS natively; no external whitelist is needed.
- The first-cycle freeze duration drops from seconds/minutes to sub-second for all overlay sizes.
- Lazysync trails the live disk by 9–15 s (the background copy duration) rather than pausing it.

**Validated:** msi-a4b5 test on 2026-06-03 — freeze and thaw completed in the same second, lazysync trail confirmed 9–15 s, `.pyc` files intact end-to-end.

```
Phase G + H combined first-cycle sequence:
  guest-ping (2 s timeout)
  guest-fsfreeze-freeze     → N filesystems frozen
  QMP: drive-backup sync=top target=tmpPath  → backup job queued
  guest-fsfreeze-thaw       → guest unfrozen (sub-second total freeze)
  [background] QEMU copies overlay blocks to tmpPath asynchronously
  [background] LazysyncDaemon scans tmpPath, computes CIDs, pushes blocks
```

**Files changed:** `src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs` (replace first-cycle path, delete `GetOverlayChunkOffsetsAsync`).

---

#### H-2: Remove `RunFsckOnOverlayAsync`

**Problem (identified 2026-06-03):** After `ReconstructOverlayAsync` assembled the target overlay from blockstore chunks, the node agent ran `fsck.ext4 -fy` on the overlay via `qemu-nbd`. The intent was to repair crash-consistent filesystem state before boot.

**Root cause of regression:** `fsck.ext4 -fy` applies crash-recovery heuristics — including resolving "duplicate block" conflicts between normal inodes and in-flight journal entries — by clearing one side's extent mapping. For a crash-consistent overlay (which is exactly what lazysync produces), the journal contains the uncommitted side of recent transactions. fsck interpreted these as duplicate blocks and cleared the journal entries' extent mappings, leaving inodes with correct size metadata but zeroed data blocks.

Observed consequence: `welcome.service` and `welcome-server.py` (both 252 bytes / 4424 bytes) read as all-zeros on the migrated target. Nine netplan `.pyc` files were moved to `lost+found`. The fsck exit code was 0 (or 1 = "errors corrected"), masking the corruption.

**Fix:** Delete `RunFsckOnOverlayAsync` and its call site entirely. The correct mechanism for crash-consistent ext4 overlays is the kernel's own journal replay at mount time:

```
[boot log — correct behavior after H-2]
EXT4-fs (vda1): recovery complete
EXT4-fs (vda1): mounted filesystem with ordered data mode
```

Journal replay does exactly what fsck should not have done: it replays the journal's committed transactions and discards the uncommitted ones. This leaves the filesystem in the last fully-committed state, which is precisely the state lazysync was capturing (files committed by the guest before capture + any open transactions that will be rolled back). No file corruption.

**Design rule:** Never run `fsck` on a crash-consistent image produced by a content-addressed replication system. The kernel's mount-time journal replay is the boundary the system already provides. Align to it.

**Files changed:** `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` (delete method and call site).

---

#### H-3: Blockstore Pre-Flight and Owner-Indexing Hardening

**Problem (identified 2026-06-03):** Three related gaps in blockstore block accounting were preventing migrations from proceeding even when all required blocks were physically present on the target node.

**Gap 1 — Pre-flight queries ownership index, not raw presence.**

`TryDirectBlockstorePreflightAsync` called `GET /owners/{vmId}` on the target blockstore. The ownership index is only written when a block arrives via the gossipsub `new-blocks` path (which carries vmId metadata). Blocks that arrive via any other path — presence catchup (`performCatchupFromPeer`), direct GET /blocks/{cid} during reconstruction, cross-VM content dedup — are physically stored and DHT-announced but absent from the ownership index.

Consequence: a target blockstore holding all 329 required CIDs (confirmed by `ConfirmedVersion` advancing, because each CID had srv022010 as a DHT provider) reported only 50/329 = 15% to the pre-flight check (the gossipsub-received subset), triggering DHT-walk fallback → 503 → migration deferred indefinitely.

**Fix:** Add `POST /blocks/has` endpoint to the blockstore. Accepts `{"cids":[...]}`, checks `bstore.Has()` for each CID, returns `{"present":[...]}`. Replace the `/owners/{vmId}` GET in `TryDirectBlockstorePreflightAsync` with a `/blocks/has` POST sending all ChunkMap CIDs. This queries raw physical presence, independent of ownership metadata.

**Gap 2 — Ownership index not written on GET /blocks/{cid} during reconstruction.**

`ReconstructOverlayAsync` calls `GET /blocks/{cid}` for each overlay block. The blockstore's `getBlock` handler fetched and served blocks but never wrote the ownership index. After reconstruction, all 329 blocks were present but the ownership index showed 0 for this VM (or only the gossipsub subset from earlier replication).

**Fix:** Add `?owner={vmId}` parameter support to `GET /blocks/{cid}`. The `getBlock` handler reads the `?owner=` query param and appends the CID to `owners/{vmId}.cids` in a goroutine. `ReconstructOverlayAsync` appends `?owner={spec.Id}` to all block fetch URLs.

**Gap 3 — Ownership index not written on `performCatchupFromPeer`.**

The presence catchup path (`performCatchupFromPeer`) fetches all blocks for a VM from a peer via `session.GetBlocks()` but never wrote the ownership index. These blocks (the bulk of the replication, typically 280+ of 329 for a standard Debian VM) were present, DHT-announced, and counted by LazysyncManager as remote providers — but `/owners/{vmId}` stayed at the gossipsub-received subset (~50 CIDs, consistent across all VMs).

**Fix:** Add owner index writes inside the `GetBlocks` receive loop in `performCatchupFromPeer`. The vmId is already in scope from the outer loop over the peer's owner list.

**Why 50 is consistent across VMs:** The gossipsub `new-blocks` path delivers blocks during the active seeding window (first lazysync cycle, when MSI is announcing freshly-pushed blocks). The catchup path delivers all remaining blocks when srv022010 sees MSI's presence heartbeat and fetches the diff. The gossipsub window closes first (burst settled, debounce fired), so exactly the "first wave" of blocks get owner metadata and the catchup blocks do not. With `performCatchupFromPeer` now writing the ownership index, all four block-arrival paths are covered: gossipsub receive, POST /blocks?owner=, GET /blocks/{cid}?owner=, and presence catchup.

**Files changed:**
- `system-vms/blockstore/src/main.go` — add `/blocks/has` route + handler; add `?owner=` indexing in `getBlock`; add owner index writes in `performCatchupFromPeer`
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` — add `vmId` param to `ReconstructOverlayAsync`; append `?owner=` to block fetch URLs
- `src/Orchestrator/Background/BackgroundServices.cs` — replace `/owners/{vmId}` GET with `/blocks/has` POST in `TryDirectBlockstorePreflightAsync`

#### Phase H file inventory

- **`src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs`** — H-1: replace first-cycle path with `drive-backup sync=top`, delete `GetOverlayChunkOffsetsAsync` (H-1).
- **`src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs`** — H-2: delete `RunFsckOnOverlayAsync`; H-3: add `vmId` param to `ReconstructOverlayAsync`, append `?owner=` to block fetch URLs.
- **`system-vms/blockstore/src/main.go`** — H-3: `/blocks/has` endpoint; `?owner=` in `getBlock`; owner indexing in `performCatchupFromPeer`.
- **`src/Orchestrator/Background/BackgroundServices.cs`** — H-3: `/blocks/has` in `TryDirectBlockstorePreflightAsync`.

---

### Phase I: Full-Disk Snapshot Replication — ⚠️ Superseded by Phase J (2026-06-05)

Phase I shipped `drive-backup sync=full` inside a per-cycle guest-fsfreeze. It was confirmed structurally incapable of producing temporally coherent snapshots on active guests via live diagnostics on msi-1867 (2026-06-04). See §6.1.7 and §6.1.8 for full analysis.

---

### Phase J: blockdev-snapshot-sync — Correct Coherence Model — ✅ Complete 2026-06-05

**Goal:** Replace `drive-backup` with a snapshot primitive that produces genuine temporal coherence — one where all bytes are captured at the same filesystem transaction boundary, not sequentially over tens of seconds.

**Root cause of Phase I failure (msi-1867, 2026-06-04):** `drive-backup sync=full` installs a COW snapshot point at job creation but reads clusters unfrozen over tens of seconds. QEMU's COW notifier guarantees each cluster is captured at T=0 but clusters are read at different wall-clock moments. Coupled ext4 metadata structures (journal, inode bitmap, inode table) captured at different moments produce temporal incoherence. Debugfs confirmed missing journal transactions 9331/9332; e2fsck found dozens of directory entries pointing to deleted inodes; VM unbootable. `qemu-img check` reported "No errors" — the corruption was temporal, not structural.

**Fix:** `blockdev-add` + `blockdev-snapshot`. The snapshot performs no data movement. At the freeze moment QEMU atomically redirects guest writes to a new overlay; disk.qcow2 is immutable from that point forward. Guest thaws in sub-second; lazysync reads disk.qcow2 at leisure via `--force-share`. Every byte is at the same transaction boundary. The COW race cannot exist because there is nothing to race against.

**QMP sequence confirmed on QEMU 8.2.2 via live testing:**
- `qemu-img create -f qcow2 {newOverlayPath} {virtualSize}` — no `-b` flag (write-lock conflict)
- `chown libvirt-qemu:libvirt-qemu {newOverlayPath}`
- `blockdev-add` — register overlay as named block node
- `blockdev-snapshot` (inside fsfreeze) — atomic write redirect
- `qemu-img convert --force-share disk.qcow2 → tmpRawPath` → `ScanChunksAsync`
- `block-commit` → poll `query-block-jobs` → `job-complete` → `blockdev-del` → delete file

**Additional fix — migration bootcmd D-Bus hang:** `systemctl start qemu-guest-agent` in `bootcmd` blocks indefinitely if D-Bus is not yet available (cloud-init runs before systemd is fully initialized). Removed from migration cloud-init; the `qemu-guest-agent.service` symlink in `multi-user.target.wants` handles startup automatically.

**Dirty bitmap removed:** Incremental tracking was always provided by `ScanChunksAsync`'s CID diff against `state.Chunks`. The dirty bitmap was redundant. `AddDirtyBitmapAsync`, `RemoveDirtyBitmapAsync`, `ClearDirtyBitmapAsync`, `StartDriveBackupIncrementalAsync`, `StartDriveBackupFullAsync`, `WaitForBackupJobAsync` removed from `IQmpClient`/`QmpClient`. `BitmapCreated` field removed from `LazysyncState`. Single linear cycle path replaces two interleaved branches.

**End-to-end verification (msi-7825, 2026-06-05):** Clean boot, no EXT4 errors, no emergency mode, networking up, cloud-init completed, guest agent ready, SSH functional, welcome page shows pre-migration content, browser terminal and file browser working, stale VM cleanup on source node recovery correct.

**Files changed:**
- `src/DeCloud.NodeAgent.Core/Interfaces/Qmp/IQmpClient.cs` — remove bitmap/drive-backup methods, add `CreateSnapshotAsync` + `DeleteSnapshotNodeAsync`
- `src/DeCloud.NodeAgent.Infrastructure/Services/Qmp/QmpClient.cs` — implement blockdev-add+blockdev-snapshot, block-commit teardown, `backing_file_depth`-based node discovery, silent pre-del in `CreateSnapshotAsync`
- `src/DeCloud.NodeAgent.Infrastructure/Services/Resilience/LazysyncDaemon.cs` — single cycle path, `newOverlayPath` + `tmpRawPath`, `BitmapCreated` removed
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` — remove `systemctl start qemu-guest-agent` from migration `bootcmd`

---

## 9. How This Connects to Future Features

### Built-In (Phase A-F)
- **Continuous VM disk replication (lazysync)** — Core feature, not optional
- **Live migration on node failure** — Core feature, the primary purpose of the block store
- **Template image distribution** — Distribute base images via block store (dedup across nodes)
- **Split-brain prevention** — Zero-config zombie VM detection and cleanup
- **Mesh-driven self-healing** — Reactive `needs-replica` signaling + proactive presence-loss + survey loops keep replication ≥ RF without orchestrator involvement

### Near-Term Extensions
- **On-demand VM migration** — User-initiated, not just failure-driven
- **Point-in-time recovery** — Retain historical manifest versions for rollback (keep N confirmed manifests instead of GC'ing immediately)
- **Content delivery** — Serve static content from nearest block store node
- **User file storage** — Simple put/get API for users to store data persistently

### Long-Term Vision
- **Full IPFS compatibility** — Upgrade to IPFS node for interoperability
- **Erasure coding** — Reed-Solomon coding for storage efficiency (reduce replication overhead)
- **Storage marketplace** — Users pay for persistent storage, node operators earn

---

## 10. Key Design Decisions

### Decision 1: Separate VM from DHT (not embedded)
**Why:** DHT VMs are lightweight (512 MB RAM, 2 GB disk). Block storage needs different resources (more disk, similar RAM). Keeping them separate means:
- DHT stays fast and lean (just routing)
- Block store can be deployed selectively (only on nodes with ≥100 GB storage)
- Independent failure domains (block store crash doesn't break DHT)
- Different scaling profiles

### Decision 2: 5% storage duty (not configurable, not percentage of free space)
**Why:** A fixed 5% of total storage creates a predictable, bounded obligation:
- Every eligible node contributes proportionally — fair across hardware tiers
- The orchestrator can calculate aggregate network capacity deterministically
- No node sacrifices significant resources — 5% is small enough to never conflict with user VMs
- The aggregate across many nodes creates substantial capacity (1,000 nodes × 1 TB avg = 50 TB)
- Nodes hold OTHER nodes' replicated data — this is a collective duty, not local storage

### Decision 3: libp2p bitswap (not custom protocol)
**Why:** Bitswap is battle-tested by IPFS with millions of nodes. It handles:
- Want/have negotiation
- Block prioritization
- Peer reputation
- Parallel downloads from multiple peers
Using a standard protocol also opens the door to IPFS interop later.

### Decision 4: FlatFS storage backend (not LevelDB for blocks)
**Why:** FlatFS stores each block as a separate file on disk. This is:
- Simple to debug (blocks are just files)
- Easy to backup/restore (cp -r)
- Good for large blocks (no LSM tree overhead)
- Proven by IPFS Kubo for years
LevelDB is only used for metadata (access times, block index, stats).

### Decision 5: DHT-native scatter replication (not orchestrator-directed placement)
**Why:** Decoupling replication from scheduling creates a simpler, more resilient system:
- **Storage ≠ compute**: a 2-core node can store chunks for a 16-core VM. Restricting replicas to scheduling-eligible nodes wastes the storage capacity of smaller nodes and shrinks the replication pool
- **Kademlia XOR scatter is free**: the DHT's mathematical properties naturally distribute blocks across the network with high diversity. No placement logic needed
- **Larger replication pool = better durability**: blocks land on 10-20+ nodes scattered across the ID space, not 3 pre-selected "placement-aware" targets. An entire region can go offline without data loss
- **Faster migration**: bitswap fetches from many scattered providers in parallel, not 3 specific replicas
- **Simpler**: no `SelectReplicaNodesAsync`, no failure domain reasoning in the replication path, no `/replicate` commands. The block store just participates in Kademlia — blocks flow to where they belong
- The orchestrator's role reduces to **auditing** (checking provider counts) and **scheduling** (picking migration targets based on compute fit). Bitswap is both the transfer and replication mechanism

### Decision 6: Local LRU GC + orchestrator audit (not orchestrator-coordinated pinning)
**Why:** With scatter replication across many nodes, local GC is safe:
- Blocks have 10-20+ providers scattered across the network. One node evicting a block via LRU has negligible impact on durability
- Kademlia's provider record re-publication naturally recovers from provider loss — nearby nodes discover the gap and pull from remaining providers
- No orchestrator pin/unpin lifecycle needed. Nodes manage their own 5% budget via LRU eviction
- Old blocks (no longer in any current manifest) naturally disappear: nodes stop re-announcing → provider records expire (TTL) → blocks become GC candidates everywhere
- The orchestrator audits provider counts and advances `confirmedVersion` — this is a read-only check, not a coordination step
- In the rare case of persistent under-replication, the orchestrator can publish a "re-replicate" hint — but this is a fallback, not the normal path

### Decision 7: Content-addressed with CIDv1
**Why:** Using IPFS-compatible CIDs (Content IDentifiers) means:
- Deduplication is automatic (same content = same CID)
- Integrity verification is built-in
- Future IPFS interop
- Industry standard format
- Critical for lazysync efficiency: unchanged overlay blocks across cycles share the same CID and are never re-transferred

### Decision 8: DAG manifests from day one (not bolted on later)
**Why:** VM disk state is a structured collection of blocks, not a flat blob. Supporting DAG manifests in the initial design means:
- The single evolving manifest per VM is clean and native
- The block store understands "pin this DAG" (manifest + all referenced blocks)
- Future features (large file storage, directory trees) get DAG support for free
- Avoids painful migration from flat-block-only to DAG-aware later

### Decision 9: Lazysync over periodic snapshots + delta chains
**Why:** The traditional approach of periodic full snapshots with delta chains between them has inherent complexity: delta chains that need consolidation, snapshot events that create I/O bursts, two different manifest types, and recovery requiring chain traversal. Lazysync replaces this with a simpler model:
- **No snapshot events**: dirty blocks trickle out continuously in the background. A VM writing 1 GB/hour generates ~17 MB per 5-minute cycle — easily absorbed
- **No delta chains**: there's one manifest per VM. Chunk CIDs are replaced in-place. No chain to walk, no consolidation
- **Better recovery point**: ~5-7 minutes vs 30+ minutes with periodic deltas
- **Simpler GC**: blocks become unreferenced when their manifest slot is updated. Track "is this block referenced by any confirmed manifest?" — if not, GC it
- **Natural write coalescing**: if a chunk is written 100 times between cycles, only the final state is replicated
- **Simpler reconstruction**: read manifest, assemble chunks in offset order.

### Decision 10: Overlay-only replication (not full disk)
**Why:** Replicating only the overlay layer (not the full virtual disk) reduces storage and bandwidth by 90%+:
- **Base images are shared**: `debian-12-generic.qcow2` is the same for thousands of VMs. Store it once in the image registry, not replicated per-VM
- **Overlays are sparse**: a 100 GB virtual disk might have 3 GB of actual writes. Replicate 3 GB, not 100 GB
- **Cloud-init is regenerable**: the orchestrator has the VM's labels — regenerate the ISO in milliseconds
- `qemu-img map` discovers allocation without reading the full virtual disk
- **Natural fit with qcow2**: QEMU already tracks dirty blocks at the overlay level. The dirty bitmap covers overlay writes only — base image reads are invisible to CBT

### Decision 11: GossipSub fast path + DHT durable fallback (not pure Kademlia)
**Why:** Standard Kademlia provider records are passive — they answer queries but don't push notifications. Waiting for periodic DHT scans to discover new blocks would add minutes of latency to replication. GossipSub provides near-instant notification:
- Source block store publishes new CIDs to `decloud/blockstore/new-blocks` topic
- All subscribed block store nodes receive the notification in seconds
- Nodes evaluate XOR distance locally and pull if close enough
- GossipSub is ephemeral (missed messages are lost), so the DHT serves as the durable fallback: periodic neighborhood scans catch anything missed
- This dual-path approach gives both speed (GossipSub) and reliability (DHT provider records)

### Decision 12: Per-VM configurable replication factor (not fixed network-wide)
**Why:** Different workloads have different durability requirements and cost tolerances:
- N=0 (ephemeral): development VMs, CI runners — no replication cost
- N=3 (default): production workloads — standard durability
- N=5+ (premium): databases, stateful services — higher confidence
- The cost lever is primarily N=0 vs N>0 (whether lazysync runs at all). Once running, incremental cost of higher N is minimal — Kademlia scatters to many nodes regardless, N just sets the confirmation threshold
- Ties directly to pricing tiers without complicating the replication mechanism

### Decision 13: NodeId as authority (not separate lease service)
**Why:** The `VirtualMachine.NodeId` field already tracks which node is authorized to run a VM. Using this existing field as the fencing mechanism:
- Requires no new services or database tables
- Leverages existing heartbeat/reconciliation infrastructure
- Simple to understand: "if NodeId != myNodeId, stop the VM"
- Eliminates lease renewal complexity and potential lease expiration edge cases
- Already used throughout the codebase for VM-to-node association

### Decision 14: Replication factor on the wire (Phase F)
**Why:** Pre-Phase-F, the per-VM replication factor lived only in the orchestrator's `VmSpec.ReplicationFactor`. Every mesh-side repair decision required an orchestrator round-trip to read it — making truly autonomous self-healing impossible. Carrying RF on the manifest POST + the `new-blocks` GossipSub announcement, persisted locally as `owners/{vmId}.meta`, closes that gap:
- Each blockstore now knows the RF of every VM whose blocks it holds, without orchestrator round-trips
- The `omitempty` JSON tag preserves backward compatibility with pre-Phase-F peers (mixed-version mesh safe)
- The receiver-side meta write is gated on `ManifestVersion > existing` to avoid rewrite churn during seeding bursts
- The atomic tmp+rename write protects concurrent readers from torn JSON
- Once persisted, the value is queryable by the survey and needs-replica handlers in O(1) local file read
- Ephemeral VMs (RF=0) are naturally excluded — `saveOwnerMeta` is a no-op for non-positive RF, so no stale meta files accumulate

### Decision 15: Mesh-driven repair (not orchestrator-polled) (Phase F)
**Why:** Pre-Phase-F, every failure class — capacity eviction, replica offline, silent wipe — surfaced as under-replication during the orchestrator's 5-minute audit cycle, which then dispatched a `ReseedVm` command. This put the orchestrator on the critical path for routine durability events. The blockstore mesh already had the local information needed to detect each failure class directly:
- **Reactive (Phase F-B):** GC knows when it's about to evict. Publishing `needs-replica` BEFORE the delete gives XOR-close peers maximum time to absorb. Receivers re-publish on `new-blocks` if they hold the block, triggering the existing XOR-pull machinery. Per-CID receive cooldown (60s) prevents thundering-herd responses.
- **Proactive — presence (Phase F-C):** The presence topic already broadcasts heartbeats. A 30s monitor detects peers timing out (180s threshold) and signals `needs-replica` for their cached owner indexes.
- **Proactive — survey (Phase F-C):** Every 5 minutes, fetch each presence peer's `/owners/{vmId}` and compare against local CIDs. CIDs with remote count below RF get re-published on `new-blocks`. This is the cold path: catches silent wipes, TTL drift, and the case where presence-loss signaled an empty cache.

Each failure class has exactly one primary handler. Pathways do not contend. Per-CID publisher cooldown (15min) prevents survey republish storms. Repair latency drops from "next 5-min audit" to seconds (reactive) or single-digit minutes (proactive).

### Decision 16: Sentinel audit cadence (orchestrator as backstop) (Phase F)
**Why:** With reactive and proactive repair on the mesh, the orchestrator audit no longer needs to fire every 5 minutes for confirmed manifests. But it cannot be removed entirely — only the orchestrator can advance `ConfirmedVersion` (the field lives in MongoDB; the mesh has no write authority over it), and only the orchestrator can detect catastrophic total-loss (every replica gone simultaneously, mesh signals lost). The Phase F predicate splits the cadence:
- **`Version > ConfirmedVersion` (active progress):** 5-minute cadence retained. Initial confirmation must progress quickly because VMs cannot migrate until `ConfirmedVersion > 0`.
- **`ConfirmedVersion == 0 + stale > 30 min`:** 30-minute cadence retained. Defensive — initial confirmation should not stall.
- **`ConfirmedVersion > 0 + stale > 6 h`:** 6-hour sentinel backstop. The mesh handles routine repair; the audit only fires if the mesh has failed catastrophically.

DHT load on host nodes drops ~12× at steady state. `MaybeTriggerReannounceAsync` and `ReannounceCooldown` are deleted — the mesh's `needs-replica` topic plus survey-driven `new-blocks` re-publish replace this function's role entirely.

### Decision 17: Migration pre-flight DHT check (Phase F)
**Why:** The slower sentinel cadence (6 h vs 30 min) creates a window where the orchestrator's `ConfirmedChunkMap` snapshot may be up to 6 h stale at migration time. If the mesh lost replicas in that window — or never recovered — committing to authority transfer would result in a partially-reconstructed VM on the target. The pre-flight is a one-shot DHT walk against the target node's view, immediately before authority transfer:
- Samples 50 random CIDs from `manifest.ChunkMap` (statistically meaningful for typical 10K–100K-CID manifests)
- 90% pass threshold — bitswap retry on the target handles modest gaps; below 90% the manifest is genuinely degraded
- Indeterminate results (HTTP 503 from cold DHT, network errors, timeouts) are excluded from both numerator and denominator — same semantics as the existing audit math
- Three outcomes:
  - **Pass:** proceed with authority transfer (NodeId reassignment, CreateVm dispatch)
  - **Defer:** under threshold → push status message to VM, return without authority transfer; next 5-minute scan cycle retries
  - **Tolerant pass:** target has no DHT VM at all → proceed optimistically rather than blocking on missing telemetry
- Uses the TARGET's DHT VM (the source is offline by definition) — also the most accurate predictor of what the target's bitswap will actually see

This trades a few hundred milliseconds of pre-flight latency for failed-fast migration safety. Aborting on the orchestrator side is far cheaper than committing to a partial reconstruction on the target.

### Decision 18: Application-consistent capture via guest fsfreeze (Phase G) — ✅ Complete
**Why:** Lazysync's pre-Phase-G capture reads the qcow2 without coordinating with the guest kernel, producing crash-consistent snapshots. ext4's journal recovery handles crash-consistent state when the guest itself crashes — but a snapshot taken of a running guest, replicated to a target, and booted there is *not* a crash recovery: it's a perfect reconstruction of a transient mid-flush state that on the source would have been followed by a flush, and on the target never is. The 2026-05-31 migration test exposed this concretely (zero-tail `.pyc`, see §6.1.4).

The fix aligns to the boundary the virtualization industry settled on decades ago: bracket the capture in `guest-fsfreeze-freeze` / `guest-fsfreeze-thaw`. The freeze forces a journal commit and dirty-page writeback inside the guest before the host reads the qcow2, so what the host sees is what the guest filesystem itself considers committed.

The decision is not whether to do this — it's the only correct fix for the failure mode — but how to scope it:

- **Best-effort, not strict.** A missing or broken guest agent must not block replication. Pre-Phase-G behaviour (crash-consistent) is the documented fallback. The platform's existing tolerance for diverse tenant images is preserved.
- **Freeze brackets the read, nothing else.** Block push, CID computation, and manifest registration run outside the freeze window. Guest write-pause is minimized to the duration of the qcow2 export itself.
- **Thaw must be unconditional.** `finally`-block thaw + `CancellationToken.None` on the thaw call + libvirt's built-in `fsfreeze-timeout` form three independent safety layers. No code path in Phase G can leave a guest frozen.
- **No qcow2 internal snapshots, yet.** Internal snapshots would reduce the freeze window to sub-second but add lifecycle state to manage. Reasonable next lever if first-cycle freeze durations on multi-GB overlays become customer-visible; not needed for the platform default.

The change is entirely local to the node agent. No orchestrator changes, no wire format changes, no schema migrations. That matters: it means Phase G can ship independently and revert independently if it misbehaves.

**Validation (2026-06-03):** Re-ran the failure scenario (msi-a4b5). Freeze and thaw completed in the same second. Lazysync trailed 9–15 s. `.pyc` intact end-to-end. cloud-init `init-local` completed cleanly on the migrated VM. Phase G confirmed closed.

---

### Decision 19: `drive-backup sync=top` replaces `qemu-img convert --force-share` (Phase H)
**Why:** Phase G exposed a secondary problem: the freeze window for a first-cycle full-overlay convert (`qemu-img convert`) was proportional to overlay size — seconds to minutes for active VMs. `drive-backup sync=top` uses QEMU's internal block-layer machinery to export overlay blocks asynchronously. The freeze brackets only the QMP job-queue call (sub-second), not the data export. The guest is unfrozen before data copying begins, reducing the write-pause from seconds/minutes to milliseconds regardless of overlay size. The `qemu-img map` whitelist (`GetOverlayChunkOffsetsAsync`) is deleted because `sync=top` handles the overlay-only filter natively — the BDS topology enforces the boundary that the whitelist was approximating externally.

---

### Decision 20: Remove `RunFsckOnOverlayAsync` — use kernel journal replay (Phase H)
**Why:** After `ReconstructOverlayAsync`, the node agent ran `fsck.ext4 -fy` on the assembled overlay. The intent was to repair crash-consistent state. The actual effect was the opposite: `fsck.ext4 -fy` applies crash-recovery heuristics that resolve "duplicate block" conflicts between journal entries and normal inodes by clearing extent mappings. On crash-consistent overlays produced by lazysync, this corrupted files by zeroing their data blocks while leaving size metadata intact. The kernel's own ext4 journal replay at mount time is the correct mechanism — it replays committed transactions and discards uncommitted ones, producing the last fully-consistent filesystem state without touching data blocks. The design rule: never apply external filesystem repair tools to a crash-consistent image produced by a content-addressed replication system. Align to the boundary the kernel already provides.

---

### Decision 21: Pre-flight raw CID presence (`POST /blocks/has`) over ownership index (Phase H)
**Why:** `TryDirectBlockstorePreflightAsync` queried `/owners/{vmId}` to check how many of the required CIDs the target blockstore already held. The ownership index is only written by the gossipsub `new-blocks` path — blocks arriving via presence catchup, cross-VM content dedup, or direct GET fetches are present but not indexed. In a two-node cluster with a standard Debian base image, the target held 329 of 329 required CIDs (all DHT-announced, all confirmed as remote providers by LazysyncManager), but the ownership index showed 50 (only the gossipsub-delivered subset). Pre-flight reported 15% and fell through to a DHT walk that returned 503 on all samples, deferring migration indefinitely. The fix — `POST /blocks/has` using `bstore.Has()` directly — queries raw physical presence independent of ownership metadata. This is the authoritative "does this node hold this block" query; the ownership index is a derived secondary index that must not gate migration.

---

### Decision 22: Owner indexing across all four block-arrival paths (Phase H)
**Why:** The ownership index (`owners/{vmId}.cids`) is the data structure that drives replication surveys, presence-loss repair signals, and was previously used for pre-flight checks. It was only written by one of four paths by which a block can arrive at a blockstore: the gossipsub `new-blocks` receiver path. The other three — `POST /blocks?owner=` (lazysync push), `GET /blocks/{cid}?owner=` (reconstruction), and `performCatchupFromPeer` (presence catchup) — stored and DHT-announced blocks without writing ownership metadata. This produced systematically incomplete ownership indexes: every VM showed ~50 gossipsub-received blocks in the index regardless of how many blocks the node actually held for that VM. Fix: add owner index writes to all four paths. The gossipsub and POST paths already existed; the GET path and catchup path required new writes. With all four paths covered, the ownership index becomes the reliable secondary index it was designed to be — replication surveys and survey-based `needs-replica` signals now accurately reflect what the node holds.

---

### Decision 23: Overlay-only replication structural limits — closed by Phase J (2026-06-05)

**Why this became a decision and then a closed issue:** After extended testing, the following was established: overlay-only replication with Phase G+H guest-fsfreeze produces application-consistent, bootable migrated VMs for planned migrations (source node available). For unplanned migrations (source died abruptly), the ConfirmedChunkMap used for reconstruction represents the last confirmed lazysync snapshot — crash-consistent from the block perspective but potentially containing temporally incoherent ext4 metadata structures across blocks captured at different lazysync cycles.

The content-addressing model guarantees per-block integrity, not cross-block temporal coherence. ext4's inode bitmaps, block bitmaps, and journal entries form a logically coupled structure. If they were captured at different lazysync cycles, the combination can be internally inconsistent in ways journal replay cannot repair — producing corrupt inode bitmaps at boot.

**Decision 23 consensus (2026-06-03):** Accept structural limits for unplanned recovery; defer full-disk snapshot replication due to storage/bandwidth overhead.

**Decision 23 closed (2026-06-05) — Phase J shipped full-disk snapshot replication:**

The `drive-backup` approach (Phases H/I) was confirmed structurally incapable of solving the temporal coherence problem via live diagnostics on msi-1867 (2026-06-04). `drive-backup sync=full` installs a COW snapshot point at job creation but reads clusters sequentially over tens of seconds unfrozen — each cluster is correct at T=0 but coupled ext4 metadata structures read at different wall-clock moments produce temporal incoherence regardless of `sync=full` vs `sync=top`. e2fsck found missing journal transactions 9331/9332 and dozens of directory entries pointing to deleted inodes.

Phase J (`blockdev-add` + `blockdev-snapshot`) solves this by performing no data movement during the freeze. The guest is frozen for one QMP round-trip (sub-second). At that moment QEMU atomically redirects writes to a new overlay; the original disk is immutable and coherent at the exact freeze moment. Every byte is at the same filesystem transaction boundary. The COW race that broke Phase I cannot exist because there is nothing to race against. This was confirmed via end-to-end migration test (msi-7825, 2026-06-05): clean boot, no EXT4 errors, no emergency mode, user data intact, SSH functional, browser terminal functional, welcome page showing pre-migration content.

See §6.1.8 for the full Phase J design and verification record.

---

## 11. Risk Assessment

| Risk | Likelihood | Impact | Mitigation |
|------|------------|--------|------------|
| Block store VM crash | Medium | Low | Self-healing reconciliation redeploys; blocks scattered across many providers |
| Node storage full | Medium | Low | Quota enforcement (hard refuse at 95%); LRU GC naturally trims excess as nodes fill up; Phase F `needs-replica` signal preserves replica count by triggering XOR-close peers to absorb before delete |
| Source offline before scatter | Low | Medium | GossipSub + bitswap propagation completes in seconds; window is small. Orchestrator alerts on confirmedVersion=0 + node failure. Phase F mesh repair shortens post-scatter recovery further but cannot eliminate the unscattered-window itself. Cannot be fully eliminated without synchronous replication |
| GossipSub message loss | Low | Low | GossipSub fetch retry queue recovers failed fetches within 60s (hot path); Phase F replication survey (5min cadence) catches entirely missed messages via owner-index diff against presence peers (cold path) — replaces the original Phase D+1 DHT neighborhood scan |
| Ephemeral VM data loss (N=0) | N/A | N/A | By design — user explicitly chose N=0. Dashboard shows ephemeral status. No alert on node failure |
| Split-brain / zombie VM | Medium | High | NodeId authority enforcement, reconciliation on startup/heartbeat, lazysync fencing, optional network fencing |
| False positive zombie detection | Low | High | Conservative approach: keep running if orchestrator unreachable; version tracking for edge cases |
| Reconciliation delays | Low | Medium | Multiple reconciliation triggers (startup, heartbeat, reconnection); InvalidVmIds in heartbeat response for proactive notification |
| Stale ConfirmedChunkMap at migration (6h sentinel window) | Low | Medium | Phase F migration pre-flight: DHT walk + direct blockstore `/blocks/has` check against target before authority transfer (Phase H); defers migration cycle if <90% of sampled CIDs have providers or raw presence is below threshold. Closes the gap created by the slower sentinel cadence |
| Catastrophic mesh failure (all replicas of a CID gone simultaneously) | Very Low | High | Orchestrator sentinel audit (6h cadence) detects `ConfirmedCids.Count == 0` drain, triggers `TriggerReseedAsync` — irreducible escape hatch |
| Torn writes in captured overlay (file metadata committed, data extents not) | Low (post-G+H) | High | Phase G+H: `drive-backup sync=top` inside a guest-fsfreeze bracket. Freeze sub-second (QMP job-queue call only); guest unfrozen before data export; application-consistent for all planned migrations. Best-effort fallback (crash-consistent, pre-Phase-G behaviour) when guest agent is unreachable. See §6.1.4, §6.1.5 |
| Guest left frozen by Phase G capture path | Very Low | High | Three independent safety layers: `finally`-block thaw, `CancellationToken.None` on thaw call so cancellation still thaws, libvirt's `fsfreeze-timeout` (auto-thaw if host fails to thaw within a bounded interval). No code path can leave a guest frozen indefinitely |
| fsck corrupting crash-consistent overlay metadata (pre-H) | Closed | High | Phase H: `RunFsckOnOverlayAsync` removed. `fsck.ext4 -fy` applied crash-recovery heuristics that zeroed data blocks in crash-consistent overlays. Kernel ext4 journal replay at mount time is the correct mechanism — replays committed transactions without touching data blocks. See §6.1.5 H-2 |
| Pre-flight falsely deferring migration when blocks are present (pre-H) | Closed | Medium | Phase H: `TryDirectBlockstorePreflightAsync` uses `POST /blocks/has` (raw blockstore presence) instead of `GET /owners/{vmId}` (ownership index). Ownership index incomplete for blocks arriving via catchup/dedup paths; raw presence is authoritative. See §6.1.5 H-3 |
| Unplanned migration producing non-bootable VM due to ext4 metadata temporal incoherence | Closed by Phase J | Phase J (`blockdev-add` + `blockdev-snapshot`) eliminates this by construction. The snapshot is instantaneous — no data movement during the freeze, no COW race. Every byte in the original disk is at the same filesystem transaction boundary. Confirmed via live diagnostics (msi-1867) and end-to-end migration test (msi-7825, 2026-06-05). See §6.1.8. |

---

## 12. Success Metrics

### Phase A-C (Core Block Store)
- Block Store VM deploys on all eligible nodes (≥100 GB storage, ≥2 GB RAM)
- 5% storage allocation is enforced — no node exceeds its duty
- Blocks survive node restart (persistent FlatFS)
- Cross-node block retrieval works via bitswap
- GossipSub propagation delivers new block notifications to subscribed nodes within seconds
- Adaptive XOR threshold correctly adjusts pull behavior based on local capacity
- Local LRU GC works correctly — evicts least-recently-used blocks, enforces 5% budget
- Bootstrap polling discovers peers within 60 seconds
- Self-healing redeploys on VM failure
- DHT proximity endpoint returns accurate distance and rank estimates

### Phase D (Lazysync & Migration) — ✅ Production-verified 2026-04-20
- ✅ Lazysync daemon continuously replicates dirty overlay blocks for all running VMs
- ✅ Lazysync cycle completes within configured interval (~5 minutes) under normal load
- ✅ QEMU incremental backup produces crash-consistent dirty block exports without VM pause
- ✅ Confirmed manifest version stays within 2 versions of current (Kademlia scatter keeps up)
- ✅ All chunks in confirmed manifests have ≥N providers in the DHT (verified by audit loop)
- ✅ Blocks scatter across the network via Kademlia XOR proximity — no orchestrator-directed placement
- ✅ When a node goes offline, its VMs are rescheduled to a scheduling-eligible node
- ✅ Migration target selected by scheduling fit + resource headroom (bitswap fetches overlay from scattered providers)
- ✅ VM disk reconstruction from confirmed manifest completes successfully (base image + overlay via bitswap + cloud-init regeneration) — confirmed 2026-04-20
- ✅ Initial overlay seeding of new VMs completes within minutes (not hours)
- ✅ Recovery point objective: ≤10 minutes of data loss under normal conditions
- ✅ Overlay-only replication keeps per-VM storage cost at 1-10% of virtual disk size
- ✅ Self-healing: provider count recovers autonomously when nodes leave/rejoin the network
- ✅ Per-VM replication factor correctly enforced:
    N=0 → lazysync skipped entirely, VM flagged Ephemeral on dashboard
    N=1 → lazysync runs, confirmedVersion advances when ≥1 provider confirmed
    N=3 → default; confirmedVersion advances when all chunks have ≥3 providers
    N=5 → high availability; confirmedVersion advances when all chunks have ≥5 providers
- ✅ Scheduler rejects nodes without Active BlockStore for VMs with replicationFactor > 0
- ✅ Source-offline alerting correctly classifies VMs as UNRECOVERABLE / RECOVERING /
  MIGRATING / LOST based on confirmedVersion, currentVersion, and replicationFactor
- ✅ `replicationFactor=0` VMs correctly classified as LOST on node failure with no alert
  (user opted in to ephemeral semantics at creation time)
- ✅ Storage replication cost billed correctly: `blockCount × blockSizeKb / 1024 × replicationFactor × costPerMbPerHour` — wired through `settleCycle()` via `OnChainSettlementService`
- ✅ `settleCycle()` on-chain integration — `BuildSettleCycleRequestAsync` + `ExecuteSettleCycleAsync` + `DeCloudEscrow.sol`
- ✅ Storage pool distribution on-chain — `_distributionPhase` distributes proportionally to `storageBytes` per node
- ✅ Block size enforcement: `blockSizeBytes` matches manifest type constant at write time
- ✅ Model shard chunk counts are manageable: Llama-3 70B Q4 = 640 × 64 MB blocks

### Phase D-fixes (Replication Reliability) — Production-verified 2026-04-08

- Lazysync daemon replicates overlay data only — base OS image excluded via
  `qemu-img map` depth=0 whitelist; initial seed ~30 MB not ~2 GB
- GossipSub-triggered bitswap fetches target correct peer via DHT session, not
  the relay hop; all successful fetches show correct `source` node ID in diag log
- Blockstores establish direct cross-region libp2p connections on boot; both nodes
  show each other's WireGuard IP in peer list within 60 seconds of startup
- `bitswapSent` counter correctly reflects blocks served via bitswap
- GossipSub fetch retry queue recovers blocks that timed out on initial fetch;
  `retry_fetch` events visible in diag log within one retry cycle (60s)
- `retry_give_up` rate is 0 under normal cross-region conditions (routing table
  warm after first retry; subsequent retries resolve in <8ms)

### Phase D Migration Fixes — Production-verified 2026-04-20

Additional bugs discovered and fixed during end-to-end migration testing:

**Bug 7 — MAC-pinned netplan caused `network-online.target` hang on receiving node**
The overlay's `/etc/netplan/50-cloud-init.yaml` contains a `match: macaddress` entry
bound to the source node's VM MAC address. On the receiving node the VM gets a
different MAC, so systemd-networkd cannot bring up `enp1s0` and
`network-online.target` waits forever — freezing boot at the cfg80211/udev stage
(visible ~t=47s via VNC). Fix: add `bootcmd` to migration cloud-init that overwrites
the netplan file with a generic DHCP config before networkd starts.
File: `LibvirtVmManager.cs`.

**Bug 8 — Cloud-init config stage blocked on apt operations during migration boot**
The overlay's `/etc/cloud/cloud.cfg` enables `package_update` and `package_upgrade`
by default. Migration user-data had `package_update: false` but was missing
`package_upgrade: false`, `package_reboot_if_required: false`, `resize_rootfs: false`,
`runcmd: []`, and `write_files: []`. Cloud-init config stage blocked on apt.
Fix: add all disable flags to migration cloud-init. File: `LibvirtVmManager.cs`.

**Bug 9 — Dirty bitmap `lazysync` persists in qcow2 metadata through migration**
The QEMU persistent dirty bitmap is embedded in the overlay qcow2 file metadata.
When the overlay is reconstructed on the receiving node, the bitmap already exists.
`AddDirtyBitmapAsync` fails with "Bitmap already exists: lazysync" causing
`LazysyncDaemon` to fall back to full export every cycle indefinitely.
Fix: add `RemoveDirtyBitmapAsync` to `IQmpClient` and `QmpClient`; call it before
`AddDirtyBitmapAsync` in `LazysyncDaemon.SyncVmAsync`, swallowing the not-found
error if the bitmap is absent. Files: `IQmpClient.cs`, `QmpClient.cs`, `LazysyncDaemon.cs`.

**Bug 10 — Ingress domain not live until after `WaitForPrivateIpAsync` timeout**
`VmLifecycleManager` registered ingress after `WaitForPrivateIpAsync` completed.
For migration boots, the IP is assigned quickly but `WaitForPrivateIpAsync` had
an unnecessary delay. Fix: register ingress before `WaitForPrivateIpAsync` so the
domain resolves as soon as migration boot completes. File: `VmLifecycleManager.cs`.

### Production Bug Fixes (2026-04-08)

Discovered and fixed during initial lazysync seeding tests across two nodes
(us-east-1 and tr-south) with a VM at replicationFactor=1.

**Bug 1 — LazysyncDaemon replicated full backing chain, not overlay only**
`qemu-img convert` merges the backing chain into a flat raw file. The `-S 65536`
flag skips zero/unallocated regions, not backing-file extents. The base OS image
(~2 GB of non-zero EXT4 data) was replicated every cycle alongside actual overlay
writes. Fix: run `qemu-img map --force-share --output=json` first to build a
whitelist of depth=0 chunk offsets; `ScanChunksAsync` skips any offset not in the
whitelist. File: `LazysyncDaemon.cs`.

**Bug 2 — Bitswap fetches targeted the GossipSub relay hop, not the block provider**
Blockstores were only connected to their co-located DHT VM. GossipSub messages
propagated via the DHT relay chain: source blockstore → source DHT → (WireGuard) →
target DHT → target blockstore. `msg.ReceivedFrom` in the GossipSub receive loop
was the target's local DHT VM peer ID, not the source blockstore. `sessionForPeer`
sent WANT messages to that peer — which holds no blocks — causing guaranteed 30s
timeouts on every fetch. Fix: remove the `nodeIDToPeerID` map write and always use
`bsExch.NewSession(ctx)` in `handleNewBlockAnnouncement`, which performs a DHT
FindProviders walk to locate the actual provider. File: `main.go`.

**Bug 3 — Bitswap port 5001 blocked by host firewall**
`blockstore-vm-cloudinit.yaml` runcmd had no `ufw allow` rule for port 5001.
`install.sh` only opens agent port 5100/tcp and WireGuard 51820/udp. Direct cross-
region libp2p connections were dropped at the host firewall; blockstores remained
permanently isolated from each other via bitswap. Fix: add
`ufw allow __BLOCKSTORE_LISTEN_PORT__/tcp` to cloud-init runcmd.
File: `blockstore-vm-cloudinit.yaml`.

**Bug 4 — Bootstrap poll backed off too aggressively on simultaneous boot**
`POLL_INTERVAL_CONNECTED=300s` (5 minutes). When two blockstores booted
simultaneously and one joined the orchestrator before the other was registered as
Active, the first received 0 bootstrap peers, set `INITIAL_POLL_DONE=true`, then
backed off for 5 minutes while the second blockstore became discoverable within
seconds. Fix: reduce `POLL_INTERVAL_CONNECTED` to 60s. File:
`blockstore-bootstrap-poll.sh`.

**Bug 5 — `bitswapSent` counter never incremented**
`n.bitswapSent uint64` field was declared in `BlockNode` but never written; all
three handlers reported 0 permanently. Fix: remove field, replace with
`n.bsExch.Stat().BlocksSent` read in `handleHealth`, `handleStats`, and
`handleDiagnostics`. File: `main.go`.

**Bug 6 — GossipSub fetch concurrency caused burst timeouts**
All GossipSub-triggered fetch goroutines fired simultaneously on message burst.
With 100 messages arriving in one batch and each requiring a 30s DHT walk over WAN,
concurrent walks saturated the link and all timed out. Fix: add `fetchSem chan struct{}`
(capacity `GossipSubFetchConcurrency=4`) with blocking `select` and 5-minute wait
timeout, so goroutines queue rather than drop. File: `main.go`.

### Phase E (Split-Brain Prevention) — ✅ Complete
**Foundation:** `vm.NodeId` updated atomically on migration (Phase D). Push-based enforcement active.
- ✅ Zombie VMs destroyed within one heartbeat cycle (~30–60 s) of source node reconnecting
- ✅ `TargetNodeId` pre-check fires before heartbeat round-trip — catches zombie immediately on reconnect
- ✅ `InvalidVmIds` in heartbeat response — orchestrator pushes stale VM list each cycle
- ✅ Local blockstore blocks cleaned up atomically with VM deletion (`DELETE /owners/{vmId}`)
- ✅ Remote blockstore replicas evicted via GossipSub `decloud/blockstore/vm-deleted` on orchestrator deletion
- ✅ False positive protection: `TargetNodeId` check only fires when field is explicitly set (migration path); `InvalidVmIds` requires orchestrator reachability — node keeps running if orchestrator is unreachable
- ✅ Stale manifest pushes from zombie nodes rejected with HTTP 403 — `BlockStoreController` NodeId fence active
- 🔲 Network fencing (item 39, optional) — WireGuard revocation for defense-in-depth
- 🔲 NodeAssignmentVersion (item 40, optional) — fencing token for A→B→A migration edge case

### Phase F (Self-Healing Replication) — ✅ Complete 2026-05-31

**Foundation:** Phase A (RF on the wire) provides the per-VM RF policy at every blockstore. Phases B–C
add reactive + proactive repair on the blockstore mesh. Phase D demotes the orchestrator audit to
sentinel cadence and adds migration safety.

- ✅ **Replication factor reaches every peer locally** — `owners/{vmId}.meta` persists `{RF, version}`
  on both publisher (write from `POST /manifests`) and receivers (write from `new-blocks`
  announcement, version-guarded). No orchestrator round-trip required for any RF-aware mesh decision.
- ✅ **GC eviction no longer silently shrinks RF** — `runGC` publishes `needs-replica` BEFORE every
  `DeleteBlock`, on both confirmed-evict and LRU paths. Cross-VM dedup respected via the
  `buildCIDOwnerIndex` single-pass inverted scan. Receivers re-publish on `new-blocks` if they hold
  the CID and per-CID 60s cooldown has elapsed; XOR-close peers absorb via existing bitswap path.
  Repair latency: seconds.
- ✅ **LRU eviction now correctly withdraws DHT provider records** — `dht.Provide(c, false)` aligned
  with the confirmed-evict path. Closes a long-standing bug where LRU evictions left stranded
  provider records for up to 24h, causing `FindProviders` to lie about block availability.
- ✅ **Replica node offline detected from heartbeat alone** — `startPresenceLossMonitor` (30s tick)
  identifies peers whose last heartbeat is older than 180s, removes them from shared `presenceState`,
  and signals `needs-replica` for their cached owner indexes. No orchestrator polling. Repair
  latency: under 4 minutes worst case (180s timeout + 30s tick + GossipSub propagation + bitswap
  fetch).
- ✅ **Silent FlatFS wipes detected proactively** — `startReplicationSurvey` (5min cadence) fetches
  `/owners/{vmId}` from every presence peer, counts remote replicas per CID, re-publishes
  under-replicated CIDs directly on `new-blocks` (subject to 15min per-CID publisher cooldown).
  Repair latency: next survey cycle.
- ✅ **Cooldown maps bounded** — `startCooldownCleanup` (hourly) sweeps stale entries from both
  `needsReplicaCooldown` (60s receive window, 2×=120s retention) and `publishCooldown` (15min
  publish window, 2×=30min retention) via `sync.Map.Range`.
- ✅ **Orchestrator DHT load drops ~12× at steady state** — `GetPendingAuditManifestsAsync` predicate
  splits confirmed-manifest staleness from 30min to 6h; `Version > ConfirmedVersion` still audits
  every 5min so initial confirmation progresses without delay.
- ✅ **`MaybeTriggerReannounceAsync` and `ReannounceCooldown` removed** — orchestrator no longer
  dispatches `ReseedVm` on partial coverage. `TriggerReseedAsync` (drain-detection escape hatch)
  retained unchanged.
- ✅ **Migration pre-flight catches stale `ConfirmedChunkMap`** — 50-sample DHT walk against target
  node's view (port 8080) immediately before authority transfer in `MigrateVmAsync`. Defers
  migration cycle (no NodeId reassignment) if <90% of sampled CIDs have providers. Indeterminate
  results (HTTP 503 cold DHT, timeouts) excluded from both numerator and denominator.
- ✅ **Mixed-version mesh safe during rollout** — `replicationFactor` field uses `omitempty` JSON tag;
  pre-Phase-F peers don't subscribe to `needs-replica` and don't run the survey loop. They neither
  publish nor respond to the new signals. Self-healing improves monotonically as the upgraded
  fraction grows.
- ✅ **Compilation and end-to-end signals verified** — `go build ./...` clean on
  `system-vms/blockstore/`; orchestrator `dotnet build` clean; `/diagnostics` event log on
  upgraded blockstore shows `needs_replica_publish`, `needs_replica_response`,
  `replication_survey`, `presence_loss_detected`, `cooldown_sweep` events firing as designed.