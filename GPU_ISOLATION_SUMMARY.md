# GPU Isolation & Passthrough — Architecture Summary Card

**Date:** 2026-02-22
**Status:** Design Phase — Needs Implementation
**Context:** Continuation of conversation about GPU workload isolation for heterogeneous node environments

---

## The Problem

DeCloud currently supports two GPU deployment modes:
1. **VFIO Passthrough** (bare-metal Linux with IOMMU) — Full VM isolation, tenant gets a real GPU device
2. **Docker Container** (`--gpus all`, for nodes without IOMMU, e.g. WSL2) — GPU access but weak isolation

The container fallback (mode 2) exposes the node agent to risk. A malicious or buggy tenant container could escape and compromise the node agent, which holds credentials, wallet keys, and network access. **There is no VM-level isolation boundary for GPU workloads on nodes without IOMMU.**

### Why This Matters
- WSL2 nodes (Windows users contributing GPU) cannot do VFIO — no IOMMU exposed
- Cloud VMs (nested virtualization) often lack IOMMU
- These are likely a large portion of early GPU contributors (gaming PCs, home labs)
- Without isolation, the platform can't safely run untrusted GPU workloads on these nodes

---

## Current Implementation

### Models (Orchestrator)

**`VmSpec`** (`src/Orchestrator/Models/VirtualMachine.cs:157-171`):
```csharp
public bool RequiresGpu { get; set; }
public string? GpuModel { get; set; }
public DeploymentMode DeploymentMode { get; set; } = DeploymentMode.VirtualMachine;
public string? ContainerImage { get; set; }
```

**`DeploymentMode`** (`src/Orchestrator/Models/VirtualMachine.cs:361-365`):
```csharp
public enum DeploymentMode
{
    VirtualMachine = 0,  // KVM/QEMU via libvirt (default, full isolation)
    Container = 1        // Docker container with GPU sharing (WSL2, no IOMMU)
}
```

**`GpuInfo`** (`src/Orchestrator/Models/Node.cs:248-272`):
```csharp
public class GpuInfo
{
    public string Vendor { get; set; }           // NVIDIA, AMD, Intel
    public string Model { get; set; }            // e.g., RTX 4090
    public string PciAddress { get; set; }       // e.g., 0000:01:00.0
    public long MemoryBytes { get; set; }
    public bool IsIommuEnabled { get; set; }
    public bool IsAvailableForPassthrough { get; set; }
    public bool IsAvailableForContainerSharing { get; set; }
    // ... utilization metrics
}
```

**`HardwareInventory`** (`src/Orchestrator/Models/Node.cs:166-186`):
```csharp
public bool SupportsGpu { get; set; }
public List<GpuInfo> Gpus { get; set; }
public List<string> ContainerRuntimes { get; set; }  // "docker", "podman"
public bool SupportsGpuContainers { get; set; }      // Docker + NVIDIA Container Toolkit
```

**`VmTemplate`** (`src/Orchestrator/Models/VmTemplate.cs:120-135`):
```csharp
public bool RequiresGpu { get; set; }
public string? GpuRequirement { get; set; }    // "RTX 3060 or better"
public string? ContainerImage { get; set; }     // "ollama/ollama:latest"
```

### Scheduler (Orchestrator)

**`VmSchedulingService`** (`src/Orchestrator/Services/VmScheduling/VmSchedulingService.cs:395-410`):
- FILTER 5: GPU Requirement
- Accepts nodes with EITHER `IsAvailableForPassthrough` OR `IsAvailableForContainerSharing`
- Does NOT differentiate isolation level when scoring — treats both as equally valid

### Deployment Logic (Orchestrator)

**`VmService.CreateVmAsync`** (`src/Orchestrator/Services/VmService.cs:840-882`):
```
STEP 7: Resolve GPU and deployment mode
├─ If node has VFIO-capable GPU → DeploymentMode.VirtualMachine, attach PCI address
├─ Else if node supports GPU containers → DeploymentMode.Container (Docker --gpus)
└─ Else → Warning logged, no GPU assigned
```

The deployment mode is sent to the node agent as part of the CreateVm command payload.

### Node Agent

The node agent receives the command and either:
- Creates a KVM VM with VFIO GPU passthrough (VirtualMachine mode)
- Runs a Docker container with `--gpus all` (Container mode)

**Current Container mode has NO VM isolation layer** — the container runs directly on the host alongside the node agent.

---

## Explored Solutions

### Solution 1: Harden Docker Isolation (Quick Win, Partial)

Run Docker in Container mode with security hardening:
- Rootless Docker (unprivileged daemon)
- `--security-opt no-new-privileges`, drop all capabilities
- Read-only root filesystem, no host mounts
- Dedicated network namespace (no access to node agent)
- Separate user namespace for node agent

**Verdict:** Reduces risk but is NOT equivalent to VM isolation. Container escapes (runc CVEs) remain a threat vector. Acceptable for low-risk / development scenarios.

### Solution 2: VM-Inside-Docker (Recommended Architecture)

**The key insight:** Instead of passing GPU into a VM (requires VFIO/IOMMU), run the VM *inside* a GPU-enabled Docker container.

```
Host (WSL2 or bare-metal without IOMMU)
 └─ Docker Container (--gpus all, --device /dev/kvm)
      ├─ QEMU/KVM VM (tenant sandbox, no GPU device)
      │    └─ Tenant workload runs here (isolated)
      │    └─ Communicates with GPU proxy via virtio-vsock
      └─ GPU Proxy Daemon (has real CUDA access)
           └─ Receives compute requests from VM via vsock
           └─ Executes on real GPU
           └─ Returns results
```

**Why this works:**
- Docker gets GPU access from host (GPU-PV on WSL2, native on Linux)
- Docker gets KVM access (`--device /dev/kvm`, nested virt on WSL2)
- VM inside container provides hard isolation boundary
- GPU proxy bridges the gap — VM doesn't need a GPU device
- Works identically on WSL2 and bare-metal Linux
- Ships as a single Docker image (QEMU + GPU proxy)

### GPU Proxy Architecture (General-Purpose)

The proxy must support **any CUDA workload**, not just inference. Three interception levels:

```
Tenant Code (PyTorch, custom CUDA, rendering, training, etc.)
     │
     ▼
┌─────────────────────────────────────────┐
│ Level 3: CUDA Runtime API (libcudart)   │ ← Easiest, misses raw CUDA
├─────────────────────────────────────────┤
│ Level 2: CUDA Driver API (libcuda)      │ ← Good coverage (~95%)
├─────────────────────────────────────────┤
│ Level 1: Kernel ioctl (/dev/nvidia*)    │ ← Full transparency, hardest
└─────────────────────────────────────────┘
```

**Phased approach:**

| Phase | Strategy | Coverage | Effort |
|-------|----------|----------|--------|
| Phase 1 | LD_PRELOAD shim intercepting CUDA Driver API over virtio-vsock | ~95% of workloads | Medium |
| Phase 2 | Resource controls in proxy (memory quotas, kernel timeouts, metering) | N/A (policy layer) | Low |
| Phase 3 | ioctl-level proxy (reference: gVisor nvproxy) for full transparency | 100% of workloads | High |

**Reference implementations:**
- gVisor nvproxy (Google) — ioctl-level `/dev/nvidia*` proxy, production-grade, open source
- rCUDA — CUDA Driver API remoting over TCP, academic, proven concept
- GVirtuS — CUDA Runtime API remoting, academic

### Performance Expectations

| Operation | Overhead | Notes |
|-----------|----------|-------|
| CUDA kernel launches | ~microseconds | Just parameter serialization over vsock |
| Small memcpy (<1MB) | ~10-50μs | vsock serialization |
| Large memcpy (>100MB) | Significant | Optimize with shared memory (virtio-pmem) |
| LLM inference steady-state | Near zero | Weights loaded once, then just token I/O |
| Training (gradient sync) | 5-15% | Dominated by memory transfers |

---

## Proposed New Deployment Mode

Add a third deployment mode: **ContainerWithVm** (or `HybridIsolated`)

```csharp
public enum DeploymentMode
{
    VirtualMachine = 0,        // KVM/QEMU with VFIO GPU passthrough (bare-metal + IOMMU)
    Container = 1,             // Docker --gpus (legacy, weak isolation)
    ContainerWithVm = 2        // Docker --gpus + KVM VM inside + GPU proxy (strong isolation, no IOMMU)
}
```

### Decision Matrix (Scheduler)

```
Node has IOMMU + VFIO-capable GPU?
  ├─ YES → DeploymentMode.VirtualMachine (best: native GPU in VM)
  └─ NO → Node has KVM + Docker + NVIDIA Container Toolkit?
       ├─ YES → DeploymentMode.ContainerWithVm (good: VM isolation + GPU proxy)
       └─ NO → DeploymentMode.Container (fallback: Docker only, reduced isolation)
```

### What the Orchestrator Needs to Track Per-Node

New fields on `HardwareInventory` or `GpuInfo`:
```csharp
public bool SupportsNestedVirtualization { get; set; }  // /dev/kvm accessible
public bool SupportsGpuProxy { get; set; }               // GPU proxy image available
public string? GpuProxyVersion { get; set; }              // Installed proxy version
```

---

## Implementation Roadmap

### Milestone 1: Infrastructure (Foundation)
- [ ] Design virtio-vsock communication protocol between VM and GPU proxy
- [ ] Build GPU proxy daemon (CUDA Driver API shim, ~40-50 functions)
- [ ] Build matching LD_PRELOAD shim for inside the VM
- [ ] Package as Docker image: QEMU + cloud-init + GPU proxy + CUDA shim

### Milestone 2: Node Agent Integration
- [ ] Add `DeploymentMode.ContainerWithVm` handling in node agent
- [ ] Node agent launches Docker container with `--gpus all --device /dev/kvm`
- [ ] Container starts QEMU VM + GPU proxy, reports VM IP back
- [ ] Heartbeat integration (VM health inside container)

### Milestone 3: Orchestrator Integration
- [ ] Add `SupportsNestedVirtualization` to hardware discovery
- [ ] Update scheduler to prefer VFIO > ContainerWithVm > Container
- [ ] Update VmService deployment logic for new mode
- [ ] Update billing (same rates regardless of deployment mode)

### Milestone 4: Resource Controls
- [ ] GPU memory quotas in proxy (reject cuMemAlloc past limit)
- [ ] Kernel execution timeouts (kill hung kernels)
- [ ] GPU utilization metering for billing accuracy

### Milestone 5: Full Transparency (Optional, Future)
- [ ] Move from LD_PRELOAD to ioctl-level proxy
- [ ] Reference gVisor nvproxy for ioctl surface mapping
- [ ] Version-track NVIDIA driver ioctl changes

---

## Key Design Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| VM inside Docker, not Docker inside VM | VM-in-Docker | Docker gets GPU from host; VM provides isolation; no VFIO needed |
| GPU proxy over virtio-vsock | vsock | Low-latency, kernel-level transport; no network config needed |
| LD_PRELOAD first, ioctl later | Phased | 95% coverage with 20% effort; upgrade path clear |
| General CUDA support, not inference-only | Full CUDA | DeCloud is general-purpose compute; can't limit to inference |
| Same billing for all deployment modes | Flat | Users shouldn't pay more for platform limitations |

---

## Open Questions

1. **GPU proxy packaging:** Single Docker image with QEMU + proxy, or compose with separate containers?
2. **Multi-GPU:** Can the proxy multiplex multiple VMs onto one GPU with memory partitioning?
3. **AMD GPUs:** ROCm equivalent of CUDA proxy? Separate implementation or unified?
4. **Shared memory optimization:** virtio-pmem for zero-copy large transfers between VM and container?
5. **Existing projects:** Fork rCUDA/GVirtuS, use gVisor nvproxy as reference, or build from scratch?

---

## Files to Modify (When Implementing)

**Orchestrator:**
- `Models/VirtualMachine.cs` — Add `ContainerWithVm` to `DeploymentMode`
- `Models/Node.cs` — Add nested virt detection fields to `HardwareInventory`
- `Services/VmScheduling/VmSchedulingService.cs` — Update GPU filter and scoring
- `Services/VmService.cs` — Update STEP 7 deployment mode resolution

**Node Agent:**
- `Core/Models/VmModels.cs` — Mirror `DeploymentMode` enum
- `Services/CommandProcessorService.cs` — Handle `ContainerWithVm` create flow
- `Infrastructure/Libvirt/LibvirtVmManager.cs` — Generate VM config for nested use
- NEW: `Services/GpuProxyContainerManager.cs` — Manage Docker container lifecycle
- NEW: `Infrastructure/Docker/DockerContainerManager.cs` — Docker SDK integration

**New Components (Separate Repo or Subdir):**
- `gpu-proxy/` — GPU proxy daemon (runs in container, real CUDA access)
- `cuda-shim/` — LD_PRELOAD library (runs inside VM, intercepts CUDA calls)
- `Dockerfile` — Packages QEMU + proxy + shim into single image

---

*This document captures the design discussion as of 2026-02-22. Implementation has not started. The recommended approach is Solution 2 (VM-Inside-Docker with GPU Proxy) with phased CUDA interception.*
