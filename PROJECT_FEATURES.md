# DeCloud Platform Features

**Last Updated:** 2026-04-08
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

10. [Compliance & Legal Framework](#10-compliance--legal-framework)
    - [CSAM Proactive Filtering](#csam-proactive-filtering)
    - [Terms of Service](#terms-of-service)
    - [Abuse Reporting & AI Triage](#abuse-reporting--ai-triage)
    - [Template Review Gate](#template-review-gate)
    - [Enforcement Mechanism](#enforcement-mechanism)
    - [DMCA Agent & Safe Harbor](#dmca-agent--safe-harbor)
11. [Planned Features](#11-planned-features)
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

CPU compute points are calculated via sysbench benchmarking, normalized against a reference baseline. Formula: `computePoints = (nodePerformance / baselinePerformance) × coreCount`.

---

## 2. Networking Infrastructure

### CGNAT Relay System

**Status:** ✅ Production-ready

**The problem:** 60–80% of internet users (mobile networks, residential ISPs) are behind CGNAT and have no unique public IP. Without handling, these nodes cannot accept inbound connections and cannot host VMs.

**The solution:** When the orchestrator detects a CGNAT node during registration, it automatically deploys a lightweight relay VM on a public-IP node and establishes a WireGuard tunnel.

#### Node Registration & CGNAT Detection

```csharp
// NodeService.cs
public async Task<Node> RegisterNodeAsync(NodeRegistrationRequest request)
{
    var isPublicIp = await _networkAnalyzer.IsPublicIpAsync(request.PublicIp);
    var isCgnat = !isPublicIp || request.ReportedCgnat;

    node.IsBehindCgnat = isCgnat;
    node.RequiresRelay = isCgnat;

    if (isCgnat)
        await _relayService.AssignRelayAsync(node.Id);
}
```

Detection logic: check if reported IP matches actual source IP, validate IP is not in private ranges (10.x, 192.168.x, 172.16.x), allow manual CGNAT flag for complex network setups.

#### Relay VM Deployment

```csharp
// RelayService.cs
public async Task AssignRelayAsync(string cgnatNodeId)
{
    var relayNode = await FindBestRelayNodeAsync(); // public IP, sufficient resources

    var relayVm = await _vmService.CreateVmAsync(
        userId: "system",
        request: new CreateVmRequest(
            Name: $"relay-{cgnatNodeId}",
            VmType: VmType.Relay,
            Spec: new VmSpec { MemoryBytes = 512 * 1024 * 1024, VirtualCpuCores = 1 }
        ),
        targetNodeId: relayNode.Id
    );

    await _relayRepository.CreateRelayAsync(new Relay
    {
        RelayNodeId = relayNode.Id,
        RelayVmId = relayVm.Id,
        CgnatNodeId = cgnatNodeId,
        Status = RelayStatus.Provisioning
    });
}
```

**Relay node selection criteria:** public static IP, online and healthy, sufficient available resources, geographic proximity (future: latency-based).

#### WireGuard Tunnel Configuration

**Relay VM side** (via cloud-init):
```yaml
write_files:
  - path: /etc/wireguard/wg0.conf
    content: |
      [Interface]
      PrivateKey = ${RELAY_PRIVATE_KEY}
      Address = 10.200.0.1/24
      ListenPort = 51820
      PostUp = iptables -A FORWARD -i wg0 -j ACCEPT; iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
      PostDown = iptables -D FORWARD -i wg0 -j ACCEPT; iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE

      [Peer]
      PublicKey = ${CGNAT_NODE_PUBLIC_KEY}
      AllowedIPs = 10.200.0.2/32
      PersistentKeepalive = 25
```

**CGNAT node side:**
```ini
[Interface]
PrivateKey = <node_private_key>
Address = 10.200.0.2/24

[Peer]
PublicKey = <relay_public_key>
Endpoint = <relay_public_ip>:51820
AllowedIPs = 10.200.0.0/24
PersistentKeepalive = 25
```

`PersistentKeepalive = 25` prevents NAT timeout. WireGuard keypairs are reused across relay redeployments to preserve mesh connectivity for DHT and BlockStore VMs.

#### Traffic Flow

```
User Browser (HTTPS)
  ↓
Relay Node public IP (e.g., 203.0.113.50:443)
  ↓ iptables DNAT
WireGuard tunnel (10.200.0.1 → 10.200.0.2)
  ↓
CGNAT Node (receives as local traffic)
  ↓
VM (192.168.122.x:port)
```

#### Port Allocation

```csharp
// RelayService.cs
public async Task AllocatePortForVmAsync(string vmId, int vmPort)
{
    var relay = await _relayRepository.GetByCgnatNodeIdAsync(vm.AssignedNodeId);
    var relayPort = await FindAvailablePortAsync(relay.RelayNodeId);

    await ExecuteOnRelayVmAsync(relay.RelayVmId,
        $"iptables -t nat -A PREROUTING -p tcp --dport {relayPort} " +
        $"-j DNAT --to-destination 10.200.0.2:{vmPort}");

    await _portMappingRepository.CreateAsync(new PortMapping
    {
        RelayId = relay.Id,
        VmId = vmId,
        RelayPort = relayPort,
        VmPort = vmPort
    });
}
```

#### Health Monitoring

`RelayHealthMonitorService` runs continuously:

```csharp
protected override async Task ExecuteAsync(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        foreach (var relay in await _relayRepository.GetActiveRelaysAsync())
        {
            var tunnelUp   = await CheckWireGuardTunnelAsync(relay);
            var portsValid = await ValidatePortMappingsAsync(relay);

            relay.Status = (tunnelUp && portsValid)
                ? RelayStatus.Active : RelayStatus.Degraded;

            if (!tunnelUp || !portsValid)
                await _relayRepairService.RepairRelayAsync(relay.Id);
        }
        await Task.Delay(TimeSpan.FromMinutes(1), ct);
    }
}
```

**Monitoring intervals:**
- WireGuard tunnel status: every 60 seconds
- Port mapping validation: every 5 minutes
- Bandwidth metrics: every 3 minutes

**Monitored metrics:** WireGuard peer handshake age, port mapping rule validity, bandwidth utilization, relay-to-CGNAT latency, relay VM resource usage.

#### Symbiotic Economics

| Role | Revenue share | Detail |
|---|---|---|
| CGNAT node (VM host) | 80% | Keeps majority despite needing relay |
| Relay node | 20% | Passive income for providing connectivity |

**For relay node operators:** 512MB RAM / 1 CPU relay VM earns passive income from every CGNAT node connected. Low resource cost, predictable earnings.

**For CGNAT node operators:** Can host VMs from home broadband, mobile networks, or any carrier-grade NAT connection without any ISP or infrastructure changes.

**For the platform:** 60–80% more potential nodes, geographic diversity via residential and mobile nodes, competitive advantage (Akash and most competitors don't support CGNAT).

#### Implementation Files

| File | Purpose |
|---|---|
| `RelayService.cs` | Relay VM deployment and lifecycle |
| `RelayNodeService.cs` | WireGuard keypair management, subnet assignment |
| `RelayHealthMonitor.cs` | Health monitoring and repair |
| `RelayController.cs` | REST API (`/api/relays`, `/api/relays/mesh-status`) |
| `relay-vm-cloudinit.yaml` | Relay VM cloud-init (nginx, WireGuard, relay API) |
| `relay-api.py` | Relay management API (peer add/remove, NAT rules) |
| `WgMeshEnrollController.cs` | NodeAgent proxy for system VM enrollment |
| `wg-mesh-enroll.sh` | Two-strategy WireGuard enrollment script |

#### Strategic Value: "DeCloud Relay SDK"

The relay infrastructure is platform-agnostic and could be packaged as a standalone product for other decentralized platforms (Akash, Filecoin, residential blockchain validators, gaming servers). Estimated addressable market: $5M–$25M.

---

### Smart Port Allocation (Direct Access)

**Status:** ✅ Production-ready (all 5 bugs fixed, end-to-end tested)

Enables external TCP/UDP access to VM services through automated port forwarding. CGNAT nodes use 3-hop forwarding through relay nodes; public nodes use direct forwarding.

#### 3-Hop Architecture (CGNAT Nodes)

```
External Client :40000
  ↓ (Internet)
Relay Node iptables DNAT  (PublicPort:40000 → TunnelIP:40000)
  ↓ (WireGuard tunnel)
CGNAT Node iptables DNAT  (PublicPort:40000 → VM:ServicePort)
  ↓ (libvirt bridge)
VM service
```

#### Direct Forwarding (Public Nodes)

```
External Client :40000
  ↓ (Internet)
Node iptables DNAT  (PublicPort:40000 → VM:ServicePort)
  ↓ (libvirt bridge)
VM service
```

#### Port Allocation Flow

1. User requests port mapping (VM port → public port)
2. Orchestrator validates: range (40000–65535), not already allocated, VM running
3. Orchestrator identifies path (CGNAT → relay + node commands; public → node only)
4. Nodes create iptables DNAT + FORWARD rules
5. SQLite database persists mappings on each node

#### Database Schema

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

```csharp
// Delete by PublicPort for relay mappings (VmPort=0 marker)
if (vmPort.Value == 0)
    removed = await _portMappingRepository.RemoveByPublicPortAsync(mapping.PublicPort);
else
    removed = await _portMappingRepository.RemoveAsync(vmId, vmPort.Value);
```

#### Reconciliation & Self-Healing

`PortForwardingReconciliationService` runs at startup: flushes iptables chain and recreates all rules from database. Database is single source of truth — one-way reconciliation (DB → iptables).

| Scenario | Behavior |
|---|---|
| Service restart | ✅ All rules recreated from database |
| iptables flushed | ✅ Rules recreated on next restart |
| Database deleted | ❌ All mappings lost (DB is authoritative) |

#### Health Monitoring & Failure Handling

`NodeHealthMonitorService` runs every 30 seconds. No heartbeat for 2 minutes → node marked Offline, all its VMs marked Error, billing stops.

**Design rationale:** Don't auto-cleanup immediately (prevents unintended service interruptions). Mark Error + stop billing (fair to users, allows investigation). Manual or delayed cleanup gives users control.

#### Implementation Files

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

HTTP/HTTPS/WS/WSS traffic is already routed by Caddy subdomain routing (CentralIngress). `AutoAllocateTemplatePortsAsync` filters these out to avoid redundant iptables rules:

```csharp
var ingressProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "http", "https", "ws", "wss" };

var portsToAllocate = template.ExposedPorts
    .Where(p => p.IsPublic)
    .Where(p => !ingressProtocols.Contains(p.Protocol ?? ""))
    .Where(p => vm.DirectAccess?.PortMappings?.Any(m => m.VmPort == p.Port) != true)
    .ToList();
```

#### Protocol Routing Summary

| Protocol | Routing | Example |
|---|---|---|
| `http` / `https` | CentralIngress (Caddy subdomain) | `app-xyz.vms.stackfi.tech` → VM:8080 |
| `ws` / `wss` | CentralIngress (Caddy WebSocket upgrade) | Transparent on same connection |
| `tcp` | Direct Access (iptables DNAT) | `publicIP:40001` → VM:22 |
| `udp` | Direct Access (iptables DNAT) | `publicIP:40002` → VM:8388 |
| `both` | Direct Access (TCP+UDP DNAT) | `publicIP:40003` → VM:8388 |

#### Impact by Template

| Template | Ports | Direct Access Allocated |
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

The Node Agent includes a unified proxy (`GenericProxyController`) that handles all ingress traffic to VMs, routing HTTP, WebSocket, and raw TCP through a single controller.

#### Routes

```
/api/vms/{vmId}/proxy/http/{port}/{**path}   HTTP/HTTPS proxy
/api/vms/{vmId}/proxy/ws/{port}              WebSocket tunnel (SSH, SFTP, etc.)
/api/vms/{vmId}/proxy/tcp/{port}             Raw TCP over WebSocket
```

**Examples:**
- `/api/vms/{vmId}/proxy/http/9999/challenge` — attestation agent
- `/api/vms/{vmId}/proxy/ws/22` — SSH over WebSocket
- `/api/vms/{vmId}/proxy/tcp/3306` — MySQL over WebSocket

#### Security: Allowed Ports

Access is restricted to infrastructure ports (22 SSH, 80 HTTP, 443 HTTPS, 9999 attestation) plus any ports defined in the VM's template `ExposedPorts`. Arbitrary port access is blocked.

---

### VM Lifecycle Management

**Status:** ✅ Complete (2026-02-09)
**Location:** `src/Orchestrator/Services/VmLifecycleManager.cs`

All confirmed VM state transitions flow through `VmLifecycleManager.TransitionAsync()`. Replaced 5 separate code paths that changed VM status with inconsistent side effects, removing ~460 lines of duplicated code.

#### Design Principles

1. **Single entry point** — every status change goes through `TransitionAsync`
2. **Validate → persist → effects** — invalid transitions rejected before any write
3. **Side effects keyed by (from, to) pair** — ingress setup, port allocation, template fee settlement, resource cleanup
4. **SafeExecuteAsync pattern** — each side effect individually isolated; one failure does not block others
5. **Persist-first** — status persisted before effects run; crash-safe by design

Notable fixes included in this refactor: PrivateIp timing race (IP now persisted before transition + 30s polling safety net), 5 call sites all routed through lifecycle manager (command ack, heartbeat, health check, timeout, manual admin action).

---

### Per-Service Readiness Tracking

**Status:** Orchestrator complete (2026-02-10) | NodeAgent implementation pending

Distinguishes "VM running" (hypervisor reports domain active) from "VM ready for service" (application-level services responding). Uses `qemu-guest-agent` — no direct network access to the VM is required.

#### Architecture

```
Template Creation (Orchestrator):
  VmTemplate.ExposedPorts[].ReadinessCheck
    ↓ VmService.BuildServiceList
  VirtualMachine.Services[]
    ↓ CreateVm command payload

Node Agent:
  CommandProcessorService parses services from payload
    ↓
  VmInstance.Services[] (persisted in SQLite)
    ↓
  VmReadinessMonitor polls via qemu-guest-agent (10s cycle)  ← PENDING
    ↓
  HeartbeatService reports services[] per VM (15s cycle)

Orchestrator:
  NodeService.ProcessHeartbeatAsync reads services[]
    ↓
  VirtualMachine.Services[] updated
    ↓
  Frontend displays per-service status
```

#### Service List Structure

Every VM has an ordered service list:
1. **System** (always first) — `CloudInitDone` check via `cloud-init status --format json`
2. **Template services** — one entry per `ExposedPorts` item

**Gating rule:** All non-System services wait for System to reach Ready before their own checks begin.

#### Check Types

| Type | Method | Example |
|---|---|---|
| `CloudInitDone` | `cloud-init status --format json` via guest agent | System service |
| `TcpPort` | `nc -zv -w2 localhost {port}` | PostgreSQL :5432 |
| `HttpGet` | `curl -sf -o /dev/null http://localhost:{port}{path}` | Stable Diffusion `/api/v1/sd-models` |
| `ExecCommand` | Arbitrary command, exit 0 = ready | `pg_isready` (600s timeout) |

Check type is auto-inferred from protocol (`http` → `HttpGet`, `tcp` → `TcpPort`) with explicit override support.

All checks execute inside the VM via `virsh qemu-agent-command` using `guest-exec` / `guest-exec-status` QMP protocol. Communication is through the virtio-serial channel — no VM network access needed.

#### Service Readiness States

```
Pending   → VM just started, waiting for System service
Checking  → Actively being probed
Ready     → Check passed (service accepting traffic)
TimedOut  → Timeout expired without passing
Failed    → cloud-init reported error (System service only)
```

#### Design Advantages

- **No direct VM network access** — qemu-agent-command works even if VM networking is misconfigured
- **Universal** — works for all VM types (general, relay, template-based, bare)
- **Template-extensible** — new templates automatically get readiness checks via protocol inference
- **Industry-aligned** — follows patterns from Kubernetes readiness probes, AWS CloudFormation cfn-signal

#### Pending Work

- NodeAgent `VmReadinessMonitor` background service (spec in `NODE_AGENT_READINESS_CHANGES.md`)
- Frontend per-service status display under VM details

---

## 4. Authentication & Security

**Status:** ✅ Production-ready

### Wallet-Based Authentication

All user identity is anchored to Ethereum wallet signatures. No usernames, no passwords, no KYC.

- Frontend: WalletConnect / Reown AppKit (MetaMask, Coinbase Wallet, WalletConnect QR)
- Backend: signature verification against wallet address
- Node identity: `SHA256(machineId + walletAddress)` — deterministic and stable

### SSH Access

Two modes:

1. **User-registered SSH keys** — preferred; user's public key injected via cloud-init
2. **Wallet-derived SSH keys** — derived from wallet signature when no key is registered; private key displayed once at VM creation

All SSH access uses certificate-based authentication via a per-platform CA. VM certificates carry principal `vm-{vmId}`, enforcing tenant isolation — User A's certificate cannot authenticate to User B's VM. **SECURITY NOTE:** SSH key injection into VMs is explicitly avoided; VMs validate certificates end-to-end.

**Files:** `SshCertificateService.cs`, `WalletSshKeyService.cs`

### VM Attestation

Each VM runs an attestation agent (`/usr/local/bin/decloud-agent`) exposing a challenge/response endpoint at port 9999. The orchestrator verifies VM identity before issuing certificates. Agent is deployed via cloud-init with `NoNewPrivileges`, `ProtectSystem=strict`, `PrivateTmp` hardening.

### Template Security Validation

`TemplateService` validates all community-submitted cloud-init scripts before publish:
- Fork bombs (`:(){ :|:& };:`)
- Destructive commands (`rm -rf /`)
- Untrusted pipe-to-shell patterns (`curl ... | bash`)

---

## 5. Economics

### Billing & Compute Pricing

**Status:** ✅ Production-ready

**Compute points:** Nodes benchmarked with sysbench and assigned normalized compute points. VMs priced per compute-point-hour.

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

Bandwidth limits enforced at the hypervisor level via libvirt QoS `<bandwidth>` elements on virtio NIC interfaces. Both x86_64 and ARM paths apply matching rules. Unmetered tier omits the element entirely (no artificial cap).

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
- **Floor enforcement:** `Math.Max(operatorRate, floorRate)` applied server-side
- **Rate lifecycle:** Initial rate uses platform defaults → recalculated with node-specific pricing after scheduling
- **Configuration:** Operators set pricing via `appsettings.json`, environment variables, or `PATCH /api/nodes/me/pricing`

---

## 6. Marketplace & Discovery

### Node Marketplace

**Status:** ✅ Complete (2026-01-30)

**API:** `/api/marketplace/nodes`

**Features:**
- Search and filter by tags, region, price, GPU, uptime, compute capacity
- Featured nodes (top 10 by uptime and capacity, requires 95%+ uptime and online status)
- One-click "Deploy VM" from any node card — pre-populates VM creation modal with target node ID
- Node detail modal with full hardware specs, reputation stats, pricing
- Operator profile management (`PATCH /api/marketplace/nodes/{id}/profile`)

---

### VM Template Marketplace

**Status:** ✅ Complete (2026-02-09)

**Implementation:** `TemplateService.cs` (715 lines), `TemplateSeederService.cs` (1,828 lines), `MarketplaceController.cs`

**Features:**
- Full template CRUD with community and platform-curated templates
- 5 seed categories: AI & ML, Databases, Dev Tools, Web Apps, Privacy & Security
- Cloud-init variable substitution (`${DECLOUD_VM_ID}`, `${DECLOUD_PASSWORD}`, `${DECLOUD_DOMAIN}`, etc.)
- Community templates: draft → publish workflow with security validation
- Paid templates: PerDeploy pricing (85/15 author/platform split via escrow)
- Template deployment hooks into per-service readiness tracking
- Frontend: `marketplace-templates.js`, `my-templates.js`, `template-detail.js`

**Seed templates (6):**

| Template | Category | GPU | Direct Access |
|---|---|---|---|
| Stable Diffusion WebUI | AI & ML | Required | None (CentralIngress) |
| PostgreSQL Database | Databases | No | ✅ TCP :5432 |
| VS Code Server (code-server) | Dev Tools | No | None (CentralIngress) |
| Private Browser (Neko/WebRTC) | Privacy & Security | No | None (CentralIngress) |
| Shadowsocks Proxy | Privacy & Security | No | ✅ Both :8388 |
| Web Proxy Browser (Ultraviolet) | Privacy & Security | No | None (CentralIngress) |

**Note on Web Proxy Browser:** Uses Ultraviolet service-worker proxy. Requires Cross-Origin Isolation (COOP/COEP headers) for SharedArrayBuffer/epoxy transport — nginx configured accordingly. Ephemeral privacy model: deploy, browse (traffic exits from VM IP), delete.

**Planned additional templates:** Nextcloud, Whisper AI, Ollama chatbot, Minecraft server, Jellyfin, VPN/Tor relay, Mastodon.

---

### Reputation System

**Status:** ✅ Core complete | Frontend polish pending

**Uptime tracking:**
- 30-day rolling window, precision to 15-second heartbeat intervals
- `FailedHeartbeatsByDay` dictionary on Node model (`{"2026-01-30": 245}`)
- Auto-cleanup removes data older than 30 days
- Formula: `uptime% = (expected – failed) / expected × 100`
- Integrated with `NodeHealthMonitorService` — no additional monitoring infrastructure

**Metrics tracked:**
1. Uptime percentage (30-day rolling)
2. Total VMs hosted (lifetime counter, increments on VM assignment)
3. Successful VM completions (clean terminations)

**Review system:**
- Universal: covers both nodes and templates (1–5 stars, title, comment)
- Eligibility-verified: proof of deployment/usage required before submission
- Denormalized aggregates on templates: `AverageRating`, `TotalReviews`, `RatingDistribution`
- API: submit review, get reviews, check user's own review

**Pending frontend work:** trust badges (99.9% uptime, 100+ VMs hosted), node rating aggregate display, review prompts after VM termination.

---

## 7. Monitoring & Health

### Node Health Monitoring

- Heartbeat every 15 seconds; 2-minute silence → node marked Offline
- All VMs on offline node marked Error, billing stops
- Failed heartbeats tracked per day (`FailedHeartbeatsByDay`), drives uptime calculation
- `NodeHealthMonitorService` runs every 30 seconds

**Failure handling:** Immediate Error + billing stop → user decides to wait for recovery or delete → on deletion, orchestrator triggers port cleanup on relay nodes.

### Relay Health Monitoring

- WireGuard tunnel status (peer handshake age): every 60 seconds
- Port mapping rule validity: every 5 minutes
- Bandwidth utilization, relay-CGNAT latency: every 3 minutes
- Automatic repair (`RelayRepairService`) triggered on degraded status

---

## 8. Advanced Compute

### GPU Proxy (CUDA Virtualization)

**Status:** ✅ Production-ready (2026-03-13)
**Verified workloads:** Ollama/ggml inference (436 tok/s), PyTorch inference, PyTorch training (backward + AdamW), LoRA fine-tuning via PEFT (1,038 tok/s, 1,360 MB VRAM)

**The problem:** Nodes without IOMMU (WSL2, consumer PCs, most VPS) cannot do GPU passthrough to VMs. This excludes the majority of GPU-equipped nodes from the network.

**The solution:** A CUDA virtualization layer — shim libraries inside the VM intercept all CUDA calls and forward them via TCP RPC to a daemon on the host that holds the real GPU.

#### Architecture

```
VM (Guest)                              Host
─────────────────────────────────       ────────────────────────
PyTorch / Ollama                        gpu-proxy-daemon
  ↓ LD_PRELOAD                            ↓ real CUDA runtime
libdecloud_cuda_shim.so                  nvidia-smi / libcuda.so
libcuda_pytorch_stubs.so
libcudart.so.12
  ↓ TCP RPC (vsock or TCP:9999)
  ────────────────────────────────────→
```

**LD_PRELOAD order (critical — must be in this order):**
```
libdecloud_cuda_shim.so:libcuda_pytorch_stubs.so:libcudart.so.12
```
Shim must precede PyTorch stubs to win symbol resolution.

#### Wire Protocol

- Magic: `0x44435544` ("DCUD"), Version: 2
- Transport: TCP (port 9999) or vsock (CID=2, port=9999)
- Auth: Token-based (per-VM tokens, SIGHUP reload)
- Header: 16 bytes (magic, version, cmd, flags, payload_len, status)
- Max payload: 2GB (streaming for large fatbins)
- **Critical:** `TCP_NODELAY` + `TCP_QUICKACK` (eliminates 40ms delayed ACK)

#### Command ID Map

| Range | Group | Commands |
|---|---|---|
| 0x01–0x05 | Device mgmt | GetDeviceCount, GetProperties, SetDevice, DriverVersion, UUID |
| 0x10–0x13 | Memory | Malloc, Free, Memcpy, Memset |
| 0x20–0x24 | Execution | LaunchKernel, DeviceSync, CtxCreate, MemGetInfo, CtxDestroy |
| 0x30–0x32 | Streams | Create, Destroy, Synchronize |
| 0x40–0x44 | Events | Create, Destroy, Record, Synchronize, ElapsedTime |
| 0x50–0x55 | Modules | RegisterModule/Function/Var, FuncGetAttributes, OccupancyMaxBlocks |
| 0x56–0x58 | cuBLAS | GemmBatched, GemmStrided, LtMatmul |
| 0x59 | Function | SetAttribute (flash-attention dynamic shared mem) |
| 0x60–0x61 | Resource mgmt | SetMemoryQuota, GetUsageStats |
| 0x70–0x77 | Virtual memory | VmemCreate/Release/Reserve/Free/Map/Unmap/SetAccess/GetGranularity |
| 0xF0–0xF1 | Lifecycle | Hello, Goodbye |

#### Environment Configuration Flags

All app-specific config driven by template `DefaultEnvironmentVariables` → written to `/etc/decloud/gpu-proxy.env` → shim constructor reads and applies:

| Flag | Default | Purpose |
|---|---|---|
| `DECLOUD_GPU_VMEM_PROXY` | 1 | Virtual memory APIs proxy to daemon — required for PyTorch |
| `CUDA_MODULE_LOADING` | `EAGER` | Forces eager `__cudaRegisterFunction` (CUDA 12 lazy loading bypass) |
| `TORCHINDUCTOR_DISABLE` | 1 | Disables torch.compile (requires kernel driver) |
| `PYTORCH_CUDA_ALLOC_CONF` | `max_split_size_mb:128,expandable_segments:False` | Tuned for proxy RPC efficiency |
| `DECLOUD_GPU_DEBUG` | (unset) | Gates all shim + stub debug logging |

**Note:** `DECLOUD_GPU_GRAPH_NOOP` was removed in Session 15. CUDA graphs now always return `cudaErrorNotSupported`, forcing apps to use direct kernel execution (see CUDA Graphs section below).

#### Critical Fixes Applied

| Bug | Fix | Impact |
|---|---|---|
| TCP delayed ACK (40ms/RPC) | `TCP_QUICKACK` re-armed before every `read()` | 150× speedup (0.07 → 436 tok/s) |
| CUDA 12 lazy loading | `CUDA_MODULE_LOADING=EAGER` forced via env | PyTorch module registration works |
| `maxThreadsPerMultiProcessor` wrong offset | Fixed to `raw+624` (0x270); `regsPerMultiprocessor=65536` at `raw+648` | Eliminated SIGFPE in GPT-2 sampling |
| `static` occupancy functions invisible to PLT | Non-static exported wrappers in `cuda_driver_shim.c` | cuOccupancyMaxActiveBlocksPerMultiprocessor works |
| cublasLt unconditional stderr logging | `DECLOUD_GPU_DEBUG` gate in `cublasLt_stub.c` | Clean output in normal operation |

#### CUDA 12.1 / PyTorch 2.3.1 Device Properties Offset Map (RTX 4060, SM8.9)

| Offset | Field | Value |
|---|---|---|
| 0x184 (388) | multiProcessorCount | 24 |
| 0x270 (624) | maxThreadsPerMultiProcessor | 1536 |
| 0x288 (648) | regsPerMultiprocessor | 65536 |
| 0x2c8 (712) | maxBlocksPerMultiProcessor | 1024 |
| 0x2d0 (720) | reservedSharedMemPerBlock | 65536 |

**CUDA 12 ABI note:** The real `cudaDeviceProp` has `uuid(16) + luid(8) + luidDeviceNodeMask(4) + pad(4) = 32 bytes` between `name[256]` and `totalGlobalMem`. Without accounting for this, all raw offset writes land 32 bytes early, causing fields like `maxThreadsPerMultiProcessor` to read as 0 → integer divide-by-zero SIGFPE.

#### CUDA Graphs Strategy

CUDA graphs cannot be faithfully proxied — capture records host-side API calls but execution is remote. Attempts at capture/replay emulation produced gibberish output (unreproducible scheduling semantics).

**Current approach (Session 15, 2026-03-17): Pass-through mode**

The proxy honestly reports that CUDA graphs are not supported, forcing applications to fall back to direct kernel execution:
- `cudaStreamBeginCapture` → `cudaErrorNotSupported`
- `cuStreamBeginCapture` → `CUDA_ERROR_NOT_SUPPORTED`
- `cudaStreamEndCapture` → `cudaErrorNotSupported`
- `cudaStreamIsCapturing` → `cudaStreamCaptureStatusNone`
- `cudaGraphInstantiate` → `cudaSuccess` + dummy handle (harmless no-op)
- `cudaGraphLaunch` → `cudaSuccess` (harmless no-op)

Applications with fallback paths (ggml, PyTorch) automatically switch to direct kernel execution. Both the runtime and driver shims must return consistent results because libcudart resolves `cudaStreamBeginCapture` through the driver API (`cuGetProcAddress`).

#### Key Design Decisions

- **TCP_QUICKACK** — must be re-armed before every `read()` (Linux resets per-operation); combined with `TCP_NODELAY` achieves sub-ms RPC latency
- **Deferred + eager module upload** — fat binaries stored locally at `__cudaRegisterFatBinary`, uploaded to daemon only when needed; `cudaFuncGetAttributes` triggers eager upload because ggml queries attributes before first launch
- **Streaming module upload** — 1.56GB fatbin from libggml-cuda.so written directly from mmap'd memory (zero copy, zero malloc)
- **cuBLAS GEMM proxy** — cuBLAS init requires `cuGetExportTable` (private NVIDIA internals, cannot be proxied); stub init, use ggml's MMQ path, proxy only `cublasGemmBatchedEx`/`cublasGemmStridedBatchedEx` for GQA attention
- **Real GPU attributes via RPC** — `cu_func_get_attribute` queries daemon, caches per-function in `DriverFunctionSlot`, falls back to safe defaults; eliminates hardcoded SM8.9 vendor dependency

#### Deployment

Fully automated via cloud-init. `install.sh` builds shims and daemon on host, exposes via 9p share. Cloud-init copies shims into VM, replaces bundled CUDA libs (PyTorch ships its own `libcublas.so.12` — terminal scan replaces all with stubs), writes transport config, restarts application. Zero manual steps.

**GoBinaryBuildStartupService** (`install.sh`) handles: Docker compat build (glibc 2.31 for universal compatibility), native daemon build, stale daemon kill + restart with captured args, libcudart.so.12 sync, 9p share freshness verification, symbol count verification for all stubs.

#### Implementation Files

| File | Purpose |
|---|---|
| `shim/cuda_shim.c` | Runtime API shim (~1,900 lines) |
| `shim/cuda_driver_shim.c` | Driver API shim (~1,800 lines) |
| `shim/transport.c` | TCP/vsock transport with QUICKACK |
| `stubs/cublasLt_stub.c` | cublasLtMatmul stub (29 versioned symbols) |
| `stubs/cublas_stub.c` | cuBLAS stub (20+ versioned symbols) |
| `stubs/libcudnn_stub.c` | cuDNN stub (87 versioned symbols) |
| `stubs/libcuda_pytorch_stubs.c` | PyTorch compat stubs (cudaMallocAsync, 22 more) |
| `daemon/gpu_proxy_daemon.c` | Host daemon (~2,100 lines) |
| `proto/gpu_proxy_proto.h` | Wire protocol definitions |
| `LibvirtVmManager.cs` | `EnsureGpuProxyShim` — cloud-init injection |
| `GpuProxyService.cs` | Daemon lifecycle management |
| `install.sh` | Build pipeline + symbol count verification |

---

### DHT Infrastructure

**Status:** ✅ Production-verified (2026-02-15)

A libp2p-based DHT layer provides decentralized peer coordination. DHT nodes run as lightweight VMs connected via WireGuard mesh over the relay tunnel network.

#### Verified Deployment

- **Node 1** (us-east-1): peerId `12D3KooWD8zw...B8n1`, tunnelIP `10.20.1.199`, connectedPeers: 1
- **Node 2** (tr-south): peerId `12D3KooWHNLM...j8Yx`, tunnelIP `10.20.1.202`, connectedPeers: 1

#### End-to-End Flow

1. Orchestrator deploys DHT VM with WG mesh labels (`wg-relay-endpoint`, `wg-relay-pubkey`, `wg-tunnel-ip`, `wg-relay-api`)
2. Cloud-init writes labels to `/etc/decloud/wg-mesh.env` and runs `wg-mesh-enroll.sh`
3. Enrollment script generates WG keypair, registers with relay, starts `wg-mesh` interface
4. `dht-bootstrap-poll.sh` calls `POST /api/dht/join` → receives known peer list
5. DHT binary connects to peers over WireGuard mesh (e.g., `10.20.1.199:4001`)
6. DHT VM calls `POST /api/dht/ready` to report its libp2p peer ID

#### WireGuard Mesh Enrollment

`wg-mesh-enroll.sh` uses a two-strategy approach:
1. **Strategy 1 (primary):** NodeAgent proxy via virbr0 default gateway (`POST http://<gateway>:5100/api/relay/wg-mesh-enroll`)
2. **Strategy 2 (fallback):** Direct relay API (`POST http://<relay-ip>:8080/api/relay/add-peer`)

**NodeAgent WG Mesh Enrollment Proxy (`WgMeshEnrollController`):** DHT VMs cannot reach relay port 8080 (only UDP/51820 is NAT-forwarded). The NodeAgent proxy:
1. DHT VM discovers NodeAgent via virbr0 default gateway (port 5100)
2. NodeAgent finds relay VM's bridge IP via `IPortForwardingManager.GetRelayVmIpAsync()`
3. NodeAgent forwards enrollment to `http://<relay-bridge-ip>:8080/api/relay/add-peer`

For CGNAT hosts without a local relay VM, proxy discovers the relay tunnel gateway IP (10.20.x.254) from the host's WireGuard interface addresses.

```csharp
// Two-strategy relay discovery in WgMeshEnrollController
var relayIp = await _portForwardingManager.GetRelayVmIpAsync(ct);        // co-located relay VM
var relayTunnelIp = await DiscoverRelayTunnelGatewayAsync(ct);            // CGNAT host
```

#### Critical Bug Fixes (2026-02-15)

These two bugs prevented WireGuard mesh enrollment from working and are important references for understanding the architecture.

**Bug #1: Environment variables not exported to child process**

*Symptom:* `wg-mesh-enroll.sh` logged `ERROR: Missing required env: WG_RELAY_ENDPOINT` despite `/etc/decloud/wg-mesh.env` containing correct values.

*Root cause:* Cloud-init runcmd used `bash -c 'source wg-mesh.env && bash wg-mesh-enroll.sh'`. The `source` command set shell variables in the parent `bash -c` context, but spawning `bash wg-mesh-enroll.sh` as a child process did not inherit them (shell variables are not exported by default).

*Fix:*
```bash
# Before (broken):
bash -c 'source /etc/decloud/wg-mesh.env && bash /usr/local/bin/wg-mesh-enroll.sh'

# After (fixed):
bash -c 'set -a && source /etc/decloud/wg-mesh.env && set +a && /usr/local/bin/wg-mesh-enroll.sh'
```
Also modified `wg-mesh-enroll.sh` to self-source the env file with `set -a` as belt-and-suspenders.

**Bug #2: Relay API port 8080 unreachable from DHT VM**

*Symptom:* Even with env vars fixed, `curl http://<relay-public-ip>:8080/api/relay/add-peer` failed.

*Root cause:* Both relay and DHT VMs use libvirt's `default` network (NAT via virbr0). The relay VM's port 8080 is only accessible from the host via the bridge IP — not from other VMs via the public IP. Only UDP/51820 is NAT-forwarded.

*Fix:* Created `WgMeshEnrollController` proxy on the NodeAgent (described above).

#### DHT Ready Callback

**Endpoint:** `POST /api/dht/ready` on NodeAgent → `DhtCallbackController`

1. **Authentication:** HMAC-SHA256 token using machine ID as secret (`X-DHT-Token` header)
2. **Service update:** Marks VM's System service as `Ready` with `peerId=<libp2p-peer-id>`
3. **Persistence:** Stores peer ID to `/var/lib/decloud/vms/{vmId}/dht-peer-id` for heartbeat reporting
4. **Idempotency:** Updates peer ID even if already Ready (handles race with cloud-init readiness monitor)

#### Implementation Files

| File | Purpose |
|---|---|
| `DhtNodeService.cs` | DHT VM deployment (Orchestrator) |
| `DhtController.cs` | `/api/dht/join` bootstrap endpoint |
| `DhtCallbackController.cs` | `/api/dht/ready` ready callback (NodeAgent) |
| `WgMeshEnrollController.cs` | WireGuard enrollment proxy (NodeAgent) |
| `dht-vm-cloudinit.yaml` | DHT VM cloud-init template |
| `wg-mesh-enroll.sh` | Two-strategy WireGuard enrollment |
| `dht-bootstrap-poll.sh` | Bootstrap peer polling (runs in VM) |

---

### Block Store & Storage Economics

**Status:** Phase A–C ✅ Production-verified (2026-03-20) | Phase D–E (Lazysync + Live Migration) ✅ Complete (2026-04-20)

#### Phase D–E: Lazysync & Live Migration

**What was built:**
- `LazysyncDaemon` — background service running on each node; every 5 minutes exports VM overlay disk in 1 MB content-addressed chunks and pushes new/changed blocks to the local BlockStore VM
- Incremental dirty bitmap (`block-dirty-bitmap-add` via QMP) — after first full export, only clusters written since last cycle are exported via `drive-backup sync=incremental`
- `RemoveDirtyBitmapAsync` — cleans the overlay-carried bitmap on the receiving node before re-adding, preventing `AddDirtyBitmap failed` fallback to full export every cycle
- Node-offline detection triggers automatic migration: `MarkNodeVmsAsErrorAsync` classifies VMs as `Migrating` / `Recovering` / `Unrecoverable` / `Lost` based on manifest confirmed state
- `MigrateVmAsync` in `BackgroundServices.cs` — selects best target node, fetches `MigrationManifest` (confirmed chunk map), sends `ProvisionVm` command with `OverlayChunkMap` to reconstruct the overlay
- Migration cloud-init on receiving node: `package_update/upgrade: false`, `bootcmd` overwrites MAC-pinned netplan (`/etc/netplan/50-cloud-init.yaml`) before networkd starts — prevents `network-online.target` hanging forever
- Ingress registered before `WaitForPrivateIpAsync` — domain live immediately after migration boot
- Post-migration reseed: `ConfirmedVersion` reset to 0, `ReseedVm` command sent, LazysyncDaemon re-pushes all blocks so blockstore network replicates to RF providers on new node

**Confirmed end-to-end (2026-04-20):**
- VM created on MSI (Turkey, CGNAT) → versions 1–9 replicated incrementally to blockstore
- MSI shutdown → orchestrator classifies VM as `Migrating` → overlay reconstructed on dedixlabvm (US East) from chunk map
- Post-migration: user files preserved, browser terminal functional, ingress subdomain live, incremental replication resumes

**Fix (2026-04-24) — GossipSub fetch retry queue (item 31b):**
Freshly booted blockstores hit the 30s BitswapTimeout on all initial GossipSub-triggered
fetches because DHT provider records for new blocks propagate ~15s after GossipSub publish,
creating a race on cold routing tables. Failed CIDs were silently dropped with no recovery
path. Fix: `retryQueue` + `startRetryLoop` in `main.go` — enqueues failed CIDs, retries
sequentially every 60s with 60s timeout, max 3 attempts. Requires blockstore VM redeployment.

#### Overview

Every eligible node (≥100 GB storage, ≥2 GB RAM) runs a Block Store VM as a network duty obligation, contributing 5% of total storage to a distributed content-addressed storage network.

**Two purposes:**
1. **VM resilience** — overlay disks continuously replicated; failed VMs reconstructable on another node without data loss
2. **AI model distribution** — LLMs chunked and distributed, enabling decentralized model serving and pipeline-parallel inference

#### Variable Chunk Sizes

| Manifest Type | Chunk Size | Rationale |
|---|---|---|
| `vm-overlay` | 1 MB | Aligns with QEMU dirty bitmap granularity. ~5,120 chunks for 5 GB overlay. Content-addressed means unchanged regions never re-transferred. |
| `model-shard` | 64 MB | Aligns with transformer layer boundaries. Llama-3 70B Q4 (~40 GB) = 640 chunks. |

#### Block Store VM Spec

```csharp
VirtualCpuCores = 1
MemoryBytes     = 512 MB
DiskBytes       = 5% of node total storage (StorageDutyFraction = 0.05)
QualityTier     = Burstable
ImageId         = "debian-12-blockstore"
MinNodeStorageBytes = 100 GB  // eligibility threshold
```

#### Block Store Binary Features

- libp2p host with persistent Ed25519 identity
- Bitswap client+server for block exchange
- FlatFS backend (content-addressed flat files)
- Storage quota enforcement (refuse writes when full)
- GossipSub subscription (`decloud/blockstore/new-blocks`) for near-instant block discovery
- Adaptive XOR threshold pull logic (closer DHT distance + more free space → more aggressive pulling)
- Periodic DHT neighborhood scan (durable fallback for missed GossipSub messages)
- Localhost HTTP API on port 5090
- Garbage collection (LRU eviction within 5% budget)

#### Authentication

```
Orchestrator → VM:  auth token via cloud-init labels
VM → Orchestrator:  HMAC-SHA256(authToken, nodeId:vmId) via X-BlockStore-Token
VM → NodeAgent:     HMAC-SHA256(machineId, vmId:peerId) via X-BlockStore-Token
```

---

## 9. Node Operations

### Resource Management

**CPU benchmarking:**
```bash
sysbench cpu --threads=$(nproc) --time=10 run
```
Normalized against a reference baseline. Points scale linearly with core count and per-core performance.

**Resource discovery:** CPU (cores, model, frequency), RAM, GPU (model, VRAM, IOMMU group, passthrough eligibility), storage (disks, available space), network bandwidth (ethtool or sysfs reported speed, falls back to 1000 Mbps conservative default).

**Quality tier allocation:** `computePoints = (nodePerformance / baselinePerformance) × coreCount`. Used for scheduling, billing, and marketplace display.

---

### Windows WSL2 Auto-Start

**Status:** ✅ Complete (2026-04-05)

Enables Windows users running the node agent inside WSL2 to survive reboots and crashes without manual intervention.

#### The Problem

WSL2 does not start automatically on Windows boot. If an operator runs the node agent inside WSL2, it stops whenever Windows restarts or WSL is shut down — requiring manual re-entry.

#### Solution: Windows Scheduled Task Watchdog

A single self-contained `DeCloud-Node-Setup.bat` (~23 KB) distributed via the `releases/` folder. Double-clicking handles UAC elevation internally and installs a Windows Scheduled Task (runs as SYSTEM) that:

1. Fires at Windows boot (30-second startup delay) and every 5 minutes (self-healing fallback)
2. Ensures the WSL2 Ubuntu distro is running
3. Enables systemd in WSL automatically if not already configured (`[boot] systemd=true` in `/etc/wsl.conf`)
4. Starts or restarts `decloud-node-agent` via systemctl if not active
5. Backs off exponentially after repeated failures (5 consecutive → 120-second pause)
6. Rotates its own log file at 10 MB cap
7. Uses a PID lock file to prevent duplicate watchdog loops when the 5-min trigger fires while a previous instance is still running

The watchdog script (`DeCloud-WslWatchdog.ps1`) is embedded as base64 in the `.bat` and extracted to `C:\ProgramData\DeCloud\` at install time. Install directory ACL-locked to SYSTEM and Administrators only.

#### install.sh Integration

When `install.sh` detects WSL2 (via `/proc/version` and `/dev/dxg`), `print_summary` automatically:
- Downloads `DeCloud-Node-Setup.bat` to the Windows Desktop via `cmd.exe` interop and `wslpath`
- Shows a clickable OSC 8 hyperlink in Windows Terminal pointing to the downloaded file
- Falls back to manual instructions if download fails

WSL2 detection uses `detect_wsl2()` — a standalone function called early in `main()`, independent of GPU detection (fixing a prior bug where `IS_WSL2` was only set when a GPU was present).

#### Accessing the CLI

While the watchdog keeps the agent running as SYSTEM in the background, users connect interactively at any time from Windows Terminal / PowerShell by typing `wsl`. This attaches a new session to the running WSL instance without affecting the background agent.

#### Key Files

| File | Purpose |
|---|---|
| `releases/DeCloud-Node-Setup.bat` | Single-file Windows installer (double-click to run) |
| `install.sh → detect_wsl2()` | Standalone WSL2 detection, runs early in `main()` |
| `install.sh → stage_windows_installer()` | Downloads `.bat` to Windows Desktop via WSL interop |
| `install.sh → make_hyperlink()` | Emits OSC 8 clickable terminal link |
| `install.sh → print_summary()` | WSL2 notice block (conditional on `IS_WSL2=true`) |

---

## 10. Compliance & Legal Framework

**Status:** 🔲 Planned — Pre-Launch Requirement
**Reference:** Full specification in `COMPLIANCE.md`

DeCloud's censorship-resistance mission protects political speech, privacy, and legitimate AI workloads. It explicitly does not protect content that is illegal in every jurisdiction — CSAM, illegal marketplaces, malware infrastructure. The compliance framework draws this line operationally without compromising the platform's core value proposition.

The framework rests on four pillars, all required before public launch:

```
┌─────────────────────────────────────────────────────────────────┐
│                  DeCloud Compliance Stack                        │
├─────────────────┬──────────────────┬──────────────┬────────────┤
│  CSAM           │  Terms of        │  Abuse       │  Template  │
│  Proactive      │  Service         │  Reporting   │  Review    │
│  Filtering      │  (with teeth)    │  + AI Triage │  Gate      │
│                 │                  │              │            │
│  Keeps platform │  Legal standing  │  Reactive    │  Blocks    │
│  out of federal │  to act +        │  detection + │  amplifi-  │
│  criminal       │  DMCA safe       │  triage +    │  cation of │
│  exposure       │  harbor          │  SLA queue   │  harm at   │
│                 │                  │              │  source    │
└─────────────────┴──────────────────┴──────────────┴────────────┘
```

---

### CSAM Proactive Filtering

**Status:** 🔲 Planned — Pre-Launch Requirement (Non-Negotiable)

**Why non-negotiable:** CSAM exposure carries federal criminal liability (18 U.S.C. § 2258A). It is the only compliance failure that cannot be remediated retroactively.

**Implementation:** Hash-based detection at Block Store ingestion using the NCMEC PhotoDNA hash database. No content inspection — only hash comparison against known illegal material. Preserves the censorship-resistance architecture entirely.

**Detection response:**
1. Block quarantined immediately (removed from serving, preserved for evidence)
2. NCMEC CyberTipline report filed within 24 hours (legally required)
3. Associated wallet blacklisted, all VMs terminated
4. Incident written to enforcement audit trail

**Limitation:** Catches known material only. The Template Review Gate is the complementary control for generation pipelines that could produce new material.

**Key files to create:**

| File | Purpose |
|---|---|
| `NcmecHashService.cs` | Hash lookup at Block Store ingestion |
| `CsamQuarantineService.cs` | Block quarantine and CyberTipline reporting |

---

### Terms of Service

**Status:** 🔲 Planned — Pre-Launch Requirement

**What it achieves:**
- Legal standing to suspend wallets, forfeit escrow, and terminate VMs without those actions being challengeable as arbitrary
- DMCA Section 512 safe harbor prerequisite
- Framework for law enforcement cooperation
- Section 230 / Article 14 (EU) shield

**Wallet-signed acceptance** — cryptographically verifiable, not just a checkbox:
```
Wallet connects →
Platform presents ToS + document hash →
User signs: { tosVersion, tosHash, walletAddress, timestamp } →
Signature stored in TosAcceptances collection →
VM creation gated until signature confirmed
```

**Required wallet-auth specific clauses:**
- **User responsibility** — all content associated with the wallet address is the user's sole responsibility
- **Escrow forfeiture** — violation may result in forfeiture of escrow balance toward damages, no court order required
- **Blockchain transparency** — wallet address and on-chain history shareable with law enforcement upon valid legal process
- **Prohibited content** — explicit enumeration: CSAM, illegal marketplaces, C2 infrastructure, malware hosting, human trafficking facilitation
- **Law enforcement cooperation** — platform will cooperate with valid legal process including NCMEC CyberTipline mandates
- **Repeat infringer termination** — required for Section 512 DMCA safe harbor

**Key models/collections to create:**

| Item | Detail |
|---|---|
| `TosAcceptance` model | `{ walletAddress, tosVersion, tosHash, signature, signedAt }` |
| `TosAcceptances` MongoDB collection | Queried at VM creation to verify current acceptance |
| ToS version bump flow | Material changes prompt re-signature; VM creation blocked after 30 days without re-sign |

---

### Abuse Reporting & AI Triage

**Status:** 🔲 Planned — Pre-Launch Requirement

**Public intake endpoint:** `POST /api/abuse` — unauthenticated, anyone can submit.

**Urgency tiers:**

| Category | Priority | SLA | Auto-Action |
|---|---|---|---|
| CSAM | P0 — Immediate | 2 hours | Block quarantine, NCMEC flag |
| Active malware / C2 | P1 — Critical | 4 hours | None (human decision) |
| Illegal marketplace | P1 — Critical | 8 hours | None (human decision) |
| DMCA copyright | P2 — Standard | 48 hours | None (human decision) |
| ToS violation | P3 — Normal | 72 hours | None (human decision) |
| Spam / low-quality | P4 — Low | Best effort | None |

**AI triage pipeline** (AI-assisted, always human-decided):
1. Incoming report → AI classifies category and assigns urgency
2. For template reports: AI re-analyzes the template specifically through the lens of the reported concern
3. Multiple reports about the same resource are aggregated and summarized for the reviewer
4. Human reviewer sees AI assessment, reported resource, and wallet enforcement history
5. Human makes all enforcement decisions — AI never acts autonomously

**Prompt injection hardening:** The AI receives untrusted user content. System prompt explicitly treats all report and template content as untrusted input. Output is always structured JSON, never interpreted as instructions.

**Enforcement audit trail:** Every action logged to append-only `EnforcementActions` collection — retained indefinitely, never updated or deleted.

**Key files to create:**

| File | Purpose |
|---|---|
| `AbuseController.cs` | `POST /api/abuse` public intake |
| `AiReviewService.cs` | AI triage + template review (shared service, two methods) |
| `AbuseReport.cs` | MongoDB model with reference ID, category, urgency, triage result |
| `EnforcementAction.cs` | Append-only audit log model |

---

### Template Review Gate

**Status:** 🔲 Planned — Pre-Launch Requirement

**Why highest-leverage:** A single malicious public template can be deployed by thousands of users. Blocking one template blocks thousands of potential deployments. The marketplace is the primary amplification risk vector.

**Review workflow:**
```
Author submits → Draft
Author requests publish → PendingReview
AI review runs (async) → AI assessment stored on template
Admin reviews AI assessment + template → Published / Rejected / Request Changes
```

Community templates always go through this workflow. Publish is not automatic on author request — human admin approval is required.

**AI review scope:**
- Intent vs. description coherence (does the script actually do what it claims?)
- Obfuscated commands (base64 payloads, eval chains, aliased shell functions, heredoc encoding)
- Data exfiltration patterns (phone-home scripts, unexpected outbound connections, credential harvesting)
- Known malicious tooling signatures (C2 frameworks, RAT components, cryptominer installers)
- Category coherence (an "AI/ML" template installing network scanners is misclassified)
- Prompt injection attempts in template content (itself a red flag, logged as a concern)

**AI output stored on `VmTemplate` document:**
```json
{
  "riskLevel": "Low | Medium | High | Reject",
  "concerns": ["string"],
  "recommendation": "Approve | RequestChanges | Reject",
  "reasoning": "string",
  "reviewedAt": "ISO 8601"
}
```

**Changes to existing infrastructure:**

| Item | Change |
|---|---|
| `TemplateStatus` enum | Add `PendingReview` between `Draft` and `Published` |
| `VmTemplate` model | Add `AiAssessment` field (nullable) |
| `TemplateService.PublishTemplateAsync` | Gate on admin-set `IsVerified`, not just author request |
| Existing `ValidateTemplateAsync` | Remains as fast pre-filter; AI review runs after it passes |

**Retroactive review required:** The Private Browser (Ultraviolet) and Shadowsocks seed templates predate this framework and must be reviewed against the rubric before public launch. Platform-authored templates are not exempt.

---

### Enforcement Mechanism

**Status:** 🔲 Planned — Pre-Launch Requirement

**Wallet blacklist** — `SuspendedWallets` MongoDB collection, checked at VM scheduling:

```csharp
// Added to VmService.CreateVmAsync before node scheduling
var isSuspended = await _dataStore.IsWalletSuspendedAsync(userId);
if (isSuspended)
    throw new UnauthorizedAccessException("Account suspended. Contact support.");
```

**Admin takedown endpoint** — single atomic call orchestrating all enforcement steps:

```
POST /api/admin/takedown
Authorization: Bearer <admin-token>

{
  "walletAddress": "0x... (optional)",
  "vmId": "string (optional)",
  "templateId": "string (optional)",
  "reason": "string (required)",
  "category": "csam | dmca | malware | tos_violation (required)",
  "reportReference": "ABU-2026-00123 (optional)"
}
```

Orchestrates in sequence: VM termination → template archiving → wallet suspension → audit log entry. Returns a summary of all actions taken.

**Escrow forfeiture** — deliberately separate from takedown (irreversible, requires proportionality judgment):

```
POST /api/admin/escrow-forfeiture
{ walletAddress, amount?, reason, enforcementActionId }
```

Uses the existing `DeCloudEscrow.sol` authorized caller mechanism. The ToS escrow forfeiture clause establishes the legal basis — no court order required.

**Key files to create:**

| File | Purpose |
|---|---|
| `SuspendedWallet.cs` | MongoDB model `{ walletAddress, reason, suspendedAt, suspendedBy, enforcementActionId, isPermanent, expiresAt }` |
| `EnforcementAction.cs` | Append-only audit log `{ actionId, reportReference, actionType, targetWallet, targetVmId, targetTemplateId, reason, category, actingAdmin, timestamp }` |
| `AdminComplianceController.cs` | `POST /api/admin/takedown` and `POST /api/admin/escrow-forfeiture` |
| `WalletBlacklistService.cs` | Suspension check + blacklist management |

---

### DMCA Agent & Safe Harbor

**Status:** 🔲 Planned — Pre-Launch Requirement (Primarily Administrative)

**One-time filing:** Register a DMCA Designated Agent with the US Copyright Office at https://www.copyright.gov/dmca-directory/ ($6 USD). Grants Section 512 safe harbor — without it the platform is fully liable for all copyright infringement hosted by users.

**DMCA intake flow:**
1. Copyright holder submits notice via `POST /api/dmca` or published agent email
2. AI triage classifies as P2, assigns 48h SLA, returns reference ID to claimant
3. Human reviewer validates notice (required elements: copyrighted work identification, infringing material identification, contact info, good-faith statement, accuracy statement, signature)
4. If valid: template archived or VM flagged, wallet warned, claimant notified within 48h
5. Counter-notice process available to affected users — content restored after 10–14 business days if claimant does not file suit

**ToS requirement:** Section 512 safe harbor also requires the ToS to state that repeat infringers will be terminated. This clause must be present in the ToS document.

**Pre-launch administrative checklist:**
- [ ] Retain legal counsel familiar with CFAA, DMCA, and 18 U.S.C. § 2258A
- [ ] File DMCA Designated Agent (https://www.copyright.gov/dmca-directory/)
- [ ] Establish NCMEC CyberTipline account and reporting procedure
- [ ] Draft and legally review ToS document
- [ ] Retroactive rubric review of Private Browser and Shadowsocks seed templates

---

*For the complete compliance specification — wallet identity analysis, full liability framework, build checklist, and operational procedures — see `COMPLIANCE.md`.*

---

## 11. Planned Features

### Prebuilt Binary Distribution

**Status:** 🔲 Planned | **Tracked:** `# TODO(future)` in `install.sh` above `download_node_agent()`
**Effort:** Low (~1 week, primarily GitHub Actions + install.sh changes)

**Current flow:** `git clone` → `dotnet publish` → ~5 min build, requires .NET SDK on every node
**Target flow:** `curl` GitHub Release asset → extract → ~30 seconds, no SDK needed

**What is needed:**
1. GitHub Actions release workflow — publishes `linux-amd64` and `linux-arm64` tarballs on every `git tag` push, bundling the DHT, BlockStore, and GPU proxy binaries
2. Download URL in `install.sh` replacing the current `git clone` + build steps
3. Removal of `install_dotnet` (SDK) from `main()` — only the .NET runtime is needed to run the agent
4. `releases/` folder in git hosts distributable scripts only; compiled binaries go to GitHub Release assets

---

### Lightweight Node Support

**Status:** 🔲 Planned
**Strategic value:** Dramatically expands the supply side — any machine can contribute to the network.

Nodes without KVM hardware virtualization (VPS with disabled nested virt, Raspberry Pi, old laptops, mobile via Termux) currently cannot run full VMs. Under QEMU TCG software emulation, a single DHT VM takes 1–2 hours to boot. These nodes are excluded entirely — but can contribute meaningfully without VMs.

**Proposed capabilities:** native process execution (workloads run directly on host), Docker container hosting, DHT participation (always possible), block store contribution (storage-only nodes).

**Key constraint:** Security model changes — no VM isolation means different trust guarantees. Full design TBD.

---

### Alpine Linux System VMs

**Status:** 🔲 Planned (two-phase approach)
**Motivation:** Current system VMs (relay, DHT, block store) use `debian-12-generic` (~427 MB). Alpine cloud image is ~50 MB — 40–70× smaller, sub-5-second boot.

#### Image Progression

| Image | Size | Boot (KVM) | NoCloud ISO detection | Status |
|---|---|---|---|---|
| `debian-12-generic` | ~427 MB | ~20s | ✅ Reliable — AHCI compiled into kernel | **Current** |
| `debian-12-genericcloud` | ~334 MB | ~10s | ❌ Broken — AHCI as module, timing race | **Rejected (2026-03-28)** |
| `alpine-3.x` | ~50 MB | ~5s | ✅ Reliable — virtio/AHCI compiled in + `tiny-cloud` seeds from `/var/lib/cloud/seed/nocloud/` | **Target** |

**Why `debian-12-genericcloud` was rejected:** uses `linux-image-cloud-amd64` where AHCI drivers are loadable modules. `cloud-init-local` runs before `udev` finishes loading SATA modules — `blkid -tLABEL=cidata` returns nothing. Injecting `datasource_list` got cloud-init to run but the NoCloud probe still calls `blkid` internally — same race, same failure. The correct fix (`InjectCloudInitSeedAsync`) requires a guestmount step in `LibvirtVmManager`. Alpine is immune: `tiny-cloud` checks the seed path first with no block device scan.

**Defensive fix added to `CloudInitCleaner`:** injects `datasource_list: [NoCloud, None]` into every base image during cleaning, preventing future Debian point releases from silently breaking this.

#### Phase 1 — Alpine + systemd (low effort)

Install `systemd` on Alpine cloud image; test existing cloud-init templates unchanged. Overhead: ~50 MB extra. Alpine's `tiny-cloud` doesn't use `ds-identify` so `datasource_list` injection issue doesn't apply.

#### Phase 2 — Full OpenRC Rewrite (high effort, maximum benefit)

Rewrite all three system VM cloud-init templates to use OpenRC natively. True ~50 MB footprint. Boot time under 5 seconds. New image IDs: `alpine-relay`, `alpine-dht`, `alpine-blockstore`.

**Phase 2 prerequisite:** `InjectCloudInitSeedAsync` in `LibvirtVmManager` — writes `user-data` + `meta-data` directly into the overlay at `/var/lib/cloud/seed/nocloud/` before VM start. Eliminates cidata ISO timing dependency for all image types.

#### What Works Out of the Box on Alpine

All required packages available via `apk`: `wireguard-tools`, `nginx`, `python3`, `qemu-guest-agent`, `curl`, `jq`, `openssl`.

#### What Requires Changes

1. **Package manager:** `apt-get` → `apk add` in `packages:` blocks
2. **Init system:** Alpine defaults to OpenRC, not systemd. Templates use `systemctl`, `wg-quick@` units extensively. Options: install systemd on Alpine (~50 MB extra) or rewrite to `rc-update`/`rc-service` (Phase 2)
3. **`tiny-cloud` variant:** most directives work (`packages`, `write_files`, `runcmd`, `bootcmd`) but needs validation
4. **WireGuard:** included in Alpine kernel since 5.15; `wg-mesh-enroll.sh` needs minor path adjustments (`/etc/init.d/` vs `/etc/systemd/`)

#### Files to Change

| File | Change |
|---|---|
| `ArchitectureHelper.cs` | Add Alpine image URLs for amd64 + arm64 |
| `VmService.cs` | Add `alpine-*` → URL mappings |
| `DataStore.cs` | Register Alpine image entries |
| `LibvirtVmManager.cs` | Add `InjectCloudInitSeedAsync` |
| `CloudInitCleaner.cs` | Already updated — injects `datasource_list` + removes `cloud-init.disabled` |
| `relay-vm-cloudinit.yaml` | `apt` → `apk`, `systemctl` → `rc-service` (Phase 2 only) |
| `dht-vm-cloudinit.yaml` | Same |
| `blockstore-vm-cloudinit.yaml` | Same |

This feature pairs naturally with **Lightweight Node Support** — Alpine is ideal for native process deployment on lightweight nodes.

---

### Phase 3+ Roadmap

#### Shared VMs (Multi-Wallet Access)
Multiple wallets can access the same VM. Enables team development environments. `authorizedWallets` list on VM model; owner manages collaborators.

#### Infrastructure Templates (Multi-VM)
One-click deployment of interconnected VM stacks (e.g., web + database + Redis + load balancer). Templates define VM relationships and networking.

#### Live Network Visualization
Interactive globe/map showing nodes, VMs, and relay connections in real time. Public-facing for marketing.

#### ✅ Live VM Migration — Complete (2026-04-20)
Automatic VM migration when source node goes offline. Full pipeline: LazysyncDaemon incremental replication → node-offline detection → overlay reconstruction from chunk map → migration cloud-init (MAC-pinned netplan fix + minimal package config) → ingress registration → dirty bitmap reset on receiving node. Confirmed end-to-end: user files preserved, browser terminal and ingress functional post-migration.

#### Optional Premium Node Staking
XDE token staking for premium marketplace placement. Strictly optional — free tier always available. Must earn reputation first (95%+ uptime, 10+ completions). Stake lockable, not slashable (security deposit model).

#### Advanced Analytics Dashboard
Deep metrics for node operators: historical uptime trends, earnings projections, competitive benchmarking, resource utilization analytics.

---

*For strategic context, business priorities, and current development status, see PROJECT_MEMORY.md.*