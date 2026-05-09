# System VM Lifecycle & Reconciliation — Flow Map and Gap Analysis

**Scope:** `Orchestrator` + `DeCloud.NodeAgent` (and `DeCloud.NodeAgent.Infrastructure`).
**Goal:** identify the natural system boundaries, the existing self-healing surface, and the
remaining gaps / races that prevent the system VM management from being fully self-healing.
**Status:** Reflects the post-P11 architecture (node-authoritative reconciliation via the
`SystemVmReconciler` matrix). Last refreshed alongside the addendum / handout for the
cloud-init rendering migration.

This document follows the project design philosophy: align fixes to natural boundaries; do
not wrap systemic problems in artificial controls (sleeps, retry counts, status overrides).
Every recommendation below is a *boundary alignment*, not a band-aid.

## References

- `SYSTEM_VM_RESILIENCE_DESIGN.md` — phased migration to node-authoritative reconciliation
  (P1–P11) and the (intent, reality, pending) matrix.
- `TENANT_VM_UNIFIED_PIPELINE.md` — umbrella plan for unifying tenant + system VM
  cloud-init rendering on the orchestrator side. Covers `base-system.yaml`,
  `base-tenant.yaml`, `TemplateComposer`, and CA public key migration.
- `SYSTEM_VM_RENDERING_ADDENDUM.md` — system-VM-specific specifications inside the umbrella
  plan: per-deployment rendering, lifecycle, P9 channel payload changes.
- `SYSTEM_VM_RENDERING_HANDOUT.md` — execution-level instructions for the rendering migration.

---

## 1. Authoritative components and the boundary each owns

### 1.1 Orchestrator side

| Component | Boundary it owns | Cadence |
|---|---|---|
| `NodeService.RegisterNodeAsync` | Seed obligations, assign relay for CGNAT, generate identity (Ed25519, WG keys, AuthToken) under `registrationLock` | Per registration |
| `NodeService.SyncVmStateFromHeartbeatAsync` | VM state truth from the node, ghost detection, orphan recovery | Per heartbeat (~30 s) |
| `NodeService.MarkNodeVmsAsErrorAsync` | Node-offline classification → Migrating / Recovering / Unrecoverable / Lost | On node-offline transition |
| `NodeService.CheckNodeHealthAsync` | Node Online → Offline transition (2 min heartbeat timeout) | 30 s loop (`NodeHealthMonitorService`) |
| `NodeService.ProcessCommandAcknowledgmentAsync` | Command ack → status transition (Provisioning→Running, Deleting→Deleted, …) | Per ack |
| `VmService.DeleteVmAsync` | Issue `DeleteVm` command, mark `Deleting`, register ack expectation | Per call |
| `VmService.CreateVmAsync` (via `TryScheduleVmAsync`) | Resource reservation, `CreateVm` command, mark `Provisioning` (tenant VMs only) | Per call |
| `VmLifecycleManager.TransitionAsync` | **Single writer** of `vm.Status`; runs side effects (ingress, ports, billing, events) | Per transition |
| `SystemVmObligationService` | Maintain obligation set per node (add on capability gain; never remove). Compute `obligationHealth` for heartbeat response. **No deployment logic** — that lives on the node. | Heartbeat path |
| `SystemVmTemplateSeeder` | Seed system VM templates into MongoDB (revision-aware) | Startup |
| `BlockStoreController.Join` / `DhtController.Join` | Receive node-initiated registration, store PeerId / ListenAddress, return remote bootstrap peers | Per call |
| `BackgroundServices.CleanupExpiredCommands` | Stale-command timeout → Provisioning/Deleting → Error, clear `ActiveCommandId` (tenant VMs only) | 60 s loop |
| `StatePruningService` | Long-term DB pruning | Periodic |

**Note:** the orchestrator's lifecycle role for system VMs is now narrow. It owns
*intent* (which obligations exist, with what config) and *health classification*
(`obligationHealth`). It does **not** own deployment, retries, or state-machine
transitions. Those moved to the node in P6.

### 1.2 NodeAgent side

| Component | Boundary it owns | Cadence |
|---|---|---|
| `OrchestratorClient` | Heartbeat I/O, applies orchestrator-pushed commands; processes `InvalidVmIds`; persists obligations + system templates from P9 channel into local SQLite | Per heartbeat |
| `HeartbeatService` | Builds heartbeat snapshot; **pre-flight zombie check** (`TargetNodeId != myNodeId` → destroy) | 30 s |
| `CommandProcessorService` | Executes `CreateVm/StartVm/StopVm/DeleteVm/...`; **duplicate-name guard** for system VMs | Push + queue |
| `VmManagerInitializationService` | One-shot on startup: load VMs from SQLite, reconcile with libvirt | Startup, 60 s timeout |
| `LibvirtVmManager.ReconcileAllWithLibvirtAsync` | Reconcile in-memory `_vms` map + SQLite ↔ `virsh list` | Called by `VmHealthService` (1 min) |
| `LibvirtVmManager.PrepareCloudInitVariables` | Currently substitutes role-specific cloud-init variables (relay/dht/blockstore blocks). **Migrating away** — see addendum. | Per VM create |
| `VmHealthService` | Periodic VM health (1 min); 5-min heartbeat threshold → restart for tenant VMs, mark Failed for system VMs (matrix handles redeploy) | 1 min |
| `SystemVmWatchdogService` | Startup: ensure `virbr0` active, re-attach orphan tap interfaces, hard-reset zombies (port 80 closed), restart Stopped/Failed system VMs. Periodic: tap re-attach. | Startup + 5 min |
| `SystemVmReconciler` ⭐ | **Authoritative system VM lifecycle.** Runs the (intent, reality, pending) matrix per role per cycle. Issues Create/Delete commands directly via `IVmManager`. | 30 s |
| `IIntentComputation` / `IRealityProjection` / `IOutstandingCommands` | Three matrix input axes. Each is a single-purpose interface with no cross-coupling. | Per matrix tick |
| `VmReadinessMonitor` | Per-service readiness via qemu-guest-agent; **liveness ping** stamps `LastHeartbeat` for fully-ready VMs | 10 s |
| `OrphanedPortCleanupService` | Free port mappings whose VM no longer exists | 1 hr |
| `ObligationStateController` (virbr0) | Identity (Ed25519 key, WG key, tunnel IP, auth token) served to system VM at boot from local SQLite | On VM-bridge request |
| `SystemVmController.GetPeerInfo` ⭐ | **Local sibling discovery** — answers `GET /api/system-vms/{role}/peer-info` for blockstore-bootstrap-poll to dial the co-located DHT directly via virbr0 | On VM-bridge request |
| `ArtifactCacheController` | Serve cached artifacts to system VMs at boot via `http://192.168.122.1:5100/api/artifacts/{sha256}` | On VM-bridge request |

⭐ = component that did not exist or was substantially different in pre-P11 architecture.

---

## 2. State models — the two state machines

### 2.1 Orchestrator `VmStatus` machine (`VmLifecycleManager.ValidTransitions`)

```
                     ┌───────────┐
                     │  Pending  │
                     └─────┬─────┘
                           │
                ┌──────────┼──────────────┐
                ▼          ▼              ▼
          Scheduling  Provisioning      Error ───┐
                │          │              │     │
                └────┬─────┘              │     │
                     ▼                    │     │
                 Provisioning ────────────┤     │
                     │                    │     │
                     ▼                    │     │
                  Running ─────► Stopping─┤     │
                     │ ▲           │      │     │
                     │ │           ▼      │     │
                     │ └─── Stopped ◄─────┤     │
                     │     │              │     │
                     ▼     ▼              ▼     │
                  Deleting ◄──── Migrating      │
                  │  ▲ │                        │
       (false-pos)│  │ │                        │
                  │  │ ▼                        │
                  │  └──Running                 │
                  ▼                             │
              ┌─────────┐                       │
              │ Deleted │ (terminal)            │
              └─────────┘                       │
                                                │
            Error → Provisioning|Running|Stopped|Deleting|Error
                    └────────────────────────────────────────┘
```

Key invariants (correctly enforced today):
- `TransitionAsync` is the only writer of `vm.Status`.
- Heartbeat handler **skips** writes when current status is *transitional*
  (`Provisioning`, `Stopping`, `Deleting`) **or terminal** (`Deleted`) — command-ack owns those.
- Side effects keyed by `(from, to)` pair; failures inside one effect do not abort siblings.

### 2.2 The `SystemVmReconciler` matrix (NodeAgent)

Replaces the old `SystemVmStatus` state machine entirely. Three input axes; one decision
function; three possible actions. No timers, no grace periods, no "deploying" status —
every cycle is a fresh evaluation against current reality.

```
Inputs (computed per role each cycle)
─────────────────────────────────────
intent  : Intent             ← from obligations + dependency graph
                                (wantDeployed: bool, depsMet: bool)

reality : RealitySnapshot    ← from libvirt + readiness monitor
                                (None | Healthy | Unhealthy)

pending : OutstandingCommand?← from in-memory map of issued-but-unacked
                                (Create | Delete | null)

Decision function (pure, no I/O)
────────────────────────────────
Decide(intent, reality, pending) → MatrixAction

         intent.want=false                  intent.want=true
         ────────────────                  ────────────────
reality=None,      pending=*       Wait    pending=Create   Wait
reality≠None,      pending=Delete  Wait    pending=Delete   Wait
reality≠None,      pending≠Delete  Delete  reality=Healthy  Wait
                                           reality=None,    Create (if depsMet)
                                                            Wait   (otherwise)
                                           reality=Unhealthy + pending=null  Delete

Actions
───────
Wait        — converged or command in flight
IssueCreate — actCreateAsync: read template, prefetch artifacts, build VmSpec,
                              call IVmManager.CreateVmAsync directly
IssueDelete — actDeleteAsync: call IVmManager.DeleteVmAsync directly
```

**Why this is simpler than what it replaced.** The old orchestrator-side reconciliation
service had a state machine (Pending → Deploying → Active → Failed) overlaid on top of
the VM lifecycle, plus six different timeouts (CloudInitReadyTimeout, ActiveVmGracePeriod,
HeartbeatSnapshotTtl, ProvisioningTimeout, StuckDeletingTimeout, plus per-role command
ack timeouts). Each was a boundary the system had to defend. The matrix collapses them
into one timeout (`CommandTimeout = 20 min`) and one rule: every cycle re-derives action
from current truth. Stale state cannot accumulate because there is no stored decision.

### 2.3 NodeAgent `VmState` machine (`VmInstance.State`)

```
Creating → Starting → Running → Stopping → Stopped
                         │
                         ├──► Failed (tenant: VmHealthService restarts;
                         │            system: matrix re-evaluates → Delete + Create)
                         │
                         └──► Deleted
```

---

## 3. End-to-end flows

### 3.1 Fresh registration — happy path

```
Node                           Orchestrator
 │  POST /api/nodes/register      │
 │ ─────────────────────────────► │
 │                                ├── registrationLock acquired
 │                                ├── ComputeObligations(node) → [Relay?, Dht, BlockStore?, Ingress?]
 │                                ├── If CGNAT: AssignCgnatNodeToRelayAsync (sets node.CgnatInfo)
 │                                ├── Seed SystemVmObligations (all Pending)
 │                                ├── GenerateAndAttachObligationStates
 │                                │    (creates Ed25519/WG keys, AuthToken,
 │                                │     stores StateJson + StateVersion=1)
 │                                ├── SaveNodeAsync
 │                                └── registrationLock released
 │ ◄── 200 OK + ObligationStates  │
 │     + system templates         │
 │     + obligations              │
 │
 │  OrchestratorClient persists obligations + templates into local SQLite.
 │
 │  SystemVmReconciler (next 30-s tick):
 │    For each obligation:
 │      ┌── ComputeIntent(obligation)         → intent.WantDeployed=true, deps?
 │      ├── ProjectReality(role)              → reality=None (no VM yet)
 │      ├── _outstanding.TryGet(role, ...)    → pending=null
 │      └── Decide(...) → IssueCreate (if deps met)
 │           └── ActCreateAsync:
 │                ├── read template from SQLite
 │                ├── PrefetchAsync artifacts to local cache
 │                ├── SubstituteArtifactVariables (${ARTIFACT_URL:name}) ⚠
 │                ├── build VmSpec; record outstanding Create
 │                └── IVmManager.CreateVmAsync(spec)
 │                       └── LibvirtVmManager.PrepareCloudInitVariables
 │                              substitutes role-specific __PLACEHOLDER__ vars ⚠
 │                              from spec.Labels (relay/dht/blockstore blocks)
 │                       └── virsh define + start
 │
 │  VM boots → cloud-init queries http://192.168.122.1:5100/api/obligations/{role}/state
 │             for identity → wg-mesh-enroll → systemd starts service
 │
 │  Heartbeat (every 30 s) reports VM Running + Services[System].Status=Ready.
 │  Orchestrator stamps obligationHealth based on freshness.
 │
 │  Next matrix tick: reality=Healthy → Wait. Converged.
```

⚠ The marked steps are the cloud-init rendering pathway being moved to the orchestrator
in the rendering migration. After migration, the node receives a fully-rendered cloud-init
in the obligation payload and `LibvirtVmManager.PrepareCloudInitVariables` deletes
entirely. See `SYSTEM_VM_RENDERING_ADDENDUM.md`.

### 3.2 Health classification path

The orchestrator no longer drives Active promotion. It computes `obligationHealth` from
heartbeat freshness and reports it back to the node, which uses it as one input to the
intent computation:

```
Orchestrator (heartbeat handler):
  For each obligation on the heartbeating node:
    obligationHealth =
      LastHeartbeatAt fresher than HeartbeatFreshness (90 s)
      ∧ Services[System].Status == "Ready"
      ⇒ Healthy
      else Unhealthy or Unknown

  Returned in heartbeat response — node consumes as a hint, not a command.
```

The node's matrix uses `obligationHealth` as a tiebreaker, not as a state. The
authoritative reality comes from the local `IRealityProjection` against libvirt and the
readiness monitor.

### 3.3 Redeploy of a stale or unhealthy VM

```
SystemVmReconciler (30 s tick)
  intent.WantDeployed = true
  reality.State       = Unhealthy   ← libvirt says Failed, or readiness monitor stale
  pending             = null

  Decide(...) → IssueDelete

  ActDeleteAsync:
    ├── _outstanding.Set(role, Delete cmd)
    ├── IVmManager.DeleteVmAsync(vmId)
    └── _outstanding.Clear(role) on completion

Next tick:
  reality.State = None
  pending       = null
  Decide(...) → IssueCreate

  ActCreateAsync redeploys.

Throughout: identity (Ed25519, WG keys, auth token) is preserved by
ObligationStateService in local SQLite, so the new VM picks up the same
peer ID and tokens — peers do not need to update their bootstrap lists.
```

### 3.4 Node goes offline

```
NodeHealthMonitorService (30 s)
  └── CheckNodeHealthAsync
        ├── timeSinceLastHeartbeat > 2 m?
        ├── node.Status = Offline; SaveNodeAsync
        ├── Emit NodeOffline event
        └── MarkNodeVmsAsErrorAsync(nodeId)
              └── For each Running tenant VM on node:
                   ├── ClassifyOfflineVm → Migrating / Recovering / Unrecoverable / Lost
                   ├── lifecycleManager.TransitionAsync(Running → Error, NodeOffline)
                   └── PushMessage with classification

System VMs (Relay/Dht/BlockStore) on the offline node:
  - Their VirtualMachine records are tenant-VM filtered out of MarkNodeVmsAsErrorAsync.
  - obligationHealth flips to Unhealthy as soon as heartbeat goes stale.
  - The matrix runs *on the node* — when the node is offline, it isn't ticking.
  - When the node reconnects, its very first matrix tick sees current reality
    (libvirt has the VM running, healthy or not) and acts accordingly.

  ⇒ Result: system VM lifecycle decisions wait for the node to come back. Identity
            survives because it lives in the node's local SQLite, not in MongoDB.
            This is the correct behaviour: don't move system VMs while their host
            is dead. Dependency is to the node, not to the cluster.
```

### 3.5 Zombie VM after migration (split-brain prevention)

Already implemented as a push model:

```
Source-node reconnects after orchestrator migrated its VM:
  HeartbeatService.SendHeartbeatAsync (pre-flight)
    └── For each local VM where TargetNodeId != myNodeId AND State == Running:
         └── _vmManager.DeleteVmAsync(vmId)  (fire-and-forget, Task.Run)

Belt-and-suspenders:
  Orchestrator.SyncVmStateFromHeartbeatAsync:
    └── For reported VMs not in DB or with mismatched NodeId:
         └── return invalidVmIds in response

  OrchestratorClient receives invalidVmIds:
    └── enqueue DeleteVm commands (RequiresAck=false)
    └── CommandProcessorService.HandleDeleteVmAsync executes
```

This is sound — both paths align to the natural boundary "the VM's `NodeId` field is
the lease, and the node enforces it locally on reconnect".

### 3.6 Local sibling discovery (DHT ↔ blockstore on same host)

Added in the recent debugging arc to fix a class of bug where the orchestrator was
asked questions only the node could answer:

```
Blockstore VM boots → wg-config-fetch.sh allocates tunnel IP, writes blockstore.env
                   → blockstore-bootstrap-poll service starts

bootstrap-poll iteration:
  ├── connect_local_dht():
  │     curl http://192.168.122.1:5100/api/system-vms/dht/peer-info
  │     ├── 200 → multiaddr /ip4/{virbr0_ip}/tcp/4001/p2p/{peerId}
  │     │        POST /connect with multiaddr to local blockstore binary
  │     │        (idempotent: no-op if already connected)
  │     └── 404 → DHT not yet ready, retry next iteration
  │
  └── poll orchestrator for REMOTE bootstrap peers via /api/blockstore/join
        orchestrator returns peers from OTHER nodes only

Result: local sibling connection is established by the node; remote federation
        is established via the orchestrator. Each authority answers what it knows.
```

The DHT side does not symmetrically discover the blockstore — DHT is foundational
and depends on no consumer. libp2p reports the connection bidirectionally as soon
as the blockstore dials in.

---

## 4. Identified gaps, races, and conceals (ranked)

Several gaps from earlier revisions of this document have been resolved by the P5–P11
architecture migration. Those are listed under **Resolved** at the end of this section
for traceability.

### 🔴 Severity HIGH

#### G7. Capability-loss drain is intentionally absent
`SystemVmObligationService.EnsureObligationsAsync` only **adds** missing roles. It
never removes obligations whose eligibility went away (e.g. node's reported total
storage drops below 100 GB → BlockStore obligation should be retired). The comment is
honest about this:
> *"Existing obligations are never removed (removal would require draining VMs)."*

That's the design intent today; not a bug. But it means the cluster will
**over-allocate** system VMs over time if nodes shrink. When drain support lands,
this becomes a sequenced workflow (drain → cordon → migrate tenant VMs → tear down
system VM), not a unilateral remove.

**Status:** documented design boundary. Open question §8.2.

#### G14. Role-specific deployment policy in `LibvirtVmManager`
`LibvirtVmManager.PrepareCloudInitVariables` contains three blocks (steps 5.5/5.6/5.7)
with hardcoded knowledge of relay, DHT, and blockstore deployment policy: ports,
region resolution, advertise-IP fallback chains, orchestrator-URL substitution,
WireGuard endpoint composition. None of this is host-local; all of it is orchestrator
policy that happens to execute on the node side because the cloud-init template was
shipped half-rendered.

**Symptom history.** Every system VM type added to the platform has required a
NodeAgent rebuild and per-host redeploy for what should have been a pure orchestrator
decision. Concrete instances:
- Blockstore dashboard displaying literal `__WG_TUNNEL_IP__` (variable not in BlockStore block).
- Blockstore nginx crashing on `__BLOCKSTORE_API_PORT__` (variable not in BlockStore block).
- Blockstore region rendering as literal `__DHT_REGION__` (variable name leak from DHT block).
- `MergeCustomUserDataWithBaseConfig` PYTORCH bug from line-by-line YAML injection fragility.

**Boundary alignment.** Move all role-specific substitution to the orchestrator at
deploy time. Node receives a fully-rendered cloud-init document. Specifications and
execution plan are in `SYSTEM_VM_RENDERING_ADDENDUM.md` and
`SYSTEM_VM_RENDERING_HANDOUT.md`.

---

### 🟠 Severity MEDIUM

#### G15. `__WG_TUNNEL_IP__` in immutable metadata JSON
The blockstore cloud-init writes `/etc/decloud/blockstore-metadata.json` with
`"advertise_ip": "__WG_TUNNEL_IP__"`. The metadata file is meant to be the immutable
identity card of the VM (vm_id, node_id, region, ports). `advertise_ip` is a runtime
value — assigned by `wg-config-fetch.sh` at first boot from a relay-allocated subnet.
Mixing the two lifecycles in one document caused the blockstore dashboard to display
the literal placeholder string when LibvirtVmManager's BlockStore block didn't define
the variable.

**Partial fix shipped.** Dashboard now reads `BLOCKSTORE_ADVERTISE_IP` from the
runtime env file (`/etc/decloud-blockstore/blockstore.env`, written by
`wg-config-fetch.sh`). The metadata JSON template has been cleaned up to drop the two
runtime fields.

**Remaining work.** The DHT cloud-init has the same layering issue but it is invisible
because the DHT dashboard reads addresses from the binary's `/health` endpoint instead
of metadata. Worth cleaning up for consistency. The category error — runtime values
in immutable metadata — is the underlying boundary mismatch.

#### G5. Concurrent writes to `Node.SystemVmObligations` from heartbeat handler
Multiple heartbeats from the same node can land concurrently if the orchestrator is
behind. The `obligationHealth` writes are idempotent but `EnsureObligationsAsync`'s
add-only semantics interact poorly with parallel readers/writers. Last-write-wins on
MongoDB.

**Symptom:** rare, brief inconsistency in `obligationHealth`. Self-heals next
heartbeat.

**Boundary alignment:** per-node lock. Implementation: a `ConcurrentDictionary<string,
SemaphoreSlim>` keyed by `nodeId`, with weak-reference cleanup. Existing
`registrationLock` is per-process global — too coarse.

#### G16. No audit record of the cloud-init dispatched to a node
Currently the orchestrator dispatches a template to the node, and the node renders
it. The substituted output never returns to the orchestrator. If you ask "what
cloud-init did node X run for its DHT VM?" — there is no answer at the orchestrator.

**Boundary alignment:** the rendering migration (G14) puts substitution at the
orchestrator. As a side effect, `obligation.RenderedCloudInit` becomes the audit
record for free. No additional work needed once G14 ships.

---

### 🟡 Severity LOW

#### G10. Side effects on `Deleting → Running` recovery
The lifecycle allows `Deleting → Running` for false-positive recovery. The transition
fires `OnVmBecameRunningAsync` (ingress + ports). If a future code path triggers
`Deleting → Running` from somewhere else (e.g., manual operator action), obligation
re-attachment must follow. Low risk today; worth a code comment.

#### G11. No upper bound on `FailureCount`
`OutstandingCommands` sweeps expired entries at `CommandTimeout` (20 min). There is no
counter of cumulative failures per role and no escalation path. Cosmetic — logs only.

#### G12. `vm.LastHeartbeat` (NodeAgent) vs `vm.LastHeartbeatAt` (Orchestrator)
Two near-identical field names with subtly different update rules:
- NodeAgent's `LastHeartbeat`: updated by `VmReadinessMonitor` only for *fully-ready* VMs.
- Orchestrator's `LastHeartbeatAt`: stamped by `SyncVmStateFromHeartbeatAsync` only
  for `Running` non-transitional VMs.

Justified duplication, but the naming is a footgun. Worth a docstring on both.

#### G13. Orphan VM directory cleanup
`LibvirtVmManager.DeleteVmAsync` cleans `/var/lib/decloud-vms/{vmId}` only inside the
"happy path" branch. The "VM not found, idempotent success" branch logs but doesn't
sweep. Disk fills slowly with orphan dirs (small in size). Low priority; could be a
dedicated once-an-hour sweep.

---

### ✅ Resolved by P5–P11 (recorded for traceability)

| Old gap | Resolution |
|---|---|
| **G1** — heartbeat-path Active promotion uses weaker readiness criterion than the loop | Resolved. Active promotion was deleted in P7. The matrix evaluates current reality every cycle; there is no "promotion" step. |
| **G2** — `nodeJustReconnected` guard rarely triggers | Resolved. The matrix is event-free in this sense — every cycle re-derives from current state. The guard is gone. |
| **G3** — heartbeat-path stamps `vm.UpdatedAt`, defeating stale-Error cleanup | Resolved by removing the stale-Error cleanup path entirely. The matrix doesn't rely on `UpdatedAt` as a freshness signal. |
| **G4** — `StuckDeletingTimeout` declared but never read | Resolved. Collapsed into `CommandTimeout` (20 min). One timeout governs all in-flight commands. |
| **G6** — reconciliation loop ignores Offline nodes | No longer applicable — the loop runs on the node, so an offline node has no loop. Behaviour is correct: lifecycle decisions wait for the node to come back. |
| **G8** — `TryRetryAsync` doesn't clear FailureCount on next success | Resolved. Retry/backoff machinery deleted; matrix re-derives every 30 s. |
| **G9** — NodeAgent and Orchestrator clocks both decided "unhealthy" within the same minute | Resolved. The orchestrator no longer has a clock for system VM lifecycle. One clock, on the node. |

---

## 5. Self-healing posture — what works well today

Worth recording the parts the project gets right, so refactors do not regress them:

1. **Single transition writer** (`VmLifecycleManager.TransitionAsync`) — every status
   change runs the same side-effect chain regardless of trigger. Design philosophy in action.

2. **Transitional-status guard in heartbeat handler** — explicitly prevents stale node
   reports from overwriting in-flight commands. The race is documented inline.

3. **Push-model zombie destruction** (pre-flight `TargetNodeId` check + `InvalidVmIds`
   in heartbeat response) — replaces the originally-planned per-VM pull. Cheaper, faster.

4. **Identity preservation across redeploys** via `ObligationStateService` — the Ed25519
   peer ID and the relay's WG keypair survive VM destruction. Eliminates cascading mesh
   updates.

5. **Belt-and-suspenders duplicate-name guard** on the NodeAgent
   (`CommandProcessorService`) closes the residual race window.

6. **Per-node exception isolation** in the matrix loop — one bad role doesn't freeze
   evaluation for the other roles or for other nodes.

7. **Per-role `EligibilityResult` with `FailureReasons`** — eligibility decisions are
   self-explaining. No re-derivation in callers.

8. **Dependency graph is conditional**
   (`SystemVmDependencies.AreDependenciesMet` ignores deps the node doesn't itself have)
   — CGNAT-only nodes correctly skip the local Relay dependency for DHT.

9. **The matrix is event-free.** Every 30 s it re-derives action from current state with
   no stored decision. Stale state cannot accumulate. The architecture is convergent by
   construction. ⭐

10. **Local sibling discovery via NodeAgent peer-info.** The blockstore dials its
    co-located DHT through virbr0 using data the NodeAgent already has, with no
    orchestrator round-trip. Idempotent `/connect` makes the call cheap to retry every
    poll iteration; sibling redeploys self-heal without explicit notification. ⭐

11. **One timeout — `CommandTimeout` (20 min).** Replaces five different lifecycle
    timeouts from the pre-P11 architecture. If a command hasn't completed in 20
    minutes, the matrix clears it; next cycle decides afresh. ⭐

⭐ = property added or substantially strengthened by P5–P11.

---

## 6. Recommended fixes (priority-ordered, all aligned to existing boundaries)

### Round 1 — operational hygiene

1. **G5 / per-node lock**: take a per-node monitor in `SyncVmStateFromHeartbeatAsync`
   and the obligation-update path. Implementation: a `ConcurrentDictionary<string,
   SemaphoreSlim>` keyed by `nodeId`, with weak-reference cleanup.

### Round 2 — observability and hygiene

2. **G11**: emit a `SystemVmReconcilerThrash` event when the same role flips
   IssueDelete → IssueCreate more than N times in a window. Visibility, not auto-suspend.

3. **G12**: docstring both `LastHeartbeat` fields. No code change.

4. **G13**: schedule an hourly sweep of `/var/lib/decloud-vms` removing directories
   whose `vmId` is unknown to both `_vms` and SQLite.

### Round 3 — design-intent acknowledgements (no code change)

5. **G7**: capability-loss drain is intentionally absent. Document explicitly in
   `EnsureObligationsAsync`'s docstring.

6. **G10**: add a code comment in `VmLifecycleManager.OnVmBecameRunningAsync` noting
   that obligation re-attachment is the caller's responsibility.

### Round 4 — DHT metadata layering cleanup (G15)

7. Apply the same fix to DHT that landed for blockstore: remove `advertise_ip` and
   `wg_tunnel_ip` from `dht-metadata.json`, read runtime values from `dht.env` if any
   dashboard component needs them. Currently DHT dashboard reads addresses from the
   binary's `/health` so this is mostly cosmetic, but the layering principle should be
   uniform.

### Round 5 — Cloud-init rendering boundary realignment (G14, G16)

8. Execute the rendering migration. Specification and step-by-step instructions are in:
   - `SYSTEM_VM_RENDERING_ADDENDUM.md` (specification, decisions, gap analysis specific
     to system VMs)
   - `SYSTEM_VM_RENDERING_HANDOUT.md` (execution-level instructions for an implementing
     agent)

   This is the single largest outstanding architectural item and resolves both G14
   (role-specific blocks in `LibvirtVmManager`) and G16 (no orchestrator-side audit
   record of dispatched cloud-init) in one migration.

---

## 7. What we explicitly are not adding

In keeping with the design philosophy:

- **No artificial sleeps** between transition steps. Every wait point in this system is
  a natural one (cloud-init timeout, ack timeout, heartbeat cadence, command timeout).
- **No hard-coded "retry up to N times then give up".** The matrix is convergent; it
  re-evaluates forever. There is no failure counter to roll over.
- **No status overrides** to mask state-machine inconsistencies. If a transition is
  invalid, `VmLifecycleManager` logs and refuses — that's the boundary, do not bypass.
- **No new background services** to babysit existing background services. The matrix is
  the babysitter. `SystemVmWatchdogService` is the one exception, and its scope is
  narrow (virbr0 ensure, tap re-attach, startup-time zombie reset).
- **No symmetric local-sibling discovery on the DHT side.** DHT is foundational and
  does not depend on its consumers. Inverting the dependency graph would couple DHT
  startup to blockstore availability, which is conditional on hardware.

---

## 8. Open questions for the team

1. **Should `node.Status == Offline` time out?** Today an offline node stays Offline
   forever in the DB with its last `SystemVmObligations` snapshot intact. If a node is
   gone for weeks, its obligations still occupy compute-points accounting. Worth deciding
   the eviction policy (and whether the obligations cascade into the cluster's overall
   Relay/DHT topology counters).

2. **Capability-loss drain (G7)** — is this a real product requirement? If yes, it's a
   non-trivial workflow (drain → cordon → migrate tenant VMs → tear down system VM).

3. **CA rotation policy.** When the rendering migration ships (per the addendum),
   `RenderedCaFingerprint` becomes the invalidation trigger for cached cloud-init.
   We have not yet decided how often the CA is rotated and whether rotation is global
   or per-orchestrator-instance. Decision belongs to security / ops, not to this
   document — recording here so it does not get lost.

---

## Appendix A — Constants reference (Orchestrator)

| Constant | Value | Used by |
|---|---|---|
| Heartbeat freshness | 90 s | `obligationHealth` staleness |
| Node heartbeat timeout (`CheckNodeHealthAsync`) | 2 m | Online → Offline |
| Command ack timeout (tenant VMs) | 5 m | Provisioning/Deleting → Error (tenant only) |
| `ProvisioningTimeout` | 30 m | stale-Provisioning cleanup (tenant only) |

The pre-P11 reconciliation timeouts (`CloudInitReadyTimeout`, `ActiveVmGracePeriod`,
`HeartbeatSnapshotTtl`, `StuckDeletingTimeout`, per-role retry backoff) **no longer
exist on the orchestrator**. They are subsumed by the node-side `CommandTimeout`.

## Appendix B — Constants reference (NodeAgent)

| Constant | Value | Used by |
|---|---|---|
| Heartbeat interval | 30 s | `HeartbeatService` |
| `SystemVmReconciler.ReconcileInterval` | 30 s | matrix tick |
| `OutstandingCommands.CommandTimeout` | 20 m | sweep expired in-flight commands |
| `VmReadinessMonitor.PollInterval` | 10 s | per-service readiness, liveness ping |
| `VmHealthService.HealthCheckInterval` | 1 m | libvirt reconcile + per-VM health |
| 5-min heartbeat threshold | 5 m | tenant VM restart, system VM mark-Failed |
| `SystemVmWatchdogService` periodic | 5 m | tap re-attach |
| `OrphanedPortCleanupService` | 1 hr | port mapping sweep |
| Cloud-init timeout (per service) | 300 s default | `VmServiceStatus.TimeoutSeconds` |
