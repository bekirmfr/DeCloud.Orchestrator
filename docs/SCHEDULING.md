# DeCloud Scheduling

How the orchestrator places tenant VMs onto operator nodes: the entry
paths, the filter chain, the scoring model, the tunable parameters, the
constraint vocabulary that lets tenants express node selection
requirements, and the dashboard surface that lets them compose those
requirements.

> **Companion documents:**
> - [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) — the country/region/zone model the scheduler consumes
> - [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — how nodes reach a schedulable state
> - [`RELEASE-PIPELINE.md`](RELEASE-PIPELINE.md) — how the orchestrator binary is produced

---

## Table of Contents

1. [Overview](#overview)
2. [Entry paths](#entry-paths)
3. [The filter chain](#the-filter-chain)
4. [Quality tiers](#quality-tiers)
5. [Scoring](#scoring)
6. [Tunable parameters](#tunable-parameters)
7. [Constraint vocabulary](#constraint-vocabulary)
8. [API endpoints](#api-endpoints)
9. [Client integration](#client-integration)
10. [Worked example](#worked-example)
11. [What is intentionally NOT here](#what-is-intentionally-not-here)
12. [References](#references)

---

## Overview

When a tenant requests a VM, the orchestrator picks a single node from
the federated pool. The scheduler does this in two phases:

1. **Hard filtering** — eliminate every node that cannot host this VM.
   A failed hard filter is a categorical reject; no scoring happens.

2. **Scoring** — among eligible nodes, compute a weighted score across
   capacity, load, reputation, and locality, then pick the highest.

**Constraints are the sole mechanism for tenant-expressible node
selection requirements.** Tenants express all per-node requirements —
locality, architecture, reputation, jurisdiction, GPU model, hardware
capability, or any other selection predicate — as a flat AND list of
`{ target, operator, value }` constraints in `spec.Constraints`. No
method-level override parameters exist on the scheduler interface.

A small set of **platform-imposed filters** apply regardless: node
status, scheduling readiness, a load and free-memory safety floor, GPU
VRAM headroom for proxied GPU VMs, system obligations, and KVM
availability. These are detailed in §3 — tenants cannot disable them.
They are node-situational and capacity checks, not selection predicates,
which is why they stay hardcoded.

Requirements carried by **first-class spec fields** — `QualityTier`,
`GpuMode`, `ReplicationFactor` — are *derived* into ephemeral constraints
at evaluation time (`DerivedConstraints.Derive(spec)`) and evaluated in
FILTER 10 through the same `IConstraintEvaluator` as tenant-authored
constraints. One evaluator sees every placement requirement; the fields
stay authoritative for execution (billing, CPU quota, device assignment,
lazysync). Derived constraints are never persisted. See §7.

Configuration lives in MongoDB (`scheduling_configs` collection,
versioned with audit history) and is served by `ISchedulingConfigService`
with a 5-minute in-memory cache. Operators with the `Admin` role update
it through `PUT /api/admin/scheduling-config`.

---

## Entry paths

Four paths reach the scheduler. All converge on the same filter chain.

### Path A — Auto-scheduling (default VM creation)

```
POST /api/vms  (no targetNodeId)
  → VmsController.Create
  → VmService.CreateVmAsync
      STEP 1: validate spec.Constraints (ValidateSet)
      STEP 2: name pipeline, quota check, password gen
      STEP 3: persist VM (status: Pending)
      STEP 4: TryScheduleVmAsync
                → _schedulingService.SelectBestNodeForVmAsync(spec)
                    (tier read from spec.QualityTier — the single source)
                    → GetScoredNodesForVmAsync
                    → for each Online node: ScoreNodeForVmAsync
                        → ApplyHardFiltersAsync   ← single filter chain
                        → score if eligible
                    → sort by total score, pick highest
```

### Path B — Marketplace-targeted deployment

```
POST /api/vms  (targetNodeId set)
  → VmService.CreateVmAsync
      (same constraint validation and spec steps as Path A)
      STEP 4: TryScheduleVmAsync
                → dataStore.GetNodeAsync(targetNodeId)
                → _schedulingService.ValidateNodeForVmAsync(node, spec)
                    → ApplyHardFiltersAsync   ← same filter chain, single node
                    → null = eligible, string = rejection reason
```

System VMs (Relay, DHT, BlockStore) bypass eligibility checks — they are
orchestrator-controlled placements targeting a specific node by design.

### Path C — Background migration (error recovery)

```
VmSchedulerService (BackgroundService, polls every ~10 s)
  → detects VM in Error state with offline host and no in-flight command
  → MigrateVmAsync
      → add ephemeral architecture-stickiness constraint to spec copy:
          { target: node.architecture, operator: eq, value: sourceNode.Architecture }
        (NOT persisted on the VM — migration-specific, restored in finally)
      → _schedulingService.SelectBestNodeForVmAsync(specCopy)
          → same filter chain as Path A
```

A VM that booted on an x86_64 node has its overlay disk filled with
x86_64 binaries. Migration enforces architecture stickiness via a
constraint so the VM cannot land on an architecturally incompatible node.

### Path D — Template marketplace deployment

```
POST /api/marketplace/templates/{templateId}/deploy
  → MarketplaceController.DeployTemplate
  → TemplateService.BuildVmRequestFromTemplateAsync
      STEP 1: merge template constraints with user spec (see below)
      STEP 2: produce CreateVmRequest with merged Constraints[]
  → VmService.CreateVmAsync   ← converges on Path A from here
```

Template marketplace deployments produce a regular `CreateVmRequest` and
then enter Path A. The interesting work happens before: the user submits
a partial `customSpec` (CPU, memory, disk, etc.) along with the template
ID, and `BuildVmRequestFromTemplateAsync` produces the final `VmSpec` by
merging three constraint sources with explicit precedence.

**Template constraint merging.** A `VmTemplate` carries two embedded
`VmSpec` instances, both of which may declare `Constraints`:

| Source | Precedence | User can remove? | Use case |
|---|---|---|---|
| `template.MinimumSpec.Constraints` | Mandatory | No | Hard workload requirement: "needs NVMe", "needs ≥24 GB GPU VRAM" |
| `template.RecommendedSpec.Constraints` | Default | Yes | Author recommendation: "prefer EU placement", "prefer high-uptime operators" |
| `customSpec.Constraints` (user-supplied) | Override | — | Tenant-specific requirements |

Merge algorithm:

1. Start with `template.MinimumSpec.Constraints` (always present, by Target).
2. For each `template.RecommendedSpec.Constraint`, add unless
   `customSpec.Constraints` already targets the same field.
3. Append `customSpec.Constraints`.
4. On conflict (same `Target` between `MinimumSpec` and a user constraint),
   `MinimumSpec` wins — the user cannot remove a mandatory constraint.

The merged list is what `VmService.CreateVmAsync` receives and validates
as `request.Spec.Constraints` before scheduling. In the dashboard, the
deploy modal renders `MinimumSpec.Constraints` as locked rows (non-removable),
`RecommendedSpec.Constraints` as editable defaults, and lets the user
append their own. See §9 for the unified constraint builder component.

### Path E — Stack deployment

A **stack** is a named multi-VM topology defined in a `StackTemplate`.
Deploying a stack invokes `StackService`, which is the *only* caller that
reasons about cross-VM placement. The scheduler itself remains stateless
and single-VM-per-decision — all multi-VM reasoning happens in
`StackService` before any `CreateVmAsync` call.

```
POST /api/marketplace/stacks/{stackId}/deploy
  → MarketplaceController.DeployStack
  → StackService.DeployStackAsync

      STEP 1  Load StackTemplate — blocks, connections, placement policies,
              stackConstraints[].

      STEP 2  Build placement graph.
                → Group blocks by mutual co-location policy into
                  PlacementGroups (A co-locates-with B AND B co-locates-with A
                  → same group, not a cycle).
                → Identify anti-affinity edges between groups.

      STEP 3  Resolve already-deployed members.
                → For each PlacementGroup: if any member block is already
                  running, its NodeId is the group's anchor. Conflicts
                  (two already-deployed members on different nodes) are
                  infeasibility errors surfaced here, before any VM is created.

      STEP 4  Compute per-group merged constraint set.
                MergeConstraints(
                    stackConstraints,               ← mandatory for every block
                    block.extraConstraints,         ← block-specific requirements
                    template.MinimumSpec.Constraints,
                    template.RecommendedSpec.Constraints
                )
                Co-location groups merge the constraints of ALL members —
                the combined set must be satisfiable by a single node.
                → Run POST /api/vms/scheduling/preview per group.
                  Zero eligible nodes = preflight error. No VM is created
                  until every group is confirmed feasible.

      STEP 5  Deploy in topological order (dependency DAG, leaves first).
                Anchor block  → Path A (auto-scheduling, merged constraints)
                Co-located    → Path B (targetNodeId = anchor's NodeId)
                Anti-affinity → inject ephemeral
                                { target: "node.id", operator: "not_in",
                                  value: [nodeIds of avoided blocks] }
                                into the anchor's spec before CreateVmAsync.
                                (node.id is never user-authored — see §7.)

      STEP 6  Propagate inter-VM service discovery.
                → After each block's IP is known, inject
                  ${STACK_<BLOCK_ID>_IP} and
                  ${STACK_<BLOCK_ID>_PORT_<N>}
                  as cloud-init variables for dependent blocks.
                  Same substitution pipeline as existing ${DECLOUD_*} vars.
```

**Placement policy fallback tiers.** A `co-locate` policy can fail at
runtime if the target node is full. Each placement policy carries an
optional fallback:

| Policy | Fallback options |
|---|---|
| `co-locate` | `same-region`, `any`, `fail` |
| `same-region` | `any`, `fail` |
| `anti-affinity` | *(no fallback — always enforced via node.id injection)* |
| `any` | *(no fallback needed)* |

`StackService` tries the primary policy first (Path B for co-locate).
If the target node fails capacity checks, it falls back: `same-region`
injects a `node.locality.region eq <anchor.region>` constraint and
retries Path A; `any` drops the placement constraint entirely; `fail`
returns a preflight error.

**User-paused blocks.** Blocks marked for later deployment store their
placement policy in the `StackTemplate` intact. When the user deploys
them, `StackService` re-runs the resolution — by then more blocks are
deployed and more node IDs are known. The placement policy is the
durable design-time record; concrete `node.id` constraints are ephemeral
computation never persisted on the VM.

### Marketplace browse — NOT scheduling

```
GET /api/nodes/search
GET /api/nodes/featured
  → INodeMarketplaceService
  → _dataStore.GetAllNodesAsync() + LINQ filter
  → does NOT call IVmSchedulingService
```

Browse is an exploration affordance — it shows candidate nodes regardless
of whether any specific VM could be placed on them. Eligibility is checked
at deploy time (Path A, B, or D). A node visible in marketplace listings
may be rejected at deploy if the VM's constraints are not satisfied. This
is expected and by design.

---

## The filter chain

All scheduling paths converge on `ApplyHardFiltersAsync` in
`VmSchedulingService`. It evaluates filters in order, short-circuiting on
the first failure. The rejection reason is a human-readable string returned
to the caller (and surfaced in `ScoredNode.RejectionReason`).

```
ApplyHardFiltersAsync(node, spec, tierConfig, config, ct)
  │                         (tier read from spec.QualityTier)
  ├── FILTER 1: Node status
  │     node.Status == Online?
  │     Source: node heartbeat
  │
  ├── FILTER 1.5: Scheduling readiness
  │     node.IsSchedulingReady?
  │     Operator-controlled pause — a logged-out node is online and
  │     heartbeating but has opted out of new VM placement.
  │     Source: operator login/logout (see NODE-LIFECYCLE.md)
  │
  ├── FILTER 2: (removed — tier eligibility is a derived constraint)
  │     Every VM derives { target: "node.tier", operator: "contains",
  │       value: spec.QualityTier } — evaluated in FILTER 10.
  │     The node.tier extractor returns an empty list when
  │     PerformanceEvaluation is null, so unevaluated nodes are
  │     rejected exactly as the old null-check did.
  │     Filter number preserved for commit-history continuity.
  │
  ├── FILTER 3: (reserved — historically architecture)
  │     Architecture matching migrated to FILTER 10 as a constraint.
  │     Express via spec.Constraints[i] =
  │       { target: "node.architecture", operator: "eq", value: "x86_64" }.
  │     Filter number preserved for commit-history continuity.
  │
  ├── FILTER 4: (reserved — historically locality and reputation)
  │     Locality and reputation thresholds migrated to FILTER 10 as
  │     constraints. Express via node.locality.* targets and
  │     node.uptimePercent / node.reputationScore.
  │     Filter number preserved for commit-history continuity.
  │
  ├── FILTER 5: GPU VRAM headroom (capacity)
  │     spec.GpuMode == Proxied && spec.GpuVramBytes > 0 →
  │       available proxied VRAM (operator ceiling − used − reserved)
  │       >= spec.GpuVramBytes?
  │     GPU *capability* (can this node host this GpuMode at all?) is
  │     a derived constraint — node.gpu.proxiedAvailable /
  │     node.gpu.passthroughAvailable — evaluated in FILTER 10.
  │     Source: spec.GpuMode, spec.GpuVramBytes + node-side
  │
  ├── FILTER 6: Load average
  │     node.LatestMetrics?.LoadAverage <= config.Limits.MaxLoadAverage?
  │     Passes if LatestMetrics or LoadAverage is null (no signal to reject on).
  │     Source: node telemetry
  │
  ├── FILTER 7: Free memory
  │     (node.TotalResources.Memory - node.ReservedResources.Memory) / 1MiB
  │       >= config.Limits.MinFreeMemoryMb?
  │     Source: node-side
  │
  ├── FILTER 8: Obligations active
  │     All node.SystemVmObligations have Status == Active?
  │     A node that hasn't completed platform obligations (DHT, Relay,
  │     BlockStore, Ingress) is not fully onboarded.
  │     Source: node-side
  │
  ├── FILTER 8.1: (removed — BlockStore requirement is a derived constraint)
  │     spec.ReplicationFactor > 0 derives { target:
  │       "node.hasActiveBlockStore", operator: "eq", value: true } —
  │     evaluated in FILTER 10.
  │     Filter number preserved for commit-history continuity.
  │
  ├── FILTER 9: KVM available
  │     node.HardwareInventory.KvmAvailable?
  │     Source: node-side
  │
  └── FILTER 10: Unified constraint evaluation
        1) Derived constraints — DerivedConstraints.Derive(spec), from
           first-class spec fields (see §7 "Derived constraints").
           Evaluated first, preserving the old precedence where the
           tier / GPU-capability / BlockStore filters ran before
           tenant constraints. First failure →
           "Derived from <Field>=<value>: {target} ({actual}) {op} {value}"
        2) Authored constraints — spec.Constraints (validated at VM
           creation). First failure →
           "Constraint #i failed: {target} ({actual}) {op} {value}"
        One evaluator for every placement requirement.
```

The filters fall into three categories:

- **Platform-imposed** (1, 1.5, 5, 6, 7, 8, 9). Tenants cannot disable
  these. They are node-situational safety thresholds (status, readiness,
  load, memory, obligations, KVM) and live capacity checks (VRAM
  headroom). Capacity compares the VM's size against the node's
  remaining headroom right now — it is deliberately never expressed as
  a constraint (see §11, live counters) and never evaluated in
  compliance, where the resident VM would count against itself.

- **Spec-derived** (evaluated in 10). Requirements carried by
  first-class spec fields, reduced to ephemeral constraints by
  `DerivedConstraints.Derive(spec)`: `QualityTier` → `node.tier
  contains <tier>`, `GpuMode` → `node.gpu.proxiedAvailable` /
  `node.gpu.passthroughAvailable eq true`, `ReplicationFactor > 0` →
  `node.hasActiveBlockStore eq true`. The field stays authoritative for
  execution; the derived constraint is how the one evaluator sees the
  requirement. Never persisted.

- **Tenant-expressible** (10, authored). Every other selection predicate.
  Architecture, locality, jurisdiction, country, reputation, GPU model,
  hardware capabilities, custom tags — all live in `spec.Constraints` and
  are evaluated through the constraint vocabulary registered in
  `ConstraintEvaluator.BuildTargetRegistry`.

---

## Quality tiers

Tiers determine overcommit ratios, price multipliers, and node eligibility
floors. `NodePerformanceEvaluator` assigns each node a set of eligible
tiers at registration based on its benchmark score.

| Tier | CPU overcommit | Storage overcommit | Benchmark floor | Price multiplier |
|---|---|---|---|---|
| Burstable | 4.0× | 2.5× | 1 000 | 0.5× |
| Balanced | 2.7× | 2.0× | 1 500 | 0.7× |
| Standard | 1.6× | 1.5× | 2 500 | 1.0× |
| Guaranteed | 1.0× | 1.0× | 4 000 | 1.8× |

`Burstable` is the baseline tier; all nodes that pass the performance
evaluation qualify for it. A node qualifies for a higher tier when its
benchmark score meets or exceeds that tier's floor.

Compute points per vCPU:

```
pointsPerVCpu = (tierMinBenchmark / baselineBenchmark)
              × (baselineOvercommitRatio / tierCpuOvercommitRatio)
```

---

## Scoring

Nodes that pass all hard filters are scored across four dimensions. Each
returns a value in `[0.0, 1.0]`. The weighted sum determines placement.

### Capacity score

```
remainingComputePoints / totalComputePoints
```

Higher remaining capacity = better. Encourages spreading load across the
pool and keeping nodes from saturating. Falls to 0.0 when remaining
capacity is zero. `remainingComputePoints` is computed by
`NodeCapacityCalculator` as
`AllocatedResources.ComputePoints − UsedResources.ComputePoints − ReservedResources.ComputePoints`;
the denominator is `TotalComputePoints` from the same availability
snapshot.

### Load score

```
max(0, 1.0 - loadAverage / 16.0)
```

Lower current load = better. Avoids stacking new VMs on already-saturated
hosts. Neutral 0.5 when `LatestMetrics` is null or `LoadAverage` is null.

### Reputation score

```
(uptimePercentage / 100 × 0.7) + (successRate × 0.3)
```

Where `uptimePercentage` is the 0.0–100.0 rolling 30-day measurement on
`Node.UptimePercentage` (divided by 100 in the formula to bring it into
the [0, 1] range), and `successRate = successfulVmCompletions /
totalVmsHosted`, or `0.5` for new nodes (`totalVmsHosted == 0`) —
neutral, neither rewarded nor penalised before they have a track record.

Single source of truth: `NodeReputation.Compute(node)` in
`src/Orchestrator/Services/VmScheduling/NodeReputation.cs`. The scoring
path, the `node.reputationScore` constraint target, and any future caller
all route through this method.

### Locality score

**Currently neutral (0.5) for all nodes.** Hard locality requirements
(region, zone, country, jurisdiction) are expressed as constraints in
FILTER 10 and evaluated as categorical hard filters. Soft locality
preferences — graduated rewards for proximity, adjacency, or
same-continent placement — are a deferred design concern. When
implemented, they will use a dedicated soft-preference mechanism that
feeds the locality score component without changing the hard-filter chain.

### Default weights

```
Capacity:    0.40
Load:        0.25
Reputation:  0.20
Locality:    0.15   (weight retained; score is neutral until soft preferences ship)
```

Operators change weights via `PUT /api/admin/scheduling-config`. The
update increments `SchedulingConfig.Version`, archives the previous
version, and takes effect on the next scheduling decision (cache TTL:
5 minutes). Weights must sum to 1.0; validation rejects any update that
violates this.

---

## Tunable parameters

Everything below lives in `SchedulingConfig` in MongoDB — versioned,
audit-logged, and updateable without a deploy. Defaults are conservative.

| Parameter | Default | Effect |
|---|---|---|
| `BaselineBenchmark` | 1 000 | Reference benchmark for "1 point per core" |
| `BaselineOvercommitRatio` | 4.0 | Burstable-tier overcommit, used in capacity calculation |
| `MaxPerformanceMultiplier` | 20.0 | Caps benchmark advantage of exceptionally fast hardware |
| `Tiers[t].MinimumBenchmark` | varies | Node benchmark floor for tier eligibility |
| `Tiers[t].CpuOvercommitRatio` | varies | Logical CPU capacity multiplier |
| `Tiers[t].StorageOvercommitRatio` | varies | qcow2 thin-provisioning multiplier |
| `Tiers[t].PriceMultiplier` | varies | Tenant pays this × baseline rate |
| `Limits.MaxUtilizationPercent` | 90.0 | Reject if VM placement would push node above this |
| `Limits.MinFreeMemoryMb` | 512 | Always-free RAM reservation |
| `Limits.MaxLoadAverage` | 8.0 | Reject nodes above this 1-minute load |
| `Weights.Capacity` | 0.40 | Scoring weight |
| `Weights.Load` | 0.25 | Scoring weight |
| `Weights.Reputation` | 0.20 | Scoring weight |
| `Weights.Locality` | 0.15 | Scoring weight |

`Burstable` tier must always be present in the configuration (it is the
baseline for capacity calculations); validation rejects any update that
omits it.

---

## Constraint vocabulary

Constraints are the sole mechanism for expressing tenant-side VM
node-selection requirements. All entries in `spec.Constraints` are
evaluated as a flat AND in FILTER 10 — alongside the constraints
*derived* from first-class spec fields (see "Derived constraints"
below), through the same evaluator. Authored constraints are validated
at VM creation; malformed entries are rejected before any resource is
allocated.

### Wire shape

```csharp
public class Constraint
{
    public required string  Target   { get; set; }  // e.g. "node.locality.country"
    public required string  Operator { get; set; }  // e.g. "in"
    public required object? Value    { get; set; }  // type depends on target + operator
}
```

JSON form:

```json
{ "target": "node.locality.jurisdictionTags",
  "operator": "contains_all",
  "value": ["EU", "Schengen"] }
```

### Typed constants

All target and operator names are available as compile-time constants in
`src/Orchestrator/Models/ConstraintVocabulary.cs`. Use these instead of
string literals everywhere a constraint is constructed or compared.

```csharp
// Correct — compile-time constant, refactor-safe
new Constraint
{
    Target   = ConstraintTargets.Node.Locality.Country,
    Operator = ConstraintOperators.In,
    Value    = new[] { "DE", "FR", "NL" }
}

// Wrong — string literal, breaks silently on typo
new Constraint { Target = "node.locality.cuntry", Operator = "in", ... }
```

### Target vocabulary

| Constant | Wire value | Node source | Type |
|---|---|---|---|
| `ConstraintTargets.Node.Country` | `node.country` | `Node.Locality.Country` | string (alias) |
| `ConstraintTargets.Node.Locality.Country` | `node.locality.country` | `Node.Locality.Country` | string |
| `ConstraintTargets.Node.Locality.Region` | `node.locality.region` | `Node.Locality.Region` | string |
| `ConstraintTargets.Node.Locality.Zone` | `node.locality.zone` | `Node.Locality.Zone` | string |
| `ConstraintTargets.Node.Locality.JurisdictionTags` | `node.locality.jurisdictionTags` | `Node.Locality.JurisdictionTags` | string[] |
| `ConstraintTargets.Node.Locality.LocationMismatch` | `node.locality.locationMismatch` | `Node.Locality.LocationMismatch` | bool |
| `ConstraintTargets.Node.Architecture` | `node.architecture` | `Node.Architecture` | string |
| `ConstraintTargets.Node.KvmAvailable` | `node.kvmAvailable` | `Node.HardwareInventory.KvmAvailable` | bool |
| `ConstraintTargets.Node.GpuModel` | `node.gpuModel` | `Node.HardwareInventory.Gpus[0].Model` | string |
| `ConstraintTargets.Node.Gpu.ProxiedAvailable` | `node.gpu.proxiedAvailable` | `SupportsGpu && Gpus.Count > 0 && HasProxiedCapableGpu` | bool |
| `ConstraintTargets.Node.Gpu.PassthroughAvailable` | `node.gpu.passthroughAvailable` | `SupportsGpu && Gpus.Count > 0 && HasPassthroughCapableGpu` | bool |
| `ConstraintTargets.Node.HasActiveBlockStore` | `node.hasActiveBlockStore` | `Node.BlockStoreInfo?.Status == BlockStoreStatus.Active` | bool |
| `ConstraintTargets.Node.Hardware.HasGpu` | `node.hardware.hasGpu` | `Node.HardwareInventory.SupportsGpu` | bool |
| `ConstraintTargets.Node.Hardware.HasNvme` | `node.hardware.hasNvme` | `Node.HardwareInventory.Storage.Any(s => s.Type == StorageType.NVMe)` | bool |
| `ConstraintTargets.Node.Hardware.HighBandwidth` | `node.hardware.highBandwidth` | `Node.HardwareInventory.Network.BandwidthBitsPerSecond > 1_000_000_000` | bool |
| `ConstraintTargets.Node.Hardware.CpuCores` | `node.hardware.cpuCores` | `Node.HardwareInventory.Cpu.PhysicalCores` | numeric |
| `ConstraintTargets.Node.Hardware.GpuVramBytes` | `node.hardware.gpuVramBytes` | `Node.HardwareInventory.Gpus.Sum(g => g.MemoryBytes)` | numeric |
| `ConstraintTargets.Node.Tier` | `node.tier` | `Node.PerformanceEvaluation.EligibleTiers` | string[] |
| `ConstraintTargets.Node.BenchmarkScore` | `node.benchmarkScore` | `Node.PerformanceEvaluation.BenchmarkScore` | numeric |
| `ConstraintTargets.Node.UptimePercent` | `node.uptimePercent` | `Node.UptimePercentage` | numeric |
| `ConstraintTargets.Node.ReputationScore` | `node.reputationScore` | `NodeReputation.Compute(node)` | numeric |
| `ConstraintTargets.Node.Tags` | `node.tags` | `Node.Tags` | string[] |
| *(system-generated)* | `node.id` | `Node.Id` | string *(internal)* |

`node.country` and `node.locality.country` resolve to the same field.
Prefer `ConstraintTargets.Node.Locality.Country` in new code.

`node.id` is a **system-generated-only** target. `StackService` injects it
as an ephemeral anti-affinity constraint before calling `CreateVmAsync` (Path
E, STEP 5). It is never user-authored, never persisted on the VM, and is not
exposed in the constraint builder UI or the preset library. Adding it to the
builder would require the user to know node IDs at design time, which is not
possible for stacks that mix deployed and not-yet-deployed blocks.

Use `ConstraintTargets.Node.ReputationScore` when the composite
reputation measure matters; use `ConstraintTargets.Node.UptimePercent`
when only uptime matters.

`node.tier` is **load-bearing**: every VM derives `node.tier contains
<spec.QualityTier>` (see "Derived constraints" below) — it replaced the
former FILTER 2 and is the most-evaluated target in the system. Most
targets reflect static hardware spec or slowly-changing state, but not
all: locality changes on re-registration, and `node.gpu.proxiedAvailable`
is designed to track GPU-proxy daemon liveness once the node-side health
fix lands. What the vocabulary never contains is a live *counter* (free
memory, remaining VRAM) — see §11.

`node.kvmAvailable` is redundant with the unconditional KVM hard filter
(FILTER 9) — expressing it as a constraint re-asserts a platform
guarantee. It remains registered but inert; its removal is staged in the
vocabulary restructure (see `VOCABULARY_RESTRUCTURE.md`), together with
a subsystem regrouping of the `node.hardware.*` / flat
performance-and-reputation names.

Adding a new target requires: (1) a constant in `ConstraintVocabulary.cs`,
(2) a `TargetDescriptor` entry in `ConstraintEvaluator.BuildTargetRegistry`,
(3) an entry in this table.

### Operator vocabulary

| Constant | Wire value | Semantics | Compatible types |
|---|---|---|---|
| `ConstraintOperators.Eq` | `eq` | Equality (case-insensitive for strings) | string, numeric, bool |
| `ConstraintOperators.Neq` | `neq` | Inequality | string, numeric, bool |
| `ConstraintOperators.In` | `in` | Scalar is a member of configured list | string, numeric |
| `ConstraintOperators.NotIn` | `not_in` | Scalar is not a member of configured list | string, numeric |
| `ConstraintOperators.Contains` | `contains` | Target list contains the configured scalar | string[] |
| `ConstraintOperators.NotContains` | `not_contains` | Target list does not contain configured scalar | string[] |
| `ConstraintOperators.ContainsAll` | `contains_all` | Target list is a superset of configured list | string[] |
| `ConstraintOperators.ContainsAny` | `contains_any` | Target list and configured list overlap | string[] |
| `ConstraintOperators.ContainsNone` | `contains_none` | Target list and configured list do not overlap | string[] |
| `ConstraintOperators.Gte` | `gte` | Greater than or equal | numeric |
| `ConstraintOperators.Lte` | `lte` | Less than or equal | numeric |
| `ConstraintOperators.Gt` | `gt` | Strictly greater than | numeric |
| `ConstraintOperators.Lt` | `lt` | Strictly less than | numeric |
| `ConstraintOperators.StartsWith` | `starts_with` | Target string starts with the configured prefix, case-insensitive. Primary use: hierarchical region codes — `node.locality.region starts_with "na"` matches `na-central`, `na-east`, `na-west`. | string |
| `ConstraintOperators.EndsWith` | `ends_with` | Target string ends with the configured suffix, case-insensitive. Example: `node.locality.region ends_with "central"` matches `na-central`, `eu-central`, `ap-central`. | string |
| `ConstraintOperators.Includes` | `includes` | Target string contains the configured substring, case-insensitive. Named `includes` to avoid collision with `contains` (which checks list membership). Example: `node.gpuModel includes "3090"` matches `RTX 3090`, `RTX 3090 Ti`. | string |
| `ConstraintOperators.AdjacentTo` | `adjacent_to` | Node's region is adjacent to configured region per `region-adjacency.json` | string (region) |
| `ConstraintOperators.SameContinentAs` | `same_continent_as` | Node's region shares a continent with configured region | string (region) |
| `ConstraintOperators.HasJurisdictionTag` | `has_jurisdiction_tag` | Node's country carries the configured supranational tag | string (country) |

### Validation

Constraints are validated at VM creation in `VmService.CreateVmAsync`
via `IConstraintEvaluator.ValidateSet` — before any resource is allocated,
before scheduling is attempted. Validation checks:

- Unknown `target` → reject with `"Unknown target '...'"`
- Unknown `operator` → reject with `"Unknown operator '...'"`
- Type incompatibility → reject with `"Operator '...' is not compatible with target '...'"`
- Malformed `value` (e.g., `in` with a scalar instead of a list) → reject

Error messages include the constraint index so tenants can locate the
offending entry: `"Constraint #2: Unknown target 'node.foobar'"`.

### Derived constraints

First-class spec fields that carry placement requirements are reduced to
ephemeral constraints by `DerivedConstraints.Derive(spec)`
(`src/Orchestrator/Services/VmScheduling/DerivedConstraint.cs`) and fed
through the same evaluator as authored constraints:

| Spec field condition | Derived constraint | Origin label |
|---|---|---|
| always (every VM has a tier) | `node.tier contains <spec.QualityTier>` | `QualityTier=<t>` |
| `GpuMode == Proxied` | `node.gpu.proxiedAvailable eq true` | `GpuMode=Proxied` |
| `GpuMode == Passthrough` | `node.gpu.passthroughAvailable eq true` | `GpuMode=Passthrough` |
| `ReplicationFactor > 0` | `node.hasActiveBlockStore eq true` | `ReplicationFactor=<n>` |

Derived constraints are **never persisted** — the spec field stays
authoritative for execution (billing, CPU quota, GPU assignment,
lazysync); derivation is only how the evaluator sees the requirement.
This generalizes the ephemeral `node.architecture` constraint that
`MigrateVmAsync` has always used. Rejection and compliance messages name
the origin field (`Derived from GpuMode=Proxied: ...`) — never an index
into a constraint list the tenant never wrote.

The same `Derive` function runs in compliance
(`NodeService.FlagNonCompliantVmsAsync`), so scheduling and compliance
cannot drift — a VM with zero authored constraints is still evaluated
against its derived requirements. Parity with the deleted hard filters
(FILTER 2, FILTER 5 capability, FILTER 8.1) is proven by
`DerivedConstraintsParityTests`, which uses the deleted filter bodies
verbatim as oracles.

### FILTER 10 evaluation loop

```csharp
// Derived first — preserves the old precedence where the tier /
// GPU-capability / BlockStore hard filters ran before tenant constraints.
foreach (var derived in DerivedConstraints.Derive(spec))
{
    var result = _constraintEvaluator.Evaluate(derived.Constraint, node);
    if (!result.Passed)
        return $"Derived from {derived.Origin}: {result.RejectionReason}";
}

if (spec.Constraints is { Count: > 0 })
{
    for (var i = 0; i < spec.Constraints.Count; i++)
    {
        var result = _constraintEvaluator.Evaluate(spec.Constraints[i], node);
        if (!result.Passed)
            return $"Constraint #{i} failed: {result.RejectionReason}";
    }
}
```

The evaluator is a singleton; its target and operator registries are
built once at construction and are immutable and thread-safe. The hot
path is: look up target extractor → extract node value → look up operator
evaluator → compare actual vs configured value.

### Updating constraints on a stranded VM

A tenant whose VM is in `Error` state waiting for migration can update
its scheduling constraints via:

```
PATCH /api/vms/{vmId}/scheduling
Body: { "constraints": [ ... ] }
```

The full constraint list is replaced atomically. The migration scheduler
picks up the change on its next scan cycle (≤10 s). Validation runs
before saving; malformed entries are rejected. Passing `null` or an
empty list removes all authored constraints (the VM will migrate to any
node satisfying its derived requirements — tier, GPU mode, replication).

Only allowed in `Error` state. The endpoint requires ownership or `Admin`
role.

### JSON serialization notes

- `QualityTier` and `BandwidthTier` enums are serialized as strings via
  `JsonStringEnumConverter` (registered globally). Wire form:
  `"qualityTier": "Guaranteed"`, not `"qualityTier": 1`.
- `Constraint.Value` arrives as a `JsonElement` from JSON wire input or
  `BsonValue` from MongoDB reads. Before any `SaveVmAsync` call,
  `Constraint.NormalizeValue()` unboxes `JsonElement` into native C#
  types (`string`, `bool`, `double`, `List<object?>`) so MongoDB's
  `ObjectSerializer` can persist the document. This is called in
  `VmService.CreateVmAsync` and `VmsController.UpdateSchedulingConstraints`
  immediately after `ValidateSet`. The evaluator also normalizes
  internally at evaluation time; calling `NormalizeValue()` on an
  already-normalized value is a safe no-op.

---

## API endpoints

### Scheduling configuration (Admin only)

All endpoints under `/api/admin/` require the `Admin` role. Authentication
is enforced at the controller level.

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/admin/scheduling-config` | Current configuration |
| `PUT` | `/api/admin/scheduling-config` | Replace configuration (validates, archives previous, increments version) |
| `POST` | `/api/admin/scheduling-config/reload` | Force cache flush |
| `GET` | `/api/admin/scheduling-config/history?limit=10` | N most recent archived configurations |
| `POST` | `/api/admin/scheduling-config/validate` | Validate a candidate config without persisting |

### VM scheduling constraint update

| Method | Path | Description |
|---|---|---|
| `PATCH` | `/api/vms/{vmId}/scheduling` | Replace scheduling constraints on a stranded VM (Error state only) |

### Constraint vocabulary (anonymous, read-only)

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/vms/constraint-vocabulary` | Registered target and operator names |

Response:

```json
{
  "targets":     ["node.architecture", "node.country", ...],
  "targetTypes": { "node.architecture": "String", "node.uptimePercent": "Numeric", "node.hardware.hasGpu": "Boolean", ... },
  "operators":   ["adjacent_to", "contains", ...]
}
```

`targets` — sorted list of all registered target name strings.  
`targetTypes` — mapping from target name to value type (`"String"`,
`"Numeric"`, `"Boolean"`, `"StringList"`). The constraint builder uses
this to filter operator dropdowns and render the appropriate value input
without hardcoding metadata client-side. Adding a target in
`ConstraintEvaluator.BuildTargetRegistry` makes it appear here
automatically.  
`operators` — sorted list of all registered operator name strings.

A richer schema (closed-domain enumerations, descriptions, units,
default operator suggestions) is still planned — see §11.

### Template deployment

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/marketplace/templates/{templateId}/deploy` | Deploy a VM from a template — applies the merge policy from §2 Path D, then converges on Path A |

### Locality reference (read-only, anonymous)

See [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) for the country/region
model. Reference endpoints:

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/locality/countries` | Full country list with jurisdiction tags |
| `GET` | `/api/locality/regions` | Full region list |
| `GET` | `/api/locality/suggest/{country}` | Suggested region for a country code |

### Constraint builder preview

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/vms/scheduling/preview` | Required | Eligibility preview — accepts `{ constraints, qualityTier? }`, returns count of matching nodes and top rejection reasons (see §9). Uses a minimal VmSpec (1 vCPU / 256 MB / 1 GB, `ComputePointCost=0`) so resource-capacity filters do not mask constraint mismatches. The quality tier is honoured via the derived `node.tier` constraint (FILTER 10). Rejection reasons are normalized by stripping per-node actual values so structurally identical rejections collapse into one count. |

### Preset library

Constraint presets are served as a static file — no controller endpoint
is required. The file `wwwroot/public/config/constraint-presets.json`
is copied verbatim into `wwwroot/dist/` by the Vite build and served at
`/config/constraint-presets.json` by the production static-file
middleware. In development the Vite dev server serves it from
`wwwroot/public/` at the same path.

The constraint builder loads it once per page session via a plain
`fetch('/config/constraint-presets.json')` call (no auth required). If
the fetch fails the preset section is silently omitted and the builder
continues to function normally.

---

## Client integration

The dashboard exposes a single reusable **constraint builder** component
that the user encounters in five contexts:

1. **Create VM modal** (`POST /api/vms`) — empty initial state, no locked rows.
2. **Template deploy modal** — pre-populated with
   `template.MinimumSpec.Constraints` (locked) and
   `template.RecommendedSpec.Constraints` (editable defaults). See §2
   Path D for the merge policy.
3. **Template detail page** — read-only display of both constraint
   sources, so users can review requirements before clicking Deploy.
4. **Stranded VM editor** (`PATCH /api/vms/{id}/scheduling` for VMs in
   `Error` state) — pre-populated with the VM's current constraints.
5. **Diagnostics view** — read-only, with eligibility annotations per
   constraint.

Same component, same `Constraint[]` data shape, five modes. This is the
single source of truth for constraint editing across the dashboard — no
per-flow ad-hoc constraint logic.

### Two-layer UX

To keep the builder approachable for the common cases without locking out
the long tail:

- **Preset library** — one-click toggles for frequently-needed requirements
  ("EU jurisdiction only", "GPU required", "NVMe storage", "≥99% uptime").
  Each preset compiles to one or more ordinary constraints. The list is
  curated in `wwwroot/public/config/constraint-presets.json` (7 presets
  across 4 categories: Jurisdiction, Hardware, Reliability, Network),
  loaded by the builder at mount time via a plain `fetch`. No auth
  required, no startup validation — if the file is missing the section
  is silently omitted and the builder works normally.

- **Custom builder** — three-column rows (target dropdown, operator
  dropdown filtered by target type, value input rendered based on
  `(target, operator)`). Power users compose anything the vocabulary
  supports. Same data path as presets — both write to the underlying
  `Constraint[]`.

Locked rows from `template.MinimumSpec.Constraints` appear in the custom
section as greyed-out, non-removable entries with a "Required by template"
tooltip.

### Live eligibility preview

While the user edits constraints, the builder debounces (500 ms) and
calls `POST /api/vms/scheduling/preview` with the working constraint
set. Response includes the count of eligible nodes and the top rejection
reasons among ineligible ones:

```json
{
  "totalOnline": 47,
  "eligible": 12,
  "rejected": 35,
  "rejectionReasons": [
    { "count": 28, "reason": "node.locality.country not in [DE, FR]" },
    { "count":  7, "reason": "node.hardware.hasNvme = false" }
  ]
}
```

Users discover infeasible constraint combinations *while editing*, not
on deploy failure. This is the single most impactful UX affordance the
builder provides.

### Component contract

The builder is a vanilla ES module (no framework dependency) exposing:

```javascript
const handle = constraintBuilder.mount(containerEl, {
    initial: existingConstraints,     // Constraint[] starting state
    lockedRows: templateConstraints,  // template.MinimumSpec.Constraints (non-removable)
    mode: 'edit' | 'readonly',
    onChange: (constraints) => { ... } // fired on every edit, debounced for preview
});
```

The builder owns its DOM and its working state. Consumers pass an array
in, get an array out — they never reach inside.

### Vocabulary as the source of truth

The builder never hardcodes target names or operator names. Both come
from `GET /api/vms/constraint-vocabulary` (cached for the session).
Adding a new target in `ConstraintEvaluator.BuildTargetRegistry` makes it
immediately available in the builder UI — no frontend deploy required.

Presets are *data*: `wwwroot/public/config/constraint-presets.json`,
served as a static asset at `/config/constraint-presets.json`. Adding a
preset is editing the JSON file and redeploying. Curation (user-friendly
label and category) is separate from vocabulary (machine-readable
target/operator/value); adding a new target does not auto-create a
preset.

Implementation lives in
`src/Orchestrator/wwwroot/src/constraint-builder.js`.

### Stack composition (visual infrastructure editor)

The constraint builder is embedded within a larger **visual infrastructure
editor** where multi-VM topologies are composed as node graphs. Each VM
template becomes a block with typed port connectors; a wire between two
ports means "block B can reach block A on that port via an internal
hostname." The editor is the creation interface for `StackTemplate`
documents.

#### Two vocabularies at design time

The editor exposes two distinct types of block configuration that must
not be conflated:

**Node requirements** — `spec.Constraints` (vocabulary-based, §7).
Expressed using the standard constraint builder embedded in each block's
config panel. These constraints describe what kind of node the block
*needs*: GPU, NVMe, EU jurisdiction, architecture. They are absolute and
apply regardless of what other blocks exist.

**Placement policies** — symbolic, reference other *blocks* by name, not
node IDs. Never reference concrete node IDs (those do not exist at design
time). Defined in the `StackTemplate`, resolved into concrete scheduling
decisions by `StackService` at deploy time.

```
Placement policy vocabulary:

co-locate    with: <blockId>           fallback: same-region | any | fail
             "This block must run on the same node as <blockId>."

same-region  as: <blockId>            fallback: any | fail
             "This block must run in the same region as <blockId>."

anti-affinity against: [<blockId>, …]
             "This block must NOT share a node with any of these blocks."

any          (default — no placement constraint)
```

Placement policies are design-time intent. The distinction between
"deployed block" and "not-yet-deployed block" is not relevant to
policy definition — the policy references a block ID and `StackService`
resolves it whenever deployment is triggered, reading current node
assignments from MongoDB for already-deployed members.

#### Preflight feasibility before any VM is created

Before STEP 5 (actual deployment), `StackService` computes the merged
constraint set for every placement group and submits it to
`POST /api/vms/scheduling/preview`. The editor surfaces this result
on the canvas:

- **Each block** shows its individual eligible node count (standard
  preview counter from the embedded constraint builder).
- **Each co-location group** shows the merged feasibility: "3 nodes can
  host both A and B together" — a lower count than either block alone.
- **Infeasibility** (0 matching nodes) highlights the group in red and
  lists the conflicting constraints before the user clicks Deploy. No
  VM is created.

#### Wire coloring (post-deployment)

After a stack is (partially or fully) deployed, wires are colored by
the actual relationship between the connected VMs' nodes:

| Color | Meaning |
|---|---|
| Green | Same node — direct libvirt bridge, negligible latency |
| Yellow | Same region — intra-region WireGuard, low latency |
| Orange | Adjacent region — cross-region, higher latency |
| Red | Cross-continent or high-latency path |

Source: `Node.Locality.Region` from MongoDB for each VM's `NodeId`,
evaluated through `LocalityService.AreAdjacent` (the adjacency graph
already used by the `adjacent_to` constraint operator).

#### Constraint merge order in Path E

When a block in a stack is deployed, four constraint sources are merged
in priority order (highest to lowest):

```
stackConstraints                    ← mandatory for every block in the stack
block.extraConstraints              ← block-specific node requirements
template.MinimumSpec.Constraints    ← template mandatory (non-removable)
template.RecommendedSpec.Constraints← template defaults (user-overridable)
```

Same `MergeConstraints` function used by Path D. `StackService` is the
new outer caller; the merge logic itself is unchanged.

Co-location groups additionally intersect the merged constraint sets of
*all* members before scheduling the anchor block — the anchor must
satisfy every block in the group simultaneously.


---

## Worked example

A fintech tenant needs a payment-processing VM with strict placement
requirements:

1. Must run in an EU member state (GDPR, PSD2)
2. Must not run in countries on their internal exclusion list
3. Must have a Guaranteed-tier node (predictable performance)
4. Operator must have ≥ 99 % uptime
5. Must not be on a node with a location mismatch (IP-derived country
   disagrees with declared country — they need jurisdictional certainty)
6. Must run in `eu-central` or `eu-west` specifically

All requirements are expressed in `spec.Constraints`. Constraints are
built using the typed constants from `ConstraintVocabulary.cs`:

```csharp
var constraints = new List<Constraint>
{
    // C0 — EU jurisdiction (GDPR, PSD2)
    new() {
        Target   = ConstraintTargets.Node.Locality.JurisdictionTags,
        Operator = ConstraintOperators.Contains,
        Value    = "EU"
    },
    // C1 — country exclusion list
    new() {
        Target   = ConstraintTargets.Node.Locality.Country,
        Operator = ConstraintOperators.NotIn,
        Value    = new[] { "RU", "BY", "CN" }
    },
    // C2 — operator reliability floor
    new() {
        Target   = ConstraintTargets.Node.UptimePercent,
        Operator = ConstraintOperators.Gte,
        Value    = 99.0
    },
    // C3 — jurisdictional certainty (no IP/declared country mismatch)
    new() {
        Target   = ConstraintTargets.Node.Locality.LocationMismatch,
        Operator = ConstraintOperators.Eq,
        Value    = false
    },
    // C4 — region preference as hard requirement
    new() {
        Target   = ConstraintTargets.Node.Locality.Region,
        Operator = ConstraintOperators.In,
        Value    = new[] { "eu-central", "eu-west" }
    },
};
```

Over the wire (JSON), each constant resolves to its string value — see
the vocabulary table in §7 for the mapping. The JSON form a tenant sends
in `POST /api/vms` is:

```json
{
  "name": "payment-processor-prod-1",
  "spec": {
    "virtualCpuCores": 4,
    "memoryBytes": 8589934592,
    "diskBytes": 107374182400,
    "qualityTier": "Guaranteed",
    "constraints": [
      { "target": "node.locality.jurisdictionTags", "operator": "contains",  "value": "EU" },
      { "target": "node.locality.country",          "operator": "not_in",    "value": ["RU", "BY", "CN"] },
      { "target": "node.uptimePercent",             "operator": "gte",       "value": 99.0 },
      { "target": "node.locality.locationMismatch", "operator": "eq",        "value": false },
      { "target": "node.locality.region",           "operator": "in",        "value": ["eu-central", "eu-west"] }
    ]
  }
}
```

### What the scheduler does

1. **FILTER 1 (Status):** drops offline nodes.
2. **FILTER 1.5 (Readiness):** drops logged-out nodes.
3. **FILTER 5 (VRAM capacity):** `GpuMode` not set → no check.
4. **FILTER 6 (Load):** drops nodes above the load ceiling (default 8.0).
5. **FILTER 7 (Memory):** drops nodes below the free-memory floor (default 512 MB).
6. **FILTER 8 (Obligations):** drops nodes with any non-Active system obligation.
7. **FILTER 9 (KVM):** drops non-KVM nodes.
8. **FILTER 10 (Unified constraints):**
   - Derived from `QualityTier=Guaranteed`: `node.tier contains
     Guaranteed` — drops nodes not eligible (benchmark < 4 000) and
     nodes with no performance evaluation. (`GpuMode` unset and
     `ReplicationFactor` unset derive nothing further.)
   - C0: `jurisdictionTags contains EU` — drops non-EU nodes.
   - C1: `country not_in [RU, BY, CN]` — drops excluded countries.
   - C2: `uptimePercent gte 99.0` — drops unreliable operators.
   - C3: `locationMismatch eq false` — drops VPN/leased-foreign nodes.
   - C4: `region in [eu-central, eu-west]` — restricts to preferred regions.

Eligible survivors are scored (capacity 0.40 + load 0.25 + reputation
0.20 + locality 0.15 neutral). The node with the highest total score
wins.

### Adding architecture pinning

If this tenant's template only publishes x86_64 artifacts, add one more
constraint using the typed constant:

```csharp
new() {
    Target   = ConstraintTargets.Node.Architecture,
    Operator = ConstraintOperators.Eq,
    Value    = "x86_64"
}
```

Wire form: `{ "target": "node.architecture", "operator": "eq", "value": "x86_64" }`.

Multi-arch templates do not need this — architecture is resolved
post-scheduling from the selected node's `HardwareInventory.Cpu.Architecture`,
and artifact URLs are substituted accordingly.

### Same workload via marketplace template

If the same tenant deploys a payment-processor *template* from the
marketplace (Path D), the template author can bake the mandatory EU
requirement into `MinimumSpec.Constraints` so it survives any user
customization:

```csharp
template.MinimumSpec.Constraints = new()
{
    new() {
        Target   = ConstraintTargets.Node.Locality.JurisdictionTags,
        Operator = ConstraintOperators.Contains,
        Value    = "EU"
    },
    new() {
        Target   = ConstraintTargets.Node.Locality.LocationMismatch,
        Operator = ConstraintOperators.Eq,
        Value    = false
    },
};
```

The author can put the country exclusion list and uptime floor in
`RecommendedSpec.Constraints` so users can override them per deployment.
The dashboard renders `MinimumSpec.Constraints` as locked rows in the
deploy modal; users see exactly what the template requires before they
deploy.

---

## What is intentionally NOT here

- **Marketplace browse.** `GET /api/nodes/search` and `GET /api/nodes/featured`
  do not run through the scheduler's filter chain. They are a browse
  affordance backed by `INodeMarketplaceService` with its own, independent
  filter logic. A node listed in the marketplace may be rejected at deploy
  time. This is by design — browse is exploration, not pre-flight. If a
  future requirement needs eligibility-aware browsing for a specific VM
  spec, that is a new endpoint with a `VmSpec` parameter, not a change to
  the existing browse paths.

- **Soft locality preferences.** The locality score is neutral (0.5) today.
  Graduated rewards for same-region, adjacent-region, or same-continent
  placement are deferred. Hard locality requirements (specific region,
  country, jurisdiction) are fully expressible via constraints. When soft
  preferences ship, they will feed the locality scoring component through a
  separate mechanism without touching the hard-filter chain.

- **Node-side constraints.** Operator placement policies ("this node only
  accepts Guaranteed-tier VMs") are a future feature. `IConstraintEvaluator`
  is designed to support the inverse direction (`constraint, node, vmSpec`),
  but no `Node.Constraints` field exists yet.

- **Affinity / anti-affinity between VMs.** Implemented at the
  `StackService` level via placement policies (see §9), not as a scheduler
  primitive. The scheduler remains stateless and single-VM-per-decision.
  Co-location is resolved via Path B (`targetNodeId`); anti-affinity is
  enforced by `StackService` injecting ephemeral `node.id not_in [...]`
  constraints before each `CreateVmAsync` call. These constraints are never
  persisted on the VM and are not user-authored.
  `VmSpec.SchedulingTags` is reserved for future use but not consumed today.

- **Soft constraint composition (OR, NOT groups).** The constraint list is a
  flat AND. Negation is expressible per-constraint (`not_in`,
  `not_contains`, `neq`). OR composition and nested groups are not in v1.

- **Per-tenant scheduling overrides.** No mechanism exists for trusted
  tenants to bypass filters. Not planned.

- **Cost-optimal placement.** The scheduler picks the highest-scoring
  eligible node, not the cheapest. Tenants control cost through tier
  selection and constraint design.

- **Live migration.** The scheduler decides where a VM first lands.
  Moving a running VM is handled by `BackgroundServices.MigrateVmAsync`,
  which calls the same filter chain but adds an architecture-stickiness
  constraint.

- **Vocabulary introspection beyond names.** The
  `/api/vms/constraint-vocabulary` endpoint now returns `targetTypes`
  (mapping from target name to value type string: `"String"`,
  `"Numeric"`, `"Boolean"`, `"StringList"`) alongside `targets` and
  `operators`. The constraint builder uses this to filter operator
  dropdowns and render typed value inputs. Still deferred: closed-domain
  enumerations (e.g., valid region codes for `node.locality.region`),
  per-target descriptions, units, and default operator suggestions.
  The data lives in `TargetDescriptor` and `OperatorDescriptor`.

- **Resource-threshold constraints (live counters).** The vocabulary
  exposes static node attributes — physical CPU cores, total GPU VRAM,
  declared bandwidth class. It does *not* expose
  `availableComputePoints` or `freeMemoryBytes` as constraint targets,
  even though these fields exist on `Node`. The v1 "static fields only"
  principle (Constraint.cs) prevents tenants from writing
  "give me a node with ≥ 16 free CPU cores right now" — such
  constraints would pass validation but might fail at scheduling time
  because the value drifts between validation and evaluation. Resource
  thresholds are enforced by the platform filters (FILTER 7 for memory,
  FILTER 5 for proxied-GPU VRAM headroom, and the implicit capacity
  check) rather than by tenant constraints. The same principle is why
  capacity is never a *derived* constraint either — capability
  ("can this node ever host this GpuMode") derives cleanly; capacity
  ("is there room right now") stays a placement-time hard filter and is
  never evaluated in compliance, where the resident VM would count
  against itself.

---

## References

**Code**

| File | Role |
|---|---|
| `src/Orchestrator/Services/VmScheduling/VmSchedulingService.cs` | Filter chain, scoring, public scheduling API |
| `src/Orchestrator/Services/VmScheduling/ConstraintEvaluator.cs` | Constraint evaluation, target and operator registries |
| `src/Orchestrator/Services/VmScheduling/DerivedConstraint.cs` | Spec-field → derived-constraint reduction (unified evaluation) |
| `src/Orchestrator/Models/ConstraintVocabulary.cs` | `ConstraintTargets` and `ConstraintOperators` typed constants |
| `src/Orchestrator/Models/Constraint.cs` | `Constraint` and `ConstraintEvaluation` models |
| `src/Orchestrator/Services/VmScheduling/NodeReputation.cs` | Reputation formula (single source of truth) |
| `src/Orchestrator/Services/VmScheduling/SchedulingConfigService.cs` | Versioned config + history |
| `src/Orchestrator/Services/Locality/LocalityService.cs` | Locality data + adjacency graph |
| `src/Orchestrator/Services/NodeCapacityCalculator.cs` | Overcommit math |
| `src/Orchestrator/Services/NodePerformanceEvaluator.cs` | Tier eligibility |
| `src/Orchestrator/Services/TemplateService.cs` | `BuildVmRequestFromTemplateAsync` — Path D merge policy |
| `src/Orchestrator/Services/StackService.cs` | *(planned)* Path E — stack deployment, placement graph solver, constraint merge, anti-affinity injection |
| `src/Orchestrator/Models/StackTemplate.cs` | *(planned)* `StackTemplate`, `StackBlock`, `StackConnection`, `PlacementPolicy` models |
| `src/Orchestrator/Controllers/MarketplaceController.cs` | `POST /api/marketplace/templates/{id}/deploy` — Path D entry |
| `src/Orchestrator/Models/SchedulingConfig.cs` | Config schema |
| `src/Orchestrator/Models/VirtualMachine.cs` | `VmSpec` (host of `Constraints`) |
| `src/Orchestrator/Controllers/VmsController.cs` | VM creation, PATCH /scheduling, GET /constraint-vocabulary |
| `src/Orchestrator/Background/BackgroundServices.cs` | Migration scheduler |
| `src/Orchestrator/Interfaces/VmScheduling/IVmSchedulingService.cs` | Scheduler contract |
| `src/Orchestrator/wwwroot/src/constraint-builder.js` | Global constraint builder UI module (ES module, `mount()` API) |
| `wwwroot/public/config/constraint-presets.json` | Preset library data (7 presets; served as static asset at `/config/constraint-presets.json`) |

**Related docs**

- [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) — country/region/zone model
- [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — node states the scheduler consumes
