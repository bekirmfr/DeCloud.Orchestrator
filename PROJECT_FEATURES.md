# DeCloud Platform Features

**Last Updated:** 2026-02-15
**Purpose:** Technical documentation of all major platform features and innovations

---

## Table of Contents

1. [Core Architecture](#core-architecture)
2. [Relay Infrastructure](#relay-infrastructure)
3. [VM Proxy System](#vm-proxy-system)
4. [Authentication & Security](#authentication--security)
5. [Billing & Economics](#billing--economics)
6. [Marketplace & Discovery](#marketplace--discovery)
7. [Resource Management](#resource-management)
8. [Monitoring & Health](#monitoring--health)
9. [Per-Service VM Readiness Tracking](#per-service-vm-readiness-tracking)
10. [DHT Infrastructure](#dht-infrastructure)
11. [Future Features](#future-features)

---

## Core Architecture

### Orchestrator-Node Model

DeCloud uses a coordinator-worker architecture:

```
User → Orchestrator (Central Brain) → Node Agents (Distributed Workers)
```

**Orchestrator Responsibilities:**
- VM scheduling and lifecycle management
- Node registration and health monitoring
- Resource allocation using compute points
- Billing and payment processing (USDC on Polygon)
- Relay infrastructure coordination
- Marketplace and discovery APIs

**Node Agent Responsibilities:**
- KVM/libvirt virtualization
- Resource discovery (CPU, memory, GPU, storage)
- VM provisioning and management
- WireGuard networking
- Local proxy for VM ingress traffic

**Communication:**
- Heartbeat system (15-second intervals)
- REST APIs for VM operations
- WebSocket support for real-time features
- WireGuard tunnels for secure overlay networking

---

## Relay Infrastructure

### Critical Innovation: CGNAT Bypass via Automated WireGuard Tunnels

**The Problem:**  
Approximately 60-80% of internet users (mobile networks, residential ISPs) are behind **CGNAT (Carrier-Grade NAT)**, meaning they do not have a unique public IP address. Without special handling, these nodes cannot accept inbound connections, making them unable to host VMs that users need to access.

**The Solution:**  
DeCloud's relay infrastructure automatically creates secure tunnels from public-IP nodes to CGNAT nodes, enabling universal participation.

### How It Works

#### 1. Node Registration & Detection

When a node registers with the Orchestrator:

```csharp
// NodeService.cs - Registration flow
public async Task<Node> RegisterNodeAsync(NodeRegistrationRequest request)
{
    // Detect if node is behind CGNAT
    var isPublicIp = await _networkAnalyzer.IsPublicIpAsync(request.PublicIp);
    var isCgnat = !isPublicIp || request.ReportedCgnat;
    
    node.IsBehindCgnat = isCgnat;
    node.RequiresRelay = isCgnat;
    
    // If CGNAT detected, trigger relay assignment
    if (isCgnat)
    {
        await _relayService.AssignRelayAsync(node.Id);
    }
}
```

**Detection Logic:**
- Check if reported IP matches actual source IP
- Validate IP is not in private ranges (10.x, 192.168.x, 172.16.x)
- Allow manual CGNAT flag for complex network setups
- Track connectivity status in node metadata

#### 2. Relay VM Deployment

The Orchestrator automatically deploys a lightweight **Relay VM** on a node with a static public IP:

```csharp
// RelayService.cs - Relay assignment
public async Task AssignRelayAsync(string cgnatNodeId)
{
    // Find suitable relay node (public IP, sufficient resources)
    var relayNode = await FindBestRelayNodeAsync();
    
    // Deploy lightweight relay VM
    var relayVm = await _vmService.CreateVmAsync(
        systemUserId: "relay-system",
        request: new CreateVmRequest
        {
            Name = $"relay-{cgnatNodeId}",
            VmType = VmType.Relay,
            Memory = 512,  // 512MB is sufficient
            Cpu = 1,
            Storage = 5,   // 5GB for logs
            Template = "relay-wireguard"
        },
        targetNodeId: relayNode.Id
    );
    
    // Store relay relationship
    await _relayRepository.CreateRelayAsync(new Relay
    {
        RelayNodeId = relayNode.Id,
        RelayVmId = relayVm.Id,
        CgnatNodeId = cgnatNodeId,
        Status = RelayStatus.Provisioning
    });
}
```

**Relay Node Selection Criteria:**
- Must have public static IP
- Must be online and healthy
- Sufficient available resources
- Geographic proximity (future: latency-based selection)
- Load balancing across relay providers

#### 3. WireGuard Tunnel Establishment

Once the relay VM is deployed, it establishes a secure WireGuard tunnel to the CGNAT node:

**Relay VM Configuration (`relay-wireguard` template):**
```yaml
#cloud-config
packages:
  - wireguard
  - iptables
  - nginx

write_files:
  - path: /etc/wireguard/wg0.conf
    content: |
      [Interface]
      PrivateKey = ${RELAY_PRIVATE_KEY}
      Address = 10.200.0.1/24
      ListenPort = 51820
      
      # NAT rules for forwarding traffic
      PostUp = iptables -A FORWARD -i wg0 -j ACCEPT
      PostUp = iptables -A FORWARD -o wg0 -j ACCEPT
      PostUp = iptables -t nat -A PREROUTING -p tcp --dport 2222 -j DNAT --to-destination 10.200.0.2:22
      PostUp = iptables -t nat -A POSTROUTING -o eth0 -j MASQUERADE
      
      PostDown = iptables -D FORWARD -i wg0 -j ACCEPT
      PostDown = iptables -D FORWARD -o wg0 -j ACCEPT
      PostDown = iptables -t nat -D PREROUTING -p tcp --dport 2222 -j DNAT --to-destination 10.200.0.2:22
      PostDown = iptables -t nat -D POSTROUTING -o eth0 -j MASQUERADE
      
      [Peer]
      PublicKey = ${CGNAT_NODE_PUBLIC_KEY}
      AllowedIPs = 10.200.0.2/32
      PersistentKeepalive = 25

runcmd:
  - systemctl enable wg-quick@wg0
  - systemctl start wg-quick@wg0
```

**CGNAT Node Configuration:**
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

**Key Configuration Details:**
- **Relay Side:** 10.200.0.1 (tunnel endpoint)
- **CGNAT Side:** 10.200.0.2 (tunnel endpoint)
- **PersistentKeepalive:** 25 seconds prevents NAT timeout
- **DNAT Rules:** Port mapping from relay public IP to CGNAT node VMs
- **MASQUERADE:** Enables outbound traffic from CGNAT node

#### 4. Traffic Routing & Port Mapping

When a user accesses a VM on a CGNAT node, traffic flows through the relay:

```
User Browser (HTTPS)
  ↓
  → Relay Node Public IP (e.g., 203.0.113.50:443)
  ↓
  → iptables DNAT rule intercepts
  ↓
  → WireGuard tunnel (10.200.0.1 → 10.200.0.2)
  ↓
  → CGNAT Node (receives as local traffic)
  ↓
  → VM on CGNAT Node (192.168.122.x:port)
```

**Dynamic Port Allocation:**
```csharp
// RelayService.cs - Port mapping
public async Task AllocatePortForVmAsync(string vmId, int vmPort)
{
    var vm = await _vmRepository.GetByIdAsync(vmId);
    var relay = await _relayRepository.GetByCgnatNodeIdAsync(vm.AssignedNodeId);
    
    // Find available port on relay node
    var relayPort = await FindAvailablePortAsync(relay.RelayNodeId);
    
    // Configure iptables DNAT rule on relay VM
    await ExecuteOnRelayVmAsync(relay.RelayVmId, 
        $"iptables -t nat -A PREROUTING -p tcp --dport {relayPort} " +
        $"-j DNAT --to-destination 10.200.0.2:{vmPort}");
    
    // Store mapping
    await _portMappingRepository.CreateAsync(new PortMapping
    {
        RelayId = relay.Id,
        VmId = vmId,
        RelayPort = relayPort,
        VmPort = vmPort,
        Protocol = "tcp"
    });
    
    // Return public access URL
    return $"https://{relay.RelayNodePublicIp}:{relayPort}";
}
```

#### 5. Health Monitoring

The `RelayHealthMonitor` service continuously monitors relay status:

```csharp
// RelayHealthMonitorService.cs
public class RelayHealthMonitorService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var activeRelays = await _relayRepository.GetActiveRelaysAsync();
            
            foreach (var relay in activeRelays)
            {
                // Check WireGuard tunnel status
                var tunnelUp = await CheckWireGuardTunnelAsync(relay);
                
                // Check port forwarding rules
                var portsValid = await ValidatePortMappingsAsync(relay);
                
                // Update relay status
                relay.Status = (tunnelUp && portsValid) 
                    ? RelayStatus.Active 
                    : RelayStatus.Degraded;
                
                relay.LastHealthCheckAt = DateTime.UtcNow;
                
                // Trigger repair if needed
                if (!tunnelUp || !portsValid)
                {
                    await _relayRepairService.RepairRelayAsync(relay.Id);
                }
            }
            
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
```

**Monitored Metrics:**
- WireGuard tunnel connectivity (peer handshake)
- Port mapping rule validity
- Bandwidth utilization
- Latency between relay and CGNAT node
- Resource usage on relay VM

### Symbiotic Economics

The relay system creates a win-win-win scenario:

**For Relay Node Operators:**
- **Passive Income:** Earn USDC for providing connectivity and bandwidth
- **Low Resource Cost:** Relay VMs use minimal resources (512MB RAM, 1 CPU)
- **Predictable Earnings:** Percentage of all VMs hosted on connected CGNAT nodes
- **Example:** Relay node connecting 5 CGNAT nodes earning 20% commission = steady revenue stream

**For CGNAT Node Operators:**
- **Market Access:** Can host VMs and earn income despite CGNAT
- **No Infrastructure Changes:** No need to change ISP or get static IP
- **80% Share:** Keep 80% of VM revenue (20% to relay)
- **Example:** Home server behind mobile network can now earn from GPU hosting

**For the Platform:**
- **Massive Market Expansion:** 60-80% more potential nodes
- **Network Effects:** More nodes → more capacity → more users → more value
- **Competitive Advantage:** Akash and most competitors don't support CGNAT nodes
- **Geographic Diversity:** Mobile and residential nodes provide global coverage

### Technical Components

#### RelayService
**Location:** `src/Orchestrator/Services/RelayService.cs`

**Responsibilities:**
- Relay node selection and assignment
- Port allocation and mapping
- Configuration generation (WireGuard keys, iptables rules)
- Relay lifecycle management

**Key Methods:**
- `AssignRelayAsync(cgnatNodeId)` - Find and assign relay node
- `AllocatePortForVmAsync(vmId, vmPort)` - Create port mapping
- `UpdateRelayConfigurationAsync(relayId)` - Reconfigure relay
- `RemoveRelayAsync(relayId)` - Clean up relay when CGNAT node leaves

#### RelayHealthMonitor
**Location:** `src/Orchestrator/Services/RelayHealthMonitorService.cs`

**Responsibilities:**
- Monitor WireGuard tunnel status
- Validate port forwarding rules
- Detect and repair relay failures
- Track performance metrics

**Monitoring Intervals:**
- Tunnel status: Every 60 seconds
- Port mappings: Every 5 minutes
- Bandwidth metrics: Every 30 seconds

#### RelayController
**Location:** `src/Orchestrator/Controllers/RelayController.cs`

**API Endpoints:**
- `GET /api/relays` - List all active relays
- `GET /api/relays/{id}` - Get relay details
- `POST /api/relays/{id}/repair` - Manually trigger relay repair
- `GET /api/relays/stats` - Platform-wide relay statistics

### Strategic Value

#### Standalone Product Opportunity: "DeCloud Relay SDK"

The relay infrastructure is architected to be **platform-agnostic** and could be packaged as a standalone product:

**Potential Use Cases:**
- **Akash Network:** Enable CGNAT nodes to participate as providers
- **Filecoin:** Allow residential nodes to provide storage
- **Blockchain Nodes:** Run validators from home connections
- **Gaming Servers:** Host game servers behind CGNAT
- **IoT Networks:** Connect edge devices without public IPs

**Licensing Model:**
- Open-source core (build community)
- Enterprise support subscriptions
- SaaS management dashboard
- Custom integration consulting

**Estimated Market:**
- 100+ decentralized platforms need CGNAT solutions
- $50K-$250K licensing per platform
- Total addressable: $5M-$25M

---

## Smart Port Allocation (Direct Access)

### Overview

The Smart Port Allocation system enables external TCP/UDP access to VM services through automated port forwarding. For CGNAT nodes, it implements a 3-hop forwarding architecture through relay nodes, while public nodes use direct forwarding.

**Key Features:**
- Automatic relay forwarding for CGNAT nodes
- Direct forwarding for public nodes (bypasses relay)
- Database-backed persistence with reconciliation
- Protocol support: TCP, UDP, or both
- Port range: 40000-65535 (25,536 assignable ports)

### 3-Hop Forwarding Architecture

For VMs on CGNAT nodes, traffic flows through three hops:

```
External Client:40000
  ↓ (Internet)
Relay Node iptables DNAT
  PublicPort:40000 → TunnelIP:40000
  ↓ (WireGuard tunnel)
CGNAT Node iptables DNAT
  PublicPort:40000 → VM:22
  ↓ (libvirt bridge)
VM SSH Server:22
```

**Direct forwarding** (public nodes):
```
External Client:40000
  ↓ (Internet)
Node iptables DNAT
  PublicPort:40000 → VM:22
  ↓ (libvirt bridge)
VM SSH Server:22
```

### Port Allocation Flow

1. **User requests port** - VM service port (e.g., SSH 22) → public port (e.g., 40000)
2. **Orchestrator validates:**
   - Port in valid range (40000-65535)
   - Port not already allocated
   - VM exists and is running
3. **Orchestrator identifies path:**
   - **CGNAT node:** Finds assigned relay node → sends commands to both nodes
   - **Public node:** Sends command directly to node
4. **Nodes create iptables rules:**
   - DNAT: `PublicPort → TargetIP:TargetPort`
   - FORWARD: Accept forwarded packets
5. **Database persistence:**
   - Both nodes store mapping in SQLite
   - Survives service restarts

### Database Schema

```sql
CREATE TABLE PortMappings (
    Id TEXT PRIMARY KEY,
    VmId TEXT NOT NULL,
    VmPrivateIp TEXT NOT NULL,  -- For CGNAT: VM IP, For Relay: Tunnel IP
    VmPort INTEGER NOT NULL,     -- For CGNAT: VM port, For Relay: 0
    PublicPort INTEGER NOT NULL UNIQUE,
    Protocol INTEGER NOT NULL,   -- 0=TCP, 1=UDP, 2=Both
    Label TEXT,
    CreatedAt TEXT NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1
);
```

**Special marker for relay mappings:** `VmPort = 0`

This marker distinguishes relay forwarding rules from direct VM forwarding rules, enabling correct matching and deletion logic.

### Port Deletion - Critical Bug Fixes (2026-02-08)

Port deletion for relay nodes required fixing **5 interconnected bugs**:

#### Bug #1: Deadlock in CreateForwardingAsync
**Symptom:** Port allocation hung indefinitely  
**Root Cause:** Method held lock while calling `GetRelayVmIpAsync`, which also tried to acquire same lock  
**Fix:** Refactored to `CreateForwardingInternalAsync` (lock-free internal method) - [commit 73d5ee7]  
**Files:** `PortForwardingManager.cs`

#### Bug #2: VmPort Mismatch in Orchestrator
**Symptom:** Relay `RemovePort` payload had `vmPort=22` instead of `0`  
**Root Cause:** Orchestrator used original VM port instead of relay's special `VmPort=0`  
**Fix:** Detect relay forwarding and send `VmPort=0` in RemovePort command - [commit 9a76f41]  
**Files:** `DirectAccessService.cs`

#### Bug #3: IP Address Mismatch
**Symptom:** iptables deletion used VM IP (192.168.x.x) instead of tunnel IP (10.20.x.x)  
**Root Cause:** Relay forwarding uses tunnel IP, not VM IP  
**Fix:** Resolve relay VM's tunnel IP before calling RemoveForwardingAsync - [commit 2add8d8]  
**Files:** `CommandProcessorService.cs`

#### Bug #4: PublicPort Matching
**Symptom:** Deleted wrong relay mapping (e.g., SSH instead of Redis)  
**Root Cause:** All relay mappings have `VmPort=0`, so matching by VmPort returned first mapping  
**Fix:** Extract `PublicPort` from payload and match by `PublicPort` when `VmPort=0` - [commits b8d505f, 3b46fed]  
**Files:** `DirectAccessService.cs`, `CommandProcessorService.cs`

Example:
```csharp
// Orchestrator: Send PublicPort in relay RemovePort
payload["publicPort"] = mapping.PublicPort;

// NodeAgent: Match by PublicPort when VmPort=0
if (vmPort.Value == 0 && publicPort.HasValue)
    mapping = mappings.FirstOrDefault(m => m.PublicPort == publicPort.Value);
else
    mapping = mappings.FirstOrDefault(m => m.VmPort == vmPort.Value);
```

#### Bug #5: Database Deletion
**Symptom:** All relay mappings deleted when removing one port  
**Root Cause:** After correct matching, code called `RemoveAsync(vmId, vmPort=0)` which matched ALL relay mappings  

SQL executed:
```sql
DELETE FROM PortMappings WHERE VmId = '...' AND VmPort = 0  -- Deletes all!
```

**Fix:** Added `RemoveByPublicPortAsync` method, use it for relay mappings - [commit 75934c8]  
**Files:** `PortMappingRepository.cs`, `CommandProcessorService.cs`

```csharp
// Delete by PublicPort for relay mappings
if (vmPort.Value == 0)
    removed = await _portMappingRepository.RemoveByPublicPortAsync(mapping.PublicPort);
else
    removed = await _portMappingRepository.RemoveAsync(vmId, vmPort.Value);
```

### Reconciliation & Self-Healing

**Startup Reconciliation:**
- `PortForwardingReconciliationService` runs on node startup
- Reads all active mappings from database
- Flushes iptables chain
- Recreates all rules from database
- **Database is source of truth** - iptables synced to match

**Self-Healing Scenarios:**

| Scenario | Behavior |
|----------|----------|
| Service restart | ✅ All rules recreated from database |
| iptables flushed | ✅ Rules recreated on next restart |
| Database deleted | ❌ All mappings lost (DB is source of truth) |
| Orphaned iptables rules | ❌ Not detected (acceptable - DB drives state) |

**Design Decision:** One-way reconciliation (DB → iptables) is correct architecture.

### Health Monitoring & Failure Handling

**Node Health Detection:**
- `NodeHealthMonitorService` runs every 30 seconds
- No heartbeat for 2 minutes → node marked `Offline`
- All VMs on offline node marked `Error`
- Billing stops for Error VMs

**Failure Handling Strategy:**

When CGNAT node goes offline:
1. **Immediate:** Node marked `Offline`, VMs marked `Error`, billing stops
2. **User decision:** Wait for recovery OR delete VM
3. **On deletion:** Orchestrator triggers port cleanup on relay nodes
4. **Future:** Auto-delete after grace period (e.g., 24 hours)

**Design rationale:**
- ❌ Don't auto-cleanup immediately → prevents unintended service interruptions
- ✅ Mark as Error + stop billing → fair to users, allows investigation
- ✅ Manual or delayed cleanup → gives users control

### Implementation Files

**Orchestrator:**
- `DirectAccessService.cs` - Port allocation coordinator
- `NodeService.cs` - Health monitoring
- `BackgroundServices.cs` - NodeHealthMonitorService

**NodeAgent:**
- `PortForwardingManager.cs` - iptables management
- `PortMappingRepository.cs` - Database persistence
- `CommandProcessorService.cs` - Command handling
- `PortForwardingReconciliationService.cs` - Startup reconciliation

### Production Status

✅ **All critical bugs fixed** (5 bugs, 6 commits)  
✅ **Database persistence working**  
✅ **Reconciliation on restart**  
✅ **Health monitoring active**  
✅ **End-to-end tested and verified**  

The 3-hop port forwarding system is **production-ready** for CGNAT bypass scenarios.


## VM Proxy System

### Unified HTTP and WebSocket Proxy

The Node Agent includes a sophisticated proxy system that handles all ingress traffic to VMs, automatically detecting and routing both HTTP and WebSocket requests.

**Location:** `src/DeCloud.NodeAgent/Controllers/GenericProxyController.cs`

### Architecture

```
External Request (HTTP/HTTPS/WSS)
  ↓
Caddy (Central Ingress or Local)
  ↓
GenericProxyController (/api/vms/{vmId}/proxy/http/{port}/*)
  ↓
Automatic WebSocket Detection
  ├─ HTTP Request → Standard HTTP Proxy
  └─ WebSocket Upgrade → WebSocket Tunnel
       ↓
VM Service (192.168.122.x:port)
```

### HTTP Proxying

**Standard HTTP/HTTPS requests** are proxied with:

- Header forwarding (Host, User-Agent, Authorization, etc.)
- Query string preservation
- Request body streaming (no buffering to avoid data loss)
- Proper status code and header passthrough
- Timeout handling

**Code Example:**
```csharp
// GenericProxyController.cs - HTTP proxying
public async Task<IActionResult> ProxyHttp(
    string vmId, int port, CancellationToken ct)
{
    // Validate port is allowed
    if (!_allowedPorts.Contains(port))
        return BadRequest("Port not allowed");
    
    // Check if WebSocket upgrade request
    if (IsWebSocketUpgradeRequest())
    {
        await HandleWebSocketTunnel(vmId, vmIp, port, ct);
        return new EmptyResult();
    }
    
    // Standard HTTP proxy
    var targetPath = Request.Path.Value.Replace($"/api/vms/{vmId}/proxy/http/{port}", "");
    var targetUrl = $"http://{vmIp}:{port}{targetPath}{Request.QueryString}";
    
    using var httpClient = _httpClientFactory.CreateClient("VMProxy");
    var request = new HttpRequestMessage(Request.Method, targetUrl);
    
    // Forward headers
    foreach (var header in Request.Headers)
    {
        if (!_skipHeaders.Contains(header.Key))
            request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
    }
    
    // Stream request body
    if (Request.Body != null)
        request.Content = new StreamContent(Request.Body);
    
    // Execute and return response
    var response = await httpClient.SendAsync(request, ct);
    return new ProxyResult(response);
}
```

### WebSocket Proxying (Critical Innovation)

**Problem Solved:**  
Web-based IDEs (code-server, JupyterLab) and real-time applications require WebSocket connections. Previous implementation failed because WebSocket upgrade requests were not properly detected and handled.

**Solution Implemented (2026-02-02):**  
Automatic WebSocket detection with **WebSocket-to-WebSocket bridging**.

#### WebSocket Detection

```csharp
private bool IsWebSocketUpgradeRequest()
{
    var connectionHeader = Request.Headers["Connection"].ToString();
    var upgradeHeader = Request.Headers["Upgrade"].ToString();
    
    return connectionHeader.Contains("Upgrade", StringComparison.OrdinalIgnoreCase) &&
           upgradeHeader.Equals("websocket", StringComparison.OrdinalIgnoreCase);
}
```

**Headers Checked:**
- `Connection: Upgrade`
- `Upgrade: websocket`

#### WebSocket Tunnel Establishment

```csharp
private async Task HandleWebSocketTunnel(
    string vmId, string vmIp, int port, CancellationToken ct)
{
    // Extract actual backend path (remove proxy prefix)
    var proxyPrefix = $"/api/vms/{vmId}/proxy/http/{port}";
    var backendPath = Request.Path.ToString().Replace(proxyPrefix, "");
    
    // Connect to backend WebSocket using ClientWebSocket
    using var backendWs = new ClientWebSocket();
    var backendUrl = $"ws://{vmIp}:{port}{backendPath}{Request.QueryString}";
    
    _logger.LogInformation(
        "Establishing WebSocket tunnel to {Url} for VM {VmId}",
        backendUrl, vmId);
    
    await backendWs.ConnectAsync(new Uri(backendUrl), ct);
    
    // Accept client WebSocket
    var clientWs = await HttpContext.WebSockets.AcceptWebSocketAsync();
    
    _logger.LogInformation("WebSocket tunnel established for VM {VmId}", vmId);
    
    // Bidirectional relay between client and backend
    await Task.WhenAll(
        RelayWebSocketAsync(clientWs, backendWs, "client->backend", ct),
        RelayWebSocketAsync(backendWs, clientWs, "backend->client", ct)
    );
}
```

**Key Implementation Details:**
1. **ClientWebSocket:** Creates proper WebSocket connection to backend (not raw TCP)
2. **Path Extraction:** Removes proxy prefix to get actual backend path
3. **Query String Preservation:** Maintains full URL including parameters
4. **Bidirectional Relay:** Two concurrent tasks relay data in both directions

#### WebSocket Frame Relay

```csharp
private async Task RelayWebSocketAsync(
    WebSocket source, WebSocket destination, 
    string direction, CancellationToken ct)
{
    var buffer = new byte[8192];
    
    try
    {
        while (source.State == WebSocketState.Open &&
               destination.State == WebSocketState.Open)
        {
            var result = await source.ReceiveAsync(
                new ArraySegment<byte>(buffer), ct);
            
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await destination.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    result.CloseStatusDescription, ct);
                break;
            }
            
            await destination.SendAsync(
                new ArraySegment<byte>(buffer, 0, result.Count),
                result.MessageType,
                result.EndOfMessage,
                ct);
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, 
            "WebSocket relay error ({Direction}) for VM {VmId}", 
            direction, vmId);
    }
}
```

**Frame Preservation:**
- **Message Type:** Text, Binary, or Close frames preserved
- **Message Boundaries:** `EndOfMessage` flag maintained
- **No Buffering:** Direct relay for low latency
- **Error Handling:** Graceful connection cleanup

### Traffic Flow Examples

#### HTTP Request Flow
```
User → https://myapp-abc123.vms.stackfi.tech/api/data
  ↓
Caddy → https://node-ip:5100/api/vms/{vmId}/proxy/http/8080/api/data
  ↓
GenericProxyController
  → No WebSocket headers detected
  → HTTP proxy mode
  ↓
VM → http://192.168.122.50:8080/api/data
  ↓
Response flows back through same path
```

#### WebSocket Flow (e.g., code-server)
```
Browser → wss://code-server-xyz.vms.stackfi.tech/stable-xxx/socket
  ↓ (Connection: Upgrade, Upgrade: websocket)
Caddy → https://node-ip:5100/api/vms/{vmId}/proxy/http/8080/stable-xxx/socket
  ↓
GenericProxyController
  → WebSocket upgrade detected
  → Create ClientWebSocket to backend
  → Accept client WebSocket
  → Bridge the two connections
  ↓
VM code-server → ws://192.168.122.50:8080/stable-xxx/socket
  ↓ (Full WebSocket protocol handshake)
Bidirectional WebSocket frames relay
```

### Allowed Ports

```csharp
private static readonly HashSet<int> _allowedPorts = new()
{
    22,    // SSH
    80,    // HTTP
    443,   // HTTPS
    3000,  // Common dev servers
    5000,  // ASP.NET default
    8000,  // Common HTTP
    8080,  // Common HTTP alt
    8443,  // HTTPS alt
    9000   // Various services
};
```

**Security:** Only whitelisted ports are proxied to prevent abuse.

### Performance Considerations

- **No Request Body Buffering:** Streams directly to avoid memory issues
- **Concurrent WebSocket Relay:** Both directions handled in parallel
- **Connection Pooling:** HTTP client factory reuses connections
- **Timeout Management:** 100-second timeout for long-running requests

---

## Authentication & Security

### Wallet-Based Authentication

**No passwords, no KYC, no email.** DeCloud uses Ethereum wallet signatures for authentication.

**Flow:**
1. User connects wallet (MetaMask, WalletConnect, etc.)
2. Frontend requests signature of deterministic message
3. Backend verifies signature recovers wallet address
4. Wallet address is the user ID

**Benefits:**
- **Privacy:** No personal information required
- **Decentralized:** No centralized auth provider
- **Censorship-Resistant:** Cannot be banned by email/phone provider
- **Interoperable:** Works with any Ethereum-compatible wallet

**Code Example:**
```csharp
// AuthController.cs
public async Task<IActionResult> VerifyWallet([FromBody] WalletVerifyRequest request)
{
    var message = $"DeCloud Login: {request.Nonce}";
    var signer = _ethService.RecoverSignerAddress(message, request.Signature);
    
    if (signer.ToLower() != request.WalletAddress.ToLower())
        return Unauthorized("Invalid signature");
    
    var user = await _userRepository.GetOrCreateByWalletAsync(signer);
    var token = _jwtService.GenerateToken(user.Id, signer);
    
    return Ok(new { token, walletAddress = signer });
}
```

### Encrypted VM Access

**SSH Passwords:**  
When creating a VM, the user provides a password which is **encrypted with their wallet's public key**:

```csharp
var encryptedPassword = await _walletCrypto.EncryptAsync(
    plainPassword, userWalletAddress);

vm.EncryptedPassword = encryptedPassword;
```

**Decryption:**  
Only the wallet owner can decrypt the password.

**Security Properties:**
- Orchestrator never stores plaintext passwords
- Node agents receive encrypted passwords (can't read them)
- Users must have wallet access to retrieve passwords
- Lost wallet = lost VM access (user responsibility)

---

## Billing & Economics

### USDC-Based Payments (Polygon Network)

All payments are in **USDC stablecoin** on the **Polygon blockchain** for low fees and fast settlement.

**Pricing Model:**
- **Compute Points:** CPUs benchmarked and normalized to standard units
- **Per-Hour Billing:** `hourlyRate = computePoints * nodePricePerPoint`
- **Quality Tiers:** Guaranteed (1:1), Standard (1.5:1), Balanced (2:1), Burstable (4:1)

**Payment Flow:**
1. User deposits USDC to their account (on-chain transaction)
2. Orchestrator tracks VM usage per hour
3. Billing service calculates charges and deducts from balance
4. Node operators accumulate earnings
5. Payout service transfers earnings to node wallets (weekly batches)

**Relay Revenue Split:**
- **CGNAT Node:** 80% of VM revenue
- **Relay Node:** 20% of VM revenue (passive income)

---

## Marketplace & Discovery

### Node Marketplace

**Features:**
- Search and filter nodes by tags, region, price, GPU, uptime
- Featured nodes (top performers with 95%+ uptime)
- One-click VM deployment to specific nodes
- Real-time availability and pricing
- Node profiles with descriptions and custom tags

**API:** `/api/marketplace/nodes`

### VM Template Marketplace (COMPLETE - Phase 1.2)

**Status:** ✅ Fully implemented with backend API, frontend UI, and 5 seed templates

**Implementation:**
- VmTemplate model with cloud-init scripts, variable substitution, specs, pricing, ratings
- TemplateService: CRUD, search/filter, validation, deployment helpers
- TemplateSeederService: Auto-seeds on startup with semantic version comparison
- MarketplaceController: Full REST API (browse, featured, detail, create, update, delete, publish, deploy)
- Community templates: User-created with draft→publish workflow
- Paid templates: PerDeploy pricing model (85/15 author/platform split via escrow)
- ReviewService: Universal review system with eligibility proofs
- Frontend: marketplace-templates.js, my-templates.js, template-detail.js

**Seed Templates (6):**
- Stable Diffusion WebUI (AI image generation, GPU required) ✅
- PostgreSQL Database (production-ready) ✅
- VS Code Server (code-server, browser IDE) ✅
- Private Browser (Neko/WebRTC streaming) ✅
- Privacy Proxy (Shadowsocks, TCP+UDP) ✅
- Web Proxy Browser (Ultraviolet, private browsing) ✅

**Planned Additional Templates:**
- Nextcloud (personal cloud)
- Whisper AI (speech-to-text)
- Mastodon (social network)
- Minecraft server
- VPN/Tor relay
- Jellyfin (media server)
- AI chatbot (Ollama)

---

## VM Lifecycle Management

### Centralized State Machine (VmLifecycleManager)

**Status:** ✅ Implemented (2026-02-09)
**Location:** `src/Orchestrator/Services/VmLifecycleManager.cs`

All confirmed VM state transitions flow through a single entry point: `VmLifecycleManager.TransitionAsync()`. This replaces the previous pattern where 5 separate code paths changed VM status with inconsistent side effects.

### Design Principles

1. **Single entry point** — Every confirmed status change goes through `TransitionAsync`
2. **Validate first, persist second, effects third** — Invalid transitions rejected before any mutation
3. **Side effects keyed by (from, to) pair** — Different transitions trigger different effects
4. **Individual error isolation** — `SafeExecuteAsync` wraps each side effect; one failure never aborts the chain
5. **Persist-first** — Status saved before effects run; crash mid-effects leaves VM in correct state for reconciliation

### State Transition Map

```
Pending       → Scheduling, Provisioning, Error, Deleting
Scheduling    → Provisioning, Pending, Error, Deleting
Provisioning  → Running, Error, Deleting
Running       → Stopping, Error, Deleting
Stopping      → Stopped, Running, Error, Deleting
Stopped       → Provisioning, Running, Deleting, Error
Deleting      → Deleted, Error
Migrating     → Running, Error, Deleting
Error         → Provisioning, Running, Deleting, Stopped, Error
Deleted       → (terminal — no transitions)
```

Invalid transitions (e.g., `Deleted → Running`) are rejected with a warning log.

### Side Effect Dispatch

| Transition | Handler | Effects |
|---|---|---|
| `Provisioning/Stopped/Error → Running` | `OnVmBecameRunningAsync` | Wait for PrivateIp → Ingress registration → Template port auto-allocation → Template fee settlement |
| `Running → Stopping/Deleting/Error` | `OnVmLeavingRunningAsync` | Ingress cleanup |
| `* → Stopped` | `OnVmStoppedAsync` | Ingress cleanup |
| `* → Deleted` | `OnVmDeletedAsync` | Ingress deletion → DirectAccess port cleanup → Free node resources → Free user quotas → Increment node reputation |

Key behaviors:
- `Error → Running` does **not** re-allocate ports or re-charge template fees
- `Stopped → Deleting` does **not** run ingress cleanup (VM wasn't serving)
- `Running → Running` (same status) is a no-op, preventing duplicate side effects

### PrivateIp Timing Resolution

VM private IPs arrive via heartbeat, often after the command ack sets status to Running. The lifecycle manager handles this with two mechanisms:

1. **Structural fix:** The heartbeat path persists PrivateIp to the datastore **before** calling `TransitionAsync`, so IP is available when side effects fire
2. **Safety net:** `WaitForPrivateIpAsync` polls the datastore every 2 seconds for up to 30 seconds, accommodating the ~15-second IP discovery delay on nodes

If the IP still isn't available after 30 seconds, effects are deferred (not failed). A future reconciliation service or the next heartbeat can retry.

### Transition Context

Every transition carries metadata about what triggered it:

| Trigger | Source | Use Case |
|---|---|---|
| `CommandAck` | NodeService command acknowledgment | Node confirmed a create/stop/delete completed |
| `Heartbeat` | NodeService heartbeat sync | Node reports VM status different from Orchestrator's record |
| `Manual` | VmService / SignalR hub | Admin or API-initiated status change |
| `Timeout` | BackgroundServices stale command cleanup | Command not acknowledged within timeout window |
| `NodeOffline` | NodeService health check | Node missed heartbeats, all its VMs marked Error |
| `CommandFailed` | NodeService command acknowledgment | Node reported command execution failure |

Context is used for structured logging and debugging — every transition log includes the trigger type, source, and relevant IDs.

### Call Sites (5 entry points, 1 destination)

All confirmed transitions route through `IVmLifecycleManager.TransitionAsync()`:

1. **Command acknowledgment** (`NodeService.ProcessCommandAcknowledgmentAsync`) — Node confirms create → Running, stop → Stopped, delete → Deleted, or failure → Error
2. **Heartbeat sync** (`NodeService.SyncVmStateFromHeartbeatAsync`) — Node reports a VM status that differs from the Orchestrator's record
3. **Node offline** (`NodeService.MarkNodeVmsAsErrorAsync`) — Health check detects node went offline, all VMs → Error
4. **Background timeout** (`BackgroundServices.CleanupExpiredCommands`) — Command not acknowledged within timeout → Error
5. **Manual/API** (`VmService.UpdateVmStatusAsync`) — SignalR hub or API-initiated status change

**Note:** "Command issuance" paths (setting `Provisioning` on create, `Stopping` on stop, `Deleting` on delete) intentionally set status directly as optimistic updates before the node confirms. These are not confirmed transitions and do not trigger side effects.

### Implementation Files

| File | Change |
|---|---|
| `Services/VmLifecycleManager.cs` | New — lifecycle manager with interface, context, and all side effect handlers |
| `Program.cs` | DI registration (`AddSingleton<IVmLifecycleManager, VmLifecycleManager>`) |
| `Services/NodeService.cs` | Command ack, heartbeat, and node-offline paths route through lifecycle manager |
| `Services/VmService.cs` | `UpdateVmStatusAsync` delegates to lifecycle manager; removed ~280 lines of dead code |
| `Background/BackgroundServices.cs` | Command timeout path routes through lifecycle manager |

### Previous Issues Fixed

- **Auto port allocation never fired:** `AutoAllocateTemplatePortsAsync` only existed in `VmService.UpdateVmStatusAsync`, which was only called from the SignalR hub. The actual VM lifecycle paths (command ack, heartbeat) set `vm.Status` directly, bypassing all side effects.
- **PrivateIp timing race:** Heartbeat path set status to Running and saved, then set PrivateIp afterward. Side effects requiring IP would fail because IP wasn't persisted yet.
- **Fire-and-forget anti-pattern:** Old code used `_ = Task.Run(async () => ...)` for side effects, which was unreliable and swallowed exceptions silently.
- **Inconsistent cleanup on deletion:** `NodeService.CompleteVmDeletionAsync` freed node resources but not DirectAccess ports. `VmService.CompleteVmDeletionAsync` freed quotas but used different logic. Now unified in `OnVmDeletedAsync`.

---

## Web Proxy Browser Template (Ultraviolet)

### Overview

**Status:** ✅ Production-ready (v3.0.0, 2026-02-09)
**Template slug:** `web-proxy-browser`
**Category:** Privacy & Security

A privacy-focused web proxy that runs entirely within a VM, allowing users to browse the internet through a service-worker-based proxy (Ultraviolet). The template deploys the official [titaniumnetwork-dev/Ultraviolet-App](https://github.com/nickerlass/Ultraviolet-App) behind nginx with HTTP Basic Auth.

### Architecture

```
User Browser
  ↓ (HTTPS via CentralIngress subdomain)
nginx (:8080) — COOP/COEP headers + Basic Auth
  ├─ / → Ultraviolet frontend (proxy_pass :3000)
  ├─ /uv/ → UV service worker assets
  ├─ /epoxy/ → Epoxy transport (SharedArrayBuffer-based)
  ├─ /baremux/ → BareMux shared worker
  └─ /wisp/ → Wisp WebSocket endpoint (proxy_pass :3000, WS upgrade)
         ↓
Ultraviolet-App (Node.js :3000)
  ↓ (Wisp protocol over WebSocket)
Target Website
```

### Critical Fix: Cross-Origin Isolation (v2.0.0 → v3.0.0)

**Problem:** The Ultraviolet proxy loaded the initial HTML page successfully but all sub-resources (CSS, JS, images) failed silently. Console showed `bare-mux: recieved request for port from sw` followed by service worker errors.

**Root Cause:** The **epoxy transport** uses `SharedArrayBuffer` for Wisp communication between the BareMux shared worker and the service worker. Browsers only expose `SharedArrayBuffer` when the page has **Cross-Origin Isolation** headers:
- `Cross-Origin-Opener-Policy: same-origin`
- `Cross-Origin-Embedder-Policy: require-corp`

Without these headers, the initial fetch worked (handled by the service worker directly), but all subsequent sub-resource fetches through the BareMux transport failed because the shared worker couldn't allocate `SharedArrayBuffer`.

**Fix:** Added COOP/COEP headers to the nginx server block and added WebSocket-friendly settings for the wisp endpoint:

```nginx
server {
    listen 8080;
    server_name _;

    # Cross-Origin Isolation headers - REQUIRED for SharedArrayBuffer
    add_header Cross-Origin-Opener-Policy "same-origin";
    add_header Cross-Origin-Embedder-Policy "require-corp";

    auth_basic "Private Browser";
    auth_basic_user_file /etc/nginx/.htpasswd;

    location / { proxy_pass http://127.0.0.1:3000; auth_basic off; }

    location /wisp/ {
        proxy_pass http://127.0.0.1:3000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_buffering off;
        proxy_read_timeout 86400s;
        proxy_send_timeout 86400s;
        auth_basic off;
    }

    location /uv/ { proxy_pass http://127.0.0.1:3000; auth_basic off; }
    location /epoxy/ { proxy_pass http://127.0.0.1:3000; auth_basic off; }
    location /baremux/ { proxy_pass http://127.0.0.1:3000; auth_basic off; }
}
```

### Version History

| Version | Change | Issue |
|---------|--------|-------|
| v1.0.0 | Initial template with custom npm packages | Non-existent `@nicotine33/*` packages, crashes on start |
| v2.0.0 | Switch to official Ultraviolet-App repo | Sub-resources fail — missing COOP/COEP headers |
| v3.0.0 | Add Cross-Origin Isolation headers + WS tuning | ✅ Production-ready |

### Privacy Use Case

The Web Proxy Browser enables ephemeral private browsing: deploy a VM, browse the web through the proxy (traffic exits from the VM's IP, not the user's), then delete the VM. No browsing history persists. Combined with wallet-based auth (no KYC), this provides strong privacy guarantees.

---

## CentralIngress-Aware Port Allocation

### Overview

**Status:** ✅ Implemented (2026-02-09)
**Location:** `VmLifecycleManager.AutoAllocateTemplatePortsAsync()`

When a template-based VM starts, the lifecycle manager auto-allocates direct access ports (iptables DNAT rules) for the template's exposed ports. However, HTTP and WebSocket ports are already handled by CentralIngress (Caddy subdomain routing), making iptables port allocation redundant for those protocols.

### Problem

Before this fix, all public template ports received direct access allocation, including HTTP ports like 8080. This created redundant iptables rules for ports that were already accessible via CentralIngress subdomains (e.g., `web-proxy-browser-xyz.vms.stackfi.tech` → VM:8080).

### Solution

The `AutoAllocateTemplatePortsAsync` method now filters out protocols handled by CentralIngress:

```csharp
// Skip http/ws protocol ports — those are handled by CentralIngress subdomain routing
// (Caddy reverse proxy handles both HTTP and WebSocket upgrades on the same port)
var ingressProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "http", "https", "ws", "wss" };

var portsToAllocate = template.ExposedPorts
    .Where(p => p.IsPublic)
    .Where(p => !ingressProtocols.Contains(p.Protocol ?? ""))
    .Where(p => vm.DirectAccess?.PortMappings?.Any(m => m.VmPort == p.Port) != true)
    .ToList();
```

### Protocol Routing Summary

| Protocol | Routing Method | Example |
|----------|---------------|---------|
| `http` | CentralIngress (Caddy subdomain) | `app-xyz.vms.stackfi.tech` → VM:8080 |
| `https` | CentralIngress (Caddy subdomain) | Same as HTTP with TLS termination |
| `ws` | CentralIngress (Caddy WebSocket upgrade) | Transparent over same HTTP connection |
| `wss` | CentralIngress (Caddy WebSocket upgrade) | Same as WS with TLS |
| `tcp` | Direct Access (iptables DNAT) | `publicIP:40001` → VM:22 (SSH) |
| `udp` | Direct Access (iptables DNAT) | `publicIP:40002` → VM:8388 (Shadowsocks) |
| `both` | Direct Access (iptables DNAT, TCP+UDP) | `publicIP:40003` → VM:8388 |

### Impact by Template

| Template | Exposed Ports | Direct Access Allocated |
|----------|--------------|------------------------|
| Stable Diffusion | 7860 (http) | None (CentralIngress) |
| PostgreSQL | 5432 (tcp) | ✅ 5432 → 40xxx |
| VS Code Server | 8080 (http) | None (CentralIngress) |
| Private Browser | 8080 (http) | None (CentralIngress) |
| Shadowsocks | 8388 (both) | ✅ 8388 → 40xxx (TCP+UDP) |
| Web Proxy Browser | 8080 (http) | None (CentralIngress) |

---

## Resource Management

### CPU Benchmarking

**sysbench-based:** Normalized CPU performance across heterogeneous hardware

```bash
sysbench cpu --threads=$(nproc) --time=10 run
```

**Compute Points Calculation:**
```
computePoints = (nodePerformance / baselinePerformance) * coreCount
```

### Quality Tiers

| Tier | Overcommit Ratio | Use Case |
|------|------------------|----------|
| Guaranteed | 1:1 | Production databases, critical apps |
| Standard | 1.5:1 | Web apps, APIs |
| Balanced | 2:1 | Development environments |
| Burstable | 4:1 | Batch jobs, testing |

---

## Monitoring & Health

### Node Health Monitoring

**Heartbeat System:**
- Nodes send heartbeat every 15 seconds
- 2-minute timeout → marked offline
- Failed heartbeats tracked per day
- 30-day rolling uptime calculated

**Reputation Metrics:**
- **Uptime Percentage:** Based on heartbeat history
- **Total VMs Hosted:** Lifetime counter
- **Successful Completions:** VMs terminated cleanly

### Relay Health Monitoring

**Monitoring:**
- WireGuard tunnel status (every 60s)
- Port mapping validation (every 5min)
- Bandwidth utilization tracking
- Automatic repair on failures

---

## Per-Service VM Readiness Tracking

### Overview

**Status:** Orchestrator side complete (2026-02-10), NodeAgent implementation pending
**Location:** Multiple files across Orchestrator models and services

Distinguishes "VM Running" (hypervisor reports domain active) from "VM Ready for Service" (application-level services are actually responding). Uses **qemu-guest-agent** to probe service readiness from the hypervisor level — no network access to the VM needed.

### The Problem

Previously, a VM was marked "Running" as soon as the libvirt domain started, but cloud-init could still be installing packages, services could be starting up, and the actual application might not respond for 30-120+ seconds. Users saw "Running" but got connection refused. There was no visibility into what was actually ready inside the VM.

### Solution: Universal qemu-guest-agent Probing

Every VM gets per-service readiness tracking via the qemu-guest-agent virtio channel:

1. **System service** (cloud-init) is always tracked — gates all other checks
2. **Template-specific services** are inferred from `ExposedPorts` or explicitly defined per template
3. **Node agent polls** via `virsh qemu-agent-command` every 10 seconds
4. **Results reported** to orchestrator via existing 15-second heartbeat
5. **Frontend displays** individual service status (Pending → Checking → Ready / TimedOut / Failed)

### Architecture

```
Template Creation (Orchestrator):
  VmTemplate.ExposedPorts[].ReadinessCheck
    ↓ (VmService.BuildServiceList)
  VirtualMachine.Services[]
    ↓ (CreateVm command payload)

Node Agent:
  CommandProcessorService parses services from payload
    ↓
  VmInstance.Services[] (persisted in SQLite)
    ↓
  VmReadinessMonitor polls via qemu-guest-agent (10s cycle)
    ↓
  HeartbeatService reports services[] per VM (15s cycle)
    ↓

Orchestrator:
  NodeService.ProcessHeartbeatAsync reads services[]
    ↓
  VirtualMachine.Services[] updated
    ↓
  Frontend displays per-service status
```

### Check Types

| Check Type | Method | Use Case |
|------------|--------|----------|
| `CloudInitDone` | `cloud-init status --format json` | System service (always first) |
| `TcpPort` | `nc -zv -w2 localhost {port}` | Databases, SSH, generic TCP |
| `HttpGet` | `curl -sf -o /dev/null http://localhost:{port}{path}` | Web apps, APIs |
| `ExecCommand` | Arbitrary command, exit 0 = ready | Custom checks (e.g., `pg_isready`) |

All checks execute inside the VM via `virsh qemu-agent-command` using the `guest-exec` / `guest-exec-status` QMP protocol. No network access to the VM is required — communication happens through the virtio-serial channel.

### Service Readiness States

```
Pending     → VM just started, waiting
Checking    → Actively being probed
Ready       → Check passed (service responding)
TimedOut    → Timeout expired without success
Failed      → cloud-init reported error (System service only)
```

**Gating Rule:** All non-System services wait for the System (cloud-init) service to reach Ready before their own checks begin.

### Auto-Inference from Template Protocols

When a template port has no explicit `ReadinessCheck`, the check strategy is inferred:

| Protocol | Inferred Check | Notes |
|----------|---------------|-------|
| `http`, `https`, `ws`, `wss` | HttpGet `/` | Web services, verified via HTTP probe |
| `tcp`, `both` | TcpPort | Raw TCP connection check |
| `udp` | TcpPort (fallback) | UDP not directly probeable; TCP fallback |

### Explicit Template Checks

Templates can override auto-inference with explicit `ReadinessCheck` on `TemplatePort`:

```csharp
// Stable Diffusion — API model list endpoint, 600s timeout
new TemplatePort { Port = 7860, Protocol = "http",
    ReadinessCheck = new ServiceCheck {
        Strategy = CheckStrategy.HttpGet,
        HttpPath = "/api/v1/sd-models",
        TimeoutSeconds = 600
    }
}

// PostgreSQL — pg_isready command, 120s timeout
new TemplatePort { Port = 5432, Protocol = "tcp",
    ReadinessCheck = new ServiceCheck {
        Strategy = CheckStrategy.ExecCommand,
        ExecCommand = "pg_isready -U postgres",
        TimeoutSeconds = 120
    }
}
```

### Service List Building

`VmService.BuildServiceList()` constructs the service list when creating a VM:

1. **Always adds "System" service** — `CloudInitDone` check, 300s timeout
2. **For each template ExposedPort:**
   - If `ReadinessCheck` is defined → use explicit strategy, path, command, timeout
   - If no `ReadinessCheck` → auto-infer from protocol
3. **Bare VMs** (no template) → System-only service list

### Lifecycle Integration

- **On VM creation:** Service list built from template, stored on `VirtualMachine.Services[]`, sent in CreateVm command payload to node agent
- **On VM becomes Running:** `VmLifecycleManager.OnVmBecameRunningAsync()` resets all services to `Pending` (handles restart/recovery scenarios)
- **On heartbeat:** `NodeService.UpdateServiceReadiness()` updates service statuses from node agent reports
- **IsFullyReady:** Computed property — `true` when all services report `Ready`

### Relay VM Separation of Concerns

Relay VMs have existing callback mechanisms (`RelayNatCallbackController`, `RelayController.RegisterCallback`) that perform **infrastructure actions** (iptables NAT rules, WireGuard peer registration). These callbacks remain unchanged — they are action triggers, not readiness signals.

Relay VM readiness is now tracked via the universal qemu-guest-agent system like all other VMs. This cleanly separates:
- **Callbacks** = infrastructure plumbing (VM needs host/orchestrator to do things)
- **qemu-agent** = readiness observation (is the service actually responding?)

### ARM Architecture Support

The ARM domain XML (`GenerateLibvirtXmlMultiArch()` in `LibvirtVmManager.cs`) was missing several devices compared to x86_64. Fixed by adding:
- `<serial>` — console access
- `<video>` — `virtio` model (not `qxl`, which is x86-specific)
- `<rng>` — hardware random number generator
- `<channel>` — qemu-guest-agent virtio channel (critical for readiness monitoring)

### Implementation Files

**Orchestrator (complete):**

| File | Change |
|------|--------|
| `Models/VmTemplate.cs` | `ServiceCheck` class, `CheckStrategy` enum, `ReadinessCheck` on `TemplatePort` |
| `Models/VirtualMachine.cs` | `VmServiceStatus`, `CheckType`, `ServiceReadiness` enums, `Services` list, `IsFullyReady`, updated `VmSummary` |
| `Models/Node.cs` | `HeartbeatServiceInfo`, `Services` on `HeartbeatVmInfo` |
| `Services/VmService.cs` | `BuildServiceList()`, `BuildDefaultServiceList()`, `InferCheckStrategy()`, services in CreateVm payload |
| `Services/NodeService.cs` | `UpdateServiceReadiness()` in heartbeat processing |
| `Services/VmLifecycleManager.cs` | Reset services to Pending on Running transition |
| `Services/TemplateSeederService.cs` | Explicit checks for Stable Diffusion (HttpGet) and PostgreSQL (ExecCommand) |

**NodeAgent (implementation spec created, pending development):**

| File | Change |
|------|--------|
| `Core/Models/VmModels.cs` | `VmServiceStatus`, `CheckType`, `ServiceReadiness` on `VmInstance` |
| `Services/VmReadinessMonitor.cs` | **NEW** — Background service polling via `virsh qemu-agent-command` |
| `Services/HeartbeatService.cs` | Include `Services` in VM summary sent to orchestrator |
| `Services/CommandProcessorService.cs` | Parse services from CreateVm payload |
| `Infrastructure/Persistence/VmRepository.cs` | Persist `Services` JSON to SQLite |
| `Program.cs` | Register `VmReadinessMonitor` as hosted service |
| `Infrastructure/Libvirt/LibvirtVmManager.cs` | ARM XML: added serial, video, rng, guest-agent channel |

### Design Advantages

- **Minimal NodeAgent changes** — Intelligence lives in orchestrator (template→service list building, auto-inference, state management). NodeAgent is a simple executor that runs `virsh` commands and reports results.
- **No network dependency** — qemu-guest-agent communicates through virtio-serial, not the VM's network stack. Works even if VM networking is misconfigured.
- **Universal** — Works for all VM types (general, relay, template-based, bare).
- **Template-extensible** — New templates automatically get readiness checks via protocol inference. Custom templates can define explicit checks.
- **Industry-aligned** — Follows patterns from Kubernetes readiness probes, AWS CloudFormation cfn-signal, and VMware Tools.

---

## DHT Infrastructure

### Overview

**Status:** ✅ Production-verified (2026-02-15)
**Purpose:** Decentralized coordination layer using libp2p DHT nodes connected via WireGuard mesh

DeCloud deploys dedicated DHT VMs that form a peer-to-peer coordination network over the relay's WireGuard mesh. Each DHT node runs a libp2p binary that discovers and connects to other DHT peers, creating a decentralized overlay independent of the central orchestrator.

### Architecture

```
Orchestrator
  ↓ (deploys DHT VMs with WG mesh labels)
Node Agent (host)
  ↓ (cloud-init provisions VM)
DHT VM (QEMU/KVM)
  ├─ WireGuard mesh enrollment (wg-mesh interface)
  ├─ DHT bootstrap polling (orchestrator /api/dht/join)
  └─ libp2p DHT binary (connects to peers over WG mesh)
```

**End-to-end flow:**
1. Orchestrator deploys DHT VM with WG mesh labels (`wg-relay-endpoint`, `wg-relay-pubkey`, `wg-tunnel-ip`, `wg-relay-api`)
2. Cloud-init writes labels to `/etc/decloud/wg-mesh.env` and runs `wg-mesh-enroll.sh`
3. Enrollment script generates WG keypair, registers with relay, starts `wg-mesh` interface
4. `dht-bootstrap-poll.sh` calls `POST /api/dht/join` on orchestrator to discover bootstrap peers
5. DHT binary connects to peers over WireGuard mesh tunnel IPs (e.g., `10.20.1.199:4001`)
6. DHT VM calls `POST /api/dht/ready` callback to report its peer ID

### WireGuard Mesh Enrollment

**Script:** `src/DeCloud.NodeAgent/CloudInit/Templates/shared/wg-mesh-enroll.sh`

DHT VMs join the relay's WireGuard mesh to communicate with each other over private tunnel IPs. The enrollment script:

1. **Sources environment** from `/etc/decloud/wg-mesh.env` (uses `set -a` for auto-export)
2. **Generates WG keypair** via `wg genkey` / `wg pubkey`
3. **Writes WG config** to `/etc/wireguard/wg-mesh.conf` with relay as peer
4. **Registers with relay** using two-strategy approach:
   - **Strategy 1:** NodeAgent proxy via virbr0 default gateway (`POST http://<gateway>:5100/api/relay/wg-mesh-enroll`)
   - **Strategy 2:** Direct relay API fallback (`POST http://<relay-ip>:8080/api/relay/add-peer`)
5. **Starts WG interface** via `wg-quick up wg-mesh`
6. **Verifies connectivity** by pinging relay gateway over tunnel

#### NodeAgent WG Mesh Enrollment Proxy

**Controller:** `src/DeCloud.NodeAgent/Controllers/WgMeshEnrollController.cs`
**Endpoint:** `POST /api/relay/wg-mesh-enroll`

DHT VMs run inside QEMU with NAT networking (virbr0). Only UDP/51820 is NAT-forwarded from the host to the relay VM — port 8080 (relay API) is not reachable from inside other VMs. The NodeAgent proxy solves this by:

1. DHT VM discovers NodeAgent via default gateway (virbr0 bridge IP, port 5100)
2. NodeAgent finds relay VM's bridge IP via `IPortForwardingManager.GetRelayVmIpAsync()`
3. NodeAgent forwards enrollment request to `http://<relay-bridge-ip>:8080/api/relay/add-peer`

For CGNAT hosts without a local relay VM, the proxy discovers the relay's tunnel gateway IP (10.20.x.254) from the host's WireGuard interface addresses.

```csharp
// Two-strategy relay discovery
// Strategy 1: Local relay VM (co-located on same host)
var relayIp = await _portForwardingManager.GetRelayVmIpAsync(ct);

// Strategy 2: Relay tunnel gateway (CGNAT host with WG tunnel)
var relayTunnelIp = await DiscoverRelayTunnelGatewayAsync(ct);
```

### DHT Bootstrap Polling

**Script:** `dht-bootstrap-poll.sh` (runs inside DHT VM)
**Orchestrator endpoint:** `POST /api/dht/join`

After WireGuard mesh enrollment, the DHT binary needs bootstrap peers to join the DHT network. The bootstrap poll script:

1. Calls `POST /api/dht/join` on the orchestrator with the VM's peer ID and tunnel IP
2. Orchestrator returns a list of known DHT peers (peer IDs + WG mesh addresses)
3. DHT binary connects to peers over WireGuard tunnel (e.g., `10.20.1.199:4001`)

### DHT Ready Callback

**Controller:** `src/DeCloud.NodeAgent/Controllers/DhtCallbackController.cs`
**Endpoint:** `POST /api/dht/ready`

When the DHT binary starts and obtains a libp2p peer ID, the VM calls back to the NodeAgent:

1. **Authentication:** HMAC-SHA256 token using machine ID as secret (`X-DHT-Token` header)
2. **Service update:** Marks the VM's System service as `Ready` with `peerId=<libp2p-peer-id>`
3. **Persistence:** Stores peer ID to `/var/lib/decloud/vms/{vmId}/dht-peer-id` for heartbeat reporting
4. **Idempotency:** Updates peer ID even if service was already marked Ready (handles race with cloud-init readiness monitor)

### Critical Bug Fixes (2026-02-15)

Two bugs prevented WireGuard mesh enrollment from succeeding:

#### Bug #1: Environment Variables Not Exported to Child Process

**Symptom:** `wg-mesh-enroll.sh` logged `ERROR: Missing required env: WG_RELAY_ENDPOINT` despite `/etc/decloud/wg-mesh.env` containing correct values.

**Root Cause:** Cloud-init runcmd used `bash -c 'source wg-mesh.env && bash wg-mesh-enroll.sh'`. The `source` command set shell variables in the parent `bash -c` context, but spawning `bash wg-mesh-enroll.sh` as a child process did not inherit them (shell variables are not exported by default).

**Fix:**
1. Modified `dht-vm-cloudinit.yaml` runcmd to use `set -a` (allexport) before sourcing
2. Modified `wg-mesh-enroll.sh` to self-source the env file with `set -a` as a belt-and-suspenders approach

```bash
# Before (broken):
bash -c 'source /etc/decloud/wg-mesh.env && bash /usr/local/bin/wg-mesh-enroll.sh'

# After (fixed):
bash -c 'set -a && source /etc/decloud/wg-mesh.env && set +a && /usr/local/bin/wg-mesh-enroll.sh'
```

#### Bug #2: Relay API Port 8080 Unreachable from DHT VM

**Symptom:** Even with env vars fixed, `curl http://<relay-public-ip>:8080/api/relay/add-peer` from inside the DHT VM would fail because only UDP/51820 is NAT-forwarded.

**Root Cause:** Both relay and DHT VMs use libvirt's `default` network (NAT via virbr0). The relay VM's port 8080 is only accessible from the host via the bridge IP, not from other VMs via the public IP.

**Fix:** Created `WgMeshEnrollController` proxy on the NodeAgent. DHT VMs discover the NodeAgent via their default gateway (virbr0), and the NodeAgent proxies the enrollment request to the relay VM's bridge IP.

### Production Verification

Both DHT nodes confirmed connected after fixes:
- **Node 1** (us-east-1): peerId `12D3KooWD8zw...B8n1`, advertiseIp `10.20.1.199`, connectedPeers: 1
- **Node 2** (tr-south): peerId `12D3KooWHNLM...j8Yx`, advertiseIp `10.20.1.202`, connectedPeers: 1

### Implementation Files

**NodeAgent:**

| File | Change |
|------|--------|
| `CloudInit/Templates/shared/wg-mesh-enroll.sh` | Self-sourcing env file + two-strategy registration (NodeAgent proxy first, direct fallback) |
| `CloudInit/Templates/dht-vm-cloudinit.yaml` | Fixed runcmd to use `set -a` for env var export |
| `Controllers/WgMeshEnrollController.cs` | **NEW** — Proxy endpoint for WG mesh enrollment |
| `Controllers/DhtCallbackController.cs` | DHT ready callback with HMAC auth |

**Orchestrator:**

| File | Change |
|------|--------|
| `Services/DhtNodeService.cs` | DHT VM deployment with WG mesh labels |
| `Controllers/DhtController.cs` | `/api/dht/join` endpoint for bootstrap peer discovery |

**Relay VM:**

| File | Change |
|------|--------|
| `CloudInit/Templates/relay-vm/relay-api.py` | `add_cgnat_peer` handles mesh enrollment; stale peer cleanup protects DHT peers (last octet >= 198) |

---

## Future Features

### Phase 2: User Engagement
- Node operator dashboard
- User reviews after VM termination
- Enhanced reputation system

### Phase 3: Collaboration
- Shared VMs (multi-wallet access)
- Infrastructure templates (multi-VM stacks)
- Live network visualization

### Phase 4: Monetization
- Optional premium node staking (XDE token)
- Advanced analytics dashboard
- Enterprise features

### Long-Term Vision
- Mobile integration (two-tier architecture)
- Smart contract coordination
- DeCloud Relay SDK (standalone product)

---

*For strategic roadmap and business context, see [PROJECT_MEMORY.md](file:///c:/Users/BMA/source/repos/DeCloud.Orchestrator/PROJECT_MEMORY.md)*
