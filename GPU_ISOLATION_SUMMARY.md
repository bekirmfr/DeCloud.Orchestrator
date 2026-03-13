# GPU Isolation & Passthrough — Architecture Summary Card

**Date:** 2026-02-22 (designed), 2026-03-13 (updated)
**Status:** ✅ Implemented — GPU Proxy (Solution 2) is production-ready; PyTorch training + LoRA confirmed
**Context:** GPU workload isolation for heterogeneous node environments

> **UPDATE 2026-03-06:** Solution 2 (GPU Proxy) has been fully implemented and is in production.
> The proxy achieves 436 tok/s prompt eval and 13-21 tok/s generation through TCP RPC.
> See `GPU_PROXY_ARCHITECTURE_REVIEW.md` for full technical details.
> The ContainerWithVm deployment mode is no longer needed — the proxy runs directly on the
> host (not inside Docker), and VMs connect via TCP over virbr0 or vsock.

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

### Measured Performance (Production, RTX 4060 Laptop GPU)

| Operation | Measured | Notes |
|-----------|----------|-------|
| Prompt eval (warm) | **436 tok/s** | llama3.2:1b, TCP with QUICKACK |
| Prompt eval (cold) | **188 tok/s** | First run after restart |
| Token generation | **13-21 tok/s** | Varies by model size (1b-3b) |
| Model load (warm) | **~130ms** | Cached in VRAM |
| Model load (cold) | **3-7s** | Module upload + cuModuleLoadData |
| RPC round-trip | **<1ms** | TCP_NODELAY + TCP_QUICKACK |
| 1.56GB fatbin upload | **~11s** | Streaming, zero-copy from mmap |
| GPU memory allocation | **<1ms** | Single RPC |
| PyTorch full fine-tune | **1,252 tok/s / 409ms/step** | GPT-2, batch=4, seq=128, AdamW |
| PyTorch LoRA fine-tune | **1,038 tok/s / 493ms/step** | GPT-2, PEFT r=8, 1,360MB VRAM |

---

## Implemented Deployment Mode

Instead of the originally proposed `ContainerWithVm`, we implemented `GpuMode.Proxied` — a simpler and more effective approach that runs the daemon directly on the host.

```csharp
public enum GpuMode
{
    None = 0,           // No GPU needed
    Passthrough = 1,    // VFIO passthrough (bare-metal + IOMMU, full perf)
    Proxied = 2         // GPU proxy daemon on host, shim in VM (no IOMMU needed)
}
```

### Actual Architecture (Simpler Than Design)

```
Host (WSL2 or bare-metal without IOMMU)
 ├─ gpu-proxy-daemon (real CUDA access, per-VM tokens)
 │    └─ Listens on TCP 192.168.122.1:9999 + vsock port 9999
 └─ KVM VM (tenant sandbox, no GPU device)
      ├─ libcudart.so.12 → Runtime API shim (LD_PRELOAD)
      ├─ libcuda.so.1 → Driver API shim
      ├─ libnvidia-ml.so.1 → NVML shim
      └─ Application (Ollama, PyTorch, etc.)
           └─ All CUDA calls → RPC to daemon → real GPU
```

No Docker container wrapping needed. The daemon runs as a native process, VMs connect via TCP over virbr0 (or vsock on bare metal).

### Decision Matrix (Scheduler)

```
Node has IOMMU + VFIO-capable GPU?
  ├─ YES → GpuMode.Passthrough (best: native GPU in VM)
  └─ NO → Node has GPU + libvirt/KVM?
       └─ YES → GpuMode.Proxied (GPU proxy, 436 tok/s, VM isolation)
```

---

## Implementation Roadmap

### Milestone 1: Infrastructure (Foundation) — ✅ COMPLETE
- [x] Design virtio-vsock/TCP communication protocol between VM and GPU proxy
- [x] Build GPU proxy daemon (35+ CUDA commands, module streaming, cuBLAS GEMM proxy)
- [x] Build matching LD_PRELOAD shim for inside the VM (Runtime + Driver API)
- [x] Package via 9p shared mount (host builds, VM copies at boot via cloud-init)

### Milestone 2: Node Agent Integration — ✅ COMPLETE
- [x] Add `GpuMode.Proxied` handling in node agent
- [x] `GpuProxyService.cs` manages daemon lifecycle (start/stop/health)
- [x] Per-VM token auth with SIGHUP reload
- [x] `EnsureGpuProxyShim` injects GPU config into any cloud-init template
- [x] Template-driven env vars (generic proxy, no hardcoded app dependencies)

### Milestone 3: Orchestrator Integration — ✅ COMPLETE
- [x] GPU discovery via NVML shim + driver shim
- [x] Scheduler prefers Passthrough > Proxied > Container
- [x] Template `DefaultGpuMode` propagation
- [x] Template `DefaultEnvironmentVariables` with GPU app vars

### Milestone 4: Resource Controls — ✅ COMPLETE
- [x] GPU memory quotas in proxy (per-connection `memory_quota`)
- [x] Kernel execution timeouts (configurable, default disabled for perf)
- [x] GPU utilization metering (kernel count, kernel time, peak memory, uptime)

### Milestone 5: Full Transparency (Optional, Future) — ❌ DEFERRED
- [ ] Move from LD_PRELOAD to ioctl-level proxy
- [ ] Reference gVisor nvproxy for ioctl surface mapping
- [ ] Version-track NVIDIA driver ioctl changes
- **Note:** API-level proxy covers 95%+ of AI hosting workloads. ioctl deferred until demand.

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

## Files Modified (Implementation Complete)

**GPU Proxy (`src/gpu-proxy/`):**
- `shim/cuda_shim.c` — Runtime API shim with config-driven constructor
- `shim/cuda_driver_shim.c` — Driver API shim with `cuGetProcAddress` dispatch
- `shim/transport.c` / `transport.h` — TCP/vsock transport with TCP_QUICKACK
- `shim/nvml_shim.c` — NVML fake GPU info
- `daemon/gpu_proxy_daemon.c` — 35+ command handlers
- `proto/gpu_proxy_proto.h` — Wire protocol definitions
- `stubs/cublas_stub.c` / `cublasLt_stub.c` — cuBLAS stubs with version tags

**Orchestrator:**
- `Models/VirtualMachine.cs` — `GpuMode` enum (None, Passthrough, Proxied)
- `Services/VmService.cs` — GPU mode propagation from templates
- `Services/TemplateSeederService.cs` — `DefaultEnvironmentVariables` with GPU app vars

**Node Agent:**
- `Infrastructure/Libvirt/LibvirtVmManager.cs` — `EnsureGpuProxyShim` with template-driven env vars
- `Infrastructure/Services/GpuProxyService.cs` — Daemon lifecycle management
- `install.sh` — Full build + deploy pipeline with daemon restart

---

*This document was originally written 2026-02-22 as a design proposal. Updated 2026-03-06 to reflect full implementation of Solution 2 (GPU Proxy) with production performance numbers. Updated 2026-03-13 to reflect PyTorch inference + full training + LoRA fine-tuning confirmed working end-to-end. The LD_PRELOAD + TCP RPC approach achieved 95%+ CUDA coverage with near-native performance for AI inference and training workloads. The Docker-in-VM approach was not needed — direct daemon + VM architecture is simpler and faster.*