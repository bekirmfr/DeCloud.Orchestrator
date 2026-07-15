# DeCloud Platform — Compliance & Legal Framework

**Last Updated:** 2026-07-16
**Status:** Active — pre-launch. Pillars 2, 3, 4 built; Pillar 1 first pass built (seam + gate + report chain), real matcher gated on external prerequisites. See `COMPLIANCE_INTEGRATION_PLAN.md` §7 for build state.
**Purpose:** Authoritative reference for DeCloud's **content policy, legal obligations, and compliance philosophy** — what the platform undertakes to do and why.

> **Document ownership (read this first).**
> This document owns the **legal framework**: the pillars, the obligations, the
> policy, the reasoning. It does **not** own implementation. Endpoints,
> interfaces, collection schemas, and mechanisms are the **design of record** in
> `COMPLIANCE_INTEGRATION_PLAN.md` (§1 Locked Decisions, §3 code-grounded state,
> §7 build log). Where this document once specified a mechanism and a locked
> decision has since superseded it, the decision wins and this document points
> to it rather than restating it. Do not implement from a code sample here
> without checking the plan; where the two conflict, the plan is authoritative
> for *how*, this document for *what and why*.

---

## Table of Contents

1. [Philosophy & Scope](#1-philosophy--scope)
2. [The Four Pillars](#2-the-four-pillars)
3. [Pillar 1 — CSAM Proactive Filtering](#3-pillar-1--csam-proactive-filtering)
4. [Pillar 2 — Terms of Service](#4-pillar-2--terms-of-service)
5. [Pillar 3 — Abuse Reporting & AI Triage](#5-pillar-3--abuse-reporting--ai-triage)
6. [Pillar 4 — Template Review Gate](#6-pillar-4--template-review-gate)
7. [Enforcement Mechanism](#7-enforcement-mechanism)
8. [DMCA Agent & Safe Harbor](#8-dmca-agent--safe-harbor)
9. [Wallet Identity & Liability](#9-wallet-identity--liability)
10. [User Liability Under Wallet-Auth](#10-user-liability-under-wallet-auth)
11. [What This Framework Does Not Cover](#11-what-this-framework-does-not-cover)
12. [Build Checklist](#12-build-checklist)

---

## 1. Philosophy & Scope

DeCloud's core mission is **censorship-resistant infrastructure** — compute that cannot be shut down by centralized authorities for political, ideological, or competitive reasons. This mission is worth defending.

It does not mean, and must never be used to mean, that the platform is a safe harbor for content that is illegal in every jurisdiction on Earth. These are categorically different things:

| Category A — What Censorship Resistance Protects | Category B — What It Must Never Cover |
|---|---|
| Journalism in authoritarian regimes | Child Sexual Abuse Material (CSAM) |
| Political opposition speech | Drug marketplace infrastructure |
| Whistleblowing platforms | Botnet C2 servers |
| Privacy-preserving communication | Ransomware / malware hosting |
| Uncensored AI research tools | Human trafficking facilitation |

The compliance framework is designed to draw this line clearly and operationally — not philosophically. It protects the platform's ability to serve Category A by ensuring Category B cannot sustainably operate on it.

### Audience Accountability

The framework addresses four distinct audiences simultaneously:

| Audience | What They Need | How the Framework Serves Them |
|---|---|---|
| **Law enforcement** | Good-faith cooperation, records, takedown capability | Enforcement mechanism + ToS cooperation clause |
| **Copyright holders** | A defined process to request removal | DMCA agent + takedown mechanism |
| **Regulators / courts** | Evidence that reasonable steps were taken | All four pillars together |
| **Node operators** | Legal cover for hosting user content | ToS + DMCA safe harbor |
| **Users** | Clear rules, due process | ToS + appeal path |

---

## 2. The Four Pillars

The framework rests on four mutually reinforcing pillars. All four are required before public launch.

```
┌─────────────────────────────────────────────────────────────────┐
│                  DeCloud Compliance Stack                        │
├─────────────────┬──────────────────┬──────────────┬────────────┤
│  CSAM           │  Terms of        │  Abuse       │  Template  │
│  Proactive      │  Service         │  Reporting   │  Review    │
│  Filtering      │  (with teeth)    │  + AI Triage │  Gate      │
│                 │                  │              │            │
│  Keeps platform │  Legal standing  │  Reactive    │  Blocks    │
│  out of federal │  to act +        │  detection + │  amplifi-  │
│  criminal       │  DMCA safe       │  triage +    │  cation of │
│  exposure       │  harbor          │  SLA queue   │  harm at   │
│                 │                  │              │  source    │
└─────────────────┴──────────────────┴──────────────┴────────────┘
```

No single pillar is sufficient alone. CSAM filtering without a ToS gives you no right to act on other violations. A ToS without enforcement is theater. Enforcement without abuse reporting means you only act on what you happen to notice. Template review without enforcement is decoration.

---

## 3. Pillar 1 — CSAM Proactive Filtering

### Why Non-Negotiable

Child Sexual Abuse Material is illegal in every jurisdiction on Earth. Hosting it, even
unknowingly, exposes platform operators to federal criminal liability under
18 U.S.C. § 2258A (US) and equivalent statutes elsewhere. Unlike all other content
violations, this one carries criminal — not merely civil — exposure.

### Why Block-Level Hash Checking Does Not Work

DeCloud's Block Store is content-addressed (CID-based) and stores raw 1 MB disk sectors
at arbitrary byte offsets (e.g. offset `2684354560`). These blocks are filesystem
fragments — partial inodes, journal entries, EXT4 metadata, or fractions of a file split
across non-contiguous sectors.

CSAM hash databases (PhotoDNA, NCMEC) operate on **decoded, whole files** — a JPEG,
PNG, or video file whose bytes are intact and decodable. A JPEG split across three
non-adjacent 1 MB blocks will never produce a matching hash at the block level regardless
of the database used.

Block-level CSAM checking is therefore technically infeasible and is NOT implemented.
The correct detection surface is the **filesystem layer on the hosting node**, where
whole files are accessible in their decoded form before replication.

### Detection Architecture — Node-Level Filesystem Scanning

The hosting node is the only point in the architecture with plaintext access to VM
overlay data. Detection is performed here, before encryption and before replication,
on reconstructed files from the overlay filesystem.

#### Scan Scope

| Cycle | Scan Scope | Rationale |
|---|---|---|
| **Every cycle** | Files that changed since the last cycle, per the scanner's own file map | Only genuinely-changed image/video files are hashed. A quiet VM costs almost nothing, so there is no reason to exempt a "first" cycle. |
| **Template publish** | Full filesystem scan of template image | Separate pipeline; gates template availability before any VM can be deployed from it. |

**No cycle is skipped.** An earlier version of this document exempted the first
(seeding) cycle on the reasoning that a fresh overlay is clean by construction.
That reasoning is sound but the exemption is not worth having: the scanner's
change-source already makes a clean cycle nearly free, so skipping buys nothing
and costs an assumption — that nothing was written before the first cycle ran —
which is exactly the kind of assumption a hostile tenant is motivated to break.
Every enrolled tenant VM is scanned every cycle (~5 min, after a 5-minute
startup delay), regardless of replication factor (plan Decision 15).

**Change-source.** The scan hashes only files that genuinely changed, diffed
against a persisted `{ path → size, mtime, hash }` map — **not** replication's
`changedChunks`, which ephemeral `RF=0` VMs never produce. For `RF=0` VMs the
cheap "did anything change at all" signal is a scan-only QEMU dirty bitmap
(plan Decision 15, D1).

#### Per-Cycle Pipeline

```
LazysyncDaemon — incremental cycle:

1. Export overlay → tmp.raw                    (existing)

2. Map changed blocks → changedChunks offsets  (existing)

3. Mount frozen disk read-only via libguestfs  (new)
   → guestmount, LIBGUESTFS_BACKEND=direct      (CloudInitCleaner pattern)
   → NEVER a host-kernel nbd mount — see below

4. Resolve files touching changedChunks        (new)
   → walk filesystem inode table
   → map block offsets → file paths
   → collect only files overlapping changedChunks offsets
   → skip non-file types (journals, swap, inodes)
   → skip files not decodable as image/video

5. CSAM scan resolved files                    (new)
   → hash each file
   → call Microsoft CSAM Matching API (or local PhotoDNA)
   → collect any matches

6. Unmount (always — even on match)            (new)
   → tear down the libguestfs appliance

7a. IF MATCH:
    → record the match on the node BEFORE reporting (a crash must not lose it)
    → report to the orchestrator over the node's authenticated channel
    → orchestrator files a P0 (csam) abuse report + applies a protective hold
    → the VM is force-stopped and its disk preserved; owner cannot restart it
    → STOP — no blocks pushed, this cycle or any later one, until a human acts

7b. IF NOT A MATCH (Clean / Unscannable / type-skipped / NotScanned):
    → POST blocks to local blockstore                  (existing)
    → blockstore publishes via GossipSub               (existing)
    → register manifest with orchestrator              (existing)
    (RF=0 VMs stop after the scan — there is nothing to replicate)
```

> **Mount discipline — non-negotiable (host security).** The guest filesystem
> under scan is **adversarial input**: a tenant can craft a corrupt or malicious
> filesystem specifically to attack whatever parses it. `qemu-nbd` + `mount`
> parses that filesystem **in the host kernel**, where a bug is a host
> compromise — from an unprivileged tenant, on a node hosting other tenants.
> Scanning therefore uses **libguestfs/`guestmount` with
> `LIBGUESTFS_BACKEND=direct`** (the pattern `CloudInitCleaner` already uses),
> which parses the filesystem inside a throwaway appliance VM, not the host
> kernel. The mount is **owned by the scanner** behind `ICsamScanner`, so the
> discipline lives in exactly one place and a non-scanning (stub) build never
> mounts at all. This is a hard rule, not a preference: never mount an
> untrusted guest filesystem on the host kernel.

#### CSAM Database Source

**Microsoft CSAM Matching API** is used as the primary database source:
- No local database to maintain or sync
- Pay-per-call pricing — negligible cost at current scale
- Covers NCMEC hash database plus PhotoDNA perceptual hashing
- Requires Microsoft Azure account and CSAM API agreement

Node agents require outbound HTTPS access to the Microsoft CSAM API endpoint.
No file content is transmitted — only hashes.

#### False Positive Handling

PhotoDNA perceptual hashing has a non-zero false positive rate. A hash match does NOT
automatically result in NCMEC reporting or wallet termination. The response pipeline is:

1. VM suspended immediately (protective measure, reversible)
2. A P0 item appears in the admin abuse queue with the match metadata (file hash, file path, database source) and the target wallet's enforcement history. *(There is no operator email/webhook notification — no notification mechanism exists to reuse, and inventing one was out of scope. The queue is the alert surface.)*
3. Human review within 2 hours (P0 SLA)
4. If confirmed CSAM: NCMEC report filed, wallet blacklisted, VM terminated
5. If false positive: VM unsuspended, incident logged, no further action

**Automated enforcement on CSAM detection is prohibited.** Human confirmation is
required before NCMEC reporting and wallet termination. This protects against both
false positive harm and adversarial exploit of the detection system.

### VM Suspension State

A detected match places the VM under an **administrative hold** — a dedicated
flag, *not* a lifecycle status value (plan Decision 10: a status is overwritten
the moment the node reports the VM's live state; an orthogonal flag survives
every sync). The obligations the hold must meet:

- The VM is force-stopped and its disk preserved for evidence.
- Neither the owner nor any automated path can restart it — and the hold is
  enforced on the node itself, so it holds through node restart, libvirt
  autostart, the health watchdog, and migration, even with the orchestrator
  unreachable.
- **Deletion is refused while held** (evidence preservation), at the service
  boundary — so cleanup and takedown paths are covered, not just the API.
- It is lifted only by an authenticated admin action, reason-required and
  audited. Lifting is **human-in-the-loop by construction**: no automated path
  calls it.

### NCMEC Reporting

Upon human-confirmed detection:

1. NCMEC CyberTipline report filed. **The statute (18 U.S.C. § 2258A) requires
   reporting "as soon as reasonably possible" after obtaining actual knowledge —
   it does not name a deadline.** A 24-hour internal target is *our policy*, set
   so "as soon as reasonably possible" has an operational meaning. Do not let the
   two be conflated: the obligation is the statute's, the clock is ours. **Final
   wording is counsel's** (§2 checklist).
2. Report includes: file hash, platform VM identifier, wallet address, timestamp,
   node region. Does NOT include file content.
3. Associated wallet blacklisted platform-wide
4. All VMs owned by that wallet suspended pending review
5. Incident logged to append-only `EnforcementActions` collection

### API Surface

> **Mechanism owned by the plan** (`COMPLIANCE_INTEGRATION_PLAN.md` §7,
> 2026-07-08 → 10 entry). The obligations below are what this document owns; the
> routes and payloads are the plan's and the code's. An earlier version of this
> section specified `POST /api/admin/csam-report` carrying a `nodeId` **in the
> request body**, an admin-role suspend endpoint, and an `unsuspend` returning
> `501`. All three are superseded — the reasons matter and are recorded here:

**A node's match report is authenticated as a node, and its identity comes from
its credentials — never from the request body.** A `nodeId` field a caller fills
in is a field a hostile caller *forges*: it would let anyone with a node token
report against any VM. The built endpoint takes node identity from the
authenticated token, and it **fences the automatic suspension to the VM's
authoritative host** — a report about a VM the reporting node does not host is
still filed for a human, but cannot suspend anything. A safety mechanism that
can be aimed by an attacker is a denial-of-service weapon.

**The report lands in the existing Phase 3 abuse queue as P0/2h** (`csam`
category, `ABU-YYYY-NNNNN` reference) — not a second review surface. The
protective hold is applied immediately and automatically, because it is
reversible and withholds nothing but service; **everything irreversible waits
for a human** (see the enforcement rule above).

**Un-suspending is a human admin action, audited and reason-required.** The
earlier `501 Not Implemented` existed to guarantee "no automated unsuspend" —
but that property is already held by the shipped endpoint being admin-only,
audited, and called by no automated path. A 501 would have broken a working,
tested surface to re-state a guarantee that was already in force.

### Node Agent Interface

> **Mechanism owned by the plan.** The seam is built; its exact shape is the
> design of record in `COMPLIANCE_INTEGRATION_PLAN.md` (Decisions 9 & 15, Phase 6
> step 1, and the 2026-07-08 → 10 build-log entry) and in
> `src/DeCloud.NodeAgent.Core/Interfaces/ICsamScanner.cs`. What this document
> owns is the **honesty obligation** below.

The node agent calls a scanner behind `ICsamScanner` on the frozen disk, before
any replication. The scanner is given the **frozen disk path** and owns its own
read-only mount (see the mount discipline above). It returns an outcome, not a
boolean:

```
NotScanned  — no matcher wired, or the scan did not run
Clean       — scanned, no match against the hash database
Match       — positive hash match
Unscannable — could not be hashed (too large for budget, undecodable, etc.)
```

**The honesty invariant (non-negotiable).** *"Not scanned" must never read as
"clean."* A matcher-less stub returns **`NotScanned`** — never `Clean`. No path
upgrades `NotScanned` or `Unscannable` to `Clean`; the shipped
`NullCsamScanner` refuses to return `Clean` even when a test hook explicitly
asks it to, and the caller clamps any `Clean` to `NotScanned` while no matcher
is enabled. A stub that reported `Clean` would be **worse than no scanner at
all**: it would manufacture false assurance about child-safety coverage — to
operators, to auditors, and eventually in a legal filing.

**Consequently, until a real matcher is wired:** no external surface (UI, API,
badge, marketing, compliance claim) may represent stub state as scanning
coverage. The honest claim is *"reactive detection + template-publish review"* —
**not** *"proactive CSAM scanning."* Today every VM's recorded scan state is
truthfully `NotScanned`.

**Replication is gated on the scan *result*, not on the presence of a matcher.**
Only a positive `Match` blocks (suspend + preserve + report). `NotScanned`,
`Clean`, `Unscannable`, and type-skipped all proceed — otherwise the stub would
halt the entire platform, and one un-hashable file would strand a VM forever. A
scan that does not finish within its budget defers to the next cycle.

### Template Review Gate (Complementary Control)

Template images are scanned at publish time via a full filesystem scan — not per-VM
at deploy time. This is the correct place to catch content baked into a template
before it can be deployed to any VM. See Pillar 4 for the full template review pipeline.

### Known Limitations

**Novel CSAM.** Hash matching catches known material in the NCMEC database. Newly
generated material not yet in any database will not be detected. This is a known,
accepted limitation industry-wide. No platform-level scanning solves this — reactive
abuse reporting is the complementary control.

**Non-image/video content.** The scanner targets decodable image and video files.
Documents, archives, encrypted files, and binary data are not evaluated. CSAM
delivered inside encrypted archives or non-standard formats will not be detected.

**Filesystem type dependency.** Initial implementation targets EXT4 overlays.
NTFS and BTRFS overlays require additional inode-to-offset mapping logic.

**Encryption at rest — not built; forward-compatible by construction.** Blocks
are replicated as raw bytes today; there is no DEK step in the pipeline above.
The ordering already protects the future: the scan runs on the **plaintext**
frozen disk before anything leaves the node, so an encryption step slots in
between scan and push without reopening this pillar. One genuine fork must be
settled first (plan Cross-Phase Notes): if the content-address is
`hash(plaintext)`, a fetching node cannot verify ciphertext against the CID
without the key (breaking bitswap's integrity check); if it is
`hash(ciphertext)`, the same file gets different addresses under different keys
(losing cross-VM dedup, and re-keying re-addresses every block). Either way the
consequence for scanning is the same and is already true: **only the hosting node
can scan** — replica holders see bytes they cannot interpret, which is precisely
why detection lives at the node's filesystem layer.

---

## 4. Pillar 2 — Terms of Service

### What the ToS Achieves

A Terms of Service for a wallet-authenticated platform serves four distinct purposes — none of which is primarily "let us sue the user":

1. **Establishes the platform's right to act** — suspend wallets, terminate VMs, refuse service — without those actions being challengeable as arbitrary. (Enforcement is **withhold-of-service only**; the platform does not touch deposited funds — see §7.)
2. **Creates DMCA safe harbor** — 17 U.S.C. § 512 protection requires a ToS, a DMCA agent, and a defined takedown process. Without it, the platform is fully liable for user-hosted content.
3. **Defines law enforcement cooperation** — the legal framework under which wallet addresses and deployment logs are shared with authorities upon valid legal process.
4. **Establishes user responsibility as a public record** — Section 230 (US) and Article 14 (EU) shields require that a ToS exist and be presented to users.

### Wallet-Signed Acceptance (Not Just a Checkbox)

Standard checkbox ToS acceptance is legally weak. For a wallet-auth platform, acceptance must be **cryptographically signed**:

```
User connects wallet →
Platform presents ToS + hash of ToS document →
User signs message: { tosVersion, tosHash, walletAddress, timestamp } →
Signature stored in database against wallet address →
Platform access gated until signature confirmed
```

This creates a verifiable, tamper-proof record that this specific wallet accepted these specific terms at this specific time. It is legally equivalent to an electronic signature and is stronger because it is verifiably linked to the wallet's private key.

### Required ToS Clauses (Wallet-Auth Specific)

Beyond standard platform ToS language, these clauses are required for the wallet-auth context:

**User responsibility clause:** "You are solely responsible for all content deployed, stored, or transmitted through VMs and infrastructure associated with your wallet address."

**Cost recovery — reserved, not enforced (Decision 2):** the ToS may reserve a *future* right to escrow-based cost recovery **only if counsel advises it is worth having**, and must state plainly that no such capability exists today. `DeCloudEscrow.sol` v3 has **no function that can move user funds** (explicit contract comment: *"There is NO admin function to drain user funds"*) and `withdrawBalance()` has no pause or freeze gate — a user can always exit. A ToS clause claiming a seizure power the contract cannot perform is worse than no clause: it misstates the platform's capabilities to users, counsel, and courts. Enforcement is withhold-of-service only (§7); the fund-sacrosanct invariant is treated as a **regulatory asset**, not a limitation. The shipped ToS draft (`src/Orchestrator/Compliance/terms-of-service.md` §8) carries this as a reserved right, flagged as not technically enforced.

**Blockchain transparency notice:** "You acknowledge that your wallet address and all associated on-chain transactions are permanently recorded on a public blockchain. The platform may share your wallet address and associated transaction history with law enforcement agencies upon receipt of a valid legal request."

**Prohibited content clause:** An explicit enumeration of prohibited content categories (CSAM, illegal marketplaces, C2 infrastructure, etc.) with acknowledgment that wallet suspension, VM termination, and blacklisting apply to these categories without prior notice.

**Cooperation clause:** "The platform will cooperate with law enforcement agencies and regulatory bodies upon receipt of valid legal process, including but not limited to court orders, subpoenas, and NCMEC CyberTipline mandates."

### ToS Versioning — no grace period (decided 2026-07-16)

The ToS is versioned, and acceptance is bound to a specific version **and the
hash of its exact bytes**. When the ToS changes materially:

- The version is bumped, which changes the hash, which **invalidates every prior
  acceptance immediately**. There is no window: a wallet that has not signed the
  current version+hash cannot create a VM, starting the moment the bump lands.
- Users are prompted to re-sign at the point they next act. The new acceptance is
  stored alongside the prior one (the record of what was agreed, and when, is
  never overwritten).
- **Existing VMs keep running.** The gate is on *new* deployment, not on service
  already being consumed — enforcement is withhold-of-*new*-service, and pulling
  a running workload over a paperwork lapse would be a penalty, not a gate.

**Why no grace period.** An earlier version of this document promised 30 days
before VM creation is blocked. That is the standard shape, and it is wrong here.
The version is bumped *because something material changed* — often because
counsel required it. A grace window would be a period during which the platform
knowingly serves users under terms it has already decided are inadequate, and it
would exist for no other purpose. The right to act rests on the user having
agreed to the terms in force; a window is a hole in exactly that.

It is also nearly frictionless to comply: the acceptance gate surfaces at deploy
time, the user signs in their wallet, and the deploy proceeds. The cost of a bump
is one signature at the moment of next use — not a lockout.

**Consequences, accepted deliberately:**
- **Non-interactive clients break at the bump.** An API- or script-driven deploy
  gets a "ToS not accepted" refusal with no modal to click, at whatever hour the
  bump lands. That is the correct outcome — an automated deployer has no more
  right to deploy under superseded terms than a human — but it means a bump is an
  **operational event**: announce it, and expect automation to need a re-signature.
- **A bump is any byte change.** The hash is over the document's exact bytes, so a
  typo fix or a whitespace edit invalidates every acceptance just as surely as a
  new clause. Treat the document text and `Tos:Version` as a single deliberate,
  reviewed change — never an incidental edit.

---

## 5. Pillar 3 — Abuse Reporting & AI Triage

### Public Intake Endpoint

```
POST /api/abuse
```

**Authentication:** None required. Anyone must be able to report, including NGOs, automated scanners, and law enforcement liaisons.

**What intake must guarantee** (the request/response shapes are the code's — see
`AbuseController` and plan §7):

- **Anyone can report**, with no account and no identity — see below.
- The report names **what is being reported** (a free-text pointer: VM id,
  template slug, wallet, URL) and **why**, and may optionally carry a target
  wallet (so the queue can join the target's enforcement history) and a contact
  for follow-up.
- The **category** is one of a fixed set — `csam`, `malware_c2`,
  `illegal_marketplace`, `dmca`, `tos_violation`, `spam` — which
  deterministically fixes priority and SLA (below). There is no free-form
  `other`: an unroutable category is a report nobody owns.
- The reporter gets back a **reference and an SLA**, so the report can be
  followed up and the platform is held to its own clock.
- **Nothing the reporter says is fetched or executed.** Intake stores the
  pointer and the text — it never retrieves the reported content. (For a CSAM
  report, fetching the evidence would mean *the platform* downloading CSAM.)
- Intake **fails closed**: strict validation, field and body caps, per-IP rate
  limiting, and no acceptance without persistence — a report is never
  accepted-and-dropped.

### Triage — deterministic today, AI-shaped for later

> **AI triage is deferred (plan Decision 6).** No model calls run in v1. What
> ships is a **deterministic category → priority/SLA map** — deliberately the
> exact shape an AI triage would later emit, so the model can slot in behind it
> without changing a single consumer. The AI functions described below are the
> design for that later pass; they are **not** in force today. Read this section
> as "what triage will do," with the table as "what triage does now."

**1. Category → priority and SLA (in force, deterministic):**

| Category | Priority | SLA | Auto-Action |
|---|---|---|---|
| CSAM | P0 — Immediate | 2 hours | None automated. A *node scanner* match applies a protective hold (reversible); a *report* triggers nothing until a human acts. |
| Active malware / C2 | P1 — Critical | 4 hours | None (human decision) |
| Illegal marketplace | P1 — Critical | 8 hours | None (human decision) |
| DMCA copyright | P2 — Standard | 48 hours | None (human decision) |
| ToS violation (other) | P3 — Normal | 72 hours | None (human decision) |
| Spam / low-quality | P4 — Low | Best effort | None |

*(The earlier version of this table listed "Quarantine block, flag for NCMEC" as
an automatic action on a CSAM **report**. That was wrong twice over: replica
quarantine is unbuilt and deferred, and nothing about CSAM is ever actioned
automatically off an anonymous report — an unauthenticated stranger must not be
able to trigger enforcement by typing a wallet address into a form.)*

**The functions below are deferred, not in force:**

**2. Resource re-analysis in context of the report.** For template abuse reports, the AI re-analyzes the template's cloud-init script specifically through the lens of the reported concern. A report alleging "this template installs a keylogger" prompts a targeted analysis that a general review might have missed. This creates a feedback loop from abuse reporting into template review quality.

**3. Repeat report aggregation.** Multiple independent reports about the same resource are summarized. Volume and consistency of reports is surfaced to the human reviewer: "7 independent reports about this template allege C2 activity, all consistent in description."

### Prompt Injection Hardening

The AI triage system receives untrusted user content (report descriptions, template content). The system prompt must explicitly instruct the model to treat all user-provided content as untrusted input and flag any embedded instructions as a red flag rather than following them. Output is always structured JSON — never interpreted as instructions.

### Human Review Queue

Reports feed a human-reviewed admin queue, ordered by urgency (priority, then
age). The reviewer sees:
- Priority and category (deterministic today; AI-assigned later)
- The reported resource and the reporter's description
- **Previous enforcement history for the target wallet**, joined in place
- Actions: Dismiss / Warn / Takedown *(Warn and Dismiss share one mechanism —
  both close the report without withholding service — because there is no
  notification seam to reuse and one was not invented. Escalation to NCMEC is a
  human procedure, not a button; see §2's checklist.)*
- **Takedown withholds service on all three surfaces the wallet reaches others
  through**: its VMs stop, its nodes are suspended and its settlement withheld,
  and its published community templates are archived. Every action writes an
  audit row carrying the report's reference.
- *(AI reasoning and concerns appear here when triage lands — Decision 6.)*

**The AI never takes enforcement action autonomously.** All enforcement actions require human confirmation. This is non-negotiable — automated enforcement creates both false-positive harm and adversarial exploit surfaces.

### Enforcement Audit Trail

Every action taken on an abuse report is logged to an append-only `EnforcementActions` collection:

```json
{
  "actionId": "ENF-2026-00089",
  "reportReference": "ABU-2026-00123",
  "actionType": "wallet_suspension | vm_termination | template_archive | node_suspension | ncmec_report",
  "targetWallet": "0x...",
  "targetVmId": "...",
  "targetTemplateId": "...",
  "reason": "string",
  "category": "csam | dmca | ...",
  "actingAdmin": "0x... (admin wallet)",
  "timestamp": "ISO 8601",
  "notes": "string"
}
```

This audit trail is the evidentiary foundation for legal defensibility. It must be append-only (no updates or deletes), retained indefinitely, and accessible to legal counsel on demand.

---

## 6. Pillar 4 — Template Review Gate

### Why Templates Are the Highest-Risk Vector

A single malicious template, once publicly listed, can be deployed by thousands of users. A raw VM requires the bad actor to bring their own tooling — significant friction. A template eliminates that friction entirely. The template marketplace is therefore the highest-leverage point for content control: blocking one template blocks thousands of potential deployments.

### Review Workflow

```
Author submits template →           Draft
Author requests publish →           PendingReview   (author may withdraw → Draft)
    ↓                               [AI review will attach an assessment here — deferred, Decision 6]
Admin reviews →
    Approve → Published (listed + deployable)
    Reject  → Rejected, WITH A REQUIRED VISIBLE REASON
              (author edits and republishes → re-enters review)
```

**The invariant: a community template reaches `Published` only via an admin
approve action. There is no other path.** It is enforced at the service
boundary, not per-caller, so no endpoint can route around it.

**A published template is immutable in place.** Changes do not edit the live
version — they fork to a **draft revision** ("New version") that is reviewed
like any submission and promotes onto the live version on approval. Two things
fall out, both of which the earlier version of this document lacked: an approved
template cannot be silently swapped for something else after review, and the
live version is **never pulled from the marketplace while its replacement is
reviewed**. (An earlier design reset the live template to `PendingReview` on
edit — correct about the risk, but it punished authors for improving their work.)

**Rejection is visible, not silent.** `Rejected` carries the reason. There is no
separate "Request Changes" state — it would be the same thing with a softer name.

**Enforcement reaches published templates.** A takedown of the author archives
their published community templates and delists them; a wallet suspended while
a submission sits in the queue cannot have it approved. Review clears the
content, not the author.

Community templates always go through this workflow. Platform-curated seed
templates are reviewed once at authoring time and are exempt from the queue (but
must be retroactively reviewed against the rubric if the rubric changes — see
the seed-template item in §2).

### AI Review Scope

> **Deferred (plan Decision 6).** No AI review runs today; a nullable
> `AiAssessment` field is reserved on the template document so this slots in
> without rework. Human review (the rubric below) is the gate in force. This
> subsection is the design for later.

The AI reviewer analyzes:

- **Intent vs. description coherence** — does the cloud-init script do what the description claims?
- **Obfuscated commands** — base64-encoded payloads, heredocs with encoded content, eval chains, aliased shell functions
- **Data exfiltration patterns** — unexpected outbound connections, credential harvesting, phone-home scripts
- **Known malicious tooling** — C2 framework installers, cryptomining components, RAT components embedded in setup sequences
- **Category coherence** — an "AI/ML" template that installs network scanning tools is misclassified
- **Untrusted download chains** — `curl | bash` from unknown domains (extends existing regex validation)
- **Prompt injection attempts** — template content that attempts to manipulate the AI reviewer is itself a red flag

**Output schema:**
```json
{
  "riskLevel": "Low | Medium | High | Reject",
  "concerns": ["string"],
  "recommendation": "Approve | RequestChanges | Reject",
  "reasoning": "string",
  "reviewedAt": "ISO 8601"
}
```

### Human Review Rubric

Independent of AI assessment, the human reviewer applies this rubric:

| Check | Pass Criteria |
|---|---|
| Description accuracy | Template does what it claims |
| Source URLs | All external downloads from identified, reputable sources |
| Category match | Template category correctly describes its function |
| Port exposure | All exposed ports serve the stated purpose |
| Author history | No prior enforcement actions on this wallet |
| AI risk level | Low or Medium (High requires detailed justification; Reject blocks publish) |

### Existing Seed Template Review

The private browser template (Ultraviolet proxy) and Shadowsocks template in the current seed set were created before the review framework existed. Both must be retroactively reviewed against this rubric before public launch. They are not exempt because they are platform-authored.

---

## 7. Enforcement Mechanism

### The Enforcement Gate

> **Mechanism owned by the plan** (Decision 4; §3). This document owns the
> obligations. An earlier version specified a single `SuspendedWallets`
> collection; the built design is **two stores behind one predicate**, and the
> distinction is not cosmetic.

**Two boundaries, because there are two different questions:**

- **Internal suspension** — "this actor did something *here* and was suspended
  for it." Carried on the user record.
- **A provenance-bearing denylist** — "this wallet is blocked for a reason that
  originated *elsewhere*": sanctions lists, law-enforcement lists,
  cross-platform intelligence. Each entry records its **source**, reason,
  reference, who added it and when; it is bulk-importable, and **removal is
  scoped to the source** — a sanctions re-import must never lift an internal
  takedown, and lifting an internal takedown must never clear a sanctions
  block. It never creates an account for a wallet that has none.

**One predicate over both.** Every chokepoint asks a single question — *is this
wallet blocked?* — and gets one answer. One gate, two stores answering two
questions, is alignment, not duplication.

**Non-negotiables about how the gate is read:**

- **Server-side, at action time — never from the token.** An access token lives
  ~60 minutes and carries a stale snapshot; a block must bite immediately, not
  when the token happens to expire.
- **Address casing is normalized on both write and read.** Imported lists arrive
  lowercased; a case mismatch is a silent bypass, and a silent bypass in a
  sanctions gate is the whole failure.

**Where the gate is checked:** VM creation, node registration, template
publication — and, added since: node login (so a suspended operator cannot
re-enter scheduling) and the settlement loop (so no on-chain payout is signed to
a blocked payee).

### Admin Takedown

> **Mechanism owned by the plan.** An earlier version specified one
> `POST /api/admin/takedown` endpoint; what shipped is the same guarantees
> reached through the admin compliance surface (suspend / block / suspend-vm /
> resume-vm / cutoff). What matters is below, not the route list.

**Takedown withholds service on every surface the wallet reaches others
through** — atomically, in one admin action, always audited:

1. Its **VMs** stop (stopped, not deleted — reversible, disk state preserved).
2. Its **nodes** are suspended: scheduling off, re-login refused, and
   **settlement to it withheld** (records persist unsettled, so legitimately
   earned pre-block income still settles if the wallet is later cleared).
   Tenant VMs hosted on those nodes are evacuated through the existing
   disaster-recovery migration path before the node is cut off — **other
   people's workloads are not collateral damage in someone else's takedown.**
3. Its **published community templates** are archived and delisted. Templates
   are the amplification surface — one public template deploys many times — so a
   takedown that leaves the actor's listings live and deployable is not a
   takedown. Review cleared the content, not the author. This is **not** undone
   by a later unsuspend: the author republishes, which re-enters review. (Nodes
   and VMs are infrastructure and come back; a published template is
   distribution.)
4. Exactly one **`EnforcementActions`** record is written, carrying the report
   reference if the action came from the abuse queue.

**No step touches funds** — see below.

**A wallet with no account** is handled by the denylist, not by inventing an
account for it.

### Escrow Forfeiture — Removed From Scope (Decision 2)

**The platform does not, and cannot, seize or freeze user funds. This is
deliberate and is not a deferral.**

- `DeCloudEscrow.sol` v3 has **no function that can move user funds** — the
  contract says so explicitly (*"There is NO admin function to drain user
  funds"*) — and `withdrawBalance()` has no pause or freeze gate, so a user can
  always exit. Forfeiture would require a **contract redeployment** through the
  migration path.
- Faking usage to drain a balance through the settlement path is a defeatable
  override and an anti-pattern; it is not a design, and it will not be built.
- Enforcement is therefore **withhold-of-service only**: refuse to schedule,
  terminate what is running, suspend operated nodes, withhold settlement to a
  blocked payee, blacklist the wallet. Money that was deposited stays the
  user's.

**Why this is an asset, not a gap.** A platform that cannot touch custodied
value has a materially simpler regulatory posture (see the money-transmission /
custody question on the counsel checklist), and "user funds are sacrosanct" is a
promise the *contract itself* keeps — not one the operator asks to be trusted
on. Withholding service is sufficient to stop abuse; seizing deposits is not
what stops it.

**What remains available:** settlement to a blocked *operator* wallet is
**withheld, not forfeited** — the usage records persist unsettled and settle
normally if the wallet is later cleared (Decision 13). Permanent forfeiture in a
sanctions context is an out-of-band **legal** action, never a settlement-loop
decision.

---

## 8. DMCA Agent & Safe Harbor

### Filing Requirement

Register a DMCA Designated Agent with the US Copyright Office. This is a one-time administrative action.

- **Cost:** $6 USD
- **URL:** https://www.copyright.gov/dmca-directory/
- **Grants:** Section 512 safe harbor — platform is not liable for user-hosted copyright infringing content as long as it responds to valid takedown notices

**This must be completed before public launch.** Without it, the platform is fully liable for all copyright infringement hosted by users.

### DMCA Notice Processing

**No new code — this is process, not a build (plan Phase 5).** There is no
`POST /api/dmca` endpoint and there should not be one: a notice arrives at the
registered agent's published contact channel, or as a `dmca`-category report on
the **existing** abuse queue (P2/48h, deterministic — no AI). Takedown and
restoration run through the **existing** enforcement.

1. Copyright holder sends notice to the registered DMCA agent, or files a
   `dmca` report on the abuse queue.
2. Triage assigns P2 / 48h (deterministic map; AI triage is deferred).
3. A human — admin or counsel — validates the notice against §512(c)(3): the
   work identified, the infringing material identified, contact information, a
   good-faith-belief statement, a statement under penalty of perjury, and a
   signature.
4. If valid: the template is archived or the VM held, and the wallet warned,
   through the existing enforcement (which writes the audit row linked to the
   reference).
5. Acknowledgment to the claimant with the reference within 48 hours.
6. Counter-notice available to affected users (standard §512(g) process).

**An anonymous `dmca` report is a complaint, not a valid notice.** Anonymous
intake cannot satisfy §512(c)(3) — no identity, no perjury statement, no
signature. Judge such a report on its illegality/ToS merits; do **not** action it
*as a formal DMCA takedown*, or the safe-harbor process it belongs to is
theater.

### Counter-Notice Process

Users whose content is taken down via DMCA have the right to submit a counter-notice. Upon receipt of a valid counter-notice, the platform notifies the original claimant and restores the content after 10–14 business days unless the claimant files suit.

---

## 9. Wallet Identity & Liability

### Pseudonymous, Not Anonymous

A wallet address is pseudonymous — not anonymous. Every USDC deposit to the escrow contract is permanently recorded on the Polygon blockchain. Law enforcement with a court order can:

- Subpoena exchange KYC data for any wallet that touched a centralized exchange (the vast majority of USDC-holding wallets)
- Use on-chain analytics (Chainalysis, Elliptic, TRM Labs) to trace fund flows
- Cross-reference on-chain activity with IP logs, timing data, and other platform metadata

A wallet address gives law enforcement a starting point for investigation. That is the platform's contribution — maintaining records and cooperating. Criminal prosecution is law enforcement's responsibility.

### Escrow as a Sybil Resistance Bond

The minimum escrow deposit requirement (required before any VM can be deployed) makes disposable wallet attacks expensive:

- Each new wallet after a blacklist costs the attacker real USDC
- AI-assisted behavioral analysis can flag new wallets exhibiting the same patterns as a suspended wallet (similar cloud-init structure, same deployment timing, same infrastructure choices)
- The combination raises the cost of persistent bad actors without blocking legitimate users

This is not a KYC substitute. It is a **Sybil resistance mechanism** — it does not identify who the person is, but it makes throwaway identity expensive.

### On-Chain Traceability as a Deterrent

The platform should communicate clearly in the ToS and onboarding that wallet addresses are traceable. This is not a threat — it is factual and serves as a deterrent for bad actors who incorrectly assume blockchain activity is anonymous.

---

## 10. User Liability Under Wallet-Auth

### What the ToS Enforces Without KYC

| Enforcement Action | Mechanism |
|---|---|
| Stop the user immediately | Wallet suspension + VM termination |
| Financial penalty | **None at platform level** — deposits are always withdrawable (Decision 2). The only financial friction is that returning after a blacklist costs a fresh escrow bond. |
| Prevent return | Wallet blacklist (new wallet costs real money) |
| Law enforcement cooperation | Wallet address + on-chain tx history + deployment logs |
| DMCA compliance | Takedown + counter-notice process |

### What Requires External Investigation

| Goal | Who Does It |
|---|---|
| Identify the human behind a wallet | Law enforcement (court order → exchange KYC) |
| Criminal prosecution | Law enforcement + prosecutors |
| Civil damages | Plaintiff + courts |
| Recovery of platform costs from a violator | Counsel + courts (no platform-level seizure exists) |

The platform's role ends at providing records, cooperating with valid legal process, and taking platform-level enforcement action. This is the correct scope and is sufficient for the "reasonable steps" legal standard.

### KYC — Deliberately Deferred

Mandatory KYC is incompatible with the censorship-resistance mission at this stage. A political dissident who KYCs to access the platform has created a record that can be seized by their government. The four-pillar framework is sufficient for launch and for the platform's legal obligations without requiring identity verification.

Optional KYC tiers (for node operators who want to serve verified-only users, or for specific high-risk template categories) remain a future option but are not required for the compliance baseline.

---

## 11. What This Framework Does Not Cover

Being explicit about gaps prevents false confidence:

**Raw VMs without templates.** A user can deploy a blank Ubuntu VM and run anything inside it. The template review gate does not apply. The escrow minimum and wallet blacklist are the only levers. This is the same exposure as any standard VPS provider. It is acceptable and industry-standard.

**Newly generated CSAM.** Hash matching catches known material. An uncensored image generation model running on a GPU node could produce new material not yet in the NCMEC database. The template review gate reduces this risk by scrutinizing AI generation templates, but cannot eliminate it entirely.

**End-to-end encrypted traffic.** VM traffic transiting the relay network is encrypted at the WireGuard layer. The platform cannot inspect it. This is by design (censorship resistance). It is the same trade-off made by every VPN provider and Tor.

**Content inside VMs at rest.** The orchestrator has no visibility into what is running or stored inside a VM. Detection of in-VM illegal content depends entirely on abuse reports and behavioral signals.

---

## 12. Build Checklist

All items required before public launch. **Build state is read from
`COMPLIANCE_INTEGRATION_PLAN.md` §7, not from here** — this list is the
obligation set; the plan is the record of what has landed.

> **The shape of what remains: the code is ahead of the paper.** Every technical
> pillar below is built except the CSAM matcher. Almost everything still open is
> **administrative** — forms, accounts, and counsel — and one of those gaps is
> live rather than theoretical (NCMEC, below).

### Administrative (Non-Code) — **none of these are done; all block launch**
- [ ] Retain legal counsel familiar with CFAA, DMCA, and 18 U.S.C. § 2258A
- [ ] File DMCA Designated Agent with US Copyright Office ($6, https://www.copyright.gov/dmca-directory/) — **the load-bearing item for §512**: without it, the entire DMCA process in §8 confers no safe harbor. One form, $6.
- [ ] Establish NCMEC CyberTipline account and reporting procedure — **the live gap.** The abuse intake is deployed and public, so a credible CSAM report can arrive today; that is *actual knowledge*, and §2258A's reporting duty attaches to it with no registered channel to discharge it. Not blocked on engineering.
- [ ] Draft + counsel-review the ToS text — **wallets are signing a draft today.** The acceptance machinery is built and gating VM creation, but the embedded document says "NOT YET IN EFFECT. REQUIRES LEGAL REVIEW." Every enforcement action the ToS is meant to make defensible currently rests on unreviewed text. Includes the §512 repeat-infringer clause, and the reserved cost-recovery clause **only if counsel advises** (§4).
- [ ] Counsel: **money-transmission / custody classification** — does any fund-control capability trigger MSB/custody obligations? (Decision 2's answer is that the platform has none; confirm that is the right posture.)
- [ ] Counsel: **OFAC sanctioned-address screening** — the SDN list includes wallet addresses. The denylist's sanctions-source import path is **built and unused**: no list has been imported. Strict-liability territory, and the platform pays USDC to node operators.
- [ ] Microsoft CSAM Matching API account + agreement — gates the real matcher (weeks of vetting; apply early).
- [ ] Retroactive review of seed templates (Private Browser, Shadowsocks) against the review rubric — platform-authored is not exempt.

### Technical — Enforcement — ✅ built (plan §7)
- [x] The enforcement gate — a provenance-bearing denylist + internal suspension behind one predicate (superseded the single `SuspendedWallets` collection; Decision 4), checksum-normalized, source-scoped removal, bulk-importable.
- [x] Atomic, audited admin takedown — VMs stopped, nodes suspended + settlement withheld + drained, published community templates archived (superseded the single `/api/admin/takedown` route; same guarantees).
- [x] `EnforcementActions` append-only audit collection.
- [x] Gate checked server-side at action time at **five** chokepoints: VM creation, node registration, template publication, node login, settlement.
- [x] Single-VM administrative hold, enforced node-side against every revival path; delete-while-held refused for evidence preservation.
- [x] ~~Escrow forfeiture admin tool~~ — **must not be built** (Decision 2): the contract has no fund-moving function and enforcement is withhold-of-service only.

### Technical — CSAM Filtering
- [x] ~~NCMEC hash database integration at Block Store ingestion~~ — **must not be built** (§3, Decision 1): block-level hash matching is technically infeasible (1 MB raw sectors are not decodable whole files). The detection surface is **node-level filesystem scanning**.
- [x] ~~*Automated* NCMEC CyberTipline report trigger on detection~~ — **must not be built** (§3, and the enforcement rule below): automated enforcement on a hash match is prohibited. A match suspends the VM (protective, reversible) and raises a P0 item; **a human confirms before any NCMEC report or blacklist.**
- [ ] Real matcher behind the built `ICsamScanner` seam — gated on the Microsoft CSAM Matching API agreement (see Administrative above). The seam, the honest stub, fleet-wide enrollment, the result-gate, and the node→P0-queue report chain are **built** (plan §7, 2026-07-08 → 10).
- [ ] **NCMEC CyberTipline reporting procedure** — human-operated, off the P0 queue. Gated on the CyberTipline account (see Administrative above). *This is the live gap: the abuse intake is deployed, so a credible report can arrive today and create a reporting obligation with no registered channel to discharge it.*
- [ ] Replica quarantine for the retroactive / backlog / bypassed-origin cases — designed (plan Decision 14: admin CID declaration → signed broadcast → move-to-sealed + CID denylist), **deferred**; open item is the sealed evidence store (counsel). The reactive P0 path is the interim control.

### Technical — ToS — ✅ built (the machinery; the *text* is the open item above)
- [x] Versioned ToS document + SHA-256 of its bytes; fail-closed if the document is absent.
- [x] Wallet-signed acceptance (EIP-191), reusing the existing signature primitive — no new crypto.
- [x] `tos_acceptances` collection.
- [x] VM creation blocked without a signature over the **current** version+hash, checked server-side at action time (never from the token).
- [x] A version bump invalidates prior acceptances automatically (the cache keys on version+hash) and re-prompts.
- [ ] **Decide: re-sign grace period.** This document (§4) promises 30 days before VM creation is blocked; the code blocks immediately on a bump. The code is *stricter* than the promise — safe, but one of the two must move. A product call, not an engineering one.

### Technical — Abuse Reporting — ✅ built (plan §7)
- [x] `POST /api/abuse` public unauthenticated intake — rate-limited per IP, strictly validated, body-capped, fails closed, never fetches reported content.
- [x] `abuse_reports` collection + atomic per-year `ABU-YYYY-NNNNN` references.
- [x] Deterministic category → priority/SLA triage (no AI — Decision 6).
- [x] Admin queue UI ordered by urgency, each report joined to the target's enforcement history; dismiss / warn / takedown routed through enforcement, audit linked by reference.
- [x] Acknowledgment with reference + SLA.
- [ ] AI triage service — **deferred** (Decision 6), seam kept.

### Technical — Template Review — ✅ built (plan §7)
- [x] `PendingReview` + `Rejected` statuses (appended, so existing serialization is unchanged) + review fields.
- [x] Publish gated on human approval, enforced at the service boundary across create / publish / update / deploy.
- [x] Admin review queue UI with the full deployable payload, inline approve / reject (reason required).
- [x] Published templates immutable in place; changes go through reviewed draft revisions, so the live version is never pulled during review.
- [x] Enforcement reaches templates: takedown archives + delists the author's published community templates; a suspended author's pending submission cannot be approved.
- [ ] AI review service + assessment — **deferred** (Decision 6); the nullable `AiAssessment` field is reserved.

---

*For platform features context, see PROJECT_FEATURES.md. For strategic context, see PROJECT_MEMORY.md.*
