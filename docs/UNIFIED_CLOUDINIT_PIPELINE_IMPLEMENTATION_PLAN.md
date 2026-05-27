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

**Active phase:** **Migration complete.** Phases 0–4 closed. Unified cloud-init pipeline is the single authoritative path for both system and tenant VMs. Relay v6.2 / DHT v8.3 / BlockStore v4.5 all deploy successfully on relay nodes (srv022010) and CGNAT nodes (MSI). General tenant VMs and marketplace template VMs deploy successfully. Validated end-to-end via two full deploy cycles plus dual-tamper drift acceptance. Legacy code paths deleted: `LibvirtVmManager.PrepareCloudInitVariables` STEP 5.5/5.6/5.7 (~147 lines), `MergeCustomUserDataWithBaseConfig` (~145 lines), `CloudInitTemplateService` entirely, `decloud-agent-*.b64` files. Net deletion: ~470 lines of legacy substitution / merge / template-service code. Single point of authority is now `CloudInitRenderer` (orchestrator side); single deferred substitution is `__VM_ID__` (node side, in `SystemVmReconciler.ActCreateAsync` after libvirt UUID minting per BLOCKSTORE-FIX §6).

**Last session arc:** 2026-05-06 / 2026-05-07 — closed every remaining cleanup-queue item plus Phase 4. Order of work: (1) bootstrap-poll ADVERTISE_IP source misalignment fixed (5 surgical changes — `dht-bootstrap-poll.sh`/`blockstore-bootstrap-poll.sh` re-source the watcher-owned `environment` file in the in-loop refresh, both bootstrap-poll service units gain `EnvironmentFile=-/etc/decloud-{role}/environment`, dead `*_ADVERTISE_IP` sed removed from `wg-config-fetch.sh`); side effect closed the "DHT/BlockStore stuck Pending" cleanup item — same root cause. (2) MSI CGNAT onboarding fixed (`install.sh` SSH CA path mismatch — was writing to `/etc/decloud/ssh_ca*`, all C# code expects `/etc/ssh/decloud_ca*`; aligned + added migration block for nodes installed pre-fix). (3) DHT v8.3 dashboard hardening folded into the v8.3 commit (added `EnvironmentFile=-/etc/decloud-dht/environment` and `PartOf=decloud-dht.service` to `decloud-dht-dashboard.service`, mirroring BlockStore's P3.3.4 fix). (4) VmId inversion closed (BLOCKSTORE-FIX §6) — removed `VmId = Guid.NewGuid().ToString()` pre-assignment from `NodeService.ReconcileNodeAsync` (fresh-registration block) and `SystemVmObligationService.TryAdoptExistingVmAsync` (no-existing-VM + stale-VM-ID-fallback branches); deferred `__VM_ID__` substitution to deploy-time on node side via single `Replace` in `SystemVmReconciler` (Option A); updated `WG_DESCRIPTION` resolver to emit `__VM_ID__` placeholder reference instead of phantom GUID; heartbeat-based adoption guard now live, role-specific self-heal callbacks become belt-and-suspenders. (5) P3.3.6 closed as bookkeeping (BlockStore scope-correctness audit findings pulled forward into P3.3.1/P3.3.4 — same shape as DHT's P3.2.6; documented full 24-variable rationale: 12 platform-common statics + 5 BlockStore-specific statics + 4 WG_* demoted statics + 1 VARIABLE_SCOPES_BLOCK + 2 dynamics, mirroring DHT). Phase 3 closed. (6) Phase 4 executed bold-cutover style — P4.1 deletes STEP 5.5/5.6/5.7 (147 lines, replaced with breadcrumb comment); P4.2 deletes `MergeCustomUserDataWithBaseConfig` (145 lines), call site collapses to direct `cloudInitYaml = spec.CloudInitUserData;`; P4.3 deletes `CloudInitTemplateService` entirely (else branch in `CreateCloudInitIsoAsync` replaced with `throw new InvalidOperationException` — accepted risk over telemetry-first); P4.4 deletes `decloud-agent-*.b64` and legacy YAML templates; P4.5 surfaced one bug — welcome page leak — root-caused to **two** issues: (i) `general-api.py`'s `do_GET` checks `path.endswith('.html')` but `translate_path('/')` returns the directory, not `/index.html` — directory→index.html resolution happens later in the parent's `send_head()`, after dispatch, so the substitution branch is bypassed for default browser requests; (ii) syntax mismatch — `general-api.py` used `__VM_NAME__` as the substitution key while `index.html` had been migrated to the `{{VM_NAME}}` consumer-render convention. Fix: directory→index.html resolution at top of `do_GET` + key syntax aligned to `{{VM_NAME}}`. (7) Architectural review of full unified pipeline conducted — confirmed three substitution boundaries are clean (render-time `__VARNAME__` in cloud-init bodies, deploy-time single `__VM_ID__` exception in `SystemVmReconciler`, runtime `$VARNAME` via `EnvironmentFile=` watcher); rescinded one bad finding from the review (initially claimed dashboard.js artifacts had the same leak as welcome page, but `dht-dashboard.py`/`blockstore-dashboard.py` template JS at request time via `_send_file_with_substitution` — the dashboards work correctly).

**Last session-arc commit messages (as recorded on disk):**
- `P4.1: Delete LibvirtVmManager STEP 5.5/5.6/5.7 — legacy role-specific cloud-init substitution`
- `P4.2: Delete LibvirtVmManager.MergeCustomUserDataWithBaseConfig`
- `P4.3 + P4.4: Delete legacy CloudInitTemplateService path`
- `P4.5: welcome page leak fix — directory routing + {{VM_NAME}} convention`
- `P3.3.6: BlockStore scope-correctness audit closed (bookkeeping only)`
- `Phase 3 closed; Phase 4 closed; unified cloud-init pipeline migration complete`

**Migration scoreboard (final):**

| Phase | Status | Done date |
|---|---|---|
| P0 — DeCloud.Builds preparation | ✓ | 2026-05-04 |
| P1 — Shared infrastructure (Orchestrator) | ✓ | 2026-05-04 |
| P2 — Tenant VM cutover | ✓ | 2026-05-04 |
| P3.1 — Relay cutover | ✓ | 2026-05-04 |
| P3.2 — DHT cutover | ✓ | 2026-05-05 |
| P3.3 — BlockStore cutover | ✓ | 2026-05-06 |
| P3.x.6 — scope audits | ✓ | 2026-05-06 |
| P4 — Cleanup | ✓ | 2026-05-07 |

**Deployments validated end-to-end on the closing day (2026-05-07):**

| Node | Role(s) deployed | Status |
|---|---|---|
| srv022010 (relay) | Relay v6.2 + DHT v8.3 + BlockStore v4.5 | All Active. routingTable populated. All peers discovered. |
| srv022010 (relay) | Marketplace template VM (general) | Active. Welcome page renders correct VM_NAME after P4.5 fix. |
| MSI (CGNAT) | DHT v8.3 + BlockStore v4.5 | All Active. peerId 12D3KooWHDYDiRCnmKSSJnQZ7SF5ctBWJ7EvMSQv1zAvKRoAWCrY (DHT) / 12D3KooWAXLXGUaEm9VMa8xuzJjDnJRZXKVKBh9NNZfvZLPjiaWD (BlockStore). Tunnel IPs 10.20.248.2 / 10.40.0.97. |
| MSI (CGNAT) | General tenant VM (st4-3387) | Active. SSH cert auth working. Welcome page renders correct VM_NAME after P4.5 fix. |

**Next task:** None. Migration is complete. Ongoing maintenance now happens through normal commit cycles outside this plan's scope. New work should reference `UNIFIED_CLOUDINIT_PIPELINE.md` (design doc) for architectural changes, not this implementation document.

**Post-closure additions:** §2 has live entries beyond migration closure when material pipeline-shape decisions are made (e.g. 2026-05-27 — compose-pipeline tenant template centralisation pattern).

**Pre-Phase-3 user action items:**
- ~~Cut a `binaries/v1.1.0` release for the updated BlockStore binary.~~ **Done 2026-05-05.** Tag pushed to `bekirmfr/DeCloud.Builds`; CI built dht-node + blockstore-node; decloud-agent files copied from v1.0.0 manually (bit-identical SHAs preserved). **Strategy B applied:** orchestrator updated to point all three seeders (`SystemVmTemplateSeeder` for relay/DHT/BlockStore, `GeneralVmTemplateSeeder` for decloud-agent) at `binaries/v1.1.0` — single release tag for all binaries.

**Open items deferred from migration scope (not blocking, tracked separately):**
- **P0.10** — `decloud-agent` source missing. Binaries shipped via artifact pipeline as opaque blobs (same wire shape as CI-built binaries; no path from commit to running bytes). Behavior verified empirically (deploy → challenge → response). Reprioritised to immediate only on security trigger or attestation protocol change.
- **Auto-generated SHA256 constants drift.** `SystemVmTemplateSeeder.Artifacts.cs` is regenerated by `compute-artifact-constants.sh`. No CI gate enforces regeneration when shared scripts change. Worth a future CI hook check (`git diff --exit-code` on the generated file after running the script).
- **Tenant userdata fast-fail throw is unverified in production.** P4.3 replaced the legacy `CloudInitTemplateService` else branch with a fast-fail `InvalidOperationException`. Verified clean for general tenant + marketplace template; not yet exercised by every possible orchestrator-side caller (e.g., container-mode CreateVm, custom-cloud-init paths). If a future caller arrives without `userData`, the throw fires and points at the originating code with a clear message — by design, not a regression risk to monitor proactively.
- **Single deferred placeholder (`__VM_ID__`) is fragile design.** Currently the only render-late variable; pattern documented in `SystemVmReconciler.ActCreateAsync` as a one-off. Adding a second deferred placeholder would require code edits (no general "render-late" mechanism in `TemplateVariable`). If the future brings another node-minted value, scope it as a small follow-up rather than building generic infrastructure speculatively.
- **`__VARNAME__` vs `{{VARNAME}}` convention split.** Adopted in P4.5: `__VARNAME__` for orchestrator render-time inside cloud-init bodies; `{{VARNAME}}` for consumer render-time inside artifact bytes (HTML/JS served from VMs). DHT and BlockStore dashboard `_SUBSTITUTIONS` dicts predate the convention and use `__VARNAME__` for both purposes — harmless because their server-side keys are scoped to artifact serving and never collide with orchestrator-render placeholders. Documented in `DeCloud.Builds/README.md`.

**Pre-existing issues that closed as side-effects of the migration:**
- ~~"DHT obligation status stuck Pending" — closed by 2026-05-06 bootstrap-poll fix (was a guard-skipping-join symptom of the watcher-source misalignment, not an orchestrator state-machine bug).~~
- ~~"DHT v8.3 dashboard hardening" — folded into the v8.3 commit alongside the bootstrap-poll fix.~~

**Unresolved cosmetic items (not blocking):**
- `dashboard.js` line 708 `rdotFailed` ReferenceError — UI typo, cosmetic. Triage when convenient; doesn't affect functional behavior.

**Known stylistic / non-blocking items:**
- `DecloudAgentAmd64Bytes` constant (`5_087_232`) disagrees with upstream `content-length` (`5087384`) by 152 bytes. SHA256 matches; the discrepancy is informational metadata only. Refresh on the next `decloud-agent` binary rotation rather than spending a revision bump on it now.
- Path note: `GeneralVmTemplateSeeder.cs` lives under `src/Orchestrator/Services/Tenant/` (namespace `Orchestrator.Services.Tenant`), not `…/SystemVm/…` as some prior session notes suggested.

**Artifact declarations** (canonical URLs and SHA256, all on release `binaries/v1.1.0` of `bekirmfr/DeCloud.Builds`):

System VM binaries (CI-built):
- `dht-node` / amd64 / sha256 `2cb706387725d13c161fa0781ba5fdf282fd4088e40522cccd4da73c06474210`
- `dht-node` / arm64 / sha256 `52e4cfd2349b53663361872d6dc2324b8bab362fe446c516c31e95fc918d7fd4`
- `blockstore-node` / amd64 / sha256 `01c62200e7a0be6c2c8cf5b73c88892b13b176df9269a817b9229df29c3c3cd2`
- `blockstore-node` / arm64 / sha256 `4cb83cd4721b0c9b4a3ab34898e179851de45f7094dfa49a3a3e93c846e5ffae`

Tenant VM binaries (manually uploaded; see P0.10):
- `decloud-agent` / amd64 / sha256 `530c33c349f4c55d17a4f7e6a328d60da09b1257c1ffd2c54c4efd9f3e3962a2`
- `decloud-agent` / arm64 / sha256 `349ad8dd16c837a868d8385a693a8ee376f72948660f823e8e70f150bda142ac`

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
- **2026-05-03 / P0.8** — **Watcher / watchdog overlap analyzed; ruled complementary, not conflicting.** The pre-existing `wg-mesh-watchdog.sh` (in `base-system-mesh.yaml`, every 2 min) reacts to *observed* WG handshake staleness; the new `decloud-env-watcher.sh` (every 60s) reacts to *declared* state changes from the orchestrator's env endpoint. Both can fire on a relay failover. The watcher fires first and is the primary path; the watchdog is the backstop that catches scenarios where the orchestrator hasn't noticed yet or where the env endpoint was briefly unreachable. No deduplication needed.
- **2026-05-03 / P0.8** — **Watcher additionally restarts `wg-quick@wg-mesh` when any `WG_*` variable changes**, alongside the role service. Without this, a `WG_RELAY_ENDPOINT` change would restart `decloud-{role}` with new env vars but leave the kernel WireGuard interface pointing at the old relay — broken until the watchdog catches up 60–120s later. The role's systemd `BindsTo=wg-quick@wg-mesh` does *not* propagate restart upward; explicit bounce required. Implementation: watcher checks if any changed variable has the `WG_` prefix and bounces wg-quick if so, but only if the unit exists (relay doesn't have one). This adds a small role-specific awareness to a "generic" watcher; acceptable because the `WG_*` names are part of the platform contract (declared in `base-system-mesh.yaml`), not arbitrary role choices, and the action is universal across mesh-participant roles. Logged as a small design refinement to §3.5 of the design doc; to apply at next doc-cleanup pass.
- **2026-05-03 / P0.9** — **Identity-fetch standardization landed across all three system VM roles in one commit.** All three roles now use the same retry-loop shape: `curl -sf` to `/api/obligations/{role}/state`, 60 attempts × 10s = 10 min ceiling, hard-fail on timeout, no fallbacks. Per-role outcomes:
  - **DHT** (cloud-init.yaml v8.0 → v8.1): Retry ceiling extended 2 min → 10 min. Log message format normalized (counter visible every retry). On-disk file format unchanged (raw 32-byte seed).
  - **BlockStore** (cloud-init.yaml v4.0 → v4.1, **plus binary change**): Identity-fetch runcmd added, writes `/var/lib/decloud-blockstore/identity.key`. Binary's `loadOrCreateIdentity()` replaced with `loadIdentity()` — file-reader only, no HTTP, no self-create. Identity-specific helpers removed from binary: `loadIdentityFromNodeAgent` and `cacheIdentityToDisk`. `defaultGateway` retained — still used by `fetchStorageQuotaFromNodeAgent` and `fetchAuthTokenFromNodeAgent` for runtime data queries (different concern from identity-loading). Imports unchanged.
  - **Relay** (cloud-init.yaml v6.0 → v6.1): Removed 4 label-baked write_files (`wg-relay-server.conf` with `__WIREGUARD_PRIVATE_KEY__` placeholder, `private.key`, `public.key`, `relay-subnet`). Removed sed-rewrite-from-obligation-state runcmd. Added new identity-fetch + heredoc-render runcmd that writes `/etc/wireguard/private.key`, `/etc/wireguard/public.key`, `/etc/decloud/relay-subnet`, then renders `wg-relay-server.conf` from those values. `relay-metadata.json` retains `__WIREGUARD_PUBLIC_KEY__` placeholder (option 1 — documentary metadata, render-time substitution) per user direction.
- **2026-05-03 / P0.9 (file-format discovery)** — While editing BlockStore's `main.go`, found that the existing on-disk identity-cache format (libp2p protobuf via `crypto.MarshalPrivateKey`) did **not** match the format cloud-init writes (raw 32-byte seed from `base64 -d` of `ed25519PrivateKeyBase64`). The existing fallback path (cache-to-disk after HTTP fetch) was internally consistent but incompatible with cloud-init-written files. Resolution: binary now reads raw seed, expands via `stded25519.NewKeyFromSeed`, passes to `crypto.UnmarshalEd25519PrivateKey`. Cloud-init writes 32 bytes; binary reads 32 bytes; no protobuf wrapper. **Note for DHT**: the existing DHT cloud-init has been writing the same raw-seed format with no apparent issues, suggesting DHT's binary already uses the read-raw-seed pattern (DHT source not in our repo's scope; no change needed for DHT in P0.9).
- **2026-05-03 / P0.9 (Phase 1 implication)** — `RelayWireGuardPrivateKeyResolver` (planned for Phase 1) is no longer needed for cloud-init substitution since `__WIREGUARD_PRIVATE_KEY__` no longer appears in any cloud-init template. The relay's WG private key now lives only in `obligation.StateJson.WireGuardPrivateKey` and is served via `/api/obligations/relay/state`. The Phase 1 resolver inventory shrinks accordingly.
- **2026-05-03 / P1.x (out-of-order implementation)** — P1.3 (`TemplateComposer`) implemented before P1.1 (`TemplateVariable` model) and P1.2 (add `Variables` to template models). The composer operates on raw YAML strings and has no dependency on the model layer; doing it first while context was fresh from Phase 0 was efficient. P1.1 and P1.2 followed in the same session as small bookkeeping work. No future tasks affected; resolver registry (P1.4) and renderer (P1.7) consume both `TemplateComposer` output and `TemplateVariable` declarations, so both prerequisites are now in place before P1.4 starts.
- **2026-05-03 / P1.3 (path)** — `TemplateComposer.cs` placed at `src/Orchestrator/Services/TemplateComposer.cs` rather than `src/Orchestrator/Services/CloudInit/TemplateComposer.cs` (the path the plan suggested). Reasoning: the `CloudInit/` folder is intended for the resolver/renderer cluster (P1.4–1.8 will create ~12 files there); `TemplateComposer` is a single self-contained pure function that pre-dates the resolver pipeline conceptually. Keeping it at the `Services/` root keeps the `CloudInit/` folder cohesive (resolvers, renderer, validator) without a one-off composer file in the middle. **Revisit at P1.7** — if the composer ends up tightly coupled to the renderer in practice, move it under `CloudInit/` then.
- **2026-05-03 / P1.3 (Python reference implementation)** — Maintained `template_composer_reference.py` alongside the C# implementation. Used during this session to validate the algorithm against actual P0 files (DHT, BlockStore, relay compositions all matched expected structural counts). Kept as a test asset at `test/Orchestrator.Tests/Services/template_composer_reference.py`. If the C# implementation is ever modified, run the Python equivalent as a cross-check — they should produce structurally-identical outputs (modulo cosmetic whitespace/header differences).
- **2026-05-03 / P1.5 (deliberate bug fix during lift)** — `IndentForYaml` was lifted from `LibvirtVmManager.CreateCloudInitIsoAsync` step 5 with a corrected indent semantic. The original code prefixed **every** line with the indent (including line 1). For a placeholder at column 6 with multi-line content, this produces post-substitution output where line 1 sits at column 12 and lines 2+ sit at column 6 — asymmetric. YAML's literal block scalar (`|`) auto-detects indent from the first content line; with line 1 at column 12 and line 2 at column 6, line 2 sits below the detected baseline and YAML treats it as a sibling key, terminating the block scalar early. The bug was masked in production because SSH CA public keys are typically single-line (auto-detection of 12 vs 6 produces equivalent stripped output). The new helper indents only lines 2+, leaving line 1 bare so the placeholder column provides its indent. Same single-line behavior; correct multi-line behavior. Plan §10 risk register has a "lift carefully" caveat against reimplementation drift; this fix is in spirit of that caveat — a tested algorithm that happened to have a latent bug is fixed at lift time, with the change documented and the new code carrying a comment pointing to this entry. **Verification at use site:** when `CaPublicKeyResolver` is wired up in P1.6, ensure the placeholder remains at 6-space indent in `base-tenant.yaml` (currently `      __CA_PUBLIC_KEY__`); changing the template indent without updating the resolver's `IndentForYaml` argument would re-introduce a similar bug.
- **2026-05-03 / P1.6 (artifact resolvers skipped — design mismatch)** — Plan §5.3 listed `ArtifactUrlResolver` and `ArtifactSha256Resolver` as platform-common resolvers. Skipped during P1.6 because the placeholder syntax for artifacts is parameterized (`${ARTIFACT_URL:dht-node}`, `${ARTIFACT_SHA256:dht-node}`) — not the `__VARNAME__` form that `IVariableResolver` is designed around. The resolver interface has a single `ResolverKey` per resolver; it can't dispatch on artifact name without per-template dynamic registration, which would be ugly. **Resolution:** keep artifact substitution as a separate concern in `CloudInitRenderer` (P1.7). The renderer's `Render()` method will run two passes: (1) `__VARNAME__` substitution via the resolver registry, (2) `${ARTIFACT_URL/SHA256:name}` substitution from `template.Artifacts` (existing `SubstituteArtifactVariables` logic, unchanged). This keeps both pipelines clean and matches how cloud-init substitution already works in the legacy path. P1.7 will document this two-pass design.
- **2026-05-03 / P1.6 (resolver inventory expanded beyond plan)** — Beyond the 10 plan-named resolvers (minus the 2 artifact ones we skipped), added 7 more discovered by auditing `__VARNAME__` placeholders in actual base/role files: `DeCloudRoleResolver` (mesh-base), `HostMachineIdResolver` (all role files), `PublicIpResolver` (relay), `SshPasswordAuthResolver` (tenant-base), `AdminPasswordResolver` (tenant-base), plus `PasswordConfigBlockResolver` (tenant-base — was implicitly listed but worth being explicit). Final count: 15 resolvers. The plan's resolver list was a sketch; the actual list comes from the templates. **For future readers:** any new placeholder added to a base file needs either a new resolver in this folder or, if it's role-specific, a new resolver in the appropriate Phase-3 role-resolver folder. The `CloudInitValidator` (P1.8) catches missing resolvers at render time so drift is loud.
- **2026-05-03 / P1.6 build errors and the P1.9/P1.10 partial-land split** — First build attempt of P1.6 surfaced 6 errors. Two were straightforward code mistakes — `VirtualMachine.SshPublicKey` is actually `Vm.Spec.SshPublicKey` (a nested `VmSpec`), and `SystemVmObligation.Role` is a `SystemVmRole` enum, not a string (use `SystemVmRoleMap.ToCanonicalName`). Two were forward dependencies — `SystemVmObligation.VmName` (P1.10) and `Node.SshCaPublicKey` (P1.9) don't exist yet. **Decision:** instead of stub-coding around the missing fields, add just the model-field halves of P1.9 and P1.10 NOW (one-line additions each, drafted as patches `P1.9_node_sshcapublickey_field.md` and `P1.10_obligation_vmname_field.md`). The "registration plumbing" half of P1.9 (NodeRegistrationRequest, NodeService stamping, OrchestratorClient reading) and the "assign at obligation creation" half of P1.10 (which was actually labeled P1.11) remain as separate tasks. P1.9 and P1.10 in the plan are now half-done; their remaining halves keep their task numbers but are smaller. **Why this is OK:** the resolver code references fields on existing models with `string?` types and `null` defaults — pre-migration MongoDB documents deserialize cleanly. Resolvers throw their null-guard exceptions until the plumbing lands; that's the documented behavior, with error messages that point at the responsible task. No silent failures.
- **2026-05-03 / P0.3a (post-strip discussion)** — **Identity-fetch standardization across all three system VM roles.** Current state has three different patterns: DHT (cloud-init runcmd fetches → file → binary reads, hard-fail at 2 min), BlockStore (binary self-fetches at startup via `loadOrCreateIdentity()`, self-creates if missing), Relay (cloud-init fetches → sed-rewrites baked-in `wg-relay-server.conf`, falls back to label-baked WG key). Three code paths, three failure modes, three debug surfaces. **New standard:** cloud-init runcmd fetches obligation state at first boot with bounded retry (10 minutes, 60 × 10s), writes role-specific identity file, hard-fails the boot on timeout. Binary reads file at startup; no HTTP client in binary. No fallbacks. No self-creation. No label-baked WG key. The shared retry-loop pattern is duplicated across the three role YAMLs (acceptable — the *destination file* differs per role; the loop itself is ~15 lines). **Implications:**
  - **DHT:** extend retry ceiling 2 min → 10 min. Smallest change.
  - **BlockStore:** binary loses `loadOrCreateIdentity()` self-create path; becomes a file-reader. Real binary change — needs coordination with binary owner (solo dev). Cloud-init gains an identity-fetch runcmd block.
  - **Relay:** drop `__WIREGUARD_PRIVATE_KEY__` substitution from cloud-init. Drop sed-rewrite gymnastics on `wg-relay-server.conf`. Cloud-init runcmd writes `/etc/wireguard/private.key` from obligation state, then starts `wg-quick@wg-relay-server`. Behavioral change — current label-baked-key path goes away. The `RelayWireGuardPrivateKeyResolver` (planned for Phase 3) is no longer needed for cloud-init substitution; it lives only as the source for the obligation-state endpoint's response.
  - **Logging convention:** `${ROLE}-identity` log tag, `/var/log/decloud-${ROLE}-bootstrap.log` path.
  - **Sequencing:** standardization is **not** done in P0.3a/b/c (those remain pure structural strips, preserving current behavior). It lands as a new task **P0.9** (standardize identity-fetch across role YAMLs) executed after Phase 0 strips complete, before Phase 1. This isolates the behavior change from the layout change. New task added to Phase 0 task list.
  - **Design doc impact:** §2 (decisions table) gains a new row for the identity-fetch standard. §5 Phase 0 gains P0.9. §8 (out of scope) loses any implicit assumption that current identity-fetch paths persist. To be applied at next doc-cleanup pass.
- **2026-05-03 / DEPLOY-FIX (post-Audit#2 first-deploy hotfix)** — First live relay deploy on node e9277b2c failed at cloud-final due to missing jq + qemu-guest-agent packages. Root cause: SystemVmTemplateSeeder was never updated to call TemplateComposer.Compose when fetching role layers — P0.3a-c stripped base content from the role YAMLs on the assumption composition would happen at seed time, but the seeder change to actually USE the composer was never tracked as a separate task. Both prior audits (IMPLEMENTATION_AUDIT, PIPELINE_AUDIT) checked that strips were done and that TemplateComposer existed, but neither traced whether the seeder USED the composer.
  - Coordinated patch (DEPLOY-FIX_relay_first_deploy.md) covering three files across three repos:
    - Orchestrator/Services/SystemVm/SystemVmTemplateSeeder.cs: added BaseUrlFor() role→base mapping,
      FetchAsync helper, FetchComposedCloudInitAsync helper; switched all three Build*TemplateAsync
      methods to compose; bumped all three TemplateRevision constants 1→2.
    - NodeAgent Libvirt/LibvirtVmManager.cs: dropped F2's ${VARNAME} regex half (high false-positive
      rate on legitimate bash references); kept __VARNAME__ half (caught real leak this deploy).
    - DeCloud.Builds system-vms/relay/cloud-init.yaml: deleted documentary
      "wireguard_public_key": "__WIREGUARD_PUBLIC_KEY__" line from /etc/decloud/relay-metadata.json
      per "documentary metadata, not source of truth" v6.0 design comment; bumped role version 6.1→6.2.
  - Operational: tore down broken relay VM, restarted orchestrator (re-seeded composed r2),
  restarted node-agent (re-pulled r2), redeployed.
  Lesson for future audits: a P-task being marked done means the file edits described in that
  task are done — it doesn't mean every consumer of those edits has been updated. Add an
  explicit "consumers updated" check to the audit checklist.
- **2026-05-03 / AUDIT-FIX (F1+F2+F3+F4 pipeline hardening)** — Two coordinated audits (`IMPLEMENTATION_AUDIT_2026-05-03.md`, `PIPELINE_AUDIT_2026-05-03.md`) ran end-to-end checks on the cloud-init pipeline post-DEPLOY-FIX. Surfaced four small defects landed as one combined patch set:
  - **F1** (orchestrator/VmService): `vm.Spec.SshPublicKey` was never stamped from the resolved `sshPublicKey` local at scheduling time. `SshAuthorizedKeysBlockResolver`'s primary path looked at `vm.Spec.SshPublicKey` and found null; it fell through to the user-supplied fallback (correct behavior, but the spec field should also reflect the resolved value for downstream consumers and observability). Patch stamps both scheduler entry points (`TryScheduleVmAsync` and `BackgroundServices.ScanMigratingVmsAsync`).
  - **F2** (node-agent/LibvirtVmManager): leak detection regex was too aggressive — `${VARNAME}` form has high false-positive rate against legitimate bash references in cloud-init runcmd (e.g. `${ROLE}` in shell scripts). Dropped that half. Kept the `__VARNAME__` half — that one caught a real leak in DEPLOY-FIX. Final regex matches only the orchestrator's substitution placeholder syntax.
  - **F3** (node-agent/LibvirtVmManager): hardened conditional checks in `MergeCustomUserDataWithBaseConfig` so the inline injection of password-auth + CA-pubkey blocks only runs when the spec actually requires them. Pre-fix, the injection ran unconditionally, producing duplicate write_files entries when the cloud-init template already had them.
  - **F4** (orchestrator/SystemVmTemplateSeeder): system VM template push to nodes never engaged the `CloudInitRenderer` — system templates were pushed with raw `__VARNAME__` placeholders intact. Consumers (SystemVmReconciler) were doing post-fetch substitution from local data, but the render pipeline didn't run at template-seed time. Patch wires `CloudInitRenderer.RenderAsync` into the seeder for system templates, with a synthetic `ResolutionContext` covering platform-common + system-VM resolvers. **Caveat documented:** since render happens at template-push time (not deploy time), `WG_DESCRIPTION` resolves with the pre-assigned obligation `VmId` rather than the deployed `VmId`. The runtime `wg-mesh-enroll.sh` script in the VM patches this at boot; not breaking, but a known limitation to remove when Phase 3 collapses pre-assigned VmIds (BLOCKSTORE-FIX §6).
- **2026-05-03 / BLOCKSTORE-FIX (join self-heal alignment)** — After DEPLOY-FIX brought the system VM triple-deploy to green, tenant VM creation failed with "unmet BlockStore obligation (Pending)". Root cause: `BlockStoreController.Join` used a strict `Role == BlockStore && VmId == request.VmId` match. `NodeService.ReconcileNodeAsync` pre-assigns `VmId = Guid.NewGuid()` at obligation creation; the deployed VM gets a different `VmId` from `SystemVmReconciler`. Match always failed, obligation stayed Pending. Relay had a callback-based adoption path (`RelayController.RegisterCallback`); DHT had a self-heal path in `DhtController.DhtJoin`; BlockStore alone had been missed in the P11 self-heal sweep. Patch added a self-heal block to `BlockStoreController.Join` mirroring the DHT pattern: lookup `Role == BlockStore && !string.IsNullOrEmpty(AuthToken)`, replace the stale `VmId`, set `Status = Deploying`. After HMAC verification, stamps `obligation.Status = Active; ActiveAt ??= UtcNow`. **Confirmed working:** BlockStore obligation transitioned to Active 20:41:38 with the deployed VmId, tenant VM us-1-8c15 deployed successfully on first attempt afterward. **Phase 3 cleanup target:** the underlying systemic issue is the pre-assigned VmId pattern in `NodeService.ReconcileNodeAsync`. Self-heal works around it correctly; Phase 3 removes the pre-assignment so all three roles align on a single uniform "node mints VmId, orchestrator adopts via heartbeat" pattern. F4's stale-VmId-in-WG_DESCRIPTION caveat resolves automatically when this lands.
- **2026-05-04 / TENANT-PREFETCH-FIX (drafted, SUPERSEDED, do not apply)** — First-shot fix for the us-1-8c15 cloud-init failure where `${ARTIFACT_URL:decloud-agent}` rendered to a local cache URL but the cache was empty (orchestrator's `CloudInitRenderer` Pass 2 substitution worked correctly; the missing piece was prefetch). Drafted as a two-file patch: orchestrator-side adds `Artifacts` to CreateVm payload, node-agent-side adds `_artifactCache.PrefetchAsync` directly into `CommandProcessorService.HandleCreateVmAsync`. **Reframed during review** — the diagram in the design discussion showed every VM type takes a different deployment path through the node, with prefetch existing on the system-VM path and missing on every tenant path. Patching only the tenant call site would entrench two parallel prefetch implementations. Renamed file to `TENANT-PREFETCH-FIX_artifact_prefetch_alignment_SUPERSEDED.md`; replaced by P2.5 unified pipeline below.
- **2026-05-04 / P2.5 (REFRAMED — unified node-side deploy pipeline)** — Original P2.5 was a smoke-test verification task ("verify EnsureQemuGuestAgent + GPU-shim safety nets still run"). Reframed during the route-diagram analysis to also cover an architectural extraction: a single `IVmDeploymentPipeline` on the node side that owns prefetch + binary verification + manager dispatch (steps 3 + 5 of the diagram), called from both `SystemVmReconciler.ActCreateAsync` (system VMs) and `CommandProcessorService.HandleCreateVmAsync` (tenant + custom + container). The original smoke-test acceptance is folded in — the pipeline is now where any post-render hooks would live, and the bu4-2770 redeploy after the CRLF fix exercises the full flow end-to-end including those safety nets. **Six-file patch document:** `patches/P2.5_unified_node_deploy_pipeline.md`. Files: NEW `IVmDeploymentPipeline.cs` interface + `VmDeploymentPipeline.cs` impl, DI registration in `Program.cs`, refactor `SystemVmReconciler.ActCreateAsync` (drops the local prefetch+verify block; calls `_pipeline.DeployAsync(vmSpec, template.Artifacts, ...)`); refactor `CommandProcessorService.HandleCreateVmAsync` (parses new `Artifacts` field from CreateVm payload; dispatches via `_pipeline.DeployAsync(vmSpec, artifacts, password, ct)`); orchestrator `VmService.cs` adds `Artifacts = template?.Artifacts ?? new List<TemplateArtifact>()` to CreateVm payload after `Services`. **Verified in repo** — six files all match patch spec. **Cosmetic follow-up:** `_artifactCache` field in `SystemVmReconciler` is now dead weight; remove in a separate three-line commit. **Out of scope for P2.5, deferred to Phase 4:** (a) synthetic-template path for `request.CustomCloudInit` so it inherits renderer + validator (~30 lines in `VmService.CreateVmAsync`), (b) cross-orchestrator `IVmDeployer` that pushes rendered cloud-init for every VM type and removes the SQLite render-on-node path for system VMs.
- **2026-05-04 / CRLF line endings (tenant artifacts)** — `bu4-2770` deployed via the new P2.5 pipeline. decloud-agent runs, attestation succeeds, terminal + file browser work. But `welcome.service` exits 127 with `/usr/bin/env: 'python3\r': No such file or directory`. Diagnosis: `tenant-vms/general/assets/general-api.py` and `tenant-vms/general/assets/index.html` were committed with CRLF line endings. The pipeline preserves bytes faithfully end-to-end (Builds repo → seeder constants → MongoDB → CreateVm payload → ArtifactCacheService → local cache → curl-to-VM). The defect is at the source. Per design philosophy "Do not wrap systemic problems in artificial controls": fix at the source, not in the pipeline. **`.gitattributes` added** to `DeCloud.Builds` enforcing LF on all text artifacts. **`core.autocrlf=true`** was set globally on the user's Windows machine, which fights `.gitattributes` — diagnosed via `git ls-files --eol | Select-String "w/crlf"`. Fixed via `git config --local core.autocrlf false` + delete + checkout. Both files verified LF-only on disk. `compute-artifact-constants.sh` re-ran successfully. **Next session continues here:** paste new constants into `GeneralVmTemplateSeeder.cs`, bump `GeneralTemplateRevision` 1→2, build, restart, redeploy bu4-2770. The `.gitattributes` is a permanent invariant — future Windows contributors with default `core.autocrlf` settings cannot reintroduce the same bug.
- **2026-05-04 / CRLF closure** — Constants pasted into `GeneralVmTemplateSeeder.cs` (timestamp `2026-05-04T15:48:55Z`), `GeneralTemplateRevision` bumped 1→2, orchestrator restarted, MongoDB confirmed `platform-general` at r2. Full platform reset performed; all three system VMs (relay/DHT/BlockStore) deployed cleanly via the new pipeline; fresh General tenant VM deployed cleanly with welcome page reachable on `*.vms.stackfi.tech` ingress. Welcome service running, shebang resolved, page rendering. `.gitattributes` enforcement and the local `core.autocrlf=false` flip combine to make this a permanent invariant going forward. **CRLF arc fully closed.**
- **2026-05-04 / P2.5 cosmetic cleanup** — Removed unused `_artifactCache` field from `SystemVmReconciler` (field + ctor param + assignment). Three lines. The pipeline (P2.5) owns prefetch end-to-end; the field was dead weight after the refactor. No behaviour change.
- **2026-05-04 / decloud-agent SHA verification** — Verified `decloud-agent-amd64` upstream binary on the live testbed: `curl -sL …/decloud-agent-amd64 \| sha256sum` returns `530c33c349f4c55d17a4f7e6a328d60da09b1257c1ffd2c54c4efd9f3e3962a2`, matching the published `.sha256` file and the `DecloudAgentAmd64Sha256` constant in `GeneralVmTemplateSeeder.cs`. Integrity intact — no constant refresh needed. The earlier-flagged 152-byte size discrepancy (`5_087_232` declared vs `5087384` upstream `content-length`) is informational only: SHA256 is the integrity gate at every layer (`ArtifactCacheService.VerifyAsync`, `TemplateService.VerifyExternalArtifactAsync` for HTTPS, the inline `Artifact()` factory's verify-at-build for `data:` URIs); `SizeBytes` is metadata that even gets overwritten from response Content-Length in `VerifyExternalArtifactAsync`. Refresh the constant on the next `decloud-agent` binary rotation rather than burning a revision bump for cosmetic accuracy.
- **2026-05-04 / `__VM_NAME__`-in-artifact regression — boundary fix** — Fresh General VM deployed cleanly (welcome page reachable, ingress serving) but the rendered HTML showed `__VM_NAME__` literally instead of the deployed VM's name. **Root cause:** the artifact pipeline ships bytes faithfully (the same property that bit us with CRLF). `__VM_NAME__` lives in `tenant-vms/general/assets/index.html` and is shipped as a base64 `data:` URI in `template.Artifacts`; the orchestrator's `CloudInitRenderer` substitutes `__VARNAME__` placeholders in the cloud-init **YAML body** but never traverses artifact bytes (per design — substituting per-VM would defeat content-addressable caching and produce a unique SHA256 per VM). **Why it worked before:** the pre-P0.1 path inlined the welcome HTML as a `write_files` entry inside `general-vm-cloudinit.yaml`, so `LibvirtVmManager.cloudInitYaml.Replace("__VM_NAME__", spec.Name)` happened to substitute it in passing. P0.1 moved the welcome content to artifacts and severed the substitution path; the regression surfaced now because this is the first deploy where the welcome page renders cleanly enough to be visually inspected. **Boundary analysis — three options considered:** (a) substitute on the orchestrator before computing the artifact's SHA256 / data URI — rejected, breaks content-addressability and per-node cache coherence; (b) `sed -i` in cloud-init runcmd after `curl` — works, but the cloud-init renderer also substitutes `__VM_NAME__` in the YAML body (it's a declared static), so the search pattern in the runcmd would itself get rewritten; workaround forces a marker convention split (`__VARNAME__` in YAML vs `{{VARNAME}}` in HTML); (c) substitute at serve time inside `welcome-server.py` using `socket.gethostname()` — single boundary (display-time data, display-time substitution), preserves the unified `__VARNAME__` convention across YAML and HTML, future placeholders extend trivially. **Lesson, restated at a different layer than CRLF:** artifact byte-faithfulness is a feature, not a bug; substitution must happen either before SHA computation (orchestrator-side, breaks content-addressability — wrong boundary) or after fetch on the VM (right boundary). Fix applied this session; constants regenerated, `GeneralTemplateRevision` bumped accordingly.
- **2026-05-04 / P2.6 closure** — Deployed an existing marketplace template (web-proxy-browser) on the same testbed. Template has no declared `Variables` list, so it exercises the legacy compatibility branch in `VmService.TryScheduleVmAsync` (P2.4-B). Deploy succeeded end-to-end; application reachable. Marketplace path works under the new pipeline. **Phase 2 functionally complete.** P2.3 (tenant resolvers) remains deferred — no current consumer; Phase 4 cleanup eliminates the legacy compatibility path that makes P2.3 unnecessary. **Phase 3 unblocked.**
- **2026-05-04 / P3.1.1+P3.1.2 (relay seeder + resolvers)** — Expanded `BuildRelayVariables()` in `SystemVmTemplateSeeder` from 1 entry (`WIREGUARD_PUBLIC_KEY`) to 21 entries covering all `__VARNAME__` placeholders in the v6.1 composed relay cloud-init: 13 platform-common statics (all have P1.6 resolvers), `WIREGUARD_PUBLIC_KEY` (existing), and 6 relay-specific statics. **Design clarification on RELAY_REGION/RELAY_CAPACITY:** plan P3.1.2 labels them "Dynamics" but design §2.3 text says relay is "nearly all Static" and P3.1.4 says "keep that pattern" (values baked into relay-metadata.json at render time; relay-api.py reads that file). Declared as **Static** (not Dynamic). `RelayWireGuardPrivateKeyResolver` omitted — `__WIREGUARD_PRIVATE_KEY__` was removed from the relay cloud-init in P0.9. Six new resolver files in `Resolvers/SystemVm/Relay/`: `OrchestratorPublicKeyResolver` (reads via `IWireGuardManager.GetOrchestratorPublicKeyAsync()` — key at `/etc/wireguard/orchestrator-public.key`, NOT in appsettings), `OrchestratorIpResolver` (derives from `ctx.OrchestratorUrl` with localhost→PublicIp sub), `OrchestratorWgPortResolver` (IConfiguration / "51821" fallback), `RelaySubnetResolver` (extracts octet from `RelayObligationState.RelaySubnet`), `RelayRegionResolver` (`ctx.Node.Region` / "default"), `RelayCapacityResolver` (`ctx.Node.RelayInfo.MaxConnections` / "10"). All registered in `SystemVmResolvers.AddSystemVmResolvers()`. P3.1.1+P3.1.2 land in one coordinated commit (declaring Required statics without registering their resolvers would throw at orchestrator startup). `RelayTemplateRevision` bumped.
- **2026-05-04 / P3.1.3 (environment endpoint)** — Added `GET /api/obligations/{role}/environment` to NodeAgent. Two files: `DeCloud.NodeAgent.Core/Models/ObligationEnvironment.cs` (response record with `values`/`scopes`/`generation` matching `decloud-env-watcher.sh` wire shape) and `DeCloud.NodeAgent/Controllers/ObligationEnvironmentController.cs` (virbr0-enforced, loads `SystemVmTemplate` from SQLite via `IObligationStateService`, filters Dynamic variables, returns values+scopes+generation). Generation = SHA256(sorted compact JSON of values)[..16]. **Scope decision:** node-side `IVariableResolver` mirror and relay-specific node-side resolver files (listed in plan P3.1.3) are deferred to P3.2.3. Relay has zero Dynamic variables in Phase 3 — building a resolver infrastructure with zero implementations would be dead code. DHT's 7 dynamics are the first real consumers. For relay, endpoint always returns `{ values: {}, scopes: {}, generation: "44136fa355ba77b9" }` (SHA256 of `{}`). Watcher sees empty scope file and exits cleanly each tick.
- **2026-05-04 / P3.1.4 (relay cloud-init v6.2 + seeder amendment)** — Two-repo patch. **DeCloud.Builds** relay cloud-init v6.1→v6.2: added `write_files` for `/etc/decloud-relay/variable-scopes.conf` (`__VARIABLE_SCOPES_BLOCK__`) and `/etc/decloud-relay/environment` (stub); added `EnvironmentFile=-/etc/decloud-relay/environment` to `decloud-relay-api.service` and `decloud-relay-http-proxy.service`; added runcmd blocks for watcher artifact install (3 `curl -f` fetches), initial env-fetch (best-effort; falls back to stub on endpoint-not-yet-ready), and `decloud-env-watcher@relay.timer` enable via `timers.target.wants/` symlink. Fixed: duplicate empty write_files path entry for http-proxy service (stray from patch application); relay-metadata.json `"version"` 6.1→6.2. **DeCloud.Orchestrator** seeder amendment: added `VARIABLE_SCOPES_BLOCK` to `BuildRelayVariables()` (this was missed in P3.1.1 — `VariableScopesBlockResolver` is platform-common from P1.6; for relay it produces `"# No dynamic variables declared"`); added three watcher artifacts (`decloud-env-watcher`, `-service`, `-timer`) to relay artifact list using existing constants from P0.8; bumped `RelayTemplateRevision`. Apply Orchestrator commit first; Builds commit in same push. `EnvironmentFile=-` with `-` prefix means systemd ignores absent file — defensive even though stub file pre-exists.
- **2026-05-04 / P3.1.5 (reconciler pure executor)** — `ActCreateAsync` simplified to two changes. (1) Drop `SubstituteArtifactVariables` call: `template.CloudInitContent` is the fully-rendered string from the orchestrator (`CloudInitRenderer` Pass 1 + Pass 2); node-side substitution is a no-op on already-rendered content. (2) Drop relay-specific label injection block (read `relaySubnet` from obligation state → `spec.Labels["relay-subnet"]` so LibvirtVmManager STEP 5.5 could substitute `__RELAY_SUBNET__`): `RelaySubnetResolver` (P3.1.1/P3.1.2) now handles this at orchestrator render time; placeholder absent from `template.CloudInitContent`. Labels dict kept at `node-arch`/`system-vm-role`/`system-vm-revision`; role-specific labels dropped. LibvirtVmManager STEP 5.5 still runs for relay but all `Replace()` calls are no-ops — dormant legacy path until Phase 4.1. `Guid.NewGuid()` retained with TODO: VmId inversion (remove pre-assignment from `NodeService.ReconcileNodeAsync`) is Phase 3 cleanup per BLOCKSTORE-FIX §6. `SubstituteArtifactVariables` method kept — Phase 4.1 deletes it alongside STEP 5.5/5.6/5.7. Fix: removed stray patch instructions from comment block (lines 441–444 in the applied file).
- **2026-05-04 / P3.1.6 (relay acceptance)** — All checks passed on fresh deploy of `relay-dab43779`. Cloud-init log clean (no `__VARNAME__` literals). `variable-scopes.conf` = `# No dynamic variables declared`. `environment` = `ENV_GENERATION=44136fa355b3678a`. relay-metadata.json clean. Services healthy. Watcher timer: **initially inactive** due to wrong symlink target path — cloud-init runcmd used `/etc/systemd/timers.target.wants/` (host-level path, not present in VM) instead of `/etc/systemd/system/timers.target.wants/`. Hotfix: correct the path + add `mkdir -p` guard. After fix: timer active, fires every ~63s. Node agent log confirms endpoint being polled: `ObligationEnvironmentController: served relay environment (0 dynamics, gen=44136fa355b3678a)`. Watcher journal (`-u decloud-env-watcher@relay`) shows service starting and deactivating cleanly each tick (no output because script logs to `/var/log/decloud-env-watcher-relay.log`, not stdout; `-t decloud-env-watcher` was wrong filter). Recovery test: `relay-b74fcd90` destroyed + undefined → `relay-dab43779` deployed by reconciler → same WireGuard pubkey (`CCix/Q9PvmQjHzlF8V+cuKQmYQ3B9akSery7TQvfhWs=`) confirmed from obligation state → identity preserved. 24-48h observation period started.
- **2026-05-04 / P3.1.7 (relay scope audit)** — Sole binary owner (solo developer) confirms: `RELAY_REGION` and `RELAY_CAPACITY` are both Static variables baked into `relay-metadata.json` at orchestrator render time. `relay-api.py` reads the metadata file at startup; it does not watch the environment file or react to runtime changes for these values. Scope `Noop` is correct — the watcher rewrites the env file but takes no service action. Phase 4 relay-api.py refactor (read env vars instead of metadata JSON) would change this to `Restart` at that time. No code change required. Audit complete.
- **2026-05-04 / watcher base refactor** — Moved `decloud-env-watcher` artifact fetch block (3 `curl` lines + `chmod` + `mkdir -p /etc/systemd/system/timers.target.wants`) from `relay/cloud-init.yaml` role runcmd into `base-system.yaml` and `base-system-mesh.yaml` base runcmd. Eliminates copy-paste for P3.2 (DHT) and P3.3 (BlockStore). **Constraint:** `${ARTIFACT_URL:decloud-env-watcher*}` in base is resolved at orchestrator render time using each role's artifact list — DHT and BlockStore must register the watcher artifacts before their first render or the placeholder stays unresolved. Watcher artifacts added to DHT and BlockStore seeder artifact lists as a prerequisite. All three `TemplateRevision` constants bumped (composed output changes for all three). `relay/cloud-init.yaml` v6.2→v6.3. Discipline note from `base-system-mesh.yaml` header applied: universal-content change lands in both base files. The hotfix symlink path correction (P3.1.6) is folded into the same relay v6.3 bump — single revision covers both changes.
- **2026-05-04 / P3.2.1+P3.2.2 (combined commit)** — Expanded `BuildDhtTemplateAsync`'s Variables list from 2 entries (`BuildMeshSystemVmVariables()`) to 24 via new `BuildDhtVariables()`. Composition: 12 platform-common statics (VM_ID, VM_NAME, HOSTNAME, NODE_ID, ORCHESTRATOR_URL, HOST_MACHINE_ID, TIMESTAMP, CA_PUBLIC_KEY, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, SSH_PASSWORD_AUTH, ADMIN_PASSWORD) + 5 DHT-specific statics (WG_DESCRIPTION, DECLOUD_ROLE, DHT_LISTEN_PORT default="4001", DHT_API_PORT default="5080", VARIABLE_SCOPES_BLOCK) + 7 dynamics with scopes per design §2.3. **Three issues caught during P3.2.2 verification and fixed in the same commit:** (1) renamed `SSH_CA_PUBLIC_KEY` → `CA_PUBLIC_KEY` (matches `base-system-mesh.yaml` placeholder + `CaPublicKeyResolver.ResolverKey`); (2) dropped `PUBLIC_IP` (DHT YAML doesn't reference it; `wg-config-fetch.sh` does runtime IP discovery); (3) added `PASSWORD_CONFIG_BLOCK`, `SSH_PASSWORD_AUTH`, `ADMIN_PASSWORD` (inherited from base-system-mesh.yaml's SSH/password machinery, same as relay). **No new resolver files** — coverage verified: all 17 statics covered by P1.6 platform-common (12 resolvers) + existing system-VM resolvers (`WgDescriptionResolver`, `DeCloudRoleResolver`) + `DefaultValue` for DHT_LISTEN_PORT/DHT_API_PORT + `VariableScopesBlockResolver` (auto-iterates Variables filtering Dynamics, emits scope lines). **Orchestrator-side Dynamic resolvers deferred per KISS** — render pipeline iterates only Statics (design §3.2); no preview/diagnostics consumer exists; same judgment as P3.1.3. `DhtTemplateRevision` 1→2.
- **2026-05-04 / P3.2.3 (node-side resolution, minimal)** — KISS approach. After P3.2.4's re-classification (below), DHT has only 2 dynamics — building `INodeVariableResolver` mirror infrastructure with registry, DI helper, and per-variable resolver classes for two values (one returning empty string) is dead infrastructure. Same KISS judgment applied to orchestrator-side Dynamic resolvers in P3.2.2. **Three changes:** (1) **NEW** `INodeRelayConfigProvider` + `NodeRelayConfigProvider` in `DeCloud.NodeAgent.Infrastructure/Services/CloudInit/` — extracted from `ObligationStateController.GetWgConfig` (CGNAT path + co-located relay path); returns `NodeRelayConfig?` (null on transient unavailability). (2) **REFACTOR** `ObligationStateController.GetWgConfig` to thin adapter — constructor reduced 6→3 deps; deleted dead helpers (`ParseWgConfigField`, `ComputeVmTunnelIp`, `DiscoverRelayGatewayFromWgAsync`) and tunnel-offset constants; wire format and 200/202 semantics for `wg-config-fetch.sh` callers preserved. (3) **UPDATE** `ObligationEnvironmentController.GetEnvironment` — inject `INodeRelayConfigProvider` + `INodeMetadataService`, pre-fetch relay config once per request (skip for relay role), replace TODO loop with inline `(role, name)` switch resolving 2 DHT dynamics. `DeriveAdvertiseIp` strips /CIDR suffix; returns "" when relayConfig null (watcher detects transition via generation diff). **DI registration: typed `HttpClient`** via `AddHttpClient<INodeRelayConfigProvider, NodeRelayConfigProvider>(client => client.Timeout = TimeSpan.FromSeconds(5))` in `Program.cs` — managed handler lifetime, no DNS staleness, no socket leak. `Microsoft.Extensions.Http` package ref only needed in the web project; `DeCloud.NodeAgent.Infrastructure` only needs `System.Net.Http` (BCL).
- **2026-05-04 / P3.2.4 (DHT YAML + scope re-classification)** — Three-repo coordinated commit. Implementation surfaced two architectural mismatches with the live system that warrant re-classifying 5 of the 7 design-declared dynamics. **Variable re-classification rationale:** (1) Four WG_* vars (`WG_RELAY_ENDPOINT`, `WG_RELAY_PUBKEY`, `WG_RELAY_API`, `WG_TUNNEL_IP`) demoted from Dynamic Restart → Static `DefaultValue=""`. `wg-config-fetch.sh` already polls `/api/obligations/{role}/wg-config` at boot and **overwrites `/etc/decloud/wg-mesh.env`** with real values — orchestrator-render-time values for these placeholders never reach the binary's runtime. Making them genuine Dynamics through the watcher pipeline would require restructuring `wg-mesh-enroll.sh` and `wg-mesh.env` (shared with BlockStore); significant scope creep with no functional gain because the existing path already works. Future P4 candidate. (2) `DHT_REGION` demoted from Dynamic Noop → Static `DefaultValue="default"`. Design §2.3's "consumed only by `dht-metadata.json` written once at boot; cosmetic, fixed by next redeploy" reasoning matches Static semantics exactly — the binary has no mechanism to observe runtime region changes. P3.2.6 scope audit finding pulled forward. **Result: DHT has 2 dynamics through the new pipeline** — `DHT_ADVERTISE_IP` (Restart) and `DHT_BOOTSTRAP_PEERS` (Noop, currently empty; binary uses `/api/dht/join` runtime endpoint). **DeCloud.Builds:** cloud-init v8.1→v8.2 — stripped 2 dynamics from `dht.env`, parameterised port literals (`DHT_LISTEN_PORT`/`DHT_API_PORT` → `__DHT_LISTEN_PORT__`/`__DHT_API_PORT__`, resolved via DefaultValue), added `/etc/decloud-dht/variable-scopes.conf` (`__VARIABLE_SCOPES_BLOCK__`), added `/etc/decloud-dht/environment` stub, added second `EnvironmentFile=-/etc/decloud-dht/environment` to `decloud-dht.service` (later override beats `dht.env` on key collision), added initial env-fetch runcmd, added watcher timer enable with `mkdir -p /etc/systemd/system/timers.target.wants` guard from P3.1.6 lesson, metadata version 8.1→8.2. **DeCloud.Orchestrator:** new `DhtRegionResolver` (Static, reads `ctx.Node.Region`, falls back to "default"), `DhtTemplateRevision` 2→3.
- **2026-05-04 / P3.2.5 acceptance** — All 11 checklist steps passed. Fresh DHT VM deployed cleanly via new pipeline. Cloud-init log clean (no `__VARNAME__` leaks). Static files correct: `variable-scopes.conf` has 2 lines (`DHT_ADVERTISE_IP=restart`, `DHT_BOOTSTRAP_PEERS=noop`); `dht.env` has statics only; `environment` populated via initial env-fetch with real generation hash. Watcher timer active, firing every ~63s. Environment endpoint serves expected JSON with `DHT_ADVERTISE_IP=10.30.0.x`, `DHT_BOOTSTRAP_PEERS=""`, deterministic generation. **wg-config endpoint regression check: green** — `wg-config-fetch.sh` callers see same wire format and 200/202 semantics after the `ObligationStateController` refactor. `decloud-dht.service` running with both `EnvironmentFile=` directives in correct order; binary's `/proc/PID/environ` confirms `DHT_ADVERTISE_IP` resolves from `environment` (overrides absent value in `dht.env`). DHT joined mesh, registered with orchestrator, peerId stable. Recovery test: destroy + redeploy preserved peerId from obligation state. Drift test for `DHT_ADVERTISE_IP` skipped (real-world drift requires relay reassignment, not easily synthesizable on srv022010); mechanism validated at boot via initial fetch path. 24-48h observation started. **P3.2.6 closure:** scope audit findings recorded as P3.2.4 amendments above; formal task is bookkeeping with no further code changes. **(Superseded 2026-05-05 — see next entry. The procedural pass missed three live-deploy bugs.)**
- **2026-05-05 / P3.2.5 (re-acceptance)** — Re-ran acceptance after platform reset and uncovered three live-deploy bugs missed by 2026-05-04's procedural checklist. Diagnosis path mostly via SQLite inspection of the node-side template cache (`/var/lib/decloud/vms/obligation-state.db`, table `system_template`).

  **Bug 1 — YAML symlink continuation (relay + dht).** Cloud-init log showed `ln: failed to create symbolic link ' /etc/systemd/system/timers.target.wants/decloud-env-watcher@dht.timer': No such file or directory` (note the leading space in the target). Root cause: PyYAML folds `\<newline>` plain-scalar continuation into a single space; bash sees `\<space>` as escaped space, not as a line continuation. The multi-line `ln -sf ... \<newline>  /etc/.../decloud-env-watcher@dht.timer` runcmd produced a target with a leading space and the symlink failed. **Fix:** replaced `ln -sf X \<newline>  Y` with single-line `ln -sf X Y` in both `relay/cloud-init.yaml` and `dht/cloud-init.yaml`. Validated in next deploy's `cloudInitUserData` dump (relay's runcmd shows correct single line).

  **Bug 2 — Variables omitted from node-side template projection.** Symptom: env endpoint returned `0` dynamics for DHT despite the orchestrator template in MongoDB having all 24 variables (verified in admin UI). SQLite query on the node showed `SELECT json_extract(template_json, '$.Variables') FROM system_template WHERE role='dht'` returning empty (jq: `.variables | length` → `0`); top-level keys present include `variables` (camelCase serialization) but the array was empty. Root cause: `BuildSystemVmTemplate` in `src/Orchestrator/Services/NodeService.cs` constructs the `SystemVmTemplate` payload for delivery to nodes by copying `CloudInitContent`, `Artifacts`, `VirtualCpuCores`, `MemoryBytes`, `DiskBytes`, `BaseImageUrl`, `BaseImageHash`, `Services` — but **never copies `Variables`**. The destination property exists (so JSON serializes the field with default `[]`), but the source-to-target mapping omitted the line. Same method is called from both `NodeService.GenerateSystemTemplatePayloads` (registration / heartbeat-pull) and `NodeSelfController.GetSystemTemplate` (per-role pull endpoint), so fixing the constructor closes both delivery paths simultaneously. **Fix:** one line added to the object initialiser:
  ```csharp
  Variables = template.Variables ?? new List<TemplateVariable>(),
  ```
  Validated post-fix on srv022010: env endpoint serves 2 dynamics with values + scopes; node SQLite cache shows 24 variables; `/etc/decloud-dht/environment` populated with real generation hash `9d38730139cc827f` and `DHT_ADVERTISE_IP=10.20.248.199`.

  **Bug 3 — DHT runcmd merged-line.** When applying Bug 1's symlink fix to `dht/cloud-init.yaml`, the `- systemctl start decloud-env-watcher@dht.timer` runcmd entry got concatenated onto the same line as the `ln -sf` because the editor stripped the line break. The composed YAML contained `... decloud-env-watcher@dht.timer  - systemctl start decloud-env-watcher@dht.timer` on a single line. `ln -sf` ran with a malformed second arg; the actual `systemctl start` was never executed; the timer was inactive at boot. Hotfix on live VM (manual `mkdir -p` + `ln -sf` + `systemctl daemon-reload` + `systemctl start`) succeeded — timer firing on 60s schedule, watcher service exits cleanly each tick on no-change path. **Fix in YAML:** ensure each runcmd entry is on its own line with leading `  - `:
  ```yaml
    - mkdir -p /etc/systemd/system/timers.target.wants
    - ln -sf /etc/systemd/system/decloud-env-watcher@.timer /etc/systemd/system/timers.target.wants/decloud-env-watcher@dht.timer
    - systemctl start decloud-env-watcher@dht.timer
  ```

  **Reactor end-to-end validation (dual-tamper drift test).** First single-tamper attempt revealed an architectural detail worth recording: the watcher uses generation-hash as a fast-path skip — if local `ENV_GENERATION` matches the live endpoint generation, no per-variable diff is performed (cheap exit, no log). Tampering only the value (without bumping the gen marker) leaves the fast-path intact and the watcher correctly does nothing. Tampering only the generation (without the values) results in `generation bumped but no tracked variable changed (possible template revision skew) — env file refreshed, no service action`. **Dual-tamper** (both gen and value drifted) is the path that exercises the restart scope: `journalctl -t decloud-env-watcher` logged `changed: DHT_ADVERTISE_IP (restart) — restarting decloud-dht`; `decloud-dht.service` `ActiveEnterTimestamp` advanced to the same second as the watcher log; env file repaired to live values. The fast-path optimisation is good design (8-byte hash compare before O(n) value diff) and worth documenting in the design doc.

  **Final acceptance after second platform reset.** No manual intervention required. Relay env endpoint: `{values:{}, scopes:{}, generation:"44136fa355b3678a"}` (empty-dict SHA-prefix, expected — relay has zero dynamics). DHT env endpoint: 2 dynamics with values + scopes + real generation hash `9d38730139cc827f`. Both watcher timers `active` at boot. Node-side `system_template` cache: 24 variables for DHT. Reactor dual-tamper validated again on the fresh deploy.

  **Lesson for future acceptance protocols:** procedural checklists ("does this file exist", "is this service running") miss systemic bugs. Substantive end-to-end validation must be part of every acceptance going forward — at minimum: (1) `sqlite3 .../obligation-state.db "SELECT json_extract(template_json, '$.variables') FROM system_template WHERE role=X"` to confirm the node-side cache content, (2) curl the env endpoint and verify the full body matches the seeder's declared variables, (3) dual-tamper drift test exercising at least one Restart-scope dynamic. Adding these to the P3.3.5 (BlockStore acceptance) checklist.

  **Deferred for Phase 3 cleanup, not blocking P3.2 closure:** (a) services list on dashboard shows duplicate "System" placeholders with null ports (node-side projection bug — `VmSpec.Services` not being mapped from `SystemVmTemplate.Services` correctly); (b) `dashboard.js` line 708 `rdotFailed` ReferenceError (UI typo, cosmetic); (c) DHT obligation status stuck at `Pending` in DB while VM is Running and services Ready (orchestrator state-machine bug, pre-existing).

- **2026-05-05 / binaries/v1.1.0 cutover (Strategy B)** — Cut `binaries/v1.1.0` release on `bekirmfr/DeCloud.Builds` and pointed the orchestrator at it across all three seeders. **Strategy B chosen over Strategy A** (single release tag for everything vs split URLs per binary). Rationale: KISS — one URL constant per seeder is simpler than per-binary constants; future cuts are a single-line change instead of N. The DHT churn cost is small and one-time (URL/SHA update; no functional change because dht-node source didn't change between v1.0.0 and v1.1.0 — SHA difference is purely Go build non-determinism). The `decloud-agent` SHAs in `GeneralVmTemplateSeeder` did NOT change (manual copy was bit-perfect) but the URL did, requiring a `GeneralTemplateRevision` bump because the Artifacts list serialises URLs and changing them is a template-content change. **What this closes:** the P0.9 BlockStore binary change (committed to DeCloud.Builds main since 2026-05-03 but unreleased) is now in production. The old v1.0.0 binary's `loadOrCreateIdentity()` self-create path was vestigial since v4.1 cloud-init (which writes the identity file before binary starts); v1.1.0 removes the dead code. **Plan §1 wording corrected:** the "blocked on v1.1.0" line was overcautious — P3.3 watcher work was never strictly blocked because the watcher pipeline is binary-agnostic. The release is a release-hygiene matter that's now resolved.

- **2026-05-06 / Bootstrap-poll ADVERTISE_IP source misalignment fixed** — After P3.2.4/P3.3.4 promoted `*_ADVERTISE_IP` to Dynamic (watcher-owned in `/etc/decloud-{role}/environment`), the bootstrap-poll scripts and `wg-config-fetch.sh` were never repointed to the new authority. Scripts sourced the statics file (`dht.env` / `blockstore.env`) where the line had been correctly stripped. `ADVERTISE_IP` evaluated to empty, the guard skipped `/api/{role}/join` indefinitely, and obligations stuck at `Pending` despite healthy services and joined mesh.

  **Fix (5 surgical changes, one commit, both DHT v8.3 + BlockStore v4.5):**
  - `dht-bootstrap-poll.sh` in-loop source: `dht.env` → `environment`.
  - `blockstore-bootstrap-poll.sh` in-loop source: `blockstore.env` → `environment`.
  - `decloud-dht-bootstrap-poll.service`: added `EnvironmentFile=/etc/decloud-dht/dht.env` + `EnvironmentFile=-/etc/decloud-dht/environment` (defense-in-depth, matches main service unit pattern).
  - `decloud-blockstore-bootstrap-poll.service`: same pattern.
  - `wg-config-fetch.sh`: removed dead `sed -i "s|^DHT_ADVERTISE_IP=.*|...|"` and equivalent BlockStore block. Watcher is now sole authority for the value; concealing parallel-write removed.

  **Boundary alignment:** the watcher owns the dynamic value, consumers source the file the watcher writes. The fix is a textbook example of "do not wrap systemic problems in artificial controls" — the existing wg-config-fetch sed and the bootstrap-poll source-from-statics were both indirect alternative paths to the same value, masking which one was actually authoritative. **Side effect:** closes the "DHT/BlockStore obligation status stuck Pending" cleanup item — it was a symptom of this same misalignment, not a separate state-machine bug.

- **2026-05-06 / MSI CGNAT install.sh CA path fix** — Fresh CGNAT node (MSI) registered with `SshCaPublicKey=null` because `install.sh` wrote the CA keypair to `/etc/decloud/ssh_ca[.pub]` but every C# code path that reads it (`OrchestratorClient.SshCaPublicKeyPath`, `SshCertificateController.CA_PUB_PATH`, `LibvirtVmManager`, `base-tenant.yaml`'s `__CA_PUBLIC_KEY__` placeholder) expects `/etc/ssh/decloud_ca[.pub]`. Symptom: every `/api/nodes/me/system-templates/{role}` fetch threw `InvalidOperationException` from `CaPublicKeyResolver`, surfaced as 500 to the agent, retried forever; system VMs never deployed.

  **Fix:** aligned `install.sh`'s `SSH_CA_KEY_PATH` and `SSH_CA_PUB_PATH` constants to `/etc/ssh/decloud_ca` and `/etc/ssh/decloud_ca.pub`. Added a migration block in `setup_ssh_ca` that moves legacy `/etc/decloud/ssh_ca[.pub]` files to the canonical path on update runs (idempotent across fresh, update-pre-fix, and update-post-fix scenarios). Validated on MSI: re-ran `cli-decloud-node login` after the fix → registration succeeded with CA key forwarded → DHT and BlockStore VMs deployed and joined mesh through the relay (10.20.248.2 / 10.40.0.97 tunnel IPs). **Defense-in-depth retained:** `CaPublicKeyResolver.ResolveAsync` continues to throw on null `SshCaPublicKey` rather than silently substituting empty.

- **2026-05-06 / DHT v8.3 dashboard hardening folded into v8.3** — Mirrored the BlockStore P3.3.4 dashboard fix to DHT: added `EnvironmentFile=-/etc/decloud-dht/environment` and `PartOf=decloud-dht.service` to `decloud-dht-dashboard.service`. Without these, the dashboard didn't see Dynamic values (`DHT_ADVERTISE_IP`, `DHT_BOOTSTRAP_PEERS`) and didn't cascade-restart when the watcher restarted the main service on a Restart-scope change. Folded into the same v8.2 → v8.3 commit as the bootstrap-poll fix.

- **2026-05-06 / VmId inversion (BLOCKSTORE-FIX §6) closed** — Removed `VmId = Guid.NewGuid().ToString()` pre-assignment from three sites: `NodeService.ReconcileNodeAsync` (fresh-obligation creation block); `SystemVmObligationService.TryAdoptExistingVmAsync` (no-existing-VM branch); same method (stale-VM-ID-fallback branch). Re-registration backfill block already didn't pre-assign and stayed correct. Adopted-existing-VM branch keeps `VmId = existingVmId` (correct — that's a real libvirt UUID).

  **Deferred-substitution mechanism:** `__VM_ID__` placeholder remains in cloud-init bytes after orchestrator render; `SystemVmReconciler.ActCreateAsync` substitutes it with the real libvirt UUID just before `_pipeline.DeployAsync()`. Single line: `template.CloudInitContent = template.CloudInitContent.Replace("__VM_ID__", vmSpec.Id);` — Option A from the design discussion. `WG_DESCRIPTION` resolver updated to emit the literal `__VM_ID__` placeholder reference (e.g., `WG_DESCRIPTION={role}-__VM_ID__`) so the same node-side pass substitutes both at once.

  **Result:** orchestrator and node converge on a single VmId (the real libvirt UUID). Heartbeat-based adoption guard (`NodeService.SyncVmStateFromHeartbeatAsync`) is now live code instead of dead code — its `string.IsNullOrEmpty(o.VmId)` precondition is finally reachable. Role-specific self-heal callbacks (Relay register, DhtJoin, BlockStoreJoin) become belt-and-suspenders rather than the only path. Single source of truth for VmId across the whole stack.

- **2026-05-06 / P3.3.6 closed (BlockStore scope-correctness audit)** — No code changes. Audit findings were pulled forward into P3.3.1 (variables declaration) and P3.3.4 (cloud-init v4.2 cutover) just as DHT's P3.2.6 was pulled into P3.2.4. BlockStore mirrors DHT's classification choices: 4 WG_* vars demoted Dynamic Restart → Static `DefaultValue=""` (wg-config-fetch.sh overwrites `/etc/decloud/wg-mesh.env` at boot — render-time value never reaches binary); `BLOCKSTORE_REGION` demoted Dynamic Noop → Static `DefaultValue="default"` (binary has no mechanism to observe runtime region changes; cosmetic in `blockstore-metadata.json`). **Result: BlockStore has 2 dynamics through the new pipeline** — `BLOCKSTORE_ADVERTISE_IP` (Restart) and `BLOCKSTORE_BOOTSTRAP_PEERS` (Noop, currently empty by design). Same shape as DHT.

  Full 24-variable rationale documented in §1 of this plan and in `BlockStoreSeeder` xmldocs:
  - 12 platform-common statics (VM_ID, VM_NAME, HOSTNAME, NODE_ID, ORCHESTRATOR_URL, HOST_MACHINE_ID, TIMESTAMP, CA_PUBLIC_KEY, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, SSH_PASSWORD_AUTH, ADMIN_PASSWORD).
  - 5 BlockStore-specific statics (DECLOUD_ROLE, WG_DESCRIPTION, BLOCKSTORE_REGION, BLOCKSTORE_LISTEN_PORT, BLOCKSTORE_API_PORT).
  - 4 WG_* demoted statics (WG_RELAY_ENDPOINT, WG_RELAY_PUBKEY, WG_RELAY_API, WG_TUNNEL_IP).
  - 1 VARIABLE_SCOPES_BLOCK (P1.6 resolver — produces `variable-scopes.conf` consumed by env-watcher).
  - 2 dynamics (BLOCKSTORE_ADVERTISE_IP / Restart, BLOCKSTORE_BOOTSTRAP_PEERS / Noop).

  Phase 3 closed.

- **2026-05-06 / P4.1 — `LibvirtVmManager` STEP 5.5/5.6/5.7 deletion** — Deleted role-specific cloud-init substitution blocks in `CreateCloudInitIsoAsync`. STEP 5.5 (Relay metadata + orchestrator pubkey read, 83 lines), STEP 5.6 (DHT metadata, 28 lines), STEP 5.7 (BlockStore metadata, 36 lines) totaled 147 lines deleted, replaced with a 16-line breadcrumb comment explaining where the substitution moved to. Updated stale comment in merged-userdata branch ("type-specific blocks above" → "STEP 1 already set them") and updated leak-detection `LogWarning` to drop the dead reference to "LibvirtVmManager STEP 5.x VmType branch".

  **Why safe:** orchestrator's `CloudInitRenderer` (post-P3.1) substitutes every role-specific placeholder before pushing to the node. By the time cloud-init reaches `LibvirtVmManager`, every `__VARNAME__` is resolved except `__VM_ID__` (deferred to `SystemVmReconciler`). The deleted blocks ran `Replace()` on already-substituted content — silent no-ops. Tenant VMs (`General`/`Inference`) had `if (spec.VmType == VmType.Relay)` guards that prevented the deleted blocks from running for them in the first place. Validated end-to-end this session: BlockStore/DHT/Relay all deploy cleanly through the new pipeline; P3.3.5 dual-tamper drift test passes; MSI CGNAT bootstrap successful.

- **2026-05-06 / P4.2 — `MergeCustomUserDataWithBaseConfig` deletion** — Deleted the ~145-line method entirely. Call site in `CreateCloudInitIsoAsync` collapsed to direct assignment: `cloudInitYaml = spec.CloudInitUserData;` followed by the existing defensive variables-Replace pass and leak detector (kept as safety nets).

  **Why safe:** the method's actual structural-injection work (hostname, ssh_authorized_keys, chpasswd, ssh_pwauth, bootcmd) was guarded by `if (!customUserData.Contains("hostname:", ...))` checks. Every condition skipped because the orchestrator-rendered cloud-init already contained those fields — verified empirically across all three system VM roles during P3.3.5 acceptance. Tenant VMs receive equally complete cloud-init per `VmDeploymentPipeline.cs` comment: *"tenant VMs receive pre-rendered cloud-init from the orchestrator."*

- **2026-05-06 / P4.3 + P4.4 — `CloudInitTemplateService` deleted entirely** — User explicitly chose bold deletion over a slower telemetry-first approach (which would have added a `LogWarning` in the else branch and observed for one release cycle before deleting). Risk acknowledged: if any orchestrator-side `CreateVm` caller still omits `userData`, the new `InvalidOperationException` fires at tenant deploy time. The throw message identifies the VmType, making the source caller easy to track down. Validation (general tenant + marketplace template) passed clean; ready for any future caller to surface itself loudly if it lacks the contract.

  **Changes:**
  - else branch in `CreateCloudInitIsoAsync` replaced with fast-fail `throw new InvalidOperationException(...)`.
  - Removed `_templateService` field, constructor parameter, and constructor assignment from `LibvirtVmManager`.
  - Removed `AddSingleton<ICloudInitTemplateService, CloudInitTemplateService>()` from `Program.cs`.
  - Deleted `CloudInitTemplateService.cs` and `ICloudInitTemplateService.cs`.
  - Deleted `decloud-agent-*.b64` files (P4.4 — they were consumed only by the deleted service).
  - Deleted legacy YAML templates in `src/DeCloud.NodeAgent/CloudInit/Templates/` directory.

  **Boundary alignment:** orchestrator's `CloudInitRenderer` is now the single authoritative substitution layer for cloud-init across all VM types. Per design philosophy: do not wrap systemic problems in fallbacks; fail fast at the boundary. If a `CreateVm` command arrives without `userData`, the originating orchestrator caller is the bug, not the node-side fallback that masked it.

- **2026-05-07 / P4.5 — welcome page leak compound bug** — Architectural review during the migration found the welcome page on a deployed general tenant VM showing literal `Virtual Machine __VM_NAME__` instead of the substituted hostname. Root-caused to **two simultaneous issues**:

  **Bug 1 (routing):** `general-api.py`'s `do_GET` checked `if path.endswith('.html') and os.path.isfile(path):` to dispatch into the substitution branch. But for a default browser request to `/`, `translate_path('/')` returns `/var/www` (the directory). Directory→`index.html` resolution happens later inside `SimpleHTTPRequestHandler.send_head()`, **after** `do_GET` dispatch. So root requests bypassed the substitution branch entirely and fell through to the parent's raw byte serve.

  **Bug 2 (syntax mismatch):** `general-api.py`'s `SUBSTITUTIONS` dict used `__VM_NAME__` as the key, but `index.html` had been migrated to `{{VM_NAME}}` per the consumer-render convention adopted earlier in the session. Even when the substitution branch did run (for explicit `/index.html` requests), the `Replace()` found nothing to replace.

  **Fix:**
  - Added directory→`index.html` resolution at the top of `do_GET` before the `.html` check.
  - Aligned `SUBSTITUTIONS` key to `{{VM_NAME}}` to match the HTML.

  **Convention adopted:** `__VARNAME__` for orchestrator render-time substitution inside cloud-init bodies; `{{VARNAME}}` for consumer render-time substitution inside artifact bytes (HTML/JS served from VMs). The two layers are independent and never overlap on the same token. Documented in `DeCloud.Builds/README.md`. DHT and BlockStore dashboard `_SUBSTITUTIONS` dicts predate the convention and use `__VARNAME__` for both purposes — harmless because their keys are scoped to artifact serving and never collide with orchestrator-render placeholders. No migration needed for them.

  **Architectural review correction:** initial review of the unified pipeline claimed the dashboard.js artifacts had the same leak as the welcome page (CONFIG.vmId etc. shipping as literal `__VM_ID__` strings). User pushed back; re-checked dashboard servers and confirmed `dht-dashboard.py` and `blockstore-dashboard.py` both have working `_SUBSTITUTIONS` dicts plus `_send_file_with_substitution()` that template JS/HTML at request time via `_serve_static`. The dashboards work correctly. The review's finding was wrong; rescinded. Lesson: read the serving layer before claiming a leak in the served bytes.

  **Generic-scanner proposal evaluated and declined.** User asked about a generic post-fetch scanner that would walk all artifact paths and substitute `{{*}}` placeholders. Per design philosophy: convention beats infrastructure when you have one current consumer that needs the fix and a clean alternative pattern (consumer reads its own values from existing endpoints / env vars). DHT and BlockStore dashboards demonstrate the working pattern: server-side templating at request time. Welcome page now follows the same pattern. Future template authors create their own variable-serving endpoints for static assets when needed; no generic scanner introduced.

- **2026-05-07 / Migration closure** — Phases 0–4 all marked complete. Unified cloud-init pipeline is the single authoritative path. ~470 lines of legacy substitution / merge / template-service code removed across `LibvirtVmManager`, `Program.cs`, deleted files. Final deploys validated end-to-end on both relay and CGNAT nodes for all three system VM roles plus tenant general VM and marketplace template VM. Plan transitions from active execution document to historical record + reference for the design doc update.

- **2026-05-27 / AI Chatbot (Ollama + Open WebUI) opt-in migration; compose-pipeline tenant templates centralised in TemplateSeederService.** Marketplace template `ai-chatbot-ollama` migrated from inline `TemplateSeederService.CreateOllamaOpenWebUiTemplate()` (legacy `${VARNAME}` path) to the compose pipeline. Root cause: deployed AI Chatbot VMs were failing the browser terminal proxy with "No suitable authentication method" because the inline template bypassed `base-tenant.yaml`, which carries the sshd_config drop-in (`99-decloud-password-auth.conf`) and the `chpasswd` bootcmd. Migration is the §11 opt-in path. New role layer at `DeCloud.Builds/tenant-vms/ai-chatbot/cloud-init.yaml`; `${DECLOUD_PASSWORD}` → `__ADMIN_PASSWORD__`, `${DECLOUD_DOMAIN}` → `__DECLOUD_DOMAIN__`. Ollama pinned to 0.7.0 per GPU_PROXY_DEBUGGING_JOURNAL.md Session 16 (Bug 23 — `ggml_backend_cuda_graph_reserve` aborts on shim's `cudaErrorNotSupported`).

  **Pattern decision:** compose-pipeline tenant templates centralised in `TemplateSeederService` rather than per-template seeder classes. Mirrors `SystemVmTemplateSeeder`'s shape (one class, many roles, shared compose/upsert helpers) on the tenant side. Adding the next marketplace template to the pipeline now requires: a role layer in `DeCloud.Builds`, a `{RoleUrl, TemplateRevision}` pair, a `Build*TemplateAsync` method, and a call site in `SeedComposeTenantTemplatesAsync`. No new file, no new DI registration. `GeneralVmTemplateSeeder` retained as a standalone for now because of its `partial class` artifact-constants pairing; absorbing it into `TemplateSeederService` is a tractable follow-up.

  **Resolver registry impact:** none. All declared statics (VM_ID, VM_NAME, HOSTNAME, ORCHESTRATOR_URL, CA_PUBLIC_KEY, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, ADMIN_PASSWORD, SSH_PASSWORD_AUTH) resolve via existing platform-common resolvers; DECLOUD_DOMAIN falls back to `UserSuppliedStatics["DECLOUD_DOMAIN"]` populated by `TemplateService.GetAvailableVariables`.

  **First post-Phase-4 §2 entry.** Confirms the impl plan remains a live decision log for material pipeline-shape choices after migration closure, per §1's "ongoing maintenance now happens through normal commit cycles" — entries here when the choice would surprise a future maintainer.
- **2026-05-28 / TemplateComposer: scalar collisions throw instead of silent role-wins.**
  The Phase-0 implementation merged scalars with "role wins if both declare the
  same key". No current template (general, ai-chatbot, relay, dht, blockstore)
  exercises that case — no scalar collision exists in the shipped set. The
  silent-override semantic was a latent footgun: a community template author
  could downgrade `ssh_pwauth: false` from `base-system.yaml` to `true` and
  the composer would silently emit the role's value. After this change,
  `CheckScalarCollisions` accumulates all colliding scalar keys and throws
  `InvalidOperationException` with both layer names + per-collision base/role
  values + design §2.4 reference. Lists (`packages` union, `runcmd`/`bootcmd`/
  `write_files` concat) are unaffected — they're intentionally additive.
  Caught by `TemplateSeederService.TryUpsertComposeAsync`'s per-template
  failure isolation, so a broken template doesn't block other seeds.
  Composed output for the existing template set is byte-identical pre/post
  change (no collisions to fire on). The change tightens behavior for future
  templates — especially community-authored ones — without affecting current
  ones.
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

- [x] **P0.8** — Create `decloud-env-watcher.sh` and systemd units.
  - Files: `system-vms/shared/assets/decloud-env-watcher.sh`, `decloud-env-watcher@.service`, `decloud-env-watcher@.timer`.
  - Script content: see design §3.5.
  - Acceptance: `bash -n decloud-env-watcher.sh` passes; systemd unit files validate (`systemd-analyze verify` if available).
  - **Done 2026-05-03.** Three files created in `system-vms/shared/assets/`. Watcher is role-parameterized (`/usr/local/bin/decloud-env-watcher.sh dht` etc.); systemd units are templated (`decloud-env-watcher@dht.timer`). Smoke-tested: argument validation works (exit 2 on missing role), graceful exit when scope file absent (exit 0 with log message). Algorithm: poll endpoint → diff generation → if changed, diff each declared variable, fold to max scope (noop/reload/restart), rewrite env file, apply action. **Design refinement during P0.8:** when any `WG_*` variable changes, watcher additionally restarts `wg-quick@wg-mesh` alongside the role service (only if the unit exists — relay safe). Reduces failover window from 60–120s to ~60s. See §2 for rationale on why this small role-aware extension is acceptable in a "generic" watcher.

- [x] **P0.9** — Standardize identity-fetch across all three system VM role YAMLs.
  - Pattern: cloud-init runcmd fetches `/api/obligations/{role}/state` from node-local API (192.168.122.1:5100) with bounded retry (60 attempts × 10s = 10 min ceiling). On success, writes role-specific identity file. On final timeout, hard-fails the boot with clear log message.
  - **Per-role changes:**
    - **DHT:** extend retry ceiling 2 min → 10 min. Update the existing identity-fetch runcmd block in `system-vms/dht/cloud-init.yaml`. Adjust log message wording for the new attempt-count format. Smallest change.
    - **BlockStore:** add identity-fetch runcmd block to `system-vms/blockstore/cloud-init.yaml`. Block fetches obligation state, writes `/var/lib/decloud-blockstore/identity.key`. **Coordinated binary change required:** `blockstore-node` must lose its `loadOrCreateIdentity()` self-create path; becomes a file-reader (reads `/var/lib/decloud-blockstore/identity.key`). Both changes land together — the cloud-init writes the file, the binary reads it. If only one half ships, the role breaks.
    - **Relay:** drop `__WIREGUARD_PRIVATE_KEY__` placeholder from `system-vms/relay/cloud-init.yaml` (no more label-baked WG key). Drop the sed-rewrite block on `wg-relay-server.conf`. Replace with: identity-fetch runcmd writes `/etc/wireguard/private.key` and `/etc/decloud/relay-subnet` from obligation state, then renders `wg-relay-server.conf` *from* those files (small templating helper or just `tee` of a heredoc). `wg-quick@wg-relay-server` then starts as before.
  - Standard logging convention: `${ROLE}-identity` log tag, `/var/log/decloud-${ROLE}-bootstrap.log` path.
  - Acceptance: each role's cloud-init has the same retry-loop shape (line counts within ±2 of each other across roles). All three hard-fail on 10-minute timeout. None has any fallback path. None has any self-creation. Relay's cloud-init no longer references `__WIREGUARD_PRIVATE_KEY__`.
  - **Note:** This task changes runtime behavior, unlike P0.3a/b/c which preserve it. Lands after the structural strips so any regression is isolated to this change.
  - **Done 2026-05-03.** All three role YAMLs updated; BlockStore binary edited (`loadOrCreateIdentity` → `loadIdentity`, `loadIdentityFromNodeAgent` and `cacheIdentityToDisk` removed; `defaultGateway` retained for non-identity runtime queries). Composition simulation passes for all three (DHT/BlockStore: 11 write_files / 15 runcmd against base-system-mesh; relay: 9 write_files / 40 runcmd against base-system). `__WIREGUARD_PRIVATE_KEY__` placeholder eliminated from relay cloud-init (one functional reference removed; two remaining are documentation comments). `__WIREGUARD_PUBLIC_KEY__` retained in `relay-metadata.json` as documentary metadata per option-1 decision. **Phase 1 implication:** `RelayWireGuardPrivateKeyResolver` no longer needed for cloud-init substitution; resolver inventory shrinks. **Pre-commit verification by user:** `go mod tidy && go build ./...` from `system-vms/blockstore/src/` (binary needs to compile); ideally smoke-test deploy (cloud-init writes file, binary reads file, role comes up).

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

- [x] **P1.1** — `TemplateVariable` model in Shared (design §2.2).
  - File: `src/Shared/Models/TemplateVariable.cs`.
  - Acceptance: compiles, JSON round-trip preserves all fields, BSON serialization works (test against MongoDB doc).
  - **Done 2026-05-03.** Sealed class with `init`-only properties; `Name` and `Kind` required, `Scope` defaults to `Restart` (conservative), `DefaultValue`/`Required` for statics, `ResolverKey` optional override. Two enums: `VariableKind { Static, Dynamic }` and `WatcherScope { Noop, Reload, Restart }`, both decorated with `[JsonStringEnumConverter]` so JSON round-trip uses string names (not ints) — keeps API output and MongoDB documents human-readable.

- [x] **P1.2** — Add `Variables: List<TemplateVariable>` to both template models.
  - Files: `src/Orchestrator/Models/VmTemplate.cs`, `src/Shared/Models/SystemVmTemplate.cs`.
  - Default empty list. Existing MongoDB documents must deserialize without error.
  - Acceptance: load existing template documents from MongoDB; verify no exception. Persist a template with `Variables` populated; reload; fields present.
  - **Done 2026-05-03 (drafted as patch).** Patch documented at `patches/P1.2_template_variables_field.md` — single field added to each of the two model classes, with usings, default value, and XML docs. User applies the patch directly to the source files (one-line additions in two existing classes). Migration safety: `= new()` default ensures pre-migration documents deserialize without error.

### 5.2 TemplateComposer

- [x] **P1.3** — Implement `TemplateComposer` (design §5 Phase 1.2).
  - File: `src/Orchestrator/Services/CloudInit/TemplateComposer.cs`.
  - String-based section detection (no YAML parser dependency).
  - Merge rules: `packages` union/dedup; `write_files`/`bootcmd`/`runcmd` base-first then role; scalar keys role-wins; `#cloud-config` header emitted once.
  - Unit tests: base-only, role-only, both packages, both runcmd, role-hostname-wins, both-have-hostname, neither-has-section.
  - Acceptance: all unit tests pass. `Compose(base-system.yaml, dht/cloud-init.yaml)` produces structurally-valid cloud-init (manual inspection acceptable for this milestone; full integration test in Phase 3).
  - **Done 2026-05-03** (out-of-order — P1.3 implemented before P1.1 and P1.2 because TemplateComposer has no dependency on the model layer; works on raw YAML strings). Implementation in `src/Orchestrator/Services/TemplateComposer.cs` (C#) plus a Python reference implementation in `test/Orchestrator.Tests/Services/template_composer_reference.py` for cross-validation. Validated against all three system-VM compositions: DHT/BlockStore (5 base + 6 role write_files = 11; 2 base + 13 role runcmd = 15; 8 packages), Relay (0 base + 9 role write_files = 9; 2 base + 38 role runcmd = 40; 9 packages with dedup of base 8 + role 1). All compose to valid YAML. Two characteristics worth knowing: (1) inter-section comments travel with the preceding section (string-based parser limitation, acceptable); (2) packages list-union assumes scalar items only — compound items (e.g., `- {name: foo, version: bar}`) would fall through to `EmitListConcat` semantics. Path note: implemented under `src/Orchestrator/Services/TemplateComposer.cs` rather than `src/Orchestrator/Services/CloudInit/TemplateComposer.cs` per plan — folder structure decision deferred to P1.4 when we know how many CloudInit-related classes will live alongside.

### 5.3 Resolver registry and renderer

- [x] **P1.4** — `IVariableResolver`, `IVariableResolverRegistry`, `ResolutionContext`, `VariableResolverRegistry` (design §3.1).
  - Files in `src/Orchestrator/Services/CloudInit/`.
  - Registry is DI singleton. Lookup by `(ResolverKey, Kind)`.
  - Acceptance: register a synthetic resolver, look it up, dispatch a `ResolveAsync` call. Missing resolver returns null (not throws — caller decides).
  - **Done 2026-05-03.** Four files in `src/Orchestrator/Services/CloudInit/`: `IVariableResolver.cs` (interface + `ResolutionContext` record co-located — they're tightly coupled), `IVariableResolverRegistry.cs` (interface), `VariableResolverRegistry.cs` (singleton impl), `CloudInitServiceCollectionExtensions.cs` (DI helper). Registry is built from `IEnumerable<IVariableResolver>` via constructor; duplicates detected at construction with a clear `InvalidOperationException` (better than the cryptic ArgumentException that ToDictionary throws). Lookup returns `IVariableResolver?` — null on miss; the caller (renderer) decides between user input, default, or throw. Diagnostic accessor `RegisteredKeys` exposes registered (Key, Kind) pairs for startup logging. **Program.cs wire-up patch** at `patches/P1.4_program_cs_registration.md` — a single `builder.Services.AddVariableResolverRegistry()` call. User applies the patch directly.

- [x] **P1.5** — Implement `CloudInitFormatting` helper.
  - File: `src/Orchestrator/Services/CloudInit/CloudInitFormatting.cs`.
  - Methods: `BuildSshKeysBlock`, `BuildPasswordBlock`, `IndentForYaml`. Lift logic from `LibvirtVmManager` and current `VmService` — do not reimplement.
  - Acceptance: per-method unit tests covering empty inputs, populated inputs, multi-line inputs.
  - **Done 2026-05-03.** Three pure-function methods, no I/O, no DI. `BuildSshKeysBlock` has two overloads — `IEnumerable<string>` (canonical) and `string?` (newline-separated, matches existing call sites). Empty inputs return YAML comment strings (`# No SSH keys provided`, `# No password authentication`) so the placeholder substitution always yields valid YAML. **Behavior change vs. lifted source:** `IndentForYaml` indents only lines 2+ (line 1 is bare since the placeholder column provides line 1's indent). The original `LibvirtVmManager` code prefixed every line with the indent, producing asymmetric output (line 1 at column 12, line 2+ at column 6) — masked for single-line CA keys by YAML auto-detection but breaks block scalars for multi-line inputs. Documented as a deliberate fix in §2 below. Validated against six test cases (single/multi/empty SSH keys, password present/absent, single/multi-line CA key).

- [x] **P1.6** — Platform-common resolvers (design §4.2 layout).
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/PlatformCommon/*.cs`. One per resolver:
    - `VmIdResolver`, `VmNameResolver`, `HostnameResolver`, `NodeIdResolver`
    - `OrchestratorUrlResolver` (with co-located guard — see design §11 Q1)
    - `CaPublicKeyResolver`, `SshAuthorizedKeysBlockResolver`, `PasswordConfigBlockResolver`
    - `ArtifactUrlResolver`, `ArtifactSha256Resolver`
    - `VariableScopesBlockResolver` (renders the `VAR=scope` block from `template.Variables`)
    - `TimestampResolver`
  - All `Kind = Static` for these.
  - Acceptance: each resolver has a unit test using a synthetic `ResolutionContext`. Resolvers throw with clear messages on missing required input (no empty-string returns).
  - **Done 2026-05-03.** 15 resolvers under `Resolvers/PlatformCommon/` plus the `PlatformCommonResolvers.AddPlatformCommonResolvers()` DI grouping helper. **Differences from plan:** (1) `ArtifactUrlResolver` and `ArtifactSha256Resolver` were SKIPPED — these placeholders use `${ARTIFACT_URL:name}` parameterized syntax (not `__VARNAME__`) and don't fit the single-key-per-resolver `IVariableResolver` shape. The artifact substitution will continue to use the existing `SubstituteArtifactVariables` pathway in P1.7 (renderer); decision-log entry below. (2) Three resolvers added beyond the plan to cover placeholders found in actual base files: `DeCloudRoleResolver` (`__DECLOUD_ROLE__`), `HostMachineIdResolver` (`__HOST_MACHINE_ID__`), `PublicIpResolver` (`__PUBLIC_IP__`). (3) Three more added to cover password-related placeholders: `SshPasswordAuthResolver` (`__SSH_PASSWORD_AUTH__`), `AdminPasswordResolver` (`__ADMIN_PASSWORD__`), and the explicit `PasswordConfigBlockResolver` for `__PASSWORD_CONFIG_BLOCK__`. **Coverage audit:** all 15 placeholder names that appear in `base-{tenant,system,system-mesh}.yaml` are covered. Role-specific (`DHT_*`, `BLOCKSTORE_*`, `RELAY_*`) and mesh-specific (`WG_*`, `WIREGUARD_*`, `ORCHESTRATOR_IP/PORT/PUBLIC_KEY`) placeholders are intentionally deferred to their respective Phase 3 resolver groups.

- [x] **P1.7** — `CloudInitRenderer` (design §3.2).
  - File: `src/Orchestrator/Services/CloudInit/CloudInitRenderer.cs`.
  - Iterate `template.Variables` where `Kind == Static`; resolve each; substitute. Validate.
  - Acceptance: synthesize a template with 5 statics; render; all substituted, validator passes. Missing resolver throws. Required user input missing throws.
  - **Done 2026-05-03.** Three files: `Interfaces/CloudInit/ICloudInitRenderer.cs` (interface), `Interfaces/CloudInit/ICloudInitValidator.cs` (forward declaration for P1.8), `Services/CloudInit/CloudInitRenderer.cs` (implementation). Two-pass design: Pass 1 substitutes `__VARNAME__` via resolver registry with fallback chain (resolver → user input → default → throw); Pass 2 substitutes `${ARTIFACT_URL/SHA256:name}` via `template.Artifacts` filtered by `ctx.TargetArchitecture` (algorithm lifted from `SystemVmReconciler.SubstituteArtifactVariables` so node-side and orchestrator-side outputs match during cutover). Pass 3 validates if `ICloudInitValidator` is registered (null until P1.8 — silently skipped). Duplicate variable names within a template throw at render time. `Required: true` with no resolver/input/default throws with explicit message naming the offending variable. `Required: false` with no fallback also throws — empty-string silent fallback would corrupt rendering. DI extension method `AddCloudInitRenderer()` added alongside the existing `AddVariableResolverRegistry()`. Patch document at `patches/P1.7_program_cs_renderer.md` for `Program.cs` wire-up. **Mental simulation against a DHT template traced cleanly** through both passes; documented in session log.

- [x] **P1.8** — `CloudInitValidator` (design §3.4).
  - File: `src/Orchestrator/Services/CloudInit/CloudInitValidator.cs`.
  - Three failure modes: unresolved static, undeclared placeholder, dynamic-in-wrong-form.
  - Acceptance: unit tests for each failure mode. Clean output passes.
  - **Done 2026-05-03.** Implementation matches design §3.4 verbatim. **All three checks run before any throw** — the exception message has separate `[Unresolved statics]`, `[Dynamics in wrong form]`, and `[Undeclared placeholders]` sections, listing every offender across all three buckets so a template with multiple kinds of error gets fixed in one round trip rather than playing whack-a-mole. The wrong-form section additionally suggests the correct `$VARNAME` shell-source form. DI registration via `AddCloudInitValidator()` extension method; once registered, `CloudInitRenderer`'s optional validator dependency switches from null to this instance and Pass 3 (validation) activates automatically — no renderer code change needed. Patch document at `patches/P1.8_program_cs_validator.md`. **Validated against six test cases:** (1) clean output passes; (2) unresolved static throws naming the variable; (3) undeclared placeholder throws naming the variable; (4) dynamic in wrong form throws with `$VARNAME` suggestion; (5) all three modes simultaneously emit a single throw listing all three; (6) legitimate `$VARNAME` shell references don't trigger false positives — the regex only matches `__VARNAME__` form.

### 5.4 Node record and obligation field changes

- [x] **P1.9** — Add `Node.SshCaPublicKey` field and registration plumbing (design §5 Phase 1.6).
  - Files: `src/Orchestrator/Models/Node.cs` (add field, in `NodeRegistrationRequest` too), `src/Orchestrator/Services/NodeService.cs` (stamp on register), `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs` (read `/etc/ssh/decloud_ca.pub`, send in registration payload).
  - Acceptance: a node re-registers; orchestrator's stored `Node.SshCaPublicKey` matches the file on the node.
  - **Done 2026-05-03** (model field already done in P1.6 build-error fixes; plumbing split into four patches that land together). Patches: `P1.9-A` extends `NodeRegistrationRequest` (orchestrator wire DTO), `P1.9-B` extends `NodeRegistration` (node-agent wire DTO — same JSON shape), `P1.9-C` extends `OrchestratorClient.RegisterWithPendingAuthAsync` to read `/etc/ssh/decloud_ca.pub` and populate the field with non-fatal failure handling, `P1.9-D` extends `NodeService.RegisterNodeAsync` to stamp `request.SshCaPublicKey ?? existingNode?.SshCaPublicKey` (mixed-version safe — pre-P1.9 agents don't null out previously-stamped values). All four must ship in one coordinated commit; landing only some leaves the orchestrator with null `SshCaPublicKey` and any tenant deploy that uses `__CA_PUBLIC_KEY__` will fail at render time with the documented message pointing at the gap.

- [x] **P1.10** — Add `SystemVmObligation.VmName` field (design §11 last row).
  - File: `src/Orchestrator/Models/SystemVmObligation.cs`. Nullable string.
  - Acceptance: existing obligation documents load without error.
  - **Done 2026-05-03** (model field already landed inline as P1.6 build-error fix). Field is `public string? VmName { get; set; }` — null on legacy obligations, populated at obligation creation by P1.11.

- [x] **P1.11** — Assign `VmId` and `VmName` at obligation creation.
  - File: `src/Orchestrator/Services/SystemVm/SystemVmObligationService.cs`.
  - In `EnsureObligationsAsync` and `TryAdoptExistingVmAsync`: when creating a new `Pending` obligation, set `VmId = Guid.NewGuid().ToString()` and `VmName = $"{canonicalRole}-{node.Id[..8]}"`.
  - Acceptance: new obligation documents in MongoDB carry both fields. Existing obligations are untouched (backwards-compatible nullables).
  - **Done 2026-05-03 (drafted as patch).** Patch at `patches/P1.11_obligation_vmid_vmname_assignment.md`. **Scope discovery:** the plan listed two methods but there are actually **five** `new SystemVmObligation` sites across **two** files — `NodeService.RegisterNodeAsync` has two (fresh registration + re-registration backfill), `SystemVmObligationService.TryAdoptExistingVmAsync` has three (no-existing-VM, stale-VM-ID-fallback, plus the existing Active/Failed/Deploying paths that need `VmName` added). All five must be updated to satisfy the acceptance criterion. **Helper added:** `SystemVmRoleMap.ToVmName(role, nodeId)` consolidates the naming convention (single source of truth, defensive guard for short test-fixture node IDs). For adopted VMs, prefer the existing `vm.Name` over a synthesized one — falls back to synthesis only when `vm.Name` is unset. **Backfill deliberately deferred** — pre-existing obligations carry null values; resolvers' null-guards throw with clear messages if hit. Forward-compatible with P3.1.5 which will invert the VmId flow direction (orchestrator → node-agent instead of the current node-agent → orchestrator-via-adoption). No live behavior change in P1.11; values written but unconsumed until P3.1.5.

**Phase 1 done when:** All P1.x checked. Unit-test coverage on every new class. No live behaviour changed — system VMs still on legacy `LibvirtVmManager` substitution path; tenant VMs still on legacy `VmService` variable-building path.

---

## §6. Phase 2 — Tenant VM cutover

Tenant VMs (general + marketplace) move to the new pipeline. System VMs stay on legacy paths.

- [x] **P2.1** — `GeneralVmTemplateSeeder`.
  - File: `src/Orchestrator/Services/Tenant/GeneralVmTemplateSeeder.cs`.
  - Composes `base-tenant.yaml + tenant-vms/general/cloud-init.yaml` via `TemplateComposer`.
  - Declares `Variables` list for the tenant set: VM_ID, HOSTNAME, NODE_ID, CA_PUBLIC_KEY, ORCHESTRATOR_URL, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, ADMIN_PASSWORD, ARTIFACT_URL:* / ARTIFACT_SHA256:* for `decloud-agent`/`general-api-py`/`general-index-html`, TIMESTAMP, VARIABLE_SCOPES_BLOCK.
  - Revision-aware upsert (same pattern as `SystemVmTemplateSeeder`).
  - Acceptance: `platform-general` visible in MongoDB after orchestrator startup. `Variables` populated.
  - **Done 2026-05-03.** Three patches: `GeneralVmTemplateSeeder.cs` (new file), `P2.1-A_template_seeder_service_wire.md` (constructor + SeedAsync call), `P2.1-B_program_cs_di_registration.md` (typed `HttpClient` registration). Plus a manual-procedure document `P2.1-C_compute_artifact_constants.md` covering the `compute-artifact-constants.sh` step in `DeCloud.Builds/tenant-vms/`. **Variables actually declared = 9 (not 12 as the plan listed).** Audit of the placeholders in the actual composed cloud-init found: VM_ID, VM_NAME, HOSTNAME, ORCHESTRATOR_URL, CA_PUBLIC_KEY, SSH_AUTHORIZED_KEYS_BLOCK, PASSWORD_CONFIG_BLOCK, ADMIN_PASSWORD, SSH_PASSWORD_AUTH. The plan-listed extras (NODE_ID, TIMESTAMP, VARIABLE_SCOPES_BLOCK) aren't currently used in `base-tenant.yaml` or `tenant-vms/general/cloud-init.yaml`. ARTIFACT_URL/SHA256 references aren't declared in `Variables` — they're substituted via the renderer's Pass 2 from `template.Artifacts` (matching the design's two-pass split, P1.6 decision log). **Composition done at startup, not pre-composed offline.** The seeder fetches both layers from DeCloud.Builds and runs `TemplateComposer.Compose` at startup — exercises the composer in production and avoids storing a duplicated composed file in the repo. **Failure modes are non-fatal at startup.** Network failure, compose-throw, SHA256 mismatch, and validator-detected variable drift all surface clearly without preventing orchestrator boot. **Live behavior change so far: zero.** Tenant deploys still go through legacy `LibvirtVmManager.PrepareCloudInitVariables`. The new template sits in MongoDB with declared `Variables`, ready for P2.2 (`VmService.AssignVmToNodeAsync` cutover) to call `CloudInitRenderer.RenderAsync` against it.

- [x] **P2.2** — `VmService.CreateVmAsync` assigns `TemplateId = platform-general` for general VMs.
  - Per design §5 Phase 2.2.
  - Acceptance: a VM created with no `TemplateId` and `VmType == General` lands in MongoDB with `TemplateId = platform-general`.
  - **Done 2026-05-03 (drafted as patch).** Patch at `patches/P2.2_template_id_default.md`. **Slug-vs-id discovery during drafting:** `request.TemplateId` flows directly into `_templateService.GetTemplateByIdAsync(...)` which is a strict Mongo `_id` lookup. Setting `request.TemplateId = "platform-general"` (the slug) would make the downstream `else if (!string.IsNullOrEmpty(request.TemplateId))` branch's lookup fail. **Resolution:** the default block resolves the slug via `GetTemplateBySlugAsync("platform-general")` first, then sets `request.TemplateId = general.Id` (the actual `_id`). **Record-mutation idiom:** `CreateVmRequest` is a positional record with init-only props; `request = request with { TemplateId = general.Id };` rebinds the local parameter. **Seeder-failure fallback:** if `platform-general` isn't in MongoDB (P2.1 seeder failed at startup or didn't run yet), log a warning and fall through to the legacy node-side cloud-init path rather than throwing — Phase-2 conservatism, removed in Phase 4 cleanup. **Trigger conditions (all four):** `!isSystemVm` (defensive), `request.VmType == VmType.General`, `string.IsNullOrEmpty(request.TemplateId)`, `string.IsNullOrEmpty(request.CustomCloudInit)`. Marketplace deploys (always set `TemplateId`), custom-cloud-init deploys, Inference VMs, and system VMs are all unaffected. **First P2.x with live behavior change** — General tenant VMs that previously had `TemplateId = null` now have it set, and `vm.Spec.UserData` is populated from `platform-general.CloudInitTemplate` (with `__VARNAME__` placeholders intact); legacy node-side substitution still produces the final cloud-init until P2.4 cuts over to `CloudInitRenderer.RenderAsync`. Ingress, GPU mode, and other template-flow side effects (handled by the existing `else if` branch) now apply to previously-defaulted General VMs.

- [ ] **P2.3** — Tenant resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/Tenant/*.cs`.
  - `DecloudVmIdResolver`, `DecloudPasswordResolver`, `DecloudDomainResolver`, etc. — one per platform-bound tenant variable.
  - For now: ingress URL etc. that depend on runtime state are out of scope; existing marketplace templates without `Variables` declarations use the legacy compatibility path.
  - Acceptance: per-resolver unit tests.

- [x] **P2.4** — `VmService.AssignVmToNodeAsync` uses `CloudInitRenderer`.
  - Replace existing variable-building with: build `ResolutionContext`, call `_cloudInitRenderer.RenderAsync(template, ctx, ct)`.
  - For templates with no `Variables` declarations: legacy compatibility path — treat all referenced placeholders as statics, resolve via platform-common + `DefaultEnvironmentVariables`.
  - Acceptance: fresh general VM deploys via new pipeline. Cloud-init has no unresolved placeholders. `decloud-agent` running, welcome server reachable, SSH CA login works.
  - **Done 2026-05-03 (drafted as two patches).** Split into A + B during the testing-prep audit when a config gap surfaced. **P2.4-A (`patches/P2.4-A_orchestrator_url_config.md`)** adds `OrchestratorClient.BaseUrl` to both `appsettings.*.json` files and the `install.sh` env-file generator — prerequisite plumbing. The orchestrator never previously needed to know its own public URL (it was the *destination* of HTTP calls, not the originator); the renderer's `OrchestratorUrlResolver` is the first consumer. Key choice mirrors the node-agent's existing `OrchestratorClient.BaseUrl` convention. **P2.4-B (`patches/P2.4-B_render_cloudinit_via_renderer.md`) — the cutover.** STEP 6.5 of `TryScheduleVmAsync` (the actual private method — plan calls it `AssignVmToNodeAsync` but the codebase doesn't have that name) splits into two branches by `template is { Variables.Count: > 0 }`. **New pipeline branch** builds `ResolutionContext` from current scope and calls `_cloudInitRenderer.RenderAsync`. **Legacy branch** preserves the existing `${VARNAME}` substitution code verbatim for marketplace templates without declared Variables — Phase 4 cleanup removes it. **Constructor injection:** `IConfiguration` and `ICloudInitRenderer` added; both wire automatically through existing DI. **Three-layer `UserSuppliedStatics`** (precedence-ordered): `template.DefaultEnvironmentVariables` (Layer 1), env vars from `vm.Labels["custom:cloud-init-vars"|"template:cloud-init-vars"]` (Layer 2, custom > template per legacy parity), and platform secrets — `ADMIN_PASSWORD = password`, `SSH_PUBLIC_KEYS = sshPublicKey` (Layer 3, wins). **SSH key gotcha resolved:** STEP 5's local `sshPublicKey` (built from `vm.Spec.SshPublicKey ?? owner.SshKeys`) is never stamped back to `vm.Spec.SshPublicKey` — `SshAuthorizedKeysBlockResolver`'s primary path finds null. Layer 3's `userSupplied["SSH_PUBLIC_KEYS"] = sshPublicKey` populates the resolver's fallback. **Render failures throw, don't silent-fall-back.** The validator's three-bucket message (unresolved statics / undeclared placeholders / dynamics-in-wrong-form) points at the specific gap; silent fallback would mask drift indefinitely. **Migration path unchanged:** `BackgroundServices.ScanMigratingVmsAsync` ships `UserData = null` (out of scope for P2.4). **System VMs unaffected:** they go through `SystemVmReconciler`, not `TryScheduleVmAsync`. **No new DI registrations needed** — `AddCloudInitRenderer()` (P1.7) and the framework's implicit `IConfiguration` cover everything. **Apply order: P2.4-A first, then P2.4-B.** Reversed order produces a transient throw on the first General deploy (recoverable: apply A, restart). **Live behavior change:** General tenant deploys (defaulted to `platform-general` by P2.2) now flow through the orchestrator-side renderer; the rendered string ships in the `CreateVm` command's `UserData`; the node-agent's `__VARNAME__` substitution becomes a no-op for these VMs (nothing left to substitute) but stays as a defensive safety net through Phase 2/3, removed in Phase 4.

- [x] **P2.5** — *[REFRAMED 2026-05-04: extract `IVmDeploymentPipeline` + verify safety nets in same redeploy.]* Unified node-side deploy pipeline + EnsureQemuGuestAgent / GPU-shim verification.
  - **Architectural extraction (done):** Six-file patch in `patches/P2.5_unified_node_deploy_pipeline.md`. NEW `IVmDeploymentPipeline.cs` interface + `VmDeploymentPipeline.cs` impl in node-agent. DI registration in `Program.cs`. `SystemVmReconciler.ActCreateAsync` and `CommandProcessorService.HandleCreateVmAsync` both refactored to call `_pipeline.DeployAsync(spec, artifacts, password, ct)` instead of the local prefetch + manager-dispatch shapes they each had. Orchestrator-side `VmService.cs` adds `Artifacts = template?.Artifacts ?? []` to CreateVm payload so the node has the artifact list to feed to the pipeline. **Verified in repo** post-apply — all six files match patch spec.
  - **Smoke-test verification (DONE — closed by post-CRLF-fix redeploys):** Per design §5 Phase 2.5, confirm `EnsureQemuGuestAgent` + GPU-shim safety nets still run when `CloudInitUserData` comes from the new pipeline. These are post-substitution concerns in `LibvirtVmManager.CreateCloudInitIso`; they continue to fire because the new pipeline's only change is *where* prefetch and dispatch live, not what `LibvirtVmManager.CreateCloudInitIso` does post-CloudInitUserData. Verified end-to-end on the 2026-05-04 testbed reset: General tenant VM and a marketplace VM (web-proxy-browser) both deployed cleanly via the new pipeline, with the libvirt domain confirming `qemu-guest-agent` is present in the rendered ISO and the agent socket is reachable from the host.
  - **Cosmetic follow-up (DONE 2026-05-04):** `_artifactCache` field in `SystemVmReconciler` removed. Field + ctor parameter + assignment, three lines.
  - **Out of scope, deferred to Phase 4:** (a) synthetic-template path for `request.CustomCloudInit`, (b) cross-orchestrator `IVmDeployer` that pushes rendered cloud-init for every VM type and removes the SQLite render-on-node path for system VMs. See §2 / 2026-05-04 / P2.5 for full rationale.
  - **Done 2026-05-04 (extraction + verification + cleanup all closed)** — patch applied and repo-verified; smoke-test verification closed by the post-CRLF-fix redeploys; `_artifactCache` cosmetic cleanup applied. Phase 2 complete.

- [x] **P2.6** — Marketplace template smoke test.
  - Pick one existing marketplace template. Deploy it. Verify legacy compatibility path works (template has no `Variables` list; render still succeeds).
  - Acceptance: marketplace VM boots, application reachable.
  - **Done 2026-05-04.** Web-proxy-browser marketplace template deployed cleanly on the 2026-05-04 testbed reset. Template has no declared `Variables`, exercising the legacy-compat branch in `VmService.TryScheduleVmAsync` (P2.4-B). VM boots, application reachable end-to-end. The legacy `${VARNAME}` substitution path remains in place as a defensive net through Phase 3; Phase 4.1 deletes it once all roles are on the new pipeline.

**Phase 2 done when:** All P2.x checked. Fresh general VM and one marketplace VM deploy successfully via new pipeline. System VMs unaffected.

---

## §7. Phase 3 — System VM cutover, one role at a time

Order: **relay → DHT → BlockStore.** Each role is a complete cutover with its own production observation window before the next starts.

### 7.1 Sub-phase 3.1 — Relay cutover (also lands shared system-VM infrastructure)

This sub-phase brings up the generic environment endpoint and the reconciler simplification, since both are needed by all three roles. Subsequent role cutovers don't repeat these.

- [x] **P3.1.1** — Update `SystemVmTemplateSeeder` for `system-relay`.
  - Compose cloud-init via `TemplateComposer.Compose("base-system", "relay")`.
  - Declare `Variables`: full static set + dynamics per design §2.3 (relay has near-empty dynamics).
  - Bump `Revision`.
  - Acceptance: `system-relay` template reflects new content; `Variables` populated; revision incremented.
  - **Done 2026-05-04.** `BuildRelayVariables()` replaces single `WIREGUARD_PUBLIC_KEY` entry with 21-entry full static set. `FetchCloudInitAsync` already calls `TemplateComposer.Compose` (landed in DEPLOY-FIX). Coordinated commit with P3.1.2 — declaring Required statics without registering resolvers throws at startup. See §2 / 2026-05-04 / P3.1.1+P3.1.2 for design clarification on RELAY_REGION/RELAY_CAPACITY (Static, not Dynamic).

- [x] **P3.1.2** — Relay-specific resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/SystemVm/Relay/*.cs`.
  - Statics: `OrchestratorPublicKeyResolver`, `OrchestratorIpResolver`, `OrchestratorWgPortResolver`, `RelaySubnetResolver`, `RelayRegionResolver`, `RelayCapacityResolver`. (`RelayWireGuardPrivateKeyResolver` omitted — placeholder removed in P0.9.)
  - No Dynamics: RELAY_REGION and RELAY_CAPACITY are Static per design §2.3 text and P3.1.4 note. Plan's "Dynamics" label was a misnomer.
  - Acceptance: per-resolver unit tests.
  - **Done 2026-05-04.** Six resolvers in `Resolvers/SystemVm/Relay/`. `OrchestratorPublicKeyResolver` injects `IWireGuardManager` (reads from `/etc/wireguard/orchestrator-public.key` via `GetOrchestratorPublicKeyAsync()` — NOT in appsettings). Registered in `SystemVmResolvers.AddSystemVmResolvers()`. Coordinated commit with P3.1.1.

- [x] **P3.1.3** — Generic environment endpoint on node agent.
  - Files: `src/DeCloud.NodeAgent/Controllers/ObligationEnvironmentController.cs`, `src/DeCloud.NodeAgent.Core/Models/ObligationEnvironment.cs`.
  - Endpoint: `GET /api/obligations/{role}/environment`.
  - Returns `{ values, scopes, generation }` per design §3.3.
  - Acceptance: endpoint returns valid JSON for relay role. `generation` is deterministic SHA256 of values.
  - **Done 2026-05-04.** virbr0-enforced. Loads `SystemVmTemplate` from SQLite, filters Dynamic variables (none for relay), returns empty values/scopes + `generation = "44136fa355ba77b9"` (SHA256 of `{}`). Generation computed as `SHA256(sorted compact JSON)[..16]`. Node-side `IVariableResolver` mirror and relay-specific resolver files deferred to P3.2.3 — relay has zero dynamics; DHT's 7 dynamics are the first real consumers.

- [x] **P3.1.4** — Update `system-vms/relay/cloud-init.yaml` in `DeCloud.Builds`.
  - Use `__VARIABLE_SCOPES_BLOCK__` for the scope file write.
  - Add environment-fetch from `/api/obligations/relay/environment`. Write `/etc/decloud-relay/environment` and `/etc/decloud-relay/variable-scopes.conf`.
  - Install + enable `decloud-env-watcher.timer`.
  - Service unit uses `EnvironmentFile=/etc/decloud-relay/environment`.
  - Note: relay-api.py currently has values substituted directly into the .py at cloud-init time — for this sub-phase, keep that pattern; `RELAY_REGION` and `RELAY_CAPACITY` declared `Noop` reflects this (design §2.3).
  - Acceptance: composed cloud-init renders; relay VM boots; cloud-init log clean.
  - **Done 2026-05-04.** Two-repo patch (apply Orchestrator first). relay cloud-init v6.1→v6.2. Seeder amendment adds `VARIABLE_SCOPES_BLOCK` to `BuildRelayVariables()` (missed in P3.1.1), three watcher artifacts to relay artifact list, bumps `RelayTemplateRevision`. Identity-fetch runcmd already present from P0.9 — not re-added. `EnvironmentFile=-` with `-` prefix for optional-file safety. `__VARIABLE_SCOPES_BLOCK__` renders to `"# No dynamic variables declared"` for relay; watcher finds scope file, reads empty scope map, exits cleanly.

- [x] **P3.1.5** — `SystemVmReconciler.ActCreateAsync` becomes pure executor (design §5 Phase 3.1.6).
  - File: `src/DeCloud.NodeAgent.Infrastructure/Services/SystemVm/SystemVmReconciler.cs`.
  - Replaces current substitution logic. Reads cached rendered cloud-init from local SQLite. Builds `VmSpec` with `CloudInitUserData = template.CloudInitContent`. Labels reduced to `system-vm-role`, `system-vm-revision`, `node-arch`.
  - **NOTE:** Drop `SubstituteArtifactVariables`. `Guid.NewGuid()` retained (VmId inversion is Phase 3 cleanup, BLOCKSTORE-FIX §6).
  - Acceptance: relay deploys via new path. `LibvirtVmManager` step 5.5 relay substitutions are no-ops on rendered content. Legacy path dormant for relay.
  - **Done 2026-05-04.** Two changes: (1) `cloudInit = template.CloudInitContent` replaces `SubstituteArtifactVariables` call; (2) relay-specific label injection block removed (read `relaySubnet` from obligation state to feed `LibvirtVmManager` STEP 5.5 `__RELAY_SUBNET__` substitution — now handled by `RelaySubnetResolver` at orchestrator render time). `SubstituteArtifactVariables` method kept unreferenced — Phase 4.1 deletes it. Fix: removed stray patch instructions from comment block.

- [x] **P3.1.6** — Per-role acceptance for relay (design §5.3.2).
  - Fresh relay deploys cleanly. Cloud-init log has no unresolved placeholders.
  - `/etc/decloud-relay/environment` and `variable-scopes.conf` populated.
  - Watcher timer running.
  - Recovery test: `virsh destroy` + undefine; reconciler redeploys; identity preserved (same WireGuard pubkey).
  - Environment-drift test: mutate `node.Region` on orchestrator; within 60s, env file rewritten; service NOT restarted (Noop scope); verify by inspection.
  - 24-48h production observation. Document in §1 Notes.
  - **Done 2026-05-04.** All checks passed. Hotfix required: wrong symlink path (`/etc/systemd/timers.target.wants/` → `/etc/systemd/system/timers.target.wants/`) + missing `mkdir -p` guard. Fixed in relay v6.3 commit. Recovery test: same WireGuard pubkey confirmed after redeploy. Watcher fires every ~63s; endpoint polled (`gen=44136fa355b3678a`). Environment-drift test deferred — relay has no dynamics, Noop confirmed by scope audit (P3.1.7). See §2 / 2026-05-04 / P3.1.6.

- [x] **P3.1.7** — Scope-correctness audit for relay (design §7.6).
  - For each declared dynamic: confirm with binary owner (or via empirical test) that the declared scope matches actual binary behaviour.
  - Corrections bump template revision and trigger re-render.
  - Acceptance: audit results recorded in §2 (Decision log).
  - **Done 2026-05-04.** Binary owner confirmed: `RELAY_REGION` and `RELAY_CAPACITY` Static/Noop correct — baked into relay-metadata.json at render time; relay-api.py reads file; no dynamic reaction. No code change. Phase 4 relay-api.py refactor will revisit. See §2 / 2026-05-04 / P3.1.7.

### 7.2 Sub-phase 3.2 — DHT cutover

Reuses the environment-endpoint controller, watcher, and reconciler changes from 3.1. Only DHT-specific resolvers and template content are new.

- [x] **P3.2.1** — Update `SystemVmTemplateSeeder` for `system-dht`.
  - Compose via `TemplateComposer`.
  - Declare `Variables` per design §2.3 DHT table (statics + 7 dynamics with scopes).
  - Bump `Revision`.
  - Acceptance: template updated; `Variables` populated; revision incremented.
  - **Done 2026-05-04 (combined with P3.2.2 in one commit).** `BuildDhtTemplateAsync` switched from `BuildMeshSystemVmVariables()` (2 entries) to new `BuildDhtVariables()` with 24 entries: 12 platform-common statics + 5 DHT-specific statics + 7 dynamics. `DhtTemplateRevision` bumped 1→2. P3.2.2 verification caught three issues fixed in the same commit (see §2 / 2026-05-04 / P3.2.1+P3.2.2). Seven dynamics later re-classified to two by P3.2.4 (see below).

- [x] **P3.2.2** — DHT-specific resolvers.
  - Files: `src/Orchestrator/Services/CloudInit/Resolvers/SystemVm/Dht/*.cs` (orchestrator side).
  - Statics: `DhtListenPortResolver`, `DhtApiPortResolver`.
  - Dynamics (orchestrator-side, for diagnostics/preview rendering): `DhtAdvertiseIpResolver`, `DhtBootstrapPeersResolver`, `DhtRegionResolver`, `WgRelayEndpointResolver`, `WgRelayPubkeyResolver`, `WgRelayApiResolver`, `WgTunnelIpResolver`.
  - Acceptance: per-resolver unit tests.
  - **Done 2026-05-04 (no new code).** Coverage audit found all DHT statics already covered by existing P1.6 platform-common resolvers (12) + existing system-VM resolvers `WgDescriptionResolver` and `DeCloudRoleResolver` + `DefaultValue` for `DHT_LISTEN_PORT`="4001" and `DHT_API_PORT`="5080". `VariableScopesBlockResolver` (P1.6) iterates Variables and emits scope lines automatically. Orchestrator-side Dynamic resolvers deferred per KISS — render pipeline iterates only Statics; no preview/diagnostics consumer exists. `DhtRegionResolver` (Static) added later by P3.2.4 (see below). See §2 / 2026-05-04 / P3.2.1+P3.2.2.

- [x] **P3.2.3** — Node-side DHT dynamic resolvers.
  - Files: `src/DeCloud.NodeAgent/Services/CloudInit/Resolvers/SystemVm/Dht/*.cs`.
  - Resolve from `NodeMetadataService`, heartbeat-cached `RelayInfo`, etc.
  - Acceptance: environment endpoint returns correct values for DHT role.
  - **Done 2026-05-04.** Minimal implementation per KISS: `INodeRelayConfigProvider` + `NodeRelayConfigProvider` extracted from `ObligationStateController.GetWgConfig` (CGNAT vs co-located dispatch), `ObligationStateController` refactored to thin adapter (3-dep constructor; dead helpers deleted), `ObligationEnvironmentController.GetEnvironment` updated with inline `(role, name)` switch resolving 2 DHT dynamics. **No `INodeVariableResolver` mirror infrastructure built** — universe is small (2 entries for DHT, ~2 more for BlockStore in P3.3); resolver pattern would earn nothing at this scale. Typed `HttpClient` registered via `AddHttpClient<INodeRelayConfigProvider, NodeRelayConfigProvider>` in `Program.cs` (managed handler lifetime, no DNS staleness). `NodeRelayConfigProvider` lives in `DeCloud.NodeAgent.Infrastructure`; `Microsoft.Extensions.Http` package only needed in the web project. See §2 / 2026-05-04 / P3.2.3.

- [x] **P3.2.4** — Update `system-vms/dht/cloud-init.yaml` in `DeCloud.Builds`.
  - Same pattern as P3.1.4. Use `$VARNAME` shell references for dynamics in scripts/configs that previously used `__VARNAME__`.
  - Acceptance: DHT VM deploys via new path.
  - **Done 2026-05-04 (3-repo coordinated commit).** Cloud-init v8.1→v8.2: stripped 2 dynamics (`DHT_ADVERTISE_IP`, `DHT_BOOTSTRAP_PEERS`) from `dht.env`; parameterised port literals `DHT_LISTEN_PORT`/`DHT_API_PORT`; added `/etc/decloud-dht/variable-scopes.conf` (`__VARIABLE_SCOPES_BLOCK__`); added `/etc/decloud-dht/environment` stub; second `EnvironmentFile=-/etc/decloud-dht/environment` on `decloud-dht.service`; initial env-fetch runcmd; watcher timer enable with `mkdir -p` guard from P3.1.6 lesson; metadata version 8.1→8.2. Orchestrator: `BuildDhtVariables()` re-classified 4 WG_* and `DHT_REGION` from Dynamic to Static (see §2 / 2026-05-04 / P3.2.4 — wg-config-fetch.sh already runtime-manages WG_*; DHT_REGION is effectively Static per binary behaviour); new `DhtRegionResolver` (Static, reads `ctx.Node.Region`); `DhtTemplateRevision` 2→3. Result: DHT has **2 dynamics** through the new pipeline (down from design's 7) — `DHT_ADVERTISE_IP` (Restart) and `DHT_BOOTSTRAP_PEERS` (Noop, currently empty).

- [x] **P3.2.5** — Per-role acceptance for DHT.
  - Same shape as P3.1.6.
  - Environment-drift test: mutate `node.RelayInfo` on orchestrator (simulate failover); within 60s, watcher detects, service restarted, new endpoint in use; **no redeploy**. Mutate bootstrap peer set; env file rewritten; no service action (Noop scope).
  - 24-48h production observation.
  - **First pass 2026-05-04** marked done after 11-step procedural checklist but **missed three live-deploy bugs**: (1) YAML symlink continuation bug in both relay and dht cloud-init (PyYAML folding `\<newline>` to space); (2) `BuildSystemVmTemplate` in `NodeService.cs` omitting `Variables` from the node-side projection — node cache had 0 variables and the env endpoint returned 0 dynamics despite the orchestrator template having all 24; (3) merged-line bug in `dht/cloud-init.yaml` runcmd causing the watcher timer to never start at boot.
  - **Closed cleanly 2026-05-05** after all three bugs were fixed and a second platform reset produced a working DHT VM with no manual intervention. Reactor end-to-end proven via dual-tamper drift test (stale ENV_GENERATION + drifted DHT_ADVERTISE_IP value): watcher logged `changed: DHT_ADVERTISE_IP (restart) — restarting decloud-dht`; `decloud-dht.service` `ActiveEnterTimestamp` advanced to the same second as the watcher log; env file repaired to live values. Final acceptance state on second reset: relay env endpoint returns empty-dict SHA-prefix `44136fa355b3678a` (expected — zero dynamics); DHT env endpoint returns 2 dynamics with real values, scopes, and generation hash `9d38730139cc827f`; both watcher timers `active` at boot; node-side `system_template` cache shows 24 variables for DHT. **Lesson:** procedural checklists miss systemic bugs; substantive end-to-end validation (drift test under value-tamper + cache-content inspection) must be part of every acceptance going forward. See §2 / 2026-05-05 / P3.2.5 (re-acceptance) for full diagnosis and fix locations.

- [x] **P3.2.6** — Scope-correctness audit for DHT.
  - **Done 2026-05-04 (pulled forward into P3.2.4).** Audit findings recorded as P3.2.4 amendments rather than as a separate task: (1) Four WG_* variables (`WG_RELAY_ENDPOINT`, `WG_RELAY_PUBKEY`, `WG_RELAY_API`, `WG_TUNNEL_IP`) demoted from Dynamic Restart → Static `DefaultValue=""` because `wg-config-fetch.sh` already runtime-manages `/etc/decloud/wg-mesh.env` independently of the watcher pipeline. (2) `DHT_REGION` demoted from Dynamic Noop → Static because the binary has no mechanism to observe runtime region changes (consumed only by `dht-metadata.json` written once at boot). Future work: unifying WG_* through the watcher would require restructuring `wg-mesh-enroll.sh` and shared scripts — tracked as P4 candidate, not a Phase 3 blocker. See §2 / 2026-05-04 / P3.2.4 for full reasoning.

### 7.3 Sub-phase 3.3 — BlockStore cutover

Same shape as DHT (P3.2). Apply the same scope re-classifications surfaced
during P3.2.4 (4 WG_* + region-style variable as Static, not Dynamic) — the
underlying constraints are identical: `wg-config-fetch.sh` runtime-manages
WG_* via the existing `wg-mesh.env` path, and `blockstore-metadata.json`
is written once at boot with no binary re-read mechanism. Expected result:
BlockStore declares 2 true dynamics through the watcher pipeline,
`BLOCKSTORE_ADVERTISE_IP` (Restart) and `BLOCKSTORE_BOOTSTRAP_PEERS` (Noop).

**Blocked on:** `binaries/v1.1.0` release (see §1 action items).

- [ ] **P3.3.1** — Update `SystemVmTemplateSeeder` for `system-blockstore`.
  - Replace `BuildMeshSystemVmVariables()` call with new `BuildBlockStoreVariables()`
    method, mirroring `BuildDhtVariables()` shape: 12 platform-common statics +
    5 BlockStore-specific statics + 2 dynamics.
  - Apply scope re-classifications up front: WG_* and region-style vars as Static.
  - Bump `BlockStoreTemplateRevision` (currently 1) to 2.
  - Acceptance: orchestrator startup log shows `✓ System template 'system-blockstore' seeded (r2, ...)`.

- [ ] **P3.3.2** — BlockStore-specific resolvers (orchestrator).
  - Per the P3.2.2 coverage pattern, expect no new orchestrator resolver
    files needed: P1.6 platform-common resolvers cover all 12 statics; existing
    `WgDescriptionResolver` and `DeCloudRoleResolver` cover the rest.
    `BlockStoreRegionResolver` (new, Static, mirrors `DhtRegionResolver`) reads
    `ctx.Node.Region` if BlockStore declares its own region variable.
  - Acceptance: render of a fresh BlockStore template produces no `__VARNAME__`
    placeholders in the composed cloud-init output.

- [ ] **P3.3.3** — Node-side BlockStore dynamic resolution.
  - **Reuses P3.2.3's `NodeRelayConfigProvider`** — no new service needed. Extend
    `ObligationEnvironmentController.GetEnvironment`'s inline switch with two
    new cases:
    ```csharp
    ("blockstore", "BLOCKSTORE_ADVERTISE_IP")    => DeriveAdvertiseIp(relayConfig),
    ("blockstore", "BLOCKSTORE_BOOTSTRAP_PEERS") => "",
    ```
  - Acceptance: `curl http://192.168.122.1:5100/api/obligations/blockstore/environment`
    on a node with a BlockStore VM returns 2 dynamics with values + scopes +
    real generation hash.

- [ ] **P3.3.4** — Update `system-vms/blockstore/cloud-init.yaml`.
  - Mirror `dht/cloud-init.yaml` v8.2 changes: strip 2 dynamics from `blockstore.env`,
    parameterise port literals, add `/etc/decloud-blockstore/variable-scopes.conf`
    (`__VARIABLE_SCOPES_BLOCK__`), add `/etc/decloud-blockstore/environment`
    stub, second `EnvironmentFile=-/etc/decloud-blockstore/environment` on
    `decloud-blockstore.service`, initial env-fetch runcmd, watcher timer enable.
  - **Critical: avoid YAML pitfalls from P3.2.5 bugs.** (1) NO `\<newline>`
    plain-scalar continuations in runcmd — use single-line commands. (2) Each
    runcmd entry on its own line with leading `  - `; verify nothing got
    concatenated by editor whitespace handling. (3) Bump version 4.x → 4.x+1
    in both header and `blockstore-metadata.json`.
  - Acceptance: cloud-init log clean on fresh deploy; `cat composed-userdata.yaml`
    shows correctly-formed runcmd block with each `ln -sf` and `systemctl start`
    on its own line.

- [ ] **P3.3.5** — Per-role acceptance for BlockStore.
  - **Substantive validation required**, not just procedural file-existence
    checks. Per the lesson from P3.2.5 (see §2 / 2026-05-05 / P3.2.5 re-acceptance):
    1. `sqlite3 /var/lib/decloud/vms/obligation-state.db "SELECT json_extract(template_json, '$.variables') FROM system_template WHERE role='blockstore'" | jq '. | length'`
       — **must return >= 17** (12 platform statics + ~3-5 BlockStore statics + 2 dynamics).
       Bug 2 from P3.2.5 (Variables omitted from node-side projection) was a
       systemic issue that would re-occur if regression-introduced.
    2. `curl http://192.168.122.1:5100/api/obligations/blockstore/environment`
       must return both dynamics with non-empty values, real generation hash.
    3. **Dual-tamper drift test** exercising the Restart scope: corrupt both
       `ENV_GENERATION` AND `BLOCKSTORE_ADVERTISE_IP` value in
       `/etc/decloud-blockstore/environment`, wait one watcher tick, verify
       `journalctl -t decloud-env-watcher` logs `changed: BLOCKSTORE_ADVERTISE_IP
       (restart) — restarting decloud-blockstore` and `decloud-blockstore.service`
       `ActiveEnterTimestamp` advances.
    4. Recovery test: destroy + redeploy preserves peerId from obligation state.
    5. wg-config endpoint regression check (same wire format and 200/202
       semantics as before — `NodeRelayConfigProvider` is shared with DHT,
       so a regression here would have already broken DHT).
  - Acceptance: all five checks pass on a fresh BlockStore deploy after a
    second platform reset (no manual intervention).

- [ ] **P3.3.6** — Scope-correctness audit for BlockStore.
  - Likely pulled forward into P3.3.1 / P3.3.4 just like DHT's P3.2.6 was
    pulled into P3.2.4. Document the audit findings as decision-log entries
    rather than as separate code commits.
  - Acceptance: every variable in `BuildBlockStoreVariables()` has a
    documented consumer + observability + scope rationale either in the
    seeder comment or in §2 of this plan.

**Phase 3 done when:** All three roles deploy via new path, all three audits complete, all three recovery + drift tests pass, full release cycle observed without regressions.

---

## §8. Phase 4 — Cleanup

**Status: COMPLETE (2026-05-07).** All P4.x done. Migration closed.

Originally gated by "all three roles on the new path for one full release cycle without regressions". That release cycle elapsed during the closing session arc; all three system VM roles plus general tenant + marketplace template VM all validated end-to-end. Phase 4 then executed in a single bold-cutover session with the user explicitly accepting the risk of bypassing the slower telemetry-first observation period.

- [x] **P4.1** — Delete `LibvirtVmManager.PrepareCloudInitVariables` STEP 5.5/5.6/5.7 (147 lines) and replace with breadcrumb comment. Stale comments in merged-userdata branch and leak-detection LogWarning updated. Closed 2026-05-06.
- [x] **P4.2** — Delete `MergeCustomUserDataWithBaseConfig` (~145 lines). Call site collapses to direct `cloudInitYaml = spec.CloudInitUserData;` assignment. Defensive variables-Replace pass and leak detector retained as safety nets. Closed 2026-05-06.
- [x] **P4.3** — Delete `CloudInitTemplateService` entirely. else branch in `CreateCloudInitIsoAsync` replaced with fast-fail `throw new InvalidOperationException(...)`. `_templateService` field/parameter/assignment removed from `LibvirtVmManager`. DI registration removed from `Program.cs`. Risk accepted: bold cutover instead of telemetry-first observation. Closed 2026-05-06.
- [x] **P4.4** — Delete `CloudInit/Templates/` and `decloud-agent-*.b64` files. Delivered alongside P4.3. Closed 2026-05-06.
- [x] **P4.5** — Final testbed sweep. Surfaced one cosmetic compound bug (welcome page leak — directory routing + syntax-key mismatch in `general-api.py`). Both bugs fixed. `__VARNAME__` vs `{{VARNAME}}` convention split documented in `DeCloud.Builds/README.md`. Closed 2026-05-07.

**Phase 4 done:** ✓ Migration is complete.

**Net deletion across Phase 4:** ~470 lines of legacy substitution / merge / template-service code, plus deleted `decloud-agent-*.b64` files and the legacy `CloudInit/Templates/` directory.

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

_Plan as of migration closure (2026-05-07). §1 reflects final state; §2 is the full historical decision log; §8 marks all Phase 4 tasks complete. New work referencing this pipeline should use `UNIFIED_CLOUDINIT_PIPELINE.md` (the design document) as its primary reference, not this plan._

---

## §11. Migration closure summary

This section captures the final-state architecture as the unified pipeline closes Phase 4 and transitions out of the active execution doc into reference material.

### Three substitution boundaries (final)

The pipeline has three explicit, well-bounded substitution layers. Boundaries are documented in code (`TemplateVariable.cs` xmldocs, `IVmDeploymentPipeline.cs`, `SystemVmReconciler` comments) and respected by both Orchestrator and NodeAgent.

| Layer | When | Where (code) | Syntax | Authoritative for |
|---|---|---|---|---|
| **Render-time** | Template push to node (`/api/nodes/me/system-templates/{role}` or tenant `CreateVm` command construction) | Orchestrator's `CloudInitRenderer.RenderAsync` → `ResolveStaticsAsync` | `__VARNAME__` | Cloud-init body content (yaml that becomes `spec.CloudInitUserData`). Single authoritative substitution layer for both system and tenant VMs. |
| **Deploy-time** | Node receives template, mints libvirt UUID | `SystemVmReconciler.ActCreateAsync` → `template.CloudInitContent.Replace("__VM_ID__", vmSpec.Id)` | `__VM_ID__` only | A single deferred placeholder. The libvirt UUID is minted on the node, not the orchestrator. Only deferred variable in the entire pipeline. |
| **Runtime** | Inside the VM, post-boot, polling `/api/obligations/{role}/environment` every 60s | `decloud-env-watcher.sh` (in VM) reads response, diffs against `/etc/decloud-{role}/environment`, applies `max(scope)` action across changed variables | `$VARNAME` (shell-source via `EnvironmentFile=`) | Dynamic values that change without redeploy. Currently 2 dynamics per mesh-participant role: `*_ADVERTISE_IP` (Restart), `*_BOOTSTRAP_PEERS` (Noop). |

A separate, **independent** substitution boundary exists inside artifact serving:

| Layer | When | Where | Syntax | Authoritative for |
|---|---|---|---|---|
| **Consumer-render** | Browser request to a VM's served HTTP endpoint | Per-template Python server (e.g., `dht-dashboard.py` `_send_file_with_substitution`, `general-api.py` HTML branch) | `{{VARNAME}}` (new convention; DHT/BlockStore dashboards still use `__VARNAME__` for legacy reasons) | Per-VM content inside artifact bytes (HTML/JS files served from VMs). The orchestrator's render layer cannot reach into artifacts because they're content-addressed by SHA256; the consumer reads its own values from `os.environ` or local files (`/etc/decloud/{role}-metadata.json`). |

### Key invariants

1. **One render authority.** `CloudInitRenderer` is the only layer that substitutes `__VARNAME__` in cloud-init bodies. Node-side substitution is reduced to a single deferred placeholder (`__VM_ID__`).
2. **One file authority per scope.** Statics live in `/etc/decloud-{role}/{role}.env` (written by cloud-init `write_files`, never mutated post-boot). Dynamics live in `/etc/decloud-{role}/environment` (watcher-managed, never written by anyone else). Service units load both via two separate `EnvironmentFile=` directives — systemd's later-wins merge keeps them disambiguated by file path, not by key.
3. **Content-addressed artifacts.** Every artifact is fetched by SHA256 from the node-local cache (`http://192.168.122.1:5100/api/artifacts/{sha256}`). Verified on cache write and re-verified on serve. SHA256 baked into cloud-init at render time via `${ARTIFACT_URL:name}` and `${ARTIFACT_SHA256:name}` substitutions.
4. **Identity persistence across redeploys.** `ObligationStateBase` subclasses (`RelayObligationState`, `DhtObligationState`, `BlockStoreObligationState`) preserve role identity (WG keypair, libp2p Ed25519 keypair, peerId, authToken) across `virsh destroy` + redeploy. Cloud-init fetches identity from `/api/obligations/{role}/state` before binary starts.
5. **Single VmId.** Post-BLOCKSTORE-FIX §6, both orchestrator and node converge on the libvirt UUID. No more pre-assigned phantom GUIDs. Heartbeat-based adoption is now the live convergence path; role-specific self-heal callbacks are belt-and-suspenders.
6. **Fast-fail at boundaries.** Required Statics throw on missing input (e.g., `CaPublicKeyResolver` if `Node.SshCaPublicKey` is null). Tenant userdata throws if `spec.CloudInitUserData` is empty (post-P4.3). No silent fallbacks that mask systemic gaps.

### Key files

**Shared (`DeCloud.Shared`):**
- `Models/TemplateVariable.cs` — first-class metadata declaring variable kind + scope. Single source of truth for render-time, env-endpoint, and watcher behavior.
- `Models/SystemVmTemplate.cs` — node-side projection of `VmTemplate` pushed via SQLite-cached payload.
- `Models/TemplateArtifact.cs` — content-addressed artifact metadata (SHA256, source URL or data: URI).
- `Models/ObligationState.cs` — `ObligationStateBase` + role subclasses for identity persistence.

**Orchestrator (`DeCloud.Orchestrator`):**
- `Services/CloudInit/CloudInitRenderer.cs` — render engine. `ResolveStaticsAsync` iterates `template.Variables`, dispatches to registered resolvers.
- `Services/CloudInit/Resolvers/PlatformCommon/*.cs` — 12 platform-common resolvers (VM_ID, VM_NAME, NODE_ID, ORCHESTRATOR_URL, CA_PUBLIC_KEY, etc.).
- `Services/SystemVm/SystemVmTemplateSeeder.cs` — composes base + role layers, declares Variables and Artifacts, bumps `Revision` on cloud-init/binary changes.
- `Services/SystemVm/SystemVmTemplateSeeder.Artifacts.cs` — auto-generated SHA256 constants (regenerated by `system-vms/compute-artifact-constants.sh`).
- `Controllers/NodeSelfController.cs` `GetSystemTemplate` — endpoint that renders + serves the template payload.

**NodeAgent (`DeCloud.NodeAgent`):**
- `Infrastructure/Services/SystemVm/SystemVmReconciler.cs` — consumes pushed templates, performs the single deferred substitution (`__VM_ID__`), dispatches to `_pipeline.DeployAsync()`.
- `Infrastructure/Libvirt/LibvirtVmManager.CreateCloudInitIsoAsync` — post-P4 minimal flow: take `spec.CloudInitUserData` as-is, defensive variables-Replace pass, leak detector, ISO generation.
- `Controllers/ObligationEnvironmentController.cs` — serves `/api/obligations/{role}/environment` with inline `(role, name)` switch resolving 2 dynamics per role.
- `Infrastructure/Services/VmDeploymentPipeline.cs` — unified deploy entry point (artifact prefetch + binary verification + manager dispatch) for both system and tenant VMs.

**Build outputs (`DeCloud.Builds`):**
- `system-vms/base-system-mesh.yaml` — base layer for mesh-participant system VMs (DHT/BlockStore).
- `system-vms/{role}/cloud-init.yaml` — role layers, composed with base by `TemplateComposer` at seed time.
- `system-vms/shared/assets/decloud-env-watcher.{sh,service,timer}` — generic watcher (parameterised by role argument).
- `system-vms/shared/assets/wg-config-fetch.sh` — WG mesh enrollment script.
- `tenant-vms/general/cloud-init.yaml` + `tenant-vms/general/assets/general-api.py` + `index.html` — tenant general VM template + welcome page.

### Latent issues deferred from migration scope

These are tracked but not blocking and have low likelihood of biting in normal operation:

1. **Auto-generated SHA256 constants drift** — `SystemVmTemplateSeeder.Artifacts.cs` is regenerated by `compute-artifact-constants.sh`. No CI gate enforces regeneration when shared scripts change. A developer modifying a `.sh` file but forgetting to run the script causes orchestrator/node SHA mismatch at deploy time → `sha256sum -c` fails inside cloud-init → VM bootstrap aborts. Worth a future CI hook.
2. **Single deferred placeholder is fragile design** — `__VM_ID__` is currently the only render-late variable. Adding a second deferred placeholder requires editing `SystemVmReconciler` (no general "render-late" mechanism in `TemplateVariable`). Acceptable as long as the count stays at 1.
3. **Required-Static resolvers don't fall through on throw** — `CloudInitRenderer.ResolveStaticsAsync` propagates exceptions; a single resolver throw fails the whole template fetch. Correct behavior per fail-fast philosophy, but it means every Required Static is a single point of failure. No "warn-and-substitute-empty" middle ground today.
4. **Tenant userdata fast-fail throw is unverified across all callers** — P4.3 replaced the legacy fallback with a throw. General tenant + marketplace template confirmed clean; not yet exercised by every possible orchestrator-side `CreateVm` caller. By design — if a future caller arrives without `userData`, the throw fires loudly and points at the originating code.
5. **`__VARNAME__` vs `{{VARNAME}}` convention split** — Adopted in P4.5. DHT/BlockStore dashboards predate the convention and use `__VARNAME__` for both render-time and consumer-side substitution. Harmless because their dictionaries are scoped to artifact serving and never collide with orchestrator-render placeholders. Future artifacts should use `{{VARNAME}}` for consumer-side; documented in `DeCloud.Builds/README.md`.

### Architectural review verdict

**The unified pipeline is well-architected and cleanly layered.** Three substitution boundaries are real, distinct, and respected. Shared models in `DeCloud.Shared` provide single-source-of-truth metadata. Code documentation (xmldocs in models, header comments in pipeline files) makes the design intent visible to future readers. Recent surgical fixes during the closing session arc (VmId pre-assignment removal, bootstrap-poll source alignment, `EnvironmentFile=` discipline, P4 legacy deletion) closed several boundary-mismatch issues; the pipeline now has fewer concealed paths.

The five latent issues above are tractable. (1) is a CI gate. (2) is a design consideration if the count grows. (3) is correct fail-fast behavior; no action needed unless a specific scenario forces a middle ground. (4) self-surfaces if a regression happens. (5) is a documented convention going forward.

**End of plan.**