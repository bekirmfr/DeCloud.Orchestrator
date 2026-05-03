# VM Cloud-init Pipeline Migration — Unified Plan

**Target audience:** Engineering agents picking up this work cold.
**Status:** Ready to execute. No prerequisite work outside this plan.
**Estimated effort:** 5–7 weeks elapsed end-to-end across all phases.

> **Document history.** This plan supersedes and absorbs three earlier drafts:
> `TENANT_VM_UNIFIED_PIPELINE.md` (umbrella for tenant-side cleanup),
> `SYSTEM_VM_RENDERING_ADDENDUM.md` (system-VM specifics), and the v1/v2/v3
> handouts that iterated toward this design. Where this plan contradicts an
> earlier document, this plan wins. The earlier documents are kept for
> historical record only.
>
> Key design decisions — captured here in §2 and §3 — were reached through
> several rounds of reflection. The most important: cloud-init carries only
> deploy-time-stable values; mutable environmental values are served from a
> node-local API and refreshed in-VM by a generic watcher; templates declare
> their variables (with kind, scope, and defaults) as first-class metadata
> that drives the render, runtime, and watcher behaviour uniformly.

---

## 0. Mission

Make `VmTemplate` the single declarative source of truth for what every VM —
system, marketplace, or general — needs to run. After this work:

- The orchestrator produces a complete cloud-init document at scheduling
  time (tenant flow) or pull time (system flow). The cloud-init contains
  only values stable for the life of the VM.
- Mutable environmental values (region, advertise IP, relay endpoint,
  bootstrap peers, ingress URL, etc.) are served from a node-local
  `/api/obligations/{role}/environment` endpoint and refreshed in-VM by a
  generic watcher. Environmental drift is handled by VM **restart**, not
  redeploy.
- The node agent does no rendering, no role-specific substitution, no
  template processing. `LibvirtVmManager.CreateCloudInitIso` collapses to
  one line of meaningful logic.
- Adding a new system VM role, or a new marketplace template variable,
  is data — not code — outside one resolver registration.

This is **not a feature**. It is a layered boundary correction across
three boundaries simultaneously: orchestrator/node, deploy-time/runtime,
and template-data/orchestrator-code. Each prior boundary draw produced a
class of bugs the next boundary draw fixes.

---

## 1. Required reading

Read end-to-end before writing any code:

1. **`SYSTEM_VM_RESILIENCE_DESIGN.md`** — context on the obligation lifecycle.
   The pull-on-stale primitive (`systemTemplateVersions` /
   `systemTemplatesPending`) and the boot-time identity-fetch pattern (§4.8)
   are both load-bearing here.
2. **`SYSTEM_VM_LIFECYCLE_FLOW_MAP.md`** — concrete data flow.

If you read only one section deeply, read this plan's §3 (the
template-driven render/runtime/watcher trio).

---

## 2. Decisions — do not re-litigate

| Decision | Status |
|---|---|
| **Templates declare their variables** as first-class structured metadata | New `VmTemplate.Variables: List<TemplateVariable>` field. Drives rendering, environment endpoint, watcher. See §2.2. |
| **Two variable kinds: Static, Dynamic** | Statics resolve at render time and bake into cloud-init. Dynamics resolve at runtime via the environment endpoint and watcher. |
| **Three watcher scopes: Noop, Reload, Restart** | Declared per dynamic variable. Watcher applies the max scope across all changed variables in a poll cycle. See §2.3 for concrete scope policy per role. |
| **Single render pipeline** for tenant + system VMs | One `CloudInitRenderer`, driven by `template.Variables` and a `IVariableResolverRegistry`. No separate system/tenant renderers. |
| **Resolver registry replaces ad-hoc per-role contributors** | Each variable is resolved by exactly one registered resolver. Resolvers organized by domain in folder structure (§5). The renderer is generic. |
| **Render fresh on each pull (system VMs)** | Existing pull endpoint (`GET /api/nodes/me/system-templates/{role}`) renders on demand. No `RenderedCloudInit` cache field on the obligation. |
| **Render at scheduling (tenant VMs)** | `VmService.AssignVmToNodeAsync` calls `CloudInitRenderer.Render` and ships the result in the `CreateVm` command's `UserData`. |
| **Delivery via existing primitives** | System VMs: existing `systemTemplatesPending` flow, wire format unchanged (`SystemVmTemplate.CloudInitContent` carries rendered output). Tenant VMs: existing `CreateVm` command's `UserData` field. |
| **Environment endpoint is generic** | `GET /api/obligations/{role}/environment` returns values for exactly the dynamic variables the role's template declares. New roles need no new endpoint code. |
| **Watcher is generic** | One shell script (`decloud-env-watcher.sh`), role-parameterized. Reads scope policy from `/etc/decloud-{role}/variable-scopes.conf` baked at cloud-init render time. |
| **`__WG_TUNNEL_IP__` is a declared dynamic** | No special-case in the validator. Each role declares it with `Scope: Restart`. |
| **`LibvirtVmManager.PrepareCloudInitVariables` deletes** | Post-cutover, `CreateCloudInitIso` collapses to `cloudInitYaml = spec.CloudInitUserData` plus `EnsureQemuGuestAgent` + GPU-shim safety nets. |
| **CA public key sourced from `Node.SshCaPublicKey`** | Stored on the node record at registration. No node-side disk read at deploy time. |
| **Base layer / role layer split** | `base-tenant.yaml`, `base-system.yaml` provide infrastructure primitives. Role layers add role-specific content. `TemplateComposer` merges. |
| **General VMs always have `TemplateId`** | A `platform-general` template is seeded; `VmService.CreateVmAsync` assigns it when no `TemplateId` is provided. No "no-template" code path. |
| **`SystemVmObligation` carries `VmId` and `VmName`** | Assigned at obligation creation by the orchestrator. Node never generates these locally. Add `VmName` field to the obligation model. |
| **Sequencing: shared first, then one role at a time** | Shared infrastructure (Phase 1) lands with all VMs still on the legacy path. Then tenant VMs cut over (Phase 2). Then system VMs cut over one role at a time: relay → DHT → blockstore (Phase 3). Legacy code deletion last (Phase 4). See §6. |

If during implementation you find yourself wanting to revisit one of these — stop. Surface in the handoff log; do not silently change.

### 2.1 The deploy-time / runtime classification

The single rule: **if a value can change without redeploying the VM, it must
be declared `Kind: Dynamic`.**

A `TemplateVariable` is one of:

- **Static** — resolved at render time. Substituted as `__VARNAME__` in
  the cloud-init YAML. Bound by user input (deploy-time form), defaults,
  or a platform resolver. Examples: `VM_ID`, `HOSTNAME`, `CA_PUBLIC_KEY`,
  `WIREGUARD_PRIVATE_KEY`, `DHT_LISTEN_PORT`, `MODEL_NAME` (tenant).
- **Dynamic** — resolved at runtime. Appears in cloud-init only as a shell
  variable reference (`$VARNAME`) sourced from
  `/etc/decloud-{role}/environment`. Always platform-bound; users do not
  supply values. Examples: `REGION`, `ADVERTISE_IP`, `WG_RELAY_ENDPOINT`,
  `BOOTSTRAP_PEERS`, `WG_TUNNEL_IP`, `INGRESS_URL` (tenant).

The `__FOO__` vs `$FOO` syntax distinction is intentional. Render-time
substitution targets `__FOO__`. Shell-source at boot targets `$FOO`. The
validator (§3.4) uses the template declarations to verify each placeholder
appears in the form appropriate to its kind.

### 2.2 The `TemplateVariable` model

```csharp
// src/Shared/Models/TemplateVariable.cs
public sealed class TemplateVariable
{
    public required string Name { get; init; }              // e.g., "REGION"
    public required VariableKind Kind { get; init; }
    public WatcherScope Scope { get; init; } = WatcherScope.Restart;  // dynamics only
    public string? DefaultValue { get; init; }              // statics only
    public bool Required { get; init; }                     // statics only
    public string? Description { get; init; }               // for UI / docs
    public string? ResolverKey { get; init; }               // optional override; defaults to Name
}

public enum VariableKind { Static, Dynamic }
public enum WatcherScope  { Noop, Reload, Restart }
```

`Scope` is meaningful only for `Dynamic`. `DefaultValue`/`Required` are
meaningful only for `Static`. `ResolverKey` lets a template variable
delegate to a differently-named platform resolver (defaults to `Name`).

### 2.3 Concrete scope policy per role

These were determined by reading the live binary code and existing scripts.
The `Reasoning` column captures why; that's the input to §6.6's scope-correctness
audit when the binary owners verify.

**DHT** (`dht-node` Go binary, reads `/etc/decloud-dht/dht.env` at startup
via systemd `EnvironmentFile=`):

| Variable | Scope | Reasoning |
|---|---|---|
| `DHT_ADVERTISE_IP` | Restart | libp2p multiaddr is bound at host startup; can't rebind without restart |
| `DHT_BOOTSTRAP_PEERS` | Noop | `dht-bootstrap-poll.sh` already re-sources env each iteration; libp2p has organic peer discovery once joined |
| `DHT_REGION` | Noop | Currently consumed only by static `dht-metadata.json` written once at boot; cosmetic, fixed by next redeploy |
| `WG_RELAY_ENDPOINT` | Restart | WireGuard kernel-module config; requires `wg-quick@wg-mesh` restart (cascades to dht service via `BindsTo`) |
| `WG_RELAY_PUBKEY` | Restart | Same as above |
| `WG_RELAY_API` | Restart | Same as above |
| `WG_TUNNEL_IP` | Restart | Same as above; also affects `DHT_ADVERTISE_IP` derivation |

**BlockStore** (`blockstore-node`, same pattern as DHT):

| Variable | Scope | Reasoning |
|---|---|---|
| `BLOCKSTORE_ADVERTISE_IP` | Restart | Same as DHT advertise IP |
| `BLOCKSTORE_BOOTSTRAP_PEERS` | Noop | Same as DHT bootstrap peers |
| `DHT_REGION` (yes, that's the actual variable name) | Noop | Cosmetic, used in dashboard metadata only |
| `WG_RELAY_*`, `WG_TUNNEL_IP` | Restart | Same as DHT |

**Relay** (`relay-api.py`, currently substitutes values into the `.py`
file at cloud-init time rather than reading env vars):

The relay's runtime-mutable surface is genuinely small. `RELAY_REGION`,
`RELAY_CAPACITY`, `RELAY_NAME` are used only for dashboard display; the
WG private key and allocated subnet never change post-deploy without
explicit identity rotation (which is a redeploy event). Public IP changes
affect cgnat-node WG configs, but those are managed by the orchestrator
through the relay-API peer-registration channel — not via env vars in the
relay VM itself.

| Variable | Scope | Reasoning |
|---|---|---|
| `RELAY_REGION` | Noop | Cosmetic only; baked into `relay-api.py` at cloud-init time. Converting `relay-api.py` to read env vars is out of scope. |
| `RELAY_CAPACITY` | Noop | Same as above. |

In other words: the relay role declares almost no dynamics. Its variables
are nearly all `Static`. The watcher runs on relay VMs but typically has
nothing to do. That is correct — the relay VM is, by design, a thin
WireGuard wrapper with most of its mutable state managed externally.

These are the **starting** scope assignments. §7.6 (scope-correctness
audit) verifies them against running binaries; corrections bump the
template revision.

### 2.4 User-provided vs platform-bound

- **`Static, Required: true, DefaultValue: null`** — user must supply at
  deploy time. Surface in marketplace deploy form.
- **`Static, Required: false`** — uses `DefaultValue` if not user-supplied.
- **`Static`** with a matching platform resolver — platform-bound, never
  shown in deploy form. (System VM templates are entirely platform-bound.)
- **`Dynamic`** — always platform-bound. Never user-supplied.

---

## 3. Architecture overview

Three pieces consume `TemplateVariable` declarations: the renderer, the
environment endpoint, the watcher. All three are generic — they iterate
the declared variables and apply the kind/scope policy.

### 3.1 Resolver registry

```csharp
public interface IVariableResolver
{
    string ResolverKey { get; }              // matches TemplateVariable.ResolverKey
    VariableKind Kind { get; }               // Static or Dynamic; distinct resolver per kind
    Task<string> ResolveAsync(ResolutionContext ctx, CancellationToken ct);
}

public sealed record ResolutionContext(
    Node Node,
    SystemVmObligation? Obligation,          // null for tenant flows
    VirtualMachine? Vm,                      // null for system flows
    VmTemplate Template,
    string OrchestratorUrl,
    string TargetArchitecture,
    IReadOnlyDictionary<string, string> UserSuppliedStatics);
```

Resolvers register at startup (`AddSingleton<IVariableResolver, FooResolver>()`).
Lookup is exact-match on `(ResolverKey, Kind)`. Static and Dynamic resolvers
are registered separately even when the variable name is the same — they
answer different questions.

### 3.2 The render pipeline

```
input:  template (raw YAML with __STATIC_PLACEHOLDERS__ and $DYNAMIC_REFERENCES)
        ResolutionContext

For each TemplateVariable v in template.Variables where v.Kind == Static:
    resolver = registry.Get(v.ResolverKey ?? v.Name, Static)
    if resolver is null:
        if v.Required and ctx.UserSuppliedStatics has no v.Name:
            throw — required user input missing
        value = ctx.UserSuppliedStatics.GetValueOrDefault(v.Name) ?? v.DefaultValue ?? throw
    else:
        value = await resolver.ResolveAsync(ctx)
    variables[$"__{v.Name}__"] = value

rendered = SubstituteCloudInitVariables(template.CloudInitTemplate, variables)
CloudInitValidator.Validate(rendered, template.Variables)
return rendered
```

### 3.3 Environment endpoint (node agent)

```
GET /api/obligations/{role}/environment

Loads template from local SQLite. For each TemplateVariable v in template.Variables
where v.Kind == Dynamic:
    resolver = nodeRegistry.Get(v.ResolverKey ?? v.Name, Dynamic)
    response.values[v.Name] = await resolver.ResolveAsync(nodeCtx)

response.scopes = { v.Name → v.Scope.ToString().ToLowerInvariant() for each dynamic }
response.generation = SHA256(values).Substring(0, 16)   // deterministic; bumps on any change
return response
```

The node agent has its own resolver registry mirroring the orchestrator's,
resolving from node-local state (`NodeMetadataService`, heartbeat-cached
`RelayInfo`, etc.). The endpoint binds to `192.168.122.1:5100` (virbr0,
same as artifact and identity endpoints).

### 3.4 Validator

```csharp
public static class CloudInitValidator
{
    public static void Validate(string rendered, IReadOnlyList<TemplateVariable> declared)
    {
        var declaredStatic  = declared.Where(v => v.Kind == VariableKind.Static).Select(v => v.Name).ToHashSet();
        var declaredDynamic = declared.Where(v => v.Kind == VariableKind.Dynamic).Select(v => v.Name).ToHashSet();

        var foundPlaceholders = Regex.Matches(rendered, @"__([A-Z][A-Z0-9_]*)__")
            .Select(m => m.Groups[1].Value).Distinct().ToList();

        var unresolvedStatics    = foundPlaceholders.Where(p => declaredStatic.Contains(p)).ToList();
        var dynamicsInWrongForm  = foundPlaceholders.Where(p => declaredDynamic.Contains(p)).ToList();
        var undeclared           = foundPlaceholders.Where(p => !declaredStatic.Contains(p)
                                                              && !declaredDynamic.Contains(p)).ToList();

        if (unresolvedStatics.Any())   throw new(...);
        if (dynamicsInWrongForm.Any()) throw new(...);
        if (undeclared.Any())          throw new(...);
    }
}
```

Three failure modes, each diagnosable from the message. Throws — no
warn-and-continue.

### 3.5 The watcher (in-VM)

One shell script, `/usr/local/bin/decloud-env-watcher.sh`, role-parameterized.
Runs every 60 seconds via systemd timer.

```bash
#!/bin/bash
ROLE="$1"
ENV_FILE="/etc/decloud-${ROLE}/environment"
SCOPE_FILE="/etc/decloud-${ROLE}/variable-scopes.conf"
ENDPOINT="http://192.168.122.1:5100/api/obligations/${ROLE}/environment"

declare -A SCOPES
while IFS='=' read -r k v; do SCOPES[$k]=${v%%#*}; done < <(grep -v '^#' "$SCOPE_FILE")

NEW=$(curl -sf --max-time 5 "$ENDPOINT") || exit 0
NEW_GEN=$(echo "$NEW" | jq -r '.generation')
OLD_GEN=$(grep -oP '^ENV_GENERATION=\K.*' "$ENV_FILE" 2>/dev/null || echo "0")
[ "$NEW_GEN" = "$OLD_GEN" ] && exit 0

WORST="noop"
for VAR in "${!SCOPES[@]}"; do
    OLD_VAL=$(grep -oP "^${VAR}=\K.*" "$ENV_FILE" 2>/dev/null || echo "")
    NEW_VAL=$(echo "$NEW" | jq -r ".values.\"${VAR}\" // empty")
    [ "$OLD_VAL" != "$NEW_VAL" ] && WORST=$(escalate "$WORST" "${SCOPES[$VAR]}")
done

write_env_file "$NEW" "$ENV_FILE"   # always rewrite — keeps file accurate for cold restart

case "$WORST" in
    reload)  systemctl reload  "decloud-${ROLE}" ;;
    restart) systemctl restart "decloud-${ROLE}" ;;
esac
```

Generic; same script for all roles. Lives in `system-vms/shared/assets/`.
The scope file is written at cloud-init render time from `template.Variables`
(via the `VARIABLE_SCOPES_BLOCK` static resolver).

---

## 4. Repository layout

### 4.1 `Shared`

Create:
- `src/Shared/Models/TemplateVariable.cs` — `TemplateVariable`, `VariableKind`, `WatcherScope`.

Modify:
- `src/Shared/Models/SystemVmTemplate.cs` — add `Variables: List<TemplateVariable>`.

### 4.2 `DeCloud.Orchestrator`

Create (organized by folder mirroring domain):

```
src/Orchestrator/Services/CloudInit/
├── IVariableResolver.cs
├── IVariableResolverRegistry.cs
├── VariableResolverRegistry.cs
├── ResolutionContext.cs
├── CloudInitRenderer.cs
├── CloudInitValidator.cs
├── TemplateComposer.cs                    (umbrella plan)
├── CloudInitFormatting.cs                  (BuildSshKeysBlock, BuildPasswordBlock, IndentForYaml)
└── Resolvers/
    ├── PlatformCommon/
    │   ├── VmIdResolver.cs
    │   ├── VmNameResolver.cs
    │   ├── HostnameResolver.cs
    │   ├── NodeIdResolver.cs
    │   ├── OrchestratorUrlResolver.cs
    │   ├── CaPublicKeyResolver.cs
    │   ├── SshAuthorizedKeysBlockResolver.cs
    │   ├── PasswordConfigBlockResolver.cs
    │   ├── ArtifactUrlResolver.cs
    │   ├── ArtifactSha256Resolver.cs
    │   ├── VariableScopesBlockResolver.cs
    │   └── TimestampResolver.cs
    ├── SystemVm/
    │   ├── Relay/
    │   │   ├── RelayWireGuardPrivateKeyResolver.cs   (Static)
    │   │   ├── RelaySubnetResolver.cs                (Static)
    │   │   ├── OrchestratorPublicKeyResolver.cs      (Static)
    │   │   ├── OrchestratorIpResolver.cs             (Static)
    │   │   ├── OrchestratorPortResolver.cs           (Static)
    │   │   ├── PublicIpResolver.cs                   (Static — relay-only; for relay this is identity)
    │   │   └── (no Dynamic resolvers — relay has no runtime-mutable values per §2.3)
    │   ├── Dht/
    │   │   ├── DhtListenPortResolver.cs              (Static, constant)
    │   │   ├── DhtApiPortResolver.cs                 (Static, constant)
    │   │   ├── DhtAdvertiseIpResolver.cs             (Dynamic)
    │   │   ├── DhtBootstrapPeersResolver.cs          (Dynamic)
    │   │   ├── DhtRegionResolver.cs                  (Dynamic)
    │   │   ├── WgRelayEndpointResolver.cs            (Dynamic)
    │   │   ├── WgRelayPubkeyResolver.cs              (Dynamic)
    │   │   ├── WgRelayApiResolver.cs                 (Dynamic)
    │   │   └── WgTunnelIpResolver.cs                 (Dynamic)
    │   └── BlockStore/
    │       └── (parallel structure to Dht)
    └── Tenant/
        ├── DecloudVmIdResolver.cs                    (Static — DECLOUD_VM_ID)
        ├── DecloudPasswordResolver.cs                (Static)
        ├── DecloudDomainResolver.cs                  (Static)
        ├── IngressUrlResolver.cs                     (Dynamic)
        └── (other DECLOUD_* and platform-bound dynamics)
```

Modify:
- `src/Orchestrator/Models/VmTemplate.cs` — add `Variables: List<TemplateVariable>` (default empty list for backward compat).
- `src/Orchestrator/Services/SystemVm/SystemVmTemplateSeeder.cs` — declare `Variables` per role; use `TemplateComposer.Compose`.
- `src/Orchestrator/Services/Tenant/GeneralVmTemplateSeeder.cs` (new file) — same pattern for `platform-general`.
- `src/Orchestrator/Services/NodeService.cs` — `BuildSystemVmTemplate` calls `CloudInitRenderer` and includes `Variables` in payload.
- `src/Orchestrator/Services/VmService.cs` — `AssignVmToNodeAsync` calls `CloudInitRenderer`. Drop the existing per-template-style ad-hoc variable building.
- `src/Orchestrator/Services/SystemVm/SystemVmObligationService.cs` — assign `VmId` and `VmName` at obligation creation.
- `src/Orchestrator/Models/SystemVmObligation.cs` — add `VmName: string?` field.
- `src/Orchestrator/Models/Node.cs` — add `SshCaPublicKey: string?` field; `NodeRegistrationRequest` likewise.

### 4.3 `DeCloud.NodeAgent`

Create:
- `src/DeCloud.NodeAgent/Controllers/ObligationEnvironmentController.cs` — `GET /api/obligations/{role}/environment`.
- `src/DeCloud.NodeAgent.Core/Models/ObligationEnvironment.cs` — response DTO.
- `src/DeCloud.NodeAgent/Services/CloudInit/IVariableResolver.cs` (node-side mirror).
- `src/DeCloud.NodeAgent/Services/CloudInit/Resolvers/SystemVm/Dht/*.cs` etc — node-side dynamic resolvers (mirror orchestrator structure).

Modify:
- `src/DeCloud.NodeAgent/OrchestratorClient.cs` — read `/etc/ssh/decloud_ca.pub` at registration time, send in `NodeRegistrationRequest`.
- `src/DeCloud.NodeAgent.Infrastructure/Services/SystemVm/SystemVmReconciler.cs` — `ActCreateAsync` becomes pure executor.
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` — delete role-specific blocks, `MergeCustomUserDataWithBaseConfig`, calls to `CloudInitTemplateService` (Phase 4 — not until verified).

Delete (Phase 4):
- `src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitTemplateService.cs`
- `src/DeCloud.NodeAgent/CloudInit/Templates/general/`
- `src/DeCloud.NodeAgent/CloudInit/Templates/decloud-agent-*.b64`

### 4.4 `DeCloud.Builds`

Create:
- `base-templates/base-tenant.yaml` — base layer for tenant VMs.
- `base-templates/base-system.yaml` — base layer for system VMs.
- `tenant-vms/general/cloud-init.yaml` — role layer (no base content).
- `tenant-vms/general/src/` — `decloud-agent` Go source moved here.
- `tenant-vms/general/assets/general-api.py`, `index.html` — moved here.
- `tenant-vms/compute-artifact-constants.sh` — sibling to system-vms script.
- `system-vms/shared/assets/decloud-env-watcher.sh` — generic watcher.
- `system-vms/shared/assets/decloud-env-watcher.service`, `.timer` — systemd units.

Modify:
- `system-vms/dht/cloud-init.yaml`, `system-vms/blockstore/cloud-init.yaml`, `system-vms/relay/cloud-init.yaml` — strip base content; fetch identity, fetch environment, write scope file, install watcher, source env file before service start, use `$VARNAME` shell references for dynamics.
- `.github/workflows/release-binaries.yml` — add `build-attestation-agent` job (mirror of `build-dht`).

---

## 5. Phases

### Phase 0 — `DeCloud.Builds` preparation

Pure infrastructure work; no code changes outside `DeCloud.Builds`. Can run
in parallel with Phase 1.

**0.1 Create `base-tenant.yaml`** — extract from current `general-vm-cloudinit.yaml`. Provides bootcmd (machine-id regen, snapd/LXD mask, qemu-guest-agent), packages, write_files for `decloud_ca.pub`, sshd hardening, systemd units for `decloud-agent` and welcome server, runcmd for artifact downloads and SSH CA setup.

**0.2 Create `base-system.yaml`** — extract from current role YAMLs. Provides bootcmd, packages (qemu-guest-agent, curl, jq, openssl, wireguard, wireguard-tools, nginx-light, python3), write_files for `wg-mesh-watchdog.{sh,service,timer}` and `wg-quick@wg-mesh` override.

**0.3 Strip base content from role YAMLs** — for each of dht, blockstore, relay: remove bootcmd, qemu-guest-agent package, wg-mesh-watchdog files, wg-quick override.

**0.4 Move tenant assets** — `general-api.py`, `index.html` from NodeAgent repo to `tenant-vms/general/assets/`.

**0.5 Move `decloud-agent` source** — Go source to `tenant-vms/general/src/`.

**0.6 Add `build-attestation-agent` CI job** — mirror of `build-dht`; produces `decloud-agent-amd64`/`-arm64` GitHub release artifacts.

**0.7 Create `tenant-vms/compute-artifact-constants.sh`** — sibling to `system-vms/`; or extend the existing script to walk both trees.

**0.8 Create `decloud-env-watcher.sh`** + systemd units in `system-vms/shared/assets/`.

**Acceptance:** Each base/role YAML composes cleanly with `TemplateComposer` (verified once `TemplateComposer` lands in Phase 1). Role YAMLs contain no base content. Watcher script syntax-checks (`bash -n`).

### Phase 1 — Shared infrastructure (orchestrator)

Lands all rendering, registry, and template-variable infrastructure. **No
behavioural change to live VMs in this phase** — legacy paths remain.

**1.1** `TemplateVariable` model + `VmTemplate.Variables` field (default empty).

**1.2** `TemplateComposer` — string-based section detection per umbrella plan §2.3. Merge rules: `packages` union/dedup, `write_files`/`bootcmd`/`runcmd` base-first then role, scalar keys role-wins.

**1.3** `IVariableResolver` interface + `VariableResolverRegistry` (DI singleton).

**1.4** Platform-common resolvers (one file each per §4.2 layout).

**1.5** `CloudInitRenderer` and `CloudInitValidator`.

**1.6** Add `Node.SshCaPublicKey` field; node agent reads `/etc/ssh/decloud_ca.pub` at registration; orchestrator stamps it.

**1.7** `SystemVmObligation.VmName` field. `EnsureObligationsAsync` and `TryAdoptExistingVmAsync` assign `VmId` and `VmName` at creation.

**Acceptance:**
- All 1.1–1.5 unit tests pass.
- `TemplateComposer.Compose(base-system, dht-role)` produces valid cloud-init.
- An ad-hoc test renders a synthetic template with one of each declared kind and validates correctly.
- Existing live VMs unaffected (legacy paths still active).

### Phase 2 — Tenant VM cutover

Tenant VMs (general + marketplace) use the new pipeline. Legacy node-side
processing remains for system VMs.

**2.1** Seed `platform-general` template via `GeneralVmTemplateSeeder`. Composes `base-tenant.yaml + tenant-vms/general/cloud-init.yaml`. Declares variables for the tenant set.

**2.2** `VmService.CreateVmAsync` — when `TemplateId` is null and `VmType == General`, assign `TemplateId = platform-general`. No "no template" code path remains.

**2.3** `VmService.AssignVmToNodeAsync` — replace the existing variable building with a call to `CloudInitRenderer.RenderAsync(template, ctx)`. The `CreateVm` command's `UserData` carries the rendered string.

**2.4** Tenant resolvers (`Resolvers/Tenant/*.cs`) — `DECLOUD_VM_ID`, `DECLOUD_PASSWORD`, `DECLOUD_DOMAIN`, etc. Note: existing tenant templates have no `Variables` declarations; legacy compatibility path treats them as "all referenced placeholders are statics, resolved by platform-common + `DefaultEnvironmentVariables`."

**2.5** Add the `EnsureQemuGuestAgent` and GPU-shim safety nets to `LibvirtVmManager.CreateCloudInitIso`'s post-substitution path (already present; verify they still run when `CloudInitUserData` is from the new pipeline).

**Acceptance:**
- Fresh general VM deploys via new pipeline. `decloud-agent` running, welcome server reachable, SSH CA set up.
- Marketplace template deploy succeeds. Existing templates work via legacy compatibility path.
- Existing system VMs unaffected (still on legacy path).

### Phase 3 — System VM cutover, one role at a time

**Order: relay → DHT → BlockStore.** This order matches the dependency
graph (DHT and BlockStore depend on a working relay; cutting over relay
first keeps the test-bed clean of cross-role interactions).

Each role's cutover is one release. Between releases: full system VM
testbed run, plus 24-48 hours of production observation. Rollback per role
is straightforward — the legacy node-side path remains live until Phase 4.

#### 3.1 Per-role cutover steps (apply for each of relay, DHT, BlockStore)

**3.1.1** Update `SystemVmTemplateSeeder` for this role:
- Compose cloud-init via `TemplateComposer.Compose("base-system", "{role}")`.
- Declare `Variables` (statics + dynamics with scopes per §2.3).
- Bump template revision.

**3.1.2** Register role-specific resolvers (one file each per §4.2 layout).

**3.1.3** Implement node-side dynamic resolvers for this role.

**3.1.4** Update the role's cloud-init YAML in `DeCloud.Builds`:
- Use `__VARIABLE_SCOPES_BLOCK__` placeholder (resolver fills it at render time).
- Fetch identity from `/api/obligations/{role}/state` (existing pattern).
- Fetch environment from `/api/obligations/{role}/environment` (new).
- Write `/etc/decloud-{role}/environment` and `/etc/decloud-{role}/variable-scopes.conf`.
- Install + enable `decloud-env-watcher.timer`.
- Service unit uses `EnvironmentFile=/etc/decloud-{role}/environment`.
- Use `$VARNAME` shell references for dynamics in scripts/configs.

**3.1.5** Generic environment endpoint on node agent: lands in 3.1 (relay cutover) since the controller is shared. Subsequent role cutovers register more dynamic resolvers on the node side; the controller code doesn't change.

**3.1.6** `SystemVmReconciler.ActCreateAsync` (lands once with relay cutover, applies to all roles after): becomes pure executor — reads `obligation.VmId`/`VmName`, reads cached rendered cloud-init from local SQLite, prefetches artifacts, builds `VmSpec`, calls `_vmManager.CreateVmAsync`. No substitution. No local Id/Name generation. Labels reduced to `system-vm-role`, `system-vm-revision`, `node-arch`.

#### 3.2 Per-role acceptance

For each role, before moving to the next:

- Fresh deploy via new pipeline succeeds. VM reaches Ready.
- All declared statics in the cloud-init render correctly (no `__FOO__` remnants other than `__WG_TUNNEL_IP__` which is declared dynamic).
- `/etc/decloud-{role}/environment` populated correctly at boot.
- Watcher timer running. Mutate one of each scope category in turn (see §7.5):
  - **Restart-scoped** mutation triggers service restart within 60s; values reflected.
  - **Reload-scoped** mutation triggers service reload within 60s; values reflected.
  - **Noop-scoped** mutation rewrites env file but no service action.
- Recovery test: `virsh destroy` + undefine, reconciler redeploys, identity preserved.
- 24-48 hours production observation, no regressions.

### Phase 4 — Cleanup

Only after all three system VMs have been on the new path for one full
release cycle without regressions.

**4.1** Delete `LibvirtVmManager.PrepareCloudInitVariables` and its role-specific blocks (5.5/5.6/5.7).

**4.2** Delete `MergeCustomUserDataWithBaseConfig` in `LibvirtVmManager`. `CreateCloudInitIso` collapses to:
```csharp
var cloudInitYaml = spec.CloudInitUserData;
cloudInitYaml = EnsureQemuGuestAgent(cloudInitYaml);
cloudInitYaml = InjectGpuProxyShim(cloudInitYaml, spec);
// generate ISO, attach
```

**4.3** Delete `CloudInitTemplateService.cs` and its DI registration.

**4.4** Delete `src/DeCloud.NodeAgent/CloudInit/Templates/general/` and `decloud-agent-*.b64` files.

**4.5** Final testbed sweep: tenant + system VM deploys all pass. No remaining references to deleted classes.

---

## 6. Sequencing summary

```
Phase 0  ─┐
          ├─→  Phase 1 (shared infra)
Phase 0  ─┘         │
                    ├─→  Phase 2 (tenant cutover)         ─┐
                    │                                       │
                    ├─→  Phase 3.1 (relay cutover)         │
                    │           │                           ├─→  Phase 4 (cleanup)
                    │           ├─→  Phase 3.2 (DHT)        │
                    │           │           │               │
                    │           │           ├─→  Phase 3.3 (BlockStore) ─┘
```

Phase 0 and Phase 1 are independent. Phase 2 and Phase 3 are independent
of each other (tenant and system flows touch different code paths once
shared infra lands). Within Phase 3, roles cut over sequentially.

Rollback is per-phase, per-role. Until Phase 4 deletes the legacy paths,
any cutover can be reverted by reverting the seeder and YAML changes.

---

## 7. Validation & test plan

### 7.1 Unit tests (Phase 1)

- `TemplateComposer`: base-only, role-only, both packages (union dedup), both runcmd (base first), role hostname wins, neither has section, etc.
- Each resolver: synthetic `ResolutionContext`, expected output, throws on missing required input.
- `CloudInitRenderer`: declared static missing resolver → throws; required user input missing → throws; renders correctly when complete.
- `CloudInitValidator`: unresolved static, undeclared placeholder, dynamic-in-wrong-form — all three throw with diagnosable messages.

### 7.2 Tenant integration test (Phase 2)

- Fresh general VM: deploys, attests, welcome server reachable, SSH CA login works.
- Marketplace VM with declared `Variables`: renders correctly, all statics resolved.
- Marketplace VM without declared `Variables` (legacy path): still works.

### 7.3 System VM integration test (per role, Phase 3)

- Fresh deploy. Cloud-init log clean. Service Ready.
- Rendered cloud-init has no `__PLACEHOLDER__` remnants except `__WG_TUNNEL_IP__`.
- Identity fetch works. Environment fetch works. Scope file populated correctly.

### 7.4 Recovery test (per role)

`virsh destroy` + undefine. Reconciler reads cached rendered template from local SQLite (no re-pull needed unless template revision bumped). VM boots with same identity (peer ID preserved). Same `obligation.VmId`/`VmName`.

### 7.5 Environment-drift test (per role) — the critical new test

Without bumping any template revision, mutate underlying state on the
orchestrator and verify the watcher behaves correctly:

- **Restart-scoped variable** (e.g., `WG_RELAY_ENDPOINT` for DHT): simulate relay failover. Within 60s, watcher detects, env file rewritten, service restarted, new endpoint in use. **No redeploy.**
- **Reload-scoped variable**: if the role declares any (current scope tables don't, but future ones may): mutate, verify SIGHUP delivered, no socket reset.
- **Noop-scoped variable** (e.g., `DHT_BOOTSTRAP_PEERS`): mutate. Within 60s, env file rewritten. Confirm by inspection. No service signal sent. Confirm bootstrap-poll script picks up the new value on its next cycle (DHT path only).

### 7.6 Scope-correctness audit (per role)

For each declared dynamic, confirm with the binary owner that the declared
scope matches actual binary behaviour. Use the diagnostics endpoint or log
inspection to verify:
- Restart-scoped: did the binary actually rebind / re-init the relevant subsystem?
- Reload-scoped: did the binary actually re-read the value? (Many won't — escalate to Restart.)
- Noop-scoped: is it actually safe to ignore? Did some downstream consumer pick up the new value via its own mechanism?

Corrections bump the template revision and trigger re-render on next pull.

### 7.7 Template-revision invalidation test

Bump a template revision (e.g., add a new artifact). Heartbeat returns role
in `systemTemplatesPending`. Node pulls fresh; orchestrator renders fresh.
Reconciler redeploys the role. New `Variables` list, new scope policy,
new rendered cloud-init.

---

## 8. Out of scope

- Author-resolver webhooks (tenant authors providing custom Dynamic resolution endpoints) — possible future extension.
- Converting `relay-api.py` to read environment variables instead of having values substituted into the `.py` source. Would expand relay's runtime-mutable surface; deferrable.
- Generalizing the watcher to support actions beyond `noop`/`reload`/`restart` (e.g., "regenerate metadata file then restart"). Current design covers all roles' needs; extend only if a future role requires it.
- Removing `Labels` from `VmRecord` entirely. Only its substitution-side-channel role disappears.
- Tenant VM re-rendering on `node.PublicIp` or other host-state change. Tenant VMs that need this should declare the relevant value as `Dynamic`; existing marketplace templates can opt in incrementally.

---

## 9. Anti-patterns to avoid

- **Resolvers returning `""` on missing required input.** Throw with a clear message. Empty strings become silent runtime failures.
- **Hard-coding scope policy outside the template.** Scope is template metadata. The watcher reads the scope file. Never hard-code scopes in C# or in `DeCloud.Builds` config files.
- **Special-casing `__WG_TUNNEL_IP__` in the validator.** It's a declared dynamic. The validator consults the template; nothing is hard-coded.
- **Fallback chains in resolvers** (`labels?.GetValueOrDefault(x) ?? metadata.X ?? ""`). Resolvers take typed inputs from `ResolutionContext` and throw on missing required data.
- **Caching rendered cloud-init on the obligation document.** Render is fresh on each pull. The earlier draft proposed caching; rejected.
- **Mixing static and dynamic resolvers in one class.** They answer different questions; keep them separate. Better diagnostics, easier testing.
- **Treating the legacy node-side path as "fine for now."** It is being deleted in Phase 4. Don't add new code there — fix the issue at the resolver or template layer.

---

## 10. Definition of done

### Phase 0
- [ ] `base-tenant.yaml`, `base-system.yaml` exist and contain no role-specific content.
- [ ] Role YAMLs contain no base content.
- [ ] `decloud-agent` Go source in `DeCloud.Builds`; CI job builds and releases the binary.
- [ ] `decloud-env-watcher.sh` + systemd units in `system-vms/shared/assets/`.

### Phase 1
- [ ] `TemplateVariable`, `TemplateComposer`, `IVariableResolverRegistry`, `CloudInitRenderer`, `CloudInitValidator` shipped with full unit-test coverage.
- [ ] All platform-common resolvers registered.
- [ ] `Node.SshCaPublicKey` populated on registration.
- [ ] `SystemVmObligation.VmName` field added; obligations carry both `VmId` and `VmName` from creation.
- [ ] Existing live VMs unaffected.

### Phase 2
- [ ] `platform-general` seeded; new general VMs use it.
- [ ] `VmService.AssignVmToNodeAsync` uses `CloudInitRenderer`.
- [ ] Fresh general VM and marketplace VM deploys succeed via new pipeline.

### Phase 3 (per role)
- [ ] Role's template declares full `Variables` set with starting scopes per §2.3.
- [ ] Role's resolvers registered (orchestrator and node sides).
- [ ] Role's cloud-init YAML in `DeCloud.Builds` updated: identity-fetch, environment-fetch, watcher install.
- [ ] Generic environment endpoint serves declared dynamics for this role.
- [ ] Role deploys cleanly via new pipeline. Recovery and environment-drift tests pass (§7.4, §7.5).
- [ ] §7.6 scope-correctness audit complete for this role; corrections applied.
- [ ] 24-48 hours production observation, no regressions.

### Phase 4
- [ ] All three roles on new path for one full release cycle.
- [ ] `LibvirtVmManager.PrepareCloudInitVariables`, `MergeCustomUserDataWithBaseConfig`, `CloudInitTemplateService` deleted.
- [ ] `CloudInit/Templates/` and `decloud-agent-*.b64` files deleted from NodeAgent repo.
- [ ] Final testbed sweep clean.

After all phases: adding a new system VM role requires authoring a template (declare `Variables`, write the role cloud-init YAML in `DeCloud.Builds`) and registering any resolvers the role's variables need that aren't already registered. Zero changes to `LibvirtVmManager`, `SystemVmReconciler`, or the environment endpoint controller.

---

## 11. Open questions resolved

| Question | Resolution |
|---|---|
| `__ORCHESTRATOR_URL__` source? | `OrchestratorUrlResolver` reads `IConfiguration["OrchestratorClient:BaseUrl"]` with co-located guard (substitute `node.PublicIp` if host is localhost). |
| Resolver inputs? | `ResolutionContext` — `Node`, `Obligation` (with deserialized `StateJson`), `Template`, etc. No labels lookup, no fallback chains. |
| Orchestrator's WireGuard public key? | `IWireGuardManager.GetOrchestratorPublicKeyAsync()`. Inject into `OrchestratorPublicKeyResolver`. |
| `ObligationRole` (Shared) vs `SystemVmRole` (Orchestrator)? | Use `SystemVmRole` enum inside the orchestrator. Convert at the wire boundary via `SystemVmRoleMap`. Resolver keys use canonical strings. |
| Per-variable scopes? | Starting set declared per role in §2.3; verified by §7.6 audit during Phase 3. |
| Backward compatibility for existing marketplace templates? | Templates with no `Variables` list use legacy compatibility: all referenced placeholders treated as statics, resolved by platform-common resolvers + `DefaultEnvironmentVariables`. Migration is opt-in per template. |
| `VmName` field on `SystemVmObligation`? | **Add it.** Nullable string for backwards compat with obligations created before this change. Set by orchestrator at obligation creation. |

---

## 12. Handoff log expectations

Maintain a brief log per phase. One entry per step: what you did, what
surprised you, what you deferred, test results.

Departures from this plan: log the rationale. The next person to touch
this code needs to know what was deliberate.

---

## 13. Contact and escalation

If you find a contradiction between this plan and the live code: read the
live code, decide which is correct, log the decision.

If you find an ambiguity neither this plan nor the code resolves: surface
it; do not silently pick.

If you find yourself wanting to bake a value derived from `node.PublicIp`,
`node.Region`, `node.RelayInfo`, or any heartbeat-cached value into a
`Static` variable: **stop.** That is the boundary this work corrects. Re-read
§2.1 and §9. The variable should be declared `Dynamic`.

If you find yourself wanting to hard-code scope policy somewhere outside
the template: **stop.** Scope is template data. Declare it on the variable.