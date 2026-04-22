# System VM Resilience — Design Document

**Status:** Approved for Implementation  
**Phase:** 1 — Foundation  
**Last Updated:** 2026-04-21  
**Author:** BMA / DeCloud Architecture

---

## 1. Problem Statement

### 1.1 Current Pain Points

System VMs (Relay, DHT, BlockStore) are stateful services whose identity — WireGuard keypairs, libp2p Ed25519 peer IDs, auth tokens, subnet IPs — is baked into cloud-init labels at deploy time and lives only in the VM's disk.

When a system VM crashes or is redeployed:

- The VM disk is deleted → Ed25519 key at `/var/lib/decloud-dht/identity.key` is gone
- `loadOrCreateIdentity()` generates a **new key** → new peer ID
- Every peer in the DHT/BlockStore network had the old peer ID → cascading reconnections
- Mesh-enrolled VMs have the old WireGuard public key baked into their configs → reconnection failures
- Result: temporary network fragmentation, cascading redeployments, operator confusion

Additionally, system VM deployment logic is scattered across three bespoke services (`RelayNodeService`, `DhtNodeService`, `BlockStoreService`), each with its own variable injection, identity generation, and label construction — duplicating ~150 lines of near-identical logic per role.

### 1.2 Root Causes

| Problem | Root Cause |
|---|---|
| Identity lost on redeploy | Identity lives on VM disk only — no persistence layer |
| Cascading mesh disruption | Peer ID changes force all mesh peers to update routing tables |
| Code duplication | Per-role bespoke deployment services instead of unified template path |
| No community extensibility | System VMs are internal platform constructs, not marketplace templates |

---

## 2. Solution Overview

Two complementary changes:

1. **ObligationStateService** (Node Agent) — persists system VM identity in SQLite, decoupling identity from VM lifecycle
2. **Unified Enriched VmTemplate** (Orchestrator) — extends the existing template model with artifacts and enables system VMs to be first-class marketplace templates

Together these establish:

```
┌─────────────────────────────────────────────────────────────────┐
│           System VM Boot — Three Clean Data Sources              │
│                                                                  │
│  ARTIFACTS              IDENTITY              CONFIGURATION      │
│  (Template)             (Node SQLite)         (Deployment)       │
│                                                                  │
│  Binaries               Ed25519 keys          Bootstrap peers    │
│  Dashboards             WireGuard keys        Orchestrator URL   │
│  Scripts                Subnet/tunnel IP      VM ID, hostname    │
│  Config files           Auth tokens           Region, arch       │
│                                                                  │
│  Served by:             Served by:            Injected by:       │
│  artifact cache         /api/obligations/     cloud-init         │
│  ${ARTIFACT_URL:x}      {role}/state          ${DECLOUD_*}       │
│                                                                  │
│  Nothing overlaps. Nothing is duplicated across sources.         │
└─────────────────────────────────────────────────────────────────┘
```

### 2.1 Design Principles

- **KISS** — minimum new abstractions; extend existing patterns
- **Security first** — identity served only on virbr0 (local VM bridge), not externally exposed
- **Backward compatible** — existing user VM deployment path unchanged
- **One code path** — `BuildVmRequestFromTemplateAsync()` works for all VMs, system or user
- **No special cases** — system VMs are marketplace templates the platform happens to deploy automatically

---

## 3. Part 1 — Unified Enriched VmTemplate

### 3.1 Design Goals

- System VMs (Relay, DHT, BlockStore) become regular marketplace templates
- Templates can carry binary files, dashboards, and scripts as artifacts
- Artifact delivery is fast (node agent local cache, served over virbr0 at ~1Gbps)
- Community members can fork system VM templates and extend them
- Existing template functionality (cloud-init, variables, pricing, ratings) is unchanged

### 3.2 TemplateArtifact Model

**File:** `src/Orchestrator/Models/TemplateArtifact.cs` *(new)*

```csharp
/// <summary>
/// A file artifact attached to a VmTemplate.
/// Artifacts are delivered to VMs at boot via the node agent's local artifact cache.
/// 
/// Two delivery tiers:
///   Platform-hosted (IsExternalReference = false): stored on orchestrator filesystem,
///   served via node agent cache at ~1Gbps over virbr0. Max 250MB per artifact.
///   
///   External reference (IsExternalReference = true): URL-only, VM downloads directly
///   from author's infrastructure. No size limit. SHA256 required for verification.
/// </summary>
public class TemplateArtifact
{
    /// <summary>Unique identifier within the template</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Artifact name — used in cloud-init variable substitution.
    /// Cloud-init references: ${ARTIFACT_URL:name} or ${EXTERNAL_URL:name}
    /// Example: "dht-node", "dashboard-assets", "shared-scripts"
    /// </summary>
    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// SHA256 hex digest. Mandatory. Immutable after template publish.
    /// Verified by node agent on download, and again inside the VM.
    /// </summary>
    public string Sha256 { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>
    /// Source URL for download (external reference URL, or internal fallback).
    /// For platform-hosted artifacts: populated automatically on upload.
    /// For external references: author-provided URL.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// Block Store CID once artifact is replicated (future — Phase 2).
    /// When set, node agents download from Block Store instead of SourceUrl.
    /// </summary>
    public string? BlockStoreCid { get; set; }

    /// <summary>
    /// Target CPU architecture. null = universal (scripts, dashboards, configs).
    /// "amd64" or "arm64" for compiled binaries.
    /// </summary>
    public string? Architecture { get; set; }

    public ArtifactType Type { get; set; }

    /// <summary>
    /// true = SourceUrl is the delivery mechanism; platform stores only metadata.
    /// false = artifact bytes stored on orchestrator filesystem.
    /// </summary>
    public bool IsExternalReference { get; set; }

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public string UploadedBy { get; set; } = string.Empty; // userId
}

public enum ArtifactType
{
    Binary,     // Compiled executables (dht-node, blockstore-node, relay-node)
    Script,     // Shell scripts (wg-mesh-enroll.sh, notify-ready.sh)
    WebAsset,   // HTML/CSS/JS bundles (dashboard.tar.gz)
    Config,     // Configuration files (systemd units, nginx.conf)
    Archive     // General archives (tar.gz with mixed content)
}
```

### 3.3 VmTemplate Extensions

**File:** `src/Orchestrator/Models/VmTemplate.cs` *(modified)*

Add to existing `VmTemplate` class:

```csharp
// ============================================
// ARTIFACTS
// ============================================

/// <summary>
/// Files attached to this template, delivered to VMs via node agent artifact cache.
/// Platform-hosted artifacts (≤250MB) are served locally at ~1Gbps.
/// External references are downloaded by the VM directly at boot.
/// </summary>
public List<TemplateArtifact> Artifacts { get; set; } = new();

/// <summary>
/// Denormalized total size of platform-hosted artifacts (not external references).
/// Enforced at upload: max 500MB per template across all platform-hosted artifacts.
/// </summary>
public long TotalArtifactSizeBytes { get; set; }

/// <summary>
/// Template version — bumped when cloud-init or artifacts change.
/// SystemVmReconciliationService uses this to detect when system VMs need redeployment.
/// </summary>
public int Version { get; set; } = 1;

/// <summary>
/// Slug for system templates (e.g., "system-relay", "system-dht", "system-blockstore").
/// Community templates may also have slugs for discoverability.
/// </summary>
public string? Slug { get; set; }
```

### 3.4 Artifact Storage

**Phase 1 storage:** Orchestrator filesystem at `/var/lib/decloud/artifacts/{sha256}`.

Metadata (name, sha256, size, URLs) stored in MongoDB `vmTemplates` collection as embedded `Artifacts[]` array on `VmTemplate`.

**Size limits (enforced server-side):**
- Per artifact: 250MB
- Per template total: 500MB (sum of platform-hosted artifacts)
- External references: unlimited (no bytes stored on platform)

### 3.5 New Orchestrator Endpoints

**File:** `src/Orchestrator/Controllers/MarketplaceController.cs` *(extended)*

```
POST /api/marketplace/templates/{id}/artifacts
    Upload artifact file. Requires auth. Validates size, type, SHA256.
    Returns: TemplateArtifact (with Id, Sha256, SizeBytes)

GET  /api/marketplace/templates/{id}/artifacts/{name}
    Download artifact by name. Used by node agents for prefetch.
    Streams from /var/lib/decloud/artifacts/{sha256}.

GET  /api/marketplace/templates/{id}/artifacts/{name}/sha256
    Returns SHA256 checksum only (for verification without download).

DELETE /api/marketplace/templates/{id}/artifacts/{name}
    Remove artifact from template. Requires template ownership. 
    Only allowed before publish (Draft status).
```

### 3.6 Node Agent Artifact Cache

**File:** `src/DeCloud.NodeAgent/Controllers/ArtifactCacheController.cs` *(new)*

```csharp
/// <summary>
/// Serves cached artifacts to local VMs over virbr0 (192.168.122.1:5100).
/// VMs use ${ARTIFACT_URL:name} which resolves to:
///   http://192.168.122.1:5100/api/artifacts/{sha256}
///
/// Security: virbr0 is accessible only from VMs on this node.
/// No authentication required — isolation is network-level.
/// SHA256 verification is the integrity mechanism.
/// </summary>
[ApiController]
[Route("api/artifacts")]
public class ArtifactCacheController : ControllerBase
{
    // GET /api/artifacts/{sha256}
    // Serve artifact bytes from local cache.
    // Returns 404 if not cached (VM should not call before prefetch succeeds).

    // GET /api/artifacts/{sha256}/sha256
    // Returns sha256 checksum as plain text. Fast integrity check.

    // POST /api/artifacts/prefetch
    // Body: { sha256: string, sourceUrl: string, expectedSizeBytes: long }
    // Downloads artifact from sourceUrl, verifies SHA256, caches locally.
    // Returns 200 immediately if already cached.
    // Returns 202 if download started (poll /status/{sha256} for completion).

    // GET /api/artifacts/prefetch/status/{sha256}
    // Returns prefetch status: Pending | Downloading | Ready | Failed
}
```

**Cache location:** `/var/lib/decloud/artifact-cache/{sha256}` (keyed by content hash, not name).

**File:** `src/DeCloud.NodeAgent.Infrastructure/Services/ArtifactCacheService.cs` *(new)*

```csharp
public interface IArtifactCacheService
{
    Task<bool> IsCachedAsync(string sha256, CancellationToken ct = default);
    Task<Stream?> GetArtifactStreamAsync(string sha256, CancellationToken ct = default);
    Task<PrefetchResult> PrefetchAsync(string sha256, string sourceUrl, long expectedSizeBytes, CancellationToken ct = default);
    Task<PrefetchStatus> GetPrefetchStatusAsync(string sha256, CancellationToken ct = default);
    Task PruneAsync(IEnumerable<string> activeSha256s, CancellationToken ct = default);
}
```

### 3.7 Variable Resolution

**File:** `src/Orchestrator/Services/TemplateService.cs` *(extended)*

Add `ResolveArtifactVariables()` to expand artifact references in cloud-init before VM deployment:

```csharp
/// <summary>
/// Expands artifact variable references in cloud-init templates.
/// Called from BuildVmRequestFromTemplateAsync() before sending CreateVm command.
///
/// Variable prefixes:
///   ${ARTIFACT_URL:name}    → platform-hosted artifact served from node agent cache
///   ${EXTERNAL_URL:name}    → external reference URL (author's infrastructure)
///   ${ARTIFACT_SHA256:name} → SHA256 checksum (available for both tiers)
///
/// Architecture selection: selects the artifact matching the target node's architecture.
/// Falls back to architecture=null (universal) if no arch-specific artifact exists.
/// </summary>
private Dictionary<string, string> ResolveArtifactVariables(
    VmTemplate template,
    string targetArchitecture)  // "amd64" or "arm64"
{
    var variables = new Dictionary<string, string>();

    foreach (var artifact in template.Artifacts)
    {
        // Skip arch-specific artifacts that don't match target
        if (artifact.Architecture != null &&
            artifact.Architecture != targetArchitecture)
            continue;

        var prefix = artifact.IsExternalReference
            ? $"EXTERNAL_URL:{artifact.Name}"
            : $"ARTIFACT_URL:{artifact.Name}";

        var url = artifact.IsExternalReference
            ? artifact.SourceUrl ?? string.Empty
            : $"http://192.168.122.1:5100/api/artifacts/{artifact.Sha256}";

        variables[prefix] = url;
        variables[$"ARTIFACT_SHA256:{artifact.Name}"] = artifact.Sha256;
    }

    return variables;
}
```

### 3.8 System VM Templates in TemplateSeederService

System VMs are seeded as regular `VmTemplate` records by `TemplateSeederService`. The only difference from community templates: `AuthorId = "system"`, `IsCommunity = false`.

**Template slugs:**

| Role | Template Slug | Category |
|---|---|---|
| Relay | `system-relay` | `infrastructure` |
| DHT | `system-dht` | `infrastructure` |
| BlockStore | `system-blockstore` | `infrastructure` |

**Example system template structure:**

```csharp
new VmTemplate
{
    Slug = "system-dht",
    Name = "DHT Peer Node",
    Version = 1,
    Category = "infrastructure",
    AuthorId = "system",
    IsCommunity = false,
    IsVerified = true,
    Visibility = TemplateVisibility.Public,   // Visible in marketplace
    PricingModel = TemplatePricingModel.Free, // Free to deploy
    CloudInitTemplate = /* loads from assets/dht/cloud-init.yaml */,
    Artifacts = new List<TemplateArtifact>
    {
        new() { Name = "dht-node", Type = ArtifactType.Binary, Architecture = "amd64", ... },
        new() { Name = "dht-node", Type = ArtifactType.Binary, Architecture = "arm64", ... },
        new() { Name = "dht-dashboard-assets", Type = ArtifactType.WebAsset, ... },
        new() { Name = "shared-scripts", Type = ArtifactType.Archive, ... },
    }
}
```

### 3.9 Unified Deployment Path

`SystemVmReconciliationService` replaces bespoke per-role deployment logic with a single call:

```csharp
// BEFORE (per-role bespoke, scattered):
case SystemVmRole.Relay:   return await _relayNodeService.DeployRelayVmAsync(node, vmService, ct);
case SystemVmRole.Dht:     return await _dhtNodeService.DeployDhtVmAsync(node, vmService, ct);
case SystemVmRole.BlockStore: return await _blockStoreService.DeployBlockStoreVmAsync(node, vmService, ct);

// AFTER (unified):
private async Task<string?> DeploySystemVmAsync(Node node, SystemVmRole role, CancellationToken ct)
{
    var templateSlug = role switch
    {
        SystemVmRole.Relay      => "system-relay",
        SystemVmRole.Dht        => "system-dht",
        SystemVmRole.BlockStore => "system-blockstore",
        _ => throw new ArgumentException($"Unknown system VM role: {role}")
    };

    var template = await _templateService.GetTemplateBySlugAsync(templateSlug)
        ?? throw new InvalidOperationException($"System template not found: {templateSlug}");

    // Build deployment config (configuration variables only — identity comes from ObligationStateService)
    var deployConfig = await BuildSystemVmDeployConfigAsync(node, role, ct);

    var vmService = _serviceProvider.GetRequiredService<IVmService>();
    var request = await _templateService.BuildVmRequestFromTemplateAsync(
        template.Id,
        vmName: $"{role.ToString().ToLower()}-{node.Id[..8]}",
        environmentVariables: deployConfig);

    var response = await vmService.CreateVmAsync(systemUserId, request, node.Id);
    return response.VmId;
}
```

`BuildSystemVmDeployConfigAsync` provides **deployment-specific** variables only (bootstrap peers, orchestrator URL, region) — **not** identity. Identity is fetched by the VM from `ObligationStateService` at boot time.

### 3.10 What Gets Deleted

Once system templates are live and tested:

```
src/Orchestrator/Services/RelayNodeService.cs
  DELETED: DeployRelayVmAsync() (200+ lines of bespoke logic)
  KEPT: AssignCgnatNodeToRelayAsync(), FindBestRelayForCgnatNodeAsync()

src/Orchestrator/Services/DhtNodeService.cs  
  DELETED: DeployDhtVmAsync() (150+ lines of bespoke logic)
  KEPT: DHT peer lookup, bootstrap peer resolution

src/Orchestrator/Services/BlockStoreService.cs
  DELETED: DeployBlockStoreVmAsync() (150+ lines of bespoke logic)
  KEPT: CID indexing, manifest management, join/announce handlers

src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitTemplateService.cs
  DELETED: InjectRelayExternalTemplatesAsync()
  DELETED: InjectDhtExternalTemplatesAsync()
  DELETED: InjectBlockStoreExternalTemplatesAsync()
  DELETED: LoadDhtBinaryAsync(), LoadBlockStoreBinaryAsync()
  DELETED: BuildDhtBinaryFromSourceAsync(), BuildBlockStoreBinaryFromSourceAsync()
  DELETED: _externalTemplateCache, _templateCache
  DELETED: Per-role variable population methods

src/DeCloud.NodeAgent/Services/GoBinaryBuildStartupService.cs
  DELETED: Entire file

src/DeCloud.NodeAgent/CloudInit/Templates/
  MOVED to assets/ in repository root (managed as template artifacts):
  ├── dht-vm/dht-node-src/      → assets/dht/dht-node-src/
  ├── blockstore-vm/blockstore-node-src/ → assets/blockstore/blockstore-node-src/
  ├── relay-vm/dashboard.*      → assets/relay/
  ├── dht-vm/dashboard.*        → assets/dht/
  └── blockstore-vm/dashboard.* → assets/blockstore/
```

---

## 4. Part 2 — ObligationStateService

### 4.1 Design Goals

- System VM identity (Ed25519 keys, WireGuard keys, auth tokens, subnet IPs) survives VM crashes and redeployments
- Identity is stored on the **node** (in SQLite), not in the VM's ephemeral disk
- Node agent serves identity to VMs over virbr0 — same trust boundary as artifact cache
- Orchestrator remains the source of truth; node agent stores authoritative local copy
- Version-based conflict resolution: higher version always wins (orchestrator is authoritative)
- No orchestrator contact required at VM recovery time

### 4.2 SQLite Schema

**File:** `src/DeCloud.NodeAgent.Infrastructure/Persistence/ObligationStateRepository.cs` *(new)*

Extends the existing SQLite database (same file as `VmRepository` and `PortMappingRepository`):

```sql
CREATE TABLE IF NOT EXISTS obligation_state (
    role        TEXT PRIMARY KEY,   -- "relay" | "dht" | "blockstore"
    state_json  TEXT NOT NULL,      -- Role-specific identity blob (see §4.3)
    version     INTEGER NOT NULL,   -- Monotonic version from orchestrator
    updated_at  TEXT NOT NULL       -- ISO 8601 UTC timestamp
);
```

### 4.3 Identity State Per Role

**File:** `src/DeCloud.NodeAgent.Core/Models/ObligationState.cs` *(new)*

```csharp
/// <summary>
/// Base class for all obligation identity state.
/// Stored in SQLite obligation_state table as JSON.
/// </summary>
public abstract class ObligationStateBase
{
    public int Version { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RelayObligationState : ObligationStateBase
{
    public string WireGuardPrivateKey { get; set; } = string.Empty;
    public string WireGuardPublicKey { get; set; } = string.Empty;
    public string TunnelIp { get; set; } = string.Empty;       // e.g. "10.20.0.1"
    public string RelaySubnet { get; set; } = string.Empty;    // e.g. "10.20.1.0/24"
    public string AuthToken { get; set; } = string.Empty;
}

public class DhtObligationState : ObligationStateBase
{
    public string Ed25519PrivateKeyBase64 { get; set; } = string.Empty; // libp2p identity
    public string PeerId { get; set; } = string.Empty;                  // derived from key
    public string WireGuardPrivateKey { get; set; } = string.Empty;
    public string WireGuardPublicKey { get; set; } = string.Empty;
    public string TunnelIp { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
}

public class BlockStoreObligationState : ObligationStateBase
{
    public string Ed25519PrivateKeyBase64 { get; set; } = string.Empty;
    public string PeerId { get; set; } = string.Empty;
    public string WireGuardPrivateKey { get; set; } = string.Empty;
    public string WireGuardPublicKey { get; set; } = string.Empty;
    public string TunnelIp { get; set; } = string.Empty;
    public string AuthToken { get; set; } = string.Empty;
    public long StorageQuotaBytes { get; set; }                          // 5% of node storage
}
```

### 4.4 ObligationStateService

**File:** `src/DeCloud.NodeAgent.Infrastructure/Services/ObligationStateService.cs` *(new)*

```csharp
public interface IObligationStateService
{
    /// <summary>
    /// Persist obligation state received from orchestrator.
    /// Only updates if incomingVersion > stored version (orchestrator is authoritative).
    /// </summary>
    Task<bool> SaveStateAsync(string role, string stateJson, int version, CancellationToken ct = default);

    /// <summary>
    /// Retrieve obligation state for a role.
    /// Returns null if no state exists for this role.
    /// </summary>
    Task<string?> GetStateJsonAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Get the current version for a role. Returns 0 if no state exists.
    /// Used during re-registration to send current version to orchestrator.
    /// </summary>
    Task<int> GetVersionAsync(string role, CancellationToken ct = default);

    /// <summary>
    /// Delete state for a role (e.g., when obligation is removed).
    /// </summary>
    Task DeleteStateAsync(string role, CancellationToken ct = default);
}
```

### 4.5 Node Agent HTTP Endpoint

**File:** `src/DeCloud.NodeAgent/Controllers/ObligationStateController.cs` *(new)*

```csharp
/// <summary>
/// Serves obligation identity state to system VMs over virbr0.
///
/// Security model:
///   - Bound to 192.168.122.1:5100 (virbr0 bridge only)
///   - Accessible only from VMs on this node's libvirt network
///   - No authentication: isolation is network-level (virbr0 is not routable externally)
///   - State contains private keys — virbr0 isolation is the security boundary
///
/// VMs query this endpoint at boot time (before starting services) to restore
/// their persistent identity from the previous deployment.
/// </summary>
[ApiController]
[Route("api/obligations")]
public class ObligationStateController : ControllerBase
{
    // GET /api/obligations/{role}/state
    // Returns identity state JSON for the given role.
    // Role: "relay" | "dht" | "blockstore"
    // Returns 404 if no state exists (first boot or state cleared).
    // Returns 200 with state JSON on success.

    // GET /api/obligations/{role}/version
    // Returns current version as integer. Lightweight check.
    // Used by orchestrator to detect if state needs updating.
}
```

### 4.6 Orchestrator State Generation and Delivery

**File:** `src/Orchestrator/Services/SystemVm/ObligationStateGenerator.cs` *(new)*

The orchestrator generates obligation state when obligations are first created, and delivers it to the node agent:

```csharp
public class ObligationStateGenerator
{
    /// <summary>
    /// Generate fresh identity state for a new obligation.
    /// Called once at obligation creation time.
    /// State is stored in MongoDB on the node's obligation record,
    /// and pushed to the node agent for SQLite persistence.
    /// </summary>
    public ObligationStateBase GenerateState(SystemVmRole role, Node node)
    {
        return role switch
        {
            SystemVmRole.Relay => new RelayObligationState
            {
                WireGuardPrivateKey = GenerateWireGuardPrivateKey(),
                WireGuardPublicKey  = DerivePublicKey(/*private key*/),
                TunnelIp            = AllocateRelayTunnelIp(node),
                RelaySubnet         = AllocateRelaySubnet(node),
                AuthToken           = GenerateSecureToken(),
                Version             = 1,
            },
            SystemVmRole.Dht => new DhtObligationState
            {
                Ed25519PrivateKeyBase64 = GenerateEd25519Key(),
                PeerId                  = DerivePeerId(/*ed25519 key*/),
                WireGuardPrivateKey     = GenerateWireGuardPrivateKey(),
                WireGuardPublicKey      = DerivePublicKey(/*private key*/),
                TunnelIp                = node.CgnatInfo?.TunnelIp ?? GetDhtTunnelIp(node),
                AuthToken               = GenerateSecureToken(),
                Version                 = 1,
            },
            SystemVmRole.BlockStore => new BlockStoreObligationState
            {
                Ed25519PrivateKeyBase64 = GenerateEd25519Key(),
                PeerId                  = DerivePeerId(/*ed25519 key*/),
                WireGuardPrivateKey     = GenerateWireGuardPrivateKey(),
                WireGuardPublicKey      = DerivePublicKey(/*private key*/),
                TunnelIp                = GetBlockStoreTunnelIp(node),
                AuthToken               = GenerateSecureToken(),
                StorageQuotaBytes       = CalculateStorageQuota(node), // 5% of total
                Version                 = 1,
            },
            _ => throw new ArgumentException($"Unknown role: {role}")
        };
    }
}
```

**Delivery mechanism:**

State is sent to the node agent via the existing `NodeRegistrationResponse` (at registration time) and via a new `PUT /api/nodes/me/obligations/{role}/state` endpoint for updates.

The `SystemVmObligation` model gains a `StateJson` field (stored in MongoDB) so the orchestrator retains a copy:

```csharp
// Added to SystemVmObligation:
public string? StateJson { get; set; }     // JSON-serialized identity state
public int StateVersion { get; set; }      // Current state version
```

### 4.7 Conflict Resolution

```
Orchestrator version > node version  → node accepts update
Orchestrator version == node version → no update (identity preserved)
Orchestrator version < node version  → log warning, orchestrator wins
                                        (should not happen — orchestrator is authoritative)
```

During re-registration, the node agent reports current state versions per role:

```csharp
// Added to NodeRegistrationRequest:
public Dictionary<string, int> ObligationStateVersions { get; set; } = new();
// Example: { "relay": 3, "dht": 2, "blockstore": 1 }
```

The orchestrator compares these against stored versions and only pushes state when its version is higher.

### 4.8 Cloud-Init Identity Query Pattern

System VM templates query the node agent for identity at boot, before starting services:

```yaml
# In system-dht cloud-init template:
runcmd:
  # ── Artifacts (from cache — fast, local) ──
  - curl -sf ${ARTIFACT_URL:dht-node} -o /usr/local/bin/dht-node
  - echo "${ARTIFACT_SHA256:dht-node}  /usr/local/bin/dht-node" | sha256sum -c -
  - chmod +x /usr/local/bin/dht-node
  - curl -sf ${ARTIFACT_URL:dht-dashboard-assets} | tar xz -C /opt/decloud-dht/
  - curl -sf ${ARTIFACT_URL:shared-scripts} | tar xz -C /usr/local/bin/

  # ── Identity (from obligation state — persistent across redeployments) ──
  - |
    IDENTITY=$(curl -sf --retry 5 --retry-delay 2 \
      http://192.168.122.1:5100/api/obligations/dht/state)

    if [ -z "$IDENTITY" ]; then
      echo "FATAL: No obligation state available — cannot start DHT service"
      exit 1
    fi

    mkdir -p /var/lib/decloud-dht /etc/decloud-dht
    chmod 700 /var/lib/decloud-dht

    echo "$IDENTITY" | jq -r '.ed25519PrivateKeyBase64' | base64 -d \
      > /var/lib/decloud-dht/identity.key
    chmod 600 /var/lib/decloud-dht/identity.key

    echo "$IDENTITY" | jq -r '.tunnelIp' > /etc/decloud-dht/tunnel-ip
    echo "$IDENTITY" | jq -r '.authToken' > /etc/decloud-dht/auth-token
    chmod 600 /etc/decloud-dht/auth-token

  # ── WireGuard (identity from obligation state) ──
  - |
    IDENTITY=$(curl -sf http://192.168.122.1:5100/api/obligations/dht/state)
    WG_PRIVATE=$(echo "$IDENTITY" | jq -r '.wireGuardPrivateKey')
    WG_TUNNEL=$(echo "$IDENTITY" | jq -r '.tunnelIp')

    cat > /etc/wireguard/wg-mesh.conf <<EOF
    [Interface]
    PrivateKey = ${WG_PRIVATE}
    Address = ${WG_TUNNEL}
    EOF
    chmod 600 /etc/wireguard/wg-mesh.conf

  # ── Start service (reads identity.key from disk — same peer ID as before) ──
  - systemctl enable --now decloud-dht
```

The Go binaries don't change. `loadOrCreateIdentity()` reads from disk first — cloud-init placed the key file before the binary starts, so it always finds the existing key. **Same peer ID across every redeployment.**

---

## 5. Security Considerations

### 5.1 Artifact Integrity Chain

```
Upload time:
  Uploader computes SHA256 locally → submits with file
  Orchestrator re-computes SHA256 server-side → stores in MongoDB
  (client-provided hash is not trusted — always re-verified)

Node agent prefetch:
  Downloads artifact from orchestrator API
  Verifies SHA256 before caching → rejects if mismatch

VM boot:
  Downloads from node agent cache (http://192.168.122.1:5100)
  Verifies ${ARTIFACT_SHA256:name} inside cloud-init
  Refuses to execute if mismatch

Trust root:
  MongoDB (orchestrator) holds authoritative SHA256 values
  A compromised node agent cannot serve a poisoned artifact —
  the VM verifies the hash provided by the orchestrator
  (embedded in cloud-init via ${ARTIFACT_SHA256:name})
```

### 5.2 ObligationStateService Security

- Endpoint (`/api/obligations/{role}/state`) binds to `192.168.122.1:5100` (virbr0 only)
- virbr0 is not routable externally — only local VMs can reach it
- No authentication required: network-level isolation is the security boundary
- State contains private keys — log access at DEBUG level, never at INFO
- SQLite file permissions: `600` (root-owned, no world read)
- State JSON never logged in full (only role + version logged)

### 5.3 Artifact Upload Security

- Max 250MB per artifact (enforced at multipart boundary, before full upload)
- Binary artifacts: require `IsCommunity = false` OR platform admin approval
- Script/WebAsset/Config/Archive: automated scan (same pattern as current `TemplateService.ValidateTemplateAsync`)
- SHA256 mismatch on upload → immediate rejection, no file stored
- External references: HEAD request to verify URL is reachable at publish time

### 5.4 Relay Revenue-Share Security

- `relayNodeId` sourced from `node.CgnatInfo.AssignedRelayNodeId` (orchestrator-assigned, not user-provided)
- Revenue share calculation happens server-side in `BillingService` — no relay VM involvement
- `PaymentConfig.RelayRevenueSharePercentage` is a server-side config value (cannot be spoofed by relay operator)

---

## 6. Relay Revenue-Share Implementation

### 6.1 Overview

CGNAT node operators pay a small revenue share (configurable, default 2%) to the relay operator that enables their network participation. No billing logic lives in the relay VM — it's purely an orchestrator-side calculation.

### 6.2 Configuration

**File:** `src/Orchestrator/Models/Payment/PaymentConfig.cs` *(extended)*

```csharp
// Added to PaymentConfig:

/// <summary>
/// Percentage of CGNAT node revenue transferred to the assigned relay node operator.
/// Applied during billing settlement for nodes with non-null CgnatInfo.AssignedRelayNodeId.
/// Range: 0.0 to 1.0 (default: 0.02 = 2%)
/// </summary>
public decimal RelayRevenueShareRatio { get; set; } = 0.02m;
```

### 6.3 Billing Integration

**File:** `src/Orchestrator/Services/Balance/BillingService.cs` *(extended)*

```csharp
// In CalculateNodeEarningsAsync() or equivalent billing settlement method:

if (node.CgnatInfo?.AssignedRelayNodeId is { } relayNodeId
    && _paymentConfig.RelayRevenueShareRatio > 0)
{
    var relayShare = nodeEarnings * _paymentConfig.RelayRevenueShareRatio;
    var cgnatShare = nodeEarnings - relayShare;

    // Record two settlements
    await RecordEarningsAsync(node.Id, cgnatShare);
    await RecordEarningsAsync(relayNodeId, relayShare);

    _logger.LogInformation(
        "Relay revenue share: CGNAT node {CgnatId} earned {CgnatAmount:C}, " +
        "relay node {RelayId} earned {RelayAmount:C} ({Ratio:P0} share)",
        node.Id, cgnatShare, relayNodeId, relayShare,
        _paymentConfig.RelayRevenueShareRatio);
}
else
{
    // Public IP node or no relay — full earnings to this node
    await RecordEarningsAsync(node.Id, nodeEarnings);
}
```

---

## 7. Resilience Scenarios

| Failure | Recovery (After This Implementation) |
|---|---|
| System VM crashes | Node agent detects via VmHealthService → redeployment via template. Identity from SQLite. Same peer ID — instant mesh rejoin. |
| System VM disk corrupted | Same as above. No identity regeneration. |
| Orchestrator unreachable | Node agent has everything locally: template (will be cached Phase 2), artifacts (cached), identity (SQLite). Can redeploy without orchestrator. |
| Node reboots (power failure) | Node agent starts → SQLite intact → system VMs redeploy with same identity. Orchestrator learns via heartbeat. |
| DHT redeployment (software update) | New VM boots → reads Ed25519 key from SQLite → same peer ID → other peers reconnect without cascading updates. |
| Relay redeployment | WireGuard keypair preserved in SQLite → same public key → all mesh-enrolled DHT/BlockStore VMs reconnect automatically. |
| Orchestrator DB lost | Node agent's SQLite has authoritative identity copy. On reconnection, orchestrator can reconstruct state from node heartbeats (identity embedded in heartbeat response). |

---

## 8. Implementation Plan

### Phase A — Enriched Template Foundation

1. **`TemplateArtifact` model** + `VmTemplate.Artifacts[]` field
2. **Artifact storage** — `/var/lib/decloud/artifacts/{sha256}` on orchestrator filesystem
3. **Upload/download endpoints** — `MarketplaceController` extensions
4. **`ResolveArtifactVariables()`** in `TemplateService`
5. **`ArtifactCacheService`** + `ArtifactCacheController` on node agent
6. **Integration test**: upload artifact → seed template → deploy VM → verify artifact downloaded and SHA256 verified inside VM

### Phase B — System Templates + Obligation State

7. **`ObligationStateBase` models** (Relay, Dht, BlockStore variants)
8. **`ObligationStateRepository`** (SQLite schema + CRUD)
9. **`IObligationStateService`** + `ObligationStateService`
10. **`ObligationStateController`** on node agent (virbr0 endpoint)
11. **`ObligationStateGenerator`** on orchestrator
12. **Delivery via `NodeRegistrationResponse`** (existing response extended)
13. **Update `SystemVmObligation`** with `StateJson` + `StateVersion` fields
14. **Integration test**: register node → verify identity state delivered → redeploy system VM → verify same peer ID

### Phase C — System VM Template Conversion

15. **Seed system templates** in `TemplateSeederService` (Relay, DHT, BlockStore)
16. **Convert cloud-init** to use `${ARTIFACT_URL:name}` + `/api/obligations/{role}/state` query
17. **Unified `DeploySystemVmAsync()`** in `SystemVmReconciliationService`
18. **Integration test**: full system VM lifecycle on a test node — deploy, crash, redeploy, verify same identity
19. **Delete bespoke deployment logic** from `RelayNodeService`, `DhtNodeService`, `BlockStoreService`

### Phase D — Revenue Share

20. **`RelayRevenueShareRatio`** in `PaymentConfig`
21. **Billing logic** in `BillingService` — split earnings for CGNAT nodes
22. **Settlement test**: simulate CGNAT node earning → verify relay operator receives correct share

### Phase E — Cleanup

23. Delete `GoBinaryBuildStartupService.cs`
24. Delete `CloudInitTemplateService.Inject*ExternalTemplatesAsync()` methods
25. Delete `CloudInitTemplateService.Load*BinaryAsync()` / `Build*FromSourceAsync()` methods
26. Move `CloudInit/Templates/` contents to `assets/` in repository
27. Remove Go toolchain from `install.sh`

---

## 9. File Change Summary

### Orchestrator — New Files

```
src/Orchestrator/Models/TemplateArtifact.cs
src/Orchestrator/Services/SystemVm/ObligationStateGenerator.cs
src/Orchestrator/Services/ArtifactStorageService.cs
```

### Orchestrator — Modified Files

```
src/Orchestrator/Models/VmTemplate.cs              (add Artifacts[], TotalArtifactSizeBytes, Version, Slug)
src/Orchestrator/Models/Payment/PaymentConfig.cs   (add RelayRevenueShareRatio)
src/Orchestrator/Models/SystemVmObligation.cs      (add StateJson, StateVersion)
src/Orchestrator/Controllers/MarketplaceController.cs (artifact upload/download endpoints)
src/Orchestrator/Services/TemplateService.cs       (ResolveArtifactVariables())
src/Orchestrator/Services/TemplateSeederService.cs (seed system templates with artifacts)
src/Orchestrator/Services/NodeService.cs           (deliver obligation state at registration)
src/Orchestrator/Services/SystemVm/SystemVmReconciliationService.cs (unified DeploySystemVmAsync)
src/Orchestrator/Services/Balance/BillingService.cs (relay revenue share)
src/Orchestrator/Persistence/DataStore.cs          (artifact storage methods)
```

### Node Agent — New Files

```
src/DeCloud.NodeAgent/Controllers/ArtifactCacheController.cs
src/DeCloud.NodeAgent/Controllers/ObligationStateController.cs
src/DeCloud.NodeAgent.Core/Models/ObligationState.cs
src/DeCloud.NodeAgent.Core/Interfaces/IObligationStateService.cs
src/DeCloud.NodeAgent.Core/Interfaces/IArtifactCacheService.cs
src/DeCloud.NodeAgent.Infrastructure/Services/ArtifactCacheService.cs
src/DeCloud.NodeAgent.Infrastructure/Services/ObligationStateService.cs
src/DeCloud.NodeAgent.Infrastructure/Persistence/ObligationStateRepository.cs
```

### Node Agent — Deleted Files

```
src/DeCloud.NodeAgent/Services/GoBinaryBuildStartupService.cs
```

### Node Agent — Modified Files

```
src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitTemplateService.cs  (remove Inject* methods)
src/DeCloud.NodeAgent.Infrastructure/Persistence/VmRepository.cs           (add obligation_state table migration)
src/DeCloud.NodeAgent/Program.cs                                            (register new services)
```

### Orchestrator — Deleted Logic (not files)

```
RelayNodeService.DeployRelayVmAsync()         → replaced by unified template path
DhtNodeService.DeployDhtVmAsync()             → replaced by unified template path
BlockStoreService.DeployBlockStoreVmAsync()   → replaced by unified template path
```

---

## 10. Success Criteria

| Criterion | Verification |
|---|---|
| System VM identity survives redeployment | Redeploy DHT VM → verify same peer ID in DHT network |
| Relay redeployment does not cascade | Redeploy relay → DHT/BlockStore VMs reconnect without reconfiguration |
| Artifacts served at ~1Gbps locally | `iperf3` or `curl` timing from VM to node agent over virbr0 |
| SHA256 verified end-to-end | Tamper artifact file → verify VM boot fails checksum |
| System VMs deployable from marketplace UI | User deploys "DHT Peer Node" from marketplace — succeeds |
| Relay revenue share calculated correctly | BillingService unit test: CGNAT node $100 → relay gets $2, CGNAT gets $98 |
| Unified deployment path | Remove `RelayNodeService.DeployRelayVmAsync()` → relay deployment still works |
| No regression on user VM deployment | Existing community template deployments unchanged |

---

*End of Design Document*
