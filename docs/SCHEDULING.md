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

A small set of **platform-imposed filters** apply regardless: node status,
quality-tier eligibility, GPU capability matching for `spec.GpuMode`, a
load and free-memory safety floor, system obligations, the BlockStore
requirement for replicated VMs, and KVM availability. These are detailed
in §3 and exist for platform safety and capability coherence — tenants
cannot disable them, and they cannot be expressed as constraints because
they encode platform invariants, not tenant preferences.

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
  ├── FILTER 5: GPU mode
  │     spec.GpuMode == Passthrough → node has IOMMU-capable GPU for VFIO?
  │     spec.GpuMode == Proxied     → node has any GPU?
  │     spec.GpuMode == None        → (no check)
  │     Source: spec.GpuMode + node.HardwareInventory
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

The filters fall into two categories:

- **Platform-imposed** (1, 2, 5, 6, 7, 8, 8.1, 9). Tenants cannot disable
  these. They encode platform safety thresholds, capability matching for
  spec-declared workload requirements (GPU mode, replication factor), and
  operational readiness (status, obligations, KVM). The §1 statement
  "constraints are the sole mechanism for tenant-expressible requirements"
  is true; platform invariants are not tenant requirements.

- **Tenant-expressible** (10). Every other selection predicate goes here.
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
evaluated as a flat AND in FILTER 10. Validated at VM creation; malformed
entries are rejected before any resource is allocated.

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

`node.country` and `node.locality.country` resolve to the same field.
Prefer `ConstraintTargets.Node.Locality.Country` in new code.

Use `ConstraintTargets.Node.ReputationScore` when the composite reputation
measure matters; use `ConstraintTargets.Node.UptimePercent` when only
uptime matters.

The hardware-capability targets (`HasGpu`, `HasNvme`, `HighBandwidth`,
`CpuCores`, `GpuVramBytes`) cover the common preset library cases in §9.
They are static node attributes — they do not change between heartbeats,
so the v1 "static fields only" principle (see §11) holds.

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

### JSON serialization notes

- `QualityTier` and `BandwidthTier` enums are serialized as strings via
  `JsonStringEnumConverter` (registered globally). Wire form:
  `"qualityTier": "Guaranteed"`, not `"qualityTier": 1`.
- `Constraint.Value` arrives as a `JsonElement` from JSON wire input or
  `BsonValue` from MongoDB. The evaluator's `NormalizeValue` layer
  unboxes both into native C# types before operator evaluation.

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

Current response is bare-minimum:

```json
{
  "targets":   ["node.architecture", "node.country", ...],
  "operators": ["adjacent_to", "contains", ...]
}
```

Used by the dashboard constraint builder (§9) to populate dropdowns
without hardcoding the vocabulary. A richer schema (target types, value
domains, descriptions, units) is planned — see §11.

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

### Planned endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/vms/scheduling/preview` | Eligibility preview — takes a `Constraint[]`, returns count of matching nodes + top rejection reasons. Powers the constraint builder's live match counter. |
| `GET` | `/api/marketplace/constraint-presets` | Preset library — returns user-facing presets that compile to ordinary constraints. Powers the "Common requirements" section of the builder. |

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
  curated, served from a config file, and validated at orchestrator
  startup against the live vocabulary (presets referencing unknown
  targets fail-fast at boot).

- **Custom builder** — three-column rows (target dropdown, operator
  dropdown filtered by target type, value input rendered based on
  `(target, operator)`). Power users compose anything the vocabulary
  supports. Same data path as presets — both write to the underlying
  `Constraint[]`.

Locked rows from `template.MinimumSpec.Constraints` appear in the custom
section as greyed-out, non-removable entries with a "Required by template"
tooltip.

### Live eligibility preview

While the user edits constraints, the builder debounces and calls
`POST /api/vms/scheduling/preview` (planned — see §11) with the working
constraint set. Response includes the count of eligible nodes and the
top rejection reasons among ineligible ones:

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

Presets are *data*: a JSON file (planned: `config/constraint-presets.json`)
loaded at orchestrator startup. Adding a preset is editing a config file.
Curation (the user-friendly label and category) is a separate concern
from vocabulary (the machine-readable target/operator/value); adding a
new target does not auto-create a preset.

Implementation lives in `src/Orchestrator/wwwroot/src/constraint-builder.js`
(planned).

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

- **Affinity / anti-affinity between VMs.** "Place this VM in the same
  zone as VM-X" or "never co-locate these two VMs on the same node" are
  valid future concerns but require multi-VM scheduling state the current
  single-VM-per-decision pipeline does not model. `VmSpec.SchedulingTags`
  is reserved for this purpose but not consumed by the scheduler today.

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

- **Vocabulary introspection beyond names.** The current
  `/api/vms/constraint-vocabulary` endpoint returns target and operator
  names only. A richer schema (target value types, closed-domain
  enumerations, descriptions, units, default operator suggestions) is
  needed to give the constraint builder typed value inputs without
  hardcoding metadata client-side. Planned; the data needed to populate
  it already lives in `TargetDescriptor` and `OperatorDescriptor`.

- **Eligibility preview at edit time.** The planned
  `POST /api/vms/scheduling/preview` endpoint accepts a `Constraint[]`
  and returns the live count of matching nodes plus top rejection
  reasons. It wraps `IVmSchedulingService.GetScoredNodesForVmAsync` —
  the underlying capability already exists; what's missing is the public
  endpoint shape. Users currently discover infeasible constraint sets
  only at deploy time.

- **Constraint preset library.** The data file
  (`config/constraint-presets.json`) and the
  `/api/marketplace/constraint-presets` endpoint that serves it are
  planned but not yet built. Presets reference only registered
  vocabulary targets; the constraint builder treats them as
  pre-composed shortcuts that materialize as ordinary
  `Constraint[]` entries.

- **Resource-threshold constraints (live counters).** The vocabulary
  exposes static node attributes — physical CPU cores, total GPU VRAM,
  declared bandwidth class. It does *not* expose
  `availableComputePoints` or `freeMemoryBytes` as constraint targets,
  even though these fields exist on `Node`. The v1 "static fields only"
  principle (Constraint.cs) prevents tenants from writing
  "give me a node with ≥ 16 free CPU cores right now" — such
  constraints would pass validation but might fail at scheduling time
  because the value drifts between validation and evaluation. Resource
  thresholds are enforced by the platform filters (FILTER 7 for memory
  and the implicit capacity check) rather than by tenant constraints.

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
| `src/Orchestrator/Services/TemplateService.cs` | `BuildVmRequestFromTemplateAsync` — Path D merge policy |
| `src/Orchestrator/Controllers/MarketplaceController.cs` | `POST /api/marketplace/templates/{id}/deploy` — Path D entry |
| `src/Orchestrator/Models/SchedulingConfig.cs` | Config schema |
| `src/Orchestrator/Models/VirtualMachine.cs` | `VmSpec` (host of `Constraints`) |
| `src/Orchestrator/Controllers/VmsController.cs` | VM creation, PATCH /scheduling, GET /constraint-vocabulary |
| `src/Orchestrator/Background/BackgroundServices.cs` | Migration scheduler |
| `src/Orchestrator/Interfaces/VmScheduling/IVmSchedulingService.cs` | Scheduler contract |
| `src/Orchestrator/wwwroot/src/constraint-builder.js` | (planned) Global constraint builder UI module |
| `config/constraint-presets.json` | (planned) Preset library data |

**Related docs**

- [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) — country/region/zone model
- [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — node states the scheduler consumes
