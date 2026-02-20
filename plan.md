# Custom Domain Routing — Implementation Plan

## Goal
Users can add custom domains (e.g., `my.awesome.app`) to their VMs via the dashboard. Navigating to `https://my.awesome.app` routes to the VM, with automatic TLS via Let's Encrypt.

## Architecture Decision

Custom domains route through the **CentralIngress** system (Caddy on the Orchestrator), not per-node Caddy. This is the same path the auto-generated subdomains use:

```
https://my.awesome.app → Caddy (Orchestrator) → NodeAgent:5100 → VM
```

**TLS strategy:**
- Wildcard subdomains (`*.vms.stackfi.tech`): DNS-01 challenge (existing, unchanged)
- Custom domains: Caddy **on-demand TLS** with an ask endpoint — Caddy calls our API to verify the domain is registered before issuing a certificate via HTTP-01

**DNS requirement for users:**
- CNAME: `my.awesome.app → vms.stackfi.tech` (preferred)
- OR A record: `my.awesome.app → <orchestrator IP>`

---

## Changes

### 1. Models — `src/Orchestrator/Models/Ingress.cs`

Add `CustomDomain` class:
```csharp
public class CustomDomain
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string VmId { get; set; } = "";
    public string Domain { get; set; } = "";         // e.g., "my.awesome.app"
    public string OwnerWallet { get; set; } = "";
    public int TargetPort { get; set; } = 80;
    public CustomDomainStatus Status { get; set; } = CustomDomainStatus.PendingDns;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? VerifiedAt { get; set; }
}

public enum CustomDomainStatus { PendingDns, Active, Paused, Error }
```

Add DTOs:
```csharp
public record AddCustomDomainRequest(string Domain, int TargetPort = 80);
public record CustomDomainResponse(string Id, string Domain, int TargetPort,
    CustomDomainStatus Status, string? PublicUrl, DateTime CreatedAt, DateTime? VerifiedAt,
    string? DnsTarget, string? DnsInstructions);
```

Update `VmIngressConfig.CustomDomains` from `List<string>` to `List<CustomDomain>`.

### 2. Service — `src/Orchestrator/Services/CentralIngressService.cs`

Add to `ICentralIngressService`:
```csharp
Task<CustomDomain?> AddCustomDomainAsync(string vmId, string domain, int targetPort, CancellationToken ct);
Task<bool> RemoveCustomDomainAsync(string vmId, string domainId, CancellationToken ct);
Task<CustomDomain?> VerifyCustomDomainDnsAsync(string vmId, string domainId, CancellationToken ct);
Task<List<CustomDomain>> GetCustomDomainsAsync(string vmId, CancellationToken ct);
bool IsCustomDomainRegistered(string domain);
```

Add in-memory lookup: `ConcurrentDictionary<string, CustomDomain> _customDomains` keyed by lowercase domain.

**AddCustomDomainAsync:**
1. Validate VM exists, user owns it, VM is Running
2. Sanitize domain (lowercase, trim)
3. Validate format (valid FQDN, not an IP, not the platform's own base domain)
4. Check global uniqueness (no other VM uses this domain)
5. Enforce limit (max 5 per VM)
6. Create `CustomDomain` with status `PendingDns`
7. Save to `vm.IngressConfig.CustomDomains`, persist VM
8. Add to `_customDomains` lookup

**VerifyCustomDomainDnsAsync:**
1. DNS lookup (`Dns.GetHostAddressesAsync`)
2. Verify resolves to orchestrator IP, or CNAME to base domain
3. If valid → set status `Active`, reload Caddy
4. If invalid → return error with instructions

**Lifecycle hooks update:**
- `OnVmStartedAsync`: Reactivate Active custom domains → reload Caddy
- `OnVmStoppedAsync`: Set Active → Paused → reload Caddy
- `OnVmDeletedAsync`: Remove all custom domains → reload Caddy

### 3. CaddyManager — `src/Orchestrator/Services/CentralCaddyManager.cs`

Update `ReloadAllRoutesAsync` to also accept custom domains:
```csharp
Task<bool> ReloadAllRoutesAsync(
    IEnumerable<CentralIngressRoute> routes,
    IEnumerable<CustomDomain> customDomains,
    CancellationToken ct);
```

In `BuildFullConfigWithPreservedRoutesAsync`:
- For each active custom domain, build a route identical to `BuildRouteConfig` but matching the custom domain hostname
- Need the VM's route info (NodePublicIp, VmId) → lookup from `_routes`

**TLS config — add on-demand TLS policy:**
```json
{
  "automation": {
    "on_demand_tls": {
      "ask": "http://localhost:5050/api/central-ingress/domain-check"
    },
    "policies": [
      {
        "subjects": ["*.vms.stackfi.tech"],
        "issuers": [{ "module": "acme", "challenges": { "dns": { ... } } }]
      },
      {
        "issuers": [{ "module": "acme", "email": "..." }]
      }
    ]
  }
}
```
The second policy (no `subjects`) is the catch-all that uses `on_demand_tls.ask` for per-domain verification. Caddy calls the ask URL before issuing a cert for any domain not covered by the wildcard policy.

### 4. Controller — `src/Orchestrator/Controllers/CentralIngressController.cs`

Add endpoints:
```
POST   /api/central-ingress/vm/{vmId}/domains             — Add custom domain
GET    /api/central-ingress/vm/{vmId}/domains             — List custom domains for VM
POST   /api/central-ingress/vm/{vmId}/domains/{id}/verify — Verify DNS & activate
DELETE /api/central-ingress/vm/{vmId}/domains/{id}        — Remove custom domain
GET    /api/central-ingress/domain-check?domain=...       — Caddy on-demand TLS ask (anonymous)
```

The `domain-check` endpoint returns 200 if domain is registered + active, 404 otherwise. Must be `[AllowAnonymous]` since Caddy calls it internally.

### 5. Frontend — `wwwroot/src/app.js`, `index.html`, `styles.css`

Add "Custom Domains" button on each VM row (next to Direct Access), opening a modal:

**Modal contents:**
- Domain list table: Domain, Port, Status badge, Actions (Verify/Remove)
- Add domain form: domain text input + port number input + Add button
- DNS instructions panel: shows CNAME/A record target with copy button

**Status badges:**
- PendingDns → yellow "DNS Pending" badge + "Verify DNS" button
- Active → green "Active" badge + clickable URL
- Paused → gray "Paused" badge
- Error → red "Error" badge

---

## File Change Summary

| File | Change |
|------|--------|
| `Models/Ingress.cs` | Add `CustomDomain`, `CustomDomainStatus`, DTOs; change `VmIngressConfig.CustomDomains` type |
| `Services/CentralIngressService.cs` | Add custom domain CRUD, DNS verification, lifecycle integration, `_customDomains` cache |
| `Services/CentralCaddyManager.cs` | Accept custom domains in reload, generate routes, on-demand TLS config |
| `Controllers/CentralIngressController.cs` | Add 5 endpoints for custom domain management |
| `wwwroot/src/app.js` | Custom domains modal + CRUD UI |
| `wwwroot/index.html` | Modal HTML + button in VM table |
| `wwwroot/styles.css` | Modal styles + status badges |

No changes to NodeAgent — custom domain traffic reaches the NodeAgent via the same proxy path as subdomain traffic.

---

## User Flow

1. User has running VM at `my-app-a1b2.vms.stackfi.tech`
2. Clicks "Custom Domains" button on VM row → modal opens
3. Types `my.awesome.app`, port 80, clicks "Add Domain"
4. Domain saved as `PendingDns` — modal shows DNS instructions:
   > Point your domain to DeCloud:
   > **CNAME** `my.awesome.app` → `vms.stackfi.tech`
5. User configures DNS at their registrar
6. User clicks "Verify DNS" → system resolves domain
7. DNS valid → status becomes `Active`, Caddy route + TLS auto-provisioned
8. `https://my.awesome.app` now routes to the VM
