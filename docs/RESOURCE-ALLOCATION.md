# Node Resource Management & Allocation

**Last Updated:** 2026-05-18
**Status:** Phase 1 (memory allocation) implemented, resource accounting refactor in progress
**Purpose:** System design for operator-controlled resource allocation limits and heartbeat-driven resource accounting.

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
   - [Heartbeat & Resource Accounting](#heartbeat--resource-accounting)
6. [What This Replaces](#6-what-this-replaces)
7. [Interaction with Existing Systems](#7-interaction-with-existing-systems)
   - [Settings Lifecycle](#settings-lifecycle)
   - [Settings Hash](#settings-hash)
   - [Quality Tiers & Overcommit](#quality-tiers--overcommit)
   - [System VM Obligations](#system-vm-obligations)
   - [Scheduling](#scheduling)
   - [Marketplace Display](#marketplace-display)
8. [Resource Accounting Model](#8-resource-accounting-model)
   - [Four Resource Layers](#four-resource-layers)
   - [Scheduling Reservation Lifecycle](#scheduling-reservation-lifecycle)
   - [Heartbeat-Driven UsedResources](#heartbeat-driven-usedresources)
   - [Availability Formula](#availability-formula)
   - [Invariants](#invariants)
   - [Re-registration](#re-registration)
   - [Edge Cases](#resource-accounting-edge-cases)
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

The current system has two related problems:

**Volatile capacity ceiling.** The node's allocatable capacity is
computed from a point-in-time snapshot at registration. For memory,
`AllocatableBytes = TotalBytes - ReservedBytes - UsedBytes`, where
`UsedBytes` is the host's memory consumption at the moment of
registration. Two identical machines registering at different moments
get different capacity ceilings.

**Accumulated reservation drift.** `ReservedResources` is a monotonic
accumulator -- it grows when VMs are scheduled and shrinks only when
VMs reach the terminal `Deleted` state. Any path that creates VMs
without going through the scheduling path (system VMs, adoption,
recovery) leaves resources unaccounted for. System VMs are created by
the node agent's reconciler and the orchestrator never reserves their
resources, causing the scheduler to overestimate available capacity.

The fix for the first problem is operator-controlled allocation limits
with a sensible default. The fix for the second is to make resource
accounting observational -- derived from what the node actually reports
in its heartbeat -- rather than transactional.

---

## 2. Design Goals

1. **Operator sovereignty** -- the operator decides how much of their
   hardware to offer. The platform manages everything inside that
   boundary (system VMs, tenant VMs, scheduling).

2. **Stable capacity ceiling** -- a node's total offered capacity must
   not change unless the operator explicitly reconfigures or
   re-registers. No volatile runtime measurements in the capacity
   calculation path.

3. **Consistent abstraction** -- the same percent/absolute pattern for
   all continuous resources (CPU, memory, storage). A separate
   count-based model for discrete resources (GPU). One mental model
   for operators to learn.

4. **Safe defaults** -- a node that never configures allocation limits
   works correctly with a platform-wide default (90%). No operator
   action required for the common case.

5. **No hidden interactions** -- percent and absolute modes are mutually
   exclusive per resource. One field, one interpretation. No
   precedence rules to debug.

6. **Observational accounting** -- resource usage is measured from
   heartbeat-reported VMs, not accumulated from scheduling
   transactions. Drift is impossible because usage is recomputed every
   heartbeat cycle, not maintained as a running total.

---

## 3. Resource Model

### Continuous Resources (CPU, Memory, Storage)

Each continuous resource supports two modes: **percent** of the
physical total, or an **absolute** value in the resource's natural
unit. They are mutually exclusive -- setting one clears the other.

| Resource | Percent base | Absolute unit | Resolved to |
| --- | --- | --- | --- |
| CPU | TotalComputePoints (derived from benchmark x cores x overcommit) | Compute points (int) | `int` points |
| Memory | TotalBytes (physical RAM) | Megabytes | `long` bytes |
| Storage | TotalStorageBytes (physical, pre-overcommit) | Megabytes | `long` bytes |

Resolution always happens on the **node agent** before registration.
The orchestrator receives a single resolved absolute value per
resource and never needs to interpret percent vs absolute itself.

### Discrete Resources (GPU)

GPUs are whole physical devices. Percent doesn't map cleanly ("90% of
1 GPU" is 0.9 -- meaningless). GPU allocation uses **count only**:

| Resource | Mode | Unit | Default |
| --- | --- | --- | --- |
| GPU | count | Discrete devices | All detected (null = all) |

### Platform Default

When the operator has not configured an allocation limit for a
resource, the platform applies a default:

| Resource | Default | Rationale |
| --- | --- | --- |
| CPU | 90% of TotalComputePoints | Reserves ~10% for host processes |
| Memory | 90% of TotalBytes | Reserves ~10% for OS page cache, host daemons |
| Storage | 90% of physical storage | Reserves ~10% for OS logs, temp files |
| GPU | All detected (100%) | Discrete resource -- partial GPUs don't exist |

---

## 4. Operator Interface

### CLI Flags

```bash
# Percent mode (survives platform config changes)
sudo decloud configure --allocated-memory-percent 70

# Absolute mode (precise operator control)
sudo decloud configure --allocated-memory-mb 4096

# GPU (count only)
sudo decloud configure --allocated-gpu-count 2

# Combined
sudo decloud configure --allocated-cpu-percent 80 --allocated-memory-mb 4096
```

### Settings File Schema

```json
{
  "version": 1,
  "orchestrator_url": "...",
  "wallet": "0x...",
  "resources": {
    "cpu":     { "mode": "percent", "value": 90 },
    "memory":  { "mode": "mb",      "value": 4096 },
    "storage": { "mode": "percent", "value": 90 },
    "gpu":     { "mode": "count",   "value": null }
  }
}
```

When absent, platform default (90% / all) applies.

### Validation Rules

| Rule | Enforcement |
| --- | --- |
| Percent range | 1-95 |
| Memory absolute minimum | >= 256 MB |
| Storage absolute minimum | >= 1024 MB |
| CPU points minimum | >= 1 |
| GPU count range | 0 to detected (validated at registration) |
| Mutual exclusion | Setting percent clears absolute and vice versa |

---

## 5. System Flow

### 5.1 Configuration

Resource allocation changes follow the **locality/rate change gate**:
logout required if enrolled.

### 5.2 Registration

Node agent resolves limits to absolute values and sends in
registration request. Orchestrator validates:

| Check | Rejection message |
| --- | --- |
| `allocated > hardwareMax` | Capped at hardware max (warning, not rejection) |
| `allocated < usedResources` | "Allocated below currently used. Drain VMs first." |
| `gpuCount > detected` | "GPU count exceeds detected GPUs" |

Note: re-registration validation checks against `UsedResources` (from
the most recent heartbeat), not `ReservedResources`. See S8.6.

### 5.3 Capacity Calculation

```
totalMemory  = min(allocatedMemoryBytes,  physicalTotalBytes)
totalPoints  = min(allocatedCpuPoints,    hardwareMaxPoints)
totalStorage = min(allocatedStorageBytes, physicalStorage) x storageOvercommitRatio
```

### 5.4 Heartbeat & Resource Accounting

Every heartbeat (~15s), the node agent reports **all VMs** including
system VMs. The orchestrator computes `UsedResources` from these
reports:

```
UsedResources = sum of (computePointCost, memoryBytes, diskBytes) for all reported VMs
```

The scheduler uses:

```
Available = TotalResources - UsedResources - ReservedResources
```

Where `ReservedResources` holds only transient scheduling holds. In
steady state, `ReservedResources` is zero. See S8 for full details.

---

## 6. What This Replaces

| Current mechanism | Replaced by | Why |
| --- | --- | --- |
| `MemoryInfo.AllocatableBytes` (volatile) | Operator config with 90% default | Stable ceiling |
| Hardcoded 2 GB `ReservedBytes` | 90% default (scales with hardware) | 2 GB wrong on both small and large machines |
| No CPU/storage/GPU allocation control | Per-resource operator limits | Operators can reserve hardware for own use |
| `ReservedResources` as lifetime ledger | `UsedResources` from heartbeat + transient holds | Eliminates drift |
| System VMs unaccounted | All VMs reported in heartbeat | No special-case reservation code |
| `FreeNodeResourcesAsync` on VM deletion | VM disappears from heartbeat automatically | No manual cleanup |

---

## 7. Interaction with Existing Systems

### Settings Lifecycle

Resource allocation settings follow the logout-first gate (same as
locality/rate changes).

### Settings Hash

Resource allocation limits are **excluded** from `SettingsHash`
(operational, not trust-relevant).

### Quality Tiers & Overcommit

Operator limit is the outer boundary. Tier overcommit operates inside.

### System VM Obligations

System VMs are **not special-cased** for resource accounting. The
node agent reports them in heartbeat like any other VM. The
orchestrator includes them in `UsedResources`.

```
Operator allocation (e.g., 9 points, 8 GB)
|-- UsedResources: system VMs (3 pts, 1.25 GB)
|-- UsedResources: tenant VMs (5 pts, 4 GB)
'-- Available for scheduling (1 pt, 2.75 GB)
```

### Scheduling

Uses the new formula:

```
Available = TotalResources - UsedResources - ReservedResources
```

### Marketplace Display

Shows `TotalResources` (offered capacity) and
`TotalResources - UsedResources` (available). Raw hardware specs in
Details view.

---

## 8. Resource Accounting Model

### 8.1 Four Resource Layers

| Field | Source of truth | Updated when | Steady-state |
| --- | --- | --- | --- |
| `TotalResources` | Capacity calculator from `AllocatedResources` | Registration | Stable (e.g., 8 GB) |
| `AllocatedResources` | Operator config via registration | Registration | Stable (operator intent) |
| `UsedResources` | Sum of VM specs from heartbeat | Every heartbeat (~15s) | Sum of all running VMs |
| `ReservedResources` | Scheduling holds only | Schedule to deploy/fail | Typically 0 |

```
+---------------------------------------------+
|         TotalResources (8 GB)               |  Operator ceiling
+---------------------------------------------+
|  UsedResources (5 GB)                       |  Heartbeat ground truth
|  +-------------+ +----------------------+  |
|  | System VMs  | |    Tenant VMs        |  |
|  |  1.25 GB    | |     3.75 GB          |  |
|  +-------------+ +----------------------+  |
+---------------------------------------------+
|  ReservedResources (0.5 GB)                 |  Transient hold
|  +----------------------+                   |
|  | VM being deployed    |                   |
|  +----------------------+                   |
+---------------------------------------------+
|  Available (2.25 GB)                        |  For new scheduling
+---------------------------------------------+
```

### 8.2 Scheduling Reservation Lifecycle

`ReservedResources` is a **short-lived hold**, not a ledger. It
exists only during the window between node selection and VM
confirmation or failure.

```
SCHEDULE:   ReservedResources += VM spec
                |
         +----- Deploy command sent to node
         |
    SUCCESS:  VM transitions Provisioning -> Running
         |    Hold released (VM now in UsedResources via heartbeat)
         |
    FAILURE:  VM transitions Provisioning -> Error or Deleting
              Hold released
```

**Hold creation:** `VmService.TryScheduleVmAsync` STEP 3 (unchanged).

**Hold release:** `VmLifecycleManager` on transition out of
`Provisioning`. This is the natural boundary -- the lifecycle manager
already handles status transition side effects.

```csharp
case VmStatus.Running when from is VmStatus.Provisioning:
case VmStatus.Error when from is VmStatus.Provisioning:
case VmStatus.Deleting when from is VmStatus.Provisioning:
    await ReleaseSchedulingHoldAsync(vm);
    break;
```

### 8.3 Heartbeat-Driven UsedResources

Every heartbeat, `ProcessHeartbeatAsync` recomputes `UsedResources`:

```csharp
node.UsedResources = new ResourceSnapshot
{
    ComputePoints = reportedVms.Sum(v => v.ComputePointCost),
    MemoryBytes   = reportedVms.Sum(v => v.MemoryBytes),
    StorageBytes  = reportedVms.Sum(v => v.DiskBytes),
};
```

This is ground truth. System VMs, tenant VMs, stopped VMs, error-state
VMs -- all included if the node reports them. When a VM is deleted and
no longer reported, `UsedResources` drops automatically.

### 8.4 Availability Formula

```
Available = TotalResources - UsedResources - ReservedResources
```

Replaces the previous `Available = TotalResources - ReservedResources`
where `ReservedResources` was a lifetime ledger.

### 8.5 Invariants

```
TotalResources >= UsedResources + ReservedResources
```

Can be temporarily violated if operator reduced `TotalResources` via
re-registration while VMs were running. The scheduler handles this
naturally -- `Available` goes negative, no new VMs are placed.

### 8.6 Re-registration

Validation checks `allocated >= usedResources` (from last heartbeat).
`UsedResources` is ground truth -- no accumulated drift to worry about.

Rejection: *"Allocated memory (4 GB) below currently used (6 GB).
Drain or delete VMs first."*

### 8.7 Resource Accounting Edge Cases

**Window between scheduling and heartbeat:** VM is in
`ReservedResources` but not yet in `UsedResources`. Available =
Total - Used - Reserved. Both counted, no gap.

**Node goes offline:** Heartbeat stops, `UsedResources` stale. Node
marked offline, scheduler skips it. First heartbeat on reconnect
refreshes `UsedResources`.

**Race -- two VMs scheduled simultaneously:** Each gets own
reservation. Second scheduling sees first's reservation. On next
heartbeat, both appear in `UsedResources`, both holds released.

**Stopped VMs:** Node reports them. In `UsedResources`. Correct --
disk and identity hold resources.

**Manual VM destruction:** VM disappears from heartbeat.
`UsedResources` drops. No stale state.

**System VMs:** No special handling. Appear in heartbeat like any VM.

---

## 9. Detailed Resource Semantics

### CPU (Compute Points)

Synthetic unit: `physicalCores x (benchmarkScore / baseline) x overcommitRatio`.
Percent is primary mode (self-adjusts when platform config changes).

### Memory

Operator limit replaces volatile `AllocatableBytes`. 90% default
scales with hardware.

### Storage

Operator specifies physical amount. Orchestrator applies tier
overcommit on top.

### GPU

Count-based. Passthrough = exclusive. Proxied = shared. Zero = opt out.

---

## 10. Edge Cases & Safety

### Operator increases allocation above hardware

Capped at hardware max with warning log.

### Operator reduces allocation below running VMs

Registration rejected. Must drain first. See S8.6.

### Platform changes overcommit ratio

Percent mode auto-adjusts. Absolute mode requires operator awareness.

### System VMs need more than allocated

Registration rejects: "Allocated below currently used."

### First registration (no prior settings)

90% default. `UsedResources` starts at zero. System VMs appear on
first heartbeat after deployment.

---

## 11. Implementation Plan

### Touch Points

| Component | File(s) | Change |
| --- | --- | --- |
| **Node model** | `Orchestrator/Models/Node.cs` | Add `UsedResources` field |
| **Heartbeat** | `Orchestrator/Services/NodeService.cs` | Compute `UsedResources` from reported VMs in `ProcessHeartbeatAsync` |
| **Scheduling** | `Orchestrator/Services/VmScheduling/VmSchedulingService.cs` | `Available = Total - Used - Reserved` |
| **Hold release** | `Orchestrator/Services/VmLifecycleManager.cs` | Release hold on Provisioning exit transitions |
| **Resource cleanup** | `Orchestrator/Services/VmLifecycleManager.cs` | `FreeNodeResourcesAsync` only releases transient hold if VM was Provisioning |
| **Registration** | `Orchestrator/Services/NodeService.cs` | Validate `allocated >= usedResources`; remove old `ReservedResources` preservation logic |
| **Marketplace** | `Orchestrator/Services/NodeMarketplaceService.cs` | Available = `Total - Used` |
| **Stats** | `Orchestrator/Persistence/DataStore.cs` | Align terminology with new model |

### Phasing

**Phase 1 -- Operator allocation limits (implemented)**

Operator-configured memory limits, 90% default, stable capacity
ceiling. CLI flags, settings schema, registration flow.

**Phase 2 -- Heartbeat-driven resource accounting (current)**

- Add `UsedResources` to Node model
- Compute from heartbeat VMs in `ProcessHeartbeatAsync`
- Change scheduler formula to `Total - Used - Reserved`
- Change `ReservedResources` to transient holds only
- Release holds on Provisioning exit
- Simplify `FreeNodeResourcesAsync`
- Update registration validation against `UsedResources`

**Phase 3 -- CPU, Storage, GPU allocation**

Extend operator limits to all resource types.

---

## 12. Example Walkthrough

Node: 2 cores, 10 GB RAM, 200 GB storage. Memory allocation: 80%.

### After registration (no VMs yet)

```
TotalResources:    { 10 pts, 8.3 GB, 465 GB }
UsedResources:     { 0, 0, 0 }
ReservedResources: { 0, 0, 0 }
Available:         { 10 pts, 8.3 GB, 465 GB }
```

### After first heartbeat (3 system VMs deployed)

```
UsedResources:     { 3 pts, 1.25 GB, 14 GB }   -- from heartbeat
Available:         { 7 pts, 7.05 GB, 451 GB }
```

### Tenant VM scheduled (during deployment)

```
ReservedResources: { 2 pts, 4 GB, 20 GB }       -- transient hold
Available:         { 5 pts, 3.05 GB, 431 GB }
```

### Next heartbeat (tenant VM running, hold released)

```
UsedResources:     { 5 pts, 5.25 GB, 34 GB }   -- includes new VM
ReservedResources: { 0, 0, 0 }                  -- hold released
Available:         { 5 pts, 3.05 GB, 431 GB }   -- same result, clean state
```

---

*For related context, see:*
- *`docs/NODE-LIFECYCLE.md` -- settings lifecycle, configure/register/login flow*
- *`PROJECT_FEATURES.md` S9 -- resource management overview*
- *`docs/SCHEDULING.md` -- constraint vocabulary, tier configuration*
