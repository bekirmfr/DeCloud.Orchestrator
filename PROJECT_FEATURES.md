# DeCloud Platform Features

**Last Updated:** 2026-04-05
**Purpose:** Technical reference for all implemented and planned platform features.

---

## Table of Contents

**Implemented**
1. [Core Architecture](#1-core-architecture)
2. [Networking Infrastructure](#2-networking-infrastructure)
   - [CGNAT Relay System](#cgnat-relay-system)
   - [Smart Port Allocation (Direct Access)](#smart-port-allocation-direct-access)
   - [CentralIngress-Aware Port Allocation](#centralingress-aware-port-allocation)
3. [VM Infrastructure](#3-vm-infrastructure)
   - [VM Proxy System](#vm-proxy-system)
   - [VM Lifecycle Management](#vm-lifecycle-management)
   - [Per-Service Readiness Tracking](#per-service-readiness-tracking)
4. [Authentication & Security](#4-authentication--security)
5. [Economics](#5-economics)
   - [Billing & Compute Pricing](#billing--compute-pricing)
   - [Bandwidth Tiers](#bandwidth-tiers)
   - [Hybrid Pricing Model](#hybrid-pricing-model)
6. [Marketplace & Discovery](#6-marketplace--discovery)
   - [Node Marketplace](#node-marketplace)
   - [VM Template Marketplace](#vm-template-marketplace)
   - [Reputation System](#reputation-system)
7. [Monitoring & Health](#7-monitoring--health)
8. [Advanced Compute](#8-advanced-compute)
   - [GPU Proxy (CUDA Virtualization)](#gpu-proxy-cuda-virtualization)
   - [DHT Infrastructure](#dht-infrastructure)
   - [Block Store & Storage Economics](#block-store--storage-economics)
9. [Node Operations](#9-node-operations)
   - [Resource Management](#resource-management)
   - [Windows WSL2 Auto-Start](#windows-wsl2-auto-start)

**Planned**

10. [Planned Features](#10-planned-features)
    - [Prebuilt Binary Distribution](#prebuilt-binary-distribution)
    - [Lightweight Node Support](#lightweight-node-support)
    - [Alpine Linux System VMs](#alpine-linux-system-vms)
    - [Phase 3+ Roadmap](#phase-3-roadmap)

---

## 1. Core Architecture

### Orchestrator-Node Model

DeCloud uses a coordinator-worker architecture:

```
User → Orchestrator (Central Brain) → Node Agents (Distributed Workers)
```

**Orchestrator responsibilities:**
- VM scheduling and lifecycle management
- Node registration and health monitoring
- Resource allocation using compute points
- Billing and payment processing (USDC on Polygon)
- Relay infrastructure coordination
- Marketplace and discovery APIs

**Node Agent responsibilities:**
- KVM/libvirt virtualization
- Resource discovery (CPU, memory, GPU, storage)
- VM provisioning and management
- WireGuard networking
- Local proxy for VM ingress traffic

**Communication:**
- Heartbeat every 15 seconds; 2-minute silence → node marked offline
- REST APIs for VM operations
- WebSocket support for real-time features
- WireGuard tunnels for secure overlay networking

### VM Types

| Type | Purpose |
|---|---|
| **General VMs** | User workloads — AI, web apps, databases |
| **Relay VMs** | Auto-deployed on public nodes to serve CGNAT nodes |
| **DHT VMs** | Decentralized coordination via libp2p over WireGuard mesh |
| **Block Store VMs** | Distributed content-addressed storage (5% storage duty) |

### Quality Tiers

| Tier | Overcommit | Use Case |
|---|---|---|
| Guaranteed | 1:1 | Production databases, critical workloads |
| Standard | 1.5:1 | Web apps, APIs |
| Balanced | 2:1 | Development environments |
| Burstable | 4:1 | Batch jobs, testing |

CPU compute points are calculated via sysbench benchmarking and normalized against a baseline. Formula: `computePoints = (nodePerformance / baselinePerformance) × coreCount`.

---

## 2. Networking Infrastructure

### CGNAT Relay System

**Status:** ✅ Production-ready

**The problem:** 60–80% of internet users (mobile networks, residential ISPs) are behind CGNAT and have no unique public IP. Without handling, these nodes cannot accept inbound connections and cannot host VMs.

**The solution:** When the orchestrator detects a CGNAT node during registration, it automatically deploys a lightweight relay VM on a public-IP node and establishes a WireGuard tunnel. NAT rules on both ends route traffic transparently.

#### Traffic flow

```
User Browser (HTTPS)
  ↓
Relay Node public IP (iptables DNAT)
  ↓ WireGuard tunnel (10.20.x.x)
CGNAT Node (receives as local traffic)
  ↓ libvirt bridge
VM (192.168.122.x:port)
```

#### Relay lifecycle

1. Node registers → orchestrator detects private IP / CGNAT indicators
2. Orchestrator deploys relay VM on a suitable public-IP node (cloud-init)
3. Relay VM enrolls in WireGuard mesh via `wg-mesh-enroll.sh`
4. CGNAT node establishes outbound WireGuard tunnel to relay
5. Traffic forwarded; relay node earns 20% of VM revenue passively

#### Revenue split

| Role | Share |
|---|---|
| CGNAT node (VM host) | 80% |
| Relay node (connectivity) | 20% |

#### WireGuard Mesh Enrollment

The enrollment script (`wg-mesh-enroll.sh`) uses a two-strategy approach:

1. **Strategy 1 (primary):** POST to NodeAgent proxy via virbr0 default gateway (`/api/relay/wg-mesh-enroll`) — solves port 8080 unreachability from NAT'd VMs
2. **Strategy 2 (fallback):** Direct relay API (`POST http://<relay-ip>:8080/api/relay/add-peer`)

The NodeAgent proxy (`WgMeshEnrollController`) finds the relay VM's bridge IP via `IPortForwardingManager.GetRelayVmIpAsync()` and forwards the enrollment request. For CGNAT hosts without a local relay VM, the proxy discovers the relay tunnel gateway from the host's WireGuard interface addresses.

#### Implementation files

| File | Purpose |
|---|---|
| `RelayService.cs` | Relay VM deployment and lifecycle |
| `RelayController.cs` | REST API (`/api/relays`) |
| `relay-vm-cloudinit.yaml` | Relay VM cloud-init (nginx, WireGuard, NAT rules) |
| `relay-api.py` | Relay management API (peer add/remove, NAT rules) |
| `WgMeshEnrollController.cs` | NodeAgent proxy for DHT/BlockStore VM enrollment |
| `wg-mesh-enroll.sh` | Two-strategy WG mesh enrollment script |

#### Strategic value: "DeCloud Relay SDK"

The relay infrastructure is platform-agnostic and could be packaged as a standalone product for other decentralized platforms (Akash, Filecoin, residential blockchain validators). Estimated addressable market: $5M–$25M.

---

### Smart Port Allocation (Direct Access)

**Status:** ✅ Production-ready (all 5 bugs fixed, end-to-end tested)

Enables external TCP/UDP access to VM services through automated port forwarding. CGNAT nodes use 3-hop forwarding through relay nodes; public nodes use direct forwarding.

#### 3-hop architecture (CGNAT nodes)

```
External Client :40000
  ↓ (Internet)
Relay Node iptables DNAT  (PublicPort → TunnelIP:PublicPort)
  ↓ (WireGuard tunnel)
CGNAT Node iptables DNAT  (PublicPort → VM:ServicePort)
  ↓ (libvirt bridge)
VM service
```

#### Direct forwarding (public nodes)

```
External Client :40000
  ↓ (Internet)
Node iptables DNAT  (PublicPort → VM:ServicePort)
  ↓ (libvirt bridge)
VM service
```

#### Port allocation flow

1. User requests port mapping (VM port → public port)
2. Orchestrator validates: range (40000–65535), not already allocated, VM running
3. Orchestrator identifies path (CGNAT → relay + node commands; public → node only)
4. Nodes create iptables DNAT + FORWARD rules
5. SQLite database persists mappings on each node

#### Database schema

```sql
CREATE TABLE PortMappings (
    Id TEXT PRIMARY KEY,
    VmId TEXT NOT NULL,
    VmPrivateIp TEXT NOT NULL,
    VmPort INTEGER NOT NULL,     -- 0 = relay mapping marker
    PublicPort INTEGER UNIQUE NOT NULL,
    Protocol INTEGER NOT NULL,   -- 0=TCP, 1=UDP, 2=Both
    Label TEXT,
    CreatedAt TEXT NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1
);
```

`VmPort = 0` marks relay-side forwarding rules, enabling correct match/delete logic.

#### Reconciliation & self-healing

`PortForwardingReconciliationService` runs at startup: flushes iptables chain and recreates all rules from database. Database is the single source of truth.

#### Implementation files

| File | Purpose |
|---|---|
| `DirectAccessService.cs` | Port allocation coordinator (Orchestrator) |
| `PortForwardingManager.cs` | iptables management (NodeAgent) |
| `PortMappingRepository.cs` | SQLite persistence (NodeAgent) |
| `CommandProcessorService.cs` | Command handling (NodeAgent) |
| `PortForwardingReconciliationService.cs` | Startup reconciliation (NodeAgent) |

---

### CentralIngress-Aware Port Allocation

**Status:** ✅ Complete (2026-02-09)

HTTP/HTTPS/WS/WSS traffic is already routed by Caddy subdomain routing (CentralIngress). Auto-allocating iptables rules for those protocols would be redundant. `AutoAllocateTemplatePortsAsync` filters them out:

```csharp
var ingressProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "http", "https", "ws", "wss" };

var portsToAllocate = template.ExposedPorts
    .Where(p => p.IsPublic)
    .Where(p => !ingressProtocols.Contains(p.Protocol ?? ""))
    .ToList();
```

#### Protocol routing summary

| Protocol | Routing Method | Example |
|---|---|---|
| `http` / `https` | CentralIngress (Caddy subdomain) | `app-xyz.vms.stackfi.tech` → VM:8080 |
| `ws` / `wss` | CentralIngress (Caddy WebSocket upgrade) | Transparent on same connection |
| `tcp` | Direct Access (iptables DNAT) | `publicIP:40001` → VM:22 |
| `udp` | Direct Access (iptables DNAT) | `publicIP:40002` → VM:8388 |
| `both` | Direct Access (TCP+UDP DNAT) | `publicIP:40003` → VM:8388 |

#### Impact by template

| Template | Ports | Direct Access |
|---|---|---|
| Stable Diffusion | 7860 (http) | None — CentralIngress |
| PostgreSQL | 5432 (tcp) | ✅ → 40xxx |
| VS Code Server | 8080 (http) | None — CentralIngress |
| Shadowsocks | 8388 (both) | ✅ → 40xxx (TCP+UDP) |
| Web Proxy Browser | 8080 (http) | None — CentralIngress |

---

## 3. VM Infrastructure

### VM Proxy System

**Status:** ✅ Production-ready

The Node Agent includes a unified proxy (`GenericProxyController`) that handles all ingress traffic to VMs, routing HTTP, WebSocket, and raw TCP connections.

#### Routes

```
/api/vms/{vmId}/proxy/http/{port}/{**path}   HTTP/HTTPS proxy
/api/vms/{vmId}/proxy/ws/{port}              WebSocket tunnel (SSH, SFTP, etc.)
/api/vms/{vmId}/proxy/tcp/{port}             Raw TCP over WebSocket
```

#### Security: allowed ports

Access is restricted to infrastructure ports (22 SSH, 80 HTTP, 9999 attestation) plus any ports defined in the VM's template `ExposedPorts`. Arbitrary port access is blocked.

---

### VM Lifecycle Management

**Status:** ✅ Complete (2026-02-09)
**Location:** `src/Orchestrator/Services/VmLifecycleManager.cs`

All confirmed VM state transitions flow through a single entry point: `VmLifecycleManager.TransitionAsync()`. This replaced 5 separate code paths that changed VM status with inconsistent side effects, removing ~460 lines of duplicated code.

#### Design principles

1. **Single entry point** — every status change goes through `TransitionAsync`
2. **Validate → persist → effects** — invalid transitions rejected before any write
3. **Side effects keyed by (from, to) pair** — ingress setup, port allocation, template fee settlement, resource cleanup
4. **SafeExecuteAsync pattern** — each side effect is individually isolated; one failure does not block others
5. **Persist-first** — status persisted before effects run; crash-safe by design

#### State machine call sites

5 call sites routed through the lifecycle manager: command acknowledgement, heartbeat processing, health check, timeout handler, manual admin action.

---

### Per-Service Readiness Tracking

**Status:** Orchestrator complete (2026-02-10) | NodeAgent implementation pending
**Location:** Orchestrator models and services

Distinguishes "VM running" (hypervisor reports domain active) from "VM ready for service" (application-level services responding). Uses `qemu-guest-agent` — no direct network access to the VM is required.

#### Service list structure

Every VM has an ordered service list:
1. **System** (always first) — `CloudInitDone` check via `cloud-init status`
2. **Template services** — one entry per `ExposedPorts` item in the template

#### Check types

| Type | Mechanism | Example |
|---|---|---|
| `CloudInitDone` | `cloud-init status --wait` via guest agent | System service |
| `TcpPort` | Connect attempt to port | PostgreSQL :5432 |
| `HttpGet` | HTTP request via guest agent | Stable Diffusion `/api/v1/sd-models` |
| `ExecCommand` | Command execution via guest agent | `pg_isready` |

Check type is auto-inferred from protocol (`http` → `HttpGet`, `tcp` → `TcpPort`) with explicit override support.

#### Pending work

- NodeAgent `VmReadinessMonitor` background service (spec in `NODE_AGENT_READINESS_CHANGES.md`)
- Frontend per-service status display under VM details

---

## 4. Authentication & Security

**Status:** ✅ Production-ready

### Wallet-Based Authentication

All user identity is anchored to Ethereum wallet signatures. No usernames, no passwords, no KYC.

- Frontend: WalletConnect / Reown AppKit (MetaMask, Coinbase Wallet, WalletConnect QR)
- Backend: Signature verification against wallet address
- Node identity: `SHA256(machineId + walletAddress)` — deterministic and stable

### SSH Access

Two modes:

1. **User-registered SSH keys** — preferred; user's own public key injected via cloud-init
2. **Wallet-derived SSH keys** — derived from wallet signature when no key is registered; private key displayed once at VM creation

All SSH access uses certificate-based authentication via a per-platform CA. VM certificates carry principal `vm-{vmId}`, enforcing tenant isolation — User A's certificate cannot authenticate to User B's VM.

**CA management:** `SshCertificateService.cs`, `WalletSshKeyService.cs`

### VM Attestation

Each VM runs an attestation agent (`/usr/local/bin/decloud-agent`) that exposes a challenge/response endpoint at port 9999. The orchestrator verifies VM identity before issuing certificates. Agent is deployed via cloud-init with `NoNewPrivileges`, `ProtectSystem=strict`, and `PrivateTmp` hardening.

### Template Security Validation

`TemplateService` validates all community-submitted cloud-init scripts for dangerous patterns before publish:
- Fork bombs (`:(){ :|:& };:`)
- Destructive commands (`rm -rf /`)
- Untrusted pipe-to-shell patterns (`curl ... | bash`)

---

## 5. Economics

### Billing & Compute Pricing

**Status:** ✅ Production-ready

**Compute points:** Nodes are benchmarked with sysbench and assigned normalized compute points. VMs are priced per compute-point-hour.

**Billing formula:**
```
hourlyRate = (computePoints × nodePricePerPoint × tierMultiplier) + bandwidthRate
```

**Payment flow:**
1. User deposits USDC to account (on-chain transaction)
2. Orchestrator tracks VM usage per hour
3. Billing service calculates charges and deducts from balance
4. Node operators accumulate earnings
5. Payout service transfers to node wallets (weekly batches)

**Paid templates:** PerDeploy pricing with 85/15 author/platform split via escrow settlement at VM termination.

---

### Bandwidth Tiers

**Status:** ✅ Complete (2026-02-02)

Bandwidth limits enforced at the hypervisor level via libvirt QoS `<bandwidth>` elements on virtio NIC interfaces. Both x86_64 and ARM paths apply matching rules.

| Tier | Speed | Multiplier | Rate |
|---|---|---|---|
| Basic | 10 Mbps | 0.8× | +$0.005/hr |
| Standard | 50 Mbps | 1.0× | +$0.015/hr |
| Performance | 200 Mbps | 1.2× | +$0.040/hr |
| Unmetered | Host NIC limit | 1.5× | +$0.040/hr |

---

### Hybrid Pricing Model

**Status:** ✅ Complete (2026-02-03)

- **Platform floor rates:** Minimum per-resource prices nodes cannot undercut
- **Node operator pricing:** Operators set custom rates (CPU, RAM, storage, GPU per hour)
- **Floor enforcement:** `Math.Max(operatorRate, floorRate)` applied server-side on every pricing update
- **Configuration:** Operators set pricing via `appsettings.json`, environment variables, or `PATCH /api/nodes/me/pricing`

---

## 6. Marketplace & Discovery

### Node Marketplace

**Status:** ✅ Complete (2026-01-30)

**API:** `GET /api/marketplace/nodes`

**Features:**
- Search and filter by tags, region, price, GPU, uptime, compute capacity
- Featured nodes (top 10 by uptime and capacity, requires 95%+ uptime)
- One-click "Deploy VM" from any node card — pre-populates VM creation modal
- Node detail modal with full hardware specs, reputation stats, pricing
- Operator profile management (`PATCH /api/marketplace/nodes/{id}/profile`)

---

### VM Template Marketplace

**Status:** ✅ Complete (2026-02-09)

**Implementation files:** `TemplateService.cs` (715 lines), `TemplateSeederService.cs` (1,828 lines), `MarketplaceController.cs`

**Features:**
- Full template CRUD with community and platform-curated templates
- 5 seed categories: AI & ML, Databases, Dev Tools, Web Apps, Privacy & Security
- Cloud-init variable substitution (`${DECLOUD_VM_ID}`, `${DECLOUD_PASSWORD}`, `${DECLOUD_DOMAIN}`, etc.)
- Community templates: draft → publish workflow
- Paid templates: PerDeploy pricing (85/15 author/platform split via escrow)
- Template deployment hooks into per-service readiness tracking
- Security validation (dangerous command detection)
- Frontend: `marketplace-templates.js`, `my-templates.js`, `template-detail.js`

**Seed templates (6):**

| Template | Category | GPU | Direct Access |
|---|---|---|---|
| Stable Diffusion WebUI | AI & ML | Required | None (CentralIngress) |
| PostgreSQL Database | Databases | No | ✅ TCP :5432 |
| VS Code Server | Dev Tools | No | None (CentralIngress) |
| Private Browser (Neko) | Privacy & Security | No | None (CentralIngress) |
| Shadowsocks Proxy | Privacy & Security | No | ✅ Both :8388 |
| Web Proxy Browser (Ultraviolet) | Privacy & Security | No | None (CentralIngress) |

**Planned templates:** Nextcloud, Whisper AI, Ollama chatbot, Minecraft server, Jellyfin, VPN/Tor relay, Mastodon.

---

### Reputation System

**Status:** ✅ Core complete (2026-01-30 + 2026-02-09) | Frontend polish pending

**Uptime tracking:**
- 30-day rolling window, precision to 15-second heartbeat intervals
- `FailedHeartbeatsByDay` dictionary on the Node model; auto-cleaned after 30 days
- Formula: `uptime% = (expected – failed) / expected × 100`
- Integrated with `NodeHealthMonitorService` — no separate monitoring infrastructure

**Metrics tracked:**
- Uptime percentage (30-day rolling)
- Total VMs hosted (lifetime counter)
- Successful VM completions (clean terminations)

**Review system:**
- Universal: covers both nodes and templates (1–5 stars, title, comment)
- Eligibility-verified: proof of deployment/usage required before review submission
- Denormalized rating aggregates on templates (`AverageRating`, `TotalReviews`, `RatingDistribution`)
- API endpoints: submit, get reviews, check user's own review

**Pending frontend work:** trust badges (99.9% uptime, 100+ VMs hosted), node rating aggregate display, review prompts after VM termination.

---

## 7. Monitoring & Health

### Node Health Monitoring

**Heartbeat system:**
- Nodes send heartbeat every 15 seconds
- 2-minute silence → node marked offline
- All VMs on offline node marked Error, billing stops

**Failure handling:**
- Immediate: offline + Error + billing stop
- User decision: wait for recovery or delete VM
- On deletion: orchestrator triggers port cleanup on relay nodes
- Future: auto-delete after grace period (planned)

### Relay Health Monitoring

- WireGuard tunnel status check every 60 seconds
- Port mapping validation every 5 minutes
- Automatic repair on detected failures

---

## 8. Advanced Compute

### GPU Proxy (CUDA Virtualization)

**Status:** ✅ Production-ready (2026-03-13)
**Verified workloads:** Ollama/ggml inference (436 tok/s), PyTorch inference, PyTorch training (backward pass + AdamW), LoRA fine-tuning via PEFT (1,038 tok/s, 1,360 MB VRAM)

**The problem:** Nodes without IOMMU (WSL2, consumer PCs, most VPS) cannot do GPU passthrough to VMs. This excludes the majority of GPU-equipped nodes from the network.

**The solution:** A CUDA virtualization layer — shim libraries inside the VM intercept all CUDA calls and forward them via TCP RPC to a daemon on the host that holds the real GPU.

#### Architecture

```
VM (Guest)                          Host
─────────────────────────────────   ────────────────────────
PyTorch / Ollama                    gpu-proxy-daemon
  ↓ LD_PRELOAD                        ↓ real CUDA runtime
libdecloud_cuda_shim.so              nvidia-smi / libcuda.so
libcuda_pytorch_stubs.so
libcudart.so.12
  ↓ TCP RPC (vsock or TCP)
  ────────────────────────────────→
```

**LD_PRELOAD order (critical):**
```
libdecloud_cuda_shim.so:libcuda_pytorch_stubs.so:libcudart.so.12
```
Shim must precede PyTorch stubs to win symbol resolution.

#### Key engineering details

- **TCP_QUICKACK fix:** Eliminated 40ms delayed ACK per RPC, achieving 150× speedup (0.07 → 13+ tok/s initial, further optimized to 436 tok/s)
- **CUDA 12 lazy loading:** `CUDA_MODULE_LOADING=EAGER` is mandatory for PyTorch — without it, module registration is deferred to `__cudaInitModule` which the proxy doesn't intercept
- **CUDA graphs:** Pass-through (`cudaErrorNotSupported`) forces application fallback to direct kernel execution — correct approach vs attempting emulation
- **PyTorch stubs:** `libcuda_pytorch_stubs.so` supplies `cudaMallocAsync`, `cudaFreeAsync`, and 22 more symbols required by `libtorch_cuda.so` but not implemented in the shim
- **cuDNN stub:** `libcudnn_stub.so` (87 versioned symbols) satisfies `DT_NEEDED: libcudnn.so.8` in `libtorch_cuda.so` without implementing any cuDNN operations

#### Deployment

Fully automated via cloud-init. `install.sh` builds shims and daemon on host, exposes via 9p share. Cloud-init copies shims into VM, replaces bundled CUDA libs (PyTorch ships its own `libcublas.so.12`), writes transport config, restarts application.

Template `DefaultEnvironmentVariables` drives application-specific config (GGML vars for Ollama, CUDA vars for PyTorch) via `/etc/decloud/gpu-proxy.env`.

#### Implementation files

| File | Purpose |
|---|---|
| `shim/cuda_shim.c` | Runtime API shim (~1,900 lines) |
| `shim/cuda_driver_shim.c` | Driver API shim (~1,800 lines) |
| `shim/transport.c` | TCP/vsock transport with QUICKACK |
| `stubs/cublasLt_stub.c` | cublasLtMatmul stub (29 versioned symbols) |
| `stubs/cublas_stub.c` | cuBLAS stub (20+ versioned symbols) |
| `stubs/libcudnn_stub.c` | cuDNN stub (87 versioned symbols) |
| `daemon/gpu_proxy_daemon.c` | Host daemon (~2,100 lines) |
| `proto/gpu_proxy_proto.h` | Wire protocol definitions |
| `LibvirtVmManager.cs` | `EnsureGpuProxyShim` — cloud-init injection |
| `GpuProxyService.cs` | Daemon lifecycle management |
| `install.sh` | Build pipeline + symbol count verification |

---

### DHT Infrastructure

**Status:** ✅ Production-verified (2026-02-15)

A libp2p-based DHT layer provides decentralized peer coordination. DHT nodes run as lightweight VMs over WireGuard mesh, connected to each other via the relay tunnel network.

#### Verified deployment

- **Node 1** (us-east-1): peerId `12D3KooWD8zw...B8n1`, tunnelIP `10.20.1.199`, connectedPeers: 1
- **Node 2** (tr-south): peerId `12D3KooWHNLM...j8Yx`, tunnelIP `10.20.1.202`, connectedPeers: 1

#### DHT bootstrap flow

1. VM boots → WireGuard mesh enrollment (via NodeAgent proxy)
2. DHT binary starts → obtains libp2p peer ID
3. `dht-bootstrap-poll.sh` polls `POST /api/dht/join` → receives known peer list
4. DHT binary connects to peers over WireGuard (e.g., `10.20.1.199:4001`)
5. `POST /api/dht/ready` callback to NodeAgent → HMAC-SHA256 authenticated

#### Implementation files

| File | Purpose |
|---|---|
| `DhtNodeService.cs` | DHT VM deployment (Orchestrator) |
| `DhtController.cs` | `/api/dht/join` bootstrap endpoint |
| `DhtCallbackController.cs` | `/api/dht/ready` ready callback (NodeAgent) |
| `WgMeshEnrollController.cs` | WireGuard enrollment proxy (NodeAgent) |
| `dht-vm-cloudinit.yaml` | DHT VM cloud-init template |
| `wg-mesh-enroll.sh` | Two-strategy WireGuard enrollment script |
| `dht-bootstrap-poll.sh` | Bootstrap peer polling (runs in VM) |

---

### Block Store & Storage Economics

**Status:** Phase A–C ✅ Production-verified (2026-03-20) | Phase D (Lazysync) 🔲 Planned

#### Overview

Every eligible node (≥100 GB storage, ≥2 GB RAM) runs a Block Store VM as a network duty obligation, contributing 5% of its total storage to a distributed content-addressed storage network.

**Two purposes:**
1. **VM resilience** — overlay disks are continuously replicated; failed VMs can be reconstructed on another node
2. **AI model distribution** — LLMs are chunked and distributed, enabling decentralized model serving and pipeline-parallel inference

#### Variable chunk sizes

| Manifest Type | Chunk Size | Rationale |
|---|---|---|
| `vm-overlay` | 1 MB | Aligns with QEMU dirty bitmap granularity |
| `model-shard` | 64 MB | Aligns with transformer layer boundaries |

#### Block Store VM spec

```csharp
VirtualCpuCores = 1
MemoryBytes     = 512 MB
DiskBytes       = 5% of node total storage
QualityTier     = Burstable
ImageId         = "debian-12-blockstore"
```

#### Block Store binary features

- libp2p host with persistent Ed25519 identity
- Bitswap client+server for block exchange
- FlatFS backend (content-addressed flat files)
- Storage quota enforcement (refuse writes when full)
- GossipSub subscription (`decloud/blockstore/new-blocks`) for near-instant block discovery
- Adaptive XOR threshold pull logic (closer DHT distance + more free space → more aggressive pulling)
- Localhost HTTP API on port 5090
- Garbage collection (LRU eviction within 5% budget)

#### Authentication

```
Orchestrator → VM:  auth token via cloud-init labels
VM → Orchestrator:  HMAC-SHA256(authToken, nodeId:vmId) via X-BlockStore-Token header
VM → NodeAgent:     HMAC-SHA256(machineId, vmId:peerId) via X-BlockStore-Token header
```

---

## 9. Node Operations

### Resource Management

**CPU benchmarking:**
```bash
sysbench cpu --threads=$(nproc) --time=10 run
```

Normalized against a reference baseline. Points scale linearly with core count and per-core performance. Used for scheduling, billing, and marketplace display.

**Resource discovery:** CPU (cores, model, frequency), RAM, GPU (model, VRAM, IOMMU group, passthrough eligibility), storage (disks, available space), network bandwidth (ethtool or sysfs reported speed).

---

### Windows WSL2 Auto-Start

**Status:** ✅ Complete (2026-04-05)

Enables Windows users running the node agent inside WSL2 to survive reboots and crashes without manual intervention.

#### The problem

WSL2 does not start automatically on Windows boot. If an operator runs the node agent inside WSL2, it stops whenever Windows restarts or WSL is shut down — requiring manual re-entry.

#### Solution: Windows Scheduled Task watchdog

A single self-contained `DeCloud-Node-Setup.bat` (~23 KB) is distributed via the `releases/` folder. Double-clicking it handles UAC elevation internally and installs a Windows Scheduled Task that:

1. Fires at Windows boot (30-second startup delay) and every 5 minutes (self-healing fallback)
2. Runs as SYSTEM account — no user login required
3. Ensures the WSL2 Ubuntu distro is running
4. Enables systemd in WSL automatically if not already configured
5. Starts or restarts `decloud-node-agent` via systemctl if not active
6. Backs off exponentially after repeated failures (5 consecutive → 120-second pause)
7. Rotates its own log file at 10 MB

The watchdog script (`DeCloud-WslWatchdog.ps1`) is embedded as base64 in the `.bat` and extracted to `C:\ProgramData\DeCloud\` at install time. The install directory is ACL-locked to SYSTEM and Administrators only.

#### install.sh integration

When `install.sh` detects WSL2 (via `/proc/version` and `/dev/dxg`), `print_summary` automatically:
- Downloads `DeCloud-Node-Setup.bat` to the Windows Desktop via `cmd.exe` interop and `wslpath`
- Shows a clickable OSC 8 hyperlink in Windows Terminal pointing to the file
- Falls back to manual instructions if the download fails

WSL2 detection is handled by `detect_wsl2()` — a standalone function called early in `main()`, independent of GPU detection.

#### Accessing the CLI

While the watchdog keeps the agent running as SYSTEM in the background, users connect interactively at any time from Windows Terminal / PowerShell by typing `wsl`. This attaches a new session to the running WSL instance without affecting the background agent.

#### Key files

| File | Purpose |
|---|---|
| `releases/DeCloud-Node-Setup.bat` | Single-file Windows installer (double-click to run) |
| `install.sh → detect_wsl2()` | Standalone WSL2 detection, runs early in `main()` |
| `install.sh → stage_windows_installer()` | Downloads `.bat` to Windows Desktop via WSL interop |
| `install.sh → make_hyperlink()` | Emits OSC 8 clickable terminal link |
| `install.sh → print_summary()` | WSL2 notice block (conditional on `IS_WSL2=true`) |

---

## 10. Planned Features

### Prebuilt Binary Distribution

**Status:** 🔲 Planned | Tracked: `# TODO(future)` in `install.sh` above `download_node_agent()`
**Effort:** Low (~1 week, primarily GitHub Actions + install.sh changes)

**Current flow:** `git clone` → `dotnet publish` → ~5 min build, requires .NET SDK on every node
**Target flow:** `curl` GitHub Release asset → extract → ~30 seconds, no SDK needed

**What is needed:**
1. GitHub Actions release workflow (`.github/workflows/release.yml`) — publishes `linux-amd64` and `linux-arm64` tarballs on every `git tag` push, bundling the DHT, BlockStore, and GPU proxy binaries
2. Download URL replacing the current `git clone` + build steps in `install.sh`
3. Removal of `install_dotnet` (SDK) from `main()` — only the .NET runtime is needed to run the agent
4. `releases/` folder in git hosts distributable scripts only; compiled binaries go to GitHub Release assets

---

### Lightweight Node Support

**Status:** 🔲 Planned
**Strategic value:** Expands supply side — any machine can contribute to the network

Nodes without KVM hardware virtualization (VPS with disabled nested virt, Raspberry Pi, old laptops, Termux on mobile) currently cannot run full VMs and are excluded entirely. These nodes can contribute meaningfully without VMs:

**Proposed capabilities:**
- Native process execution (run workloads directly on host, no VM overhead)
- Docker container hosting (for Docker-capable nodes)
- DHT participation (always possible regardless of compute capability)
- Block store contribution (storage-only nodes)

**Key constraint:** Security model changes — no VM isolation means different trust guarantees. Full design TBD.

---

### Alpine Linux System VMs

**Status:** 🔲 Planned (two-phase approach)
**Motivation:** Current system VMs (relay, DHT, block store) use Ubuntu Debian base images (~3.5 GB). Alpine cloud images are ~50 MB — 40–70× smaller, with sub-5-second boot time.

#### Phase 1 — Alpine + systemd (low effort)

Install `systemd` on Alpine cloud image; test existing cloud-init templates unchanged. Overhead: ~50 MB extra vs. full Alpine. Alpine's `tiny-cloud` doesn't use `ds-identify`, so the `datasource_list` injection issue doesn't apply.

#### Phase 2 — Full OpenRC rewrite (high effort, maximum benefit)

Rewrite all three system VM cloud-init templates to use OpenRC natively. True ~50 MB footprint. Boot time under 5 seconds. New image IDs: `alpine-relay`, `alpine-dht`, `alpine-blockstore`.

**Phase 2 prerequisite:** `InjectCloudInitSeedAsync` in `LibvirtVmManager` — writes `user-data` + `meta-data` directly into the overlay at `/var/lib/cloud/seed/nocloud/` before VM start, eliminating the cidata ISO timing dependency entirely.

#### Files to change

| File | Change |
|---|---|
| `ArchitectureHelper.cs` | Add Alpine image URLs for amd64 + arm64 |
| `VmService.cs` | Add `alpine-*` → URL mappings |
| `DataStore.cs` | Register Alpine image entries |
| `LibvirtVmManager.cs` | Add `InjectCloudInitSeedAsync` |
| `relay-vm-cloudinit.yaml` | `apt` → `apk`, `systemctl` → `rc-service` (Phase 2) |
| `dht-vm-cloudinit.yaml` | Same |
| `blockstore-vm-cloudinit.yaml` | Same |

This feature pairs naturally with **Lightweight Node Support** — Alpine is the ideal base for native process deployment on lightweight nodes.

---

### Phase 3+ Roadmap

#### Shared VMs (Multi-Wallet Access)
Multiple wallets can access the same VM. Enables team development environments. `authorizedWallets` list on the VM model; owner manages collaborators.

#### Infrastructure Templates (Multi-VM)
One-click deployment of interconnected VM stacks (e.g., web + database + Redis + load balancer). Templates define VM relationships and networking.

#### Live Network Visualization
Interactive globe/map showing nodes, VMs, and relay connections in real time. Public-facing for marketing.

#### Optional Premium Node Staking
XDE token staking for premium marketplace placement. Optional — free tier always available. Requires: >50 active nodes, >3 months operation, proven reputation data, XDE token launched.

#### Advanced Analytics Dashboard
Deep metrics for node operators: historical uptime trends, earnings projections, competitive benchmarking, resource utilization analytics.

---

*For strategic context, business priorities, and current development status, see PROJECT_MEMORY.md.*