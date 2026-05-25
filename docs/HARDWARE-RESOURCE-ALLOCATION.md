# Hardware Management & Resource Allocation

**Scope:** DeCloud NodeAgent + Orchestrator  
**Updated:** 2026-05

---

## 1. Overview

Each node operator runs the DeCloud Node Agent on their hardware. The agent
discovers the physical hardware, reports it to the orchestrator, and enforces
the operator's configured allocation limits. The orchestrator uses these
figures for scheduling, billing, and marketplace listing.

The system tracks four distinct resource layers on the orchestrator side:

```
TotalResources        — physical capacity (set at evaluate time)
AllocationConfig      — operator percentages (set at allocate time)
AllocatedResources    — concrete ceiling = TotalResources × percentages
ReservedResources     — transient scheduling holds (released at first heartbeat)
UsedResources         — ground truth from heartbeat-reported VMs
```

Available capacity for a new VM:
```
available = AllocatedResources - UsedResources - ReservedResources
```

---

## 2. Hardware Discovery

### 2.1 Agent-side discovery (`ResourceDiscoveryService`)

`DiscoverAllAsync()` runs at startup and is cached for one hour. It collects:

| Resource | Source |
|---|---|
| CPU | `lscpu` / `Get-CimInstance Win32_Processor` |
| Memory | `/proc/meminfo` / WMI |
| Storage | `df -B1` / PowerShell `Get-Volume` |
| GPU | `nvidia-smi --query-gpu=...` + sysfs IOMMU detection |
| Network | `ip addr`, `iperf3`, ipify.org |
| KVM | `/dev/kvm` existence |

`GetCurrentSnapshotAsync()` is the fast path called every heartbeat. It skips
the CPU benchmark and returns a `ResourceSnapshot` from cached hardware data
and live nvidia-smi output.

### 2.2 GPU detection specifics

Detection sets per-GPU flags on `GpuInfo`:

- `IsIommuEnabled` — IOMMU group exists at
  `/sys/bus/pci/devices/<pci_addr>/iommu_group`
- `IsAvailableForPassthrough` — IOMMU enabled AND `vfio-pci` kernel module
  loaded (`/sys/module/vfio_pci`)
- `IsAvailableForProxiedSharing` — GPU is NOT reserved for passthrough AND
  host is not Windows. Set per-GPU independently — a mixed node with one
  IOMMU-capable GPU and one without correctly marks the second for proxy use.
- `IsAvailableForContainerSharing` — Docker daemon + NVIDIA Container Toolkit
  detected

`SupportsGpuProxy` (node-level flag) = `gpus.Any(g => g.IsAvailableForProxiedSharing)`
and is stored on `HardwareInventory`. `GpuProxyService.EnsureStartedAsync` reads
this flag to decide whether to start the daemon.

The snapshot also carries two boolean convenience fields derived from
per-GPU discovery results:

- `SupportsGpuProxy` — any GPU has `IsAvailableForProxiedSharing = true`
- `SupportsGpuPassthrough` — any GPU has `IsAvailableForPassthrough = true`

The CLI reads these from `GET /api/node/gpus` for the `--gpu-count` and
`--gpu-vram-percent` pre-flight guards.

---

## 3. Agent Allocation Configuration

Operators set allocation limits in `/etc/decloud/settings.json` via
`decloud configure`. The agent reads these at startup in
`NodeMetadataService.InitializeAsync()`.

### 3.1 Configuration keys

```
resources:cpu:mode          = "percent" | "points"
resources:cpu:value         = <int>
resources:memory:mode       = "percent" | "mb"
resources:memory:value      = <int>
resources:storage:mode      = "percent" | "mb"
resources:storage:value     = <int>
resources:gpu:count         = <int>      (Passthrough only; absent = all GPUs)
resources:gpu:vram:mode     = "percent"  (Proxied only)
resources:gpu:vram:value    = <int>      (1–95; absent = 100% of proxy-eligible VRAM)
```

**Mode constraints:**
- `resources:gpu:count` is only meaningful for Passthrough nodes (IOMMU + vfio-pci).
  Proxied nodes share one physical GPU across VMs; the scheduling unit is VRAM,
  not GPU count. The correct Proxied control is `resources:gpu:vram`.
- `resources:gpu:vram` is only meaningful for Proxied nodes. Passthrough nodes
  dedicate an entire GPU per VM; there is no shared VRAM pool to partition.

### 3.2 Resolution sequence

For **percent** and **mb** modes the concrete byte/point value cannot be known
until hardware is discovered. `NodeMetadataService` stores the mode and value;
`ResolveAllocatedMemory()` / `ResolveAllocatedStorage()` / `ResolveAllocatedCpu()`
/ `ResolveAllocatedGpuVram()` are called from `UpdateInventory()` after
`DiscoverAllAsync()` completes. Each method guards with `if (HasValue) return`
— once a value is set it is not re-derived.

`ResolveAllocatedGpuVram(HardwareInventory)` sums `MemoryBytes` for all GPUs
with `IsAvailableForProxiedSharing = true`, then applies the configured percent:

```
AllocatedGpuVramBytes = totalProxiedVram × (gpuVramPercent / 100)
```

If not configured, `AllocatedGpuVramBytes` remains null and the orchestrator
treats the full proxy-eligible VRAM as the pool.

### 3.3 Persisted orchestrator resolution (`/etc/decloud/allocation-resolved.json`)

After every successful `decloud allocate`, the orchestrator confirms concrete
byte values. `NodeMetadataService.UpdateFromOrchestratorResolutionAsync()`
writes them atomically to `/etc/decloud/allocation-resolved.json`:

```json
{
  "resolvedAt": "2026-05-23T18:24:10Z",
  "computePoints": 9,
  "memoryBytes": 6621877248,
  "storageBytes": 165604884787,
  "gpuVramBytes": 6871097344,
  "cpuPercent": 0.95,
  "memoryPercent": 0.80,
  "storagePercent": 0.80,
  "gpuVramPercent": 0.80
}
```

`LoadResolvedAllocationAsync()` loads this file at the end of `InitializeAsync()`
— before hardware discovery fires — so `AllocatedMemoryBytes`,
`AllocatedStorageBytes`, `AllocatedComputePoints`, and `AllocatedGpuVramBytes`
are seeded from the orchestrator-confirmed values from the previous session.
The `Resolve*` guards then skip re-derivation.

`AllocationResolvedAt` (DateTime?) is set on the service when the file is loaded
or written; null means settings-derived only.

---

## 4. Node Lifecycle on the Orchestrator

```
register → evaluate → allocate → login → [heartbeat loop]
```

### 4.1 Register

Node sends `HardwareInventory` and `AllocatedResources` (operator percentages,
including `GpuVramPercent` when configured). `TotalResources` is zeroed — not
yet known until evaluate. `SchedulingReady = false` by default.

### 4.2 Evaluate

`NodeSelfController.EvaluateAsync()`:
- Runs sysbench benchmark, scores to compute points
- Sets `node.TotalResources` from inventory + evaluation, including:
  ```
  TotalResources.AllocatedGpuVramBytes = Σ GpuInfo.MemoryBytes
                                         where IsAvailableForProxiedSharing
  ```
  This is the physical proxy-eligible VRAM baseline used by `AllocateNodeAsync`
  to resolve `GpuVramPercent` into a concrete byte ceiling.
- Resolves `node.AllocatedResources = TotalResources × AllocationConfig percentages`
- Seeds system VM obligations (Relay, DHT, BlockStore)

### 4.3 Allocate

`NodeService.AllocateNodeAsync()`:
- Accepts updated percentages from operator, including `GpuVramPercent`
- Enforces GPU allocation mode guards before storing:
  - `GpuCount` rejected if node has no passthrough-capable GPUs
    (`IsAvailableForPassthrough = false` on all GPUs)
  - `GpuVramPercent` rejected if node has no proxy-eligible GPUs
    (`IsAvailableForProxiedSharing = false` on all GPUs)
- Stores percentages in `node.AllocationConfig`
- Resolves concrete `node.AllocatedResources.AllocatedGpuVramBytes`:
  ```
  totalProxiedVram = TotalResources.AllocatedGpuVramBytes
                     (falls back to inventory sum for pre-feature nodes)
  AllocatedGpuVramBytes = totalProxiedVram × GpuVramPercent
                          (or totalProxiedVram when GpuVramPercent is null = 100%)
  ```
- Returns `NodeAllocateResponse` with `ResolvedComputePoints`,
  `ResolvedMemoryBytes`, `ResolvedStorageBytes`, `ResolvedGpuVramBytes`,
  `EffectiveGpuVramPercent`

Agent receives the response and calls `UpdateFromOrchestratorResolutionAsync()`
to persist locally.

### 4.4 Login

`NodeService.LoginNodeAsync()`:
- Validates `UsedResources ≤ AllocatedResources` (prevents login while over-provisioned)
- Sets `node.SchedulingReady = true`

### 4.5 Heartbeat (`/api/nodes/{id}/heartbeat`)

Every ~30 seconds the agent sends:
- Current `ResourceSnapshot` (CPU%, RAM usage, storage usage, GPU VRAM)
- List of active VMs with specs (`HeartbeatVmInfo`, includes `GpuMode` and `GpuVramBytes`)

Orchestrator `ProcessHeartbeatAsync()`:
1. Updates `node.UsedResources` from the VM list sum:
   - `ComputePoints` = Σ `ComputePointCost`
   - `MemoryBytes` = Σ `MemoryBytes`
   - `StorageBytes` = Σ `DiskBytes`
   - `AllocatedGpuVramBytes` = Σ `GpuVramBytes` for Proxied VMs
2. Reconciles scheduling holds: any VM now in the heartbeat that was
   still in `ReservedResources` (Provisioning state) has its hold released
3. Saves node, updates uptime metrics

---

## 5. Agent Resource Snapshot (`GET /api/node/snapshot`)

`ResourceSnapshot` (agent model, `NodeModels.cs`) is built by
`GetCurrentSnapshotAsync()` on every heartbeat call:

```
TotalPhysicalCores / TotalVirtualCpuCores / UsedVirtualCpuCores
VirtualCpuUsagePercent
TotalComputePoints    — effectiveComputePoints (AllocatedComputePoints or % of evaluated)
UsedComputePoints     — Σ ComputePointCost from VmRepository (non-deleted VMs)
TotalMemoryBytes / AllocatedMemoryBytes / UsedMemoryBytes
TotalStorageBytes / AllocatedStorageBytes / UsedStorageBytes
TotalGpus / AllocatedGpus / UsedGpus
TotalGpuVramBytes / AllocatedGpuVramBytes / UsedGpuVramBytes
SupportsGpuProxy       — any GPU has IsAvailableForProxiedSharing = true
SupportsGpuPassthrough — any GPU has IsAvailableForPassthrough = true
```

`AllocatedStorageBytes` and `AllocatedMemoryBytes` come from
`NodeMetadataService` (operator config, resolved from file or settings).
`AllocatedGpuVramBytes` = passthrough VRAM (full GPU per assigned PCI address)
+ proxied VRAM (Σ `GpuVramBytes` quotas for active proxied VMs).
`UsedGpuVramBytes` = live from nvidia-smi `memory.used`.
`SupportsGpuProxy` and `SupportsGpuPassthrough` are used by the `decloud` CLI
for the `--gpu-vram-percent` and `--gpu-count` pre-flight validation guards.

### 5.1 Allocation endpoint (`GET /api/node/allocation`)

Dedicated endpoint returning the operator-configured allocation in a structured
form for the dashboard and `decloud allocate --show`:

```json
{
  "cpuPercent": 0.95,
  "memoryPercent": 0.80,
  "storagePercent": 0.80,
  "gpuCount": null,
  "effectiveGpuVramPercent": 0.80,
  "resolvedComputePoints": 9,
  "resolvedMemoryBytes": 6621877248,
  "resolvedStorageBytes": 165604884787,
  "resolvedGpuVramBytes": 6871097344,
  "resolvedAt": "2026-05-23T18:24:10Z",
  "source": "orchestrator"
}
```

`source` is `"orchestrator"` when values come from the persisted cache;
`"settings"` when locally derived. Polled every 10 seconds by the dashboard.
`effectiveGpuVramPercent` is null when not configured (implying 100%).
`resolvedGpuVramBytes` is 0 when no proxy-eligible GPUs are detected.

---

## 6. Scheduling Resource Checks

`VmSchedulingService.ApplyHardFiltersAsync()` enforces hard limits before
placing a VM. Resource-related filters:

| Filter | Check |
|---|---|
| FILTER 2 | Tier eligibility (benchmark score) |
| FILTER 5 | GPU mode requirements (see §7) |
| FILTER 7 | Minimum free memory: `(AllocatedResources.MemoryBytes - UsedResources.MemoryBytes - ReservedResources.MemoryBytes) / MB ≥ MinFreeMemoryMb` |

### 6.1 Scheduling holds (`ReservedResources`)

When a VM is scheduled (`VmService.TryScheduleVmAsync` STEP 3), resources are
immediately added to `node.ReservedResources` before the CreateVm command is
dispatched:

```csharp
selectedNode.ReservedResources.ComputePoints += pointCost;
selectedNode.ReservedResources.MemoryBytes   += vm.Spec.MemoryBytes;
selectedNode.ReservedResources.StorageBytes  += vm.Spec.DiskBytes;
// For Proxied GPU VMs:
selectedNode.ReservedResources.AllocatedGpuVramBytes += vm.Spec.GpuVramBytes.Value;
```

Holds are released in two ways:
1. `ReleaseSchedulingHoldAsync()` — called when VM exits Provisioning state
   (→ Running, Error, or Deleting)
2. `FreeNodeResourcesAsync()` — safety-net on VM deletion (Math.Max guards
   make double-subtraction safe)
3. Heartbeat reconciliation — any VM now in UsedResources has its hold
   released automatically

---

## 7. GPU Resource Management

### 7.1 GPU modes

| Mode | Description | Requirements |
|---|---|---|
| `None` (0) | No GPU access | — |
| `Passthrough` (1) | Dedicated GPU via VFIO | IOMMU + vfio-pci module |
| `Proxied` (2) | Shared GPU via proxy daemon | Any GPU present (no IOMMU needed) |

### 7.2 Passthrough mode

**Scheduling (orchestrator):**
- FILTER 5 checks `HasIommuCapableGpu` and `HasPassthroughCapableGpu`
- `VmService` STEP 7 assigns a specific PCI address from inventory

**Agent:**
- `CommandProcessorService._assignedGpus` (`ConcurrentDictionary<PciAddress, VmId>`)
  prevents concurrent double-assignment
- Hydrated from `VmRepository` on startup via
  `IResourceDiscoveryService.GetActivePassthroughAssignments()`
- GPU released on VM stop, delete, or failure (`CommandProcessorService`,
  `LibvirtVmManager`)

**VRAM accounting:**
- `AllocatedGpuVramBytes` += full `GpuInfo.MemoryBytes` of the assigned GPU
- Released when the passthrough GPU assignment is removed

### 7.3 Proxied mode

**Architecture:**

```
Guest VM                          Host
┌─────────────────────┐           ┌───────────────────────────┐
│  Application        │           │  gpu-proxy-daemon         │
│  ↓ LD_PRELOAD       │  vsock /  │  ├── handle_malloc        │
│  libdecloud_cuda_   │◄─────────►│  │   (quota enforcement)  │
│  shim.so            │  TCP:9999 │  ├── handle_launch_kernel │
│  (CUDA interceptor) │           │  └── handle_memcpy        │
│  ↓                  │           │       ↓                   │
│  libcuda_driver_    │           │  Real CUDA Driver / GPU   │
│  shim.so            │           └───────────────────────────┘
└─────────────────────┘
```

Transport: virtio-vsock primary (bare metal), TCP fallback (WSL2).

**Scheduling (orchestrator):**
- FILTER 5 checks `HasProxiedCapableGpu` (any GPU with
  `IsAvailableForProxiedSharing = true`)
- VRAM headroom check when `spec.GpuVramBytes > 0`:
  ```
  totalProxiedVram = node.AllocatedResources.AllocatedGpuVramBytes
                     (operator ceiling; falls back to physical inventory
                      sum for nodes evaluated before GpuVramPercent support)
  committedVram    = UsedResources.AllocatedGpuVramBytes
                   + ReservedResources.AllocatedGpuVramBytes
  available        = totalProxiedVram - committedVram ≥ spec.GpuVramBytes
  ```
  Using `AllocatedResources.AllocatedGpuVramBytes` as the pool respects the
  operator's configured VRAM ceiling. Nodes that have not run `decloud allocate`
  since the VRAM percent feature was deployed fall back to the physical sum.

**VM creation flow (agent, `LibvirtVmManager.CreateVmAsync`):**

1. Generate `GpuProxyToken` (32-byte hex)
2. Write token to `GpuProxyService.TokenFile`
   (`/var/lib/decloud/gpu-proxy-tokens`):
   ```
   <token> <vm_id> [<quota_bytes>]
   ```
   Third field present when `GpuVramBytes > 0`.
3. SIGHUP daemon to reload token file
4. Assign `VsockCid` (incrementing from 3; skipped on WSL2)
5. Inject CUDA shim, NVML shim, transport env vars into cloud-init
   (`EnsureGpuProxyShim`)
6. Ensure daemon is running (`GpuProxyService.EnsureStartedAsync`)

**VRAM quota enforcement (daemon):**

When the guest shim connects and sends `GPU_CMD_HELLO`, the daemon:
1. Validates the auth token against the registry
2. Reads `quota_bytes` from the token entry
3. Sets `ctx->memory_quota = quota_bytes` immediately — before the HELLO
   response is sent, so the first `cudaMalloc` is already subject to enforcement

`handle_malloc` then enforces: if
`memory_allocated + req.size > memory_quota`, returns
`cudaErrorMemoryAllocation`. The tenant cannot override this — the quota is
set by the host from the token registry before any CUDA call is processed.

**VRAM accounting (agent snapshot):**

`GetGpuUsageFromRepository()` does one VmRepository query:
- Passthrough VRAM = Σ `GpuInfo.MemoryBytes` for GPUs whose PCI address
  appears in active Passthrough VMs
- Proxied VRAM = Σ `VmSpec.GpuVramBytes` for active Proxied VMs with
  non-zero quota

`AllocatedGpuVramBytes = passthroughVram + proxiedVram` in the snapshot.

**GPU proxy session cleanup (`GpuProxyService.CleanupGpuProxySessionAsync`):**

Called on VM stop, delete, and pause with different `removeToken` semantics:

| Event | removeToken | Token removed | SIGHUP | TCP force-close |
|---|---|---|---|---|
| Stop | true | ✓ | ✓ | ✓ |
| Delete | true | ✓ | ✓ | ✓ |
| Pause | false | ✗ | ✗ | ✓ |

Pause keeps the token so `ResumeVmAsync` can reconnect with the same
baked-in cloud-init token. TCP force-close uses `ss -K sport = :9999`.
Stop removes the token because a stopped proxied VM cannot resume GPU proxy
access without recreation (the token is baked into cloud-init at create time).

### 7.4 GPU proxy daemon

**Binary:** `/usr/local/bin/gpu-proxy-daemon`  
**Token registry:** `/var/lib/decloud/gpu-proxy-tokens`  
**Port:** 9999 (vsock and TCP)  
**Lifecycle:** Managed by `GpuProxyService` — auto-starts when
`HardwareInventory.SupportsGpuProxy` is true, health-checked every 10 seconds
with exponential-backoff restart (max 5 attempts).

---

## 8. GPU Billing

`VmService.CalculateHourlyRate()` computes the GPU cost component:

```
GpuMode.Passthrough:
  gpuCost = max(operator.GpuPerHour, platform.FloorGpuPerHour)

GpuMode.Proxied (GpuVramBytes > 0):
  gpuCost = (GpuVramBytes / GB) ×
            max(operator.GpuVramPerGbPerHour, platform.FloorGpuVramPerGbPerHour)

GpuMode.None or Proxied without quota:
  gpuCost = 0
```

GPU cost is added to `computeCost` outside the `tierMultiplier` — GPU VRAM
is not overcommitted and must not be amplified by quality tier ratios.

**Default platform rates (`appsettings.json`):**

```json
"FloorGpuPerHour": 0.05,
"DefaultGpuPerHour": 0.10,
"FloorGpuVramPerGbPerHour": 0.003,
"DefaultGpuVramPerGbPerHour": 0.006
```

The same rate constants are mirrored in the frontend (`marketplace.js`,
`app.js`, `template-detail.js`) for live cost estimates in the node card
and VM creation modals.

---

## 9. Marketplace GPU Visibility

`NodeCapabilities` exposes GPU fields for marketplace browsing:

```
HasGpu                  — node has any GPU
GpuModel / GpuCount / GpuMemoryBytes — first GPU details
SupportsProxiedGpu      — HasProxiedCapableGpu (per-GPU flag)
TotalGpuVramBytes       — Σ MemoryBytes all GPUs
AvailableGpuVramBytes   — proxied VRAM not yet committed to running/scheduled VMs
GpuVramPerGbPerHour     — effective Proxied rate (floor-clamped)
```

`NodeSearchCriteria.MinAvailableGpuVramBytes` filters to nodes with
sufficient free VRAM for a Proxied workload before scheduling is attempted.

`AvailableGpuVramBytes` is computed in `ToAdvertisement`:
```
AvailableGpuVramBytes = Σ MemoryBytes (IsAvailableForProxiedSharing GPUs)
                      - UsedResources.AllocatedGpuVramBytes
                      - ReservedResources.AllocatedGpuVramBytes
```

---

## 10. CLI — GPU Allocation Commands

### 10.1 `decloud allocate`

```bash
sudo decloud allocate [options]

  --gpu-count <N>             GPUs to offer (Passthrough nodes only; 0 = none)
  --gpu-vram-percent <1-95>   Proxy-eligible VRAM percent to offer (Proxied nodes only)
  --show                      Show current allocation with resolved capacity
```

**Mode guards (enforced at CLI and orchestrator):**

| Flag | Valid for | Rejected on |
|---|---|---|
| `--gpu-count` | Passthrough | Nodes with no `IsAvailableForPassthrough` GPU |
| `--gpu-vram-percent` | Proxied | Nodes with no `IsAvailableForProxiedSharing` GPU |

The CLI queries `GET /api/node/gpus` for the pre-flight check. If the agent
is not running the check degrades gracefully and the orchestrator enforces
the constraint as a hard boundary.

**`--show` output includes GPU VRAM when present:**

```
Orchestrator-Resolved Capacity (confirmed 2026-05-23T18:24:10Z):
  Compute:   9 pts
  Memory:    6.2 GB
  Storage:   154.3 GB
  GPU VRAM:  6.4 GB (80%)
```

`GPU VRAM` line is omitted when `resolvedGpuVramBytes = 0` (no proxy-eligible
GPUs or GPU VRAM not configured).

### 10.2 `decloud resources`

Live snapshot section includes GPU VRAM:

```
  GPUs:     1 / 1 offered
  GPU VRAM: 8.0 GB free / 0 B committed / 8.0 GB physical
```

Fields read from `GET /api/node/snapshot`:
`totalGpuVramBytes`, `allocatedGpuVramBytes`, `availableGpuVramBytes`.
The GPU VRAM line is omitted when `totalGpuVramBytes = 0`.

---

## 11. Dashboard and UI Surfaces

### 11.1 Agent dashboard (`dashboard.js`)

Fetches every 10 seconds:

| Endpoint | Data |
|---|---|
| `GET /api/node/snapshot` | Live metrics (CPU%, RAM, storage, GPU counts, VRAM) |
| `GET /api/node/allocation` | Operator-confirmed allocation ceilings |
| `GET /api/vms` | Active VMs (used for VM-spec usage sums) |

**Hardware section** (`renderHardware`): live usage bars from the snapshot.

**Resource Allocation GPU card** — mirrors the Memory and Storage cards:
- Mode badge (green = Proxied, blue = Passthrough) from `snap.supportsGpuProxy`
- **Proxied:** `renderAllocCard` with `resolvedGpuVramBytes` (operator ceiling),
  `totalGpuVramBytes` (physical), `allocatedGpuVramBytes` (committed). Displays:
  `X% of VRAM | Y GB used / Z GB allocated / W GB physical`
- **Passthrough:** bar shows GPU slot usage (assigned / total); detail shows
  `N assigned · M free`
- Secondary line: passthrough + proxied VM counts breakdown

### 11.2 Orchestrator marketplace node card

The GPU section of the node card (`marketplace.js`) shows:

- **Spec row** (`GPU` column): last word of GPU model name + mode badge
  (green Proxied / blue Passthrough)
- **GPU highlight block**: model name, mode badge, `X.X GB free / Y GB total`,
  `$Z.ZZZ/GB·h` rate (shown when `GpuVramPerGbPerHour > 0`), VRAM usage bar
- **Node detail panel**: mode badge in the GPU label, compact
  `free / total GB VRAM · $rate/GB·h` appended after model name

### 11.3 Template Deploy modal

GPU Access section is shown when `template.requiresGpu = true` or
`template.defaultGpuMode ≠ None`. Pre-populated from the template spec:

- **GPU Mode** dropdown: None / Proxied (shared VRAM) / Passthrough (dedicated GPU)
- **VRAM (GB)** input: shown for Proxied only; pre-filled from
  `recommendedSpec.gpuVramBytes` or defaults to 4 GB
- Estimated cost includes GPU component (same rates as server-side billing)

Submitted as `gpuMode` (integer) and `gpuVramBytes` (bytes) in the deploy payload.
`requiresGpu` is derived from the selected mode (`gpuMode !== 0`) rather than the
template flag, so a user choosing None on a GPU template correctly results in no
GPU scheduling constraint.

### 11.4 Create VM modal

GPU Access section is always visible (user opts in by selecting a mode):

- **GPU Mode** dropdown: None (default) / Proxied / Passthrough
- **VRAM (GB)** input: shown for Proxied only
- Estimated cost updates live as mode or VRAM changes
- Fields reset to None on successful VM creation

Submitted as `gpuMode` and `gpuVramBytes` in `POST /api/vms`.

---

## 12. Key Files Reference

### NodeAgent

| File | Responsibility |
|---|---|
| `ResourceDiscoveryService.cs` | Hardware discovery, snapshot building, GPU usage query |
| `NodeMetadataService.cs` | Allocation config, persisted resolution cache, `ResolveAllocatedGpuVram` |
| `NodeController.cs` | `/api/node/snapshot`, `/api/node/allocation`, `/api/node/gpus` |
| `CommandProcessorService.cs` | Passthrough GPU pool, VM command dispatch |
| `LibvirtVmManager.cs` | VM lifecycle, GPU proxy token registry, vsock CID assignment |
| `GpuProxyService.cs` | Daemon lifecycle, `CleanupGpuProxySessionAsync`, `TokenFile` |
| `HeartbeatService.cs` | Snapshot + VM summaries (incl. `GpuMode`, `GpuVramBytes`) |
| `NodeModels.cs` | `ResourceSnapshot` (agent), `VmSummary` |
| `HardwareInventory.cs` | `GpuInfo`, `HardwareInventory` (agent model) |
| `dashboard.js` | Frontend resource rendering, allocation display, GPU card |
| `cli/decloud` | `decloud allocate`, `decloud resources`, GPU mode guards |

### Orchestrator

| File | Responsibility |
|---|---|
| `NodeService.cs` | Heartbeat processing, `UsedResources` computation, `ToAdvertisement` |
| `NodeSelfController.cs` | `EvaluateAsync` — sets `TotalResources.AllocatedGpuVramBytes` |
| `NodeMarketplaceService.cs` | Marketplace search, pricing floor enforcement |
| `VmService.cs` | VM scheduling, resource reservation, `CalculateHourlyRate` |
| `VmLifecycleManager.cs` | Hold release on state transitions |
| `VmSchedulingService.cs` | Hard filters (FILTER 5 GPU + VRAM headroom, FILTER 7 memory) |
| `NodeCapacityCalculator.cs` | Tier-aware capacity with overcommit ratios |
| `PricingConfig.cs` | Floor and default rates including `GpuVramPerGbPerHour` |
| `Node.cs` | `ResourceSnapshot` (orchestrator), `HardwareInventory`, `HeartbeatVmInfo` |
| `NodeMarketplace.cs` | `NodeCapabilities`, `NodeSearchCriteria` |
| `AllocationConfig.cs` | `GpuVramPercent` operator config field |
| `marketplace.js` | Node card GPU mode badge, VRAM bar, rate display |
| `app.js` | Create VM modal GPU fields, cost estimate |
| `template-detail.js` | Template deploy modal GPU fields, cost estimate |

### GPU Proxy

| File | Responsibility |
|---|---|
| `gpu_proxy_daemon.c` | Host daemon — executes CUDA on behalf of VMs, enforces VRAM quota |
| `shim/cuda_shim.c` | Guest CUDA Runtime API interceptor |
| `shim/cuda_driver_shim.c` | Guest CUDA Driver API interceptor |
| `shim/nvml_shim.c` | Guest NVML (GPU monitoring) interceptor |
| `proto/gpu_proxy_proto.h` | Wire protocol — 35+ commands, 16-byte header |
