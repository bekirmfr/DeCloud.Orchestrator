# DeCloud Scheduling

How the orchestrator places tenant VMs onto operator nodes: the entry
paths, the filter chain, the scoring model, the tunable parameters, and
the constraint vocabulary that lets tenants express all node selection
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
9. [Worked example](#worked-example)
10. [What is intentionally NOT here](#what-is-intentionally-not-here)
11. [References](#references)

---

## Overview

When a tenant requests a VM, the orchestrator picks a single node from
the federated pool. The scheduler does this in two phases:

1. **Hard filtering** — eliminate every node that cannot host this VM.
   A failed hard filter is a categorical reject; no scoring happens.

2. **Scoring** — among eligible nodes, compute a weighted score across
   capacity, load, reputation, and locality, then pick the highest.

**The constraint engine is the sole mechanism for node selection
requirements.** Tenants express all requirements — locality, architecture,
reputation, jurisdiction, GPU model, or any other per-node predicate —
as a flat AND list of `{ target, operator, value }` constraints in
`spec.Constraints`. No bespoke flat fields exist for scheduling. No
method-level override parameters exist on the scheduler interface.

Configuration lives in MongoDB (`scheduling_configs` collection,
versioned with audit history) and is served by `ISchedulingConfigService`
with a 5-minute in-memory cache. Operators with the `Admin` role update
it through `PUT /api/admin/scheduling-config`.

---

## Entry paths

Three paths reach the scheduler. All converge on the same filter chain.

### Path A — Auto-scheduling (default VM creation)

```
POST /api/vms  (no targetNodeId)
  → VmsController.Create
  → VmService.CreateVmAsync
      STEP 1: validate spec.Constraints (ValidateSet)
      STEP 2: name pipeline, quota check, password gen
      STEP 3: persist VM (status: Pending)
      STEP 4: TryScheduleVmAsync
                → _schedulingService.SelectBestNodeForVmAsync(spec, tier)
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
                → _schedulingService.ValidateNodeForVmAsync(node, spec, tier)
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
      → _schedulingService.SelectBestNodeForVmAsync(specCopy, tier)
          → same filter chain as Path A
```

A VM that booted on an x86_64 node has its overlay disk filled with
x86_64 binaries. Migration enforces architecture stickiness via a
constraint so the VM cannot land on an architecturally incompatible node.

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
at deploy time (Path A or B). A node visible in marketplace listings may
be rejected at deploy if the VM's constraints are not satisfied. This is
expected and by design.

---

## The filter chain

All three scheduling paths converge on `ApplyHardFiltersAsync` in
`VmSchedulingService`. It evaluates filters in order, short-circuiting on
the first failure. The rejection reason is a human-readable string returned
to the caller (and surfaced in `ScoredNode.RejectionReason`).

```
ApplyHardFiltersAsync(node, spec, tier, tierConfig, config, ct)
  │
  ├── FILTER 1: Node status
  │     node.Status == Online?
  │     Source: node heartbeat
  │
  ├── FILTER 2: Tier eligibility
  │     node.PerformanceEvaluation.EligibleTiers contains tier?
  │     Source: NodePerformanceEvaluator at registration
  │
  ├── FILTER 5: GPU mode
  │     spec.GpuMode == Passthrough → node has IOMMU-capable GPU for VFIO?
  │     spec.GpuMode == Proxied     → node has any GPU?
  │     spec.GpuMode == None        → (no check)
  │     Source: spec.GpuMode + node.HardwareInventory
  │
  ├── FILTER 6: Load average
  │     node.LatestMetrics.LoadAverage <= config.Limits.MaxLoadAverage?
  │     Passes if LatestMetrics is null (no signal to reject on).
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
  ├── FILTER 8.1: BlockStore for replication
  │     spec.ReplicationFactor > 0 → node has an Active BlockStore obligation?
  │     A VM requesting replication needs a node that participates in the
  │     block-storage layer.
  │     Source: spec.ReplicationFactor + node-side
  │
  ├── FILTER 9: KVM available
  │     node.HardwareInventory.KvmAvailable?
  │     Source: node-side
  │
  └── FILTER 10: Constraints
        spec.Constraints non-empty?
        → for each constraint at index i:
            IConstraintEvaluator.Evaluate(constraint, node)
            if !passed → "Constraint #i failed: {target} ({actual}) {op} {value}"
        Source: spec.Constraints (validated at VM creation)
```

**FILTER 3 (architecture) and FILTER 4 (locality, reputation) do not
exist as bespoke if-blocks.** All such requirements are expressed via
`spec.Constraints` and evaluated in FILTER 10. FILTER numbering is
preserved for historical reference in commit history.

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
capacity is zero.

### Load score

```
max(0, 1.0 - loadAverage / 16.0)
```

Lower current load = better. Avoids stacking new VMs on already-saturated
hosts. Neutral 0.5 when `LatestMetrics` is null.

### Reputation score

```
(uptimePercent × 0.7) + (successRate × 0.3)
```

Where `successRate = successfulVmCompletions / totalVmsHosted`, or `0.5`
for new nodes (neutral — neither rewarded nor penalised before they have a
track record).

Single source of truth: `NodeReputation.Compute(node)` in
`src/Orchestrator/Services/VmScheduling/NodeReputation.cs`. Both the
scoring path and the `node.reputationScore` constraint target use this
method.

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

Constraints are the sole mechanism for expressing VM node-selection
requirements. All entries in `spec.Constraints` are evaluated as a flat
AND in FILTER 10. Validated at VM creation; malformed entries are rejected
before any resource is allocated.

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
| `ConstraintTargets.Node.Tier` | `node.tier` | `Node.PerformanceEvaluation.EligibleTiers` | string[] |
| `ConstraintTargets.Node.BenchmarkScore` | `node.benchmarkScore` | `Node.PerformanceEvaluation.BenchmarkScore` | numeric |
| `ConstraintTargets.Node.UptimePercent` | `node.uptimePercent` | `Node.UptimePercentage` | numeric |
| `ConstraintTargets.Node.ReputationScore` | `node.reputationScore` | `NodeReputation.Compute(node)` | numeric |
| `ConstraintTargets.Node.Tags` | `node.tags` | `Node.Tags` | string[] |

`node.country` and `node.locality.country` resolve to the same field.
Prefer `ConstraintTargets.Node.Locality.Country` in new code.

Use `ConstraintTargets.Node.ReputationScore` when the composite reputation
measure matters; use `ConstraintTargets.Node.UptimePercent` when only
uptime matters.

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

### FILTER 10 evaluation loop

```csharp
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
empty list removes all constraints (VM will migrate to any eligible node).

Only allowed in `Error` state. The endpoint requires ownership or `Admin`
role.

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

### Locality reference (read-only, anonymous)

See [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) for the country/region
model. Reference endpoints:

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/locality/countries` | Full country list with jurisdiction tags |
| `GET` | `/api/locality/regions` | Full region list |
| `GET` | `/api/locality/suggest/{country}` | Suggested region for a country code |

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
2. **FILTER 2 (Tier):** drops nodes not eligible for `Guaranteed`
   (benchmark < 4 000).
3. **FILTER 5 (GPU):** `GpuMode` not set → passes all nodes.
4. **FILTER 6 (Load):** drops nodes above the load ceiling (default 8.0).
5. **FILTER 7 (Memory):** drops nodes below the free-memory floor (default 512 MB).
6. **FILTER 8 (Obligations):** drops nodes with any non-Active system obligation.
7. **FILTER 8.1 (BlockStore):** `ReplicationFactor` not set → passes all nodes.
8. **FILTER 9 (KVM):** drops non-KVM nodes.
9. **FILTER 10 (Constraints):**
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

- **Affinity / anti-affinity between VMs.** "Place this VM in the same
  zone as VM-X" or "never co-locate these two VMs on the same node" are
  valid future concerns but require multi-VM scheduling state the current
  single-VM-per-decision pipeline does not model.

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

---

## References

**Code**

| File | Role |
|---|---|
| `src/Orchestrator/Services/VmScheduling/VmSchedulingService.cs` | Filter chain, scoring, public scheduling API |
| `src/Orchestrator/Services/VmScheduling/ConstraintEvaluator.cs` | Constraint evaluation, target and operator registries |
| `src/Orchestrator/Models/ConstraintVocabulary.cs` | `ConstraintTargets` and `ConstraintOperators` typed constants |
| `src/Orchestrator/Models/Constraint.cs` | `Constraint` and `ConstraintEvaluation` models |
| `src/Orchestrator/Services/VmScheduling/NodeReputation.cs` | Reputation formula (single source of truth) |
| `src/Orchestrator/Services/VmScheduling/SchedulingConfigService.cs` | Versioned config + history |
| `src/Orchestrator/Services/Locality/LocalityService.cs` | Locality data + adjacency graph |
| `src/Orchestrator/Services/NodeCapacityCalculator.cs` | Overcommit math |
| `src/Orchestrator/Services/NodePerformanceEvaluator.cs` | Tier eligibility |
| `src/Orchestrator/Models/SchedulingConfig.cs` | Config schema |
| `src/Orchestrator/Models/VirtualMachine.cs` | `VmSpec` (host of `Constraints`) |
| `src/Orchestrator/Controllers/VmsController.cs` | VM creation + PATCH /scheduling |
| `src/Orchestrator/Background/BackgroundServices.cs` | Migration scheduler |
| `src/Orchestrator/Interfaces/VmScheduling/IVmSchedulingService.cs` | Scheduler contract |

**Related docs**

- [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) — country/region/zone model
- [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — node states the scheduler consumes
