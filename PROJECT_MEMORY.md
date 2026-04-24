# DeCloud Project Memory

**Last Updated:** 2026-04-24
**Status:** Phase 1 (Marketplace Foundation) complete — GPU Proxy production-ready — Phase 2 (User Engagement) in progress.

---

## Purpose & Context

BMA is developing **DeCloud**, a decentralized cloud computing platform providing **censorship-resistant infrastructure** for AI hosting and general compute workloads. Core mission: infrastructure that cannot be shut down by centralized authorities.

### Core Differentiators

1. **Full VMs** (not containers) — libvirt/KVM, not Akash-style containerization
2. **Censorship resistance** — no KYC, wallet auth, no centralized kill switch
3. **CGNAT support** — relay infrastructure enables 60–80% of residential/mobile nodes to participate
4. **Community templates** — permissionless creation drives network effects
5. **Relay economics** — symbiotic: relay nodes earn passive income, CGNAT nodes get connectivity

### Strategic Vision: "The Minecraft of Compute"

Users don't want "decentralized cloud" — they want to **build and share cool things**. The infrastructure is the enabling layer.

- **Simple primitives** (VMs, templates, nodes) → complex creation
- **Composability** → templates reference templates, multi-VM stacks
- **Permissionless creation** → no approval needed to publish templates
- **Emergent complexity** → community creates use cases never anticipated

---

## Technical Architecture

### System Overview

```
User → Orchestrator (coordinator) → Node Agents (VM hosts)
```

**Orchestrator** (srv020184 — 142.234.200.108):
- VM scheduling and lifecycle management
- Node registration and heartbeat monitoring (15s interval, 2min timeout → offline)
- USDC billing on Polygon, bandwidth-aware hybrid pricing
- MongoDB Atlas for persistence
- React/Vite frontend with WalletConnect

**Node Agents** (e.g., srv022010, ARM nodes):
- KVM/libvirt virtualization — full VMs
- Resource discovery (CPU, RAM, GPU, storage, network)
- WireGuard networking (relay and mesh)
- SQLite for local state (port mappings, VM state)

### VM Types

| Type | Purpose | Status |
|---|---|---|
| General VMs | User workloads — AI, web apps, databases | ✅ Production |
| Relay VMs | WireGuard tunnel + NAT for CGNAT nodes | ✅ Production |
| DHT VMs | libp2p coordination over WireGuard mesh | ✅ Production-verified (2026-02-15) |
| Block Store VMs | Distributed content-addressed storage (5% duty) | ✅ Phase A–E + item 31b (2026-04-24) |

### Networking

**Relay system:** CGNAT nodes get connectivity via relay VMs on public-IP nodes. WireGuard tunnel. Revenue split: 80% CGNAT node / 20% relay node.

**Smart port allocation:** 3-hop DNAT forwarding for CGNAT (relay → node → VM), direct for public nodes. Port range 40000–65535. SQLite persistence with startup reconciliation.

**CentralIngress:** Caddy reverse proxy handles HTTP/HTTPS/WS/WSS via subdomain routing. Port allocation skips these protocols — only TCP/UDP gets iptables DNAT.

### Compute & Pricing

**Quality tiers:** Guaranteed (1:1), Standard (1.5:1), Balanced (2:1), Burstable (4:1) overcommit ratios. CPU points via sysbench, normalized to baseline.

**Bandwidth tiers:** Basic (10 Mbps, 0.8×), Standard (50 Mbps, 1.0×), Performance (200 Mbps, 1.2×), Unmetered (1.5×). Enforced via libvirt QoS on virtio NIC — both x86_64 and ARM.

**Hybrid pricing:** Platform floor rates + operator custom rates. `Math.Max(operator, floor)` enforced server-side. Operators configure via API or `appsettings.json`.

### GPU Proxy (Production-Ready, 2026-03-13)

**Problem:** Nodes without IOMMU (WSL2, consumer PCs) can't do GPU passthrough to VMs.

**Solution:** CUDA virtualization layer — shim libraries in VM intercept all CUDA calls and forward them via TCP RPC to a daemon on the host with the real GPU.

**Architecture:**
- **Runtime API Shim** (`libcudart.so.12`) — intercepts `cudaMalloc`, `cudaLaunchKernel`, etc.
- **Driver API Shim** (`libcuda.so.1`) — intercepts `cuInit`, `cuGetProcAddress`, etc.
- **GPU Proxy Daemon** — host-side process executing real CUDA operations
- **Transport** — TCP with `TCP_NODELAY` + `TCP_QUICKACK` for sub-ms RPC latency
- **35+ protocol commands** covering memory, execution, streams, events, modules, cuBLAS GEMM

**Performance (RTX 4060 Laptop GPU):**
- Ollama inference (llama3.2:1b): 436 tok/s prompt eval, 13–21 tok/s generation
- PyTorch full fine-tuning (GPT-2, batch=4, seq=128): 1,252 tok/s, 409ms/step
- PyTorch LoRA fine-tuning (r=8, 0.65% trainable): 1,038 tok/s, 493ms/step, 1,360MB peak VRAM
- RPC round-trip: <1ms, 100% GPU offload, zero manual configuration

**Confirmed workloads (2026-03-13):**
- Full PyTorch 2.3.1+cu121 inference ✅
- Autograd / backward pass ✅
- AdamW optimizer step ✅ (moment accumulation + weight update kernels)
- LoRA fine-tuning via PEFT ✅ (GPT-2 loss decreasing, VRAM efficient)
- JupyterLab kernel via LD_PRELOAD ✅
- No cuDNN required — GPT-2/transformers use native CUDA attention kernels

**Key engineering decisions:**
- **TCP_QUICKACK** — The single biggest fix. TCP delayed ACK imposed 40ms per RPC (0.07 tok/s). `TCP_QUICKACK` eliminates this. Must be re-armed before every `read()` — Linux resets it per-operation. Achieved 150× speedup (0.07 → 436 tok/s).
- **CUDA 12 lazy loading** — `CUDA_MODULE_LOADING=EAGER` is mandatory for PyTorch; without it, module registration is deferred to `__cudaInitModule` which the proxy doesn't intercept.
- **CUDA graphs pass-through** — Both shims return `cudaErrorNotSupported`/`CUDA_ERROR_NOT_SUPPORTED`, forcing apps to fall back to direct kernel execution. Correct approach vs attempting emulation.
- **Generic design** — No hardcoded app/GPU dependencies. App-specific config (GGML vars for Ollama, CUDA vars for PyTorch) driven by template `DefaultEnvironmentVariables` written to `/etc/decloud/gpu-proxy.env`.
- **cuBLAS stub** — cuBLAS init requires `cuGetExportTable` (private NVIDIA internals, cannot be proxied). Stub init + proxy only `cublasGemmBatchedEx`/`cublasGemmStridedBatchedEx` for GQA attention.

**CUDA 12.1 / PyTorch 2.3.1 Device Properties Offset Map (RTX 4060, SM8.9):**

| Offset | Field | Value |
|---|---|---|
| 0x184 (388) | multiProcessorCount | 24 |
| 0x270 (624) | maxThreadsPerMultiProcessor | 1536 |
| 0x288 (648) | regsPerMultiprocessor | 65536 |
| 0x2c8 (712) | maxBlocksPerMultiProcessor | 1024 |
| 0x2d0 (720) | reservedSharedMemPerBlock | 65536 |

---

## Platform Status (2026-04-05)

### What Is Working in Production

| Feature | Status |
|---|---|
| End-to-end VM creation and lifecycle | ✅ |
| Wallet-based authentication (Ethereum signatures) | ✅ |
| SSH access (wallet-derived + user-registered keys, cert-based) | ✅ |
| HTTPS with automatic certificates (Caddy) | ✅ |
| Relay infrastructure for CGNAT nodes | ✅ |
| CPU benchmarking and compute point tracking | ✅ |
| USDC payments on Polygon | ✅ |
| WebSocket terminal access | ✅ |
| Bandwidth tiers with libvirt QoS (Basic/Standard/Performance/Unmetered) | ✅ |
| Hybrid pricing (platform floor + operator custom rates) | ✅ |
| Centralized VM lifecycle state machine (VmLifecycleManager) | ✅ |
| CentralIngress-aware port allocation (HTTP/WS skipped) | ✅ |
| Smart port allocation (3-hop CGNAT + direct public) | ✅ |
| Per-service VM readiness tracking (Orchestrator side) | ✅ |
| Node marketplace (search, filter, featured, one-click deploy) | ✅ |
| VM template marketplace (6 seed templates, community, paid) | ✅ |
| Node reputation tracking (uptime 30-day rolling, VMs hosted, completions) | ✅ |
| Review system (templates + nodes, eligibility-verified) | ✅ |
| GPU proxy — Ollama, PyTorch inference + training + LoRA | ✅ |
| DHT infrastructure (libp2p over WireGuard mesh) | ✅ |
| Block Store Phase A–C (bitswap, FlatFS, quota, GC) | ✅ |
| ARM architecture support (Raspberry Pi) | ✅ |
| Windows WSL2 auto-start (watchdog + scheduled task installer) | ✅ |

### What Is Incomplete

| Gap | Notes |
|---|---|
| NodeAgent readiness monitor | `VmReadinessMonitor` background service — spec in `NODE_AGENT_READINESS_CHANGES.md` |
| Frontend readiness UI | Per-service status display under VM details |
| Node operator dashboard | Earnings, uptime, relay stats, profile management — **Priority 2.1, next focus** |
| Trust badges in frontend | 99.9% uptime, 100+ VMs hosted |
| Node rating aggregates | ReviewService backend ready, not wired to UI |
| Review prompts after VM termination | Frontend only |
| More seed templates | Have 6, target 10–15 then community growth to 50+ |
| Block Store Phase D | Lazysync — VM overlay replication |
| System VM boot time optimizations | Fixes identified, not yet applied (see Boot Performance section below) |
| Collaboration features | Shared VMs, infrastructure templates (Phase 3) |
| Lightweight node support | Non-KVM nodes — design TBD |
| Alpine Linux system VMs | 50 MB base image, 40× smaller |
| Prebuilt binary distribution | Switch from build-from-source to GitHub Releases |

---

## System VM Boot Performance Analysis (2026-03-28)

This section documents measured boot timing, root causes, and specific fixes ready to apply.

### Measured Timing — BlockStore VM (clean deploy)

| Phase | Duration | Notes |
|---|---|---|
| VM boot (QEMU/libvirt) | 71s | Fixed cost |
| cloud-init: user-data parsing (gz+b64 binary) | 34s | Python decodes binary in-process |
| cloud-init: apt-get update | ~190s | 44 MB package list fetch — dominant cost |
| cloud-init: package install | ~40s | 11 packages, 1,242 kB, fast once lists fetched |
| cloud-init total | 343s (5m43s) | Confirmed via `cloud-init analyze show` |
| snapd + LXD services (Ubuntu base image) | ~50s | Pre-installed, useless in system VMs |
| blockstore-node startup | 1s | libp2p peer ID in 300ms |
| orchestrator reconciliation | ~90s | 15s heartbeat + 30s `CheckDeploymentProgressAsync` |
| **Total DeployedAt → ActiveAt** | **~11 minutes** | Measured from MongoDB timestamps |

### Root Causes & Fixes (ready to apply)

**1. `apt-get update` fetches 44 MB of package metadata** (~190s saved)
- `cloud-init analyze show`: `config-package_update_upgrade_install: 230.415s`
- Packages themselves install in <1s after lists are fetched
- **Fix:** `package_update: false` + `package_upgrade: false` in all system VM cloud-init YAMLs

**2. snapd + LXD run inside every system VM** (~50s saved)
- `systemd-analyze blame`: `snapd.seeded.service 24.7s`, `snapd.service 11.9s`, `snap.lxd.activate.service 3.5s`
- Ubuntu 22.04 cloud image ships snapd + LXD — completely unused in system VMs
- **Fix:** Mask in `bootcmd` before any service starts:
```yaml
bootcmd:
  - rm -f /etc/machine-id /var/lib/dbus/machine-id
  - systemd-machine-id-setup
  - systemctl mask snapd.service snapd.socket snapd.seeded.service snap.lxd.activate.service 2>/dev/null || true
```

**3. Binary embedded as gz+b64 in cloud-init YAML** (34s saved, longer-term fix)
- cloud-init parses the entire YAML in Python before writing files — multi-MB binary causes 34s decode
- **Fix (longer term):** Serve binary via NodeAgent HTTP endpoint (`curl` from `runcmd`)

**4. `notify-ready.sh` callback never fired** (~45s saved)
- Callback log was empty — orchestrator found readiness via 15s heartbeat + 30s reconciliation polling
- **Fix:** Push immediate reconciliation from `BlockStoreCallbackController.BlockStoreReady()`

**5. Redundant packages removed from relay-vm-cloudinit.yaml:**
- `python3-pip` — never used (relay API uses stdlib only)
- `htop`, `iftop`, `net-tools` — diagnostic tools, install on demand
- `nginx` (full) → `nginx-light` — only basic reverse proxy features used

### Expected Improvement After Fixes

| Fix | Saves |
|---|---|
| `package_update: false` + `package_upgrade: false` | ~190s |
| Mask snapd/LXD in bootcmd | ~50s |
| Push reconciliation from callback | ~45s |
| **Total** | **~285s (~4.75 min)** |

**Projected: ~11 min → ~6.5 min** with quick fixes.
**Pre-baked base image** (no apt at all) → ~2.5 min.

### Pre-Baked System VM Base Image (Future Work)

All three system VMs install identical packages: `qemu-guest-agent curl jq openssl python3 wireguard wireguard-tools nginx-light`. A single pre-baked qcow2 with these packages removes apt from the boot path entirely. Build via `virt-customize` as part of `install.sh` or `GoBinaryBuildStartupService`.

### Why debian-12-genericcloud Was Rejected (2026-03-28)

Attempted migration to `debian-12-genericcloud` (~334 MB vs ~427 MB) failed. That image uses `linux-image-cloud-amd64` — a stripped kernel where AHCI/SATA drivers are loadable modules. `cloud-init-local` runs before `udev` finishes loading SATA modules, so `blkid -tLABEL=cidata` returns nothing — the cidata ISO doesn't exist yet when the scan fires. Injecting `datasource_list: [NoCloud, None]` into the base image got cloud-init to run but failed for the same reason — NoCloud probe calls `blkid` internally. The correct fix (`InjectCloudInitSeedAsync` — writing user-data into the overlay before VM start without needing block device scan) requires a guestmount step in `LibvirtVmManager`.

`debian-12-generic` uses `linux-image-amd64` with AHCI compiled in — `/dev/sda` is present before `cloud-init-local` starts. Reverted to this image.

Alpine is immune: `virtio-blk` and `ahci` compiled in by default, and `tiny-cloud` checks `/var/lib/cloud/seed/nocloud/` before any block device scan.

**Defensive fix added to `CloudInitCleaner`:** Now injects `datasource_list: [NoCloud, None]` into every base image during cleaning, preventing future Debian point releases from silently breaking this:
```csharp
"rm -f /etc/cloud/cloud-init.disabled",
"mkdir -p /etc/cloud/cloud.cfg.d && echo 'datasource_list: [NoCloud, None]' > /etc/cloud/cloud.cfg.d/99_datasource.cfg",
```

---

## Strategic Roadmap

### Phase 1: Marketplace Foundation — ✅ COMPLETE

| Feature | Date |
|---|---|
| Node marketplace (search, filter, featured, one-click deploy) | 2026-01-30 |
| VM template marketplace (6 templates, community, paid, reviews) | 2026-02-09 |
| Reputation system (uptime tracking + review backend) | 2026-02-09 |
| Bandwidth tier system with libvirt QoS | 2026-02-02 |
| Hybrid pricing model (floor + operator custom) | 2026-02-03 |
| Smart port allocation (3-hop CGNAT + direct) | 2026-02-08 |
| Centralized VM lifecycle management (VmLifecycleManager) | 2026-02-09 |
| CentralIngress-aware port allocation | 2026-02-09 |
| Per-service VM readiness tracking (Orchestrator) | 2026-02-10 |
| DHT infrastructure (libp2p over WireGuard mesh) | 2026-02-15 |
| Block Store Phase A–C | 2026-03-20 |
| GPU proxy (Ollama + PyTorch confirmed) | 2026-03-13 |
| Windows WSL2 auto-start | 2026-04-05 |

---

### Phase 2: User Engagement & Retention — 🔄 IN PROGRESS

#### Priority 2.1: Node Operator Dashboard ⭐⭐⭐⭐ — **Next Focus**
- **Impact:** HIGH — attracts and retains node operators
- **Effort:** MEDIUM (3 weeks)
- Earnings (lifetime + pending payout), VMs hosted, uptime (30-day), relay earnings breakdown, profile management

#### Priority 2.2: NodeAgent Readiness Monitor ⭐⭐⭐
- **Impact:** HIGH — completes per-service readiness feature end-to-end
- **Effort:** LOW (1 week) — spec already written (`NODE_AGENT_READINESS_CHANGES.md`)
- `VmReadinessMonitor` background service + heartbeat reporting

#### Priority 2.3: Frontend Readiness UI ⭐⭐⭐
- Per-service status display under VM details (companion to 2.2)
- **Effort:** LOW (2–3 days after 2.2 ships)

#### Priority 2.4: Reputation Frontend Polish ⭐⭐
- Trust badges (99.9% uptime, 100+ VMs hosted)
- Node rating aggregates in marketplace
- Review prompts after VM termination
- **Effort:** LOW (1 week)

#### Priority 2.5: Template Library Growth ⭐⭐
- Target: 10–15 seed templates, then community growth to 50+
- Candidates: Nextcloud, Whisper AI, Ollama chatbot, Minecraft server, Jellyfin, VPN/Tor relay, Mastodon

---

### Phase 3: Collaboration & Advanced Features

#### Priority 3.1: Shared VMs (Multi-Wallet Access) ⭐⭐⭐
- Multiple users access same VM — team dev environments
- `authorizedWallets` list on VM model, owner manages collaborators
- **Effort:** MEDIUM (3 weeks)

#### Priority 3.2: Infrastructure Templates (Multi-VM) ⭐⭐
- One-click deployment of interconnected VM stacks
- Example: web + database + Redis + load balancer
- **Effort:** HIGH (5–6 weeks)

#### Priority 3.3: Live Network Visualization ⭐⭐
- Interactive globe/map: nodes, VMs, relay connections, real-time
- Public-facing, marketing value
- **Effort:** HIGH (4 weeks)

---

### Phase 4: Monetization & Premium Features

**Prerequisites:** 50+ active nodes, 3+ months operation, XDE token launched.

#### Priority 4.1: Optional Premium Node Staking ⭐⭐⭐
- XDE staking for premium marketplace placement — strictly **optional**
- Free tier always available; staking is differentiation not gatekeeping
- Must earn reputation first (95%+ uptime, 10+ completions)
- Stake is lockable, not slashable (security deposit model)

#### Priority 4.2: Advanced Analytics Dashboard ⭐⭐
- Historical uptime trends, earnings projections, competitive benchmarking
- **Effort:** MEDIUM (2–3 weeks)

---

## Success Metrics

### Phase 1 — ✅ Complete
- ✅ Node marketplace complete
- ✅ VM template marketplace complete (6 seed templates)
- ✅ Community template workflow live
- 🎯 50+ community templates (ongoing)
- 🎯 75% of new VMs deployed from templates

### Phase 2 — In Progress
- 🎯 Node operator dashboard live
- 🎯 50+ active node operators
- 🎯 200+ reviews submitted
- 🎯 10–15 seed templates

### Phase 3 — Future
- 🎯 20% of VMs shared across wallets
- 🎯 10,000+ total VMs deployed

---

## Key Learnings & Principles

### Architectural Principles

1. **Security first** — wallet-based auth, encrypted communications, minimal attack surface
2. **KISS** — industry-standard solutions over custom implementations
3. **Deterministic state** — `SHA256(machineId + walletAddress)` for stable node IDs
4. **Authoritative source** — Orchestrator maintains truth, nodes report current state
5. **Graceful degradation** — system continues working if components fail
6. **Centralized lifecycle** — all VM state transitions through `VmLifecycleManager`: validate → persist → effects (best-effort, individually guarded)

### Major Realizations

- **Simplicity wins** — wallet-encrypted passwords beat complex ephemeral SSH keys
- **Focus on unique value** — censorship resistance matters more than distributed storage pooling
- **Reusable infrastructure** — relay system could be a standalone product ("DeCloud Relay SDK")
- **Mobile integration** — hybrid architecture: lightweight tasks for mobile, full VMs for servers
- **Network effects first** — features enabling sharing/discovery grow the platform fastest

### Performance Insights

- **CPU quota critical** — incorrect calculations caused 18-min boot times (vs. 2-min optimal)
- **MAC-pinned netplan blocks migration boot** — overlay carries source node MAC in `/etc/netplan/50-cloud-init.yaml`; bootcmd must overwrite it before networkd starts or `network-online.target` hangs forever
- **Dirty bitmap survives migration** — qcow2 persistent bitmap `lazysync` is carried in the overlay; receiving node must remove-then-add to avoid `AddDirtyBitmap failed` fallback to full export every cycle
- **sysbench reliable** — provides consistent CPU performance normalization across hardware
- **apt-get update dominant** — 190s of 343s cloud-init time is package list fetch; disabling it cuts boot by ~55%
- **TCP_QUICKACK critical for RPC** — 40ms delayed ACK kills GPU proxy throughput; must re-arm before every `read()`
- **Lazy loading breaks module registration** — `CUDA_MODULE_LOADING=EAGER` mandatory for PyTorch

### What NOT to Build (Anti-Priorities)

- ❌ Custom VM image uploads — security risk, complex, low ROI
- ❌ Mandatory staking — contradicts "universal participation" principle
- ~~❌ Live migration~~ → ✅ Implemented (2026-04-20) — node-offline triggers automatic overlay reconstruction and VM reboot on best available node
- ❌ Team workspaces — niche until user base grows
- ~~❌ DHT-based discovery~~ → ✅ Implemented (2026-02-15)

---

## On the Horizon

### Near-Term (Next 3 Months)
- Node operator dashboard (Priority 2.1) — highest impact for operator growth
- NodeAgent readiness monitor + frontend UI (completes Phase 1.4)
- System VM boot optimization (fixes identified — apply `package_update: false`, mask snapd/LXD)
- Template library growth to 10–15 seed templates
- Frontend reputation polish (trust badges, review prompts, node ratings)

### Mid-Term (6–12 Months)
- **Mobile integration:** Two-tier architecture
  - Mobile tier: WebAssembly tasks, ML inference, distributed storage
  - Server tier: full VM hosting (existing)
- **Block Store Phase D–E:** Lazysync + Live Migration — ✅ Complete (2026-04-20). Continuous overlay replication, automatic node-offline migration, dirty bitmap incremental sync, overlay reconstruction from chunk map. System VM resilience watchdog remaining.
- **Alpine base images:** 50 MB system VMs, 5-second boot, ~6.5-min → ~2-min total deploy time
- **Smart contract coordination:** Move toward true decentralization

### Long-Term Vision
- **DeCloud Relay SDK** — standalone product for other decentralized platforms (Akash, Filecoin, residential validators). Addressable market: $5M–$25M.
- **Geographic expansion** — compliance frameworks for different jurisdictions
- **AI-specific optimizations** — GPU scheduling, model caching, inference templates, pipeline-parallel distributed inference via block store model shards
- **Blockchain migration** — centralized orchestrator → smart contract governance

---

## Approach & Patterns

### Development Workflow
1. Check GitHub/documentation before making changes
2. Understand root causes, not just symptoms
3. Apply evidence-based recommendations
4. Maintain comprehensive documentation
5. Evolutionary changes over revolutionary restructuring

### Technical Patterns
- **Dependency injection** — throughout .NET services
- **Async/await** — proper async patterns everywhere
- **Separation of concerns** — clean service boundaries
- **Defensive programming** — comprehensive error handling, retry mechanisms
- **Feature flags** — gradual rollouts for major changes

### Deployment Methodology
- **Automation** — `install.sh` scripts with backup/rollback
- **systemd hardening** — proper service configuration, security directives
- **Secret management** — environment files, no hardcoded credentials
- **Comprehensive logging** — structured logging for troubleshooting
- **Production-grade always** — even in dev, maintain production standards

---

## Tools & Resources

### Backend Stack
- **.NET 8 / C#** — Orchestrator and Node Agent
- **MongoDB Atlas** — Orchestrator persistence
- **SQLite** — Local node state
- **libvirt/KVM** — Virtualization
- **WireGuard** — Overlay networking
- **Caddy** — Reverse proxy with auto-HTTPS

### Frontend Stack
- **React / TypeScript** — UI
- **Vite** — Build tool
- **WalletConnect / Reown AppKit** — Wallet authentication
- **Nethereum** — Ethereum signature verification

### Infrastructure
- **Ubuntu Linux** — OS for all servers
- **Cloudflare** — DNS management
- **Polygon** — Blockchain for USDC payments
- **Let's Encrypt** — SSL certificates

### Development Tools
- **Visual Studio** — .NET development
- **GitHub Actions** — CI/CD
- **sysbench** — CPU benchmarking and normalization
- **MongoDB Compass** — Database GUI
- **cloud-init analyze** — Boot time diagnosis

---

*For full technical implementation details of each feature, see PROJECT_FEATURES.md.*