# DeCloud Terms of Service — DRAFT

> **DRAFT — NOT YET IN EFFECT. REQUIRES LEGAL REVIEW.**
> This skeleton enumerates the clauses the compliance framework requires for a
> wallet-authenticated platform. It is **not** legal text and must be drafted
> and reviewed by counsel familiar with CFAA, DMCA, and 18 U.S.C. § 2258A
> (see the counsel checklist in `COMPLIANCE_INTEGRATION_PLAN.md`) before launch.
>
> The exact bytes of the final document determine its SHA-256 hash, which is what
> wallets sign. Any edit changes the hash and invalidates prior acceptances —
> bump `Tos:Version` whenever the text changes materially.

**Version:** 2026-06-26-draft
**Effective:** (pending)

---

## 1. Acceptance

By connecting a wallet and using DeCloud, you accept these Terms. Acceptance is
recorded as a cryptographic signature from your wallet over a message naming this
version and its hash.

## 2. User Responsibility

You are solely responsible for all content deployed, stored, or transmitted
through VMs and infrastructure associated with your wallet address.

## 3. Prohibited Content

The following are prohibited without exception: child sexual abuse material
(CSAM); illegal marketplaces; command-and-control (C2) infrastructure; malware
hosting; and human-trafficking facilitation. Violation may result in immediate
wallet suspension, VM termination, and blacklisting without prior notice.

## 4. Blockchain Transparency

You acknowledge that your wallet address and all associated on-chain transactions
are permanently recorded on a public blockchain. The platform may share your
wallet address and associated transaction history with law-enforcement agencies
upon receipt of a valid legal request.

## 5. Law-Enforcement Cooperation

The platform will cooperate with law-enforcement agencies and regulatory bodies
upon receipt of valid legal process, including court orders, subpoenas, and NCMEC
CyberTipline mandates.

## 6. Enforcement

Enforcement is **withhold-of-service**: the platform may refuse to schedule VMs
for, terminate the running VMs of, and blacklist a wallet that violates these
Terms. The platform does **not** seize or freeze funds held in the escrow
contract; you may withdraw your unused balance at any time.

## 7. Repeat-Infringer Termination

Accounts (wallets) that repeatedly infringe copyright will be terminated. *(Required for DMCA Section 512 safe harbor.)*

## 8. Cost Recovery — Reserved (Not Currently Enforced)

> Include this clause **only if counsel advises.** The current escrow contract has
> no fund-seizure capability; this is a reserved future right, not an active one.

The platform reserves the right to introduce escrow-based cost recovery for
verified violations in a future version of the escrow contract. No such capability
exists today and none is exercised under the current contract.

## 9. Changes to These Terms

These Terms are versioned. When they change materially, you will be prompted to
re-sign. Continued use requires acceptance of the current version.

---

*Headings reflect the required clauses from `COMPLIANCE.md` §4 and the integration
plan. Final wording is counsel's responsibility.*
