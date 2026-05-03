# Implementation Plan — VM Cloud-init Pipeline Migration

**Companion document to:** `UNIFIED_CLOUDINIT_PIPELINE.md` (the design contract).
**Purpose:** Our shared workbench. Tracks current state, captures decisions made during implementation, and breaks the design into commit-sized tasks.

---

## How to use this document

**Each working session:**
1. Read §1 (Current state) — know where we are.
2. Read §2 (Decision log) — recall any non-obvious decisions already made.
3. Pick the next unblocked task in the active phase.
4. Work the task. Update its status in place.
5. If a decision comes up that wasn't in the design doc, append to §2.
6. If a task reveals a gap in the design doc, log it in §2 — do not silently change the design.

**Status legend:**
- `[ ]` not started
- `[~]` in progress
- `[x]` done
- `[!]` blocked (note why in the task's Notes line)
- `[?]` needs decision (note in §2)

**Task IDs** are stable across the document. Reference them in commit messages
(e.g., `P1.3: implement IVariableResolver registry`).

**Cross-references** to the design doc use section numbers, e.g., "see design §3.2".

---

## §1. Current state

**Active phase:** Phase 0.

**Last session:** 2026-05-03 — P0.1 through P0.7 complete. Tenant-side artifact-constants script in place (sibling to system-vms version). Three Phase 0 tasks remain (P0.8 watcher, P0.9 identity-fetch standardization, P0.10 deferred).

**Next task:** P0.8 — create `decloud-env-watcher.sh` and systemd units in `system-vms/shared/assets/`.

**Artifact declarations recorded for Phase 2 + Phase 3 seeders** (canonical URLs and SHA256, all on release `binaries/v1.0.0` of `bekirmfr/DeCloud.Builds`):

System VM binaries (CI-built):
- `dht-node` / amd64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/dht-node-amd64` / sha256 `4a7c213bef29b53bc5a3e253e5796628330ffdb743105d468aaab4ad228efdac`
- `dht-node` / arm64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/dht-node-arm64` / sha256 `bee3955aa9b8630f4bde1c67879e9bb998e23fcb91b0d0975e49580c3251082d`
- `blockstore-node` / amd64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/blockstore-node-amd64` / sha256 `d64663e749ef89965d7d03e0faf908bc89d75e01ba860c2f4f3d080e36f60f1b`
- `blockstore-node` / arm64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/blockstore-node-arm64` / sha256 `450c3cf4c6383a146e8431242f0850a77b37e9a94c7adc0d51e63738fe228895`

Tenant VM binaries (manually uploaded; see P0.10):
- `decloud-agent` / amd64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/decloud-agent-amd64` / sha256 `530c33c349f4c55d17a4f7e6a328d60da09b1257c1ffd2c54c4efd9f3e3962a2`
- `decloud-agent` / arm64 / `https://github.com/bekirmfr/DeCloud.Builds/releases/download/binaries%2Fv1.0.0/decloud-agent-arm64` / sha256 `349ad8dd16c837a868d8385a693a8ee376f72948660f823e8e70f150bda142ac`

**Blockers:** None.

**Owner:** Solo developer (also the binary owner for relay, DHT, BlockStore — PF.1 self-confirmed).

---

## §2. Decision log

Append-only. Each entry: date, task ID it came up under, decision made, rationale.

- **2026-05-03 / kickoff** — Solo developer; PF.1 deferred (will revisit before Phase 3 relay cutover, since the developer is also the binary owner). Phases 0–2 do not depend on scope-policy confirmation, so we proceed.
- **2026-05-03 / PF.2** — Docs land in `DeCloud.Orchestrator/docs/`. Superseded drafts go to `docs/archive/` with a pointer README.
- **2026-05-03 / P0.1** — Welcome-server content (`welcome.service` unit, `welcome-server.py`, `index.html`) lives in the **general role layer**, not in `base-tenant.yaml`. Deviation from design doc §2.2, which lists `welcome.service` under base-tenant write_files. **Rationale:** `welcome.service` references `/usr/local/bin/welcome-server.py`, which is general-VM-specific. Placing the unit in base while the script is role-only would cause `systemd` failure churn (`Restart=always`) on marketplace VMs that don't add the script. Putting all welcome content in the role layer keeps marketplace VMs cleanly free of welcome state. If a future requirement emerges for marketplace VMs to have a default landing page, that's a separate decision and a separate spec. *Action:* design doc §2.2 will be updated to reflect this when we get to a doc-cleanup pass; not blocking.
- **2026-05-03 / P0.1** — Decloud-agent transitions from inline-base64 (`__ATTESTATION_AGENT_BASE64__` in a write_files entry) to the artifact pipeline (fetched in runcmd via `${ARTIFACT_URL:decloud-agent}` with SHA256 verify). The architecture-correct binary is selected by the orchestrator's `ArtifactUrlResolver` from two declared artifacts (`decloud-agent` with `Architecture=amd64` and `Architecture=arm64`) — same pattern DHT and BlockStore already use. The actual binary upload to GitHub Releases lands in P0.6.
- **2026-05-03 / P0.2** — *[SUPERSEDED by entry below — wg-mesh content moved to role layers when relay YAML revealed structural divergence.]* `/etc/decloud/wg-mesh.env` lives in **base-system**, not in role layers (deviation from the source DHT/BlockStore YAMLs which each duplicate it). The two role-specific values inside the file (`WG_DESCRIPTION`, `WG_ROLE`) become declared `Static` variables on each role's template, with concrete `DefaultValue` set in the role-specific seeder (e.g. `WG_DESCRIPTION="DHT-__VM_ID__"`, `DECLOUD_ROLE="dht"`). Eliminates duplication of a 9-line file across three role YAMLs. Two new declared statics: `WG_DESCRIPTION` and `DECLOUD_ROLE` (the latter is also referenced by the role-layer `wg-config-fetch.sh` invocation block in runcmd).
- **2026-05-03 / P0.2** — *[SUPERSEDED — moved to role layers per relay-revision entry.]* `wg-mesh-watchdog.{sh,service,timer}` and the `wg-quick@wg-mesh` override go in base-system. These are byte-identical across DHT and BlockStore today; assumed identical for relay (will verify in P0.3c).
- **2026-05-03 / P0.2** — `wg-config-fetch.sh` invocation **stays in role layers**, not base. Reasoning: the script itself is role-fetched via `${ARTIFACT_URL:wg-config-fetch}` (artifact list is role-specific), so it isn't installed by the time base-runcmd executes. TemplateComposer concatenates base runcmd before role runcmd, so the order is correct as long as the invocation is in the role's runcmd block. A future cleanup could promote `wg-config-fetch.sh` to a shared artifact and move the invocation to base, but that's out of scope. *[Still applies — not affected by relay revision.]*
- **2026-05-03 / P0.2 (revised after relay YAML received)** — **base-system.yaml revised down.** Original draft included wg-mesh content (`/etc/decloud/wg-mesh.env`, `wg-mesh-watchdog.{sh,service,timer}`, `wg-quick@wg-mesh` override). Relay invalidated this — relay does NOT participate in the mesh; it IS the mesh hub, using `wg-quick@wg-relay-server` instead. The wg-mesh content moves to **DHT and BlockStore role layers** (kept as duplicated residue when those YAMLs are stripped in P0.3a/P0.3b). Acceptable duplication for two roles; if a fourth mesh-participant role appears, that's when we promote a `mesh-participant` mixin layer or a second base. The earlier-decided `WG_DESCRIPTION` and `DECLOUD_ROLE` placeholders also move to mesh-participant role layers — they're irrelevant to relay.
- **2026-05-03 / P0.2 (revised)** — Relay uses `hostname: __VM_NAME__` in its source YAML; DHT/BlockStore use `hostname: __HOSTNAME__`. **Standardizing on `__HOSTNAME__` everywhere.** `HostnameResolver` returns the VM's name in either case. Relay strip in P0.3c will replace `__VM_NAME__` with `__HOSTNAME__` for the hostname field (other uses of `__VM_NAME__` in relay — final_message, log lines, metadata JSON — keep `__VM_NAME__` since they semantically refer to the VM name as identity, not as hostname).
- **2026-05-03 / P0.2 (revised)** — `fail2ban` package stays in relay role layer, NOT base. Relay is the only system VM with public SSH exposure (the others are reachable only via wg-mesh). Both `fail2ban` package and the `/etc/fail2ban/jail.d/relay.conf` write_files entry stay in relay role.
- **2026-05-03 / P0.2 (revised)** — **Design doc §5 Phase 0.2 will need a correction** at the next doc-cleanup pass: it lists wg-mesh content under base-system, which is wrong. Not blocking; logging here.
- **2026-05-03 / P0.2** — Identity-fetch (curl `/api/obligations/{role}/state`) is **role-specific**, not base. DHT has it (Ed25519 keypair → `/var/lib/decloud-dht/identity.key`). BlockStore does not (binary self-loads via `loadOrCreateIdentity()` against NodeAgent obligation state at runtime). Relay has it (WireGuard private key + subnet → sed-rewrite of `wg-relay-server.conf`). All three roles handle this differently in their runcmd; nothing generalizes to base.
- **2026-05-03 / P0.2b** — **Two parallel system-VM base primitives**, one per composition target. `base-system.yaml` for non-mesh roles (relay only, today). `base-system-mesh.yaml` for mesh-participant roles (DHT, BlockStore). Each role composes against exactly one base. Rejected alternatives: (a) put mesh content in DHT/BlockStore role layers, duplicating ~70 lines across two files — drift surface for evolving content like the watchdog script; (b) extend TemplateComposer to N-layer mixin composition, requiring API change for one current use case — premature abstraction. The accepted approach: ~25 lines of universal content (scalars, bootcmd, qemu-guest-agent runcmd, package list) duplicated across the two bases. **Discipline note: any future change to universal system-VM behavior must land in both `base-system.yaml` and `base-system-mesh.yaml`.** Stable content, low drift risk; code review and per-role testbed runs catch drift.
- **2026-05-03 / P0.2b** — TemplateComposer (P1.3) stays two-layer. Seeder picks the appropriate base per role (relay → base-system; DHT/BlockStore → base-system-mesh). No N-layer extension needed.
- **2026-05-03 / P0.3a** — DHT role layer composes with `base-system-mesh.yaml` (mesh-participant base). Updated task entry's acceptance reference accordingly (was `base-system.yaml`). Same correction needed in P0.3b (BlockStore). Relay (P0.3c) stays composing with `base-system.yaml` as written.
- **2026-05-03 / P0.3a** — `dht-metadata.json` `version` field bumped to "8.0" to match the cloud-init file's version comment. Cosmetic; doesn't affect runtime behaviour. Establishes pattern: when role YAML version bumps, embedded version strings track.
- **2026-05-03 / P0.3b** — BlockStore strip mirrors DHT strip. Same set of scalars/bootcmd/packages/mesh-files removed; role layer keeps blockstore-specific write_files, artifact-fetch, wg-config-fetch invocation, service starts. **No identity-fetch added at this stage** — preserves v3.0's "binary self-loads" pattern. P0.9 changes that. blockstore-metadata.json version bumped 3.0 → 4.0.
- **2026-05-03 / P0.3c** — Relay strip differs from DHT/BlockStore: target is `base-system.yaml` (non-mesh), and `fail2ban` package is preserved in role (relay-only). Hostname placeholder normalized: source v5.1 had `hostname: __VM_NAME__`; role layer no longer declares `hostname`, so composition uses base's `__HOSTNAME__`. Both resolve to the same value at render time; only the placeholder name differs. relay-metadata.json version bumped 5.1 → 6.0. The complex WG-config-rewrite-from-obligation-state runcmd is preserved verbatim with a NOTE comment flagging P0.9 as the task that replaces it with the standardized identity-fetch pattern.
- **2026-05-03 / docs** — Renamed both documents to better reflect their unified scope (no longer system-VM-only): `SYSTEM_VM_RENDERING_HANDOUT.md` → `UNIFIED_CLOUDINIT_PIPELINE.md`; `IMPLEMENTATION_PLAN.md` → `UNIFIED_CLOUDINIT_PIPELINE_IMPLEMENTATION_PLAN.md`. Internal references in this plan updated accordingly. The design doc has no self-references.
- **2026-05-03 / P0.6** — `release-binaries.yml` workflow renamed "Release system VM binaries" → "Release VM binaries" (drops "system" — covers tenant too). Single trigger `binaries/v*` continues to cut all binaries together; rejected splitting into separate `release-tenant-binaries.yml` because release cadences are aligned in practice (binary updates ship rarely; when they ship, both system and tenant binaries change together). If decoupled cadences emerge later, splitting is cheap.
- **2026-05-03 / P0.6** — Existing manual `decloud-agent-{amd64,arm64}.b64` binaries in `NodeAgent/CloudInit/Templates/` treated as **deprecated and unverifiable**. They were built manually at unknown commit and cannot be reproduced. CI binaries from the next `binaries/v*` tag become the source of truth. The .b64 files remain in-tree for legacy-path compatibility until Phase 4.4 deletes them; new deployments via the migrated pipeline use the CI-built binaries via `${ARTIFACT_URL:decloud-agent}`.
- **2026-05-03 / P0.6 (revised)** — **Source for `decloud-agent` is missing**, not just temporarily unavailable. Initial draft of P0.6 added a `build-attestation-agent` CI job mirroring `build-dht` and renamed the workflow to "Release VM binaries". Reverted both — the workflow can't build source that doesn't exist, and the broader name overstates what the workflow does. Final state: workflow stays "Release system VM binaries" with only `build-dht` and `build-blockstore`; comment block at the top of the workflow file documents that `decloud-agent` binaries are uploaded manually until P0.10; release notes include a "Manually-uploaded assets" section listing the four expected files. Existing `.b64` binaries get decoded and uploaded by hand to the next `binaries/v*` release. The artifact pipeline can't tell a manual upload from a CI build — same SHA256, same fetch shape — so this preserves the migration's end state at the cost of a manual step per binary release until P0.10 closes the gap. **The earlier "deprecated and unverifiable" entry above remains accurate** but understated the situation: not just unverifiable, but with no current path to verify.
- **2026-05-03 / P0.10** — New deferred task added: "Recover or reconstruct `decloud-agent` source." Scheduled post-Phase-4. Two paths (recover vs reconstruct) documented in the task description, with the orchestrator-side wire protocol (`AttestationService.cs`, `AttestationModels.cs`) noted as the reconstruction reference. The task is genuine technical debt — running a security-critical attestation agent without rebuildable source — but does not block any migration phase. Risk register entry added in §10.
- **2026-05-03 / P0.6 (manual release, first iteration)** — Cut bootstrap release `binaries/v0.0.1` on `bekirmfr/DeCloud.Builds` to host the manually-uploaded `decloud-agent` binaries. CI built `dht-node-{amd64,arm64}` and `blockstore-node-{amd64,arm64}` plus `.sha256` files. `decloud-agent-{amd64,arm64}` binaries decoded from existing `.b64` files (NodeAgent repo) on Windows via PowerShell `[Convert]::FromBase64String`, hashed via `Get-FileHash`, manually uploaded via `gh release upload`. ELF magic verified on both. **Reverted** — the v0.0.1 tag created a SemVer inversion against the pre-existing `binaries/v1.0.0` release: GitHub marked v0.0.1 as "Latest" because it was newer chronologically, despite being lower in version. This would have made any seeder URL ambiguous (point at v0.0.1 and lose the existing v1.0.0 system-VM binaries; point at v1.0.0 and lose the agent files). v0.0.1 release + tag deleted; agent files re-uploaded to the existing v1.0.0 release instead. Final state: one release `v1.0.0` marked Latest, 14 assets, all SHAs and URLs recorded in §1 above for Phase 2/3 seeder consumption.
- **2026-05-03 / P0.6 (manual-release convention)** — **Manual-only artifact updates (e.g., new `decloud-agent` binary while source remains missing) ride along with the latest CI-cut release; no separate version bump for manual-only changes.** When CI is next triggered (system-VM source change triggers a new `binaries/v*` tag), upload the current manual files to the new release alongside the CI output. Don't cut tags solely to refresh manual files. Rationale: the manual files are deprecated by P0.10's resolution, so adding versioning machinery for them is investing in the wrong direction. When P0.10 lands, CI takes over the agent build and this convention retires naturally.
- **2026-05-03 / P0.7** — Sibling script (Option A) chosen over single shared script (Option B) for the tenant tree's `compute-artifact-constants.sh`. Rationale: the two trees have different release pipelines (system-VM CI is fully automated; tenant has manual decloud-agent + inline data-URI assets), and a shared script would force regeneration of both trees' constants when only one tree's assets changed. Two scripts = two domain-scoped tools, each output independent. Duplication is ~200 lines of stable algorithmic code; if a bug or improvement applies to both, fix-and-propagate is acceptable. If genuine drift starts, future cleanup can extract `DeCloud.Builds/lib/compute-artifact-common.sh` — not worth doing preemptively.
- **2026-05-03 / P0.3a (post-strip discussion)** — **Identity-fetch standardization across all three system VM roles.** Current state has three different patterns: DHT (cloud-init runcmd fetches → file → binary reads, hard-fail at 2 min), BlockStore (binary self-fetches at startup via `loadOrCreateIdentity()`, self-creates if missing), Relay (cloud-init fetches → sed-rewrites baked-in `wg-relay-server.conf`, falls back to label-baked WG key). Three code paths, three failure modes, three debug surfaces. **New standard:** cloud-init runcmd fetches obligation state at first boot with bounded retry (10 minutes, 60 × 10s), writes role-specific identity file, hard-fails the boot on timeout. Binary reads file at startup; no HTTP client in binary. No fallbacks. No self-creation. No label-baked WG key. The shared retry-loop pattern is duplicated across the three role YAMLs (acceptable — the *destination file* differs per role; the loop itself is ~15 lines). **Implications:**
  - **DHT:** extend retry ceiling 2 min → 10 min. Smallest change.
  - **BlockStore:** binary loses `loadOrCreateIdentity()` self-create path; becomes a file-reader. Real binary change — needs coordination with binary owner (solo dev). Cloud-init gains an identity-fetch runcmd block.
  - **Relay:** drop `__WIREGUARD_PRIVATE_KEY__` substitution from cloud-init. Drop sed-rewrite gymnastics on `wg-relay-server.conf`. Cloud-init runcmd writes `/etc/wireguard/private.key` from obligation state, then starts `wg-quick@wg-relay-server`. Behavioral change — current label-baked-key path goes away. The `RelayWireGuardPrivateKeyResolver` (planned for Phase 3) is no longer needed for cloud-init substitution; it lives only as the source for the obligation-state endpoint's response.
  - **Logging convention:** `${ROLE}-identity` log tag, `/var/log/decloud-${ROLE}-bootstrap.log` path.
  - **Sequencing:** standardization is **not** done in P0.3a/b/c (those remain pure structural strips, preserving current behavior). It lands as a new task **P0.9** (standardize identity-fetch across role YAMLs) executed after Phase 0 strips complete, before Phase 1. This isolates the behavior change from the layout change. New task added to Phase 0 task list.
  - **Design doc impact:** §2 (decisions table) gains a new row for the identity-fetch standard. §5 Phase 0 gains P0.9. §8 (out of scope) loses any implicit assumption that current identity-fetch paths persist. To be applied at next doc-cleanup pass.

---

## §3. Pre-flight — must complete before Phase 0

- [~] **PF.1** — Confirm scope policies with binary owners (design §2.3).
  - **Status: deferred to pre-Phase-3.** Solo developer is also the binary owner; will walk through scope tables before relay cutover (P3.1). Phases 0–2 unblocked.
  - Acceptance: written confirmation that DHT, BlockStore, Relay scope tables are correct as declared.

- [ ] **PF.2** — Place docs in `DeCloud.Orchestrator/docs/`.
  - Move `UNIFIED_CLOUDINIT_PIPELINE.md` and `UNIFIED_CLOUDINIT_PIPELINE_IMPLEMENTATION_PLAN.md` into `DeCloud.Orchestrator/docs/`.
  - Move `TENANT_VM_UNIFIED_PIPELINE.md` and `SYSTEM_VM_RENDERING_ADDENDUM.md` into `docs/archive/`.
  - Add `docs/archive/README.md`: one paragraph saying "These documents are superseded by `../UNIFIED_CLOUDINIT_PIPELINE.md`. Kept for historical record."
  - Acceptance: both active docs and archived drafts committed.

- [x] **PF.3** — Branch strategy: direct commits to main, no migration branch. Task ID in commit message.

---

## §4. Phase 0 — DeCloud.Builds preparation

Pure infrastructure. No code in NodeAgent or Orchestrator changes. Phase 1 can start in parallel once these tasks are complete.

- [x] **P0.1** — Create `base-tenant.yaml` (design §5 Phase 0.1).
  - File: `DeCloud.Builds/base-templates/base-tenant.yaml`.
  - Source: extract from current `general-vm-cloudinit.yaml` (in NodeAgent).
  - Contents: bootcmd (machine-id regen, snapd/LXD mask, qemu-guest-agent), packages, write_files for `decloud_ca.pub`, sshd hardening, decloud-agent + welcome systemd units, runcmd for artifact downloads + SSH CA setup.
  - Acceptance: file syntax-checks as YAML. No role-specific content.
  - **Done 2026-05-03.** Welcome content moved to general role layer (see §2 decision). Decloud-agent converted from inline-base64 to artifact-pipeline fetch. Note: `snapd/LXD mask` from the design's checklist isn't present in source `general-vm-cloudinit.yaml`; it's only in current system-VM YAMLs. Will be added to `base-system.yaml` (P0.2), not retro-added to `base-tenant.yaml` unless needed.

- [x] **P0.2** — Create `base-system.yaml` (design §5 Phase 0.2).
  - File: `DeCloud.Builds/base-templates/base-system.yaml`.
  - Source: extract from current role YAMLs (DHT + BlockStore + relay).
  - Contents: scalars (hostname, manage_etc_hosts, package_update/upgrade off, disable_root, ssh_pwauth, SSH keys block), bootcmd (machine-id regen, snapd/LXD mask, qemu-guest-agent early start), packages (qemu-guest-agent, curl, jq, openssl, nginx-light, wireguard, wireguard-tools, python3), runcmd (qemu-guest-agent symlink + start).
  - Acceptance: file syntax-checks as YAML. No role-specific content. No assumptions about mesh participation.
  - **Done 2026-05-03.** Initially over-broad (included wg-mesh content); revised after relay YAML received — relay does not participate in the mesh, so wg-mesh content moved to a parallel base (P0.2b). Final size: 2.8 KB. See §2 entries 2026-05-03 / P0.2 (revised) for full rationale.

- [x] **P0.2b** — Create `base-system-mesh.yaml` (parallel base for mesh-participant system VMs).
  - File: `DeCloud.Builds/base-templates/base-system-mesh.yaml`.
  - Source: extract mesh-participant content from DHT + BlockStore YAMLs; layer onto the same universal scaffold as base-system.yaml.
  - Contents: full universal scaffold (scalars, bootcmd, packages, runcmd — duplicated from base-system.yaml as a deliberate two-base design) + mesh-specific write_files (`/etc/decloud/wg-mesh.env`, `wg-mesh-watchdog.{service,timer,sh}`, `wg-quick@wg-mesh` override).
  - Acceptance: file syntax-checks as YAML. Both `WG_DESCRIPTION` and `DECLOUD_ROLE` placeholders present in `wg-mesh.env` for role-fill via declared statics.
  - **Done 2026-05-03.** Final size: 5.7 KB. See §2 decision entries 2026-05-03 / P0.2b for the two-base rationale and discipline note about universal-content drift.

- [x] **P0.3a** — Strip base content from `system-vms/dht/cloud-init.yaml`.
  - Remove: bootcmd, qemu-guest-agent package, wg-mesh-watchdog files, wg-quick override.
  - Keep: DHT-specific env file, systemd unit, nginx config, role runcmd.
  - Acceptance: composing with `base-system-mesh.yaml` (verified later in P1.2) reproduces current DHT cloud-init structurally.
  - **Done 2026-05-03.** Stripped 8 scalars + bootcmd + packages + 5 mesh write_files + 2 base runcmd entries. Role layer is 9.1 KB (was ~17 KB pre-strip). Manual composition simulation: 11 top-level keys, 8 packages, 4 bootcmd, 11 write_files (5 base + 6 role), 15 runcmd (2 base + 13 role). Matches v7.0 structurally. Bumped version comment to 8.0 with strip-history note. dht-metadata.json `version` field bumped to 8.0 to match.

- [x] **P0.3b** — Strip base content from `system-vms/blockstore/cloud-init.yaml`.
  - Same pattern as P0.3a. Strip target: `base-system-mesh.yaml` (BlockStore is mesh-participant).
  - **Done 2026-05-03.** Stripped 8 scalars + bootcmd + packages + 5 mesh write_files + 2 base runcmd entries. Role layer is 9.1 KB. Composition simulation: 11 top-level keys, 8 packages, 4 bootcmd, 11 write_files (5 base + 6 role), 14 runcmd (2 base + 12 role). Matches v3.0 structurally. Bumped version comment to 4.0; blockstore-metadata.json `version` field bumped to 4.0 to match. **No identity-fetch runcmd added** — preserves current "binary self-loads via `loadOrCreateIdentity()`" behavior; P0.9 will add the runcmd block and coordinate the binary change.

- [x] **P0.3c** — Strip base content from `system-vms/relay/cloud-init.yaml`.
  - Same pattern as P0.3a, but strip target: `base-system.yaml` (relay is NOT a mesh participant — it's the mesh hub, uses `wg-quick@wg-relay-server` instead).
  - **Done 2026-05-03.** Stripped 8 scalars + 4 bootcmd + 8 packages (9th, fail2ban, kept) + 2 base runcmd entries. Role layer is 21 KB (large — relay has lots of role-specific content). Composition simulation: 11 top-level keys, 9 packages (8 base + fail2ban via union), 4 bootcmd, 13 write_files (0 base + 13 role — relay base has no write_files), 40 runcmd (2 base + 38 role). Matches v5.1 structurally except for `hostname: __HOSTNAME__` (was `__VM_NAME__` in v5.1) — intentional standardization. Bumped version comment 5.1 → 6.0; relay-metadata.json `version` field bumped to 6.0 to match. **WG-config-rewrite-from-obligation-state runcmd preserved as-is** (with NOTE comment) — P0.9 standardization will replace it.

- [x] **P0.4** — Move tenant assets to `DeCloud.Builds`.
  - From: `NodeAgent/CloudInit/Templates/general/assets/general-api.py`, `index.html`.
  - To: `DeCloud.Builds/tenant-vms/general/assets/`.
  - Acceptance: files present, no functional change to NodeAgent yet (legacy path still references the originals).
  - **Done 2026-05-03** (executed directly by user via git).

- [x] **P0.5** — Move `decloud-agent` Go source to `DeCloud.Builds`.
  - To: `DeCloud.Builds/tenant-vms/general/src/`.
  - Acceptance: `go build` from new location produces the same binary.
  - **Done 2026-05-03 (effectively no-op).** Source is missing — the existing `decloud-agent-{amd64,arm64}.b64` binaries in NodeAgent were built manually at unknown commit and the Go source was never committed to any tracked location. Path `tenant-vms/general/src/` does not yet exist; will be populated by P0.10. For now, the existing `.b64` binaries serve as the operational artifact, decoded and uploaded manually to GitHub releases under the same `binaries/v*` tag scheme as `dht-node` and `blockstore-node`. See §2 / 2026-05-03 / P0.6 (revised) for the rationale and §10 for the risk register entry.

- [x] **P0.6** — Add `build-attestation-agent` CI job to `release-binaries.yml`.
  - Pattern: mirror of `build-dht`. Produces `decloud-agent-amd64`/`-arm64` GitHub release artifacts.
  - Acceptance: workflow run produces the artifacts; SHA256 published.
  - **Done 2026-05-03 with revisions.** Initially drafted with a `build-attestation-agent` job mirroring `build-dht` and a workflow rename to "Release VM binaries". **Reverted** when source was confirmed missing — the workflow can't build source that doesn't exist. Final state: workflow name reverted to "Release system VM binaries"; only `build-dht` and `build-blockstore` jobs run automatically; release notes include a "Manually-uploaded assets" section listing the four `decloud-agent` files that operators upload by hand to each release; comment block at top of workflow file documents why and references P0.10. The `build-attestation-agent` job will be re-added when P0.10 completes.

- [x] **P0.7** — Create `tenant-vms/compute-artifact-constants.sh`.
  - Pattern: sibling to `system-vms/compute-artifact-constants.sh`. Processes `tenant-vms/` directories.
  - Acceptance: running the script generates `artifact-constants.cs` content for the orchestrator seeder.
  - **Done 2026-05-03.** Sibling script (Option A) — separate output file for the tenant tree, no coupling between system-VM and tenant constant regeneration. Logic identical to system-VM script: same role-discovery, same naming convention (`{RolePrefix}{FileStem}{ExtSuffix}`, role prefix dropped if filename starts with role name), same MIME-type table, same C# output format. Only differences are textual: header references `GeneralVmTemplateSeeder.cs` instead of `SystemVmTemplateSeeder.cs`; "Next steps" console output points at the tenant seeder; script-path comment reflects the new location. Tested with stub `general-api.py` and `index.html` files: produces `GeneralApiPy{Sha256,DataUri}` and `GeneralIndexHtml{Sha256,DataUri}` constants with correctly-inferred mime types and properly-formed `data:` URIs.

- [ ] **P0.8** — Create `decloud-env-watcher.sh` and systemd units.
  - Files: `system-vms/shared/assets/decloud-env-watcher.sh`, `.service`, `.timer`.
  - Script content: see design §3.5.
  - Acceptance: `bash -n decloud-env-watcher.sh` passes; systemd unit files validate (`systemd-analyze verify` if available).

- [ ] **P0.9** — Standardize identity-fetch across all three system VM role YAMLs.
  - Pattern: cloud-init runcmd fetches `/api/obligations/{role}/state` from node-local API (192.168.122.1:5100) with bounded retry (60 attempts × 10s = 10 min ceiling). On success, writes role-specific identity file. On final timeout, hard-fails the boot with clear log message.
  - **Per-role changes:**
    - **DHT:** extend retry ceiling 2 min → 10 min. Update the existing identity-fetch runcmd block in `system-vms/dht/cloud-init.yaml`. Adjust log message wording for the new attempt-count format. Smallest change.
    - **BlockStore:** add identity-fetch runcmd block to `system-vms/blockstore/cloud-init.yaml`. Block fetches obligation state, writes `/var/lib/decloud-blockstore/identity.key`. **Coordinated binary change required:** `blockstore-node` must lose its `loadOrCreateIdentity()` self-create path; becomes a file-reader (reads `/var/lib/decloud-blockstore/identity.key`). Both changes land together — the cloud-init writes the file, the binary reads it. If only one half ships, the role breaks.
    - **Relay:** drop `__WIREGUARD_PRIVATE_KEY__` placeholder from `system-vms/relay/cloud-init.yaml` (no more label-baked WG key). Drop the sed-rewrite block on `wg-relay-server.conf`. Replace with: identity-fetch runcmd writes `/etc/wireguard/private.key` and `/etc/decloud/relay-subnet` from obligation state, then renders `wg-relay-server.conf` *from* those files (small templating helper or just `tee` of a heredoc). `wg-quick@wg-relay-server` then starts as before.
  - Standard logging convention: `${ROLE}-identity` log tag, `/var/log/decloud-${ROLE}-bootstrap.log` path.
  - Acceptance: each role's cloud-init has the same retry-loop shape (line counts within ±2 of each other across roles). All three hard-fail on 10-minute timeout. None has any fallback path. None has any self-creation. Relay's cloud-init no longer references `__WIREGUARD_PRIVATE_KEY__`.
  - **Note:** This task changes runtime behavior, unlike P0.3a/b/c which preserve it. Lands after the structural strips so any regression is isolated to this change.

- [ ] **P0.10** — *[DEFERRED — scheduled post-Phase-4, non-blocking]* Recover or reconstruct `decloud-agent` source.
  - Background: existing `.b64` binaries were built manually at unknown commit; Go source is missing from all tracked locations. Migration proceeds with binaries treated as opaque artifacts (manually uploaded to GitHub releases under the `binaries/v*` tag scheme). This task closes the source-of-truth gap.
  - Two paths, in priority order:
    1. **Recover:** locate the original source (private repo, archived branch, dev machine). If found, commit to `tenant-vms/general/src/` and verify `go build` produces a binary functionally equivalent to the existing `.b64` (passes attestation challenges from a test orchestrator).
    2. **Reconstruct:** if recovery fails, write fresh Go source matching the orchestrator-side wire protocol documented in `src/Orchestrator/Services/Attestation/AttestationService.cs` and `src/Orchestrator/Models/AttestationModels.cs`. Required behavior: HTTP server on port 9999 (`/challenge` endpoint), per-challenge ephemeral Ed25519 keypair (private key zeroed after signing), memory-touch test (writes pages, hashes content, measures TotalMs and MaxPageMs), reads `/proc/cpuinfo` / `/proc/meminfo` / `/etc/machine-id` / `/proc/sys/kernel/random/boot_id` / `/proc/uptime`, signs canonical message `{nonce}|{timestamp}|{vmId}|{cpuCores}|{memoryKb}|{pagesTouched}|{contentHash}|{bootId}|{uptimeSeconds:F3}|{ephemeralPubKey}`. Verified by deploying to test VM and confirming `result.Success = true` from a test orchestrator's verification.
  - Once source lands: re-add `build-attestation-agent` job to `release-binaries.yml` (mirroring `build-dht`), rename workflow back to "Release VM binaries", remove the manually-uploaded-assets section from release notes, retire the manual upload procedure.
  - Acceptance: Go source in `tenant-vms/general/src/` (`main.go`, `go.mod`, `go.sum`); CI builds pass; test-VM behavioral verification passes; release-binaries.yml updated; risk register entry resolved.
  - **Sequencing:** post-Phase-4. Phase 4 (legacy cleanup) is the natural milestone before tackling tech debt that doesn't block migration. May be reprioritized earlier if a security review or attestation bug forces the issue.

**Phase 0 done when:** All P0.x except P0.10 checked. P0.10 is a known-deferred task and does not block Phase 1. No regressions in current production (legacy paths still active).

---

## §5. Phase 1 — Shared infrastructure (Orchestrator)

All new code; no behavioural change to live VMs. Legacy paths remain. This is the largest single chunk of work and the core of the migration.

### 5.1 Models and core types

- [ ] **P1.1** — `TemplateVariable` model in Shared (design §2.2).
  - File: `src/Shared/Models/TemplateVariable.cs`.
  - Acceptance: compiles, JSON round-trip preserves all fields, BSON serialization works (test against MongoDB doc).

- [ ] **P1.2** — Add `Variables: List<TemplateVariable>` to both template models.
  - Files: `src/Orchestrator/Models/VmTemplate.cs`, `src/Shared/Models/SystemVmTemplate.cs`.
  - Default empty list. Existing MongoDB documents must deserialize without error.
  - Acceptance: load existing template documents from MongoDB; verify no exception. Persist a template with `Variables` populated; reload; fields present.

### 5.2 TemplateComposer

- [ ] **P1.3** — Implement `TemplateComposer` (design §5 Phase 1.2).
  - File: `src/Orchestrator/Services/CloudInit/TemplateComposer.cs`.
  - String-based section detection (no YAML parser dependency).
  - Merge rules: `packages` union/dedup; `write_files`/`bootcmd`/`runcmd` base-first then role; scalar keys role-wins; `#cloud-config` header emitted once.
  - Unit tests: base-only, role-only, both packages, both runcmd, role-hostname-wins, both-have-hostname, neither-has-section.
  - Acceptance: all unit tests pass. `Compose(base-system.yaml, dht/cloud-init.yaml)` produces structurally-valid cloud-init (manual inspection acceptable for this milestone; full integration test in Phase 3).

### 5.3 Resolver registry and renderer

- [ ] **P1.4** — `IVariableResolver`, `IVariableResolverRegistry`, `ResolutionContext`, `VariableResolverRegistry` (design §3.1).
  - Files in `src/Orchestrator/Services/CloudInit/`.
  - Registry is DI singleton. Lookup by `(ResolverKey, Kind)`.
  - Acceptance: register a synthetic resolver, look it up, dispatch a `ResolveAsync` call. Missing resolver returns null (not throws — caller decides).

- [ ] **P1.5** — Implement `CloudInitFormatting` helper.
  - File: `src/Orchestrator/Services/CloudInit/CloudInitFormatting.cs`.
  - Methods: `BuildSshKeysBlock`, `BuildPasswordBlock`, `IndentForYaml`. Lift logic from `LibvirtVmManager` and current `VmService` — do not reimplement.
  - Acceptance: per-method unit tests covering empty inputs, populated inputs, multi-line inputs.

- [ ] **P1.6** — Platform-common resolvers (design §4.2 layout).
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/PlatformCommon/*.cs`. One per resolver:
    - `VmIdResolver`, `VmNameResolver`, `HostnameResolver`, `NodeIdResolver`
    - `OrchestratorUrlResolver` (with co-located guard — see design §11 Q1)
    - `CaPublicKeyResolver`, `SshAuthorizedKeysBlockResolver`, `PasswordConfigBlockResolver`
    - `ArtifactUrlResolver`, `ArtifactSha256Resolver`
    - `VariableScopesBlockResolver` (renders the `VAR=scope` block from `template.Variables`)
    - `TimestampResolver`
  - All `Kind = Static` for these.
  - Acceptance: each resolver has a unit test using a synthetic `ResolutionContext`. Resolvers throw with clear messages on missing required input (no empty-string returns).

- [ ] **P1.7** — `CloudInitRenderer` (design §3.2).
  - File: `src/Orchestrator/Services/CloudInit/CloudInitRenderer.cs`.
  - Iterate `template.Variables` where `Kind == Static`; resolve each; substitute. Validate.
  - Acceptance: synthesize a template with 5 statics; render; all substituted, validator passes. Missing resolver throws. Required user input missing throws.

- [ ] **P1.8** — `CloudInitValidator` (design §3.4).
  - File: `src/Orchestrator/Services/CloudInit/CloudInitValidator.cs`.
  - Three failure modes: unresolved static, undeclared placeholder, dynamic-in-wrong-form.
  - Acceptance: unit tests for each failure mode. Clean output passes.

### 5.4 Node record and obligation field changes

- [ ] **P1.9** — Add `Node.SshCaPublicKey` field and registration plumbing (design §5 Phase 1.6).
  - Files: `src/Orchestrator/Models/Node.cs` (add field, in `NodeRegistrationRequest` too), `src/Orchestrator/Services/NodeService.cs` (stamp on register), `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs` (read `/etc/ssh/decloud_ca.pub`, send in registration payload).
  - Acceptance: a node re-registers; orchestrator's stored `Node.SshCaPublicKey` matches the file on the node.

- [ ] **P1.10** — Add `SystemVmObligation.VmName` field (design §11 last row).
  - File: `src/Orchestrator/Models/SystemVmObligation.cs`. Nullable string.
  - Acceptance: existing obligation documents load without error.

- [ ] **P1.11** — Assign `VmId` and `VmName` at obligation creation.
  - File: `src/Orchestrator/Services/SystemVm/SystemVmObligationService.cs`.
  - In `EnsureObligationsAsync` and `TryAdoptExistingVmAsync`: when creating a new `Pending` obligation, set `VmId = Guid.NewGuid().ToString()` and `VmName = $"{canonicalRole}-{node.Id[..8]}"`.
  - Acceptance: new obligation documents in MongoDB carry both fields. Existing obligations are untouched (backwards-compatible nullables).

**Phase 1 done when:** All P1.x checked. Unit-test coverage on every new class. No live behaviour changed — system VMs still on legacy `LibvirtVmManager` substitution path; tenant VMs still on legacy `VmService` variable-building path.

---

## §6. Phase 2 — Tenant VM cutover

Tenant VMs (general + marketplace) move to the new pipeline. System VMs stay on legacy paths.

- [ ] **P2.1** — `GeneralVmTemplateSeeder`.
  - File: `src/Orchestrator/Services/Tenant/GeneralVmTemplateSeeder.cs`.
  - Composes `base-tenant.yaml + tenant-vms/general/cloud-init.yaml` via `TemplateComposer`.
  - Declares `Variables` list for the tenant set: VM_ID, HOSTNAME, NODE_ID, CA_PUBLIC_KEY, ORCHESTRATOR_URL, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, ADMIN_PASSWORD, ARTIFACT_URL:* / ARTIFACT_SHA256:* for `decloud-agent`/`general-api-py`/`general-index-html`, TIMESTAMP, VARIABLE_SCOPES_BLOCK.
  - Revision-aware upsert (same pattern as `SystemVmTemplateSeeder`).
  - Acceptance: `platform-general` visible in MongoDB after orchestrator startup. `Variables` populated.

- [ ] **P2.2** — `VmService.CreateVmAsync` assigns `TemplateId = platform-general` for general VMs.
  - Per design §5 Phase 2.2.
  - Acceptance: a VM created with no `TemplateId` and `VmType == General` lands in MongoDB with `TemplateId = platform-general`.

- [ ] **P2.3** — Tenant resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/Tenant/*.cs`.
  - `DecloudVmIdResolver`, `DecloudPasswordResolver`, `DecloudDomainResolver`, etc. — one per platform-bound tenant variable.
  - For now: ingress URL etc. that depend on runtime state are out of scope; existing marketplace templates without `Variables` declarations use the legacy compatibility path.
  - Acceptance: per-resolver unit tests.

- [ ] **P2.4** — `VmService.AssignVmToNodeAsync` uses `CloudInitRenderer`.
  - Replace existing variable-building with: build `ResolutionContext`, call `_cloudInitRenderer.RenderAsync(template, ctx, ct)`.
  - For templates with no `Variables` declarations: legacy compatibility path — treat all referenced placeholders as statics, resolve via platform-common + `DefaultEnvironmentVariables`.
  - Acceptance: fresh general VM deploys via new pipeline. Cloud-init has no unresolved placeholders. `decloud-agent` running, welcome server reachable, SSH CA login works.

- [ ] **P2.5** — Verify `EnsureQemuGuestAgent` and GPU-shim safety nets still run.
  - Per design §5 Phase 2.5.
  - These are post-substitution concerns in `LibvirtVmManager.CreateCloudInitIso`; verify they're still triggered when `CloudInitUserData` comes from the new pipeline.
  - Acceptance: deploy a VM with no `qemu-guest-agent` declared in template — verify it gets injected. Deploy a Proxied-GPU VM — verify the shim is injected.

- [ ] **P2.6** — Marketplace template smoke test.
  - Pick one existing marketplace template. Deploy it. Verify legacy compatibility path works (template has no `Variables` list; render still succeeds).
  - Acceptance: marketplace VM boots, application reachable.

**Phase 2 done when:** All P2.x checked. Fresh general VM and one marketplace VM deploy successfully via new pipeline. System VMs unaffected.

---

## §7. Phase 3 — System VM cutover, one role at a time

Order: **relay → DHT → BlockStore.** Each role is a complete cutover with its own production observation window before the next starts.

### 7.1 Sub-phase 3.1 — Relay cutover (also lands shared system-VM infrastructure)

This sub-phase brings up the generic environment endpoint and the reconciler simplification, since both are needed by all three roles. Subsequent role cutovers don't repeat these.

- [ ] **P3.1.1** — Update `SystemVmTemplateSeeder` for `system-relay`.
  - Compose cloud-init via `TemplateComposer.Compose("base-system", "relay")`.
  - Declare `Variables`: full static set + dynamics per design §2.3 (relay has near-empty dynamics).
  - Bump `Revision`.
  - Acceptance: `system-relay` template reflects new content; `Variables` populated; revision incremented.

- [ ] **P3.1.2** — Relay-specific resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/SystemVm/Relay/*.cs`.
  - Statics: `RelayWireGuardPrivateKeyResolver`, `RelaySubnetResolver`, `OrchestratorPublicKeyResolver`, `OrchestratorIpResolver`, `OrchestratorPortResolver`, `PublicIpResolver`.
  - Dynamics: `RelayRegionResolver` (Noop), `RelayCapacityResolver` (Noop) per §2.3.
  - Acceptance: per-resolver unit tests.

- [ ] **P3.1.3** — Generic environment endpoint on node agent.
  - Files: `src/DeCloud.NodeAgent/Controllers/ObligationEnvironmentController.cs`, `src/DeCloud.NodeAgent.Core/Models/ObligationEnvironment.cs`, node-side `IVariableResolver` mirror, `src/DeCloud.NodeAgent/Services/CloudInit/Resolvers/SystemVm/Relay/*.cs`.
  - Endpoint: `GET /api/obligations/{role}/environment`.
  - Returns `{ values, scopes, generation }` per design §3.3.
  - Acceptance: endpoint returns valid JSON for relay role. `generation` is deterministic SHA256 of values.

- [ ] **P3.1.4** — Update `system-vms/relay/cloud-init.yaml` in `DeCloud.Builds`.
  - Use `__VARIABLE_SCOPES_BLOCK__` for the scope file write.
  - Add identity-fetch (existing pattern from `SYSTEM_VM_RESILIENCE_DESIGN.md` §4.8).
  - Add environment-fetch from `/api/obligations/relay/environment`. Write `/etc/decloud-relay/environment` and `/etc/decloud-relay/variable-scopes.conf`.
  - Install + enable `decloud-env-watcher.timer`.
  - Service unit uses `EnvironmentFile=/etc/decloud-relay/environment`.
  - Note: relay-api.py currently has values substituted directly into the .py at cloud-init time — for this sub-phase, keep that pattern; `RELAY_REGION` and `RELAY_CAPACITY` declared `Noop` reflects this (design §2.3).
  - Acceptance: composed cloud-init renders; relay VM boots; cloud-init log clean.

- [ ] **P3.1.5** — `SystemVmReconciler.ActCreateAsync` becomes pure executor (design §5 Phase 3.1.6).
  - File: `src/DeCloud.NodeAgent.Infrastructure/Services/SystemVm/SystemVmReconciler.cs`.
  - Replaces current substitution logic. Reads `obligation.VmId`/`VmName`. Reads cached rendered cloud-init from local SQLite. Prefetches artifacts. Builds `VmSpec` with `CloudInitUserData = template.CloudInitContent`. Labels reduced to `system-vm-role`, `system-vm-revision`, `node-arch`.
  - **NOTE:** Drop `SubstituteArtifactVariables`. Drop local `Guid.NewGuid()`.
  - Acceptance: relay deploys via new path. `LibvirtVmManager` step 5.5 (relay block) is unreached during deploy (verify via log). Legacy path still present but dormant for relay.

- [ ] **P3.1.6** — Per-role acceptance for relay (design §5.3.2).
  - Fresh relay deploys cleanly. Cloud-init log has no unresolved placeholders.
  - `/etc/decloud-relay/environment` and `variable-scopes.conf` populated.
  - Watcher timer running.
  - Recovery test: `virsh destroy` + undefine; reconciler redeploys; identity preserved (same WireGuard pubkey).
  - Environment-drift test: mutate `node.Region` on orchestrator; within 60s, env file rewritten; service NOT restarted (Noop scope); verify by inspection.
  - 24-48h production observation. Document in §1 Notes.

- [ ] **P3.1.7** — Scope-correctness audit for relay (design §7.6).
  - For each declared dynamic: confirm with binary owner (or via empirical test) that the declared scope matches actual binary behaviour.
  - Corrections bump template revision and trigger re-render.
  - Acceptance: audit results recorded in §2 (Decision log).

### 7.2 Sub-phase 3.2 — DHT cutover

Reuses the environment-endpoint controller, watcher, and reconciler changes from 3.1. Only DHT-specific resolvers and template content are new.

- [ ] **P3.2.1** — Update `SystemVmTemplateSeeder` for `system-dht`.
  - Compose via `TemplateComposer`.
  - Declare `Variables` per design §2.3 DHT table (statics + 7 dynamics with scopes).
  - Bump `Revision`.
  - Acceptance: template updated; `Variables` populated; revision incremented.

- [ ] **P3.2.2** — DHT-specific resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/SystemVm/Dht/*.cs` (orchestrator side).
  - Statics: `DhtListenPortResolver`, `DhtApiPortResolver`.
  - Dynamics (orchestrator-side, for diagnostics/preview rendering): `DhtAdvertiseIpResolver`, `DhtBootstrapPeersResolver`, `DhtRegionResolver`, `WgRelayEndpointResolver`, `WgRelayPubkeyResolver`, `WgRelayApiResolver`, `WgTunnelIpResolver`.
  - Acceptance: per-resolver unit tests.

- [ ] **P3.2.3** — Node-side DHT dynamic resolvers.
  - Files: `src/DeCloud.NodeAgent/Services/CloudInit/Resolvers/SystemVm/Dht/*.cs`.
  - Resolve from `NodeMetadataService`, heartbeat-cached `RelayInfo`, etc.
  - Acceptance: environment endpoint returns correct values for DHT role.

- [ ] **P3.2.4** — Update `system-vms/dht/cloud-init.yaml` in `DeCloud.Builds`.
  - Same pattern as P3.1.4. Use `$VARNAME` shell references for dynamics in scripts/configs that previously used `__VARNAME__`.
  - Acceptance: DHT VM deploys via new path.

- [ ] **P3.2.5** — Per-role acceptance for DHT.
  - Same shape as P3.1.6.
  - Environment-drift test: mutate `node.RelayInfo` on orchestrator (simulate failover); within 60s, watcher detects, service restarted, new endpoint in use; **no redeploy**. Mutate bootstrap peer set; env file rewritten; no service action (Noop scope).
  - 24-48h production observation.

- [ ] **P3.2.6** — Scope-correctness audit for DHT.

### 7.3 Sub-phase 3.3 — BlockStore cutover

Same shape as DHT.

- [ ] **P3.3.1** — Update `SystemVmTemplateSeeder` for `system-blockstore`.
- [ ] **P3.3.2** — BlockStore-specific resolvers (orchestrator).
- [ ] **P3.3.3** — Node-side BlockStore dynamic resolvers.
- [ ] **P3.3.4** — Update `system-vms/blockstore/cloud-init.yaml`.
- [ ] **P3.3.5** — Per-role acceptance for BlockStore.
- [ ] **P3.3.6** — Scope-correctness audit for BlockStore.

**Phase 3 done when:** All three roles deploy via new path, all three audits complete, all three recovery + drift tests pass, full release cycle observed without regressions.

---

## §8. Phase 4 — Cleanup

Only after all three roles have been on the new path for one full release cycle without regressions.

- [ ] **P4.1** — Delete `LibvirtVmManager.PrepareCloudInitVariables` and role-specific blocks.
  - File: `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs`.
  - Delete steps 5.5 (relay), 5.6 (DHT), 5.7 (BlockStore) — ~125 lines.
  - Delete `PrepareCloudInitVariables` method itself.
  - Acceptance: code compiles; full system VM testbed run passes.

- [ ] **P4.2** — Delete `MergeCustomUserDataWithBaseConfig`.
  - File: same.
  - `CreateCloudInitIso` collapses to: `cloudInitYaml = spec.CloudInitUserData; cloudInitYaml = EnsureQemuGuestAgent(cloudInitYaml); cloudInitYaml = InjectGpuProxyShim(cloudInitYaml, spec);` then ISO generation.
  - Acceptance: tenant + system VMs all deploy.

- [ ] **P4.3** — Delete `CloudInitTemplateService`.
  - File: `src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitTemplateService.cs`.
  - Remove DI registration in `Program.cs`.
  - Acceptance: code compiles; no remaining references.

- [ ] **P4.4** — Delete `CloudInit/Templates/` and `decloud-agent-*.b64` files.
  - Path: `src/DeCloud.NodeAgent/CloudInit/Templates/`.
  - Acceptance: NodeAgent build still succeeds; deployment artifacts smaller.

- [ ] **P4.5** — Final testbed sweep.
  - Tenant + system VM deploys all pass.
  - No remaining references to deleted classes (grep the repos).
  - Acceptance: clean.

**Phase 4 done when:** All P4.x checked. The migration is complete.

---

## §9. Per-session protocol

When we pick up work together:

1. **Open this file.** Look at §1 for current state and §2 for any open decisions.
2. **Pick the next task.** Move its status to `[~]`.
3. **Work it.**
   - I draft code or pseudocode; you review/run.
   - You paste actual diffs or code I should review.
   - We iterate.
4. **Update the file.**
   - Mark task `[x]` when acceptance criteria met.
   - Append to §2 if a non-design decision came up.
   - Update §1 Active phase / Last session / Next task.
5. **Commit.** Reference the task ID in the commit message.

If a task reveals a problem with the design (not just a problem with the code), stop and update `UNIFIED_CLOUDINIT_PIPELINE.md`. Note the change in §2 here. Do not silently work around design gaps.

If we hit something neither doc covers — surface it; don't silently pick.

---

## §10. Risk register

Things that might go wrong, and how we'd respond.

- **Scope policy turns out wrong (Phase 3, found in audit P3.x.7).** Bump template revision in seeder, trigger re-render. Cost: one revision per affected role. Mitigated by §3 PF.1.
- **Legacy compatibility path (templates without `Variables`) breaks an existing marketplace template.** Add the missing variables to the legacy resolver fallback in P2.4. If a specific template is broken, declare its variables explicitly (one-off migration of that template).
- **`TemplateComposer` produces invalid YAML for an edge case.** Add a unit test reproducing it; fix the merge logic. The string-based approach is intentional (no YAML parser dependency); keep it that way.
- **Environment endpoint returns inconsistent `generation` due to nondeterministic JSON serialization.** Sort keys before hashing. Test with two consecutive calls returning identical bytes.
- **`relay-api.py` conversion to env vars becomes necessary.** Out of scope per design §8; if a need emerges, it's a separate spec.
- **P0.9 BlockStore binary change ships out of sync with cloud-init change.** If the binary loses `loadOrCreateIdentity()` before cloud-init writes the identity file (or vice versa), the role breaks. Mitigation: both halves land in one commit. Verify on testbed before merging — fresh BlockStore deploy must produce a working node, and `virsh destroy` + redeploy must preserve peer ID. Rollback is straightforward (revert the commit).
- **P0.9 relay loses its baked-in WG key fallback.** Current relay falls back to label-baked key if obligation state is unreachable; new pattern hard-fails. If obligation state is fundamentally broken at the moment a relay is being deployed, the relay fails to come up — which historically would have come up with a (possibly truncated) baked-in key. Mitigation: 10-min retry ceiling absorbs transient failures; hard-fail is correct response to permanent failures (better to fail loudly than to come up with possibly-bad state). Relay deployment frequency is low; risk window is small.
- **P0.9 ten-minute boot wait masks a real problem.** A boot that hangs on identity fetch for 10 minutes before failing is worse for ops visibility than failing at 2 minutes. Mitigation: log every retry attempt (every 10s) so the failure mode is loud in logs even if the boot hasn't yet hard-failed. The `${ROLE}-identity` log tag standardization makes this greppable.
- **`decloud-agent` runs without rebuildable source (P0.10).** The existing `decloud-agent-{amd64,arm64}.b64` binaries were built manually at unknown commit; Go source is missing. Migration ships these as opaque artifacts via the artifact pipeline — same wire shape as CI-built binaries, but no path from a specific commit to the running bytes. Consequences: cannot patch attestation bugs at the agent level, cannot rotate cryptographic primitives if a vulnerability is found, cannot improve memory-touch timing or other anti-overcommit checks. The migration does not make this worse than today (today is the same situation), but does not improve it either. Mitigation: P0.10 is tracked, scheduled post-Phase-4. Behavioral verification (deploy → challenge → verify response) before relying on a binary in production gives a partial signal that the binary still does what the orchestrator expects. **If a security audit, a vulnerability disclosure, or a change to the orchestrator's attestation protocol forces the issue, P0.10 is reprioritized to immediate.**

---

_End of plan. Update §1 and §2 as work proceeds._