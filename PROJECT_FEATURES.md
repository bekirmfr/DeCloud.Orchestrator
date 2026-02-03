# DeCloud Platform Features

**Last Updated:** 2026-02-02  
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
9. [Future Features](#future-features)

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

### VM Template Marketplace (Planned - Phase 1.2)

**Goal:** Community-created templates for one-click deployments

**Seed Templates:**
- Nextcloud (personal cloud)
- Stable Diffusion (AI image generation)
- Whisper AI (speech-to-text)
- code-server (VS Code in browser) ✅
- Mastodon (social network)
- Minecraft server
- VPN/Tor relay
- Jellyfin (media server)

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
- DHT-based discovery
- DeCloud Relay SDK (standalone product)

---

*For strategic roadmap and business context, see [PROJECT_MEMORY.md](file:///c:/Users/BMA/source/repos/DeCloud.Orchestrator/PROJECT_MEMORY.md)*
