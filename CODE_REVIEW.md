# DeCloud Orchestrator - Comprehensive Code Review

**Date:** 2026-02-02
**Scope:** Full codebase review - backend, frontend, infrastructure, smart contracts
**Reviewed by:** Claude (Opus 4.5)

---

## Executive Summary

The DeCloud Orchestrator is a substantial codebase (~85 C# files, 7 JS modules) implementing a decentralized cloud computing platform. The architecture is sound at a high level (orchestrator-node model, wallet-based auth, relay infrastructure), but the implementation has **critical security vulnerabilities** that must be addressed before any production use with real funds or real user data.

### Severity Distribution

| Severity | Count | Description |
|----------|-------|-------------|
| **CRITICAL** | 14 | Immediate exploitation risk, data loss, or complete auth bypass |
| **HIGH** | 16 | Significant security or reliability issues |
| **MEDIUM** | 22 | Important bugs, design issues, or defense-in-depth gaps |
| **LOW** | 8 | Minor issues, code quality, or future concerns |

---

## CRITICAL Findings

### SEC-01: Hardcoded Secrets in Source Control
**File:** `src/Orchestrator/appsettings.json`

The following secrets are committed to the repository in plaintext:
- **JWT signing key** (line 10)
- **MongoDB Atlas credentials** including password (line 19)
- **Cloudflare DNS API token** (line 27)
- **Orchestrator blockchain private key** (line 160) - controls settlement funds

**Impact:** Anyone with repository access can authenticate as any user, access the database, modify DNS, and drain escrow funds.

**Recommendation:** Immediately rotate ALL exposed credentials. Move secrets to environment variables, Azure Key Vault, or `.env` files excluded from source control. The `appsettings.json` should contain only placeholder values.

---

### SEC-02: SignalR Hub Authentication Completely Disabled
**File:** `src/Orchestrator/Hubs/OrchestratorHub.cs:98-114`

The `RegisterAsNode` authentication check is entirely commented out. No hub methods have `[Authorize]` attributes or perform caller identity checks.

**Impact:** Any WebSocket client can:
- Impersonate any node
- Hijack terminal sessions by sending `TerminalOutput` to arbitrary connections
- Change VM statuses via `ReportVmStatus`
- Subscribe to any user's or VM's events

**Recommendation:** Uncomment and implement proper authentication. Add `[Authorize]` to the hub class. Validate that callers own the resources they're operating on.

---

### SEC-03: WebSocket Proxy Has No Authentication
**File:** `src/Orchestrator/Middleware/WebSocketProxyMiddleware.cs`

The `/api/terminal-proxy/{vmId}` and `/api/sftp-proxy/{vmId}` endpoints perform no authentication. Anyone who knows a VM ID gets full terminal or SFTP access.

**Impact:** Unauthenticated remote shell and file access to any VM in the system.

**Recommendation:** Add JWT validation in the middleware before establishing the proxy connection. Verify the authenticated user owns the target VM.

---

### SEC-04: Subdomain Proxy Trusts Spoofable Header
**File:** `src/Orchestrator/Middleware/SubdomainProxyMiddleware.cs`

The proxy trusts the `X-DeCloud-Subdomain` header without authentication. If the orchestrator port is reachable without Caddy in front, any request can set this header.

**Impact:** SSRF and unauthorized access to any VM's HTTP services.

**Recommendation:** Validate the header comes from a trusted source (e.g., check `X-Forwarded-For` matches Caddy, or use a shared secret header).

---

### SEC-05: Admin Scheduling Config Has Zero Authentication
**File:** `src/Orchestrator/Controllers/SchedulingConfigController.cs:12-14`

The `[Authorize(Roles = "Admin")]` attribute is commented out with a TODO. Any anonymous user can read and **overwrite** the entire scheduling configuration.

**Impact:** An attacker can manipulate VM scheduling, resource allocation, and pricing.

**Recommendation:** Uncomment the `[Authorize(Roles = "Admin")]` attribute immediately.

---

### SEC-06: Shell Injection in WireGuard Manager
**File:** `src/Orchestrator/Services/WireGuardManager.cs:377`

```csharp
Arguments = $"-c \"echo '{privateKey}' | wg pubkey\"",
```

A WireGuard private key is interpolated directly into a shell command. A malformed key containing `'` would break out of the echo and allow arbitrary command execution.

**Impact:** Remote code execution if an attacker can influence the private key value.

**Recommendation:** Use stdin piping or temporary files instead of shell interpolation. Write the key to Process.StandardInput rather than embedding in arguments.

---

### SEC-07: Null Dereference Crashes VM Deletion
**File:** `src/Orchestrator/Services/VmService.cs:609-617`

```csharp
if (node == null)
{
    node.ReservedResources.ComputePoints = ...;  // NullReferenceException
```

The code enters the `null` check block and immediately dereferences `null`.

**Impact:** VM deletion crashes with an unhandled exception every time a VM's node no longer exists. Resources leak and VMs become un-deletable.

**Recommendation:** Move the resource-freeing code to the `else` branch (when node IS found).

---

### SEC-08: Variable Typo Breaks Command Registry Lookup
**File:** `src/Orchestrator/Services/NodeService.cs:536`

```csharp
var affectedm = await _dataStore.GetVmAsync(registration!.VmId);  // "affectedm" not "affectedVm"
if (affectedVm != null)  // always null - checks wrong variable
```

**Impact:** The primary command acknowledgment lookup (Strategy 1) always fails, falling through to less reliable backup strategies. This can lead to VMs stuck in transitional states.

---

### SEC-09: Refresh Tokens Never Stored
**File:** `src/Orchestrator/Services/UserService.cs:513-519`

`GenerateRefreshToken` creates a random token but never inserts it into the `_refreshTokens` dictionary. Token refresh is 100% broken.

**Impact:** Users cannot refresh their authentication tokens. Sessions expire and cannot be renewed.

---

### SEC-10: Default Memory is 2 MB Instead of 2 GB
**File:** `src/Orchestrator/Models/VirtualMachine.cs:116`

```csharp
public long MemoryBytes { get; set; } = 2 * 1024L * 1024L;  // 2 MB, not 2 GB
```

Missing a `* 1024L` multiplier. Should be `2 * 1024L * 1024L * 1024L`.

**Impact:** Any VM created without explicitly setting memory gets 2 MB - unusable.

---

### SEC-11: Default User Quotas Are Effectively Zero
**File:** `src/Orchestrator/Models/User.cs:71-72`

```csharp
public long MaxMemoryBytes { get; set; } = 32768;    // 32 KB
public long MaxStorageBytes { get; set; } = 500;      // 500 bytes
```

Properties named `*Bytes` have values that appear to be in MB/GB units, not bytes.

**Impact:** Default quotas block all VM creation for new users.

---

### SEC-12: CreateVm Command Registered as DeleteVm Type
**File:** `src/Orchestrator/Services/VmService.cs:981`

```csharp
_dataStore.RegisterCommand(command.CommandId, vm.Id, vm.NodeId, NodeCommandType.DeleteVm);
// Should be NodeCommandType.CreateVm
```

**Impact:** Command acknowledgment heuristics (Strategy 4) will mismatch, potentially causing wrong VMs to be updated.

---

### SEC-13: PendingCommands/CommandRegistry Never Persisted
**File:** `src/Orchestrator/Persistence/DataStore.cs`

`PendingCommands`, `PendingCommandAcks`, and `CommandRegistry` are entirely in-memory ConcurrentDictionaries with no MongoDB backing. A process restart loses all in-flight operation state.

**Impact:** Orchestrator restart during VM provisioning leaves VMs in limbo states (`Provisioning`, `Deleting`) with no recovery mechanism.

---

### SEC-14: Development Mock Signature Bypass
**File:** `src/Orchestrator/Services/UserService.cs:308-313`

```csharp
if (_environment.IsDevelopment() && IsMockSignature(request.Signature))
    signatureValid = true;
```

If `DOTNET_ENVIRONMENT=Development` is accidentally set in production, any attacker can authenticate as any wallet.

**Impact:** Complete authentication bypass.

---

## HIGH Findings

### HIGH-01: No Rate Limiting on Anonymous Node Registration
**File:** `src/Orchestrator/Controllers/NodesController.cs:33-51`

Node registration is `[AllowAnonymous]` with no rate limiting. An attacker can flood the system with fake nodes.

### HIGH-02: Full Node Objects Returned to All Authenticated Users
**File:** `src/Orchestrator/Controllers/NodesController.cs:206-218`

`GET /api/nodes` returns complete `Node` models including sensitive fields like `ApiKeyHash`, `RelayInfo.WireGuardPrivateKey`, and internal network details to any authenticated user.

### HIGH-03: WireGuard Private Keys Stored in Plaintext
**File:** `src/Orchestrator/Services/RelayNodeService.cs:186-188,205`

WireGuard private keys are stored in VM labels and in the `RelayNodeInfo` model, persisted to MongoDB and potentially exposed via APIs.

### HIGH-04: Non-Atomic Quota Updates Allow Quota Bypass
**File:** `src/Orchestrator/Services/VmService.cs:207-212`

`VmService` is a singleton. Concurrent `CreateVmAsync` calls for the same user can both read, increment, and write quota values, losing one increment. Users can exceed their quotas.

### HIGH-05: Hardcoded Fallback JWT Key
**Files:** `src/Orchestrator/Services/NodeService.cs:1754`, `UserService.cs:474`

```csharp
var jwtKey = _configuration["Jwt:Key"] ?? "default-dev-key-change-in-production-min-32-chars!";
```

If the configuration key is missing, the system silently uses a well-known key.

### HIGH-06: HTTP (Not HTTPS) Communication to Nodes
**Files:** `NodeService.cs:1500,1574`, `RelayNodeService.cs`, `WebSocketProxyMiddleware.cs`, `SubdomainProxyMiddleware.cs`

All orchestrator-to-node communication uses plain `http://` and `ws://`. SSH key injection, certificate signing, terminal data, and VM commands transit in cleartext.

### HIGH-07: Private Key Visible via Process Arguments
**File:** `install.sh:205`

The blockchain private key is passed as a command-line argument, visible in `/proc/<pid>/cmdline` and `ps aux`.

### HIGH-08: Service Runs as Root
**File:** `install.sh:1305`

The orchestrator systemd service runs as `User=root`, maximizing blast radius of any vulnerability.

### HIGH-09: Smart Contract - Single Orchestrator Key Controls All Funds
**File:** `contracts/DeCloudEscrow.sol`

The `orchestrator` address can drain any user's balance via `reportUsage`. No multi-sig, no time-lock, no dispute mechanism.

### HIGH-10: Smart Contract - batchReportUsage Silently Skips Failures
**File:** `contracts/DeCloudEscrow.sol:227-229`

Invalid entries are silently skipped with no audit trail. Combined with no nonce/idempotency, usage can be double-reported.

### HIGH-11: Password Entropy ~27 Bits (Not ~37 as Claimed)
**File:** `src/Orchestrator/Services/PasswordService.cs`

The word list has ~130 unique words, not ~1000. Real entropy is ~27.5 bits (~190M combinations), brute-forceable.

### HIGH-12: IP Address Collision in VM Networks
**File:** `src/Orchestrator/Services/VmService.cs:1053-1057`

Only 252 possible IPs (`10.100.0.2-254`), randomly selected with no collision check. Birthday paradox makes collisions likely at ~20 VMs per node.

### HIGH-13: XSS Vulnerabilities in Frontend
**File:** `src/Orchestrator/wwwroot/src/app.js` (multiple locations)

At least 6 locations where user-controlled data (`vm.name`, passwords, SSH key names) is injected into `innerHTML` without escaping, despite an `escapeHtml()` function existing in the codebase.

### HIGH-14: Broken `restartVM` and `forceStopVM` Functions
**File:** `src/Orchestrator/wwwroot/src/app.js:1427,1456`

```javascript
const response = await api(`/api/{vmId}/action`, {  // literal {vmId}, not ${vmId}
```

These functions call a non-existent endpoint. Restart and force-stop are completely broken.

### HIGH-15: Orchestrator URL Redirect Enables Token Exfiltration
**File:** `src/Orchestrator/wwwroot/src/app.js:45,2031-2036`

The orchestrator URL is loaded from `localStorage` and configurable via settings. An XSS attack can redirect all authenticated API calls (including auth tokens) to an attacker-controlled server.

### HIGH-16: Production Sourcemaps Enabled
**File:** `src/Orchestrator/wwwroot/vite.config.js:15`

`sourcemap: true` exposes full original source code including comments with security notes.

---

## MEDIUM Findings

| # | Finding | File(s) |
|---|---------|---------|
| M-01 | Exception messages leaked to API clients | AuthController, VmsController, MarketplaceController, Middleware.cs |
| M-02 | Inconsistent user ID extraction across controllers (3 different patterns) | Multiple controllers |
| M-03 | Inconsistent error response formats (ApiResponse vs anonymous objects) | Multiple controllers |
| M-04 | Missing VM ownership checks on several endpoints | AttestationController:41, CentralIngressController:278, IngressController:470 |
| M-05 | Terminal session termination has no ownership verification | TerminalController:97-109 |
| M-06 | Nonce not stored server-side; potential replay attacks | AuthController:83-86 |
| M-07 | Controller directly mutates domain entities bypassing service layer | UserController:80-88 |
| M-08 | Fire-and-forget `Task.Run` throughout services (lost errors, race conditions) | NodeService, VmService, MarketplaceController |
| M-09 | `_cgnatSyncLocks` ConcurrentDictionary grows unboundedly (SemaphoreSlim leak) | NodeService:80 |
| M-10 | Relay tunnel IP allocation based on `CurrentLoad` causes collisions | RelayNodeService:484 |
| M-11 | `UpsertRouteAsync` and `RemoveRouteAsync` are no-ops (return true, do nothing) | CentralCaddyManager:77-106 |
| M-12 | CentralIngress routes lost on restart (never rehydrated from DataStore) | CentralIngressService:99 |
| M-13 | Race condition in subdomain collision detection (check-then-act) | CentralIngressService:138 |
| M-14 | HttpClient created per-call in RelayNodeService (socket exhaustion) | RelayNodeService:690,804,998,1071 |
| M-15 | Misleading constants (comments don't match values) | RelayNodeService:57-58 |
| M-16 | `AddPendingCommand` race condition (check-then-set, not GetOrAdd) | DataStore:1040-1043 |
| M-17 | Mutable objects in ConcurrentDictionary have no synchronization | DataStore (throughout) |
| M-18 | In-memory/MongoDB can diverge (no rollback on write failure) | DataStore:559 |
| M-19 | `GetVmsByNodeAsync` and `GetVmsByUserAsync` throw NRE without MongoDB | DataStore:527,538 |
| M-20 | Auth tokens stored in `localStorage` (XSS-accessible) | app.js:419-420 |
| M-21 | `window.api` and `window.ethersSigner` exposed globally | app.js:2146,2177 |
| M-22 | VM pricing returns $0/hr for most VM types (Compute, Memory, Storage, GPU) | VmService:1023-1045 |

---

## LOW Findings

| # | Finding | File(s) |
|---|---------|---------|
| L-01 | Duplicate node search endpoints across MarketplaceController and NodesController | Multiple |
| L-02 | O(N) node iteration to find ingress by ID | IngressController:196-231 |
| L-03 | `PagedResult<T>.TotalPages` division by zero when PageSize=0 | Common.cs:42 |
| L-04 | `Node` is a god-object with ~142 properties spanning 5+ concerns | Node.cs |
| L-05 | `ConnectedNodeIds` declared as `List<string>` but comment says `HashSet` | Node.cs:548 |
| L-06 | No emergency pause mechanism in smart contract | DeCloudEscrow.sol |
| L-07 | No usage nonce/idempotency in smart contract | DeCloudEscrow.sol |
| L-08 | `curl | bash` as root in install script | install.sh:697 |

---

## Architectural Observations

### Strengths
1. **Clean separation**: Controllers delegate to services; dependency injection is used throughout
2. **Dual persistence**: In-memory with MongoDB write-through provides fast reads with durability
3. **Relay infrastructure**: Creative solution for CGNAT nodes is well-designed conceptually
4. **Attestation system**: The memory touch test and adaptive timeout approach is innovative
5. **Wallet-based auth**: Eliminates password management for user accounts
6. **Frontend build integration**: Vite build embedded in .csproj is well-configured

### Weaknesses
1. **No test suite**: Zero test projects exist. With this many concurrency and business logic issues, automated tests are essential
2. **Singleton services with mutable shared state**: `NodeService`, `VmService`, `DataStore` are singletons that share mutable `Node`/`VM` objects across threads without synchronization
3. **In-memory as primary store**: The ConcurrentDictionary-based in-memory storage is the primary data path even with MongoDB configured. A crash before the next sync cycle loses data
4. **Command/acknowledgment pipeline is ephemeral**: The most critical operational state (pending commands, command registry) has zero persistence
5. **Inconsistent auth patterns**: Three different user ID claim paths, commented-out auth, mix of controller-level and method-level authorization
6. **No input validation layer**: Models have no validation attributes; controllers do minimal validation; cloud-init templates have easily bypassed content checks

---

## Priority Recommendations

### Immediate (Do First)
1. **Rotate all secrets** exposed in `appsettings.json` - JWT key, MongoDB password, Cloudflare token, orchestrator private key
2. **Enable authentication** on SignalR hub, WebSocket proxy, and scheduling config controller
3. **Fix the null dereference** in VmService.cs:609 (VM deletion crash)
4. **Fix the variable typo** in NodeService.cs:536 (command registry broken)
5. **Fix the URL template bugs** in app.js restartVM/forceStopVM (`{vmId}` -> `${vmId}`)
6. **Fix the memory default** in VirtualMachine.cs:116 (2 MB -> 2 GB)

### Short-Term (This Sprint)
7. **Add ownership checks** to all VM/node endpoints that currently lack them
8. **Sanitize all innerHTML** usage in the frontend (apply `escapeHtml()` consistently)
9. **Fix refresh token storage** in UserService.cs
10. **Fix the CreateVm/DeleteVm command type mismatch** in VmService.cs:981
11. **Add `GetOrAdd`** pattern to DataStore.AddPendingCommand to fix the race condition
12. **Persist PendingCommands/CommandRegistry** to MongoDB
13. **Disable production sourcemaps** in vite.config.js

### Medium-Term (Next Month)
14. **Add a test suite** - at minimum for scheduling, billing, command acknowledgment, and auth flows
15. **Implement rate limiting** on anonymous endpoints (node registration, auth)
16. **Switch to HTTPS/WSS** for orchestrator-to-node communication
17. **Run the service as non-root** in systemd
18. **Add multi-sig or time-lock** to the smart contract
19. **Implement proper input validation** using data annotations on models and FluentValidation in services
20. **Refactor Node model** into smaller, focused entities

---

## Files Reviewed

### Backend (C#)
- `Program.cs` - Entry point and DI configuration
- All 15 controllers in `Controllers/`
- 8 core services: NodeService, VmService, RelayNodeService, UserService, TemplateService, WireGuardManager, CentralIngressService, CentralCaddyManager
- 4 security services: PasswordService, WalletSshKeyService, SshCertificateService, ApiKeyAuthentication
- All 14+ model files in `Models/`
- `Persistence/DataStore.cs`
- `Hubs/OrchestratorHub.cs`
- Both middleware files
- `Infrastructure/Middleware.cs`

### Frontend (JavaScript)
- All 7 JS modules in `wwwroot/src/`
- `index.html`, `vite.config.js`

### Infrastructure
- `Dockerfile`
- `install.sh`
- `contracts/DeCloudEscrow.sol`
- `appsettings.json`
- `Orchestrator.csproj`

---

*This review identifies issues found through static analysis. Dynamic testing, penetration testing, and fuzzing would likely uncover additional issues.*
