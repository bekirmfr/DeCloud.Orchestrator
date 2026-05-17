# Node Resource Management & Allocation

**Last Updated:** 2026-05-17
**Status:** Design — not yet implemented
**Purpose:** System design for operator-controlled resource allocation limits across CPU, memory, storage, and GPU.

---

## Table of Contents

1. [Problem Statement](#1-problem-statement)
2. [Design Goals](#2-design-goals)
3. [Resource Model](#3-resource-model)
   - [Continuous Resources (CPU, Memory, Storage)](#continuous-resources-cpu-memory-storage)
   - [Discrete Resources (GPU)](#discrete-resources-gpu)
   - [Platform Default](#platform-default)
4. [Operator Interface](#4-operator-interface)
   - [CLI Flags](#cli-flags)
   - [Settings File Schema](#settings-file-schema)
   - [Validation Rules](#validation-rules)
5. [System Flow](#5-system-flow)
   - [Configuration](#configuration)
   - [Registration](#registration)
   - [Capacity Calculation](#capacity-calculation)
   - [Heartbeat & Monitoring](#heartbeat--monitoring)
6. [What This Replaces](#6-what-this-replaces)
7. [Interaction with Existing Systems](#7-interaction-with-existing-systems)
   - [Settings Lifecycle](#settings-lifecycle)
   - [Settings Hash](#settings-hash)
   - [Quality Tiers & Overcommit](#quality-tiers--overcommit)
   - [System VM Obligations](#system-vm-obligations)
   - [Scheduling](#scheduling)
   - [Marketplace Display](#marketplace-display)
8. [Reservation Lifecycle](#8-reservation-lifecycle)
   - [When Resources Are Reserved](#when-resources-are-reserved)
   - [When Resources Are Freed](#when-resources-are-freed)
   - [Reservation Invariants](#reservation-invariants)
   - [Re-registration with Existing Reservations](#re-registration-with-existing-reservations)
   - [Self-Healing & Drift Detection](#self-healing--drift-detection)
9. [Detailed Resource Semantics](#9-detailed-resource-semantics)
   - [CPU (Compute Points)](#cpu-compute-points)
   - [Memory](#memory)
   - [Storage](#storage)
   - [GPU](#gpu)
10. [Edge Cases & Safety](#10-edge-cases--safety)
11. [Implementation Plan](#11-implementation-plan)
    - [Touch Points](#touch-points)
    - [Phasing](#phasing)
12. [Example Walkthrough](#12-example-walkthrough)

---

## 1. Problem Statement

The current system computes a node's allocatable capacity from a
volatile point-in-time snapshot taken at registration. For memory,
`AllocatableBytes = TotalBytes − ReservedBytes − UsedBytes`, where
`UsedBytes` is the host's memory consumption at the moment of
registration. Two identical machines registering at different moments
get different capacity ceilings. A node that registers while compiling
code gets a permanently lower ceiling than one registering at idle.

More broadly, operators have no way to say "I want to offer 70% of
this machine to DeCloud and keep 30% for my own workloads." The
platform assumes it owns everything above a hardcoded 2 GB OS
reservation. Operators running mixed workloads must choose between
over-provisioning (risk of host instability) and under-provisioning
(re-registering at just the right moment).

The fix is not to patch the volatile formula. The fix is to give the
operator an explicit, stable knob for each resource class, with a
sensible platform default when no knob is set.

---

## 2. Design Goals

1. **Operator sovereignty** — the operator decides how much of their
   hardware to offer. The platform manages everything inside that
   boundary (system VMs, tenant VMs, scheduling).

2. **Stable capacity ceiling** — a node's total offered capacity must
   not change unless the operator explicitly reconfigures or
   re-registers. No volatile runtime measurements in the capacity
   calculation path.

3. **Consistent abstraction** — the same percent/absolute pattern for
   all continuous resources (CPU, memory, storage). A separate
   count-based model for discrete resources (GPU). One mental model
   for operators to learn.

4. **Safe defaults** — a node that never configures allocation limits
   works correctly with a platform-wide default (90%). No operator
   action required for the common case.

5. **No hidden interactions** — percent and absolute modes are mutually
   exclusive per resource. One field, one interpretation. No
   precedence rules to debug.

---

## 3. Resource Model

### Continuous Resources (CPU, Memory, Storage)

Each continuous resource supports two modes: **percent** of the
physical total, or an **absolute** value in the resource's natural
unit. They are mutually exclusive — setting one clears the other.

| Resource | Percent base | Absolute unit | Resolved to |
| --- | --- | --- | --- |
| CPU | TotalComputePoints (derived from benchmark × cores × overcommit) | Compute points (int) | `int` points |
| Memory | TotalBytes (physical RAM) | Megabytes | `long` bytes |
| Storage | TotalStorageBytes (physical, pre-overcommit) | Megabytes | `long` bytes |

Resolution always happens on the **node agent** before registration.
The orchestrator receives a single resolved absolute value per
resource and never needs to interpret percent vs absolute itself.

### Discrete Resources (GPU)

GPUs are whole physical devices. Percent doesn't map cleanly ("90% of
1 GPU" is 0.9 — meaningless). GPU allocation uses **count only**:

| Resource | Mode | Unit | Default |
| --- | --- | --- | --- |
| GPU | count | Discrete devices | All detected (null = all) |

### Platform Default

When the operator has not configured an allocation limit for a
resource, the platform applies a default:

| Resource | Default | Rationale |
| --- | --- | --- |
| CPU | 90% of TotalComputePoints | Reserves ~10% of CPU capacity for host processes, system overhead |
| Memory | 90% of TotalBytes | Reserves ~10% for OS page cache, host daemons, kernel buffers |
| Storage | 90% of physical storage | Reserves ~10% for OS logs, temp files, filesystem headroom |
| GPU | All detected (100%) | Discrete resource — partial GPUs don't exist. 0 GPUs offered is a valid explicit choice |

The 90% default replaces the current hardcoded 2 GB `ReservedBytes`
for memory and the volatile `UsedBytes` subtraction. On a 10 GB
machine, 90% = 9 GB offered, leaving 1 GB for the host. On a 64 GB
machine, 90% = 57.6 GB, leaving 6.4 GB. The percentage scales with
hardware; a fixed byte reservation does not.

---

## 4. Operator Interface

### CLI Flags

```bash
# ── Percent mode (survives platform config changes) ──────────

sudo decloud configure --allocated-cpu-percent 80
sudo decloud configure --allocated-memory-percent 70
sudo decloud configure --allocated-storage-percent 85

# ── Absolute mode (precise operator control) ─────────────────

sudo decloud configure --allocated-cpu-points 8
sudo decloud configure --allocated-memory-mb 4096
sudo decloud configure --allocated-storage-mb 102400

# ── GPU (count only, no percent mode) ────────────────────────

sudo decloud configure --allocated-gpu-count 2

# ── Combined (each resource independently) ───────────────────

sudo decloud configure \
    --allocated-cpu-percent 80 \
    --allocated-memory-mb 4096 \
    --allocated-gpu-count 2

# ── Reset to platform default ────────────────────────────────

sudo decloud configure --allocated-memory-percent default
```

Setting `--allocated-cpu-percent 80` stores percent mode for CPU and
clears any previously stored absolute value. Setting
`--allocated-cpu-points 8` stores absolute mode and clears percent.
They never coexist for the same resource.

### Settings File Schema

Resource allocation is stored under a `resources` key in
`/etc/decloud/settings.json`:

```json
{
  "version": 1,
  "orchestrator_url": "https://decloud.stackfi.tech",
  "wallet": "0x86b8fE9ad3b4596a66b2C586F988A04f03be45F9",
  "country": "US",
  "region": "na-central",
  "name": "Dedixlab",
  "resources": {
    "cpu":     { "mode": "percent", "value": 90 },
    "memory":  { "mode": "mb",      "value": 4096 },
    "storage": { "mode": "percent", "value": 90 },
    "gpu":     { "mode": "count",   "value": null }
  }
}
```

When the `resources` key is absent or a specific resource entry is
missing, the platform default applies (90% for continuous, all for
GPU). `gpu.value: null` means "all detected GPUs."

Mode values per resource type:

| Resource | Allowed modes | Value type |
| --- | --- | --- |
| `cpu` | `"percent"`, `"points"` | int (1–100 for percent, ≥1 for points) |
| `memory` | `"percent"`, `"mb"` | int (1–100 for percent, ≥256 for mb) |
| `storage` | `"percent"`, `"mb"` | int (1–100 for percent, ≥1024 for mb) |
| `gpu` | `"count"` | int or null (null = all, 0 = none offered) |

### Validation Rules

The CLI validates before writing to `settings.json`:

| Rule | Enforcement |
| --- | --- |
| Percent range | 1 ≤ value ≤ 95 (prevents operators from offering 0% or starving the OS at 100%) |
| Memory absolute minimum | ≥ 256 MB (below this, nothing useful can be scheduled) |
| Storage absolute minimum | ≥ 1024 MB |
| CPU points minimum | ≥ 1 |
| GPU count range | 0 ≤ value ≤ detected count (validated at registration, not at configure time, since GPUs may change) |
| Mutual exclusion | Setting percent clears absolute; setting absolute clears percent |

The orchestrator performs deeper validation at registration (see §5.2).

---

## 5. System Flow

### 5.1 Configuration

```
Operator → decloud configure --allocated-memory-mb 4096
       │
       ├─ CLI validates format and range
       ├─ Writes to /etc/decloud/settings.json
       ├─ Syncs to appsettings.Production.json
       └─ Restarts agent if running
```

Resource allocation changes follow the **locality/rate change gate**:
if the node is enrolled (credentials exist, no logout sentinel), the
CLI refuses the change and instructs the operator:

```
✗ Cannot change resource allocation while enrolled.
  Run 'decloud logout' first.

  The full sequence for resource allocation changes:
    1. sudo decloud logout
    2. sudo decloud configure --allocated-memory-mb 4096
    3. sudo decloud register
    4. sudo decloud login
```

This prevents accepting new VMs under a capacity ceiling the operator
is about to reduce.

### 5.2 Registration

The node agent resolves each resource limit to an absolute value and
includes it in the registration request:

```
Node Agent                                    Orchestrator
    │                                              │
    ├─ Read settings.json resources                │
    ├─ Run hardware discovery                      │
    ├─ Resolve each limit:                         │
    │   cpu:     percent × TotalComputePoints      │
    │   memory:  percent × TotalBytes              │
    │   storage: percent × TotalStorageBytes       │
    │   gpu:     min(configured, detected)         │
    │                                              │
    ├─ POST /api/nodes/{id}/register ─────────────►│
    │   {                                          │
    │     ...existing fields...,                   │
    │     "allocatedResources": {                  │
    │       "computePoints": 9,                    │
    │       "memoryBytes": 4294967296,             │
    │       "storageBytes": 186305249280,          │
    │       "gpuCount": null                       │
    │     }                                        │
    │   }                                          │
    │                                              │
    │                         Orchestrator validates:
    │                         ├─ Each ≤ hardware max
    │                         ├─ Each ≥ system VM needs
    │                         ├─ Each ≥ existing ReservedResources (§8.4)
    │                         ├─ Memory ≥ platform min
    │                         └─ GPU count ≤ detected
    │                                              │
    │◄─────────────── 200 OK ──────────────────────│
```

**Orchestrator validation at registration:**

| Check | Rejection message |
| --- | --- |
| `allocatedMemoryBytes > TotalBytes` | "Allocated memory (X GB) exceeds physical RAM (Y GB)" |
| `allocatedComputePoints > hardwareMaxPoints` | "Allocated CPU points (X) exceed hardware capacity (Y)" |
| `allocatedGpuCount > detectedGpuCount` | "Allocated GPU count (X) exceeds detected GPUs (Y)" |
| `allocatedMemoryBytes < systemVmMemoryNeed` | "Allocated memory (X MB) insufficient for system VM obligations (Y MB required)" |
| `allocatedComputePoints < systemVmPointNeed` | "Allocated CPU points (X) insufficient for system VM obligations (Y required)" |
| `allocatedMemoryBytes < existingReservedMemory` | "Allocated memory (X GB) below currently reserved (Y GB). Drain VMs first." |
| `allocatedComputePoints < existingReservedPoints` | "Allocated CPU points (X) below currently reserved (Y). Drain VMs first." |
| `allocatedStorageBytes < existingReservedStorage` | "Allocated storage (X GB) below currently reserved (Y GB). Drain VMs first." |

### 5.3 Capacity Calculation

`NodeCapacityCalculator.CalculateTotalCapacityAsync` changes from
computing capacity from hardware alone to respecting the operator
limit:

```
Current (hardware-derived):
  totalMemory = Memory.AllocatableBytes          ← volatile
  totalPoints = cores × pointsPerCore × overcommit
  totalStorage = physicalStorage × storageOvercommit

Proposed (operator-bounded):
  totalMemory  = min(allocatedMemoryBytes,  TotalBytes)
  totalPoints  = min(allocatedCpuPoints,    cores × pointsPerCore × overcommit)
  totalStorage = min(allocatedStorageBytes × storageOvercommit, physicalStorage × storageOvercommit)
```

The `min()` ensures the operator cannot inflate beyond hardware. The
operator can only constrain downward.

For storage, the overcommit multiplier is applied **after** the
operator limit, since overcommit is a platform scheduling policy,
not an operator decision:

```
effectiveStorageCapacity = min(operatorStorageBytes, physicalStorageBytes) × storageOvercommitRatio
```

### 5.4 Heartbeat & Monitoring

The heartbeat already carries `AvailableResources` from the node agent
and the orchestrator compares it against its tracked state. With
operator limits, both sides agree on the same ceiling:

- **Node agent snapshot**: `TotalMemoryBytes` reports the operator's
  allocated ceiling (not raw physical). `UsedMemoryBytes` reports
  OS-level usage within that ceiling. `AvailableMemoryBytes =
  allocated − used`.

- **Orchestrator**: `TotalResources.MemoryBytes` is the operator's
  allocated value (set at registration). `ReservedResources.MemoryBytes`
  is the sum of scheduled VM memory specs.

The existing heartbeat discrepancy log continues to work, now
comparing values in the same universe.

---

## 6. What This Replaces

| Current mechanism | Replaced by | Why |
| --- | --- | --- |
| `MemoryInfo.AllocatableBytes` (`Total − Reserved − Used`) as capacity input | `allocatedMemoryBytes` from operator config | Eliminates volatile `UsedBytes` from capacity calculation |
| Hardcoded `ReservedBytes = 2 GB` in `MemoryInfo` | 90% default (10% implicit reservation that scales with hardware) | 2 GB is too much on 4 GB machines, too little on 128 GB machines |
| No CPU allocation control (hardware max always offered) | `allocatedCpuPoints` or `allocatedCpuPercent` | Operators can reserve CPU for non-DeCloud workloads |
| No storage allocation control | `allocatedStorageMb` or `allocatedStoragePercent` | Same |
| No GPU allocation control (all GPUs always offered) | `allocatedGpuCount` | Operators with multiple GPUs can keep some for personal use |

**What is NOT replaced:**

- `MemoryInfo.ReservedBytes` field — kept in the model for backward
  compatibility and display purposes, but no longer used in capacity
  calculations.
- `HardwareInventory` — continues to carry raw physical values. The
  orchestrator needs them for validation, marketplace display, and
  attestation verification.
- `PerformanceEvaluation` — benchmark scores and tier eligibility are
  hardware-derived and independent of allocation limits.

---

## 7. Interaction with Existing Systems

### Settings Lifecycle

Resource allocation settings are classified as **operational
(capacity-affecting)**, alongside locality and rates. They follow
the same gate:

```
logout → configure → register → login
```

Rationale: reducing offered capacity while VMs are scheduled against
the old ceiling violates the operator's stated intent. The logout-first
gate prevents accepting new VMs under capacity the operator is about
to reduce.

### Settings Hash

Resource allocation limits are **excluded** from `SettingsHash`. The
hash covers trust-relevant fields (wallet, country, region) for
continuous drift detection. Resource limits are operational parameters
— changing them does not affect jurisdictional claims or trust
properties.

Drift detection for resource limits happens through the existing
heartbeat discrepancy check, not through the settings hash.

### Quality Tiers & Overcommit

Operator allocation limits set the **outer boundary**. Quality tier
overcommit ratios operate **within** that boundary:

```
Hardware  ──► Operator limit (e.g., 80%)  ──► Tier capacity (overcommit applied)
                    │                              │
                    │ Stable, operator-controlled   │ Platform-controlled
                    │ Set at registration            │ Per-scheduling-decision
```

Example: 2-core node, 1.413 points/core, operator offers 80%:

- Hardware max: 2 × 1.413 × 4.0 (Burstable overcommit) = 11.3 → 11 points
- Operator limit: 80% × 11 = 8.8 → 8 points
- `TotalResources.ComputePoints` = 8
- Tier-specific capacity for Guaranteed (1:1): capped by operator's 8 points
- Tier-specific capacity for Burstable (4:1): also capped at 8 points (operator limit is the ceiling)

### System VM Obligations

System VMs (DHT, Relay, BlockStore) are **inside** the operator's
allocation. The operator's limit is the outer boundary for everything
the platform does on the machine.

```
Operator allocation (e.g., 9 points, 9 GB)
├── System VM reservations (e.g., 4 points, 2 GB)
└── Available for tenant VMs (e.g., 5 points, 7 GB)
```

The orchestrator validates at registration that the operator's
allocation is sufficient to cover system VM obligations. If not,
registration is rejected with a clear message.

### Scheduling

`VmSchedulingService.CalculateResourceAvailabilityAsync` already reads
from `node.TotalResources` and `node.ReservedResources`. Since
`TotalResources` is now set from the operator's allocated value (not
raw hardware max), scheduling automatically respects the limit. No
changes needed in the scheduling filters or scoring logic.

### Marketplace Display

`NodeAdvertisement` in the marketplace should show both:

- **Physical hardware** — for buyers comparing node specifications
  (CPU model, total RAM, GPU model, storage type)
- **Offered capacity** — for buyers evaluating available resources
  (`TotalResources` after operator limit)

`HardwareInventory` continues to carry raw values. `TotalResources`
reflects what the operator has made available.

---

## 8. Reservation Lifecycle

Understanding when the orchestrator reserves and frees resources on a
node is critical to the allocation design. The operator's allocation
limit sets the outer boundary (`TotalResources`). The orchestrator's
reservation tracking (`ReservedResources`) operates inside that
boundary, incrementing when VMs are scheduled and decrementing when
VMs reach their terminal state.

### When Resources Are Reserved

Resources are reserved in `VmService.TryScheduleVmAsync` (STEP 3),
**after** the scheduler selects a node but **before** the create
command is sent to the node agent:

```
VmService.TryScheduleVmAsync:
  STEP 1: Calculate compute point cost
  STEP 2: Select best node (scheduler evaluates available = Total − Reserved)
  STEP 3: Reserve resources ← immediate, persisted to DB
          node.ReservedResources.ComputePoints += pointCost
          node.ReservedResources.MemoryBytes   += vm.Spec.MemoryBytes
          node.ReservedResources.StorageBytes   += vm.Spec.DiskBytes
          SaveNodeAsync(node)
  STEP 4: Assign VM to node, set status = Provisioning
  STEP 5: Send create command to node agent
```

If the scheduler finds no suitable node (STEP 2), an exception is
thrown before STEP 3. No reservation is made; no rollback needed.

If any step after STEP 3 fails (STEP 4 or 5), the reservation
persists. The VM transitions to Error, and the reservation remains
until the VM is explicitly deleted. This is conservative by design —
see §8.3.

### When Resources Are Freed

Resources are freed **only** when a VM reaches the terminal `Deleted`
state, via `VmLifecycleManager.OnVmDeletedAsync` →
`FreeNodeResourcesAsync`:

```
node.ReservedResources.ComputePoints = Max(0, Reserved − vm.Spec.ComputePointCost)
node.ReservedResources.MemoryBytes   = Max(0, Reserved − vm.Spec.MemoryBytes)
node.ReservedResources.StorageBytes  = Max(0, Reserved − vm.Spec.DiskBytes)
```

The `Max(0, ...)` guard prevents underflow from double-frees or
drift.

**No other status transition frees resources.** The full lifecycle:

```
Pending → Scheduling → Provisioning → Running → Stopping → Stopped → Deleting → Deleted
                            │                                              │
                       reserves ←──── resources held ────────────────► frees
```

| VM Status | Resources reserved? | Why |
| --- | --- | --- |
| Pending | No | Not yet scheduled to a node |
| Scheduling | No | Node selection in progress |
| Provisioning | **Yes** | Reserved at scheduling, VM being created |
| Running | **Yes** | VM active |
| Stopping | **Yes** | Graceful shutdown in progress |
| Stopped | **Yes** | VM paused but disk/identity persist; can restart |
| Error | **Yes** | VM may recover (Error → Provisioning) or be deleted |
| Deleting | **Yes** | Waiting for node agent to confirm destruction |
| Deleted | **No** | Terminal state; resources freed |

### Reservation Invariants

The system maintains this invariant at all times:

```
TotalResources ≥ ReservedResources (per resource class)
```

Where:
- `TotalResources` = operator's allocated capacity (set at registration)
- `ReservedResources` = sum of all non-Deleted VM specs on this node

The scheduler enforces this by checking `RemainingResources =
TotalResources − ReservedResources` before placing a new VM. If
remaining is insufficient, the node is rejected.

With operator allocation limits, a second invariant applies:

```
AllocatedResources ≤ HardwareMaximum (per resource class)
```

The operator cannot inflate capacity beyond what the hardware provides.
The `min()` in the capacity calculator enforces this.

### Re-registration with Existing Reservations

When a node re-registers (`RegisterNodeAsync`), existing reservations
are preserved:

```csharp
ReservedResources = existingNode?.ReservedResources ?? new ResourceSnapshot()
```

This creates an important constraint for operator allocation changes.
If a node has VMs with 6 GB of memory reserved and the operator
re-registers with `--allocated-memory-mb 4096` (4 GB), the result is:

```
TotalResources.MemoryBytes   = 4,294,967,296  (4 GB — operator limit)
ReservedResources.MemoryBytes = 6,442,450,944  (6 GB — existing VMs)
```

This violates the `Total ≥ Reserved` invariant. The orchestrator must
handle this at registration time. Two options:

**Option A — Reject registration (strict):**
The orchestrator rejects: *"Allocated memory (4 GB) is less than
currently reserved resources (6 GB). Drain or delete VMs first, or
increase allocation."* This prevents the invariant from ever being
violated but blocks the operator from re-registering until they
resolve the over-commitment.

**Option B — Accept with scheduling pause (lenient):**
The orchestrator accepts the registration, stores the new
`TotalResources`, and logs a warning. The node is marked as
over-committed — the scheduler will not place new VMs (remaining
resources are negative), but existing VMs are not terminated. The
operator must drain VMs to restore the invariant.

**Design choice:** Option A (reject) for re-registration, because the
operator explicitly chose to reduce capacity. If they want to shrink,
they must drain first. This keeps the invariant clean and avoids a
state where the orchestrator knowingly stores an invalid capacity
relationship. The drain sequence:

```bash
sudo decloud logout                        # stop new scheduling
sudo decloud vm drain                      # migrate VMs off (when implemented)
# ... wait for migrations and deletions ...
sudo decloud configure --allocated-memory-mb 4096
sudo decloud register                      # now Reserved ≤ Total
sudo decloud login
```

### Self-Healing & Drift Detection

The orchestrator has two mechanisms that detect when
`ReservedResources` drifts from reality:

**1. Heartbeat discrepancy check** (`NodeService.ProcessHeartbeatAsync`):
Every heartbeat, the orchestrator compares node-reported free resources
against its own calculation (`Total − Reserved`). Significant
divergence is logged as a warning. This catches cases where a VM was
destroyed on the node (e.g., operator manually ran `virsh destroy`)
but the orchestrator still holds the reservation.

```
Orchestrator free = TotalResources − ReservedResources
Node-reported free = from heartbeat AvailableResources
Drift = |Orchestrator free − Node-reported free|
```

**2. Self-healing stats** (`DataStore.GetSystemStatsAsync`):
System-wide statistics are calculated from **actual VMs** in
Running/Provisioning status, not from `ReservedResources`. This
provides a ground-truth view for dashboards and admin tooling,
independent of per-node reservation tracking.

```csharp
var actualUsedPoints = activeVms.Sum(v => v.Spec.ComputePointCost);
var actualUsedMemory = activeVms.Sum(v => (long)v.Spec.MemoryBytes);
```

Neither mechanism currently auto-corrects `ReservedResources`. The
TODO in the heartbeat handler (`// Implement resource reconciliation
logic here`) is an open item. When implemented, reconciliation should
only ever **decrease** reservations (freeing leaked resources), never
increase them — increasing would risk double-counting a VM that the
node hasn't finished creating yet.

---

## 9. Detailed Resource Semantics

### CPU (Compute Points)

**What points are:** A synthetic unit combining hardware performance
(benchmark score), core count, and overcommit ratio:
`totalPoints = physicalCores × (benchmarkScore / baselineBenchmark) × overcommitRatio`.

**Why percent is the primary mode:** Points depend on platform-
controlled parameters (baseline benchmark, overcommit ratio). If the
platform adjusts `BaselineOvercommitRatio` from 4.0 to 3.0, a node's
total drops from 11 to ~8.5 points. A percent-based allocation
(e.g., 90%) self-adjusts: 90% of 11 = 9.9, 90% of 8.5 = 7.6. The
operator's intent ("keep 10% for myself") survives platform changes.

**When absolute points are useful:** An operator who has inspected
their capacity via `decloud capacity` and wants precise control.
They accept that the total may shift if platform config changes.

**Resolution:** The node agent resolves at registration time:
- Percent: `(int)(totalComputePoints × percent / 100)`
- Absolute: used as-is, validated ≤ hardware max

### Memory

**What changes:** The current `AllocatableBytes = TotalBytes −
ReservedBytes − UsedBytes` is replaced by the operator's configured
limit. `UsedBytes` (volatile runtime metric) is removed from the
capacity calculation entirely. `ReservedBytes` (hardcoded 2 GB)
becomes a validation floor, not a calculation input.

**Why percent is the primary mode:** 90% scales with hardware — 1 GB
headroom on 10 GB, 6.4 GB on 64 GB, 12.8 GB on 128 GB. A fixed byte
reservation (the current 2 GB) is either too aggressive on small
machines or negligible on large ones.

**Resolution:** The node agent resolves at registration time:
- Percent: `(long)(totalBytes × percent / 100)`
- Absolute: `megabytes × 1024 × 1024`, validated ≤ `TotalBytes`

### Storage

**Interaction with overcommit:** The operator specifies how much
physical storage to offer. The orchestrator applies storage overcommit
on top (e.g., 2.5× for Burstable tier). The operator controls the
physical input; the platform controls the scheduling policy.

```
Operator: "offer 180 GB of my 200 GB disk"
Orchestrator: TotalResources.StorageBytes = 180 GB × 2.5 = 450 GB (scheduling headroom)
```

**Multi-volume nodes:** Physical storage total is
`sum(StorageInfo.TotalBytes)` across all mounted volumes. The
operator's percent applies to the aggregate.

### GPU

**Passthrough (VFIO):** One GPU = one VM, exclusive. An
`allocated-gpu-count` of 2 on a 4-GPU node means 2 GPUs are available
for VFIO assignment. The remaining 2 are excluded from the scheduling
pool.

**Proxied (daemon):** Multiple VMs share GPU(s) via the proxy daemon.
The `allocated-gpu-count` determines how many GPUs the proxy daemon
exposes.

**Which GPUs:** v1 uses "first N detected" ordering (stable across
reboots since PCI enumeration order is deterministic). A future
refinement could add `--allocated-gpu-devices 0000:01:00.0,...` for
explicit PCI address selection on mixed-GPU nodes.

**Zero GPUs:** `--allocated-gpu-count 0` is valid. The node has GPUs
but the operator does not want to offer them. The scheduler sees
`SupportsGpu = false` for this node.

---

## 10. Edge Cases & Safety

### Operator increases allocation above current hardware

The operator sets `--allocated-memory-mb 16384` on an 8 GB machine.
The node agent resolves this at registration to `min(16384 MB, 8 GB)`
= 8 GB. The orchestrator validates `allocated ≤ physical` and
accepts. No error, but a log warning:

```
Allocated memory (16 GB) exceeds physical RAM (8 GB), capped at 8 GB
```

### Operator reduces allocation below running VM reservations

The logout-first gate prevents changes while enrolled. After logout,
existing VMs continue running but no new VMs are scheduled. If the
operator re-registers with a lower allocation that falls below
`ReservedResources` (from existing VMs), registration is **rejected**
(see §8.4). The operator must drain or delete VMs first to bring
reservations below the new allocation target:

```bash
sudo decloud logout
# delete or migrate VMs until ReservedResources < desired allocation
sudo decloud configure --allocated-memory-mb 4096
sudo decloud register                      # succeeds once Reserved < 4096 MB
sudo decloud login
```

### Platform changes overcommit ratio

If `BaselineOvercommitRatio` changes from 4.0 to 3.0:

- **Percent mode:** Auto-adjusts. 90% of new total = lower absolute
  value. Re-registration required to pick up the new config, but the
  percent intent is preserved.
- **Absolute points mode:** The operator's cap stays at (e.g.) 8
  points. If the new hardware max is 8.5, the cap is nearly the
  entire capacity. The operator may want to reconfigure.
  Re-registration surfaces this: `decloud capacity` shows the new
  totals.

### Node with no GPUs sets gpu count

`--allocated-gpu-count 2` on a no-GPU node. The CLI accepts it
(GPUs may be installed later). Registration validates `count ≤
detected` and rejects with: "Allocated GPU count (2) exceeds detected
GPUs (0)."

### System VMs need more than allocated

The operator sets `--allocated-cpu-points 2` but system VMs need 4.
Registration rejects: "Allocated CPU points (2) insufficient for
system VM obligations (4 required). Increase allocation or remove
obligations." The operator must either increase allocation or
(in future) opt out of optional system VMs.

### First registration (no prior settings)

`resources` key absent from `settings.json`. The node agent applies
the 90% default for all continuous resources and "all" for GPU. This
matches current behavior for most nodes (the 90% default is slightly
more conservative than the current volatile formula, which can go
higher at idle).

---

## 11. Implementation Plan

### Touch Points

| Component | File(s) | Change |
| --- | --- | --- |
| **Settings schema** | `/etc/decloud/settings.json` | Add `resources` key with per-resource mode/value |
| **CLI configure** | `cli/decloud` → `cmd_configure()` | New flags, mutual-exclusion logic, logout gate for resource changes |
| **CLI help** | `cli/decloud`, `cli/docs/README-decloud-cli.md` | Document new flags |
| **Node agent models** | `NodeAgent.Core/Models/HardwareInventory.cs` | `MemoryInfo.ReservedBytes` becomes display-only |
| **Node agent settings** | `NodeAgent/Services/NodeMetadataService` (or equivalent) | Read `resources` from settings, resolve to absolute values |
| **Registration request** | `NodeAgent.Core/Interfaces/IServices.cs` → `NodeRegistration` | Add `AllocatedResources` field |
| **Registration handler** | `Orchestrator/Services/NodeService.cs` → `RegisterNodeAsync` | Accept `AllocatedResources`, validate against hardware max and existing `ReservedResources` (§8.4), pass to capacity calculator |
| **Capacity calculator** | `Orchestrator/Services/NodeCapacityCalculator.cs` | Use `AllocatedResources` instead of `Memory.AllocatableBytes` |
| **Resource snapshot** | `NodeAgent.Infrastructure/Services/ResourceDiscoveryService.cs` | `TotalMemoryBytes` in snapshot = operator-allocated ceiling |
| **Dashboard** | `cli/dashboard/screens/hardware.py` | Show operator limit as distinct field from physical total |
| **Heartbeat payload** | `NodeAgent/Services/OrchestratorClient.cs` | `availableResources` based on operator ceiling |
| **Node model** | `Orchestrator/Models/Node.cs` | Store `AllocatedResources` on node document (persisted to MongoDB) |
| **appsettings sync** | `cli/decloud` → `cmd_configure()` | Sync resource settings to `appsettings.Production.json` |

### Phasing

**Phase 1 — Memory (fixes the critical bug)**

Memory is the resource with the active volatile-ceiling bug. Ship
memory allocation first:
- `--allocated-memory-percent` and `--allocated-memory-mb` in CLI
- `AllocatedResources.MemoryBytes` in registration request
- `NodeCapacityCalculator` uses allocated value instead of
  `AllocatableBytes`
- Node agent snapshot reports allocated ceiling as
  `TotalMemoryBytes`
- 90% default when unconfigured

This fixes the volatile capacity bug with the smallest possible
change surface.

**Phase 2 — CPU and Storage**

Same pattern, extending to compute points and storage:
- `--allocated-cpu-percent`, `--allocated-cpu-points`
- `--allocated-storage-percent`, `--allocated-storage-mb`
- Resolves the TotalResources.ComputePoints dual-calculation-path
  discrepancy (12 vs 11) by making the operator limit the single
  source of truth

**Phase 3 — GPU**

- `--allocated-gpu-count`
- GPU count filtering in scheduler
- Future: `--allocated-gpu-devices` for PCI address selection

---

## 12. Example Walkthrough

Node: 2 cores (Xeon E5-2650 v4), 10 GB RAM, 200 GB storage, 0 GPUs.
Benchmark: 1413 (1.413 points/core). Burstable tier eligible (Tier 3).

### Operator configures allocation

```bash
sudo decloud logout
sudo decloud configure \
    --allocated-cpu-percent 80 \
    --allocated-memory-mb 7168 \
    --allocated-storage-percent 85
sudo decloud register
sudo decloud login
```

### Node agent resolves at registration

```
CPU:     80% × (2 × 1.413 × 4.0) = 80% × 11.3 = 9.04 → 9 points
Memory:  7168 MB = 7,516,192,768 bytes (absolute, ≤ 10,424,856,576 ✓)
Storage: 85% × 207,006,056,448 = 175,955,147,980 bytes
GPU:     not configured → all → 0 (no GPUs detected)
```

### Orchestrator validates and stores

```
TotalResources:
  ComputePoints = 9
  MemoryBytes   = 7,516,192,768
  StorageBytes  = 175,955,147,980 × 2.5 (Burstable overcommit) = 439,887,869,950

System VM obligations:
  4 points, 2 GB → fits within 9 points and 7 GB ✓

Available for tenant VMs:
  5 points, ~5.2 GB memory, ~430 GB storage (with overcommit)
```

### Heartbeat alignment

Node agent reports:
```json
{
  "availableResources": {
    "computePoints": 5,
    "memoryBytes": 5368709120,
    "storageBytes": 429150451710
  }
}
```

Orchestrator calculates:
```
Total (9 points) − Reserved (4 points) = 5 points  ✓ matches
Total (7.5 GB)   − Reserved (2 GB)     = 5.5 GB    ≈ matches (OS usage accounts for delta)
```

No discrepancy alarm. Both sides agree on the capacity universe.

---

*For related context, see:*
- *`docs/NODE-LIFECYCLE.md` — settings lifecycle, configure/register/login flow*
- *`PROJECT_FEATURES.md` §9 — resource management overview*
- *`docs/SCHEDULING.md` — constraint vocabulary, tier configuration*
