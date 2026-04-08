# DeCloud Platform — Compliance & Legal Framework

**Last Updated:** 2026-04-08
**Status:** Planned — Pre-Launch Requirement
**Purpose:** Authoritative reference for DeCloud's content policy, legal liability framework, abuse handling, and enforcement architecture.

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

Child Sexual Abuse Material is illegal in every jurisdiction on Earth. Hosting it, even unknowingly, exposes platform operators to federal criminal liability under 18 U.S.C. § 2258A (US) and equivalent statutes elsewhere. Unlike all other content violations, this one carries criminal — not merely civil — exposure.

### Implementation Approach

**Hash-based detection at Block Store ingestion.** DeCloud's Block Store is content-addressed (CID-based). NCMEC (National Center for Missing & Exploited Children) maintains a hash database of known CSAM material (PhotoDNA). At block ingestion, each block's hash is compared against the NCMEC database. No content inspection is required — only hash comparison. This approach:

- Preserves the censorship-resistance architecture (we read no content, only compare hashes)
- Catches all known CSAM material reliably
- Is the industry standard (used by Google, Microsoft, Facebook, Apple, Cloudflare)

**Limitation:** Hash matching only catches *known* CSAM already in the NCMEC database. Newly generated material will not yet be in the database. This is a known, accepted limitation industry-wide. The Template Review Gate (Pillar 4) is the complementary control that prevents the most obvious CSAM-generation pipelines from being publicly listed.

### NCMEC Reporting

Upon detection:
1. Block is immediately quarantined (removed from serving, preserved for evidence)
2. NCMEC CyberTipline report filed within 24 hours (legally required under 18 U.S.C. § 2258A)
3. Associated wallet blacklisted
4. All VMs owned by that wallet terminated
5. Incident logged in enforcement audit trail

**The AI abuse triage system must never be used to evaluate CSAM content directly.** AI triage role is limited to: classify the report category as CSAM, set urgency P0, trigger NCMEC protocol. No content analysis by AI.

---

## 4. Pillar 2 — Terms of Service

### What the ToS Achieves

A Terms of Service for a wallet-authenticated platform serves four distinct purposes — none of which is primarily "let us sue the user":

1. **Establishes the platform's right to act** — suspend wallets, forfeit escrow, terminate VMs — without those actions being challengeable as arbitrary.
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

**Escrow forfeiture clause:** "In the event of a verified Terms of Service violation, the platform may apply any escrow balance held under your wallet address toward investigation costs, takedown costs, and platform damages. Remaining balance after cost recovery will be returned to the wallet address."

**Blockchain transparency notice:** "You acknowledge that your wallet address and all associated on-chain transactions are permanently recorded on a public blockchain. The platform may share your wallet address and associated transaction history with law enforcement agencies upon receipt of a valid legal request."

**Prohibited content clause:** An explicit enumeration of prohibited content categories (CSAM, illegal marketplaces, C2 infrastructure, etc.) with acknowledgment that the escrow forfeiture and wallet suspension provisions apply to these categories without prior notice.

**Cooperation clause:** "The platform will cooperate with law enforcement agencies and regulatory bodies upon receipt of valid legal process, including but not limited to court orders, subpoenas, and NCMEC CyberTipline mandates."

### ToS Versioning

The ToS is versioned. When the ToS changes materially:
- Existing users are prompted to re-sign on next platform action
- The new version hash is stored against the wallet alongside the original acceptance
- Users who do not re-sign within 30 days have VM creation blocked (existing VMs continue running)

---

## 5. Pillar 3 — Abuse Reporting & AI Triage

### Public Intake Endpoint

```
POST /api/abuse
```

**Authentication:** None required. Anyone must be able to report, including NGOs, automated scanners, and law enforcement liaisons.

**Request schema:**
```json
{
  "resourceType": "vm | template | node | wallet",
  "resourceId": "string",
  "category": "csam | illegal_marketplace | malware_c2 | dmca | tos_violation | spam | other",
  "description": "string (max 2000 chars)",
  "reporterContact": "string (optional — email or wallet for follow-up)",
  "evidenceUrl": "string (optional)"
}
```

**Response:**
```json
{
  "referenceId": "ABU-2026-00123",
  "category": "dmca",
  "urgencyLevel": "P2",
  "sla": "48 hours",
  "message": "Your report has been received. Reference: ABU-2026-00123."
}
```

### AI Triage Pipeline

Every incoming abuse report is processed by the AI triage service before entering the human review queue. The AI performs three functions:

**1. Category confirmation and urgency assignment:**

| Category | AI-Assigned Priority | SLA | Auto-Action |
|---|---|---|---|
| CSAM | P0 — Immediate | 2 hours | Quarantine block, flag for NCMEC |
| Active malware / C2 | P1 — Critical | 4 hours | None (human decision) |
| Illegal marketplace | P1 — Critical | 8 hours | None (human decision) |
| DMCA copyright | P2 — Standard | 48 hours | None (human decision) |
| ToS violation (other) | P3 — Normal | 72 hours | None (human decision) |
| Spam / low-quality | P4 — Low | Best effort | None |

**2. Resource re-analysis in context of the report.** For template abuse reports, the AI re-analyzes the template's cloud-init script specifically through the lens of the reported concern. A report alleging "this template installs a keylogger" prompts a targeted analysis that a general review might have missed. This creates a feedback loop from abuse reporting into template review quality.

**3. Repeat report aggregation.** Multiple independent reports about the same resource are summarized. Volume and consistency of reports is surfaced to the human reviewer: "7 independent reports about this template allege C2 activity, all consistent in description."

### Prompt Injection Hardening

The AI triage system receives untrusted user content (report descriptions, template content). The system prompt must explicitly instruct the model to treat all user-provided content as untrusted input and flag any embedded instructions as a red flag rather than following them. Output is always structured JSON — never interpreted as instructions.

### Human Review Queue

The AI triage output feeds a human-reviewed admin queue, ordered by urgency. The human reviewer sees:
- AI-assigned priority and category
- AI reasoning and specific concerns
- Links to the reported resource
- Previous enforcement history for the wallet
- Action buttons: Dismiss / Warn / Takedown / Escalate to NCMEC

**The AI never takes enforcement action autonomously.** All enforcement actions require human confirmation. This is non-negotiable — automated enforcement creates both false-positive harm and adversarial exploit surfaces.

### Enforcement Audit Trail

Every action taken on an abuse report is logged to an append-only `EnforcementActions` collection:

```json
{
  "actionId": "ENF-2026-00089",
  "reportReference": "ABU-2026-00123",
  "actionType": "wallet_suspension | vm_termination | template_archive | escrow_forfeiture | ncmec_report",
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
Author submits template →
Status: Draft
    ↓
Author requests publish →
Status: PendingReview
    ↓
AI review runs (automated, async) →
AI Assessment stored on template document
    ↓
Admin reviews AI assessment + template →
    ↓
Approve → Status: Published (public in marketplace)
Reject  → Status: Draft (author notified with reasons)
Request Changes → Status: Draft (specific feedback provided)
```

Community templates always go through this workflow. Platform-curated seed templates are reviewed once at authoring time and are exempt from the queue (but should be retroactively reviewed against the rubric if the rubric changes).

### AI Review Scope

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

### Wallet Blacklist

A `SuspendedWallets` MongoDB collection enforces platform bans:

```json
{
  "walletAddress": "0x...",
  "reason": "csam | dmca | malware | tos_violation",
  "suspendedAt": "ISO 8601",
  "suspendedBy": "0x... (admin wallet)",
  "enforcementActionId": "ENF-2026-00089",
  "notes": "string",
  "isPermanent": true,
  "expiresAt": null
}
```

The orchestrator checks this collection at VM scheduling time. A suspended wallet cannot create new VMs, deploy templates, or register nodes. Existing VMs are terminated as part of the takedown action.

### Admin Takedown Endpoint

```
POST /api/admin/takedown
Authorization: Bearer <admin-token>
```

**Request:**
```json
{
  "walletAddress": "0x... (optional)",
  "vmId": "string (optional)",
  "templateId": "string (optional)",
  "reason": "string (required)",
  "category": "csam | dmca | malware | tos_violation (required)",
  "reportReference": "ABU-2026-00123 (optional)"
}
```

**What it orchestrates atomically:**
1. Terminate all running VMs owned by the wallet
2. Archive all public templates authored by the wallet
3. Add wallet to `SuspendedWallets`
4. Apply escrow forfeiture if applicable (manual step requiring separate confirmation)
5. Write to `EnforcementActions` audit log
6. Return summary of actions taken

This single endpoint replaces ad-hoc use of the VM delete endpoint for enforcement actions and ensures the audit trail is always written.

### Escrow Forfeiture

Escrow forfeiture is a separate, deliberately manual step requiring explicit admin confirmation. It is not bundled into the standard takedown because:

- It is irreversible
- It requires proportionality judgment (forfeit all vs. forfeit amount covering damages)
- It may require legal counsel review for large balances

The `DeCloudEscrow.sol` contract's authorized caller mechanism allows the orchestrator to call settlement functions against a wallet. The ToS establishes the legal basis for this action.

---

## 8. DMCA Agent & Safe Harbor

### Filing Requirement

Register a DMCA Designated Agent with the US Copyright Office. This is a one-time administrative action.

- **Cost:** $6 USD
- **URL:** https://www.copyright.gov/dmca-directory/
- **Grants:** Section 512 safe harbor — platform is not liable for user-hosted copyright infringing content as long as it responds to valid takedown notices

**This must be completed before public launch.** Without it, the platform is fully liable for all copyright infringement hosted by users.

### DMCA Notice Processing

1. Copyright holder sends notice to the registered DMCA agent (email or `POST /api/dmca`)
2. AI triage classifies as P2 (DMCA), assigns 48h SLA
3. Human reviewer validates notice (must include: identification of copyrighted work, identification of infringing material, contact info, good-faith statement, accuracy statement, signature)
4. If valid: template archived or VM flagged, wallet warned
5. Acknowledgment sent to claimant with reference ID within 48 hours
6. Counter-notice process available to affected users (standard DMCA counter-notice)

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
| Financial penalty | Escrow forfeiture (ToS clause, no court order required) |
| Prevent return | Wallet blacklist (new wallet costs real money) |
| Law enforcement cooperation | Wallet address + on-chain tx history + deployment logs |
| DMCA compliance | Takedown + counter-notice process |

### What Requires External Investigation

| Goal | Who Does It |
|---|---|
| Identify the human behind a wallet | Law enforcement (court order → exchange KYC) |
| Criminal prosecution | Law enforcement + prosecutors |
| Civil damages beyond escrow | Plaintiff + courts |

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

All items required before public launch:

### Administrative (Non-Code)
- [ ] Retain legal counsel familiar with CFAA, DMCA, and 18 U.S.C. § 2258A
- [ ] File DMCA Designated Agent with US Copyright Office ($6, https://www.copyright.gov/dmca-directory/)
- [ ] Establish NCMEC CyberTipline account and reporting procedure
- [ ] Draft ToS document (legal counsel review required)
- [ ] Retroactive review of seed templates (Private Browser, Shadowsocks) against review rubric

### Technical — Enforcement
- [ ] `SuspendedWallets` MongoDB collection + orchestrator check at VM scheduling
- [ ] `POST /api/admin/takedown` — atomic takedown endpoint with audit logging
- [ ] `EnforcementActions` append-only collection
- [ ] Wallet blacklist check in VM creation path (`VmService.cs`)
- [ ] Escrow forfeiture admin tool (separate from takedown, requires explicit confirmation)

### Technical — CSAM Filtering
- [ ] NCMEC hash database integration at Block Store ingestion
- [ ] Block quarantine mechanism (remove from serving, preserve for evidence)
- [ ] Automated NCMEC CyberTipline report trigger on detection

### Technical — ToS
- [ ] ToS version document with hash
- [ ] Wallet-signed acceptance flow at first platform access
- [ ] `TosAcceptances` collection: `{ walletAddress, tosVersion, tosHash, signature, timestamp }`
- [ ] VM creation blocked for wallets without current ToS acceptance
- [ ] Version bump triggers re-acceptance prompt

### Technical — Abuse Reporting
- [ ] `POST /api/abuse` public endpoint
- [ ] `AbuseReports` MongoDB collection
- [ ] AI triage service (`AiReviewService.TriageAbuseReportAsync`)
- [ ] Admin abuse queue UI (ordered by urgency)
- [ ] Acknowledgment response with reference ID and SLA

### Technical — Template Review
- [ ] `TemplateStatus.PendingReview` status added to enum
- [ ] AI review service (`AiReviewService.ReviewTemplateAsync`)
- [ ] AI assessment stored on `VmTemplate` document
- [ ] Admin template review queue UI
- [ ] Publish workflow gated on human approval (not just author request)

---

*For platform features context, see PROJECT_FEATURES.md. For strategic context, see PROJECT_MEMORY.md.*
