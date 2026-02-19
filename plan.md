# VM Naming Pipeline — Implementation Plan

## Goal

Replace the current ad-hoc VM naming (raw user input + scattered sanitization) with a
single pipeline that produces a **canonical, DNS-safe, unique name** at creation time.

```
User input: "My Awesome VM!"
     ↓ sanitize
"my-awesome-vm"
     ↓ validate (length, format)
OK
     ↓ append short unique suffix
"my-awesome-vm-a1b2"
     ↓ uniqueness check (per-user in MongoDB)
OK → this IS the VM name everywhere (display, hostname, subdomain, cloud-init)
```

## Design Decisions

- **Suffix is always appended.** Even if the base name is unique today, appending a
  4-char hex suffix prevents future collisions and makes names visually distinct.
  System VMs (relay, DHT) already follow this pattern (`relay-us-east-a1b2c3d4`).
- **The canonical name is DNS-safe by construction.** Downstream sanitizers become
  no-ops (kept as safety nets, not relied upon).
- **Uniqueness is per-owner.** Different users may have the same VM name —
  the suffix makes the full canonical name globally unique for subdomains.
- **Max base name length: 40 chars.** With suffix `-xxxx` = 45 chars, well under
  the 63-char DNS label limit.

## Changes

### 1. New file: `VmNameService.cs` (Orchestrator)

Location: `src/Orchestrator/Services/VmNameService.cs`

```
public class VmNameService
{
    string SanitizeName(string raw)
      → lowercase, [^a-z0-9-] → hyphen, collapse --, trim -, max 40 chars
      → empty → "vm"

    (bool Valid, string? Error) ValidateName(string sanitized)
      → min 2 chars, max 40 chars
      → must start with letter
      → must not start/end with hyphen
      → must match ^[a-z][a-z0-9-]*[a-z0-9]$

    async Task<string> GenerateUniqueNameAsync(string sanitized, string ownerId)
      → try 4-char hex suffix (first 4 of new Guid)
      → check DataStore for name collision (owner + name)
      → retry up to 3 times with different suffix
      → return "{sanitized}-{suffix}"
```

### 2. Modify: `VmService.CreateVmAsync()` (Orchestrator)

File: `src/Orchestrator/Services/VmService.cs`

- Inject `VmNameService`
- At the top of `CreateVmAsync`, before constructing the VM object:
  ```
  var sanitized = _nameService.SanitizeName(request.Name);
  var validation = _nameService.ValidateName(sanitized);
  if (!validation.Valid)
      return new CreateVmResponse("", VmStatus.Pending, validation.Error, "INVALID_NAME");

  var canonicalName = await _nameService.GenerateUniqueNameAsync(sanitized, userId);
  ```
- Replace `Name = request.Name` with `Name = canonicalName`
- Replace `Hostname = SanitizeHostname(request.Name)` with `Hostname = canonicalName`
- Remove the private `SanitizeHostname()` method (no longer needed)

### 3. Modify: System VM naming (Orchestrator)

Files: `DhtNodeService.cs`, `RelayNodeService.cs`

- System VMs already produce clean names (`relay-us-east-a1b2c3d4`)
- These bypass `VmNameService` — they call `CreateVmAsync` with `userId: "system"`
- Add an early-return in the naming pipeline: if `userId == "system"`, skip
  sanitization/suffix generation and use the name as-is. System VM names are
  constructed by code, not user input.

### 4. Modify: `VmsController.Create()` (Orchestrator)

File: `src/Orchestrator/Controllers/VmsController.cs`

- No changes needed — validation happens inside `VmService.CreateVmAsync()`.
  The controller already returns BadRequest on empty VmId response.

### 5. Modify: `MarketplaceController.DeployTemplate()` (Orchestrator)

File: `src/Orchestrator/Controllers/MarketplaceController.cs`

- No changes needed — the name flows through `BuildVmRequestFromTemplateAsync` →
  `CreateVmAsync`, where the pipeline runs.

### 6. Modify: `CentralIngressService.GenerateSubdomain()` (Orchestrator)

File: `src/Orchestrator/Services/CentralIngressService.cs`

- Change subdomain pattern default from `"{name}-{id4}"` to `"{name}"`
  The name already contains a unique suffix, so appending `{id4}` would be redundant.
- Keep `SanitizeForSubdomain()` as a safety net, but the input is already clean.
- Remove the collision-handling code (lines 138-142) — uniqueness is guaranteed
  by the naming pipeline.

### 7. Modify: `DirectAccessService` DNS naming (Orchestrator)

File: `src/Orchestrator/Services/DirectAccessService.cs`

- Change subdomain from `"{SanitizeVmName(vm.Name)}-{id4}"` to `vm.Name`
  directly (already DNS-safe, already unique).
- Keep `SanitizeVmName()` as safety net.

### 8. No changes to NodeAgent

The NodeAgent receives the canonical name in the CreateVm payload (`spec.Name`).
It already uses this for:
- Cloud-init `__VM_NAME__` / `__HOSTNAME__` → now DNS-safe, no injection risk
- `metadata.json` persistence
- Heartbeat reporting
- SQLite storage

The name arrives already sanitized and unique — no NodeAgent changes needed.

### 9. Frontend validation (optional, UX improvement)

File: `src/Orchestrator/wwwroot/src/app.js`

- Add client-side preview: show the sanitized name as the user types
  (e.g., "My Awesome VM!" → preview: "my-awesome-vm-xxxx")
- Add basic regex validation to match server-side rules
- This is cosmetic — the server is the authority.

## Files Changed

| File | Change |
|------|--------|
| `Services/VmNameService.cs` | **NEW** — centralized naming pipeline |
| `Services/VmService.cs` | Inject VmNameService, use canonical name, remove SanitizeHostname |
| `Program.cs` | Register VmNameService in DI |
| `Services/CentralIngressService.cs` | Simplify subdomain pattern, remove collision code |
| `Services/DirectAccessService.cs` | Use vm.Name directly for DNS subdomain |
| `Models/Ingress.cs` | Update default SubdomainPattern |
| `wwwroot/src/app.js` | Client-side name preview + validation (optional) |

## Not Changed

| File | Reason |
|------|--------|
| `DhtNodeService.cs` | System VMs already produce clean names |
| `RelayNodeService.cs` | System VMs already produce clean names |
| `VmsController.cs` | Validation handled by VmService |
| `MarketplaceController.cs` | Name flows through existing pipeline |
| All NodeAgent files | Receives already-clean name |
