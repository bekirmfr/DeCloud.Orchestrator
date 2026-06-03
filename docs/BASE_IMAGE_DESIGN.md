# Base Image Content Addressing — Design & Implementation

**Status:** Shipped (PR1 + PR2)
**Scope:** Image management, VM creation, migration, cleaning placement, registry consolidation
**Authors:** DeCloud engineering
**Related:** `MIGRATION_SYSTEM_DESIGN.md` (Live Migration), `UNIFIED_CLOUDINIT_PIPELINE.md` (Cloud-init rendering)

---

## 1. Problem

A tenant VM with overlay replication was migrated from one node to another.
The migration succeeded by the system's measures (chunks fetched, command
acked, VM transitioned to Running) but the guest could not boot. Diagnosis
showed GRUB dropping to `grub rescue>` with `/boot/grub/i386-pc/normal.mod`
not found.

Inspection of the target node revealed the base image at
`/var/lib/decloud/images/846E9E904A0F3A86_nohash.img` was **Ubuntu 22.04**,
while the source VM's overlay had been authored against a **Debian 12** base.
The overlay's depth-0 clusters were faithful to the source's overlay, but the
backing chain on the target contained different bytes — composing them
produced a filesystem with dentries from one distribution pointing at inodes
occupied by another's data.

The cached file's name was derived from the SHA256 of the Debian image URL.
The bytes at that path were Ubuntu. There was no integrity check, anywhere,
that would have caught this divergence.

---

## 2. Root Cause: Compounding Gaps

### 2.1 URL-keyed cache (not content-keyed)

`ImageManager.GetCacheFileName` derived the cache filename from
`SHA256(url)[..16]`. Two different qcow2 files downloaded from the same URL
at different times landed at the same path. Two nodes that downloaded from
the same URL at different times could have different bytes under the same
filename and never know.

### 2.2 `BaseImageHash` field existed but was never populated

`VmSpec.BaseImageHash`, `SystemVmTemplate.BaseImageHash`, and
`VmImage.ChecksumSha256` were all declared and persisted, but no code path
wrote a non-empty value to them. `BuildSystemVmTemplate` literally hardcoded
`BaseImageHash = string.Empty`. The `_nohash` suffix in the cached filename
was the literal trace of this.

### 2.3 Migration `CreateVmPayload` omitted `BaseImageUrl` entirely

The migration dispatch in `BackgroundServices.cs` built a `CreateVmPayload`
that jumped from `ContainerImage` straight to `SshPublicKey`, never setting
`BaseImageUrl`. The receiving node fell through to
`ArchitectureHelper.ResolveImageUrl` which defaulted to `ubuntu-22.04` when
no imageId was provided. The migration silently changed which base image
was used.

### 2.4 In-place cleaning broke identity

`ImageManager.EnsureImageAvailableAsync` called `CloudInitCleaner.CleanAsync`
on the cached file directly. The cleaner used virt-customize / guestmount /
qemu-nbd to mutate `/var/lib/cloud`, `/etc/machine-id`, etc. inside the
qcow2. Post-cleaning bytes varied per node (mtimes, inode allocation order,
ext4 journal state) — even if every node downloaded byte-identical upstream
bytes, the cached files on different nodes would not be byte-identical after
cleaning. The hash drifted away from any stable identity.

### 2.5 Fragmented image registry

The same image was tracked across four overlapping registries with no
documented rule for which field went where:

- `DataStore.Images` — user-facing catalog (display metadata).
- `BaseImageUrlResolver.ByArchThenImageId` — orchestrator-side
  (URL, SHA256) per arch.
- `ArchitectureHelper.ImageUrlsByArchitecture` — node-side fallback duplicate.
- `SystemVmRoleMap.ToBaseImageId` — role → imageId.

The fragmentation isn't directly what caused the Frankenstein-FS incident,
but it's the structural condition under which 2.1-2.4 grew. Adding a new
image required touching three to four files; "where does the hash go" had
no canonical answer; the node-side fallback was the band-aid that silently
substituted ubuntu-22.04 when migration payloads were incomplete.

---

## 3. Why Content Addressing Is Necessary, Not Optional

The lazysync overlay is a **block-level** diff. `LazysyncDaemon` exports the
depth-0 extents (clusters owned by the overlay, not the backing chain) as
1 MiB chunks keyed by byte offset in the virtual disk address space. The
overlay is meaningless in isolation — its semantics depend entirely on the
specific bytes at the offsets it does not touch, which come from the backing
chain.

That dependency is byte-level, not file-level. The overlay does not know
which files were modified; it knows which raw blocks were modified. When the
filesystem follows a directory entry → inode → data blocks, the inode and
the data blocks may be in different qcow2 layers, and they must agree about
which bytes live where.

Two consequences:

1. **"Same OS version" is not enough.** A Debian 12.5.0 base and a Debian
   12.6.0 base both report `bookworm` in `/etc/os-release`, but the on-disk
   layout differs — different package versions, different inode allocations,
   different `/boot/` content. An overlay authored against 12.5.0 cannot be
   safely composed onto 12.6.0.

2. **Byte-identical bases are the requirement.** Content addressing (SHA256
   of the file bytes) is the only mechanism that identifies "the same base"
   in a way the system can verify. It is the prerequisite of safe
   block-level overlay replication, not an optimization on top.

The rest of the system already uses content addressing for everything else:
overlay chunks are identified by CID, template artifacts are identified by
SHA256, system VM binaries are identified by cosign-verified SHA256. Base
images are the one asymmetry; aligning them to the same boundary fixes the
class of problem.

---

## 4. Design

### 4.1 One registry: `DataStore.Images` is authoritative

After consolidation (PR2), `DataStore.Images` is the single source of truth
for every base image the platform supports. It carries both catalog display
metadata and the deployment-time (URL, SHA256) table; both are consulted via
the same lookup, so they cannot diverge.

```csharp
public class VmImage
{
    // Identity & catalog metadata
    public string Id { get; set; }            // "debian-12", "ubuntu-22.04"
    public string Name { get; set; }
    public string Description { get; set; }
    public string OsFamily { get; set; }
    public string OsName { get; set; }
    public string Version { get; set; }
    public long   SizeGb { get; set; }
    public bool   IsPublic { get; set; }
    public DateTime CreatedAt { get; set; }

    // Per-arch deployment data
    public Dictionary<string, BaseImageDescriptor> ByArchitecture { get; set; }

    public static string? NormaliseArchTag(string? raw) => /* ... */;
}

public sealed record BaseImageDescriptor(string Url, string Sha256);
```

Outer dictionary key: `"amd64"` | `"arm64"` (normalised via
`VmImage.NormaliseArchTag`). Value: the (URL, SHA256) descriptor the node
uses for download and verification.

System VMs (Relay, DHT, BlockStore) all resolve to `"debian-12"` —
`SystemVmRoleMap.ToBaseImageId` returns the same imageId for all three
roles. The role-specific identity lives in the cloud-init template, not in
the image. There are no longer separate `debian-12-relay` / `debian-12-dht`
/ `debian-12-blockstore` catalog entries.

The registry is consulted from two places, both fresh-creation:

- `VmService.GetImageDescriptor(imageId, nodeArchitecture)` — tenant fresh.
- `NodeService.BuildSystemVmTemplate(role, template, arch, dataStore)` —
  system VM fresh.

Both share the same one-liner:

```csharp
var archTag = VmImage.NormaliseArchTag(nodeArchitecture) ?? "amd64";
var descriptor = _dataStore.Images.TryGetValue(imageId, out var image)
    ? image.ByArchitecture.GetValueOrDefault(archTag)
    : null;
```

Returns null on miss; callers fail the schedule with a clear error. No
fallback to a default image.

### 4.2 URL pinning discipline

Every URL in `DataStore.Images` must point at a specific, immutable upstream
build — never `latest/`, `current/`, or any other moving symlink.

Examples (current seed):

```
Ubuntu Noble:   releases/noble/release-20260518/noble-server-cloudimg-amd64.img
Ubuntu Jammy:   releases/jammy/release-20260515/jammy-server-cloudimg-amd64.img
Debian 12:      bookworm/20260518-2482/debian-12-generic-amd64-20260518-2482.qcow2
Fedora 40:      Fedora-Cloud-Base-Generic.x86_64-40-1.14.qcow2     (versioned in name)
Alpine 3.19:    nocloud_alpine-3.19.1-x86_64-bios-cloudinit-r0.qcow2 (versioned in name)
```

**Why this matters.** The content-addressing guarantee — bytes recorded at
the orchestrator are reproducibly available at any node, forever — depends
on URLs being immutable. If a URL points at `latest/` and upstream rotates,
the recorded hash mismatches the downloaded bytes, and every fresh deploy
fails until somebody updates the seed. Worse, an existing VM that migrates
to a node without the bytes cached can't be reproduced — its recorded hash
is what was at `latest/` when it deployed, but `latest/` no longer serves
those bytes.

Versioned URLs sidestep both failures: the bytes at a dated URL today are
the bytes that will be there in two years, modulo upstream eventually
pruning very old builds (years out, not weeks).

**Enforcement.** Code review is the primary check; the convention is
documented in the seed file's leading comment. A startup guard in
`DataStore.SeedDefaultData` (added as a small follow-up) warns when a seed
URL contains `/latest/` or `/current/`, making the convention executable
rather than tribal.

### 4.3 Hash propagates with every CreateVm dispatch

`CreateVmPayload` carries `BaseImageHash` alongside `BaseImageUrl`. Every
dispatch populates it:

- **Tenant fresh:** orchestrator reads `(Url, Sha256)` from
  `_dataStore.Images[imageId].ByArchitecture[arch]`.
- **System fresh:** same lookup via `SystemVmRoleMap.ToBaseImageId(role)`
  → `"debian-12"` → catalog lookup.
- **Tenant migration:** orchestrator reads from `fresh.Spec.BaseImageHash`
  and `fresh.Spec.BaseImageUrl` (populated at first deploy and
  round-tripped via heartbeat). The catalog is NOT consulted for migration.
- **System migration:** not applicable; system VMs are ephemeral.

The migration also carries `BaseImageUrl` from the VM's own spec — the
missing-URL bug from 2.3 was fixed in PR1 incidentally.

### 4.4 Node-side cache is content-addressed

`ImageManager.EnsureImageAvailableAsync` operates in two modes:

1. **Strict mode** (`expectedHash` non-empty): look up
   `{cache}/{sha256}.img`. Present → return immediately (filename match
   is the verification — the file got into the cache only after passing
   strict verification in a prior call). Missing → check for legacy
   `*_nohash.*` files in the cache directory; if any hash to the expected
   value, rename in place (lazy migration; no flag day). Still missing →
   download with `CryptoStream` hashing on the fly. On completion: if
   `computed != expected`, throw and delete the temp file; on match,
   rename to `{sha256}.img`.

2. **Permissive mode** (`expectedHash` empty): download with hash-on-the-fly
   via `CryptoStream`, rename to `{sha256}.img`, return the computed hash
   so the caller records it back into `VmSpec.BaseImageHash`.

The cached file is the exact bytes that hash to its filename — immutable.

`TryVerifyAgainstUpstreamChecksumAsync` (the SHA256SUMS-from-the-internet
fallback) was removed. It was a band-aid that masked the deeper issue and
is no longer needed once hashes are authoritative.

The node never resolves URLs on its own. If `CreateVmPayload.BaseImageUrl`
is empty, `CommandProcessorService.HandleCreateVmAsync` throws — the
orchestrator is the single source of truth, and an empty URL is a bug to
surface, not a condition to paper over.

### 4.5 Cleaning moves from cache to overlay

`ImageManager.EnsureImageAvailableAsync` no longer invokes
`CloudInitCleaner.CleanAsync` on the cached file. The cached file is the
exact bytes that hash to its filename — immutable, byte-identical across
every node that resolves the same hash.

`LibvirtVmManager.CreateVmAsync` invokes cleaning on the overlay disk after
STEP 2 (`CreateOverlayDiskAsync` for fresh, `ReconstructOverlayAsync` +
`RunFsckOnOverlayAsync` for migration) and before STEP 3 (cloud-init ISO).
The overlay is per-VM; cleaning it is a one-time CoW write of about 100 KB
that strips the machine-id and cloud-init markers carried over from the
base or from a migrated source.

`CloudInitCleaner` itself needed no logic change — it takes a qcow2 path
and operates on whatever filesystem it sees through the chain. The xmldoc
was updated to reflect the new caller.

### 4.6 Hash round-trips via heartbeat

`HeartbeatVmInfo` carries `BaseImageUrl` and `BaseImageHash`. On every
heartbeat, the node reports the values currently on `vm.Spec`. The
orchestrator's `SyncVmStateFromHeartbeatAsync`:

- If `reported.BaseImageHash` is empty → no-op.
- If `vm.Spec.BaseImageHash` is empty and `reported.BaseImageHash` is
  non-empty → adopt (first-deploy hash discovery). Log:
  *"adopted base image hash X from heartbeat (first-deploy discovery)."*
- If both are non-empty and differ → log a warning. Either the cache was
  tampered with on the node, or the orchestrator's expected hash has been
  changed mid-flight. Operator-visible signal; no automatic remediation.

A small bug — `ImageId = v.Name` in `OrchestratorClient.BuildHeartbeatPayload`
— was fixed in passing (it was reporting the VM's display name as the
imageId).

### 4.7 Per-VM pin via spec, not catalog

Once a VM exists, its base image identity lives on the VM's own spec
(`vm.Spec.BaseImageUrl`, `vm.Spec.BaseImageHash`). Migration reads from
there; the catalog is consulted only at fresh creation.

This means a catalog bump (operator updates the seed URL+hash for an
imageId, drops the Mongo doc, restarts) has **no effect on existing VMs**.
A VM created against build X1 keeps `(X1, H1)` on its spec; future
migrations of that VM use `(X1, H1)`. New VMs created after the bump get
`(X2, H2)`. Both coexist in the cluster, both verifiable, both migratable —
the node cache holds `{H1}.img` and `{H2}.img` independently.

The per-VM pin is what gives the platform reproducibility for the VM's
lifetime without any history table or version map in the catalog. Each
VM is implicitly pinned to its initial build via its own spec.

### 4.8 Behaviour during rollout

PR1 wired the structure with hashes initially empty. The catalog seed
still ships with empty hashes (operator may populate when ready). Behaviour
in this state:

- Orchestrator sends `BaseImageHash = ""` for fresh CreateVm dispatches
  that resolve to a catalog entry with empty hash.
- Node sees empty hash → permissive mode: downloads, computes, stores as
  `{sha256}.img`, returns the hash.
- `LibvirtVmManager` writes the computed hash onto `vm.Spec.BaseImageHash`.
- Heartbeat reports the hash back to the orchestrator.
- Orchestrator adopts on the next sync cycle. The VM's record now carries
  the hash on its own spec.
- Migration of that VM later carries the recorded hash. Target node
  enforces strict verification.

So the system is safe from first deploy onward even with empty seed hashes
— content-addressed by construction, strict on migration. Populating the
seed hashes upgrades fresh deploys from permissive to strict but does not
change the safety properties of the platform.

---

## 5. What This Does Not Do

- **No peer-to-peer base image distribution.** When a target node has not
  cached the required hash and the URL is unreachable or has drifted, the
  migration fails cleanly with a clear error. Operator can pre-stage the
  file, redeploy, or extend cache retention (see §6 Phase 3).
- **No retroactive hash population for existing VMs.** VMs deployed before
  PR1 ships had empty `BaseImageHash` in their spec. On their next
  heartbeat after the node-agent upgrade, the node computes the hash from
  the cached file and round-trips it. If the cache was tampered with on
  that node, the wrong hash gets recorded — but this is no worse than
  before, where there was no hash at all.
- **No compose-time refusal in `qemu-img rebase`.** The `-u` flag (unsafe
  rebase) remains. Strict verification at the ImageManager boundary makes
  this safe — by the time we rebase, we already know the bytes match.
- **No file-level overlay portability.** The block-level overlay design is
  preserved. Cross-base compatibility remains impossible by construction;
  the fix is to enforce the same base, not to relax the overlay semantics.
- **No catalog-level history of image bumps.** Per-VM pin via spec covers
  the reproducibility case; an audit trail of catalog changes could be
  added via `OrchestratorEvent` rows if needed (cheap, additive). No
  schema change in `VmImage` itself.
- **No image admin endpoint.** Updating an image's URL+hash today is
  "edit seed, drop Mongo doc, restart." An admin `PUT /api/system/images/{id}`
  is a natural follow-up but out of scope.

---

## 6. Rollout Phases

### Phase 1 — Content addressing (PR1, shipped)

Implements §4.3 through §4.6 and the rollout behaviour in §4.8. Hash table
wired structurally with initial empty values. System safe in
permissive-then-strict mode: first deploy on a fresh node downloads-and-
records, subsequent migrations enforce.

### Phase 2 — Registry consolidation (PR2, shipped)

Implements §4.1 and §4.2. Collapses the four overlapping registries
(`DataStore.Images`, `BaseImageUrlResolver`, `ArchitectureHelper.ImageUrlsByArchitecture`,
system VM aliases) into one: `DataStore.Images`. `VmImage` gains
`ByArchitecture`; `BaseImageUrlResolver` deleted; `ArchitectureHelper.ImageUrlsByArchitecture`
deleted; `CommandProcessorService.ResolveImageUrlAsync` deleted. System
VM imageIds collapsed (`SystemVmRoleMap.ToBaseImageId` returns `"debian-12"`
for all three system roles).

URL pinning to dated upstream builds applied to every seed entry. Optional
startup guard against `/latest/` and `/current/` URLs to be added as a
small follow-up.

Rollout: `db.images.drop()` in Mongo, restart orchestrator. Platform-
curated catalog repopulates with the new schema. No tenant data touched.

### Phase 3 — Peer-fetch via blockstore mesh (deferred)

When a target node needs a base image hash it doesn't have and the URL is
unreachable, fetch the bytes from a peer that does. The blockstore mesh
already moves content-addressed blobs between nodes; base images would
ride the same rail (chunked, CIDed, bitswap-distributed). Deferred until
URL drift becomes a felt operational problem — versioned URLs sidestep
the common case.

A simpler intermediate step: extend `ImageManager.PruneUnusedImagesAsync`
to consult `vm.Spec.BaseImageHash` references before deleting from the
cache. Keeps the bytes available on any node that ever held them, without
peer-fetch infrastructure.

---

## 7. Touchpoints

### Files modified in PR1 (content addressing)

**Shared:**

- `src/DeCloud.Shared/Contracts/CreateVmPayload.cs` — added `BaseImageHash` field.
- `src/DeCloud.Shared/Contracts/NodeHeartbeat.cs` — added `BaseImageUrl`
  and `BaseImageHash` to `HeartbeatVmInfo`.
- `src/DeCloud.Shared/Models/VmSummary.cs` — added `BaseImageUrl` and
  `BaseImageHash` (flat fields populated by `HeartbeatService` from
  `VmInstance.Spec`).

**Orchestrator:**

- `src/Orchestrator/Models/VirtualMachine.cs` — `VmSpec` gained
  `BaseImageUrl` and `BaseImageHash`.
- `src/Orchestrator/Services/NodeService.cs` — `BuildSystemVmTemplate`
  read hash from descriptor; `SyncVmStateFromHeartbeatAsync` adopts
  `reported.BaseImageHash` into `vm.Spec.BaseImageHash`.
- `src/Orchestrator/Services/VmService.cs` — `TryScheduleVmAsync` set
  both URL and hash on `CreateVmPayload`.
- `src/Orchestrator/Background/BackgroundServices.cs` — migration dispatch
  sets `BaseImageUrl = fresh.Spec.BaseImageUrl` and
  `BaseImageHash = fresh.Spec.BaseImageHash` on the payload.

**Node:**

- `src/DeCloud.NodeAgent.Core/Interfaces/IServices.cs` — `IImageManager.EnsureImageAvailableAsync`
  returns `EnsureImageResult` carrying `(LocalPath, Sha256)`.
- `src/DeCloud.NodeAgent.Infrastructure/Services/ImageManager.cs` —
  content-addressed cache, hash-on-the-fly download, lazy migration of
  legacy `*_nohash.*` files, no in-place cleaning, no upstream-checksum
  fallback.
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` —
  STEP 1 captures returned hash into `spec.BaseImageHash`; STEP 2.5 cleans
  the overlay.
- `src/DeCloud.NodeAgent/Services/CommandProcessorService.cs` — copy
  `req.BaseImageHash` into `vmSpec.BaseImageHash`.
- `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs` —
  `BuildHeartbeatPayload` populates `BaseImageUrl` and `BaseImageHash`
  from `VmSummary` (flat fields). `ImageId = v.Name` typo fixed.
- `src/DeCloud.NodeAgent.Infrastructure/Services/HeartbeatService.cs` —
  copies `vm.Spec.BaseImageUrl/Hash` into `VmSummary`.
- `src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitCleaner.cs` —
  no logic change; xmldoc updated to reflect new caller.

No SQLite schema migration required — `VmRecords.BaseImageHash` column
already existed at v8.

### Files modified in PR2 (registry consolidation)

**Orchestrator:**

- `src/Orchestrator/Models/Common.cs` — `VmImage` rewritten: added
  `ByArchitecture: Dictionary<string, BaseImageDescriptor>`, removed
  `Architecture` / `DownloadUrl` / `ChecksumSha256`; static
  `NormaliseArchTag` helper. `BaseImageDescriptor` record moved here.
- `src/Orchestrator/Models/BaseImageUrlResolver.cs` — **deleted entirely**.
- `src/Orchestrator/Persistence/DataStore.cs` — `SeedDefaultData` rewritten:
  five public entries (`ubuntu-24.04`, `ubuntu-22.04`, `debian-12`,
  `fedora-40`, `alpine-3.19`) with per-arch (URL, SHA256) under
  `ByArchitecture`. System VM aliases removed. All URLs pinned to dated
  upstream builds.
- `src/Orchestrator/Services/NodeService.cs` — `BuildSystemVmTemplate`
  signature takes `DataStore`; body uses `_dataStore.Images` lookup
  directly.
- `src/Orchestrator/Controllers/NodeSelfController.cs` — passes
  `_dataStore` to `BuildSystemVmTemplate`.
- `src/Orchestrator/Services/VmService.cs` — `GetImageDescriptor` becomes
  instance method, uses `_dataStore.Images` lookup directly.
- `src/Orchestrator/Models/RelayVmSpec.cs` — `ImageId = "debian-12"` in
  all spec variants (was `"debian-12-relay"`).
- `src/Orchestrator/Models/DhtVmSpec.cs` — `ImageId = "debian-12"` (was
  `"debian-12-dht"`).
- `src/Orchestrator/Models/BlockStoreVmSpec.cs` — `ImageId = "debian-12"`
  (was `"debian-12-blockstore"`).

**Node:**

- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/ArchitectureHelper.cs` —
  `ImageUrlsByArchitecture` dictionary and `ResolveImageUrl` method
  deleted. Other arch detection logic preserved.
- `src/DeCloud.NodeAgent/Services/CommandProcessorService.cs` —
  `ResolveImageUrlAsync` helper deleted; caller is now a four-line guard
  that throws if `req.BaseImageUrl` is empty.

Rollout: `db.images.drop()`, restart orchestrator. `SeedDefaultData`
repopulates with new schema.

---

## 8. Open Questions / Future Considerations

- **Catalog admin endpoint.** Today, updating an image's URL+hash is
  "edit seed, drop Mongo doc, restart." A `PUT /api/system/images/{id}`
  admin endpoint would let operators bump without a restart. Natural
  follow-up; out of scope for the consolidation PR.

- **Startup guard against floating URLs.** A five-line check in
  `DataStore.SeedDefaultData` that warns (or refuses to start) if any
  seed URL contains `/latest/` or `/current/`. Makes the URL pinning
  discipline executable rather than tribal. Recommended as a small
  follow-up to PR2.

- **Cache pinning by VM reference.** Today `PruneUnusedImagesAsync`
  deletes cached files by age. Consulting `vm.Spec.BaseImageHash`
  references before deletion would prevent the pathological case of a
  long-lived VM losing access to its base when the upstream URL
  eventually 404s years from now. Cheap; might land before Phase 3.

- **Catalog change audit trail.** Emitting an `OrchestratorEvent` on
  every catalog change (URL or hash bumped) provides a queryable history
  without modifying `VmImage`. Useful for compliance / forensics; trivial
  to add when needed.

- **Migration when no node has the bytes.** Phase 1 + 2 fail the
  migration. Phase 3's peer-fetch closes this gap. Until then, operators
  can pre-stage by issuing a dummy CreateVm to a target node to prime its
  cache, or rely on the cache-pinning improvement above to keep the bytes
  somewhere reachable.

- **Hash mismatch on heartbeat — warning vs. failure.** Currently a
  warning. Could be hardened to a security event in a future revision.
  Decoupled from the immediate fix.

- **Per-arch vs. arch-independent hash:** since the same imageId resolves
  to different URLs per arch (different upstream files), the hash table
  is naturally per-arch too. The `ByArchitecture` shape captures this
  directly. No ambiguity.
