# DeCloud Distributed LLM Inference — Design Document

**Date:** 2026-05-19
**Status:** Design — Pre-implementation
**Authors:** BMA + Claude AI assistant

---

## Executive Summary

DeCloud's existing infrastructure — GPU proxy daemon, block store with
layer-aligned model shards, DHT-based peer discovery, WireGuard mesh
networking, per-node trust and reputation scoring, and USDC settlement —
already contains most of the primitives required to run large language models
that exceed any single consumer GPU's VRAM capacity.

This document specifies a three-layer distributed inference system built on
top of those primitives. The system enables a 200 GB+ model to be served
across a voluntary network of consumer GPU providers, governed by open-market
economics, with inference costs distributed across two independent markets:
**availability** (VRAM-hour commitments) and **compute** (per-token GPU work).

No existing DeCloud primitive is redesigned. The system is an optional
participation layer — node operators opt in with a configurable GPU fraction.
Both NVIDIA discrete-GPU hosts and Apple Silicon unified-memory hosts are
first-class participants; the GPU proxy daemon is extended with a Metal/MLX
backend in parallel to its existing CUDA backend.

---

## 1. Vision

A consumer GPU owner running DeCloud can dedicate a fraction of their GPU
VRAM to persistently hosting a slice of a large language model. That slice
— resident in VRAM at all times — participates in a standing layer graph.
When a user submits an inference request, the system routes that request
through the best available path across the graph, collecting layer-by-layer
compute until the final token emerges and streams back to the user.

The model is not assembled per-request. The infrastructure is always-on.
The user is routing through standing capacity, not waking it up.

This maps directly to how DeCloud's block store, DHT, and relay obligations
already work: persistent services that a node commits to running, governed
by network economics, managed by the orchestrator's reconciliation loop.

---

## 2. The Three-Layer Market

Inference economics have two structurally different cost components that
centralized providers cannot separate. DeCloud exposes them independently.

```
┌─────────────────────────────────────────────────────────────┐
│  Layer 1 — Availability Market                              │
│                                                             │
│  Operators commit GPU VRAM fraction to host model layers.   │
│  Paid for: committed VRAM-hours, regardless of traffic.     │
│  Price: supply/demand per zone per model. Voluntary opt-in. │
└─────────────────────────────────────────────────────────────┘
        ↓ supplies capacity to
┌─────────────────────────────────────────────────────────────┐
│  Layer 2 — Compute Market                                   │
│                                                             │
│  Per-token payment to layer hosts on the inference path.    │
│  Paid for: tokens generated, proportional to layer count.   │
│  Routed by: trust score × availability × latency × load.   │
└─────────────────────────────────────────────────────────────┘
        ↓ consumes and extends
┌─────────────────────────────────────────────────────────────┐
│  Layer 3 — Model / Adapter Market                           │
│                                                             │
│  Community publishes base models and LoRA adapters.         │
│  Adapter creators earn per-use revenue on inference.        │
│  Governed by: economic demand + compliance floor.           │
└─────────────────────────────────────────────────────────────┘
```

Each layer clears independently. An operator can price availability low to
attract traffic, then earn per-token on compute. A model creator publishes
once and earns passively. A user pays the market rate for the quality of
path they route through. No committee decides pricing — each component
finds its equilibrium separately.

---

## 3. System Architecture

```
User (OpenAI-compatible API)
          │
          ▼
┌─────────────────────────────┐
│   Inference Front-Door VM   │  ← CPU-only, any node, public IP
│   - Tokenizer + sampler     │  ← OpenAI-compatible HTTP API
│   - Route calculator        │  ← Queries DHT layer graph
│   - Session manager         │  ← Sticky routing per session
│   - Result streamer         │
└────────────┬────────────────┘
             │  llama.cpp --rpc over WireGuard mesh
    ┌────────┴────────────────────────────────┐
    ▼         ▼              ▼               ▼
[LLM VM A] [LLM VM B]  [LLM VM C]     [LLM VM D]
layers 0-15 layers 16-31  layers 32-47   layers 48-79
RTX 4060    RTX 3070      M4 Pro 48GB    M2 Ultra 192GB
GPU Proxy ↓ GPU Proxy ↓  GPU Proxy ↓   GPU Proxy ↓
(CUDA)      (CUDA)        (Metal/MLX)    (Metal/MLX)
Host GPU    Host GPU      Host unified   Host unified
```

A single route can mix backends — the route assembler reasons over the layer
graph in terms of `{model_cid, layer_range, endpoint, trust_score}` records,
not in terms of host hardware. Once weights are resident in the host's
VRAM (or unified memory pool), the RPC interface is identical regardless of
backend.

### 3.1 LLM Layer VM

An optional system VM type. A node agent with a **GPU-capable host** (NVIDIA
discrete or Apple Silicon) and a public IP may elect to run an LLM Layer VM,
committing a configurable fraction of its GPU VRAM (or unified-memory pool)
to host a contiguous range of transformer layers from a registered model.

**Supported GPU proxy backends:**

| Backend | Host hardware | Status | Notes |
|---|---|---|---|
| CUDA | NVIDIA discrete (sm_70+) | v1 | Existing `gpu-proxy-daemon`. Production-ready. |
| Metal / MLX | Apple Silicon (M1+) | v1.x | New daemon backend. See §12 Phase 2.5. |
| ROCm | AMD discrete (RDNA3+) | Deferred to v2 | Smaller current installed base. |

The CUDA and Metal/MLX backends share the same on-wire protocol with the
guest VM shim (`gpu_proxy_proto.h`). The shim sees one virtualized device;
the daemon translates RPCs to the appropriate host API. Routing, layer
graph entries, KV cache, and economics are backend-agnostic.

**Key properties:**
- Hosts a layer range resident in VRAM at all times (warm, not on-demand)
- Runs `llama-rpc-server` against the local GPU proxy daemon
- Registers its availability in the DHT: `{vmId, model_cid, layer_start, layer_end, endpoint, load, trust_score, backend}`
- Reports metrics to the orchestrator on heartbeat
- Earns availability-market revenue proportional to committed VRAM-hours
- Earns compute-market revenue proportional to tokens it serves

**Not an obligation.** Operators opt in explicitly. Operators who do not opt
in are unaffected. Operators who opt in and later withdraw simply stop
advertising their layer range; the router finds alternate paths.

### 3.2 Inference Front-Door VM

A standard tenant VM (CPU-only, no GPU required) that hosts the user-facing
inference API. The front-door is model-aware and network-aware: it holds only
the model's tokenizer and configuration (a few MB), not the weights.

**Key properties:**
- Accepts `POST /v1/chat/completions` and `POST /v1/completions` (OpenAI-compatible)
- Queries the DHT layer graph to find a viable route for the requested model
- Selects the best path by trust × availability × latency × current load
- Runs `llama.cpp` client against the assembled RPC chain
- Streams tokens back as they are generated
- Pins the session to a specific route (sticky routing for KV cache continuity)
- Splits the per-token cost across layer hosts on the route, via the escrow

### 3.3 Layer Graph

The layer graph is a DHT-maintained registry of which nodes currently host
which layer ranges of which models. It is the standing infrastructure that
makes per-request assembly unnecessary.

```
DHT Provider Records (per layer range):
  Key:   SHA256(model_cid + layer_start + layer_end)
  Value: { endpoint, vram_committed, current_sessions,
           tokens_served, trust_score, zone, region, backend }
```

Each LLM Layer VM announces its record on startup and refreshes it on
heartbeat. Records expire if not refreshed (30-minute TTL, consistent with
existing DHT provider record semantics).

The orchestrator maintains an aggregated view of the layer graph per model —
which layer ranges have coverage, which have replication gaps, which zones
are represented. This view drives the availability market: zones with
replication gaps attract higher availability prices, signaling supply to fill.

### 3.4 Deployment Patterns

The Inference Front-Door VM and LLM Layer VM are templates, not centrally-
operated services. The platform is the layer graph and the economic substrate,
not the front-door. This makes two distinct deployment patterns coexist
naturally on the same architecture:

**Pattern 1: DeCloud-operated reference deployment ("DeCloud AI").**
DeCloud Foundation operates well-known instances of the front-door template,
branded and supported, with published SLA and a curated catalog of canonical
models. Layer hosts in this pattern are any operators who choose to advertise
into the canonical model's layer graph. The Foundation may bootstrap layer
hosts for new canonical models on Foundation-controlled nodes and withdraw
them as organic supply appears (see §15.4).

**Pattern 2: Community-operated alternative deployments.**
Any operator or operator collective can deploy their own front-door instance.
A community front-door may specialize: a particular model (a fine-tune the
DeCloud catalog does not carry), a particular region or zone, a particular
user base (Discord community, research lab, business), a particular pricing
tier (premium high-availability, or aggressive low-cost). Community front-
doors consume the same DHT layer graph, settle through the same escrow,
and pay the same platform fee on settlement.

Both patterns are first-class. The platform does not privilege the
Foundation's front-door in routing, settlement, or layer-graph access —
its advantage is brand, SLA, and the operational work the Foundation
chooses to put into it.

---

## 4. Operator Configuration

Operators configure LLM participation through the node agent's configuration
surface. No orchestrator push — the operator declares intent, the agent acts
on it.

```yaml
# /etc/decloud/gpu-pool.yaml (node agent config)

gpu_pool:
  # Fraction of GPU VRAM (or unified memory) to commit to LLM layer hosting.
  # 0.0 = opted out entirely (default)
  # 0.25 = 25% committed, 75% free for other workloads
  # 1.0 = fully committed
  llm_commitment_fraction: 0.25

  # Which catalog model to participate in.
  # Must be a registered model CID or well-known alias.
  llm_model: "llama-3-70b-instruct-q4"

  # Availability price floor (USDC per VRAM-GB-hour).
  # If market clears below this, the node withdraws its offer.
  llm_availability_floor_usdc: 0.002

  # Per-token price floor (USDC per 1M tokens).
  # If routed path clears below this, the node declines to serve.
  llm_compute_floor_usdc: 0.50
```

### 4.1 Practical VRAM Commitment Examples

**NVIDIA discrete GPUs** (VRAM is a separate pool from system RAM):

| GPU | Total VRAM | 25% commit | Layer capacity (70B Q4, 80 layers) |
|---|---|---|---|
| RTX 4060 (mobile) | 8 GB | 2 GB | ~4 layers |
| RTX 3060 / 4060 | 12 GB | 3 GB | ~6 layers |
| RTX 4060 Ti | 16 GB | 4 GB | ~8 layers |
| RTX 3090 / 4090 | 24 GB | 6 GB | ~12 layers |
| RTX 3080 12G | 12 GB | 3 GB | ~6 layers |

**Apple Silicon unified memory** (CPU and GPU share one pool; commitment is
a fraction of total system RAM, with practical headroom retained for the OS
— see §4.2):

| Host | Unified mem | 25% commit | Layer capacity (70B Q4, 80 layers) |
|---|---|---|---|
| M4 Mac mini | 16 GB | 4 GB | ~8 layers |
| M4 Mac mini | 24 GB | 6 GB | ~12 layers |
| M4 Pro Mac mini | 48 GB | 12 GB | ~24 layers |
| M4 Max Mac Studio | 64 GB | 16 GB | ~32 layers |
| M2 Ultra Mac Studio | 192 GB | 48 GB | full 70B-Q4 alone |

A 70B-Q4 model (~40 GB, 80 layers) across a zone of average consumer nodes
at 25% commitment requires roughly 12–15 active layer hosts per replica.
With R=2 replication, that is 24–30 nodes per zone per model — a realistic
participation target for a mature zone. Larger Apple Silicon hosts can
single-handedly cover a full replica, which is the optimal topology for
single-operator LAN clusters (see §6.4).

### 4.2 Operator Comfort Guarantees

**NVIDIA discrete-GPU hosts.** At 25% VRAM commitment:
- A gaming workload at 1080p typically fits in the remaining 75%
- A 7B-class inference job (~4 GB) runs in parallel without contention
- VRAM-intensive workloads (image generation, fine-tuning) can still run
  on the uncommitted fraction
- The committed fraction is static — no dynamic reallocation during gaming

If the operator wants full commitment (all-in on LLM hosting), they set
`llm_commitment_fraction: 1.0`. This maximizes availability earnings at the
cost of GPU exclusivity for LLM hosting.

**Apple Silicon unified-memory hosts.** Unified memory is shared with the
OS, application processes, file caches, and any other workloads. The
practical comfort ceiling is lower than on discrete GPUs:

- Recommended maximum commitment: **~50% of total unified memory** for
  hosts ≤32 GB; **~70%** for hosts ≥64 GB
- The OS needs working room for kernel caches, application memory, and
  GPU shader/texture caches for the desktop compositor
- Sustained commitment above the recommended ceiling will cause memory
  pressure → swap → severe latency degradation for both the LLM Layer VM
  and the host's user workloads
- The node agent reports `commit_ceiling_warning` in heartbeat if the
  configured fraction would exceed the recommended ceiling

A dedicated Apple Silicon host (Mac mini in a rack, no desktop user
workloads) can safely run at 80–90% commitment. The node agent's
recommendation is conservative by default; operators with dedicated hosts
override it knowingly.

---

## 5. Layer Assignment & Model Sharding

### 5.1 Layer-Range Assignment

When a node registers its LLM intent, the orchestrator assigns a layer range
based on the node's committed VRAM and the current coverage gaps in the
layer graph for that model.

Assignment algorithm:
1. Compute `layer_capacity = floor(committed_vram / bytes_per_layer)`
2. Query the layer graph for the model: which ranges have the fewest replicas?
3. Assign the node to cover the most under-replicated contiguous range it can fit
4. If no gaps exist, assign to the range with the fewest replicas (improve R)

This is equivalent to the block store's Kademlia XOR proximity logic, but
applied to a linear layer index rather than a hash space.

### 5.2 Model Weight Distribution via Block Store

Model weights are distributed using the existing block store's `model-shard`
manifest type (64 MB chunks, aligned to transformer layer boundaries).

When an LLM Layer VM boots:
1. It receives its layer range assignment from the orchestrator (via cloud-init label)
2. It computes the CIDs of the `model-shard` chunks covering its layer range
3. It requests those chunks from the block store via bitswap
4. Bitswap fetches from nearest same-zone providers first (libp2p locality)
5. Chunks are written to disk; `llama-rpc-server` mmaps them into VRAM
6. The VM registers its layer range in the DHT and reports Ready

For a 70B-Q4 model split across 15 nodes at ~2.7 GB per node:
- Each node fetches ~42 × 64 MB chunks = ~2.7 GB
- At same-zone LAN speeds (500 Mbps), fetch time: ~45 seconds
- mmap + VRAM load: ~30 seconds
- Total cold-start per node: **~75 seconds**

Because nodes warm up independently and the layer graph is persistent, a
user request doesn't wait for cold start. The graph is warm by the time
requests arrive. Cold start only matters during initial network bootstrap
or after a zone-wide outage.

### 5.3 Layer Profile

Not all transformer layers have equal compute cost. A layer profile per
model records the relative compute weight of each layer, used during
layer-range assignment to balance load across nodes.

```json
{
  "model_cid": "bafk...",
  "total_layers": 80,
  "layer_weights": [1.2, 1.0, 1.0, ..., 1.4],
  "embedding_cost": 0.3,
  "lm_head_cost": 0.8
}
```

Layer profiles are published as `model-shard` metadata and fetched once at
assignment time. The orchestrator uses them to balance assignment — a node
with 6 GB of commitment gets 6 high-cost layers or 9 low-cost layers, not
naively 7 layers.

---

## 6. Inference Routing

### 6.1 Route Selection

When the front-door VM receives an inference request, it queries the DHT
for all registered layer-range providers for the requested model. It builds
a candidate set of routes and scores each:

```
route_score = trust_score × availability_score × (1 / latency_ms) × (1 / load)
```

Where:
- `trust_score` — node's reputation score from the orchestrator (0–1)
- `availability_score` — uptime percentage over last 30 days (0–1)
- `latency_ms` — measured RTT to the layer host, probed periodically
- `load` — current active session count reported in DHT record

The highest-scoring complete route (one provider per layer range, covering
all layers) is selected. If no complete route exists (a layer range has
zero coverage), the model cannot be served; the front-door returns 503.

### 6.2 Locality Enforcement

Locality is a hard constraint on route selection, not a soft preference.
The front-door enforces:

| Constraint | Level | Enforcement |
|---|---|---|
| Same region | Hard minimum | Routes spanning multiple regions are rejected |
| Same zone | Preferred | Zone-homogeneous routes are scored 1.5× |
| Same LAN (single-operator cluster) | Preferred | Scored 1.75× (see §6.4) |
| Same node (multi-GPU) | Best case | Scored 2× (avoids WAN entirely) |

A route is only assembled from nodes in the same region. If multiple regions
have coverage, the front-door picks the region closest to the user (by IP
geolocation) and routes entirely within it.

This constraint is what makes the throughput math viable:

| Locality tier | Per-hop RTT | Network overhead (4 hops) | Net tok/s (70B Q4) |
|---|---|---|---|
| Same LAN (single cluster) | <1 ms | <4 ms | ~14 |
| Same zone (LAN) | ~1 ms | ~4 ms | ~14 |
| Same region (metro) | ~10 ms | ~40 ms | ~10 |
| Cross-region | ~60 ms | ~240 ms | ~3 |

Cross-region routes are rejected. ~3 tok/s is below interactive threshold.

### 6.3 Sticky Routing and KV Cache Continuity

Each layer host accumulates a KV cache for its layer range during a session.
This cache encodes the attention context for tokens already generated. It is
local state that cannot be transferred to another node mid-session without
re-running the full prompt.

**v1 approach: sticky routing.** A session pins to its initial route for its
entire lifetime. The front-door stores `session_id → route_map` in memory.
All subsequent tokens for a session follow the same path.

Consequences:
- A mid-session node failure ends the session. The user retries; a new route
  is selected; the new path re-processes the prompt context (visible stall,
  ~1–5 seconds depending on context length). Acceptable for chat workloads.
- Load balancing occurs at session start, not per-token. Long sessions on
  busy nodes may see degraded throughput as other sessions are added. The
  scoring function's load term mitigates this by deprioritizing busy nodes
  for new sessions.

**v2 consideration (deferred):** KV-cache snapshotting to block store on
failover. Requires protocol work; deferred until v1 traffic informs the
actual failure rate and whether the engineering investment is justified.

### 6.4 Cluster Deployment Patterns

The locality scoring in §6.2 supports three deployment topologies, each
with distinct economic and operational characteristics:

**Distributed (the default case described elsewhere in this document).**
Many independent operators across a zone, each committing a fraction of
their GPU. Replicas span operator boundaries. Trust-weighted routing is
essential because activations cross operator trust domains. This is the
target topology for organic network growth.

**Single-operator LAN cluster.**
One operator owns several homogeneous hosts on the same LAN — for example,
8–10 Apple Silicon Mac minis in one rack, or a small NVIDIA workstation
cluster in a homelab. All layer hosts have the same owner; activations
never cross operator trust domains; per-hop RTT is sub-millisecond. Routes
assembled entirely within such a cluster receive the §6.2 same-LAN bonus.

Key properties of the single-operator cluster topology:
- Optimal throughput: same-LAN RTT plus no inter-operator trust overhead
- Simplified trust model: operator vouches for the whole cluster
- Single billing surface: all layer-host earnings accrue to one wallet
- Onboarding-friendly: an operator who already runs N local-LLM hosts
  (e.g., existing OpenClaw users) can opt the whole cluster in atomically
- Natural fit for Apple Silicon: large unified-memory hosts (M4 Pro, M4
  Max, M2 Ultra) each cover many layers, so a small cluster can host a
  full 70B replica with R≥2 internal redundancy

A single-operator cluster behind CGNAT (one public IP for the LAN) is
viable provided one host in the cluster has accepted the standard relay
arrangement: the front-door reaches the cluster ingress through the
existing CGNAT relay, and intra-cluster activation traffic stays on the
LAN. See §13 for the revised CGNAT position.

**Hybrid.**
A single-operator cluster supplemented by additional distributed nodes.
The cluster provides one strong replica; distributed nodes provide
diversity. Routes may prefer the cluster (high score from same-LAN locality
and operator-internal trust) but fall back to distributed nodes when the
cluster is saturated or under maintenance.

The route assembler does not need to know about these patterns explicitly —
they emerge from locality scoring and trust accumulation on the existing
graph. The patterns are documented here for operator guidance and capacity
planning.

---

## 7. Economic Model

### 7.1 Availability Market

Operators earn for committed VRAM-hours, regardless of inference traffic.
This compensates the opportunity cost of holding layers warm.

```
availability_earnings = committed_vram_gb × hours_online × availability_rate_usdc
```

The availability rate floats with supply and demand per (zone, model) pair.
The orchestrator publishes a reference rate; operators set a floor below
which they withdraw. The market clears at the intersection.

Example at $0.003 USDC/GB-hour:
- RTX 3060, 25% commit → 3 GB × 24h = $0.216/day = $6.48/month
- RTX 4090, 50% commit → 12 GB × 24h = $0.864/day = $25.92/month
- M4 Pro Mac mini 48 GB, 50% commit → 24 GB × 24h = $1.728/day = $51.84/month

These are availability earnings only — compute earnings on top.

### 7.2 Compute Market

Per-token earnings distributed across the route, weighted by layer count.

```
per_token_total = model_compute_rate × (1 / tokens_per_second_delivered)
per_node_share  = per_token_total × (node_layer_count / total_layers)
```

A node hosting 8 of 80 layers earns 10% of the per-token compute rate for
every token that routes through it.

Example at $1.00/M tokens, 70B model, route of 15 nodes:
- Node with 6 layers: 6/80 = 7.5% share = $0.075/M tokens routed through it
- At 10 tok/s sustained, 1M tokens takes ~27.8 hours of serving
- Earning rate per busy serving hour: ~$0.036

Availability + compute combined makes the economics meaningful at moderate
utilization levels. Pure compute without availability would produce race-to-
the-bottom pricing; pure availability without compute would disincentivize
actual serving. The split is intentional.

### 7.3 Adapter Market

LoRA adapter creators publish adapters to the block store as
`lora-adapter` manifest type (256 KB chunks). A user requesting inference
with a specific adapter pays a per-token royalty to the adapter creator,
distributed via the existing escrow settlement mechanism.

```
adapter_royalty_per_token = adapter_rate (set by creator, discoverable in catalog)
```

The front-door loads the requested adapter shards at session start (a few MB,
fast). The layer hosts apply the LoRA delta on each forward pass (negligible
compute overhead for r≤16 adapters).

Adapter royalties clear independently of compute and availability. A popular
adapter can command a royalty premium; an unpopular one earns nothing
regardless of its rate.

### 7.4 Settlement Flow

```
User escrow (USDC, locked at session start)
    │
    ├─→ Availability pool (pre-settled hourly per zone)
    │       ↓ distributed to all layer hosts in zone by VRAM-hours
    │
    ├─→ Compute payments (settled per-token, per session close)
    │       ↓ split across route nodes by layer share
    │
    └─→ Adapter royalty (settled per session close)
            ↓ to adapter creator wallet
```

The existing `DeCloudEscrow.sol` contract and `OnChainSettlementService.cs`
handle the mechanics. The new primitives are: availability pool accounting
(hourly, zone-scoped) and per-route compute split (per-session, per-node).

---

## 8. Replication and Fault Tolerance

### 8.1 Replication Factor

Each layer range target has a replication factor R (default R=2). The
orchestrator's reconciliation loop monitors the layer graph and flags ranges
with fewer than R active providers as under-replicated. Under-replicated
ranges receive an elevated availability rate signal, attracting new operators.

With R=2 and 95% per-node availability:
- Per-stage availability: 1 - (1-0.95)² = 99.75%
- 20-stage chain availability: 0.9975²⁰ ≈ 95.1%

With R=3:
- Per-stage availability: 1 - (1-0.95)³ = 99.99%
- 20-stage chain availability: 0.9999²⁰ ≈ 99.8%

R=2 is the minimum for production. R=3 is the target for high-demand models
or zones with many consumer (higher-churn) nodes.

### 8.2 Node Failure Handling

**During session:** Sticky routing means a node failure ends the active
session. The front-door detects the TCP disconnect, marks the session
failed, and returns an error to the user. The user retries; the router
selects a new path excluding the failed node.

**For new sessions:** The router excludes nodes whose DHT record has expired
(30-minute TTL) or whose heartbeat-derived trust score has dropped below
threshold. New sessions route automatically around failed nodes as long as
R≥2 coverage exists.

**Layer range recovery:** The reconciler detects under-replication and
signals higher availability rates for the gap zone. Operators responding
to the signal bring new layer hosts online. Layer weights fetch via block
store bitswap, VRAM loads, DHT record registers. The gap closes without
orchestrator intervention beyond the pricing signal.

---

## 9. Model Catalog and Governance

### 9.1 v1: Single Canonical Model

The network launches with one canonical model — an open-weight, permissive-
license model at a size that consumer GPU supply can realistically serve.

Recommended: **Llama-3 70B Instruct Q4_K_M** (~40 GB)

Rationale:
- Well-understood benchmark — community knows its capability floor
- Q4 quantization fits the consumer VRAM envelope
- Apache 2.0 license (no hosted-inference restrictions)
- Sufficient quality for chat, coding, and instruction-following use cases

### 9.2 Catalog Growth

Additional models enter the catalog when:
1. Sufficient operator supply exists in at least one zone (≥R×2 node commitment)
2. The model's license permits hosted inference
3. The model passes the compliance review gate (existing template review
   process applies to model registration)

Catalog additions are supply-driven, not committee-driven. If operators
commit supply for a model and the license clears, it is served. If supply
never materializes, the model is not available — no curation needed.

### 9.3 Compliance Floor

Model selection and adapter publication operate above the existing
compliance floor:
- CSAM filtering applies to all generated content (planned, per COMPLIANCE.md)
- DMCA takedown applies to model weights hosted in block store
- ToS acceptance required for model publishers and adapter creators
- Template review gate applied to model registration (same rubric as templates)

The compliance floor does not dictate content policy above the legal
minimum. Censorship-resistant operation is the platform's stated value;
community-published adapters that push model behavior within legal bounds
are the platform's differentiated offer.

---

## 10. Integration with Existing DeCloud Primitives

| Existing primitive | How it is used |
|---|---|
| `gpu-proxy-daemon` | Each LLM Layer VM's RPC backend runs against it. Extended in Phase 2.5 with a Metal/MLX host backend alongside the existing CUDA backend; on-wire protocol unchanged. See `GPU_PROXY_ARCHITECTURE_REVIEW.md`. |
| `model-shard` block store type | Weight distribution at boot. 64 MB chunks, layer-aligned. Already designed. |
| `lora-adapter` block store type | Adapter distribution at session start. 256 KB chunks. Already designed. |
| DHT provider records | Layer graph registry. Each LLM VM announces its record. Same mechanism as block CID announcements. Record extended with `backend` field. |
| WireGuard mesh | Secure inter-stage activation transport. Already connects all nodes. |
| `NodeLocality` (region/zone) | Hard constraint on route assembly. `region` must match across all route members. Same-LAN sub-zone affinity used by §6.4 cluster scoring. |
| `IObligationEligibility` | Extended with `CheckLlm()` — GPU-capable host (CUDA or Metal/MLX), public IP (or LAN-cluster ingress via existing relay), VRAM ≥ minimum per-stage size. |
| Reputation / trust score | Primary routing signal. High-trust nodes are preferred; low-trust nodes are deprioritized without being blocked. |
| `DeCloudEscrow.sol` | Settlement. Extended for availability pool accounting and per-route compute splits. |
| `VmType` enum | New value: `LlmLayer`, `InferenceFrontDoor`. |
| `SystemVmRole` enum | `LlmLayer` added as optional (not obligated) role. |
| `TemplateSeederService` | New seed templates: `system-llm-layer`, `inference-frontend`. |

---

## 11. Trust Model and Activation Privacy

### 11.1 Trust-Weighted Routing

Routing preference is the primary trust mechanism. Nodes that:
- Drop sessions frequently → lower completion rate → lower trust score
- Serve slower than declared → completion-rate penalty
- Go offline during active sessions → uptime penalty

...receive lower trust scores and are selected last for new sessions.
Bad actors self-select out economically: they earn less, eventually nothing.

No cryptographic attestation of GPU behavior is required in v1. Trust is
behavioral, accumulated, and reputation-expressed — the same model that
governs node quality for general VMs.

### 11.2 Activation Privacy

Each layer host sees plaintext intermediate activations passing through.
These activations encode information about the user's prompt — recent
research shows partial prompt reconstruction is possible from mid-layer
representations.

There is no cryptographic fix for this. Homomorphic encryption over
activations is ~1000× slower than plaintext compute; it is not practically
viable on consumer hardware.

The honest position:

> Layer hosts see activations in plaintext, in the same way that a VPN
> provider sees your traffic. The platform does not and cannot prevent
> a malicious layer host from logging activations. Users should route
> through high-trust, high-reputation operators for sensitive workloads,
> the same way they choose a VPN provider. For truly sensitive inference,
> a private single-node deployment (which DeCloud also supports) is the
> appropriate architecture.

A single-operator LAN cluster (§6.4) reduces but does not eliminate this
concern: activations cross fewer trust domains (one operator, not many),
but that one operator still sees plaintext activations end-to-end. Trust
in a single-operator route is no stronger than trust in that operator.

This limitation is disclosed in operator terms and user documentation.
It is not a v2 problem to solve — it is a fundamental property to acknowledge.

---

## 12. Implementation Plan

### Phase 1 — Runtime Validation (no orchestrator changes)

**Goal:** Prove the throughput and latency numbers on real hardware before
building orchestration.

Stand up five nodes in the same zone manually. Assign layer ranges by hand.
Fetch model weights from HuggingFace directly (not via block store yet).
Run `llama-rpc-server` on each. Route through a hardcoded front-door. Measure:
- First-token latency
- Sustained tok/s at varying context lengths
- What actually breaks (connection drops, load imbalance, KV cache size)

**Exit criterion:** Confirmed ≥8 tok/s on 70B-Q4 at same-zone locality.

### Phase 2 — Block Store Integration

**Goal:** Replace manual weight fetch with block store model-shard distribution.

- Publish Llama-3 70B Q4 as `model-shard` manifest (640 × 64 MB chunks)
- LLM Layer VM boot sequence fetches its layer range via bitswap
- Measure actual cold-start time per node in production zone

**Exit criterion:** LLM Layer VM cold-starts in under 3 minutes from
a block store with same-zone providers.

### Phase 2.5 — Metal/MLX Backend (parallel work stream)

**Goal:** Bring Apple Silicon hosts to feature parity with NVIDIA discrete
hosts as LLM Layer VM participants. Runs in parallel with Phases 2–3; the
orchestrator changes in Phase 3 are backend-agnostic from the start.

- Add a Metal/MLX backend to `gpu-proxy-daemon` alongside the existing CUDA
  backend. Same on-wire protocol (`gpu_proxy_proto.h`); the daemon
  dispatches RPCs to the appropriate host API by command and runtime
  configuration.
- Map the existing CUDA proxy command set (memory, kernel launch, streams,
  events, GEMM) to Metal Performance Shaders / MLX equivalents. Where a
  CUDA-specific concept has no Metal analog (e.g., separate device-pointer
  address space), translate to the unified-memory model transparently.
- Extend `ResourceDiscoveryService` to detect Apple Silicon hosts and
  report Metal/MLX availability, unified memory size, and recommended
  commitment ceiling (per §4.2).
- Validate by running `llama-rpc-server` (with Metal backend) against the
  extended daemon on an Apple Silicon host, joining a same-LAN cluster
  with CUDA hosts. The layer graph must accept mixed-backend routes
  transparently.

**Exit criterion:** A 70B-Q4 single-operator LAN cluster mixing one M4 Pro
host and two NVIDIA discrete-GPU hosts sustains the §14 throughput target,
with the front-door selecting routes through both backends without
backend-aware code.

**Out of scope:** Performance parity tuning per backend (an
M4 Pro will not match an RTX 4090 token-for-token; this is acceptable as
long as the route assembler accounts for it through the existing latency
and load signals).

### Phase 3 — LLM Layer VM Template + DHT Registration

**Goal:** Automated deployment of layer hosts via the orchestrator.

- `VmType.LlmLayer` added to enum
- `CheckLlm()` eligibility predicate (GPU-capable host + public IP or
  LAN-cluster ingress + VRAM/unified-memory threshold)
- `system-llm-layer` cloud-init template
  - Reads layer assignment from cloud-init label
  - Fetches layer-range shards from block store
  - Starts `llama-rpc-server` on assigned port
  - Registers DHT provider record on Ready (including `backend` field)
- Operator configuration surface (`gpu-pool.yaml`)
- Orchestrator layer graph view (aggregated from DHT + heartbeats)

**Exit criterion:** A node operator sets `llm_commitment_fraction: 0.25`
and the orchestrator automatically assigns, deploys, and registers the
LLM Layer VM without manual intervention — on either backend.

### Phase 4 — Inference Front-Door VM + Routing

**Goal:** User-facing inference API with automatic route selection.

- `VmType.InferenceFrontDoor` added to enum
- `inference-frontend` template (CPU-only VM, tokenizer + sampler + router)
- Route calculator: queries DHT layer graph, scores paths, enforces locality
  (including §6.4 same-LAN cluster bonus)
- Session manager: sticky routing, session-to-route persistence
- OpenAI-compatible API surface (`/v1/chat/completions`, `/v1/completions`)
- Streaming token response

**Exit criterion:** A user calls the API and gets streaming tokens routed
transparently through the layer graph. No awareness of the underlying
pipeline required on the user's side.

### Phase 5 — Economics

**Goal:** Operators earn; users pay; settlement clears on-chain.

- Availability pool accounting (hourly, zone-scoped, VRAM-weighted)
- Per-token compute split across route nodes (session-close settlement)
- Escrow extension in `DeCloudEscrow.sol`
- `OnChainSettlementService` extension for new settlement types
- Operator earnings dashboard in node marketplace UI

**Exit criterion:** End-to-end: user pays USDC, session settles, operator
wallet balance increases by correct availability + compute share.

### Phase 6 — Adapter Market

**Goal:** LoRA adapter creators earn from their work.

- Adapter publish flow (block store `lora-adapter` manifest)
- Front-door loads adapter at session start
- Per-token royalty accounting
- Adapter catalog endpoint
- Creator earnings settlement

---

## 13. What Is NOT in v1

Explicit deferrals. These are known gaps, not oversights.

| Deferred | Rationale |
|---|---|
| KV-cache failover (mid-session node replacement) | Requires new protocol. v1 session-fail-and-retry is acceptable. Revisit when failure rate data exists. |
| Cross-operator pipeline trust (activation attestation) | No clean cryptographic solution. Mitigated by trust-weighted routing. |
| Multi-model catalog governance | Single canonical model for v1. Supply-driven catalog growth in v2. |
| Tensor parallelism (within a single layer, across nodes) | Requires NVLink-class bandwidth. Not viable on consumer WAN. |
| ROCm backend (AMD discrete GPUs) | Smaller current installed base than NVIDIA or Apple Silicon. Slot for v2 once Metal/MLX validates the multi-backend architecture. |
| CGNAT layer hosts in *distributed* topology | Activation routing through 3-hop DNAT introduces unpredictable latency when each hop is a separate operator behind CGNAT. Single-operator LAN clusters behind one CGNAT (one public IP for the whole cluster, intra-cluster activations stay on the LAN) ARE supported in v1 via the existing relay path — see §6.4. |
| Dynamic batch routing (multiple users, one pipeline) | llama.cpp --rpc is single-stream. Multi-tenant batching requires vLLM integration. Phase 7 candidate. |
| Automatic layer rebalancing during a session | Sticky routing simplifies v1. Rebalancing between sessions is sufficient. |

---

## 14. Success Metrics

### Phase 1–2 (Runtime + Block Store)
- ≥8 tok/s sustained throughput on 70B-Q4, same-zone, 5 nodes
- First-token latency ≤ 3 seconds (post-warmup)
- Layer VM cold-start ≤ 3 minutes from block store
- Zero manual intervention for weight distribution

### Phase 2.5 (Metal/MLX Backend)
- `gpu-proxy-daemon` accepts the existing on-wire protocol and dispatches
  correctly to either CUDA or Metal/MLX based on host detection
- Mixed-backend route (≥1 NVIDIA host + ≥1 Apple Silicon host, same LAN)
  sustains the Phase 1–2 throughput target
- Resource discovery reports Apple Silicon unified memory and recommended
  commitment ceiling without operator configuration

### Phase 3–4 (Automated Deployment + Routing)
- LLM Layer VM deploys and registers within 5 minutes of operator opt-in
- Layer graph maintains ≥R=2 coverage per layer range without orchestrator intervention
- Route selection latency ≤ 100 ms (DHT query + scoring)
- 99%+ of sessions find a valid same-zone route (given adequate supply)

### Phase 5–6 (Economics + Adapters)
- Operator earnings settle correctly within 1 hour of session close
- Availability pool distributes within ±2% of committed VRAM-hour share
- Adapter royalties flow to creator wallet within 1 hour of session close

### Network Scale Targets
- 70B-Q4 per zone: ≥30 layer hosts (R=2, avg 3 GB commit), 3 zones
- First external operator earning availability revenue within 30 days of Phase 5 deploy
- First community-published LoRA adapter with nonzero royalty earnings within 60 days of Phase 6 deploy
- First single-operator Apple Silicon LAN cluster onboarded within 60 days of Phase 2.5 deploy

---

## 15. Open Questions

These are unresolved design decisions that should be answered before Phase 3
implementation begins:

1. **Availability rate discovery mechanism.** Does the orchestrator publish
   a reference rate and let operators undercut it? Or does the orchestrator
   run a periodic auction? The simplest v1 answer is a platform-published
   floor rate (similar to compute point floor rates) with operator-set
   minimums above it.

2. **Layer range assignment on VRAM contention.** If an operator reduces
   their commitment fraction mid-service (e.g., gaming launches), how does
   the node agent handle VRAM pressure? Options: SIGTERM the LLM VM (session
   fails), pre-empt a graceful drain, or enforce commitment as a locked
   reservation. Recommendation: lock committed VRAM at LLM VM start; operator
   changes take effect on next restart. Simple and predictable. On Apple
   Silicon, the "lock" is implemented via wired-memory allocation (the
   Metal/MLX backend uses `MTLResourceStorageModePrivate` plus residency
   pinning) rather than VRAM partitioning, but the operator-facing semantics
   are identical.

3. **Front-door VM cost.** Who pays for the front-door VM? Options: user
   pays an API access fee that covers the front-door CPU cost; or the
   orchestrator subsidizes front-door VMs as network infrastructure (similar
   to relay VMs). Recommendation: user-paid, priced into the per-token rate.
   This applies uniformly to DeCloud-operated and community-operated
   front-doors (§3.4) — neither is subsidized at the platform layer.

4. **Layer graph bootstrap.** When the first zone has no layer hosts for a
   model, how does supply bootstrap? The availability rate signal only
   attracts operators if they're already watching. This is the classic cold-
   start problem. Mitigation: the orchestrator may seed one or two canonical
   layer hosts on orchestrator-controlled nodes for v1, withdrawing them as
   organic supply appears.

5. **Backend-aware compute pricing.** A token served by an M4 Mac mini and
   a token served by an RTX 4090 take different wall-clock time but
   represent the same unit of work from the user's perspective. v1
   recommendation: price per-token uniformly (a token is a token), let the
   latency-and-load route scoring naturally bias traffic toward faster
   nodes, and let the availability market compensate slower nodes through
   the VRAM-hour channel. Revisit if real traffic shows pricing distortions.

---

*For GPU proxy implementation details, see `GPU_PROXY_ARCHITECTURE_REVIEW.md`.*
*For block store design, see `BLOCKSTORE_DESIGN_IMPLEMENTATION.md`.*
*For scheduling and locality, see `SCHEDULING.md` and `LOCALITY_STANDARDS.md`.*
*For compliance framework, see `COMPLIANCE.md`.*
