# DeCloud Scheduling

How the orchestrator places tenant VMs onto operator nodes: the data
flow, the filter and scoring chain, the tunable parameters, and the
constraint vocabulary that lets tenants express placement requirements
beyond what flat fields on `VmSpec` can carry.

> **Companion documents:**
> - [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) — the country/region/zone model the scheduler consumes
> - [`NODE-LIFECYCLE.md`](NODE-LIFECYCLE.md) — how nodes get into a state where they're schedulable
> - [`RELEASE-PIPELINE.md`](RELEASE-PIPELINE.md) — how the orchestrator binary that runs this code is produced

---

## Table of Contents

1. [Overview](#overview)
2. [The scheduling pipeline](#the-scheduling-pipeline)
3. [Quality tiers](#quality-tiers)
4. [Hard filters](#hard-filters)
5. [Scoring](#scoring)
6. [Tunable parameters](#tunable-parameters)
7. [Constraints (extension to hard filters)](#constraints-extension-to-hard-filters)
8. [API endpoints](#api-endpoints)
9. [Worked example: EU-only payment processor](#worked-example-eu-only-payment-processor)
10. [What is intentionally NOT here](#what-is-intentionally-not-here)
11. [References](#references)

---

## Overview

When a tenant requests a VM, the orchestrator must pick a single node
from the federated pool to host it. The scheduler does this in two
phases:

1. **Hard filtering** — eliminate every node that *cannot* host this
   VM (architecture mismatch, insufficient capacity, region/jurisdiction
   violation, KVM unavailable, etc.). A failed hard filter is a
   categorical reject; no scoring happens.

2. **Scoring** — among nodes that pass all hard filters, compute a
   weighted score across multiple dimensions (capacity, load,
   reputation, locality) and pick the highest.

Configuration lives in MongoDB (`scheduling_configs` collection,
versioned with history) and is exposed via `ISchedulingConfigService`
with a 5-minute in-memory cache. Operators with admin role update it
through `PATCH /api/scheduling-config`; the change takes effect on the
next cache refresh.

The scheduler is implemented in `VmSchedulingService` and the entry
point is `SelectBestNodeForVmAsync(spec, tier, preferredRegion,
preferredZone, requiredArchitecture)`.

---

## The scheduling pipeline

```
                   ┌─────────────────────────────────────┐
                   │ VmService.CreateVmAsync(spec, tier) │
                   └──────────────┬──────────────────────┘
                                  ▼
                   ┌─────────────────────────────────────┐
                   │ SelectBestNodeForVmAsync(...)       │
                   └──────────────┬──────────────────────┘
                                  ▼
                   ┌─────────────────────────────────────┐
                   │ GetAllNodesAsync(NodeStatus.Online) │
                   └──────────────┬──────────────────────┘
                                  ▼
                ┌────────────────────────────────────────────┐
                │ For each node: ScoreNodeForVmAsync         │
                │   ┌────────────────────────────────────┐   │
                │   │ 1. ApplyHardFiltersAsync           │   │
                │   │    1. Status                       │   │
                │   │    2. Tier eligibility             │   │
                │   │    3. Architecture                 │   │
                │   │    4. Locality (4a–e)              │   │
                │   │       + Reputation threshold (4f)  │   │
                │   │    5. GPU mode                     │   │
                │   │    6. Load average                 │   │
                │   │    7. Free memory                  │   │
                │   │    8. Obligations active           │   │
                │   │       + BlockStore (8.1, replic'd) │   │
                │   │    9. KVM                          │   │
                │   │   10. Constraints (Phase B, future)│   │
                │   │   Reject → score 0, skip rest      │   │
                │   ├────────────────────────────────────┤   │
                │   │ 2. CalculateResourceAvailability   │   │
                │   ├────────────────────────────────────┤   │
                │   │ 3. Verify VM fits (overcommit)     │   │
                │   ├────────────────────────────────────┤   │
                │   │ 4. Check utilization safety        │   │
                │   ├────────────────────────────────────┤   │
                │   │ 5. CalculateNodeScores             │   │
                │   │    - Capacity, Load,               │   │
                │   │      Reputation, Locality          │   │
                │   └────────────────────────────────────┘   │
                └────────────────────────┬───────────────────┘
                                         ▼
                          ┌─────────────────────────────┐
                          │ OrderByDescending(Score)    │
                          │ → return highest            │
                          └─────────────────────────────┘
```

Every step is implemented as a private method on `VmSchedulingService`
with a clear contract: returns either an eligibility decision or a
score component. The chain is linear and short-circuits on the first
hard-filter failure for performance — most rejected nodes pay only
the FILTER 1–4 cost.

---

## Quality tiers

A tier expresses the performance contract a tenant pays for. The
orchestrator's `SchedulingConfig.Tiers` dictionary defines four:

| Tier | Min benchmark | CPU overcommit | Storage overcommit | Price multiplier | Use case |
| --- | --- | --- | --- | --- | --- |
| `Burstable` | 1000 | 4.0 | 2.5 | 0.5× | Dev, test, light workloads |
| `Balanced` | 1500 | 2.7 | 2.0 | 0.7× | Web servers, databases, AI inference |
| `Standard` | 2500 | 1.6 | 1.5 | 1.0× | High-traffic apps, real-time processing |
| `Guaranteed` | 4000 | 1.0 | 1.0 | 1.8× | Mission-critical, financial trading |

A tier maps to:

- A floor for the node's CPU benchmark score (a node below it can't host this tier)
- An overcommit ratio used to compute "how much logical CPU capacity does this node expose for this tier"
- A price multiplier the tenant pays vs. baseline

`NodePerformanceEvaluator` computes which tiers a node is eligible for
at registration time and stores the list on
`Node.PerformanceEvaluation.EligibleTiers`. FILTER 2 in
`ApplyHardFiltersAsync` reads this list directly.

---

## Hard filters

Nine categorical checks, evaluated in order. First failure returns a
human-readable rejection reason; the node is removed from consideration.
The chain short-circuits on the first failure, so most rejected nodes
pay only the early-filter cost (FILTERs 1–4 are the cheapest and most
discriminating).

A tenth filter — Constraints (FILTER 10) — is reserved for the
constraint engine documented in §7. Phase A landed the foundation;
Phase B wires it into this chain.

### FILTER 1: Node status

```
node.Status == NodeStatus.Online?
```

Anything else (`Offline`, `Suspended`, `Maintenance`, etc.) is rejected
with `"Node not online (status: {Status})"`.

### FILTER 2: Tier eligibility

```
node.PerformanceEvaluation.EligibleTiers contains <tier>?### FILTER 4: Locality (multi-part)
```

A `Standard`-tier VM cannot land on a `Burstable`-only node. The set
of eligible tiers is computed once at node registration and refreshed
when scheduling config changes (the `NodePerformanceEvaluator` re-runs
against the new baseline).

### FILTER 3: Architecture

```
node.Architecture matches required architecture?
```

`x86_64` (`amd64`) and `aarch64` (`arm64`) are normalized via
`NormalizeArchitecture`. A VM whose disk image is x86 cannot run on an
ARM node. This is a security/correctness filter — not a performance
hint.

### FILTER 4: Locality and reputation (multi-part)

Six sub-checks, each evaluated only if the corresponding requirement
is set on `VmSpec`. The first five (4a-e) are locality requirements;
4f is a tenant-set quality threshold that lives in the same filter
group because, like locality, it represents a tenant preference rather
than a node capability.

| Sub-filter | Field on VmSpec | Match against | Semantics |
| --- | --- | --- | --- |
| 4a. Jurisdiction tag | `RequiredJurisdictionTag` | `Node.Locality.JurisdictionTags` | Membership |
| 4b. Required country | `RequiredCountry` | `Node.Locality.Country` | Equality |
| 4c. Forbidden countries | `ForbiddenCountries` | `Node.Locality.Country` | Non-membership |
| 4d. Required region | `Region` | `Node.Locality.Region` | Equality |
| 4e. Required zone | `Zone` | `Node.Locality.Zone` | Equality |
| 4f. Minimum reputation | `MinNodeReputationScore` | `ComputeReputationScore(node)` | `>=` |

The reputation formula `(uptimePercent × 0.7) + (successRate × 0.3)`
is shared with the soft scoring path via `ComputeReputationScore`.
The default `MinNodeReputationScore` of 0.3 accepts most healthy nodes;
tenants raise the floor for compliance or latency-sensitive workloads.

See [`LOCALITY_STANDARDS.md`](LOCALITY_STANDARDS.md) for the full
locality model — what each field means, where it comes from, why
country and region are different things.

### FILTER 5: GPU mode requirement

GPU access modes have different node-capability requirements:

| `VmSpec.GpuMode` | Required of node |
| --- | --- |
| `None` | (no filter applied) |
| `Passthrough` | `SupportsGpu`, `GpuCount > 0`, `HasIommuCapableGpu`, `HasPassthroughCapableGpu` |
| `Proxied` | `SupportsGpu`, `GpuCount > 0` (no IOMMU needed) |

Passthrough demands a VFIO-capable IOMMU group; proxied mode just needs
any GPU plus the host-side proxy daemon. Rejection messages identify
which specific GPU capability is missing on the candidate node.

### FILTER 6: Load average
node.LatestMetrics.LoadAverage <= config.Limits.MaxLoadAverage?

Default `MaxLoadAverage` is 8.0. Avoids stacking new VMs on already-
saturated hosts. If `LatestMetrics` is null (node hasn't reported yet),
the filter passes — there's no signal to reject on.

### FILTER 7: Minimum free memory
(TotalResources.MemoryBytes - ReservedResources.MemoryBytes) / 1MiB
>= config.Limits.MinFreeMemoryMb?

Default `MinFreeMemoryMb` is 512 MB — a hard reserve that prevents the
host OS from being squeezed by VM allocations. Memory is never
overcommitted (unlike CPU and storage), so the math is straight
subtraction.

### FILTER 8: All node obligations must be Active
node.SystemVmObligations.All(o => o.Status == SystemVmStatus.Active)?

A node is not fully onboarded until every obligation it was assigned
(DHT, Relay, BlockStore, Ingress) has reached `Active`. Pending,
Deploying, or Failed obligations mean the node's infrastructure isn't
ready to host tenant workloads. Nodes with no obligations (hardware
below the DHT/Relay/BlockStore thresholds) have an empty list and
pass this filter trivially.

This filter supersedes the previous standalone BlockStore check —
`SystemVmReconciliationService` sets `BlockStoreInfo.Status = Active`
atomically when the BlockStore obligation flips to `Active`, so
"all obligations Active" implies "BlockStore Active too."

### FILTER 8.1: Active BlockStore required for replicated VMs
spec.ReplicationFactor > 0  =>  node.BlockStoreInfo.Status == Active?

Sub-filter that applies only when a VM has `ReplicationFactor > 0`.
Without an active BlockStore on the host node, the lazysync daemon
has nowhere to push dirty overlay blocks — replication cannot
function. Ephemeral VMs (`ReplicationFactor == 0`) bypass this check
and accept data loss on node failure.

This is partly redundant with FILTER 8 today, but documents the
specific replication invariant separately because it's a categorically
different reason for rejection (data-safety, not operational-readiness).

### FILTER 9: KVM availability
node.HardwareInventory.KvmAvailable?

User VMs require hardware virtualization. QEMU TCG software emulation
is a non-starter — performance is unsuitable for any real workload.
Non-KVM nodes can still hold operator-side roles (DHT/BlockStore/Relay)
but never tenant workloads.

### FILTER 10 (planned): Constraints

When a VmSpec carries a `Constraints` list, each constraint is
evaluated against the candidate node. A failed constraint short-circuits
with a structured rejection. This is documented separately in
[§7 Constraints](#constraints-extension-to-hard-filters) below.

Phase A (foundation: types, vocabulary, evaluator, DI registration) has
landed in code. Phase B (wire-up in `ApplyHardFiltersAsync`, `VmSpec`
field, lowering of existing flat fields) lands when a concrete tenant
requirement materializes.

---

## Scoring

After hard filters pass, four components contribute to the node's
total score. Each returns a value in `[0.0, 1.0]`. Weights sum to 1.0
(enforced by `ScoringWeightsConfig.IsValid`).

### Capacity score

```
remainingComputePoints / totalComputePoints
```

Higher remaining capacity = better. Encourages spreading load and
keeping nodes from saturating.

### Load score

```
max(0, 1.0 - loadAverage / 16.0)
```

Lower current load = better. Falls back to neutral 0.5 if metrics
unavailable.

### Reputation score

```
(uptimePercent × 0.7) + (successRate × 0.3)
```

Where `successRate = successfulVmCompletions / totalVmsHosted` (or 0.5
for new nodes). Rewards reliable history.

### Locality score

```
1.0 — same zone (implies same region)
0.8 — same region, any zone
0.5 — adjacent region per region-adjacency.json
0.3 — same continent
0.0 — no relationship
0.5 — no preference specified (neutral)
```

Implementation in `CalculateLocalityScore`. The adjacency graph is the
hand-curated `region-adjacency.json` — see `LOCALITY_STANDARDS.md` for
the topology rationale.

### Default weights

```
Capacity:    0.40
Load:        0.25
Reputation:  0.20
Locality:    0.15
```

Operators can change these via `PATCH /api/scheduling-config`. A
config change increments `SchedulingConfig.Version` and is recorded
to history collection for audit. Nodes pick up the new weights on
next scheduling decision (cache TTL: 5 minutes).

---

## Tunable parameters

Everything in the table below lives in `SchedulingConfig` (MongoDB,
versioned, audit-logged). Defaults are conservative; production
clusters may need to adjust based on workload mix.

| Parameter | Default | Type | Effect |
| --- | --- | --- | --- |
| `BaselineBenchmark` | 1000 | int | Reference benchmark score for "1 point per core" |
| `BaselineOvercommitRatio` | 4.0 | double | Used in `NodeCapacityCalculator` for max-tier capacity |
| `MaxPerformanceMultiplier` | 20.0 | double | Caps benchmark advantage of fast hardware |
| `Tiers[t].MinimumBenchmark` | varies | int | Floor for tier eligibility |
| `Tiers[t].CpuOvercommitRatio` | varies | double | Logical CPU capacity multiplier per tier |
| `Tiers[t].StorageOvercommitRatio` | varies | double | qcow2 thin-provisioning multiplier |
| `Tiers[t].PriceMultiplier` | varies | decimal | Tenant pays this × baseline rate |
| `Limits.MaxUtilizationPercent` | 90.0 | double | Reject if placement would exceed this |
| `Limits.MinFreeMemoryMb` | 512 | long | Reserve always-free RAM |
| `Limits.MaxLoadAverage` | 8.0 | double | Avoid nodes above this load |
| `Limits.PreferLocalRegion` | true | bool | Soft preference for region match |
| `Weights.Capacity` | 0.40 | double | Score weight |
| `Weights.Load` | 0.25 | double | Score weight |
| `Weights.Reputation` | 0.20 | double | Score weight |
| `Weights.Locality` | 0.15 | double | Score weight |

`Weights` must sum to 1.0; validation rejects any update that violates
this. Tier configurations must include `Burstable` (used as baseline);
validation rejects updates that omit it.

---

## Constraints (extension to hard filters)

> **Status:** designed, not yet implemented. This section is the
> contract for what gets built.

### Why

Today's hard filters are nine categorical checks (see §4); four of them
respond to fields tenants set directly on `VmSpec`. Each new tenant
requirement gets a new flat field on `VmSpec` and a new `if` block in
`ApplyHardFiltersAsync`. The result already shows strain:
`RequiredJurisdictionTag`, `RequiredCountry`, `ForbiddenCountries`,
`Region`, `Zone`, `RequiredArchitecture`, `MinNodeReputationScore` —
seven scheduling fields and counting, each with bespoke handling.

A constraint is a uniform shape that replaces field-sprawl with
vocabulary:

```json
{
  "target":   "node.locality.country",
  "operator": "in",
  "value":    ["DE", "FR", "NL"]
}
```

A `VmSpec.Constraints` list is a flat AND of these. Constraints
evaluate as a sixth hard filter, after the existing five. New tenant
requirements add vocabulary entries (a new operator or target),
not C# fields.

### Scope (v1)

Explicit, narrow:

- **VM-side only.** A VmSpec carries constraints; the orchestrator
  evaluates them against each candidate node. Node-side constraints
  ("operator policies") are a separate future feature and not included
  here.
- **Hard filters only.** Constraints are categorical AND checks. They
  do not feed into scoring — soft preferences continue to use the
  existing weighted scorer.
- **Flat AND composition.** A constraint set is a list. All must pass.
  No OR, no nesting, no NOT-groups. Negation expressible per-constraint
  via operators (`not_in`, `not_contains`, etc.).
- **Static fields only.** Constraints target fields persisted on
  `Node` and `Node.Locality`. Derived/computed values (current
  utilization, runtime reputation deltas, computed compute-points)
  are not addressable as constraint targets in v1 — they belong to
  scoring or to dedicated checks.
- **Fixed vocabulary.** Targets and operators come from a
  build-time-defined enum. Operators cannot define new ones at runtime.

### Constraint shape

```csharp
public class Constraint
{
    public required string Target   { get; set; }   // e.g. "node.locality.country"
    public required string Operator { get; set; }   // e.g. "in"
    public required object Value    { get; set; }   // type depends on target+operator
}
```

JSON form on the wire:

```json
{ "target": "node.locality.jurisdictionTags",
  "operator": "contains_all",
  "value": ["EU", "Schengen"] }
```

`VmSpec` gains a single new field:

```csharp
public List<Constraint>? Constraints { get; set; }
```

Null or empty = no constraints (existing behavior). Non-empty = each
constraint must evaluate to true against the candidate node.

### Target vocabulary (v1)

Whitelisted, build-time enum. Each entry maps to a property on `Node`
or `Node.Locality`, with declared type:

| Target | Source | Type |
| --- | --- | --- |
| `node.country` | `Node.Locality.Country` | string |
| `node.locality.country` | `Node.Locality.Country` | string (alias) |
| `node.locality.region` | `Node.Locality.Region` | string |
| `node.locality.zone` | `Node.Locality.Zone` | string |
| `node.locality.jurisdictionTags` | `Node.Locality.JurisdictionTags` | string[] |
| `node.locality.locationMismatch` | `Node.Locality.LocationMismatch` | bool |
| `node.architecture` | `Node.Architecture` | string |
| `node.tier` | `Node.PerformanceEvaluation.EligibleTiers` | string[] (tier names) |
| `node.tags` | `Node.Tags` | string[] |
| `node.uptimePercent` | `Node.UptimePercentage` | double |
| `node.benchmarkScore` | `Node.PerformanceEvaluation.BenchmarkScore` | int |
| `node.gpuModel` | `Node.HardwareInventory.Gpus[0].Model` (first GPU; null if no GPUs) | string |
| `node.kvmAvailable` | `Node.HardwareInventory.KvmAvailable` | bool |

New targets are added by editing `ConstraintTarget` enum + extractor
table. Adding a target is a deliberate code change, not a runtime
configuration. This is a feature, not a bug — it bounds the surface
area and makes every supported target documentable.

### Operator vocabulary (v1)

| Operator | Value type vs target type | Semantics |
| --- | --- | --- |
| `eq` | scalar = scalar | Equality (case-insensitive for strings) |
| `neq` | scalar ≠ scalar | Negation of `eq` |
| `in` | scalar ∈ list | Member of allowed list |
| `not_in` | scalar ∉ list | Not in disallowed list |
| `contains` | list ⊇ {scalar} | Target list contains the value |
| `not_contains` | list ⊉ {scalar} | Target list does not contain the value |
| `contains_all` | list ⊇ list | Target list is a superset of value list |
| `contains_any` | list ∩ list ≠ ∅ | Any overlap |
| `contains_none` | list ∩ list = ∅ | No overlap |
| `gte` | numeric ≥ numeric | Greater-or-equal |
| `lte` | numeric ≤ numeric | Less-or-equal |
| `gt` | numeric > numeric | Strictly greater |
| `lt` | numeric < numeric | Strictly less |
| **Domain operators** | | |
| `adjacent_to` | region ↔ region | Per `region-adjacency.json` |
| `same_continent_as` | region ↔ region | Continent equality |
| `has_jurisdiction_tag` | country → tag | Country's jurisdictionTags contains value |

Each `(target_type, operator, value_type)` triple has explicit
type-compatibility rules enforced at validation time. A constraint
with `node.country` (string) and `gte` (numeric op) is rejected
before scheduling — the scheduler never sees malformed constraints.

### Validation

Constraints are validated at VM creation, not at scheduling time:

- Unknown `target` → reject with `"unknown constraint target '{target}'"`
- Unknown `operator` → reject with `"unknown operator '{operator}'"`
- Type mismatch → reject with `"operator '{op}' is not valid for target '{tgt}' (expected {type1}, got {type2})"`
- Malformed `value` (e.g., `in` with scalar instead of list) → reject

Errors include the constraint's index in the list so tenants can
locate the offending entry. The scheduler trusts that anything in
`spec.Constraints` is well-formed; runtime failures here would
indicate a bug in validation, not a tenant input issue.

### Evaluation

Implementation lives in `IConstraintEvaluator`, called from
`ApplyHardFiltersAsync` after FILTER 5 (KVM):

```csharp
if (spec.Constraints is { Count: > 0 })
{
    foreach (var (constraint, index) in spec.Constraints.Select((c, i) => (c, i)))
    {
        var result = _constraintEvaluator.Evaluate(constraint, node);
        if (!result.Passed)
            return $"Constraint #{index} failed: {result.RejectionReason}";
    }
}
```

The evaluator does:
1. Look up the target's extractor function from the vocabulary table
2. Extract the actual value from `node`
3. Look up the operator's evaluator function
4. Compute the boolean result with a structured rejection reason on failure

Rejection reasons are designed for operator + tenant readability:

```
"Constraint #2 failed: node.locality.country (BR) not_in [DE, FR, NL]"
"Constraint #0 failed: node.locality.jurisdictionTags ([NATO, EU-CustomsUnion])
                       does not contain_all [EU, Schengen]"
```

### Migration of existing fields

The flat fields on `VmSpec` (`RequiredJurisdictionTag`,
`RequiredCountry`, `ForbiddenCountries`, etc.) stay. Server-side, at
VM creation, they lower into constraints:

```
RequiredJurisdictionTag: "EU"
  ↓ lowers to
{ target: "node.locality.jurisdictionTags",
  operator: "contains",
  value: "EU" }

ForbiddenCountries: ["RU", "BY"]
  ↓ lowers to
{ target: "node.locality.country",
  operator: "not_in",
  value: ["RU", "BY"] }
```

A tenant request can use the flat fields, the structured `Constraints`
list, or both — both forms produce the same evaluation result. New
clients prefer `Constraints`; the flat fields stay as wire-format
sugar for the common cases. No deprecation timeline; both forms work
indefinitely.

### Logging and debuggability

Every scheduling decision logs structured information per node:

```
Node {NodeId} ({Architecture}, {Region}/{Zone}) — Score: 0.00,
Capacity: 0.00, Load: 0.00, Reputation: 0.00, Locality: 0.00,
Rejection: "Constraint #1 failed: node.locality.country (BR) not_in [DE, FR, NL]"
```

The existing log format already prints `RejectionReason`. Constraint
failures slot in here without adding new infrastructure.

A future `decloud vm explain <vm-id>` command (deferred) would surface
the per-node, per-constraint matrix to tenants debugging "why didn't
my VM place" — but the underlying log entries are already sufficient
to answer this question via orchestrator logs.

### Extension points (deferred, documented for future work)

The v1 design has explicit hooks for things we know will come:

- **Composite logic (AND/OR/NOT trees).** Today's `Constraints` is
  `List<Constraint>` evaluating as flat AND. A future `ConstraintTree`
  field can carry nested expressions; when both fields are present,
  the flat list lowers to `{ op: and, rules: [...] }` and merges with
  the tree. No breaking change to existing wire format.
- **Derived/runtime values.** When a use case demands `computePoints
  >= 100` as a hard filter, a `NodeScoringContext` pre-computation
  step is introduced. Both constraint evaluation and scoring read
  from it. Static-field constraints are unaffected.
- **Node-side constraints.** A `Node.Constraints` field would let
  operators declare placement policies ("only Guaranteed tier",
  "no GPU workloads"). The evaluator already accepts
  `(constraint, node, vmSpec)` so the inverse direction is one
  call site change plus the new field.

These are *plug-in points*, not v1 work. Each has a clear shape and
a known place to add it; none are blocked by v1 decisions.

---

## API endpoints

All scheduling-config endpoints live under `/api/admin/` and require
the `Admin` role (enforced by `[Authorize(Roles = "Admin")]` at the
controller level). Anonymous access is impossible by construction.

### `GET /api/admin/scheduling-config`

Returns the current `SchedulingConfig`.

### `PUT /api/admin/scheduling-config`

Updates the configuration. Validates before persisting; archives the
previous version with `_id = history_{version}_{timestamp}`. Increments
`Version`. Cache invalidated immediately. Validation failures return
400 with the specific rule that failed.

### `POST /api/admin/scheduling-config/reload`

Forces cache flush. Useful after manual MongoDB edits (rare; not
recommended).

### `GET /api/admin/scheduling-config/history?limit=10`

Returns the N most recent archived configurations, sorted by
`UpdatedAt` descending. `limit` is bounded to `[1, 100]`.

### `POST /api/admin/scheduling-config/validate`

Validates a candidate configuration without persisting it. Useful for
testing changes before applying them. Returns 200 with `{IsValid: true}`
on success, 400 with `{IsValid: false, Errors: [...]}` on failure.

### `GET /api/locality/{countries,regions,suggest/{country}}`

Read-only locality reference data. See `LOCALITY_STANDARDS.md` for
contract.

### Constraints — no new endpoints

Constraints flow through the existing `POST /api/vms` (VM creation)
and `GET /api/vms/{id}/scheduling-explanation` (planned) endpoints.
No constraint-management API; constraints are properties of the VM
spec, not first-class resources.

---

## Worked example: EU-only payment processor

A hypothetical fintech tenant wants to deploy a payment-processing VM
with these requirements:

1. Must run in an EU member state (GDPR, PSD2)
2. Must NOT run in countries on their internal exclusion list (RU, BY)
3. Must have a Guaranteed-tier node (no overcommit, predictable latency)
4. Operator must have ≥99% uptime over the rolling window
5. Must NOT be a node flagged with location mismatch (declared locality
   differs from IP-derived) — they want strict jurisdictional certainty
6. Prefers `eu-central` region for proximity to their existing infra
7. If `eu-central` capacity exhausted, accepts `eu-west` or `eu-north`
   over going to `eu-east`

### How they'd express it

Mixing flat fields (existing) and constraints (new):

```json
{
  "name": "payment-processor-prod-1",
  "spec": {
    "vCpus": 4,
    "memoryMB": 8192,
    "diskGB": 100,
    "qualityTier": "Guaranteed",

    "region":           "eu-central",

    "constraints": [
      { "target": "node.locality.jurisdictionTags",
        "operator": "contains",
        "value": "EU" },

      { "target": "node.locality.country",
        "operator": "not_in",
        "value": ["RU", "BY"] },

      { "target": "node.uptimePercent",
        "operator": "gte",
        "value": 99.0 },

      { "target": "node.locality.locationMismatch",
        "operator": "eq",
        "value": false }
    ]
  }
}
```

### What the scheduler does
### What the scheduler does

1. **FILTER 1 (Status):** drops offline nodes
2. **FILTER 2 (Tier):** drops nodes not eligible for `Guaranteed`
3. **FILTER 3 (Architecture):** no requirement specified — passes all
4. **FILTER 4 (Locality + reputation):**
   - 4d (Region equality on `eu-central`): drops every node not in
     `eu-central`
   - 4f (`MinNodeReputationScore`): default 0.3 floor passes most
     nodes (tenant could raise this for stricter gating)
5. **FILTER 5 (GPU mode):** `GpuMode == None` — no GPU requirement
6. **FILTER 6 (Load):** drops nodes whose 1-min load exceeds the
   configured ceiling (default 8.0)
7. **FILTER 7 (Memory):** drops nodes whose free RAM is below the
   floor (default 512 MB)
8. **FILTER 8 (Obligations):** drops nodes with any non-Active
   system-VM obligation
9. **FILTER 9 (KVM):** drops non-KVM nodes (irrelevant here —
   Guaranteed tier already requires KVM)
10. **FILTER 10 (Constraints, Phase B):**
    - C0: `jurisdictionTags contains EU` — drops any non-EU node that
      somehow passed (shouldn't happen if region was `eu-central`, but
      belt-and-suspenders)
    - C1: `country not_in [RU, BY]` — irrelevant in `eu-central` but
      evaluated anyway
    - C2: `uptimePercent gte 99.0` — drops unreliable operators
    - C3: `locationMismatch eq false` — drops VPN/leased-foreign nodes

11. **Scoring (against survivors):** `Capacity 0.40 + Load 0.25 +
    Reputation 0.20 + Locality 0.15`. Locality scores `1.0` for
    any `eu-central` node (region match). The scheduler picks the
    highest total.

### What if `eu-central` is exhausted?

Current behavior: hard region filter drops all non-`eu-central`
candidates. If none survive, scheduling fails with "no eligible
nodes."

For the "fall back to eu-west / eu-north" preference to work, the
tenant has two choices:

- **Don't set `region`.** Let scoring's locality score handle the
  preference: `0.8` for region match, `0.5` for adjacent regions.
  The scheduler will prefer `eu-central` when available, fall back to
  `eu-west` (adjacent) over `eu-east` (also adjacent — see the graph),
  and so on. But the *constraints* still ensure jurisdictional
  correctness regardless of which region wins.

- **Wait for composite logic (v2).** A future `ConstraintTree` could
  express `region in [eu-central, eu-west, eu-north]` as a hard filter
  with explicit fallback ordering. Not in v1.

For most tenants, the first option (preference via scoring) is the
right answer — it gives flexibility while still preferring the named
region.

### Why this case justifies constraints

Without constraints, expressing "uptime ≥ 99%" would require a new
flat field `MinUptimePercent` on `VmSpec`, plus its handling in
`ApplyHardFiltersAsync`. Same for `locationMismatch eq false`. Two
new fields for one tenant. With constraints, the existing vocabulary
(target `node.uptimePercent`, operator `gte`, value `99.0`) handles
both new requirements without touching `VmSpec` or the filter chain.

The next tenant might want `gpuModel not_in [consumer-grade-list]`
or `tags contains_any [pci-dss-certified]`. Same engine, no new
fields.

---

## What is intentionally NOT here

- **Live migration.** Scheduling decides where a VM *first lands*.
  Moving a running VM to a new node is a separate concern,
  implemented in `VmService.MigrateVmAsync`, with its own constraints
  (drain mode, target eligibility, stop-start vs. live).
- **Cost-optimal placement.** The scheduler picks based on hard
  filters + the four scoring dimensions. Tenant cost optimization
  (cheapest node that meets requirements) is not a scheduling-time
  concern; tenants control cost by choosing tier and constraint set.
- **Affinity / anti-affinity between VMs.** "Place this VM in the
  same zone as VM-X" or "never co-locate these two VMs on the same
  node" — these are valid future scheduling concerns but not in
  the constraint engine v1. They require multi-VM scheduling state,
  which the current single-VM-per-decision pipeline doesn't model.
- **Operator-defined tiers.** Tier configuration is system-wide.
  A node operator cannot define their own tier with bespoke
  overcommit ratios. If this becomes necessary, it's a different
  feature.
- **Per-tenant scheduling overrides.** Some platforms allow trusted
  tenants to bypass certain filters (e.g., unverified node OK for
  internal testing). DeCloud doesn't, and this isn't planned.

---

## References

- **Code:**
  - `src/Orchestrator/Services/VmScheduling/VmSchedulingService.cs` — pipeline + filters + scoring
  - `src/Orchestrator/Services/VmScheduling/SchedulingConfigService.cs` — versioned config + history
  - `src/Orchestrator/Services/Locality/LocalityService.cs` — locality data + adjacency
  - `src/Orchestrator/Services/NodeCapacityCalculator.cs` — overcommit math
  - `src/Orchestrator/Services/NodePerformanceEvaluator.cs` — tier eligibility
  - `src/Orchestrator/Models/SchedulingConfig.cs` — config schema
  - `src/Orchestrator/Models/VirtualMachine.cs` — VmSpec (host of `Constraints`)
- **Related docs:**
  - `LOCALITY_STANDARDS.md` — country/region/zone model
  - `NODE-LIFECYCLE.md` — node states the scheduler consumes
- **External standards:**
  - ISO 3166-1 alpha-2 — country codes (used in country target)
  - JSONLogic — design inspiration for constraint shape (we don't use the library; we built our own narrower vocabulary)
