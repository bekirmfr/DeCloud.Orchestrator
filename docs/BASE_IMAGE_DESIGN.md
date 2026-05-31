# Base Image Content Addressing — Design & Implementation

**Status:** Implementation In Progress
**Scope:** Image management, VM creation, migration, cleaning placement
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

## 2. Root Cause: Three Compounding Gaps

### 2.1 URL-keyed cache (not content-keyed)

`ImageManager.GetCacheFileName` derives the cache filename from
`SHA256(url)[..16]`. Two different qcow2 files downloaded from the same URL
at different times land at the same path. Two nodes that downloaded from the
same URL at different times can have different bytes under the same filename
and never know.

### 2.2 `BaseImageHash` field exists but is never populated

`VmSpec.BaseImageHash`, `SystemVmTemplate.BaseImageHash`, and
`VmImage.ChecksumSha256` are all declared and persisted, but no code path
writes a non-empty value to them. `BuildSystemVmTemplate` literally hardcodes
`BaseImageHash = string.Empty`. The `_nohash` suffix in the cached filename is
the literal trace of this.

### 2.3 Migration `CreateVmPayload` omits `BaseImageUrl` entirely

The migration dispatch in `BackgroundServices.cs` builds a `CreateVmPayload`
that jumps from `ContainerImage` straight to `SshPublicKey`, never setting
`BaseImageUrl`. The receiving node falls through to
`ArchitectureHelper.ResolveImageUrl` which defaults to `ubuntu-22.04` when no
imageId is provided. The migration silently changes which base image is used.

### 2.4 In-place cleaning breaks identity

`ImageManager.EnsureImageAvailableAsync` calls `CloudInitCleaner.CleanAsync`
on the cached file directly. The cleaner uses virt-customize / guestmount /
qemu-nbd to mutate `/var/lib/cloud`, `/etc/machine-id`, etc. inside the qcow2.
Post-cleaning bytes vary per node (mtimes, inode allocation order, ext4
journal state) — even if every node downloaded byte-identical upstream bytes,
the cached files on different nodes would not be byte-identical after
cleaning. The hash drifts away from any stable identity.

---

## 3. Why Content Addressing Is Necessary, Not Optional

The lazysync overlay is a **block-level** diff. `LazysyncDaemon` exports the
depth-0 extents (clusters owned by the overlay, not the backing chain) as
1 MiB chunks keyed by byte offset in the virtual disk address space. The
overlay is meaningless in isolation — its semantics depend entirely on the
specific bytes at the offsets it does not touch, which come from the backing
chain.

That dependency is byte-level, not file-level. The overlay does not know which
files were modified; it knows which raw blocks were modified. When the
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

### 4.1 Orchestrator is authoritative for (URL, hash) per image

For every supported base image, the orchestrator carries both the download
URL and the SHA256 of the file the system expects at that URL. Two nodes
that resolve the same imageId receive the same URL and the same hash.

`BaseImageUrlResolver.Resolve(imageId, arch)` now returns a
`BaseImageDescriptor` record with both fields. The static table is the
source of truth; entries are reviewed at PR time, exactly as today's URLs
are. Hashes are populated alongside URLs whenever the table is updated
(rolled in over time; see Phase 2 in §6).

The `VmImage` catalog (`DataStore.Images`) stays as it is — display
metadata, not deployment-time resolution. The resolver is the deployment
boundary, the catalog is the user-facing boundary. The boundary the
original author chose is preserved.

### 4.2 Hash propagates with every CreateVm dispatch

`CreateVmPayload` gains a `BaseImageHash` field. Every dispatch carries it:

- Tenant fresh: orchestrator reads from the resolver descriptor.
- System fresh: same.
- Tenant migration: orchestrator reads from `fresh.Spec.BaseImageHash`
  (which was populated on first deploy and round-tripped via heartbeat).
- System migration: not applicable (system VMs are ephemeral).

The migration payload also gains `BaseImageUrl = fresh.Spec.BaseImageUrl` —
a pre-existing bug that this work fixes incidentally.

### 4.3 Node-side cache is content-addressed

`ImageManager.EnsureImageAvailableAsync` becomes:

1. **If `expectedHash` is non-empty:** look up `{cache}/{sha256}.img`. If
   present and the file's SHA256 matches, return. If present and mismatches,
   delete and re-download. If missing, download with hash-on-the-fly via
   `CryptoStream`; the temp file is renamed to `{sha256}.img` only after
   verification passes. Same pattern as `ArtifactCacheService`.

2. **If `expectedHash` is empty:** download to a temp path, compute SHA256,
   rename to `{sha256}.img`. Return the path and the computed hash so the
   caller can record it back into `VmSpec.BaseImageHash`.

The legacy `*_nohash.*` files are tolerated: on first cache lookup miss,
the manager scans the cache directory, hashes any `_nohash` files
encountered, and renames them to `<sha256>.img` in place. Subsequent uses
find them content-addressed. No data loss; no flag day.

`TryVerifyAgainstUpstreamChecksumAsync` (the SHA256SUMS-from-the-internet
fallback) is removed. It was a band-aid that masked the deeper issue and
is no longer needed once hashes are authoritative.

### 4.4 Cleaning moves from cache to overlay

`ImageManager.EnsureImageAvailableAsync` no longer invokes
`CloudInitCleaner.CleanAsync` on the cached file. The cached file is the
exact bytes that hash to its filename — immutable.

`LibvirtVmManager.CreateVmAsync` invokes cleaning on the overlay disk after
STEP 2 (`CreateOverlayDiskAsync` for fresh, `ReconstructOverlayAsync` +
`RunFsckOnOverlayAsync` for migration) and before STEP 3 (cloud-init ISO).
The overlay is per-VM; cleaning it is a one-time CoW write of about 100 KB
that strips the machine-id and cloud-init markers carried over from the
base or from a migrated source.

`CloudInitCleaner` itself needs no logic change — it takes a qcow2 path and
operates on whatever filesystem it sees through the chain. The xmldoc is
updated to reflect the new caller.

### 4.5 Hash round-trips via heartbeat

`HeartbeatVmInfo` gains `BaseImageUrl` and `BaseImageHash` properties. On
every heartbeat, the node reports the values currently in `VmSpec`. The
orchestrator's `SyncVmStateFromHeartbeatAsync`:

- If `reported.BaseImageHash` is empty: no-op.
- If `vm.Spec.BaseImageHash` is empty and `reported.BaseImageHash` is
  non-empty: adopt (first-deploy hash discovery).
- If both are non-empty and differ: log a warning. Either the cache was
  tampered with on the node, or the orchestrator's expected hash has been
  changed mid-flight. Operator-visible signal; no automatic remediation.

A small bug — `ImageId = v.Name` in `OrchestratorClient.BuildHeartbeatPayload`
— is fixed in passing (it was reporting the VM's display name as the imageId).

### 4.6 Behaviour during the rollout

In PR1 (this work), the resolver's hash table is wired structurally but
the hash strings are initially empty. Behaviour during this period:

- Orchestrator sends `BaseImageHash = ""` for all CreateVm dispatches.
- Node sees empty hash → permissive mode: downloads, computes, records the
  hash into the spec, stores the file at `<sha256>.img`.
- Heartbeat reports the computed hash back to the orchestrator.
- Orchestrator stores it in `vm.Spec.BaseImageHash`.
- Migration of that VM later carries the recorded hash. Target enforces.

So the system becomes safe as soon as PR1 ships, even before any hashes are
populated in the resolver. Once the resolver hashes are populated (Phase 2
in §6), strict verification kicks in for the initial download too.

---

## 5. What This Does Not Do

- **No peer-to-peer base image distribution.** When a target node has not
  cached the required hash and the URL has drifted, the migration fails
  cleanly with a clear error. Operator can pre-stage the file, wait, or
  redeploy. Peer-fetch via the blockstore mesh is a future option (§6
  Phase 3) but not part of this work.
- **No retroactive hash population for existing VMs.** VMs deployed before
  PR1 ships have empty `BaseImageHash` in their spec. On their next
  heartbeat after the node-agent upgrade, the node computes the hash from
  the cached file and round-trips it. If the cache was tampered with on
  that node, the wrong hash gets recorded — but this is no worse than
  today, where there is no hash at all.
- **No compose-time refusal in `qemu-img rebase`.** The `-u` flag (unsafe
  rebase) remains. Strict verification at the ImageManager boundary makes
  this safe — by the time we rebase, we already know the bytes match.
  A future PR could add a redundant qemu-side check; out of scope here.
- **No file-level overlay portability.** The block-level overlay design is
  preserved. Cross-base compatibility remains impossible by construction;
  the fix is to enforce the same base, not to relax the overlay semantics.

---

## 6. Rollout Phases

### Phase 1 — This PR

Implements §4.1 through §4.5. Hash table in `BaseImageUrlResolver` is wired
but unpopulated (empty strings). System is safe in permissive-then-strict
mode: first deploy on a fresh node downloads-and-records, subsequent
migrations enforce.

### Phase 2 — Populate hash table (follow-up PR)

For each entry in `BaseImageUrlResolver.UrlsByArchThenImageId`, add the
SHA256 of the file currently served at that URL. Switch URLs from `latest`
and `current` to versioned URLs that won't drift. A `compute-base-image-
hashes.sh` script (parallel to `compute-artifact-constants.sh`) downloads
each URL once and emits a constants file; the maintainer pastes the
constants into the resolver and bumps any affected template revisions.

After Phase 2, strict verification kicks in on every CreateVm, including
the initial deploy.

### Phase 3 — Peer-fetch via blockstore mesh (deferred)

When a target node needs a base image hash it doesn't have and the URL is
unreachable or has drifted, fetch the bytes from a peer that does. The
blockstore mesh already moves content-addressed blobs between nodes; base
images would ride the same rail (chunked, CIDed, bitswap-distributed).
Out of scope here; deferred until URL drift becomes a felt operational
problem.

---

## 7. Touchpoints

Files modified in PR1:

**Shared:**

- `src/DeCloud.Shared/Contracts/CreateVmPayload.cs` — add `BaseImageHash` field.
- `src/DeCloud.Shared/Contracts/NodeHeartbeat.cs` — add `BaseImageUrl` and
  `BaseImageHash` to `HeartbeatVmInfo`.

**Orchestrator:**

- `src/Orchestrator/Models/BaseImageUrlResolver.cs` — add
  `BaseImageDescriptor` record, change `Resolve` signature, add hash column
  (initially empty).
- `src/Orchestrator/Services/NodeService.cs` — `BuildSystemVmTemplate` reads
  hash from descriptor; `SyncVmStateFromHeartbeatAsync` propagates
  `reported.BaseImageHash` into `vm.Spec.BaseImageHash`.
- `src/Orchestrator/Services/VmService.cs` — `TryScheduleVmAsync` reads URL
  and hash via the resolver, sets both on `CreateVmPayload`. Existing
  `GetImageUrl` private helper retired.
- `src/Orchestrator/Background/BackgroundServices.cs` — migration dispatch
  sets `BaseImageUrl = fresh.Spec.BaseImageUrl` and
  `BaseImageHash = fresh.Spec.BaseImageHash` on the payload.

**Node:**

- `src/DeCloud.NodeAgent.Core/Interfaces/IServices.cs` — `IImageManager`:
  `EnsureImageAvailableAsync` returns `EnsureImageResult` carrying
  `(Path, Sha256)` instead of just path.
- `src/DeCloud.NodeAgent.Infrastructure/Services/ImageManager.cs` —
  content-addressed cache, hash-on-the-fly download, lazy migration of
  legacy `*_nohash.*` files, no in-place cleaning, no upstream-checksum
  fallback.
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` —
  STEP 1 captures returned hash into `spec.BaseImageHash`; cleaning invoked
  on the overlay after STEP 2.
- `src/DeCloud.NodeAgent/Services/CommandProcessorService.cs` — copy
  `req.BaseImageHash` into `vmSpec.BaseImageHash`.
- `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs` —
  `BuildHeartbeatPayload` populates `BaseImageUrl` and `BaseImageHash` from
  `v.Spec`. The `ImageId = v.Name` typo is fixed (it now reports the
  imageId, not the VM name).
- `src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitCleaner.cs` — no
  logic change; xmldoc updated to reflect new caller.

No schema migration required — `VmRecords.BaseImageHash` column already
exists at v8.

---

## 8. Open Questions / Future Considerations

- **Per-arch hash entries vs. arch-independent hash:** since the same
  imageId resolves to different URLs per arch (different upstream files),
  the hash table is naturally per-arch too. No question; just noting.

- **Hash table source-control vs. external:** the table is committed to
  source code (same as the URL table, same as the cosign-verified binary
  hashes). Some discussion of moving image metadata to MongoDB exists for
  user-supplied images; if that lands, the resolver would gain a
  database-backed fallback. Out of scope here.

- **Migration when target has only a stale base:** Phase 1 fails the
  migration. Phase 3's peer-fetch path closes this gap. Until then,
  operators can pre-stage by issuing a dummy CreateVm to a target node to
  prime its cache.

- **Hash mismatch on heartbeat — warning vs. failure:** currently a
  warning. Could be hardened to a security event in a future revision.
  Decoupled from the immediate fix.
