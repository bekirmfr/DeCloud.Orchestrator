# DeCloud — Compliance Integration Plan

**Status:** Active — build sequencing for the pre-launch compliance framework
**Created:** 2026-06-26
**Updated:** 2026-06-28 — Phase 1 (ToS) built. Phase 2 (Enforcement Core) complete: three-chokepoint gate, single-VM compliance hold hardened against every revival path, and the full operator-node takedown chain (scheduling-off → drain → hard cutoff, plus an immediate-cutoff admin override) with payout withheld throughout and held VMs no longer replicating. Phase 3 (Template Review Gate) built, including template versioning: community templates reach `Published` only via admin approval; a published template is immutable in place, and any change goes through a reviewed **draft revision** ("New version") that promotes onto the live version on approval — so the live version is never pulled from the marketplace during review. An admin review UI clears the queue. The first unbuilt pillar is now Phase 4 (Abuse Reporting). See the build log (§7) for detail and the open follow-ups.
**References:** `COMPLIANCE.md` (authoritative spec), `PROJECT_FEATURES.md` §10, `MINECRAFT_VISION_ROADMAP.md`
**Scope:** Turns the four-pillar framework in `COMPLIANCE.md` into a dependency-ordered build plan, grounded in the current code in `DeCloud.Orchestrator`, `DeCloud.NodeAgent`, `DeCloud.Shared`, and the `DeCloudEscrow.sol` contract.

This plan supersedes scattered "Planned" notes where they conflict. Where it states a decision, that decision was made deliberately and should not be silently reverted; reopen it explicitly if a new constraint appears.

---

## 1. Locked Decisions (the ledger)

These hold across all phases. They are the result of design discussion, not defaults.

1. **CSAM detection surface = node-level filesystem scanning, not block-store hashing.**
   The Block Store holds raw 1 MB sectors at arbitrary offsets (content-addressed CIDs over `tmp.raw` chunks); whole-file hash matching is impossible there. `PROJECT_FEATURES.md` currently describes block-store ingestion hashing — that is **wrong** and must be corrected to match `COMPLIANCE.md` and the code.

2. **Escrow forfeiture is removed from scope — not deferred.**
   `DeCloudEscrow.sol` v3 has no function to move user funds (explicit contract comment: *"There is NO admin function to drain user funds"*), and `withdrawBalance()` has no pause/freeze gate, so a user can always exit. Faking usage to seize funds is a defeatable override and an anti-pattern. Forfeiture would require a contract redeployment via the migration path. **We do not build it.** The ToS may *reserve* a future cost-recovery right only if counsel advises it is worth having.

3. **Enforcement = withhold-service-only.**
   Platform enforcement never touches deposited funds. It refuses to schedule VMs for a blocked wallet, terminates running VMs, and blacklists. The contract's "user funds are sacrosanct" invariant is treated as a regulatory asset, not a limitation.

4. **Two boundaries, one gate.**
   - `User.Status = Suspended` is the authoritative boundary for **internal, platform-originated** suspensions (someone who acted here and was suspended for it). `UserStatus { Active, Suspended, Deleted }` already exists and is already enforced at token refresh.
   - A provenance-bearing **`BlockedWallets`** denylist is the boundary for **proactive / imported** blocks (sanctions lists, law-enforcement lists, cross-platform intel). Keyed by wallet address; each entry carries `source` (sanctions | law_enforcement | cross_platform | internal), `reason`, `reference`, `addedBy`, `addedAt`. Bulk-importable. Removal is **source-scoped** (a sanctions re-import must never clobber an internal takedown, and vice versa). It **never creates a `User` row**.
   - Both compose into a single predicate, **`IsWalletBlockedAsync(wallet)`**, checked server-side at the action chokepoints. One gate function, two stores answering two different questions.
   - **Checksum-normalize on both write and read** (`AddressUtil().ConvertToChecksumAddress`). Imported lists arrive lowercased; a case mismatch is a silent bypass.

5. **Gate is checked server-side at action time, never via the JWT claim.**
   Access tokens live ~60 min and carry a stale `user_status`. Refusal must re-fetch state at each chokepoint, not trust the token.

6. **AI triage is deferred; manual admin review replaces it for launch.**
   Keep the seams clean so AI slots in later without rework: a nullable `AiAssessment` field on `VmTemplate`, and a **deterministic category→priority/SLA map** for abuse triage (the exact shape an AI would later output). No model calls in v1.

7. **Enforcement vocabulary stays distinct from the existing data-locality `Compliance` trigger.**
   `TransitionTrigger.Compliance` + `NonCompliantSince`/`NonComplianceReason` already exist and mean *geographic/locality* compliance (migrate a VM off a node whose locality violates constraints). Do not overload it. Use `Enforcement` / `VmStatus.Suspended` for legal enforcement.

8. **Admin role string is `"Admin"`.**
   `AdminUserInitializer` assigns `"Admin"`. `SystemController` has a pre-existing bug using lowercase `"admin"` in `[Authorize(Roles = "admin")]` that silently fails the role check. Do not replicate it; new admin endpoints use `"Admin"`.

9. **CSAM replication ordering = scan-before-replicate; defer on budget overrun.**
   Scan the frozen snapshot before `PushBlocksAsync`. If the per-cycle scan budget is exceeded, **defer** that cycle's replication to the next round — never publish unscanned content. Deferral keeps content on the origin (where the guest already wrote it) and only postpones redundancy; the sole cost is a temporary durability gap if the origin dies mid-window. This is forward-compatible with future encryption-at-rest, which would slot in *after* the scan clears.

10. **Single-VM hold is an orthogonal `VirtualMachine.ComplianceHold` flag, not `VmStatus.Suspended`.**
   Holding one VM (as opposed to suspending a whole wallet) sets a dedicated bool, not the `VmStatus.Suspended` enum value. A status value is overwritten by heartbeat/lifecycle state sync — the node continuously reports a VM's live state, so a status-based hold would be clobbered the moment the VM is observed. An orthogonal flag survives every sync. The node persists the **same** flag in `vms.db` (schema **v9**) and gates its VM manager on it, so the hold holds across node restarts and *before the first heartbeat*. `VmStatus.Suspended` (enum value 5, still transition-less) is left unused for this purpose. **This supersedes Phase 6 step 2's "add `VmStatus.Suspended` transitions"** — Phase 6 reuses `ComplianceHold` instead.

11. **The node enforces; the orchestrator owns desired run-state.**
   The node agent enforces holds and reboots *hung Running* VMs, but it never decides whether a *Stopped* VM should run — it does not `virsh start` a stopped domain on its own. Whether a stopped VM should be running is desired-state, owned by the orchestrator. This is the boundary that keeps the node from reviving owner-stopped, admin-held, or crashed VMs, and is why the health watchdog is gated on `Status == Running`. Consequence, recorded deliberately: node-side auto-recovery of genuinely *crashed* tenant VMs is gone; if wanted it belongs in orchestrator reconciliation with an explicit desired/intended run-state (see §7, open follow-up).

12. **Operator-node takedown = scheduling-off → drain → hard cutoff, graceful-evacuate-first by default.**
   Blocking a wallet that *operates* nodes (not just owns VMs) takes those nodes down in three stages. (a) **Immediately:** `IsSchedulingReady = false` + `NodeStatus.Suspended`, and `LoginNodeAsync` gated so the operator cannot re-enable scheduling. The scheduler already rejects such nodes (FILTER 1 on `Status != Online`, FILTER 1.5 on `!IsSchedulingReady`). (b) **Drain:** the node's *replicated* tenant VMs are evacuated to clean nodes through the **existing** offline-DR migration pipeline — its per-cycle retry and `Unrecoverable` terminal state are the "smart retry"; no parallel drain engine. Only confirmed-replica (`Protected`/`Replicating`) VMs are drained; unconfirmed ones finish seeding on the still-alive source first. **Ephemeral (factor 0) VMs are not drainable** — they resolve to `LazysyncStatus.Lost` at cutoff, by the tenant's own no-durability choice, and never gate the drain. (c) **Hard cutoff** (built): once no drainable replicated VMs remain (verified against the *manifest*, not the race-prone `LazysyncStatus`), revoke JWT + deregister — keeping the JWT live *through* the drain, since `DeregisterNodeAsync` deletes the node record and a VM still pointing at that `NodeId` would be stranded. Graceful-evacuate-first is the default; **immediate hard cutoff is an explicit per-takedown admin override** (`POST /api/admin/compliance/cutoff`), built. It collapses the *grace*, not the *ordering*: ephemeral/unconfirmed VMs are lost at t=0 and confirmed ones migrate from their DHT replica, with the node deregistered a cycle or two later — a literal same-instant deregister is impossible since it would strand the recoverable VMs. The override is a deliberate second action, not a flag, so it can't fire by accident.

13. **Settlement to a blocked operator is withheld, not forfeited.**
   On-chain payout flows only through `GetPendingSettlementsAsync` → the settlement loop, keyed on the node (payee) wallet. A blocked payee's batch is skipped, so the orchestrator never signs a USDC transfer to a suspended/sanctioned wallet — covering both pre-block accrued usage and third-party usage hosted during the drain. The `UsageRecord`s **persist unsettled** (not dropped), so legitimately-earned pre-block income settles normally if the wallet is later cleared. The gate is on the payee only (a blocked *tenant* must not stop a legitimate operator being paid). Permanent forfeiture (sanctions) stays an out-of-band legal action, never a settlement-loop decision.

14. **A held VM stops replicating immediately; its already-replicated blocks are quarantined, not purged.**
   Lazysync excludes held VMs explicitly (`!ComplianceHold`), so replication halts the moment the node knows of the hold rather than waiting for the force-stop to land — important for a content hold, where continuing would keep *spreading* the offending blocks. But holding does **not** walk back blocks already in the block-store/DHT. Purging them collides with CSAM evidence-preservation (NCMEC/legal hold), so the correct shape is **quarantine** — stop announcing and stop serving to new fetchers, retain under seal for the report — handled in Phase 6, not as a delete.

---

## 2. Counsel / Administrative Items (non-code, pre-launch)

These are explicitly **not** engineering decisions. They block launch but not the build.

- [ ] Retain legal counsel familiar with CFAA, DMCA, and 18 U.S.C. § 2258A / § 2252A.
- [ ] Counsel to advise on **money-transmission / custody classification** (does any fund-control capability trigger MSB / custody obligations?). Drives whether the ToS reserves any future cost-recovery right.
- [ ] Counsel to advise on **OFAC sanctioned-address screening** obligations (the SDN list includes wallet addresses) — feeds the `BlockedWallets` import sources.
- [ ] File DMCA Designated Agent with the US Copyright Office ($6, https://www.copyright.gov/dmca-directory/).
- [ ] Establish NCMEC CyberTipline account and reporting procedure.
- [ ] Microsoft CSAM Matching API account + agreement (Phase 6).
- [ ] Draft + legally review the ToS document (Phase 1 needs the text + hash).
- [ ] Retroactive rubric review of the Private Browser (Ultraviolet) and Shadowsocks seed templates (Phase 3).

---

## 3. Code-Grounded Starting State

Verified against the repos so future readers don't rediscover it.

**Reusable, already built:**
- Wallet signature verification: `UserService.RecoverAddressFromSignature`, `WalletSshKeyService.VerifyWalletSignature` (Nethereum EIP-191), and `AuthController.GetAuthMessage` nonce pattern. → ToS signing reuses this; no new crypto.
- Admin identity: `Admin:WalletAddress` config → `AdminUserInitializer` → role `"Admin"`; `[Authorize(Roles = "Admin")]` on controllers.
- `UserStatus { Active, Suspended, Deleted }` on `User`; refresh path already rejects non-`Active`.
- `NodeStatus { Offline, Online, Maintenance, Draining, Suspended }` — `Suspended` = "Suspended for violations", now actively used by operator-node takedown (Decision 12) and honored by the scheduler (FILTER 1 rejects `Status != Online`; FILTER 1.5 rejects `!IsSchedulingReady`).
- `VmStatus.Suspended` exists (enum value 5) **but has no entry in `VmLifecycleManager.ValidTransitions`** — no legal path in/out, and now intentionally left that way: the single-VM hold uses the orthogonal `VirtualMachine.ComplianceHold` flag instead (Decision 10), which is **built** (see §7).
- `TemplateStatus { Draft, Published, Archived }`; `VmTemplate` has `IsVerified`, `IsCommunity`, `IsFeatured`; `ValidateTemplateAsync` runs as a fast pre-filter inside `PublishTemplateAsync`.
- Escrow: `DeCloudEscrow.sol` v3, `authorizedCallers`, `batchReportUsage`/`settleCycle`, `withdrawBalance` (no pause gate), no drain function.
- Lazysync pipeline (`LazysyncDaemon`): guest-freeze snapshot → `qemu-img convert -O raw` → `tmp.raw` → `ScanChunksAsync` (CID diff → `changedChunks`) → `PushBlocksAsync` (**raw bytes, no encryption**) → `DeleteSnapshotNodeAsync`. The dirty bitmap was removed in Phase J; `changedChunks` is the incremental signal.
- `CloudInitCleaner` already mounts guest filesystems, preferring `virt-customize` → `guestmount` (`LIBGUESTFS_BACKEND=direct`) → `qemu-nbd` fallback; `libguestfs-tools` is a known dependency; has an NBD concurrency lock.

**Built since this plan was written (2026-06-27 — see §7 for detail):** `TosAcceptance` + `TosService` + `TosController` + VM-create ToS gate (Phase 1); `IWalletBlocklistService.IsWalletBlockedAsync` + `BlockedWallets`/`BlockSource` + `EnforcementActions` audit + `EnforcementService` + `AdminComplianceController` + admin-compliance UI, **with the gate wired at all three chokepoints** — `CreateVmAsync`, `RegisterNodeAsync`, `PublishTemplateAsync` (Phase 2 core, DoD met); the single-VM hold end-to-end — `VirtualMachine.ComplianceHold`, `SetVmComplianceHoldAsync`, `Suspend/ResumeVmAsync`, heartbeat re-enforcement, node-side persisted hold + VM-manager gate + autostart-disable + watchdog skip + migration exclusion + **lazysync exclusion**; the **complete operator-node takedown** — suspend the operator's nodes + login gate, withhold settlement, drain replicated VMs, hard cutoff once drained, and the immediate-cutoff override (Decisions 12–13); and **Phase 3 template review** — `TemplateStatus.PendingReview`/`Rejected` + review fields, the community-review invariant enforced across create/publish/update/deploy, the admin approve/reject/queue endpoints, edit-after-approval re-review, and the admin review UI.

**Genuinely missing (still to build):** `AbuseReport` + `POST /api/abuse` + manual queue (Phase 4); DMCA agent + notice/counter-notice flow (Phase 5); `ICsamScanner` + node scanner **and replica quarantine on hold** (Phase 6, Decision 14); block encryption-at-rest (out of scope here, noted for forward-compat). Minor, non-load-bearing: a dedicated `CutoffNodes` audit type (currently reuses `TerminateVms` with a `mode` tag), and Phase 3 polish (status-badge colors + a pending-count badge in the nav).

---

## 4. Phase Sequence (easy → hard, by dependency)

### Phase 1 — Terms of Service
**Status:** ✅ Built (2026-06-27). `TosAcceptance` model + `tos_acceptances` collection; `TosService` (version from config, hash computed from the embedded document, positive cache, fail-closed when the document is absent); `TosController` `GET/POST /api/tos` with EIP-191 signature verification reusing the existing primitive; VM-create gate wired (surfaced as a 4xx creation-gate failure, and template deploy routes through `CreateVmAsync`). **Remaining:** the embedded ToS text + repeat-infringer clause (counsel), and confirm the 30-day re-sign flow.
**Goal:** Legal basis that makes every later enforcement action defensible; DMCA safe-harbor prerequisite.
**Depends on:** existing wallet-signature primitive (built). Touches the VM-create path.
**Build:**
- `TosAcceptance` model + collection: `{ walletAddress, tosVersion, tosHash, signature, signedAt }`.
- Static ToS document + stored hash. Includes the **repeat-infringer-termination** clause (required for §512). The escrow-forfeiture clause is included **only** as a reserved future right *if counsel approves* — flagged as not technically enforced.
- `GET /api/tos` (current version + hash), `POST /api/tos/accept` (verify signature recovers the wallet, store acceptance).
- Acceptance gate in the VM-create path.
- Version-bump re-sign flow; VM creation blocked after 30 days without re-sign (existing VMs keep running).

**Definition of done:**
- A wallet cannot create a VM without a stored, signature-verified acceptance of the current ToS version+hash.
- A material ToS version bump re-prompts; the new hash is stored alongside the prior acceptance.
- Signature verification reuses the existing primitive (no new crypto introduced).

**Pre-code check:** confirm the actual `CreateVmAsync` signature and the exact insertion point for the acceptance gate.

---

### Phase 2 — Enforcement Core (the spine)
**Status:** ✅ Built (2026-06-27), DoD met. `IWalletBlocklistService` (singleton; owns `blocked_wallets` + `enforcement_actions`; `IsWalletBlockedAsync` = `User.Status == Suspended` OR any denylist source, checksum-normalized, source-scoped removal, bulk import); `EnforcementService` scoped facade (suspend/unsuspend wallet + stop its VMs, per-VM suspend/resume, block/unblock/bulk); `BlockedWallet`/`BlockSource`, `EnforcementAction`/`EnforcementActionType`; `AdminComplianceController` (`[Authorize(Roles="Admin")]`, `/api/admin/compliance/*`) + `admin-compliance.js` UI. The gate is now wired at **all three** chokepoints — `CreateVmAsync`, `RegisterNodeAsync`, `PublishTemplateAsync`. **Extended beyond the original DoD** with the complete operator-node takedown (Decisions 12–13): suspend the operator's nodes + gate re-login, withhold settlement to a blocked payee, drain replicated VMs via the migration pipeline, hard cutoff once drained (manifest-guarded), and the immediate-cutoff admin override (`POST /api/admin/compliance/cutoff`). **This area is complete.**
**Goal:** The single gate, the audit trail, and the atomic takedown that Phases 3, 4, and 6 all call into.
**Depends on:** admin auth (built), VM termination (built), template archiving (`Archived` exists). Phase 1 provides the legal basis.
**Build:**
- `BlockedWallets` collection + model (provenance fields per Decision 4), with source-scoped add/remove and bulk import.
- `IsWalletBlockedAsync(wallet)` = `User.Status == Suspended` **OR** wallet on `BlockedWallets`; checksum-normalized.
- Gate calls (server-side, at action time) at: `CreateVmAsync` (covers raw VM + template deploy — see check below), `RegisterNodeAsync`, `PublishTemplateAsync`.
- `EnforcementActions` append-only collection (never updated/deleted): `{ actionId, reportReference, actionType, targetWallet, targetVmId, targetTemplateId, reason, category, actingAdmin, timestamp, notes }`.
- `AdminComplianceController` (`[Authorize(Roles = "Admin")]`): `POST /api/admin/takedown` orchestrating, atomically and in order: terminate VMs → archive templates → set `User.Status = Suspended` (and/or add to `BlockedWallets`) → write `EnforcementActions`. Returns a summary of actions taken.

**Definition of done:**
- A blocked or suspended wallet is refused at all three chokepoints, verified server-side (not via stale JWT claim).
- Takedown is atomic and always writes exactly one `EnforcementActions` record.
- A sanctions re-import does not lift an internal suspension and vice versa (source-scoped removal proven by test).
- Address case mismatch does not bypass the gate (normalization proven by test).

**Pre-code check:** confirm template deployment converges on `CreateVmAsync` (the single-gate claim depends on it); if it bypasses, add the gate at the deploy entry too.

---

### Phase 3 — Template Review Gate (manual approval)
**Status:** ✅ Built (2026-06-27). See §7 for detail.
**Goal:** Block harm at the highest-leverage amplification point — one bad public template can be deployed thousands of times.
**Depends on:** admin auth (built), existing publish flow. Logs to Phase 2's audit trail.
**What shipped (the invariant: a community template reaches `Published` — listed + deployable — only via admin approval):**
- `TemplateStatus` gained `PendingReview` and `Rejected` (appended, not inserted, so existing 0/1/2 serialization is unchanged). `VmTemplate` gained `ReviewedBy`, `ReviewedAt`, `RejectionReason`, and nullable `AiAssessment` (reserved for the deferred AI triage, Decision 6 — null at launch).
- The gate is enforced at the service boundary, not per-caller: `CreateTemplateAsync` forces a community template off `Published`; `PublishTemplateAsync` routes a community template to `PendingReview` (platform templates publish directly); `UpdateTemplateAsync` clamps a non-admin off `Published`/`PendingReview`; `DeployTemplate` treats anything below `Published` as author-only.
- Admin review surface: `GET /api/marketplace/templates/pending`, `POST .../{id}/approve` (→ `Published`, `IsVerified = true`, stamps reviewer), `POST .../{id}/reject` (→ `Rejected` with a required reason). Both refuse anything not currently `PendingReview`.
- Rejection goes to a `Rejected` status with a visible reason (Decision: author edits and republishes, re-entering review) — not silently back to `Draft`.
- Edit-after-approval is closed by **versioning** (superseded the original slice-1b re-review): a published community template is immutable in place — every change (payload, cosmetic, or artifacts) is refused at the update and artifact endpoints and must go through a **draft revision** opened with "New version". The revision shares the parent's slug (a partial unique index excludes revisions), is reviewed like any submission, and on approval its payload is promoted **onto the parent in place** (same id/slug/reviews/deploy history); the live version stays listed throughout. At most one open revision per parent (idempotent).
- Admin review **frontend** (`admin-templates.js` + nav/page wiring): a "Template Review" page listing the queue, each card expandable to its full deployable payload, with inline approve / reject.

**Definition of done — met:**
- Community templates can no longer self-publish; reaching `Published` requires an admin approve action, and that's the *only* path.
- Approve/reject transitions are guarded (must be `PendingReview`); the publish-time pre-filter (`VerifyArtifactsAsync` + `ValidateTemplateAsync`) still runs before `PendingReview`.
- The amplification surface is closed on both ends: unreviewed content can't go live, and a published template can't be silently swapped — it's immutable in place, and any change is reviewed as a draft revision before it promotes onto the live version.

---

### Phase 4 — Abuse Reporting + Manual Queue
**Goal:** Reactive intake and a human-decided action queue.
**Depends on:** Phase 2 (its action buttons call takedown).
**Build:**
- Public unauthenticated `POST /api/abuse` with the spec schema; generated reference IDs (`ABU-YYYY-NNNNN`).
- `AbuseReports` collection.
- Deterministic category→priority/SLA map (no AI): CSAM=P0/2h, malware_c2=P1/4h, illegal_marketplace=P1/8h, dmca=P2/48h, tos_violation=P3/72h, spam=P4/best-effort.
- `GET /api/admin/abuse` ordered by urgency, showing the reported resource and the wallet's `EnforcementActions` history; actions (dismiss / warn / takedown) call Phase 2.

**Definition of done:**
- Anyone can submit a report and receives a reference ID + SLA.
- The admin queue orders by urgency and surfaces prior enforcement history.
- Takedown from the queue produces a linked `EnforcementActions` record (`reportReference` set).

---

### Phase 5 — DMCA
**Goal:** Section 512 safe-harbor process; near-zero new code.
**Depends on:** Phase 1 (repeat-infringer clause), Phase 4 (intake + queue).
**Build:**
- DMCA notices route through `POST /api/abuse` with `category=dmca` (or a thin `POST /api/dmca` alias) → P2/48h in the same manual queue.
- Counter-notice handling: restore after 10–14 business days unless the claimant files suit (process + admin action).

**Definition of done:**
- A valid DMCA notice produces a tracked queue item with a 48h SLA and reference ID.
- The Designated Agent filing is complete (counsel/admin item) — without it, the code path has no legal effect.

---

### Phase 6 — CSAM Node-Level Scanning (hardest, last)
**Goal:** Proactive known-CSAM filtering at the only layer with plaintext whole files, with human-confirmed enforcement.
**Depends on:** Phase 2 (audit + blacklist on confirmation). External: Microsoft CSAM API + NCMEC accounts.
**Layer note:** node-FS is the correct layer by elimination, but it is an **inherently partial** control — blind to guest-side LUKS/dm-crypt/LVM. Complemented by Phase 3 (generation pipelines) and Phase 4 (reactive). Never presented as a guarantee.

**Build (staged):**
1. `ICsamScanner` interface + stub returning `IsClean = true`, wired at the seam between `ScanChunksAsync` and `PushBlocksAsync`. Lands the integration point with zero behavior change.
2. **VM hold/suspend primitive — already built (see §7), reused here.** The plan originally called for `VmStatus.Suspended` lifecycle transitions; per Decision 10 this is instead the orthogonal `VirtualMachine.ComplianceHold` flag, built end-to-end and hardened: admin suspend-vm/resume-vm, force-stop on hold, owner cannot restart, persisted on the node and gated at the VM manager, survives node restart, and is not revived by autostart, the health watchdog, re-enforcement, or migration. Phase 6 only needs to *call* `SuspendVmAsync` on a confirmed match — the hold mechanism itself is done. (Deletion-blocked-while-held for evidence preservation still needs wiring on the delete path.)
3. Real scanner: short-circuit when `changedChunks` is empty; otherwise mount the frozen snapshot via **libguestfs/guestmount (`LIBGUESTFS_BACKEND=direct`)** — reusing the `CloudInitCleaner` pattern, **never** a host-kernel nbd mount of adversarial FS; per-file diff over magic-byte-typed image/video files against a persisted `{ path → size, mtime, hash }` map; read each genuinely-changed whole file; submit **hashes only** to the Microsoft CSAM Matching API.
3a. **Replica quarantine on hold (Decision 14).** Suspending a matched VM already halts *further* replication (lazysync excludes held VMs), but its already-replicated blocks remain announced and fetchable in the block-store/DHT. Phase 6 must add quarantine for a content hold: **stop announcing and stop serving** those blocks to new fetchers, while **retaining them under seal** for the NCMEC report and any legal hold — never a delete. This is the containment half that the hold itself does not cover.
4. Replication ordering per Decision 9: scan-before-replicate; defer cycle on budget overrun; surface the deferred/non-redundant state via the existing `LazysyncStatus` field rather than a new flag.
5. Orchestrator endpoints: `POST /api/admin/csam-report` (node-auth), `POST /api/admin/vms/{id}/suspend`, `POST /api/admin/vms/{id}/unsuspend` (returns 501 until the review flow exists — intentional).

**Enforcement (non-negotiable):** no automated action. A hash match suspends the VM (protective, reversible) and alerts a human; NCMEC reporting + wallet blacklisting happen **only after human confirmation**. Protects against false positives and adversarial poisoning.

**Definition of done:**
- Quiet cycles (no changed media) incur no scan cost.
- Scanning never mounts an untrusted guest FS on the host kernel.
- A match suspends the VM and preserves the overlay; the VM cannot be resumed or deleted by user/automated paths.
- No NCMEC report or blacklist is ever written without a recorded human confirmation in `EnforcementActions`.
- Unscanned content is never replicated; a budget overrun defers and is visible via `LazysyncStatus`.

---

## 5. Documentation Fix (do alongside Phase 6 or earlier)

Correct `PROJECT_FEATURES.md` §10 CSAM subsection: replace "hash-based detection at Block Store ingestion / `NcmecHashService.cs` / `CsamQuarantineService.cs`" with the node-level filesystem scanning approach from `COMPLIANCE.md`. The block-store approach is technically infeasible and must not be built.

---

## 6. Cross-Phase Notes

- **Forward-compat with encryption-at-rest:** because the CSAM scan runs on plaintext before push and defer only postpones push, a future DEK/AES-256-GCM step slots in between scan and push without reopening Phase 6.
- **Single gate, many stores:** KISS is preserved by one predicate (`IsWalletBlockedAsync`), not by forcing one store. Two stores for two genuinely different concepts is alignment, not duplication.
- **Audit is the evidentiary spine:** `EnforcementActions` is append-only and retained indefinitely. Every enforcement path (takedown, abuse action, CSAM confirmation) writes to it.

---

## 7. Build Log

Dated record of what has actually landed, so status is read from here rather than re-derived. Verified against `DeCloud.Orchestrator` and `DeCloud.NodeAgent`.

### 2026-06-27 — Phase 1, Phase 2 core, and the single-VM compliance hold

**Phase 1 — Terms of Service.** `TosAcceptance` + `TosService` + `TosController`; EIP-191 signature verification; VM-create ToS gate (fail-closed when the document is absent). Remaining: ToS text + repeat-infringer clause (counsel); confirm the 30-day re-sign flow.

**Phase 2 — Enforcement Core.** `IWalletBlocklistService` (singleton, owns `blocked_wallets` + `enforcement_actions`, `IsWalletBlockedAsync`, source-scoped denylist, bulk import, append-only audit); `EnforcementService` (scoped facade); `BlockedWallet`/`BlockSource`, `EnforcementAction`/`EnforcementActionType`; `AdminComplianceController` + `admin-compliance.js`. Verify the `RegisterNodeAsync` and `PublishTemplateAsync` chokepoints (the create gate is wired).

**Single-VM compliance hold (orchestrator).** `VirtualMachine.ComplianceHold` (orthogonal bool — Decision 10); `VmService.SetVmComplianceHoldAsync` (refuses System VMs, force-stops if active, persists the hold on a field heartbeat/lifecycle sync never touches); `EnforcementService.SuspendVmAsync`/`ResumeVmAsync` (each writes an `EnforcementAction`); `AdminComplianceController` `suspend-vm`/`resume-vm`; heartbeat re-enforcement re-issues a force-stop if a held VM reports Running, deduped so a flapping node can't burst force-stops.

**Single-VM hold (node) — the hardening chain.** A held VM was repeatedly revived by independent node-side actors; each path was found from node diagnostics and closed in turn:
- *Re-enforcement dedup* — a held VM reported Running always skips the Running transition (ingress never re-registered), and the force-stop re-issue is suppressed while a stop is already in flight.
- *Autostart disabled* — held VMs get `virsh autostart --disable`, so libvirt does not auto-start them on host/`libvirtd` restart while the orchestrator is down.
- *Health-watchdog skip* — the watchdog skips held VMs instead of "healing" them.
- *Persisted hold + VM-manager gate* — `ComplianceHold` is persisted in `vms.db` (schema **v9**); `StartVmAsync`/`RestartVmAsync` refuse a held VM (`VM_HELD`); the flag loads from the DB before any background actor runs, closing the **startup race** where the watchdog's first cycle fired before the first heartbeat populated the in-memory held set. Authoritative `StartVm` (an orchestrator command, only sent when not held) clears the hold first — the "suspension-release" exemption.
- *Migration exclusion* — held VMs are excluded from the node-offline migration scan, because the hold is node-local and does not travel in `CreateVmPayload`; without this a held VM whose node went offline would be re-created and started on the target.

**Migration-framework fix (incidental, important).** The node DB's migrate path never stamped the schema version after `MigrateSchema` (only the fresh-DB path did). The first real migration (v8→v9) therefore re-ran on every boot and crash-looped on `duplicate column name`. Fixed by stamping the version after a successful migration (restoring the run-once invariant for *all* future migrations) and making the `ADD COLUMN` idempotent. Latent bug surfaced by the first non-empty migration.

**Watchdog boundary change (Decision 11).** The health watchdog is now gated on `Status == Running`: it reboots hung *Running* guests but never `virsh start`s a *Stopped* domain. This is the node/orchestrator authority boundary, and it subsumes the held case.

### 2026-06-27 (later) — chokepoint gates, operator-node takedown, replication hold guard

**Three-chokepoint gate completed (2I).** `CreateVmAsync` was the only chokepoint enforcing `IsWalletBlockedAsync`. Wired the other two: `TemplateService.PublishTemplateAsync` (inject the blocklist; refuse a blocked author before artifact verification) and `NodeService.RegisterNodeAsync` (STEP 1.7 gate after signature freshness, before node-ID computation). Phase 2's three-chokepoint DoD is now met.

**Operator-node takedown, slice 1 — suspend + login gate.** `EnforcementService` now (via `DataStore`) suspends every node a blocked wallet operates: `IsSchedulingReady = false` + `NodeStatus.Suspended`, wired into `SuspendAsync`/`BlockAsync` and reversed by `UnsuspendAsync`/`UnblockAsync` only when the wallet is no longer blocked by *any* source. `NodeService.LoginNodeAsync` refuses a suspended/blocked operator (`InvalidOperationException` → `LOGIN_REJECTED`), and since `IsSchedulingReady` is only ever set true by login, that one gate is the complete re-enforcement. Behavior change: a *targeted* `BlockAsync` now withholds service (bulk import stays data-only). Verified the scheduler honors both flags (FILTER 1 / FILTER 1.5), including targeted marketplace deploys.

**Operator-node takedown, slice 1b — withhold settlement (Decision 13).** Payout settles on-chain only through `GetPendingSettlementsAsync` → the settlement loop. Injected the blocklist there and skip any batch whose node (payee) wallet is blocked. Records stay unsettled (reversible). Single chokepoint, so no executor-side change.

**Operator-node takedown, slice 2 — graceful drain (Decision 12).** `ScanMigratingVmsAsync`: broadened the evacuation node-set from `Offline` to `Offline | Suspended`, and added a compliance-drain block that transitions a suspended node's replicated, confirmed-replica (`Protected`/`Replicating`), non-held tenant VMs from `Running` to `Error` so the existing migration pipeline evacuates them. Reuses target selection, retry/backoff, and the `Unrecoverable` terminal state wholesale. Ephemeral VMs left running (→ `Lost` at cutoff); unconfirmed replicas left to finish seeding on the live source.

**Replication hold guard (Decision 14).** `LazysyncDaemon.RunCycleAsync` now excludes `!ComplianceHold` explicitly, so a held VM stops replicating the moment the node sees the hold rather than waiting for the force-stop to land (closing the propagation-window where a held-but-still-running VM could keep pushing/spreading blocks). The compliance-migration candidate in `ScanMigratingVmsAsync` got the explicit `!ComplianceHold` clause too, so all three migration entry points read identically.

**Operator-node takedown, slice 3 — hard cutoff (Decision 12).** `NodeService.CutoffSuspendedNodeAsync` terminalizes leftover tenant VMs (ephemeral → `Lost`, unconfirmed → `Unrecoverable`) then reuses `DeregisterNodeAsync` (JWT revoke + record delete). Guarded twice: a cheap in-memory "still draining?" pre-filter in the scan, then an authoritative re-check against the **manifest** (`ConfirmedVersion`), not the race-prone `LazysyncStatus`, before the irreversible deregister — defers if any leftover replicated VM still has a confirmed replica. No timer; the existing command-timeout sweep guarantees the drain set empties. The JWT is kept live through the drain so a deleted `NodeId` never strands a VM.

**Immediate-cutoff admin override.** `POST /api/admin/compliance/cutoff` → `EnforcementService.CutoffOperatorNodesNowAsync` → `NodeService.CutoffSuspendedNodeNowAsync`, which forces every running VM on a suspended node into the offline-DR path now (`MarkNodeVmsAsErrorAsync`): ephemeral/unconfirmed lost at t=0, confirmed migrated from the DHT replica. The node stays Suspended and slice 3 deregisters it once drained — "immediate" collapses the grace, not the ordering, since a same-instant deregister would strand the recoverable VMs. A deliberate second admin action, not a flag. Audited as `TerminateVms` with a `mode=immediate-node-cutoff` tag.

### 2026-06-27 (Phase 3) — template review gate

**Slice 1 — the invariant.** `TemplateStatus` gained `PendingReview`/`Rejected` (appended, so 0/1/2 serialization is unchanged); `VmTemplate` gained `ReviewedBy`/`ReviewedAt`/`RejectionReason`/`AiAssessment` (nullable, AI triage reserved). The gate is enforced at the service boundary: `CreateTemplateAsync` forces a community template off `Published`; `PublishTemplateAsync` routes community → `PendingReview` (platform → `Published`); `UpdateTemplateAsync` clamps a non-admin off `Published`/`PendingReview`; `DeployTemplate` treats anything below `Published` as author-only. Marketplace listing already filtered `Published + Public`, so no change there. JS `STATUS_TO_STR` got the two labels.

**Slice 2 — admin review surface.** `ApproveTemplateAsync` (→ `Published`, `IsVerified = true`, stamps reviewer) / `RejectTemplateAsync` (→ `Rejected`, required reason) / `GetPendingReviewTemplatesAsync`; `DataStore.GetTemplatesByStatusAsync` (FIFO); `MarketplaceController` admin endpoints `GET templates/pending`, `POST {id}/approve`, `POST {id}/reject`. Both mutators refuse anything not currently `PendingReview`. (Build break en route: slice-2 Edit 3 was misapplied to `TemplateService.cs` — the `GetTemplateBySlugAsync` anchor exists in both files; corrected by moving the query to `DataStore` and restoring the wrapper.)

**Slice 1b — edit-after-approval re-review (superseded by versioning, 2026-06-28).** The original approach reset a live template to `PendingReview` on a deployable-payload edit (detected via a `DeployableSignature()` comparison of cloud-init/image/ports/variables/env), pulling it from the marketplace until re-approved. Versioning replaced this: a published template is now immutable in place and edits fork to a reviewed revision, so the live version is never pulled. The reset and `DeployableSignature()` were removed. See the 2026-06-28 entry.

**Admin review frontend.** `admin-templates.js` + nav/page wiring in `app.js`/`index.html`: a "Template Review" page listing the queue, each card expandable to its full deployable payload (cloud-init, image, ports, variables, artifacts), with inline approve / reject. Reads `MarketplaceController`'s raw responses directly (not the `ApiResponse` wrapper).

### 2026-06-28 (Phase 3) — template versioning + hardening

**Draft-revision versioning (supersedes slice 1b).** A published community template is immutable in place; changes go through a draft revision. `VmTemplate.ParentTemplateId` (nullable, `[BsonIgnoreIfNull]`) links a revision to its parent. `ReviseTemplateAsync` (owner-only; parent must be `Published` community) JSON-clones the parent into a `Draft` revision — at most one open revision per parent, returned idempotently. The revision shares the parent's slug: `idx_slug` became a **partial** unique index (`PartialFilterExpression: ParentTemplateId $exists false`) so revisions are excluded from slug uniqueness, with a boot migration that drops the legacy non-partial index. `ApproveTemplateAsync` gained a promote branch — for a revision it copies the parent's identity/history/curation forward, saves over the parent id, and deletes the revision (parent deleted mid-review → publishes standalone). `DeleteTemplateAsync` cascade-deletes open revisions.

**Published templates immutable at every door.** `UpdateTemplateAsync` refuses any non-admin edit of a published community template (the slice-1b signature comparison removed); the three artifact endpoints (`AddArtifact`/`UpdateArtifact`/`RemoveArtifact`) return 409 with the same rule, since they bypass `UpdateTemplateAsync`. Frontend: a published card shows Show + Delete (no direct Edit); New version opens the revision in the edit modal.

**Field-preservation consolidation.** The "restore fields the edit form doesn't send" logic lived in three copies (controller, service pre-validation, service preserve-block), and the slug / `AuthorName` defect surfaced at each. Collapsed into one `RestoreServerOwnedFields(incoming, existing)` — the single definition of server-owned-on-update fields (author identity, `ParentTemplateId`, `CreatedAt`, `Revision`, deploy counts, ratings, artifacts) — applied before validation at each layer that validates. Root cause of the recurring "revision slug rejected" / "AuthorName reserved" errors: validation ran on the raw PUT body before the restore, at both the controller and the service; both now restore first. Audit added `Revision` (was resetting to its model default on edit) and confirmed `Artifacts` must stay restored (the edit form sends an empty list). `ReviseTemplateAsync` also heals a legacy reserved `AuthorName` (a community template that picked up the `"DeCloud"` default before AuthorName was preserved) so a new version passes the reserved-name check on publish.

**Author-side lifecycle + details.** `my-templates.js`: `PendingReview` locks to Cancel-only; `Rejected` shows the reason with Resubmit; a published template and its in-progress revision render as a **single card** (the revision panel carries its own Publish / Edit / Discard); a read-only details modal ("Show") carries the New version action. `CancelReviewAsync` (author withdraws `PendingReview` → `Draft`). The now-dead `DeployableSignature()` was removed.

### Open follow-up (deliberate, not a regression to fix blindly)

**Crashed-VM auto-recovery.** With the watchdog no longer starting Stopped VMs, a tenant VM that crashes on a *healthy* node stays Stopped/Error until the owner restarts it. The orchestrator follows node-reported state and only redeploys VMs whose node is *offline* (the migration scan) — there is no "should be Running but is Stopped on a healthy node → StartVm" loop today. If auto-recovery is wanted, it belongs in orchestrator reconciliation and needs an explicit desired/intended run-state to distinguish a crash from an owner stop (the same distinction the node deliberately refuses to guess). Decide before launch; do not restore the indiscriminate node-side restart.

**Replica quarantine on hold (Phase 6, Decision 14).** Holding a VM halts new replication but does not walk back blocks already in the block-store/DHT. The quarantine — stop announcing/serving, retain under seal for evidence — is unbuilt and tracked as Phase 6 step 3a.

**Template versioning (Phase 3 enhancement) — ✅ built (2026-06-28).** Keeping the approved version live while a draft revision is reviewed separately is now built (draft-revision model; see the 2026-06-28 build-log entry). Superseded slice 1b.

**Private community templates route to review.** Slice 1's publish gate keys on `IsCommunity`, not visibility, so a Private (author-only, never amplified) community template also lands in the review queue. If undesired, that's a one-line refinement (`IsCommunity && Visibility == Public → PendingReview`).
