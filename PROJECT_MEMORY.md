# DeCloud Project Memory

**Last Updated:** 2026-01-30  
**Status:** Active Development - Phase 1 (Marketplace Foundation) In Progress

---

## Purpose & Context

BMA is developing **DeCloud**, a decentralized cloud computing platform that provides **censorship-resistant infrastructure** for AI hosting and general compute workloads. The platform's core mission is creating "infrastructure that cannot be shut down" by centralized authorities, positioning it as a unique solution for privacy-sensitive and unrestricted AI deployment.

### Core Differentiators
- **Full VMs vs. Containers:** Unlike Akash Network, DeCloud provides full virtual machines through libvirt/KVM rather than containerized solutions
- **Censorship Resistance:** Targets markets that centralized providers won't serve
- **Privacy-First:** No KYC, wallet-based authentication, encrypted communications
- **Universal Participation:** Relay infrastructure enables CGNAT nodes (60-80% of mobile users) to participate

### Strategic Vision: "The Minecraft of Compute"

DeCloud aims to replicate Minecraft's success factors in the decentralized compute space:

**Minecraft's Magic:**
1. **Simple Primitives** → Complex Creation (dirt blocks → castles)
2. **Composability** → Redstone logic, command blocks
3. **Permissionless Creation** → No approval needed
4. **Emergent Complexity** → Community-driven innovation

**DeCloud's Translation:**
1. **Simple Primitives** → VMs, templates, nodes are building blocks
2. **Composability** → Templates can reference other templates, multi-VM setups
3. **Permissionless Creation** → Anyone can create/share templates
4. **Emergent Complexity** → Community creates use cases we never imagined

**Critical Insight:** Users don't want "decentralized cloud" - they want to **build and share cool things**. The infrastructure is just the enabling layer.

---

## Technical Architecture

### Orchestrator-Node Model
```
User → Orchestrator (coordinator) → Node Agents (VM hosts)
```

**Orchestrator (srv020184 - 142.234.200.108):**
- VM scheduling and lifecycle management
- Node registration and heartbeat monitoring
- Resource allocation using compute points
- Billing and payment processing (USDC on Polygon)
- MongoDB Atlas for persistence
- React/TypeScript frontend with WalletConnect

**Node Agents (e.g., srv022010):**
- KVM/libvirt virtualization
- Resource discovery (CPU, memory, GPU, storage)
- VM provisioning and management
- WireGuard networking
- SQLite for local state

### VM Types
1. **General VMs** - User workloads (AI, web apps, databases)
2. **Relay VMs** - Auto-deployed for CGNAT node networking
3. **DHT VMs** - Future: Decentralized coordination
4. **Inference VMs** - Future: AI-specific optimizations

### Quality Tiers & Resource Allocation
- **Guaranteed:** 1:1 compute points (no overcommit)
- **Standard:** 1.5:1 overcommit ratio
- **Balanced:** 2:1 overcommit ratio
- **Burstable:** 4:1 overcommit ratio

CPU points calculated via sysbench benchmarking, normalized against baseline performance.

### Relay Infrastructure (Critical Innovation)
**Problem:** 60-80% of nodes are behind CGNAT and can't accept inbound connections.

**Solution:** Relay VMs create WireGuard tunnels:
1. Orchestrator detects CGNAT node during registration
2. Automatically deploys lightweight relay VM on public-IP node
3. Relay VM establishes WireGuard tunnel to CGNAT node
4. NAT rules route traffic through relay
5. **Symbiotic Economics:** Relay node earns passive income, CGNAT node gets connectivity

**Licensing Opportunity:** Relay system could be standalone product ("DeCloud Relay SDK") for other decentralized platforms.

---

## Current State (Production)

### Operational Deployment
- **Orchestrator:** srv020184 (142.234.200.108) - Ubuntu, .NET 8
- **Node Agent:** srv022010 + ARM nodes (Raspberry Pi support)
- **Database:** MongoDB Atlas (orchestrator), SQLite (nodes)
- **Frontend:** React/Vite with WalletConnect integration
- **SSL/TLS:** Caddy reverse proxy with Let's Encrypt

### Working Features
✅ End-to-end VM creation and lifecycle management  
✅ Wallet-based authentication (Ethereum signatures)  
✅ SSH access with wallet-encrypted passwords  
✅ HTTPS deployment with automatic certificates  
✅ Relay infrastructure for CGNAT nodes  
✅ CPU benchmarking and resource tracking  
✅ USDC payments on Polygon blockchain  
✅ WebSocket terminal access  
✅ Automated deployment via install.sh scripts  
✅ ARM architecture support (Raspberry Pi)  
✅ Node marketplace with search and filtering  
✅ Real-time node reputation tracking (uptime, reliability)  

### Recent Achievements (2026-01-30)

🎉 **Goal 1 Complete: Node Marketplace (Backend + Frontend)**

**Backend Implementation (2026-01-29):**
- Added `Description`, `Tags`, `BasePrice` fields to Node model
- Implemented [NodeMarketplaceService](file:///c:/Users/BMA/source/repos/DeCloud.Orchestrator/src/Orchestrator/Services/NodeMarketplaceService.cs#29-228) with search/filtering
- Created 4 REST API endpoints:
  - `GET /api/marketplace/nodes` - Search with filters
  - `GET /api/marketplace/nodes/featured` - Top 10 nodes
  - `GET /api/marketplace/nodes/{id}` - Node details
  - `PATCH /api/marketplace/nodes/{id}/profile` - Update profile
- 100% backward compatible (no breaking changes)

**Frontend Implementation (2026-01-30):**
- Built complete marketplace UI module (`marketplace.js`)
- Integrated into existing Nodes navigation page
- **Search & Filtering:**
  - Tags (comma-separated, e.g., "gpu, nvme")
  - Region filtering
  - Price range (max USDC/point/hr)
  - Minimum uptime percentage
  - Minimum compute points
  - GPU requirement toggle
  - Online-only filter
  - Sort by: uptime, price, or capacity
- **Featured Nodes Display:**
  - Top 10 nodes with >95% uptime
  - Curated based on capacity and performance
- **Node Cards UI:**
  - Hardware specs (CPU, Memory, Storage)
  - Available resources (compute points, free memory/storage)
  - GPU badges and tags (NVMe, High Bandwidth)
  - Real-time pricing display
  - Color-coded uptime indicators
  - Clickable cards for detailed view
- **Node Detail Modal:**
  - Complete hardware inventory
  - Availability metrics
  - Reputation statistics (uptime, VMs hosted, completions)
  - Pricing and location info
  - Tags and capabilities
- **Integration:**
  - Seamless connection to existing authentication
  - Uses established API patterns
  - Proper error handling and loading states

**Status:** ✅ **Production-ready** - Both backend and frontend complete

**Example Usage:**
```bash
# Backend API
curl 'http://142.234.200.108:5050/api/marketplace/nodes?requiresGpu=true&tags=nvidia&minUptimePercent=95'

# Frontend: Navigate to Nodes → Use filters → Click any card for details
```

---

🎯 **Node Reputation Tracking System (2026-01-30)**

**Core Innovation:** Real-time failure tracking integrated with existing health monitoring

**Implementation Details:**
- **Failed Heartbeat Tracking by Day:**
  - Added `FailedHeartbeatsByDay` dictionary to Node model (date string → count)
  - Stores failed heartbeats per day in format `{"2026-01-30": 245}`
  - Auto-cleanup removes data older than 30 days (rolling window)
  
- **Real-Time Detection (Every 30 seconds):**
  - Integrated with existing `NodeHealthMonitorService`
  - Leverages proven "marked as offline" mechanism (2-minute timeout)
  - Records failures every 30 seconds for all offline nodes
  - `LastFailedHeartbeatCheckAt` prevents double-counting
  
- **Accurate Uptime Calculation:**
  - Expected heartbeats: Total seconds / 15 (heartbeat interval)
  - Failed heartbeats: Sum of all values in last 30 days
  - Uptime %: (Expected - Failed) / Expected × 100
  - Historical accuracy preserved (no "amnesia" after recovery)
  
- **Services Created:**
  - `NodeReputationService`: Core reputation logic
  - `NodeReputationMaintenanceService`: Hourly cleanup and recalculation
  
- **Integration Points:**
  - `NodeService.CheckNodeHealthAsync()`: Records failures for offline nodes
  - `NodeService.ProcessHeartbeatAsync()`: Updates uptime when online
  - `VmService.CreateVmAsync()`: Increments `TotalVmsHosted`
  - `VmService.DeleteVmAsync()`: Increments `SuccessfulVmCompletions`

**Metrics Tracked:**
1. **Uptime Percentage** - 30-day rolling window, precise to 15-second intervals
2. **Total VMs Hosted** - Lifetime counter, increments on VM assignment
3. **Successful Completions** - Tracks cleanly terminated VMs

**Key Benefits:**
- ✅ Uses existing health check infrastructure (no new monitoring needed)
- ✅ Real-time accuracy (30-second detection vs hourly)
- ✅ Historical data preserved (doesn't reset to 100% after recovery)
- ✅ Minimal storage overhead (~300 bytes per node for 30 days)
- ✅ Automatic cleanup (maintains 30-day window)
- ✅ No double-counting (timestamp-based prevention)

**Status:** ✅ **Production-ready** - Fully integrated and tested

**Documentation:**
- `UPTIME_TRACKING_EXPLAINED.md` - Complete system explanation
- `REPUTATION_TRACKING_IMPLEMENTATION.md` - Implementation guide

---

### Strategic Decision: Optional Premium Staking (2026-01-30)

**Decision:** Defer mandatory staking, implement **optional premium tier** in Phase 4

**Rationale:**
- ✅ **Preserves core principle:** "Universal participation" - anyone can run a node
- ✅ **Growth focus:** Phase 1-3 prioritize adoption over gatekeeping
- ✅ **Data-driven:** Need 3+ months of marketplace data before adding premium features
- ✅ **Reputation first:** Existing uptime/reliability system handles quality organically
- ✅ **Token timing:** XDE token not yet launched
- ✅ **Problem validation:** No evidence of spam/quality issues requiring barriers

**Future Implementation (Phase 4, Months 6-9):**
- Free tier remains forever available (no participation barrier)
- Premium tier requires earned reputation PLUS optional XDE stake
- Benefits: Featured placement, custom branding, priority support
- Stake is lockable security deposit, not slashable punishment

**Key Insight:** Optional staking creates differentiation for serious operators without contradicting the "permissionless participation" mission. Similar to GitHub's free vs. premium tiers.

---

## Strategic Roadmap: Phase-by-Phase Goals

### 🎯 Prioritization Framework
**Impact Scoring:**
- **Network Effects (3x multiplier):** Features that grow more valuable as more users adopt
- **User Acquisition:** Lowers barrier to entry
- **Retention:** Makes platform stickier
- **Differentiation:** Unique vs. competitors

**Effort Scoring:** Low (1-2 weeks) | Medium (3-4 weeks) | High (5-8 weeks)

---

### Phase 1: Discovery & Marketplace Foundation (Weeks 1-6)

**Goal:** Make nodes discoverable + enable one-click deployments

#### ✅ Priority 1.1: Node Marketplace (COMPLETE)
- **Impact:** 🔥 CRITICAL - Unlocks all other features
- **Effort:** 🟢 LOW (1 week backend + 1 day frontend)
- **Status:** ✅ DONE (2026-01-30)
- **Deliverables:**
  - ✅ Backend API with search/filtering
  - ✅ Frontend UI with rich marketplace experience
  - ✅ Featured nodes discovery
  - ✅ Node detail views
  - ✅ Multi-criteria filtering (tags, region, GPU, price, uptime)

#### Priority 1.2: VM Template Marketplace ⭐⭐⭐⭐⭐
- **Impact:** 🔥 CRITICAL - Drives network effects
- **Effort:** 🟡 MEDIUM (2-3 weeks)
- **Status:** 🔜 NEXT

**What:** Users create, share, and deploy VM templates
- Template model with cloud-init scripts
- Search/browse templates by category (AI, Privacy, Gaming)
- One-click deployments
- Seed 10 featured templates:
  - Nextcloud Personal Cloud
  - Whisper AI (speech-to-text)
  - Stable Diffusion
  - VPN/Tor relay
  - Jellyfin media server
  - GitLab self-hosted
  - Mastodon instance
  - Minecraft server
  - Privacy-focused web browser
  - AI chatbot (Ollama)

**Network Effect:** More templates → more users → more templates → ...

#### Priority 1.3: Basic Reputation System ⭐⭐⭐⭐
- **Impact:** 🚀 HIGH - Builds trust
- **Effort:** 🟢 LOW (1 week)
- **Status:** Planned

**What:** Uptime tracking + user reviews
- Extend existing uptime tracking (already 70% built)
- Prompt users to review nodes after VM termination
- Trust badges (99.9% uptime, 100+ VMs hosted)
- Node operator dashboard showing earnings/reputation

---

### Phase 2: User Engagement & Retention (Weeks 7-12)

#### Priority 2.1: Node Operator Dashboard ⭐⭐⭐⭐
- **Impact:** 🚀 HIGH - Attracts node operators
- **Effort:** 🟡 MEDIUM (3 weeks)

**What:** Frontend dashboard for operators
- Total earned (lifetime + pending payout)
- VMs hosted (current + historical)
- Uptime percentage (30-day rolling)
- Relay earnings breakdown
- Profile management (tags, description, pricing)

#### Priority 2.2: Targeted Node Selection ⭐⭐⭐
- **Impact:** 🎯 MEDIUM - Power user feature
- **Effort:** 🟢 LOW (1 week)

**What:** Let users choose specific nodes for VMs
- Already 50% implemented (code has `targetNodeId` parameter)
- Just need to expose in API/frontend
- Use case: "I trust Alice's GPU node, always use it"

#### Priority 2.3: User Reviews After VM Termination ⭐⭐⭐
- **Impact:** 🎯 MEDIUM - Community trust
- **Effort:** 🟡 MEDIUM (2 weeks)

**What:** Prompt for feedback when deleting VMs
- Rating (1-5 stars)
- Comment (optional)
- Aggregate ratings per node
- Display in marketplace

---

### Phase 3: Collaboration & Advanced Features (Weeks 13-20)

#### Priority 3.1: Shared VMs (Multi-Wallet Access) ⭐⭐⭐
- **Impact:** 🎯 MEDIUM - Enables teams
- **Effort:** 🟡 MEDIUM (3 weeks)

**What:** Multiple users can access same VM
- Add `authorizedWallets` list to VM model
- Owner can add/remove collaborators
- Use case: Team development environments

#### Priority 3.2: Infrastructure Templates (Multi-VM) ⭐⭐
- **Impact:** 🔮 FUTURE - Advanced use cases
- **Effort:** 🔴 HIGH (5-6 weeks)

**What:** Deploy entire application stacks
- Example: "E-commerce Stack" = web + database + Redis + load balancer
- Template defines VM relationships and networking
- One-click deployment of 5+ interconnected VMs

#### Priority 3.3: Live Network Visualization ⭐⭐
- **Impact:** 🎨 NICE-TO-HAVE - Marketing value
- **Effort:** 🔴 HIGH (4 weeks)

**What:** Interactive globe/map showing network
- Nodes, VMs, relay connections visualized
- Real-time updates
- Public-facing for marketing

---

### Quick Wins (Implementable TODAY)

#### 1. Enable Node Descriptions & Tags ✅ DONE
Already implemented via marketplace backend.

#### 2. Expose Uptime in API ✅ DONE
`Node.UptimePercentage` already exists and is returned in marketplace API.

#### 3. Create 10 Featured Templates
**Action:** Write cloud-init scripts for top 10 templates (no code needed, just YAML configs)

---

### Phase 4: Monetization & Premium Features (Months 6-9)

**Pre-requisites:**
- 50+ active nodes with proven reputation data
- 3+ months of marketplace operation
- XDE token launched and liquid
- Evidence of quality/spam problems (if applicable)

#### Priority 4.1: Optional Premium Node Staking ⭐⭐⭐
- **Impact:** 🎯 MEDIUM - Differentiation for serious operators
- **Effort:** 🔴 HIGH (4-6 weeks, includes smart contracts)

**What:** **Optional** XDE staking for premium node differentiation
- **Free Tier (Always Available):**
  - Basic marketplace listing
  - All core functionality
  - Merit-based verification badges (earned via uptime/reputation)
  
- **Premium Tier (Optional Staking):**
  - Requires earned Tier 2 status (95%+ uptime, 10+ completions)
  - XDE stake amount TBD based on market conditions
  - Benefits:
    - Premium badge & featured placement
    - Priority in search results
    - Custom marketplace page (branding, extended description)
    - Early access to new features
    - Enhanced support priority
  - Stake is **lockable, not slashable** (security deposit model)
  
**Key Principles:**
- ✅ Preserves "universal participation" - free tier always available
- ✅ Staking is **optional differentiation**, not requirement
- ✅ Must earn reputation first (can't buy quality)
- ✅ Creates natural premium tier without gatekeeping
- ❌ Never makes staking mandatory for basic participation

**Decision Criteria Before Implementation:**
```python
if (
    active_nodes > 50 and
    marketplace_age_months > 3 and
    reputation_system_proven and
    xde_token_launched and
    community_requests_premium_tier
):
    implement_optional_staking()
else:
    continue_free_model()
```

#### Priority 4.2: Advanced Analytics Dashboard ⭐⭐
- **Impact:** 🎯 MEDIUM - Professional operator tools
- **Effort:** 🟡 MEDIUM (2-3 weeks)

**What:** Deep metrics for premium node operators
- Historical uptime trends
- Earnings projections
- Competitive benchmarking
- Resource utilization analytics

---

## What NOT to Do (Anti-Priorities)

Based on strategic analysis, these should be **deferred or rejected**:

❌ **Custom VM Images Upload** - Security risk, complex, low ROI  
❌ **Mandatory Staking System** - Contradicts "universal participation" principle  
❌ **Live Migration** - Technically hard, low user demand  
❌ **Team Workspaces** - Niche until user base grows  
❌ **DHT-based Discovery** - Centralized marketplace works for MVP, can add DHT layer later  

**Rationale:** Focus on **creation and community** features that drive network effects.

---

## Success Metrics

### Phase 1 (6 weeks) - Marketplace Foundation
- ✅ Node marketplace complete (backend + frontend)
- ✅ Featured nodes discovery implemented
- ✅ Multi-criteria search and filtering operational
- 🎯 50+ templates in marketplace
- 🎯 10+ user-created templates
- 🎯 75% of new VMs deployed from templates

### Phase 2 (12 weeks) - Engagement
- 🎯 50% of VMs use templates (vs. manual configuration)
- 🎯 75% of nodes have operator-set pricing
- 🎯 200+ reviews submitted
- 🎯 50+ active node operators

### Phase 3 (20 weeks) - Advanced Features
- 🎯 20% of VMs are shared across multiple users
- 🎯 100+ infrastructure templates (multi-VM setups)
- 🎯 10,000+ total VMs deployed

---

## Key Learnings & Principles

### Architectural Principles
1. **Security First:** Wallet-based auth, encrypted communications, minimal attack surface
2. **KISS (Keep It Simple):** Industry-standard solutions over custom implementations
3. **Deterministic State:** SHA256(machineId + walletAddress) for stable node IDs
4. **Authoritative Source:** Orchestrator maintains truth, nodes report current state
5. **Graceful Degradation:** System continues working even if components fail

### Major Realizations
- **Simplicity > Complexity:** Wallet-encrypted passwords beat complex ephemeral SSH keys
- **Focus on Unique Value:** Censorship resistance matters more than distributed storage pooling
- **Reusable Components:** Relay infrastructure valuable as standalone product
- **Mobile Integration:** Hybrid architecture (lightweight tasks for mobile, full VMs for servers)
- **Network Effects First:** Features that enable sharing/discovery grow the platform fastest

### Performance Insights
- **CPU Quota Critical:** Incorrect calculations caused 18-min boot times (vs. 2-min optimal)
- **Proper Benchmarking:** sysbench provides reliable CPU performance normalization
- **Resource Accounting:** Real-time tracking prevents over-scheduling

---

## Approach & Patterns

### Development Workflow
1. Check GitHub/documentation before making changes
2. Understand root causes, not just symptoms
3. Apply evidence-based recommendations
4. Maintain comprehensive documentation
5. Evolutionary changes over revolutionary restructuring

### Technical Patterns
- **Dependency Injection:** Throughout .NET services
- **Async/Await:** Proper async patterns everywhere
- **Separation of Concerns:** Clean service boundaries
- **Defensive Programming:** Comprehensive error handling, retry mechanisms
- **Feature Flags:** Gradual rollouts for major changes

### Deployment Methodology
- **Automation:** install.sh scripts with backup/rollback
- **Systemd Hardening:** Proper service configuration
- **Secret Management:** Environment files, no hardcoded credentials
- **Comprehensive Logging:** Structured logging for troubleshooting
- **Production-Grade:** Even in dev, maintain production standards

---

## Tools & Resources

### Backend Stack
- **.NET 8 / C#** - Orchestrator and Node Agent services
- **MongoDB Atlas** - Orchestrator persistence
- **SQLite** - Local node state
- **libvirt/KVM** - Virtualization
- **WireGuard** - Overlay networking
- **Caddy** - Reverse proxy with auto-HTTPS

### Frontend Stack
- **React / TypeScript** - UI
- **Vite** - Build tool
- **WalletConnect / Reown AppKit** - Wallet authentication
- **Nethereum** - Ethereum signature verification

### Infrastructure
- **Ubuntu Linux** - OS for all servers
- **Cloudflare** - DNS management
- **Polygon** - Blockchain for payments (USDC)
- **Let's Encrypt** - SSL certificates

### Development Tools
- **Visual Studio** - .NET development
- **GitHub Actions** - CI/CD
- **sysbench** - CPU benchmarking
- **MongoDB Compass** - Database GUI

---

## On the Horizon

### Near-Term (Next 3 Months)
- Complete VM Template Marketplace (Goal 2)
- Implement reputation system (Goal 3)
- Build node operator dashboard
- Deploy 10 featured templates
- Achieve 50+ user-created templates

### Mid-Term (6-12 Months)
- **Mobile Integration:** Two-tier architecture
  - **Mobile Tier:** WebAssembly tasks, ML inference, distributed storage
  - **Server Tier:** Full VM hosting (existing)
- **Smart Contract Coordination:** Move toward true decentralization
- **DHT VMs:** Decentralized state coordination
- **Storage VMs:** Distributed persistence layer

### Long-Term Vision
- **Licensing Opportunities:** DeCloud Relay SDK as standalone product
- **Geographic Expansion:** Compliance frameworks for different jurisdictions
- **AI-Specific Optimizations:** GPU scheduling, model caching, inference templates
- **Blockchain Migration:** From centralized orchestrator to smart contract governance??

### Strategic Differentiators
1. **Full VMs** (not just containers) → More powerful, more use cases
2. **Censorship Resistance** → Unique market position
3. **CGNAT Support** → 60-80% larger addressable market
4. **Community Templates** → Network effects drive growth
5. **Relay Economics** → Symbiotic node relationships

---

## Current Development Focus (2026-01-30)

**Just Completed:**
- ✅ Node Marketplace (Goal 1.1) - **COMPLETE**
  - Backend: REST API with search/filtering
  - Frontend: Rich marketplace UI with cards, modals, filters
  - Featured nodes display (top 10 by uptime/capacity)
  - Multi-criteria search (tags, region, GPU, price, uptime)
  - Node detail views with full hardware specs
  - Operator profile management

**Ready for Next:**
- 🎯 VM Template Marketplace (Priority 1.2) - **NEXT FOCUS**
  - Template creation/sharing system
  - Cloud-init script management
  - One-click deployments
  - Seed 10 featured templates (Nextcloud, Stable Diffusion, Whisper AI, etc.)
  - Template search/browse UI (similar to node marketplace)

**Blockers:** None

**Next Milestone:** 50 templates in marketplace (target: 30 days)

---

## Project Status Summary

**Platform Maturity:** 75% Production-Ready (↑ from 70%)

**Infrastructure Complete:**
- ✅ Self-organizing relay architecture
- ✅ Sophisticated scheduling (quality tiers, compute points)
- ✅ Economic foundation (USDC payments, billing)
- ✅ Security (wallet auth, attestation, SSH certs)
- ✅ Monitoring (heartbeats, metrics, events)

**Critical Gaps (Blocking Growth):**
- ✅ Node marketplace complete (Goal 1.1)
- ❌ No VM templates yet (starting Goal 1.2)
- ❌ No reputation system (Goal 1.3 - quick win)
- ❌ No collaboration features (Phase 3)

**Strategic Position:**
- **Unique Value Prop:** Censorship-resistant compute with full VMs
- **Market Opportunity:** Privacy-sensitive AI, unrestricted hosting
- **Competitive Edge:** CGNAT support via relay innovation
- **Growth Path:** Network effects through template marketplace

**Recommendation:** Continue Phase 1 (Marketplace Foundation) → Goal 2 (VM Templates) is highest priority for driving network effects.

---

*Document maintained by BMA with AI assistance. Updated as strategic priorities evolve.*
