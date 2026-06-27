# Minimal Alpine for System VMs — Findings & Proposal

**Status:** Findings (empirically grounded), ready for a build decision
**Scope:** System VM base image (relay, DHT, blockstore); secondary — Alpine as a supported tenant OS option
**Relation to existing docs:**
- Extends `OPTIMIZATION-SYSTEM-VM-IMAGE-SIZE.md` (which recommends Alpine, Option A) with the binary-compatibility evidence its open question was waiting on, and **corrects its "Current Architecture" section** against the shipped registry consolidation.
- Respects `BASE_IMAGE_DESIGN.md` (content addressing, PR1+PR2) — the switch is designed to live inside that model, not around it.
- References `RELEASE-PIPELINE.md` for the supply-chain implications of a custom image.

---

## 1. Summary

The goal — smaller, faster-booting, more restart-resilient system VMs — is best reached by changing the **guest image** (Debian → minimal Alpine), not the VMM. This keeps `LibvirtVmManager`, QEMU, the qcow2/overlay model, VNC, and the watchdog untouched, and it is reversible.

Two findings make this low-risk:

1. **The system-VM service binaries are statically linked Go.** Verified this session on a live node: `dht-node` and `blockstore-node` are `ELF … statically linked … Go BuildID=…`, no interpreter. They run on Alpine/musl with **zero change** — no rebuild, no `gcompat`. The binary-compatibility question that gated the Alpine decision is closed.
2. **System VMs are ephemeral (`ReplicationFactor = 0`).** Per `BASE_IMAGE_DESIGN.md` §4.3/§4.7, system VMs are never overlay-replicated or migrated, so the byte-level base-image consistency constraint that binds tenant VMs does not apply. Switching their base is safe by construction.

The only real work is the **relay**, because it is the one role with distro coupling: Python services plus kernel WireGuard wired as **systemd** units. On Alpine those become OpenRC services and `apt` becomes `apk`. DHT and BlockStore are nearly free.

**Recommendation:** switch system VMs to Alpine (stock image + adapted cloud-init first; custom-baked appliance later only if boot/footprint demands it). Separately, expose a curated minimal Alpine as a **supported tenant OS option** — it largely works already, with one clearly-documented caveat (musl).

---

## 2. What we confirmed this session

### 2.1 The binaries are static Go (the decisive check)

On node `srv022010`, against the artifact cache:

```
$ file /var/lib/decloud/vms/artifact-cache/<sha>
ELF 64-bit LSB executable, x86-64, version 1 (SYSV), statically linked,
Go BuildID=…, stripped
$ readelf -l <sha> | grep -i interpreter
  (no INTERP — static)
```

All inspected binaries: statically linked, no `PT_INTERP`, Go. A static Go binary carries its own runtime and makes raw syscalls — it is libc-agnostic and runs identically on Alpine, Debian, or any Linux kernel. **musl is a non-issue for these binaries.**

> Caveat: only `x86-64` binaries were cached on this (amd64) node — it never fetched the arm64 builds. The arm64 artifacts are almost certainly static Go too (same `DeCloud.Builds` pipeline), but confirm with the same `file` check against the arm64 release artifacts before relying on it. ARM support is production (dual-arch), so this matters.

### 2.2 The relay is the only distro-coupled role

Grounded in `SystemVmTemplateSeeder` + the relay cloud-init:

| Role | Workload | Distro coupling |
|---|---|---|
| **Relay** | kernel WireGuard (`wg-quick@wg-relay-server`) + **Python** (`api.py`, `http-proxy.py`) + bash | High — services are **systemd units**, packages via apt |
| **DHT** | static Go `dht-node` (libp2p Kademlia) | Low — binary is portable; needs an OpenRC service wrapper + wg-mesh enrollment |
| **BlockStore** | static Go `blockstore-node` (libp2p BitSwap) | Low — same as DHT |

### 2.3 System VMs are outside the replication/migration constraint

`BASE_IMAGE_DESIGN.md` is explicit: base images must be byte-identical across nodes because the lazysync overlay is a block-level diff against the backing chain. **But that only binds replicated/migrated VMs.** System VMs are `ReplicationFactor = 0` and ephemeral ("System migration: not applicable; system VMs are ephemeral", §4.3). They are recreated from template, not migrated. So changing their base image carries none of the Frankenstein-FS risk that motivated content addressing — they simply boot a different base next reconcile cycle.

---

## 3. Correction: the switch point moved (PR2 already shipped)

`OPTIMIZATION-SYSTEM-VM-IMAGE-SIZE.md` describes the pre-consolidation architecture — `BaseImageUrlResolver.cs` with three IDs (`debian-12-relay/dht/blockstore`). Per `BASE_IMAGE_DESIGN.md` §4.1 / §7 (PR2, shipped), that is no longer the code:

- `BaseImageUrlResolver.cs` — **deleted entirely**.
- `ArchitectureHelper.ImageUrlsByArchitecture` + `ResolveImageUrl` — **deleted**.
- One authoritative registry: `DataStore.Images`, each entry carrying `ByArchitecture → (Url, Sha256)`.
- `SystemVmRoleMap.ToBaseImageId(role)` returns **`"debian-12"`** for all three roles; the role specs (`RelayVmSpec`/`DhtVmSpec`/`BlockStoreVmSpec`) were set to `ImageId = "debian-12"`.
- **`alpine-3.19` already exists** as a registered public `VmImage` (~50 MB, NoCloud cloud-init, amd64 bios + arm64 uefi variants).

So the system-VM image switch is **smaller** than Option A implied: there is no resolver to edit. It is `ToBaseImageId` (and the three spec `ImageId`s) flipping from `"debian-12"` to an Alpine image id, plus the cloud-init adaptation. The catalog entry to point at already exists.

---

## 4. Why the guest lever (Alpine), not the VMM lever (Cloud Hypervisor)

We evaluated Cloud Hypervisor first. Summary of why Alpine wins for *this* goal:

| Dimension | Minimal Alpine (change guest) | Cloud Hypervisor (change VMM) |
|---|---|---|
| Footprint | **Dominant win** — guest OS is the weight; ~427 MB→~50 MB image, RSS ~200 MB→~40 MB | Smaller win — only VMM overhead (~5 MB vs ~30 MB) |
| Boot/restart | Faster guest init (OpenRC, fewer services) | Faster kernel load + unique snapshot/restore |
| `LibvirtVmManager` | **Untouched** | Near-total domain-XML rewrite |
| qcow2/overlay | Unchanged | Lost (CH wants raw) — but moot for RF=0 system VMs |
| VNC / i6300esb watchdog | Kept | Lost |
| Runtime count | One (QEMU) | **Two** (CH for system, QEMU for tenant) |
| Reversibility | Flip `ToBaseImageId` back | Architectural |

The footprint of a system VM is dominated by the guest OS, not the VMM, so the guest lever moves the bigger number — and it does so without a second runtime in the node agent. (Full surface map of a CH swap is in the conversation record; not repeated here.)

---

## 5. The system-VM switch — concrete

### 5.1 Two flavors

- **Flavor A — stock Alpine + adapted cloud-init.** Keep the "generic base + cloud-init renders the role" model; point system VMs at `alpine-3.19`; produce Alpine variants of the cloud-init (OpenRC, apk). Lowest effort, keeps flexibility. **Recommended starting point.**
- **Flavor B — custom-baked appliance per role.** Bake WireGuard + the role binary + an OpenRC service into a tiny rootfs that boots straight into the service with little/no cloud-init. Smallest, fastest, most deterministic boot; but moves role identity *into* the image (more images to build/sign/version) and needs a build pipeline (see §6). Graduate to this only if Flavor A's boot/footprint isn't enough.

### 5.2 Touchpoints (Flavor A)

| Component | Change | Repo |
|---|---|---|
| `SystemVmRoleMap.ToBaseImageId` | return the Alpine image id for all three roles | Orchestrator |
| `RelayVmSpec` / `DhtVmSpec` / `BlockStoreVmSpec` | `ImageId` → Alpine id (verify whether these are still authoritative post-PR2 or `ToBaseImageId` is the single point) | Orchestrator |
| `DataStore.Images` | `alpine-3.19` already present; consider a curated `alpine-system` id with a **durable URL + populated SHA256** (see §6, §7) | Orchestrator |
| Relay cloud-init (`base-system.yaml` + `relay/cloud-init.yaml`) | systemd units → OpenRC init scripts; `apt` → `apk`; verify Python deps install on musl | DeCloud.Builds |
| DHT/BlockStore cloud-init (`base-system-mesh.yaml` + role) | OpenRC service wrapper for the static binary; wg-mesh enrollment under OpenRC; `apk` | DeCloud.Builds |

Note what is **not** here: `LibvirtVmManager`, `ImageManager`, the domain-XML generator, the qcow2/overlay pipeline. The change lives in the orchestrator's image resolution and the DeCloud.Builds cloud-init — never in the node-agent VM path.

### 5.3 The real work is the relay's init model

DHT/BlockStore reduce to "static binary + OpenRC wrapper + wg-mesh." The relay carries the weight: its cloud-init writes `decloud-relay-api.service`, `-http-proxy.service`, `-nat-callback.service`, plus the env-watcher systemd service+timer — each becomes an OpenRC service, and the Python stack must be confirmed on Alpine's musl Python.

### 5.4 Verify list (system VMs)

- [ ] arm64 `dht-node` / `blockstore-node` are static Go (same `file` check on the arm64 artifacts).
- [ ] Relay Python deps (`api.py`, `http-proxy.py`) install and run under Alpine musl Python (`apk add … && pip install …` smoke test in `alpine:3.19`).
- [ ] Kernel WireGuard + `wg-quick` come up on Alpine `linux-virt` (relay server + wg-mesh).
- [ ] **i6300esb watchdog driver** is present in the Alpine guest kernel — system VMs rely on the emulated watchdog (`<watchdog model='i6300esb' action='reset'/>`) for auto-reset; the host side is unchanged, but the *guest* needs the driver to pet `/dev/watchdog`.
- [ ] Alpine version audit: `alpine-3.19` is flagged "audit pending" in `BASE_IMAGE_DESIGN.md` §4.2 and is aging — pin a current Alpine with a durable point-versioned URL and a populated SHA256.

---

## 6. Custom appliance (Flavor B): build & supply-chain implications

A custom image is **first-class under the existing model** — `BASE_IMAGE_DESIGN.md` content addressing means you register it in `DataStore.Images` with its SHA256 and the node cache verifies it like any other base. No new runtime mechanism.

But two costs, both grounded in `RELEASE-PIPELINE.md`'s posture:

1. **You own the build + security cadence.** Stock Alpine is upstream-maintained; a custom appliance means you rebuild for CVEs. That argues for Flavor A until footprint genuinely forces Flavor B.
2. **It should be signed like everything else.** The platform's supply chain is tight — signed git tags, cosign keyless, manifest SHA-256 (NodeAgent pipeline), cosign-verified SHA-256 for the `DeCloud.Builds` binaries. A custom base image is a new trusted artifact and should ride the same rail: built by a signed pipeline, content-addressed, SHA-256 recorded in `DataStore.Images`. **Security-first: do not introduce an unsigned, hand-built base image into the trust chain.** The blockstore-mirror idea (`BASE_IMAGE_DESIGN.md` §6, Phase 3) is the natural home for self-hosted base images if/when you own distribution.

---

## 7. Alpine as a supported tenant OS option

This is mostly **already true** and cheap to make first-class.

### 7.1 It works within the existing framework today

`alpine-3.19` is a registered public `VmImage`. Content addressing (`BASE_IMAGE_DESIGN.md`) handles arbitrary bases per-VM: a tenant Alpine VM pins its `(Url, Sha256)` on its own spec at first deploy and migrates/replicates consistently — the same machinery that already serves Debian/Ubuntu/Fedora tenants. **No new node-agent or migration code is needed** to let tenants run Alpine. "Supported" is mostly curation, not construction.

### 7.2 What "supported minimal build" adds

- **Durable pin + strict hash.** Audit the URL to a current Alpine durable form and populate the SHA256 (strict-mode verification per §4.2/§4.4). This is the difference between "an image in the list" and "a supported image."
- **A clear product slot.** Surface it as the small-footprint / fast-boot option (CI runners, stateless single-binary services, edge, scale-to-many).

### 7.3 The one caveat to document loudly: musl

This is the tenant-facing flip side of §2.1. Tenant workloads are arbitrary:

- **Fine:** static Go/Rust, most interpreted stacks (with musl-built wheels), anything containerish.
- **Needs care / may not run unmodified:** glibc-dynamic binaries, .NET (needs the musl runtime build), native Python/Node wheels without musl variants. `gcompat` rescues many glibc binaries but not all.

So Alpine must be offered as a **knowingly-chosen** option — its `VmImage.Description` and any Alpine templates should state the musl constraint plainly. Pairing it with Alpine-aware templates (or raw/custom cloud-init) avoids the trap of a Debian-assuming template (`apt`, systemd units) failing on it.

### 7.4 Upside

- **Security:** a minimal image is a smaller attack surface (fewer packages, no unused services) — aligns with the platform's security-first posture.
- **Density / economics:** lighter VMs mean more workloads per node → more node-operator earnings and more marketplace supply. This is the **"simple compute blocks"** primitive of the Minecraft vision expressed as an OS choice — a cheap, fast, composable block alongside the full-OS blocks. Competing on capability and accessibility, not on stripping the full-OS options tenants also want.

---

## 8. Recommendation (phased)

1. **System VMs → stock Alpine (Flavor A).** Start with the relay's systemd→OpenRC cloud-init rewrite (the bulk of the work); DHT/BlockStore follow nearly for free given the static-Go finding. Reversible by flipping `ToBaseImageId` back. Net win: ~427 MB→~50 MB base, ~7 min→~30 s first-node setup, faster system-VM boot — directly helping the "system VM resilience" item still open before MVP.
2. **Expose curated minimal Alpine as a tenant OS option.** Durable URL audit + populated SHA256 + the musl caveat in the image description. Low effort; mostly curation on top of an image that already exists.
3. **(Optional) Custom appliance (Flavor B)** for system VMs, *only if* Flavor A's boot/footprint is insufficient — and only via a signed, content-addressed build pipeline (§6).

---

## 9. Open questions / verify list (consolidated)

- arm64 system-VM binaries are static Go (confirm).
- Relay Python stack on musl; kernel WireGuard + wg-quick on Alpine `linux-virt`.
- i6300esb watchdog driver in the Alpine guest kernel.
- Alpine version/URL audit (3.19 is aging; pin current + SHA256) — coordinate with the `BASE_IMAGE_DESIGN.md` §8 "audit pending" follow-up so Debian/Fedora/Alpine are fixed together.
- Whether `ToBaseImageId` alone is authoritative for system-VM image resolution post-PR2, or the role-spec `ImageId` fields are also consulted (flip both to be safe; verify against current `NodeService.BuildSystemVmTemplate`).
- Tenant-template strategy for Alpine (Alpine-aware templates vs. documented musl constraint on generic ones).

---

## 10. Vision alignment

A minimal Alpine block is the "simple primitives → complex outcomes" principle applied to the OS layer: a cheaper, faster, smaller compute block that raises node density (more supply, more operator earnings), shrinks first-install friction (~7 min → ~30 s), and adds an accessible option to the palette without removing the full-OS blocks. For the system VMs specifically, it makes the platform's own infrastructure lighter and quicker to recover — reinforcing the system-VM resilience work that remains before MVP.
