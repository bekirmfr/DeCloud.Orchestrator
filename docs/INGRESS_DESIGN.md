# DeCloud Ingress Redesign

## A Unified Architecture for Scale and Tenant TLS Ownership

*Design Document*

| Field | Value |
|---|---|
| Status | Proposed — pre-implementation review |
| Version | 0.2 |
| Date | 2026-06-08 |
| Supersedes | (initial) |
| Related | `SYSTEM_VM_DESIGN.md`, `PROJECT_FEATURES.md` §2, `MINECRAFT_VISION_ROADMAP.md` |

---

This document specifies a unified ingress architecture for DeCloud that resolves two structural tensions in the current CentralIngress implementation — a single-host scaling ceiling, and the impossibility of tenant-managed TLS — through one architectural change: making TLS termination optional and distributable rather than mandatory and centralized. The design extends existing platform patterns (`SystemVmReconciler`, WireGuard mesh, Caddy admin API, Cloudflare DNS integration) and introduces exactly one new component (Routing SystemVM) and one new endpoint (NodeAgent raw-TCP tunnel).

---

## 1. Problem Statement

### 1.1 Two structural tensions in the current design

The current CentralIngress implementation places a single Caddy instance on the orchestrator host. That instance owns ports 80 and 443 on the platform's only public IP, terminates TLS, and reverse-proxies plain HTTP to a per-node agent which in turn forwards to the target VM. Two structural problems follow directly from this topology:

**Tension A — Scaling ceiling at ~1,000 VMs.** Every VM lifecycle event triggers a full Caddy config rebuild via `POST /load`. A single Caddy instance on a single public IP combines with a per-node single NodeAgent proxy to cap throughput well below the Minecraft vision's 10,000-to-100,000 VM target.

**Tension B — Tenant workloads cannot own their own TLS.** Because Caddy owns ports 80 and 443 on the only public IP and always terminates TLS, no in-VM proxy (Coolify/Traefik, an app's own nginx, a Kubernetes ingress) can issue or terminate its own certificates. The Coolify file-provider override in production today is a workaround that papers over the constraint for one specific PaaS; it does not generalize.

### 1.2 The common root cause

These are not two problems. They are one architectural fact viewed from two angles: **TLS termination is mandatory and centralized.** Tension A is its manifestation as a throughput bottleneck. Tension B is its manifestation as a prohibition on tenant cert ownership. Both dissolve when termination becomes optional and distributable.

The natural boundary the platform already has — and is currently ignoring — is **SNI**. SNI is the first plaintext field in every TLS handshake. Routing on SNI is strictly weaker than terminating with SNI lookup; it requires no cert and inspects nothing the network doesn't already see. Routing without termination is what unlocks both tensions at once.

### 1.3 Scope

This document specifies:

- The unified ingress architecture that resolves both tensions
- The trust model that keeps the wildcard certificate secret while enabling community-hosted routing
- The component changes (new, modified, unchanged) needed to implement it
- The migration path that produces independent value at every step

This document does not specify:

- A routing-node selection algorithm — the design uses DNS round-robin and explicitly does not need one
- The ingress-duty revenue model for routing-node operators (open question, §9.1)
- Replacement of Direct Access (`iptables` DNAT on 40000–65535) — that remains the right answer for non-HTTPS protocols

---

## 2. Design Principles Applied

This design adheres to the project's stated architectural principles (`PROJECT_MEMORY.md` §"Architectural Principles"). Each principle gets one specific application:

### 2.1 Align to natural system boundaries

The design uses three boundaries the system already provides rather than inventing new controls:

- **SNI** for routing — already in the TLS handshake, already plaintext, already inspected by Caddy
- **DNS A records** for node membership — Cloudflare API already integrated for `*.direct.stackfi.tech`
- **GossipSub** (long-term) for route propagation — already in production on the DHT mesh

### 2.2 Security first

The wildcard private key for `*.vms.stackfi.tech` is the platform's identity. Anyone holding it can impersonate every VM subdomain. The trust model (§4) defines what each tier can and cannot do, with capability bounds enforced by what the L4 module *can* do — not by trust in operators. No third-party-operated host ever holds the wildcard key or DNS authority.

### 2.3 KISS

The design adds exactly one new component (the Routing SystemVM) and one new endpoint (NodeAgent's raw-TCP tunnel). Everything else is an extension of an existing service (`SystemVmReconciler` gains a fourth canonical role) or a refactor of an existing one (`CentralCaddyManager` becomes shard-aware). No new init systems, no new networking primitives, no new protocols.

### 2.4 Minecraft alignment

Routing is a default duty for every public-IP node, matching the relay/DHT/blockstore pattern. More public-IP nodes joining the network increases ingress capacity automatically — no provisioning, no scheduler, no whitelist. Operators self-balance via the existing bandwidth-tier configuration.

---

## 3. The Architecture

### 3.1 One-line summary

> *Wildcard `*.vms.stackfi.tech` with one A record per registered public-IP node, Caddy SystemVM on every public-IP node doing L4 SNI routing only, platform terminator holds the wildcard cert, passthrough VMs skip the terminator entirely.*

### 3.2 Trust tiers

Two tiers separated by a hard boundary at TLS termination:

**Platform tier (orchestrator-controlled):**

- DNS authority (Cloudflare API token)
- Wildcard certificate private key for `*.vms.stackfi.tech`
- On-demand TLS certificates for custom domains
- Authoritative VM-to-backend route table (source of truth)

**Community tier (any node operator):**

- Routing SystemVM — Caddy with `caddy-l4`, L4 SNI route only, no cert, no DNS access
- NodeAgent — existing HTTP/WebSocket proxy plus a new raw-TCP tunnel
- Tenant VMs — compute, optionally owning their own TLS in passthrough mode

The boundary is enforced by capability, not by policy. Community-tier hosts never receive platform certificates because the L4 module never asks for one. Tenant-owned certificates (in passthrough mode) are tenant secrets, not platform secrets, and have a blast radius of one VM.

### 3.3 Components and where they run

| Component | Tier | Hosts | Purpose |
|---|---|---|---|
| Orchestrator | Platform | `srv020184` | Control plane, DNS authority, route source |
| Platform terminator (Caddy) | Platform | `srv020184` initially, scale-out later | Hold wildcard cert, terminate TLS for managed-mode |
| Routing SystemVM | Community | Every public-IP node | L4 SNI route, no cert, no platform secret |
| NodeAgent (HTTP proxy) | Community | Every node | Existing — proxies HTTP to VMs |
| NodeAgent TCP tunnel | Community | Every node | NEW — raw byte forwarder for passthrough mode |
| VM workloads | Tenant | Wherever the VM is scheduled | TLS-agnostic (managed) or TLS-terminating (passthrough) |

### 3.4 Data flow — managed mode (the default)

```
Browser
   │ DNS lookup: vm-xyz.vms.stackfi.tech → returns N A records
   │ (one per registered public-IP node; DNS RR or geo-weighted)
   ▼
Routing SystemVM on selected public-IP node
   - Read SNI from ClientHello (no termination)
   - Look up VM → mode = Managed
   - Open TCP to platform terminator over WireGuard mesh
   ▼
Platform terminator (Caddy on srv020184)
   - TLS terminates with wildcard cert
   - Forward plain HTTP to NodeAgent over WireGuard mesh
   ▼
NodeAgent on VM's host node
   - GenericProxyController.ProxyHttp() (existing path)
   - Resolve VM private IP via libvirt
   ▼
VM service :80
```

### 3.5 Data flow — passthrough mode

```
Browser
   │ DNS lookup: vm-xyz.vms.stackfi.tech → returns N A records
   ▼
Routing SystemVM on selected public-IP node
   - Read SNI from ClientHello
   - Look up VM → mode = Passthrough
   - Open TCP to NodeAgent raw-TCP tunnel over WireGuard mesh
   ▼
NodeAgent raw-TCP tunnel on VM's host node
   - NEW endpoint, byte-forwarder to vm.PrivateIp:443
   ▼
VM service :443
   - TLS terminates inside the VM
   - VM owns and renews cert (DNS-01 with the tenant's own DNS credentials)
```

Passthrough mode delivers a property that managed mode structurally cannot: the VM receives a real TLS connection from the client, with no `X-Forwarded-Proto` ambiguity for in-VM proxies to misinterpret. Coolify-style redirect loops — where an in-VM proxy reads a plain HTTP request, issues an HTTPS redirect, and the redirect target loops back through the same managed path — do not occur in passthrough mode because the VM-side proxy is genuinely seeing HTTPS.

### 3.6 Data flow — custom domains

Custom domains (`app.example.com`) flow identically to managed-mode VM subdomains. The tenant CNAMEs to `*.vms.stackfi.tech`; resolution lands on the multi-A wildcard; the routing SystemVM reads `app.example.com` from SNI and matches it against a custom-domain route. The platform terminator holds the per-domain certificate, issued via the existing on-demand-TLS path with the `domain-check` permission endpoint unchanged from today.

Passthrough mode is also supported for custom domains, with the same toggle semantics. A passthrough custom domain requires the tenant to manage their own DNS-01 credentials for that domain.

### 3.7 DNS model — wildcard multi-A

The wildcard `*.vms.stackfi.tech` resolves to one A record per registered public-IP node. The orchestrator manages these records via the existing Cloudflare API integration. Per-VM DNS records are not created.

```
*.vms.stackfi.tech.  60  IN  A  142.234.200.108   (node A public IP)
                     60  IN  A  198.51.100.42      (node B public IP)
                     60  IN  A  203.0.113.17       (node C public IP)
                     ...
```

DNS write events:

- Public-IP node registers → orchestrator adds one A record
- Public-IP node misses heartbeat for 2 minutes → orchestrator removes one A record
- VM lifecycle events → no DNS writes

TTL is set low (60 s recommended) to bound failover latency. Cloudflare propagation is sub-second; browser cache is the gating factor.

### 3.8 Route distribution

Every routing SystemVM holds the full SNI-to-backend route table. Memory cost is modest: ~200 bytes per route × 10,000 VMs = ~2 MB per node. Caddy with `caddy-l4` uses hash lookup on SNI, so route count does not affect per-connection latency.

Two distribution mechanisms, sequenced in the migration path:

**Push-based (initial implementation).** When a VM starts, `CentralIngressService.OnVmStartedAsync` iterates over all healthy routing nodes and calls each Caddy admin API in parallel over the WireGuard mesh. Existing `CentralCaddyManager.UpsertRouteAsync` shape, with the single `_httpClient` replaced by a per-node client collection. Operations are idempotent on the receiving side. Drift converges via a periodic resync.

**GossipSub-based (deferred).** When push fan-out latency starts to matter (estimated: hundreds of routing nodes), orchestrator publishes route updates to a GossipSub topic (`decloud/ingress/routes/v1`) over the existing DHT mesh. Routing SystemVMs subscribe; new joiners bootstrap by pulling a snapshot from the orchestrator or a peer. Same data shape, lower coupling. Migration is incremental and reversible.

---

## 4. Trust Model

### 4.1 What each tier holds

| Tier | Holds | Does NOT hold |
|---|---|---|
| Orchestrator | Cloudflare API token, wildcard private key, custom-domain certs, route table | (nothing relevant beyond) |
| Platform terminator | Copy of wildcard private key, custom-domain certs (via shared storage in scale-out) | DNS write access, route source of truth |
| Routing SystemVM | SNI-to-backend route table, WireGuard mesh key for its node | Any certificate, any DNS credential, any platform secret |
| NodeAgent | Per-VM proxy logic, VM private IPs from libvirt | Any certificate, any platform secret |
| Tenant VM (passthrough) | Its own certificate and DNS credentials for its own domain | Platform wildcard, other tenants' anything |

### 4.2 Capability matrix for a routing-node operator

| Capability | Possible? | Why |
|---|---|---|
| Observe which VM hostnames are routed | Yes | SNI is plaintext on the wire — no new disclosure beyond what any on-path observer already sees |
| Drop or delay traffic | Yes | DoS only — surfaces as bad reputation via `NodeReputationService`, marketplace demotion, eventual A-record removal |
| Read VM HTTP request bodies, headers, cookies | **No** | Managed: terminator decrypts, not the node. Passthrough: VM holds the cert. Node always sees ciphertext only |
| Issue a certificate for any VM subdomain | **No** | Public CAs require DNS-01 or HTTP-01 proof. Node has neither DNS authority nor the wildcard key |
| Hijack a VM's domain to a different backend | **No** | Backend address comes from orchestrator-pushed admin-API config. Tampering breaks the route, doesn't redirect it |
| Move a VM's traffic to itself | **No** | DNS authority is orchestrator-only |
| Cause MITM by replacing the wildcard cert | **No** | Cert lives on platform terminator, never on the routing node |

### 4.3 Why per-node cert delegation was considered and rejected

The Minecraft-aligned instinct is to give each node its own cert with platform-controlled revocation. This was considered carefully and rejected for three structural reasons:

1. **Browser revocation is structurally weak.** OCSP soft-fail dominates real-world browser behavior; CRLs are barely checked. Practical revocation latency to actual user browsers is hours to days. This window is long enough for a malicious operator to MITM, exfiltrate sessions, or serve phishing forms before browsers honor the revocation.
2. **Holding a valid cert is binary, not graduated.** Once a node has the private key for any cert, every operation that cert authorizes (terminate, impersonate, intercept) is possible. The web PKI has no "limited TLS authority" primitive.
3. **Per-node sub-wildcards leak into URLs.** Distinct certs require distinct subjects, which exposes the routing node in the hostname (e.g., `*.node-srv022010.vms.stackfi.tech`). This breaks live VM migration (Phase J, production per `PROJECT_FEATURES.md`) — a VM moving from node A to node B would change subject and URL, breaking the "VM keeps its identity across nodes" property that is load-bearing for the Minecraft compose-and-share story.

The L4-only routing answer avoids all three problems by giving the node no cert and no DNS authority. The blast radius matches the operator's actual trust level.

---

## 5. Components

### 5.1 Routing SystemVM (new)

A new canonical SystemVM role: `ObligationRole.Ingress`. Deployed by the existing `SystemVmReconciler` on every public-IP node (predicate: `!node.IsBehindCgnat`), identical pattern to relay/DHT/blockstore.

**Contents:**

- Custom Caddy binary built with the `mholt/caddy-l4` module
- Cloud-init template `ingress-vm-cloudinit.yaml` (new)
- WireGuard mesh enrollment via existing `wg-mesh-enroll.sh`
- systemd service hardening (no shell login, `ProtectSystem=strict`, dropped capabilities, `NoNewPrivileges`)
- Caddy admin API listening on the WireGuard interface only (not on the node's public IP)

**Resource footprint (Alpine target per `PROJECT_FEATURES.md` §11):**

- 1 vCPU, 256–512 MB RAM
- ~50 MB binary + ~20 MB runtime
- Sub-5-second boot once Alpine system VMs ship

**Binary distribution:** Built by CI in `DeCloud.Builds`, published to GitHub Releases on `binaries/vX.Y.Z` tag push, fetched by `ArtifactCacheService` over `virbr0` — same path as the DHT and BlockStore binaries already use.

### 5.2 Platform terminator tier

**Initial deployment:** the existing Caddy on `srv020184`. The configuration *shrinks* with this redesign — wildcard cert plus on-demand TLS plus one HTTP route per VM forwarding to NodeAgent. No SNI matching against many VM hostnames, no per-VM L7 routing logic; that work has moved upstream to the routing SystemVMs.

**Scale-out path (deferred until pressure justifies it):** 2–3 small platform-owned VPSes sharing certificate storage via Caddy's clustered storage abstraction (Redis on the orchestrator, ACL-restricted, TLS-encrypted). Routing SystemVMs pick a terminator round-robin over the WireGuard mesh.

### 5.3 NodeAgent raw-TCP tunnel endpoint (new)

A new listener on the NodeAgent, distinct from the existing `GenericProxyController` WebSocket-tunneled TCP route (`/api/vms/{vmId}/proxy/tcp/{port}`, which is designed for user-facing clients like the web terminal and expects a WebSocket frame envelope).

The L4 module's `proxy` handler speaks raw TCP, not WebSocket. Wrapping every TLS-passthrough connection in WebSocket framing would add per-frame overhead and a masking requirement for no benefit. A dedicated raw-TCP listener is the correct primitive.

**Protocol:** TCP listener on a new dedicated port (proposed: `5101`). Each connection begins with a fixed-length preamble:

```
[4 bytes BE: vmId length N][N bytes UTF-8: vmId][2 bytes BE: target port]
```

After the preamble, the listener acts as a bidirectional byte-shovel between the incoming connection and the VM's private IP at the target port.

**Caller:** Routing SystemVMs only, over the WireGuard mesh.

**Authentication:** WireGuard mesh membership is the authentication. The listener binds to the WireGuard interface address and accepts connections only from peer IPs in the mesh. No application-layer auth.

**Allowed target ports:** Initially restricted to 443 (passthrough TLS). The same allow-list pattern as the existing proxy controller; extension governed by the existing security review.

### 5.4 Orchestrator changes

| Service / File | Change |
|---|---|
| `NodeService.RegisterNodeAsync` | On public-IP node registration: add one A record to `*.vms.stackfi.tech` via the existing Cloudflare integration |
| `NodeService.MarkNodeOfflineAsync` | On heartbeat timeout for a public-IP node: remove its A record |
| `SystemVmReconciler` (node-side) | Add `ObligationRole.Ingress` to `CanonicalRoles` |
| `IObligationStateService` | Add ingress template projection (same shape as relay/DHT/blockstore) |
| `CentralIngressService.OnVmStartedAsync` | Push route to every healthy routing node via per-node admin API call (parallel) |
| `CentralIngressService.OnVmStoppedAsync` | Push route deletion to every healthy routing node |
| `CentralIngressService.RestoreRunningVmRoutesAsync` | Push current route snapshot to each routing node on orchestrator startup |
| `CentralCaddyManager` | Refactor from single admin client to per-node admin client collection; add Step 0 surgical-update methods |
| `VirtualMachine` model | Add `IngressMode` enum (`Managed` \| `Passthrough`), default `Managed` |
| `CentralIngressController` | Add UI-facing API to toggle `IngressMode` per VM |

### 5.5 What stays unchanged

- CGNAT relay topology — routing SystemVMs reach CGNAT-hosted NodeAgents via the existing WireGuard tunnels through relays
- WireGuard mesh enrollment for SystemVMs (`wg-mesh-enroll.sh`, `WgMeshEnrollController`)
- `domain-check` permission endpoint for on-demand TLS
- `IngressConfig` model on VM (`DefaultSubdomainEnabled`, `DefaultPort`, `CustomDomains`)
- `DirectAccessService` and the 40000–65535 port range for raw TCP/UDP
- Per-VM custom domain UI (`custom-domains.js`)
- `GenericProxyController`'s existing HTTP, WebSocket, and WebSocket-tunneled TCP routes
- Nodes remain credential-less compute — no platform secrets ever land on them
- VM-side Coolify file-provider override remains in place for users who do not migrate to passthrough mode

---

## 6. Lifecycle Sequences

### 6.1 Public-IP node registration

```
1. Operator runs install.sh on a public-IP node.
2. NodeAgent registers with orchestrator (existing path).
3. NodeService.RegisterNodeAsync:
   a. Detect public IP (existing).
   b. NEW: Add A record (publicIp) to *.vms.stackfi.tech via Cloudflare API.
   c. NEW: Mint Ingress obligation alongside existing obligations.
4. SystemVmReconciler on the node:
   a. Sees new Ingress obligation in its reconciliation matrix.
   b. Pulls the custom Caddy binary via ArtifactCacheService.
   c. Deploys Routing SystemVM via LibvirtVmManager.
   d. SystemVM joins WireGuard mesh via wg-mesh-enroll.sh.
   e. SystemVM exposes Caddy admin API on the WireGuard interface.
5. Orchestrator detects the new routing node ready:
   a. Pushes the current full route snapshot to its Caddy admin API.
   b. Marks the routing node as ready in DataStore.
6. DNS propagation completes (under one minute on a 60-second TTL).
7. Browsers begin selecting the new node's IP via DNS RR.
```

### 6.2 VM start

```
1. VM transitions to Running (existing VmLifecycleManager path).
2. CentralIngressService.OnVmStartedAsync triggered.
3. Build route: { sni, backend, mode }
   where backend = terminator if Managed, NodeAgent-tcp-tunnel if Passthrough.
4. For each healthy routing node, in parallel:
   PATCH http://<node-wg-ip>:2019/config/.../routes  with the new route.
5. No DNS write.
6. VM marked ingress-ready.
```

### 6.3 VM stop

```
1. VM transitions to Stopped (existing path).
2. CentralIngressService.OnVmStoppedAsync triggered.
3. For each healthy routing node, in parallel:
   DELETE the route by SNI match.
4. No DNS write.
```

### 6.4 Routing node failure

```
1. Heartbeat missed for 2 minutes → node marked Offline (existing detection).
2. NodeService.MarkNodeOfflineAsync:
   a. Existing cleanup (VMs marked Error, billing stops).
   b. NEW: Remove this node's A record from *.vms.stackfi.tech.
3. Cloudflare propagates the DNS change (sub-second).
4. Browser DNS caches expire on TTL (60 s for new lookups).
5. In-flight: browser happy-eyeballs retries the next A record in its cache (~3 s).
6. Net user-visible failover: ~3 s for live traffic, ≤60 s for cold lookups.
```

### 6.5 Live VM migration (Phase J interaction)

```
1. Source node fails (existing detection).
2. Phase J reconstructs the VM on a new node (existing path).
3. CentralIngressService updates the route's backend address.
4. For each healthy routing node, in parallel:
   PATCH the route's upstream.
5. No DNS write.
6. Hostname stable across migration — load-bearing for the compose-and-share story.
```

---

## 7. Failure Modes

### 7.1 Routing node offline

DNS round-robin + low TTL + browser happy-eyeballs handles failover (§6.4). Graceful failover requires at least two healthy public-IP nodes. Below two, the architecture collapses to "the one routing node IS the network for that minute" — the same single-point-of-failure reality as today's single orchestrator, no worse.

### 7.2 Platform terminator unreachable

Routing SystemVM detects upstream failure via Caddy's L4 health check and returns a connection close. With a single terminator, graceful degradation is not possible; with the scale-out tier (§5.2), the routing node tries the next terminator. Initial deployment accepts the single-terminator risk because it is identical to today's risk.

### 7.3 Route propagation race on cold start

A new routing node could be added to DNS before its route table is populated, leading to "no route for SNI" rejections on early traffic. Mitigation is strict sequencing in `RegisterNodeAsync`: orchestrator pushes the full route snapshot to the new node's Caddy admin API and confirms success *before* writing the node's A record to DNS. Cold-start latency: a few seconds for the snapshot push, then DNS propagation. Acceptable.

### 7.4 GossipSub message loss (when GossipSub is in play)

GossipSub is eventually consistent. Mitigation: orchestrator periodically re-publishes the full route snapshot (every 60 s) so any subscriber that missed an incremental update converges. Trade-off is a 60-second worst-case staleness window for a missed update, after which convergence is automatic. Acceptable.

### 7.5 Wildcard certificate expiry

Cert managed by Caddy's existing ACME automation on the terminator tier (DNS-01 via Cloudflare). Renewal occurs 30 days before expiry. Failure mode is identical to today's and no worse.

### 7.6 Routing node operator silently malicious

An operator hosting a routing SystemVM cannot read traffic or impersonate the platform (§4.2) but can selectively drop or delay connections to disadvantage specific VMs. Mitigation: `NodeReputationService` already tracks connection-handling metrics on the node; sustained anomalies relative to peers result in marketplace demotion and eventual A-record removal. Active mitigation (orchestrator probing of routing nodes from outside the WireGuard mesh) is a possible enhancement but not initial.

---

## 8. Migration Path

Each step has independent value and is independently reversible. No step depends on a later step having shipped first.

### 8.1 Step 0 — Surgical route updates

**Goal:** Eliminate the O(n) full-config rebuild on every VM lifecycle event.

**Change:** Replace `CentralCaddyManager.ApplyConfigAsync`'s full-rebuild path with per-route `PATCH`/`PUT`/`DELETE` against the Caddy admin API.

**Independent benefit:** Single-host VM ceiling extends from ~1,000 toward ~5,000 with no topology change. Risk-free, no new infrastructure.

**Rollback:** Revert the manager change. Configuration shape unchanged.

### 8.2 Step 1 — Custom Caddy build

**Goal:** A Caddy binary with `caddy-l4` integrated in the build pipeline.

**Change:** Add a Caddy build target to `DeCloud.Builds` CI; publish as a binary artifact via GitHub Releases on tagged push; deploy to the orchestrator host.

**Single-host deployment:** Orchestrator host swaps the stock Caddy binary for the custom one. Behavior is identical for all existing VMs (managed mode only). Validates the binary in a low-risk environment.

**Rollback:** Single binary swap.

### 8.3 Step 2 — Passthrough mode pilot

**Goal:** Validate end-to-end passthrough TLS with a real workload.

**Change:**

- Add NodeAgent raw-TCP tunnel endpoint (§5.3).
- Add `IngressMode` toggle to VM model (default `Managed`).
- Add an L4 listener to the orchestrator's Caddy that routes passthrough VMs to NodeAgent's raw-TCP tunnel and hands managed VMs off to the existing L7 server.
- Pilot with Coolify users — the highest-value affected workload, and the one that currently requires the in-VM file-provider override.

**Validation criteria:** Passthrough VMs complete their own ACME DNS-01 issuance, receive raw TLS connections, exhibit no redirect loops.

### 8.4 Step 3a — Ingress as fourth canonical SystemVM role

**Goal:** Routing SystemVMs deployed across the public-IP node pool, passive.

**Change:**

- Add `ObligationRole.Ingress` and corresponding `SystemVmTemplate`.
- Add `Ingress` to `SystemVmReconciler.CanonicalRoles`.
- `NodeService` mints the Ingress obligation for public-IP nodes only.
- Routing SystemVMs come up healthy; not yet added to DNS, not yet routing traffic.

**Validation criteria:** Caddy admin API reachable from the orchestrator over the WireGuard mesh; route snapshot push succeeds; SystemVM survives node restart and re-enrollment.

### 8.5 Step 3b — Single routing node per VM (transitional)

**Goal:** First live traffic through node-hosted routing.

**Change:**

- Orchestrator picks one routing node per VM via round-robin, writes a per-VM A record (transitional — still per-VM in this step), pushes the route via admin API.
- Existing terminator on orchestrator host receives forwarded traffic.
- Wildcard remains as catch-all for legacy and unmatched subdomains.

**Validation criteria:** Pilot VMs flow through node routing without observable user impact; failover behavior matches projection.

### 8.6 Step 3c — Wildcard multi-A, full distribution

**Goal:** The architecture this document describes goes live.

**Change:**

- DNS migrates from per-VM A records (Step 3b) to wildcard multi-A — orchestrator writes one A record per public-IP node, removes the per-VM records added in Step 3b.
- Orchestrator pushes routes to *all* routing nodes (not a selected subset).
- New routing nodes join DNS round-robin only after route snapshot push completes (§7.3).

**Validation criteria:** Failover time under load matches projection (≈3 s for live traffic, ≤60 s for cold lookups). Route propagation latency under 1 s for VM start events.

### 8.7 Step 3d — Operator and geographic optimization

**Goal:** Reward operators with better routing performance; reduce latency for end users.

**Change:**

- Bandwidth-tier-weighted A record weighting via Cloudflare LB (Performance/Unmetered nodes get more weight).
- Geographic steering via Cloudflare LB region-aware A record selection.
- `NodeReputationService` extends to track connection-handling metrics; marketplace surfaces "ingress reputation" alongside hosting reputation.

**Deferred indefinitely until in-field traffic data justifies the tuning.**

---

## 9. Open Questions

These do not block Step 0 or Step 1 but require resolution before the relevant later step:

### 9.1 Revenue model for ingress duty (blocks Step 3a)

Routing carries real bandwidth cost for operators. Three candidate models:

1. **Built into the compute base rate** — ingress is a default duty like relay duty. Simple, but ignores bandwidth asymmetry between operators on different tiers.
2. **Per-routed-VM stipend** — flat amount per VM whose A record is being served by this node, paid daily. Mirrors `RelayAssignment` accounting and the existing 80/20 relay split.
3. **Per-byte ingress fee** — fraction of the bandwidth-tier multiplier on traffic actually routed. Most accurate; most complex to settle on-chain.

Recommended starting point: option 2. Parallels the existing relay revenue model and can be refined when bandwidth-cost data is available.

### 9.2 GossipSub migration trigger (blocks GossipSub adoption)

Push-based route distribution scales to "hundreds of routing nodes" before fan-out latency becomes noticeable. The concrete threshold and the trigger criteria for migrating to GossipSub-based distribution to be determined after Step 3c is operational and we have wall-clock numbers.

### 9.3 Terminator tier scale-out (blocks high-throughput managed-mode workloads)

A single terminator on `srv020184` is sufficient through Step 3c. The threshold at which we split into a 2–3 host platform-owned terminator tier with shared cert storage to be determined from Step 3c connection-rate and CPU data.

### 9.4 Coolify override deprecation

The current Traefik file-provider override — generated automatically inside Coolify VMs to suppress the redirect-loop behavior described in §1.1 — becomes legacy once passthrough mode ships. Migration path for existing Coolify users: optional one-time toggle from managed to passthrough; the override remains in place for users who do not toggle.

---

## 10. Non-Goals

This design intentionally does not address:

- **Replacing Direct Access** (`iptables` DNAT on 40000–65535) — that remains the right answer for non-HTTPS protocols (SSH, MySQL, game servers, Shadowsocks).
- **Tenant-controlled CDN, WAF, or DDoS protection** — orthogonal concerns; passthrough mode allows the tenant to deploy these in-VM if desired.
- **Multi-region orchestrator** — orchestrator remains single-region; ingress is the first geographically distributed piece of the platform.
- **Replacing Caddy** — Caddy with `caddy-l4` covers all L4 and L7 needs in one binary. Introducing nginx or envoy would add operational surface for no architectural gain.
- **Per-tenant ingress isolation beyond what SNI routing already provides** — every routing node sees every SNI, by design (§3.8). A future "private workloads" tier requiring per-VM compartmentalization would need a different layer added on top.

---

## 11. References

### Related design

| Document | Relationship |
|---|---|
| `MINECRAFT_VISION_ROADMAP.md` | Strategic frame — permissionless participation, emergent infrastructure |
| `PROJECT_FEATURES.md` §2 | Current implementation — CentralIngress-aware port allocation, Smart Port Allocation |
| `PROJECT_FEATURES.md` §11 | Prebuilt binary distribution pattern — reused for the custom Caddy binary |
| `PROJECT_MEMORY.md` | Architectural principles, deployment methodology |
| `SYSTEM_VM_DESIGN.md` | Pattern this design extends — fourth canonical SystemVM role |
| `BASE_IMAGE_DESIGN.md` | Image registry pattern for the new ingress base image |

### Code touch-points (read during design)

- `src/Orchestrator/Services/CentralCaddyManager.cs`
- `src/Orchestrator/Services/CentralIngressService.cs`
- `src/Orchestrator/Controllers/CentralIngressController.cs`
- `src/Orchestrator/Services/DirectAccessService.cs`
- `src/Orchestrator/Services/NodeService.cs`
- `src/Orchestrator/Services/VmLifecycleManager.cs`
- `src/DeCloud.NodeAgent.Infrastructure/Services/SystemVm/SystemVmReconciler.cs`
- `src/DeCloud.NodeAgent.Core/Interfaces/SystemVm/IOutstandingCommands.cs`
- `src/Shared/Models/SystemVmTemplate.cs`
- `releases/install.sh` (Caddy bootstrap)

### External dependencies introduced

| Dependency | Source | Purpose | Risk |
|---|---|---|---|
| `mholt/caddy-l4` | github.com/mholt/caddy-l4 | L4 SNI routing in the Routing SystemVM and orchestrator Caddy | Community-maintained module by Caddy's author; mature; covered by Caddy's release cadence via the custom build |

---

*End of design document. Next step: review, comment, ratify. Implementation begins at Step 0.*
