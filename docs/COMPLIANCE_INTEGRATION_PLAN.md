# DeCloud — Compliance Integration Plan

**Status:** Active — build sequencing for the pre-launch compliance framework
**Created:** 2026-06-26
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
- `NodeStatus { Offline, Online, Maintenance, Draining, Suspended }` — `Suspended` = "Suspended for violations".
- `VmStatus.Suspended` exists (enum value 5) **but has no entry in `VmLifecycleManager.ValidTransitions`** — no legal path in/out yet.
- `TemplateStatus { Draft, Published, Archived }`; `VmTemplate` has `IsVerified`, `IsCommunity`, `IsFeatured`; `ValidateTemplateAsync` runs as a fast pre-filter inside `PublishTemplateAsync`.
- Escrow: `DeCloudEscrow.sol` v3, `authorizedCallers`, `batchReportUsage`/`settleCycle`, `withdrawBalance` (no pause gate), no drain function.
- Lazysync pipeline (`LazysyncDaemon`): guest-freeze snapshot → `qemu-img convert -O raw` → `tmp.raw` → `ScanChunksAsync` (CID diff → `changedChunks`) → `PushBlocksAsync` (**raw bytes, no encryption**) → `DeleteSnapshotNodeAsync`. The dirty bitmap was removed in Phase J; `changedChunks` is the incremental signal.
- `CloudInitCleaner` already mounts guest filesystems, preferring `virt-customize` → `guestmount` (`LIBGUESTFS_BACKEND=direct`) → `qemu-nbd` fallback; `libguestfs-tools` is a known dependency; has an NBD concurrency lock.

**Genuinely missing (to build):** `BlockedWallets` + `IsWalletBlockedAsync`; suspension/denylist checks at chokepoints; `EnforcementActions` audit collection; `AdminComplianceController` + takedown; `TosAcceptance` + ToS gate; `TemplateStatus.PendingReview` + admin approval gate + nullable `AiAssessment`; `AbuseReport` + `POST /api/abuse` + manual queue; `ICsamScanner` + node scanner; `VmStatus.Suspended` transitions; block encryption-at-rest (out of scope here, noted for forward-compat).

---

## 4. Phase Sequence (easy → hard, by dependency)

### Phase 1 — Terms of Service
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
**Goal:** Block harm at the highest-leverage amplification point — one bad public template can be deployed thousands of times.
**Depends on:** admin auth (built), existing publish flow. Logs to Phase 2's audit trail.
**Build:**
- Add `TemplateStatus.PendingReview` between `Draft` and `Published`.
- Change `PublishTemplateAsync`: an author "request publish" sets `PendingReview`, **not** `Published`. Keep `VerifyArtifactsAsync` + `ValidateTemplateAsync` as the fast pre-filter.
- Add nullable `AiAssessment` field to `VmTemplate` (populated manually/left null until AI returns).
- `GET /api/admin/templates/pending` queue; `POST /api/admin/templates/{id}/approve` (→ `Published`, set `IsVerified`) and `/reject` (→ `Draft`, with feedback).

**Definition of done:**
- Community templates can no longer self-publish; publish requires an admin approve action.
- Approve/reject transitions are correct and the existing pre-filter still runs first.
- Regression guarded: the old instant-self-publish path is gone by design and tested as such.

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
2. `VmStatus.Suspended` lifecycle transitions: `Running → Suspended` (Enforcement trigger), admin-only unsuspend, deletion blocked while suspended (evidence preservation). These are currently absent from `ValidTransitions`.
3. Real scanner: short-circuit when `changedChunks` is empty; otherwise mount the frozen snapshot via **libguestfs/guestmount (`LIBGUESTFS_BACKEND=direct`)** — reusing the `CloudInitCleaner` pattern, **never** a host-kernel nbd mount of adversarial FS; per-file diff over magic-byte-typed image/video files against a persisted `{ path → size, mtime, hash }` map; read each genuinely-changed whole file; submit **hashes only** to the Microsoft CSAM Matching API.
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
