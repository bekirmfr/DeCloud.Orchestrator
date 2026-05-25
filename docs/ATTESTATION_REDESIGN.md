# Attestation & Economic Security Redesign

**Status:** Draft for review
**Supersedes:** In-VM ephemeral-key attestation (`AttestationService`, `AttestationSchedulerService`, `decloud-agent`)
**Related:** Billing & Settlement (`BillingService`, `SettlementService`)

---

## Summary

The current attestation system tries to prove VM existence and resource provisioning from inside the tenant VM, using an ephemeral-key challenge-response protocol with RTT-bounded timing. This document proposes replacing it with a three-layer model that moves attestation entirely out of the tenant VM:

1. **Authenticated node-agent heartbeats** establish VM liveness and drive billing periods. This is already the source of truth for billing; the redesign formalizes that fact and stops pretending otherwise.
2. **Host-side proof of capacity** binds the operator's declared host capacity to ongoing memory-hard computation that the operator's machine must actually perform.
3. **Bonded earnings** provide economic security without upfront stake. Operators vest earnings into a target-sized bond that gets slashed on detected fraud. No capital is required to join the network.

The in-VM attestation agent is deleted. The system stops trying to solve a problem (timing-based proof of VM integrity over the internet) that the security literature has shown cannot be solved on commodity hardware, and starts solving the problem that actually matters (bounding operator fraud economically).

---

## Why the current system is being replaced

The current design has three independent flaws that cannot be fixed by tuning thresholds.

**The timing channel is structurally unsound.** Pioneer- and SWATT-style attestation requires microsecond-precise timing because the gap between honest and malicious execution is small. Over the public internet, with operators behind CGNAT and WireGuard relays, round-trip variance is in the tens to hundreds of milliseconds. No timeout setting can simultaneously achieve a low false-negative rate and a meaningful security guarantee. Every false negative we observe in production is the system honestly reporting that it has been asked to measure something it cannot measure.

**The in-VM agent conflates subject and prover.** The agent inside the VM is both the thing whose existence we want to verify and the thing producing the evidence. When the tenant controls the VM, they control the prover. When the operator controls the host, they control the path to the prover. Five completely different failure modes — agent never installed, agent crashed, agent disabled by tenant, network unreachable, VM doesn't exist — produce identical telemetry. The system cannot distinguish them and therefore cannot act on them correctly.

**The cuckoo attack is unsolvable here.** A node operator who wants to commit fraud can relay attestation challenges to a different machine that produces valid responses. Without trusted hardware to bind the response to a specific physical host, no protocol-level check on commodity x86_64 can detect this. The current design provides no defense against the dominant adversary's most plausible attack.

The implication is not that we should add more checks to the in-VM agent. It is that the in-VM agent is the wrong layer for the problem.

---

## Design principles

Four principles drive the new architecture.

**Attestation signals must come from points the adversary does not control.** A signal produced inside an adversarial environment cannot be used as evidence about that environment. The in-VM agent fails this test; the node-agent (authenticated to the orchestrator with a key the orchestrator issued) does not, because lying through it has predictable detectable consequences when cross-validated against other signals.

**No security claim that physics doesn't support.** RTT-based timing on the internet cannot prove what microsecond-precision timing on a single hop can prove. The redesign retains timing measurements for liveness — did the node respond? — but makes no security claim about response speed.

**Economic security through bonded earnings, not upfront capital.** Operators must not be required to bring capital to join the network. Bonding through retained earnings achieves the same security posture (skin in the game) without the onboarding friction that has limited adoption in stake-based competitors.

**Failure modes collapse to one policy.** Whenever the orchestrator cannot confirm a VM is operating correctly — for any reason — billing pauses. The system does not attempt to distinguish "tenant disabled the agent" from "operator unreachable" from "VM crashed." All are handled identically: stop charging until the situation resolves.

---

## Threat model

The redesign defends against two adversaries with very different incentive profiles.

**The node operator** is the high-value adversary. They have full hypervisor access, motive (revenue from over-subscription), and means (ability to claim resources they don't physically have). A successful operator-fraud event affects many tenants and damages the platform's reputation systemically. The bulk of the security budget targets this adversary.

**The tenant** is the low-value adversary. Their fraud upside is bounded: at worst, they get free compute until the next attestation gap triggers billing pause. They have no incentive or means to harm other tenants. Defending against tenant tampering with the in-VM environment is not worth the architectural complexity it requires. The new design accepts this and bounds the loss instead.

**Out of scope.** The cuckoo attack — an operator relaying attestation challenges to a second machine that proxies for the claimed host — remains theoretically possible. No commodity-hardware design can prevent it. The bonded-earnings layer makes this attack economically irrational at scale (it requires running a second machine for every fraudulent one, eliminating the savings), but provides no cryptographic guarantee.

---

## Architecture

The new system has three layers, each with a single responsibility.

### Layer 1: Node-agent heartbeat (liveness)

The node agent already runs on every operator host, authenticated to the orchestrator with an API key the orchestrator issued at registration. Every heartbeat already reports VM state from libvirt (domains running, paused, stopped), per-VM resource usage, and host-level metrics. This is the source of truth for billing today, even though the codebase has historically pretended attestation was.

The redesign formalizes this. The heartbeat is the authoritative signal for:

- VM existence and power state
- Billing period boundaries (started, stopped, paused)
- Resource usage telemetry
- Host operational status

Heartbeat loss for N consecutive cycles pauses billing for all VMs on that node. After M cycles, VMs are marked failed and tenants are notified. There is no longer any per-VM attestation channel; per-VM correctness is inferred from per-host signals (Layer 2) and economic exposure (Layer 3).

The in-VM agent is deleted. Cloud-init templates no longer need to install or run an attestation binary. Template authors cannot accidentally disable attestation because there is nothing to disable.

### Layer 2: Host-side proof of capacity (operator honesty)

The node agent runs a continuous memory-hard proof-of-capacity (PoC) loop on the host, sized to the operator's *unsold* capacity. Each epoch, the orchestrator issues a fresh challenge nonce. The node agent runs PoC workers using working sets that physically require the declared unsold cores and RAM, and submits the proofs it found.

The orchestrator scores submissions against an expected rate derived from the declared CPU model and core count. Sustained shortfall indicates one of two failure modes:

- The host doesn't have the capacity it claims (over-declaration fraud).
- The host has the capacity but is stealing cycles for unreported workloads (over-subscription fraud).

Both warrant slashing. Both are detected from the same signal.

The PoC runs on the host, not in tenant VMs. It does not depend on tenant cooperation. It cannot be relocated to a cuckoo machine without that machine also bearing the cost of the computation, which inverts the economics of the attack.

The exact PoC algorithm is deferred to a follow-up design (see Open Questions). RandomX is the leading candidate because its 2 GiB-per-instance fast-mode working set physically binds proofs to claimed RAM. Argon2id is a simpler alternative if cross-platform binary distribution proves painful.

### Layer 3: Bonded earnings (economic security)

Every operator has a target bond, `B_target`, sized to the platform's exposure to that operator's potential fraud. Until accumulated unwithdrawn earnings reach `B_target`, all earnings vest into the bond and nothing is withdrawable. Once the bond is full, new earnings become withdrawable normally. Slashing draws from the bond. If the bond falls below target, the operator is back in vesting mode until it refills.

This replaces traditional upfront stake. No capital is required to join — the operator pays in their bond by working honestly. The mechanism is detailed in the next section.

---

## Bonded earnings: detailed mechanics

### Target sizing

The target represents the platform's worst-case exposure to a given operator's fraud:

```
B_target = declared_capacity_value_per_hour × max_detection_window_hours + tenant_compensation_buffer
```

Where:

- `declared_capacity_value_per_hour` is the operator's earning rate at full utilization, given their declared host capacity.
- `max_detection_window_hours` is the longest plausible time between fraud starting and detection firing.
- `tenant_compensation_buffer` is a small fixed addition to cover edge cases not captured by the detection window.

For an operator with $1/hour earning capacity and a 7-day detection window, the target is around $170. For a $20/hour operator, around $3,400. The target scales with declared capacity, so larger operators face proportionally larger bonds — which is correct, because they create proportionally larger exposure.

The detection window is bounded by the slowest signal the platform actually slashes on. With continuous host PoC, hard fraud (operator stops responding to challenges) is detected within one epoch — seconds. Subtler fraud (slight under-provisioning detected only through benchmark drift over weeks) is the slow path and sets the lower bound for the window.

### Vesting

Until `bonded_balance >= B_target`:

- 100% of earnings flow into the bond.
- Nothing is withdrawable.
- The operator's dashboard shows clearly: "Bond: $X / $Y. Earning to bond."

Once `bonded_balance >= B_target`:

- New earnings are immediately withdrawable.
- The bond remains locked at target.
- The operator's dashboard shows: "Bond: complete. Withdrawable: $Z."

If the bond falls below target (through slashing), vesting resumes automatically on the next settlement.

### Capacity ramp

A new operator with a small bond should not be allowed to host the full declared capacity. If they could, a first-day operator with $5 of bond could be assigned $500 of VMs and walk away after fraud with only $5 slashed.

The scheduler caps assigned capacity to the level the current bond actually covers:

```
effective_capacity = declared_capacity × (bonded_balance / B_target)
```

A new operator hosts a fraction of their declared capacity proportional to how far they've vested. As they earn honestly, the cap rises. At full bond, they can host their full declared capacity. The bootstrap fraud window is closed: the operator can never have more capacity exposed than their bond covers.

### Slashing

When fraud is detected:

- Slash amount is drawn from the bond. Slashing never makes the operator owe money — it only reduces the bond, possibly to zero.
- Minor signals (a single failed PoC epoch, brief heartbeat gap) slash a small fixed fraction so honest mistakes are recoverable.
- Severe signals (sustained PoC failure, confirmed audit drift, tenant fraud complaints with evidence) slash large fractions and freeze new placements pending review.
- If the bond reaches zero, the operator is deregistered and existing VMs migrate or expire.

Slashing rates start conservative and are tuned from production data. A starting point: 1% per failed PoC epoch, 25% on confirmed audit drift, 100% on confirmed fraud with manual review.

### Tenant compensation

When a tenant is harmed by operator fraud (downtime, under-provisioning, data loss), they are compensated from a **platform reserve**, not from the offending operator's bond.

The reserve is funded by the platform fee. The operator's bond is purely a deterrent and revenue mechanism — the platform keeps slashed funds. Tenant compensation is decoupled so tenants are made whole quickly regardless of whether the operator has bond available.

This separation matters operationally. If tenant refunds came from operator bonds, the platform's solvency would depend on always having enough bonded balance to cover claims, which isn't true for small new operators. Decoupling makes tenant protection a fixed platform liability funded predictably from the fee.

### Exit

An operator wanting to leave gracefully:

1. Marks themselves as draining (no new VM placements).
2. Existing VMs migrate or expire.
3. Cooldown period begins (e.g., 30 days, exceeding the slowest detection signal).
4. During cooldown, the bond remains locked. New audit signals can still trigger slashing.
5. After cooldown with no flags, the bond releases.

The cooldown is non-negotiable. Without it, an operator could commit fraud, immediately try to exit, and race detection. The cooldown bounds the platform's exposure exactly to the period during which fraud might still be discovered.

### Operator dashboard

Two balances are visible to the operator at all times:

- **Bonded balance:** current / target, with vesting progress and slashing history.
- **Withdrawable balance:** zero until bond is full, then accumulates normally.

The system is honest with the operator about exactly where they stand, when they will see withdrawable earnings, and what events would slash their bond.

---

## How the layers combine

The three layers are evaluated independently per VM and per host. Their outputs combine into billing decisions through a simple policy:

**Bill the VM if all of these are true:**

- The host's most recent heartbeat is within the liveness window.
- The host's PoC submissions for the current epoch are above the failure threshold.
- The VM is reported as running in the heartbeat.

**Otherwise, pause billing for that VM.**

There is no fragile distinction between "agent down" and "VM crashed" and "node operator unreachable." All collapse to the same outcome: stop charging until the situation resolves. The policy is the same regardless of cause, which is the only way to make it correct.

Operator-level signals (PoC failure, audit drift) feed into the slashing decision separately from per-VM billing. A node can be slashed while individual VMs continue to bill normally if the PoC failure is brief and recovers. A node can have all billing paused (heartbeat lost) without immediate slashing if the loss is transient.

---

## What this design does not defend against

Honest accounting of the limits:

**The cuckoo attack.** An operator with sufficient capital to run a second machine that proxies attestation can pass every check. The bonded-earnings layer makes this irrational at any reasonable scale — the proxy machine costs more than the fraud earns — but provides no cryptographic guarantee. Only TPM/TEE can.

**Sophisticated tenant tampering.** A tenant who roots their VM, kills internal services, and runs an unrelated workload is undetected by the per-host PoC. They get whatever they're paying for as long as the VM exists; whether they use it as intended is not the platform's concern.

**Collusion between operator and tenant.** If the tenant and operator collude to misreport resource usage in ways that benefit both, the heartbeat-based billing is exposed. This is a known limit of any system that trusts operator-reported telemetry. Mitigation is reputational — collusion patterns are hard to sustain at scale without leaving evidence in audit signals.

**Bootstrap-window fraud.** A brand-new operator can commit small-scale fraud in their first vesting window, capped by their effective capacity (which is capped by their bond fill ratio). The capacity ramp bounds this loss but does not eliminate it. The platform bears the residual risk as a cost of frictionless onboarding.

Each of these is a documented limitation, not an oversight. The design accepts them in exchange for simplicity and onboarding fluency.

---

## Migration

The redesign is implementable in three stages.

**Stage 1: Stop trusting the in-VM agent as a security boundary.**

- Remove the `IsVerified` security claim from billing. Heartbeat-based billing becomes the only source of truth.
- Keep the in-VM agent running for the moment, but downgrade its output to observability (logged, surfaced in metrics, never billed against).
- Update documentation and operator dashboards to remove "attestation verified" language for tenants.

**Stage 2: Introduce host-side PoC.**

- Extend the node agent with a PoC daemon (algorithm TBD per Open Questions).
- Define the orchestrator's PoC challenge endpoint and scoring logic.
- Start collecting PoC submissions in shadow mode — score them, log them, but do not slash on them.
- After two weeks of shadow data, set initial thresholds from observed distributions.

**Stage 3: Introduce bonded earnings.**

- Implement the operator dashboard with bond / withdrawable balances.
- Implement the capacity ramp in the scheduler.
- Implement slashing against the bond, starting at conservative rates.
- Migrate existing operators: their current accumulated earnings count toward their initial bond. Operators above target are immediately at withdrawable status; operators below are in vesting.

**Stage 4: Delete the in-VM agent.**

- After Stages 1-3 are stable, remove `AttestationService`, `AttestationSchedulerService`, the `decloud-agent` binary, the orchestrator's proxy challenge path, and the cloud-init injection.
- Templates no longer need to include the agent. Template validation removes the agent-presence check.

Each stage is independently shippable and reversible. Stage 1 alone resolves the false-negative noise that motivates this redesign. Stages 2-4 are net improvements but not blockers.

---

## Open questions

These decisions are deferred to follow-up design work, not blocked by this document.

**PoC algorithm.** RandomX (Fluence-style) or Argon2id (simpler) or something else. The tradeoff is binary distribution complexity vs. working-set sizing precision. Decision should be made after benchmarking both on representative operator hardware.

**Detection latency targets.** The `max_detection_window` that drives `B_target` sizing depends on which signals we actually slash on. Continuous PoC failure: seconds. Audit drift: days to weeks. We need to pick the slashing signals before we can pick the window.

**Slashing rate calibration.** Starting rates are stated above (1% / 25% / 100%) but these are guesses. They need to be calibrated from observed fraud incidence once the system is in production.

**Reputation feedback into target sizing.** Should mature, long-honest operators face lower targets (rewarding good behavior with reduced bond exposure)? Or should target remain fixed and reputation feed only into capacity caps? Both have merit. Keep it simple at launch (fixed target) and revisit.

**Platform reserve sizing.** What fraction of the platform fee funds the tenant compensation reserve? Needs financial modeling against observed fraud rates and tenant claim patterns.

**Sibling probe VMs for deep audits.** The original threat-model discussion contemplated orchestrator-controlled probe VMs running on each host to measure contention from a sibling-guest perspective. This is a powerful signal but operationally heavy (the platform runs a VM on every node for no revenue). Defer until PoC + heartbeat + bonding is in production and we can assess whether the additional signal is needed.
