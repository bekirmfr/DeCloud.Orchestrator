# VM Cloud-init Pipeline — Unified Design

**Target audience:** Engineers extending or maintaining the pipeline.
**Status:** **SHIPPED 2026-05-07.** Reference material.

> **Migration closure.** This document was the design contract for the unified
> cloud-init pipeline migration. The migration is complete: Phases 0–4 closed;
> validated end-to-end on relay node srv022010 and CGNAT node MSI; system VMs
> (Relay v6.2, DHT v8.3, BlockStore v4.5), general tenant VMs, and marketplace
> template VMs all deploy cleanly. Net deletion: ~470 lines of legacy
> substitution / merge / template-service code on the node side.
>
> §1–§9 capture the architecture and design rationale and remain authoritative.
> §10 (definition of done) is a closed scoreboard. §14 (added at closure) is
> the final-state architecture reference — start there if you're picking up
> this code cold.
>
> The companion document `UNIFIED_CLOUDINIT_PIPELINE_IMPLEMENTATION_PLAN.md`
> tracks the full execution log, decision history, and per-task acceptance.

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
system, marketplace, or general — needs to run. **Achieved 2026-05-07:**

- The orchestrator produces a complete cloud-init document at scheduling
  time (tenant flow) or pull time (system flow). The cloud-init contains
  only values stable for the life of the VM. `CloudInitRenderer` is the
  single render authority across all VM types.
- Mutable environmental values (advertise IP, bootstrap peers) are served
  from the node-local `/api/obligations/{role}/environment` endpoint and
  refreshed in-VM by `decloud-env-watcher.sh`. Environmental drift is
  handled by VM service restart, not VM redeploy.
- The node agent does no role-specific substitution and no template
  processing. `LibvirtVmManager.CreateCloudInitIsoAsync` collapses to
  taking `spec.CloudInitUserData` as-is, running a defensive variables-
  Replace pass, and an unreplaced-placeholder leak detector. A single
  deferred substitution remains: `__VM_ID__`, performed by
  `SystemVmReconciler.ActCreateAsync` after the libvirt UUID is minted.
- Adding a new system VM role, or a new marketplace template variable,
  is data — not code — outside one resolver registration.

This is **not a feature**. It is a layered boundary correction across
three boundaries simultaneously: orchestrator/node, deploy-time/runtime,
and template-data/orchestrator-code. Each prior boundary draw produced a
class of bugs the next boundary draw fixes.

---

## 1. Required reading

For context on the obligation lifecycle and identity-fetch pattern:

1. **`SYSTEM_VM_RESILIENCE_DESIGN.md`** — context on the obligation lifecycle.
   The pull-on-stale primitive (`systemTemplateVersions` /
   `systemTemplatesPending`) and the boot-time identity-fetch pattern (§4.8)
   are both load-bearing here.
2. **`SYSTEM_VM_LIFECYCLE_FLOW_MAP.md`** — concrete data flow.

If you read only one section deeply, read this document's §3 (the
template-driven render/runtime/watcher trio) and §14 (the post-shipping
final-state summary).

---

## 2. Decisions — do not re-litigate

All decisions below shipped as designed unless a "Shipped reality" note follows.

| Decision | Status |
|---|---|
| **Templates declare their variables** as first-class structured metadata | Shipped. New `VmTemplate.Variables: List<TemplateVariable>` field. Drives rendering, environment endpoint, watcher. See §2.2. |
| **Two variable kinds: Static, Dynamic** | Shipped. Statics resolve at render time and bake into cloud-init. Dynamics resolve at runtime via the environment endpoint and watcher. |
| **Three watcher scopes: Noop, Reload, Restart** | Shipped. Declared per dynamic variable. Watcher applies the max scope across all changed variables in a poll cycle. See §2.3 for concrete scope policy per role. |
| **Single render pipeline** for tenant + system VMs | Shipped. One `CloudInitRenderer`, driven by `template.Variables` and a `IVariableResolverRegistry`. No separate system/tenant renderers. |
| **Resolver registry replaces ad-hoc per-role contributors** | Shipped. Each variable is resolved by exactly one registered resolver. Resolvers organized by domain in folder structure (§4.2). The renderer is generic. |
| **Render fresh on each pull (system VMs)** | Shipped. Existing pull endpoint (`GET /api/nodes/me/system-templates/{role}`) renders on demand. No `RenderedCloudInit` cache field on the obligation. |
| **Render at scheduling (tenant VMs)** | Shipped. `VmService.AssignVmToNodeAsync` calls `CloudInitRenderer.Render` and ships the result in the `CreateVm` command's `UserData`. |
| **Delivery via existing primitives** | Shipped. System VMs: existing `systemTemplatesPending` flow, wire format unchanged (`SystemVmTemplate.CloudInitContent` carries rendered output). Tenant VMs: existing `CreateVm` command's `UserData` field. |
| **Environment endpoint is generic** | Shipped. `GET /api/obligations/{role}/environment` returns values for exactly the dynamic variables the role's template declares. New roles need no new endpoint code. |
| **Watcher is generic** | Shipped. One shell script (`decloud-env-watcher.sh`), role-parameterized. Reads scope policy from `/etc/decloud-{role}/variable-scopes.conf` baked at cloud-init render time. Small role-aware extension at P0.8: when any `WG_*` variable changes, watcher additionally restarts `wg-quick@wg-mesh` alongside the role service (only if the unit exists — relay safe). |
| **`__WG_TUNNEL_IP__` is a declared dynamic** | **Shipped reality:** demoted to Static during P3.2.4 audit alongside the other 3 WG_* variables. Runtime-managed by `wg-config-fetch.sh` writing `/etc/decloud/wg-mesh.env` directly at boot; orchestrator-render-time value never reaches the binary's runtime. The original "declared dynamic" decision was correct in design intent (the value is mutable) but redundant in implementation (the value is already runtime-managed by another path). See §2.3. |
| **`LibvirtVmManager.PrepareCloudInitVariables` deletes** | Shipped (P4.1, 2026-05-06). STEP 5.5/5.6/5.7 removed, 147 lines. `CreateCloudInitIsoAsync` post-P4 takes `spec.CloudInitUserData` as-is + defensive variables-Replace pass + leak detector. `EnsureQemuGuestAgent` and GPU-shim safety nets retained. |
| **`MergeCustomUserDataWithBaseConfig` deletes** | Shipped (P4.2, 2026-05-06). ~145 lines removed. The orchestrator's `CloudInitRenderer` is the authoritative substitution layer; node-side merge of `bootcmd`/`hostname`/`ssh_authorized_keys` was a no-op for orchestrator-rendered cloud-init. Call site collapses to `cloudInitYaml = spec.CloudInitUserData;`. |
| **`CloudInitTemplateService` deletes** | Shipped (P4.3, 2026-05-06). The legacy node-side template service is gone. The else branch in `CreateCloudInitIsoAsync` (taken when `spec.CloudInitUserData` is null) now throws `InvalidOperationException` with a message pointing at the originating CreateVm caller. Risk accepted: bold cutover instead of telemetry-first observation. |
| **CA public key sourced from `Node.SshCaPublicKey`** | Shipped. Stored on the node record at registration. No node-side disk read at deploy time. |
| **Base layer / role layer split** | Shipped. `base-tenant.yaml`, `base-system.yaml`, `base-system-mesh.yaml` (split out for mesh participants — relay does not participate in the mesh) provide infrastructure primitives. Role layers add role-specific content. `TemplateComposer` merges. |
| **General VMs always have `TemplateId`** | Shipped. A `platform-general` template is seeded; `VmService.CreateVmAsync` assigns it when no `TemplateId` is provided. No "no-template" code path. |
| **`SystemVmObligation` carries `VmId` and `VmName`** | Shipped. `VmName` field added to obligation model. Both fields assigned at obligation creation by the orchestrator. |
| **Single deferred placeholder: `__VM_ID__`** | Shipped (BLOCKSTORE-FIX §6, 2026-05-06). `VmId` pre-assignment removed from `NodeService.ReconcileNodeAsync` and `SystemVmObligationService.TryAdoptExistingVmAsync`. The libvirt UUID is minted on the node, and `SystemVmReconciler.ActCreateAsync` substitutes `__VM_ID__` in the rendered cloud-init via a single `Replace`. Heartbeat-based adoption is the live convergence path; role-specific self-heal callbacks are belt-and-suspenders. |
| **Consumer-render convention split: `__VARNAME__` vs `{{VARNAME}}`** | Adopted P4.5 (2026-05-07). `__VARNAME__` is the orchestrator render-time syntax inside cloud-init bodies. `{{VARNAME}}` is the consumer render-time syntax inside artifact bytes (HTML/JS files served from VMs). DHT and BlockStore dashboard `_SUBSTITUTIONS` dicts predate the convention and use `__VARNAME__` for both purposes — harmless because their dictionaries are scoped to artifact serving and never collide with orchestrator-render placeholders. Documented in `DeCloud.Builds/README.md`. |
| **Sequencing: shared first, then one role at a time** | Shipped as designed. Shared infrastructure (Phase 1) landed with all VMs still on the legacy path. Then tenant VMs cut over (Phase 2). Then system VMs cut over one role at a time: relay → DHT → BlockStore (Phase 3). Legacy code deletion last (Phase 4). See §6. |

If during future maintenance you find yourself wanting to revisit one of these — stop. Surface in a session log; do not silently change.

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
  supply values. Examples: `ADVERTISE_IP`, `BOOTSTRAP_PEERS`, `INGRESS_URL`
  (tenant).

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

### 2.3 Concrete scope policy per role (post-audit, shipped)

The original design declared 7 dynamics for DHT and parallel ones for
BlockStore. The §7.6 scope-correctness audits (P3.2.4 closed 2026-05-04,
P3.3.4 closed 2026-05-06) re-classified 5 of the 7 to Static for each
mesh role, on the grounds that the underlying values are runtime-managed
by `wg-config-fetch.sh` (for the WG_* variables) or never observed by
the binary post-boot (for the region variable). The watcher pipeline
ships with 2 dynamics per mesh role, not 7.

**DHT** (`dht-node` Go binary, reads `/etc/decloud-dht/dht.env` and
`/etc/decloud-dht/environment` at startup via two systemd `EnvironmentFile=`
directives — second directive wins on key collision; statics in `dht.env`,
dynamics in `environment`):

| Variable | Kind | Scope / Default | Reasoning |
|---|---|---|---|
| `DHT_ADVERTISE_IP` | Dynamic | Restart | libp2p multiaddr is bound at host startup; can't rebind without restart. Initial value set at boot by `wg-config-fetch.sh` writing `DHT_ADVERTISE_IP=...` into `/etc/decloud-dht/environment`; subsequent changes flow through `decloud-env-watcher.sh`. |
| `DHT_BOOTSTRAP_PEERS` | Dynamic | Noop | `dht-bootstrap-poll.sh` already re-sources env each iteration; libp2p has organic peer discovery once joined. Watcher rewrites `environment` but takes no service action. |
| `DHT_REGION` | Static | DefaultValue="default" | Demoted from Dynamic Noop. Binary has no mechanism to observe runtime region changes; consumed only by `dht-metadata.json` written once at boot. Cosmetic; fixed by next redeploy. |
| `WG_RELAY_ENDPOINT` | Static | DefaultValue="" | Demoted from Dynamic Restart. Runtime-managed by `wg-config-fetch.sh` polling `/api/obligations/{role}/wg-config`, which writes `/etc/decloud/wg-mesh.env` directly. Orchestrator-render-time value never reaches the binary's runtime. |
| `WG_RELAY_PUBKEY` | Static | DefaultValue="" | Same as `WG_RELAY_ENDPOINT`. |
| `WG_RELAY_API` | Static | DefaultValue="" | Same as `WG_RELAY_ENDPOINT`. |
| `WG_TUNNEL_IP` | Static | DefaultValue="" | Same as `WG_RELAY_ENDPOINT`. Note: the original design declared this as the canonical example of a Dynamic; the demotion is a correction not an exception, because the value is runtime-managed by `wg-config-fetch.sh`, not by the watcher. |

**Final shipped Variables list for DHT:** 12 platform-common statics + 5
DHT-specific statics (incl. `WG_DESCRIPTION`, `DECLOUD_ROLE`, port defaults)
+ 4 WG_* demoted statics + 1 `VARIABLE_SCOPES_BLOCK` static + 2 dynamics
= 24 entries. (The original 24-count target survived the audit; the
re-classification swapped 5 dynamics for statics within that count.)

The 4 WG_* demotions reflect the existing `wg-config-fetch.sh` runtime-management
path; routing them through the watcher pipeline would require restructuring
`wg-mesh-enroll.sh` and `wg-mesh.env`, with no functional gain because the
existing path already works. Tracked as a future refactor candidate (§14.4 #5).

**BlockStore** (`blockstore-node`, same shape as DHT):

| Variable | Kind | Scope / Default | Reasoning |
|---|---|---|---|
| `BLOCKSTORE_ADVERTISE_IP` | Dynamic | Restart | Same constraint as DHT — libp2p bind is once-at-startup. |
| `BLOCKSTORE_BOOTSTRAP_PEERS` | Dynamic | Noop | Same as DHT. `blockstore-bootstrap-poll.sh` re-sources env each iteration. |
| `NODE_REGION` (renamed from `DHT_REGION` in BlockStore template) | Static | DefaultValue="default" | Same demotion as DHT's region. |
| `WG_RELAY_ENDPOINT`, `WG_RELAY_PUBKEY`, `WG_RELAY_API`, `WG_TUNNEL_IP` | Static | DefaultValue="" | Same demotions as DHT — runtime-managed by `wg-config-fetch.sh`. |

**Final shipped Variables list for BlockStore:** 12 platform-common statics
+ 5 BlockStore-specific statics (`BLOCKSTORE_LISTEN_PORT`, `BLOCKSTORE_API_PORT`,
`BLOCKSTORE_NODE_ID`, `BLOCKSTORE_VM_ID`, `HOST_MACHINE_ID`) + 4 WG_* demoted
statics + 1 `VARIABLE_SCOPES_BLOCK` + 2 dynamics = 24 entries. Mirrors DHT's
structure with BlockStore-specific identity / port set.

**Relay** (`relay-api.py`, currently substitutes values into the `.py`
file at cloud-init time rather than reading env vars):

The relay's runtime-mutable surface is genuinely small. `RELAY_REGION`,
`RELAY_CAPACITY`, `RELAY_NAME` are used only for dashboard display; the
WG private key and allocated subnet never change post-deploy without
explicit identity rotation (which is a redeploy event). Public IP changes
affect cgnat-node WG configs, but those are managed by the orchestrator
through the relay-API peer-registration channel — not via env vars in the
relay VM itself.

| Variable | Kind | Scope | Reasoning |
|---|---|---|---|
| `RELAY_REGION` | Static | Noop | Cosmetic only; baked into `relay-api.py` at cloud-init time. Converting `relay-api.py` to read env vars is out of scope. |
| `RELAY_CAPACITY` | Static | Noop | Same as above. |

The relay role declares zero dynamics. Its variables are entirely `Static`.
The watcher runs on relay VMs but has nothing to do — `variable-scopes.conf`
is empty save for a comment marker. That is correct: the relay VM is, by
design, a thin WireGuard wrapper with most of its mutable state managed
externally.

These were the **starting** scope assignments verified by the §7.6
scope-correctness audit during Phase 3. All audits closed during Phase 3
(2026-05-04 / 2026-05-06).

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

The renderer runs three passes per template (Pass 3 is silently skipped if
the optional validator is not registered):

```
input:  template (raw YAML with __STATIC_PLACEHOLDERS__ and $DYNAMIC_REFERENCES)
        ResolutionContext

Pass 1 — Static resolution:
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

Pass 2 — Artifact substitution:
For each ${ARTIFACT_URL:name} or ${ARTIFACT_SHA256:name} in rendered:
    artifact = template.Artifacts.First(a => a.Name == name && a.Architecture == ctx.TargetArchitecture)
    Substitute with http://192.168.122.1:5100/api/artifacts/{artifact.Sha256}
        or the SHA itself
(Artifact references aren't declared in template.Variables — they're a
parameterized substitution mechanism distinct from __VARNAME__.)

Pass 3 — Validation (if ICloudInitValidator registered):
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

**Shipped reality:** the per-role dynamic resolvers live as an inline
`(role, name)` switch inside `ObligationEnvironmentController.GetEnvironment`
rather than as separate classes per dynamic. The cardinality is low (2
dynamics × 2 mesh roles = 4 inline cases) and the resolution logic is
trivial (read the cached `RelayInfo` field; relay has no dynamics). The
classes-per-resolver layout described in §4.2 is the orchestrator side
only — the node side is a single controller with a small switch. If the
dynamics count grows or resolution gets more involved, refactor to the
class-per-resolver shape.

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
warn-and-continue. The shipped implementation accumulates all three
buckets before throwing so a template with multiple kinds of error gets
fixed in one round trip rather than playing whack-a-mole.

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

**Shipped extension:** when any `WG_*` variable changes (dynamic or static
demotion path), the watcher additionally restarts `wg-quick@wg-mesh`
alongside the role service, but only if the unit exists (relay-safe). This
reduces failover window from 60–120s to ~60s. Documented in P0.8.

**Generation-hash fast path** (architectural detail validated during P3.2.5):
the generation comparison at the top of the script is a fast-path skip — if
the local `ENV_GENERATION` matches the live endpoint's generation, no
per-variable diff is performed and the watcher exits silently. Tampering only
the value (without bumping the generation marker) leaves the fast-path intact
and the watcher correctly does nothing. The dual-tamper drift test (§7.5)
exercises the slow path by mutating both the generation marker and the value.

---

## 4. Repository layout

### 4.1 `Shared`

Created:
- `src/Shared/Models/TemplateVariable.cs` — `TemplateVariable`, `VariableKind`, `WatcherScope`.

Modified:
- `src/Shared/Models/SystemVmTemplate.cs` — added `Variables: List<TemplateVariable>`.

### 4.2 `DeCloud.Orchestrator`

Created (organized by folder mirroring domain):

```
src/Orchestrator/Services/CloudInit/
├── IVariableResolver.cs
├── IVariableResolverRegistry.cs
├── VariableResolverRegistry.cs
├── ResolutionContext.cs
├── CloudInitRenderer.cs
├── CloudInitValidator.cs
├── TemplateComposer.cs                    (composes base + role layers)
├── CloudInitFormatting.cs                  (BuildSshKeysBlock, BuildPasswordBlock, IndentForYaml)
└── Resolvers/
    ├── PlatformCommon/                    (15 shipped resolvers — count expanded from
    │   │                                   plan's 10 to cover all placeholders found in
    │   │                                   live base/role YAMLs)
    │   ├── VmIdResolver.cs
    │   ├── VmNameResolver.cs
    │   ├── HostnameResolver.cs
    │   ├── NodeIdResolver.cs
    │   ├── OrchestratorUrlResolver.cs
    │   ├── CaPublicKeyResolver.cs
    │   ├── SshAuthorizedKeysBlockResolver.cs
    │   ├── PasswordConfigBlockResolver.cs
    │   ├── SshPasswordAuthResolver.cs
    │   ├── AdminPasswordResolver.cs
    │   ├── DeCloudRoleResolver.cs
    │   ├── HostMachineIdResolver.cs
    │   ├── PublicIpResolver.cs
    │   ├── VariableScopesBlockResolver.cs
    │   └── TimestampResolver.cs
    │   (Note: ArtifactUrlResolver and ArtifactSha256Resolver from the
    │   original plan were not implemented — artifact substitution uses
    │   parameterized syntax `${ARTIFACT_URL:name}` and is handled in the
    │   renderer's Pass 2, not via the resolver registry.)
    ├── SystemVm/
    │   ├── Relay/
    │   │   ├── RelayWireGuardPrivateKeyResolver.cs   (Static)
    │   │   ├── RelaySubnetResolver.cs                (Static)
    │   │   ├── OrchestratorPublicKeyResolver.cs      (Static)
    │   │   ├── OrchestratorIpResolver.cs             (Static)
    │   │   ├── OrchestratorPortResolver.cs           (Static)
    │   │   ├── RelayCapacityResolver.cs              (Static)
    │   │   ├── RelayRegionResolver.cs                (Static)
    │   │   └── (no Dynamic resolvers — relay has no runtime-mutable values per §2.3)
    │   ├── Dht/
    │   │   ├── DhtRegionResolver.cs                  (Static — demoted from Dynamic per audit)
    │   │   └── (DHT-specific port and identity values use DefaultValue
    │   │        on the TemplateVariable; no separate resolver classes
    │   │        needed beyond DhtRegionResolver and the WG_* statics
    │   │        which use shared resolvers)
    │   └── BlockStore/
    │       ├── BlockStoreRegionResolver.cs           (Static)
    │       └── (parallel structure to Dht)
    └── Tenant/
        └── (Resolvers added incrementally as marketplace templates
             declare Variables — most existing templates use the legacy
             compatibility path that doesn't require explicit resolvers)
```

Modified:
- `src/Orchestrator/Models/VmTemplate.cs` — added `Variables: List<TemplateVariable>` (default empty list for backward compat).
- `src/Orchestrator/Services/SystemVm/SystemVmTemplateSeeder.cs` — declares `Variables` per role; uses `TemplateComposer.Compose`.
- `src/Orchestrator/Services/SystemVm/SystemVmTemplateSeeder.Artifacts.cs` (auto-generated) — SHA256 constants regenerated by `system-vms/compute-artifact-constants.sh`.
- `src/Orchestrator/Services/Tenant/GeneralVmTemplateSeeder.cs` (new file) — same pattern for `platform-general`.
- `src/Orchestrator/Services/NodeService.cs` — `BuildSystemVmTemplate` calls `CloudInitRenderer` and includes `Variables` in payload.
- `src/Orchestrator/Services/VmService.cs` — `AssignVmToNodeAsync` calls `CloudInitRenderer`. Dropped existing per-template-style ad-hoc variable building.
- `src/Orchestrator/Services/SystemVm/SystemVmObligationService.cs` — assigns `VmName` at obligation creation. (`VmId` pre-assignment removed in BLOCKSTORE-FIX §6.)
- `src/Orchestrator/Models/SystemVmObligation.cs` — added `VmName: string?` field.
- `src/Orchestrator/Models/Node.cs` — added `SshCaPublicKey: string?` field; `NodeRegistrationRequest` likewise.

### 4.3 `DeCloud.NodeAgent`

Created:
- `src/DeCloud.NodeAgent/Controllers/ObligationEnvironmentController.cs` — `GET /api/obligations/{role}/environment` with inline per-role switch.
- `src/DeCloud.NodeAgent.Core/Models/ObligationEnvironment.cs` — response DTO.
- `src/DeCloud.NodeAgent/Services/NodeRelayConfigProvider.cs` — typed `HttpClient` shared between DHT and BlockStore environment-endpoint resolution paths.
- `src/DeCloud.NodeAgent.Infrastructure/Services/VmDeploymentPipeline.cs` — unified deploy entry point (artifact prefetch + binary verification + manager dispatch) for both system and tenant VMs.

Modified:
- `src/DeCloud.NodeAgent/OrchestratorClient.cs` — reads `/etc/ssh/decloud_ca.pub` at registration time, sends in `NodeRegistrationRequest`.
- `src/DeCloud.NodeAgent.Infrastructure/Services/SystemVm/SystemVmReconciler.cs` — `ActCreateAsync` is a pure executor that performs the single deferred substitution (`__VM_ID__`) before dispatching to the deployment pipeline.
- `src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs` — `CreateCloudInitIsoAsync` collapsed to: `cloudInitYaml = spec.CloudInitUserData;` + defensive variables-Replace pass + leak detector + `EnsureQemuGuestAgent` and GPU-shim safety nets.

Deleted (Phase 4):
- `src/DeCloud.NodeAgent.Infrastructure/Services/CloudInitTemplateService.cs` and DI registration
- `src/DeCloud.NodeAgent/CloudInit/Templates/general/`
- `src/DeCloud.NodeAgent/CloudInit/Templates/decloud-agent-*.b64`
- `LibvirtVmManager.PrepareCloudInitVariables` (STEP 5.5/5.6/5.7) and `MergeCustomUserDataWithBaseConfig`

### 4.4 `DeCloud.Builds`

Created:
- `base-templates/base-tenant.yaml` — base layer for tenant VMs.
- `base-templates/base-system.yaml` — base layer for system VMs (relay; non-mesh participants).
- `base-templates/base-system-mesh.yaml` — base layer for mesh-participant system VMs (DHT, BlockStore).
- `tenant-vms/general/cloud-init.yaml` — role layer (no base content).
- `tenant-vms/general/assets/general-api.py`, `index.html` — tenant general assets (consumer-render uses `{{VARNAME}}` per P4.5 convention).
- `tenant-vms/compute-artifact-constants.sh` — sibling to system-vms script.
- `system-vms/shared/assets/decloud-env-watcher.sh` — generic watcher.
- `system-vms/shared/assets/decloud-env-watcher@.service`, `decloud-env-watcher@.timer` — systemd units (templated, role-parameterized).
- `system-vms/shared/assets/wg-config-fetch.sh` — WG mesh enrollment script (shared between mesh roles).

Modified:
- `system-vms/dht/cloud-init.yaml`, `system-vms/blockstore/cloud-init.yaml`, `system-vms/relay/cloud-init.yaml` — stripped base content; fetch identity, fetch environment, write scope file, install watcher, source env file before service start, use `$VARNAME` shell references for the 2 dynamics declared per role.
- `.github/workflows/release-binaries.yml` — produces `dht-node` and `blockstore-node` artifacts. (`build-attestation-agent` job deferred per P0.10 — `decloud-agent` source missing; manual upload procedure in place.)

Deferred:
- `tenant-vms/general/src/` — `decloud-agent` Go source recovery is tracked as P0.10 (post-Phase-4 known-deferred work).

---

## 5. Phases

All phases closed. Status notes record completion dates and any deviation
from the planned shape.

### Phase 0 — `DeCloud.Builds` preparation

> **Status: COMPLETE 2026-05-04.** All 8 numbered tasks done. P0.10 (`decloud-agent` Go source recovery) is a known-deferred follow-up; tracked separately.

Pure infrastructure work; no code changes outside `DeCloud.Builds`.

**0.1 Create `base-tenant.yaml`** — extracted from current `general-vm-cloudinit.yaml`. Provides bootcmd (machine-id regen, snapd/LXD mask, qemu-guest-agent), packages, write_files for `decloud_ca.pub`, sshd hardening, systemd units for `decloud-agent` and welcome server, runcmd for artifact downloads and SSH CA setup.

**0.2 Create `base-system.yaml`** — extracted from current role YAMLs. Initially over-broad (included wg-mesh content); revised after relay YAML received — relay does not participate in the mesh, so wg-mesh content moved to a parallel `base-system-mesh.yaml` (P0.2b).

**0.3 Strip base content from role YAMLs** — for each of dht, blockstore, relay: removed bootcmd, qemu-guest-agent package, wg-mesh-watchdog files, wg-quick override.

**0.4 Move tenant assets** — `general-api.py`, `index.html` from NodeAgent repo to `tenant-vms/general/assets/`.

**0.5 Move `decloud-agent` source** — effectively no-op; source missing. P0.10 tracks recovery.

**0.6 Add `build-attestation-agent` CI job** — drafted but reverted; can't build source that doesn't exist. Manual upload procedure documented inside `release-binaries.yml`.

**0.7 Create `tenant-vms/compute-artifact-constants.sh`** — sibling script to system-vms's. Separate output file for the tenant tree, no coupling between system-VM and tenant constant regeneration.

**0.8 Create `decloud-env-watcher.sh`** + systemd units in `system-vms/shared/assets/`. Three files; watcher is role-parameterized, systemd units are templated. Smoke-tested.

**0.9** (added during execution) — Standardize identity-fetch across all three system VM role YAMLs. Pattern: cloud-init runcmd fetches `/api/obligations/{role}/state` from node-local API with bounded retry (60 attempts × 10s = 10 min ceiling). On final timeout, hard-fails the boot with clear log message. Per-role changes coordinated with binary changes (BlockStore loses `loadOrCreateIdentity()` self-create path; relay drops `__WIREGUARD_PRIVATE_KEY__` baked-in fallback).

### Phase 1 — Shared infrastructure (orchestrator)

> **Status: COMPLETE 2026-05-04.** All 11 numbered tasks done. Resolver inventory expanded from 10 planned to 15 actual to cover all `__VARNAME__` placeholders found in shipping base/role YAMLs.

Landed all rendering, registry, and template-variable infrastructure.
**No behavioural change to live VMs in this phase** — legacy paths remained.

**1.1** `TemplateVariable` model + `VmTemplate.Variables` field (default empty).

**1.2** `TemplateComposer` — string-based section detection. Merge rules: `packages` union/dedup, `write_files`/`bootcmd`/`runcmd` base-first then role, scalar keys role-wins. Validated against all three system-VM compositions.

**1.3** `IVariableResolver` interface + `VariableResolverRegistry` (DI singleton).

**1.4** Platform-common resolvers — 15 shipped (count expanded from plan's 10).

**1.5** `CloudInitRenderer` and `CloudInitValidator`. Two-pass design (statics, then artifacts). Validator runs as optional Pass 3 if registered.

**1.6** Added `Node.SshCaPublicKey` field; node agent reads `/etc/ssh/decloud_ca.pub` at registration; orchestrator stamps it.

**1.7** `SystemVmObligation.VmName` field. `EnsureObligationsAsync` and `TryAdoptExistingVmAsync` assign `VmId` and `VmName` at creation. (Note: `VmId` pre-assignment was removed later in BLOCKSTORE-FIX §6 once the inversion to libvirt-UUID-as-source-of-truth shipped.)

### Phase 2 — Tenant VM cutover

> **Status: COMPLETE 2026-05-04.** Fresh general VM and one marketplace VM (web-proxy-browser) both deployed via new pipeline.

Tenant VMs (general + marketplace) use the new pipeline. Legacy node-side
processing remained for system VMs through Phase 3.

**2.1** Seed `platform-general` template via `GeneralVmTemplateSeeder`. Composes `base-tenant.yaml + tenant-vms/general/cloud-init.yaml`. Declares variables for the tenant set.

**2.2** `VmService.CreateVmAsync` — when `TemplateId` is null and `VmType == General`, assigns `TemplateId = platform-general`. No "no template" code path remains.

**2.3** `VmService.AssignVmToNodeAsync` — replaced existing variable building with `CloudInitRenderer.RenderAsync(template, ctx)`. The `CreateVm` command's `UserData` carries the rendered string.

**2.4** Tenant resolvers — minimal set; existing tenant templates have no `Variables` declarations and use the legacy compatibility path.

**2.5** Verified `EnsureQemuGuestAgent` and GPU-shim safety nets in `LibvirtVmManager.CreateCloudInitIsoAsync` still run when `CloudInitUserData` is from the new pipeline.

### Phase 3 — System VM cutover, one role at a time

> **Status: COMPLETE 2026-05-06.** All three system VM roles cut over (relay 2026-05-04, DHT 2026-05-05, BlockStore 2026-05-06). Per-role acceptance + scope audit + dual-tamper drift test all passed. Validated end-to-end on relay node srv022010 and CGNAT node MSI.

**Order: relay → DHT → BlockStore.** This order matched the dependency
graph (DHT and BlockStore depend on a working relay; cutting over relay
first kept the test-bed clean of cross-role interactions).

Each role's cutover was one release. Between releases: full system VM
testbed run, plus 24-48 hours of production observation. Rollback per role
remained straightforward — the legacy node-side path stayed live until Phase 4.

#### 3.1 Per-role cutover steps (applied for each of relay, DHT, BlockStore)

**3.1.1** Update `SystemVmTemplateSeeder` for this role:
- Compose cloud-init via `TemplateComposer.Compose("base-system{-mesh}", "{role}")`.
- Declare `Variables` (statics + dynamics with scopes per §2.3).
- Bump template revision.

**3.1.2** Register role-specific resolvers (one file each per §4.2 layout).

**3.1.3** Implement node-side dynamic resolution (inline switch in `ObligationEnvironmentController`).

**3.1.4** Update the role's cloud-init YAML in `DeCloud.Builds`:
- Use `__VARIABLE_SCOPES_BLOCK__` placeholder (resolver fills it at render time).
- Fetch identity from `/api/obligations/{role}/state` (existing pattern; standardized in P0.9).
- Fetch environment from `/api/obligations/{role}/environment` (new).
- Write `/etc/decloud-{role}/environment` and `/etc/decloud-{role}/variable-scopes.conf`.
- Install + enable `decloud-env-watcher@{role}.timer`.
- Service unit uses two `EnvironmentFile=` directives: `/etc/decloud-{role}/{role}.env` (statics) and `-/etc/decloud-{role}/environment` (dynamics, optional during pre-watcher tick).
- Use `$VARNAME` shell references for dynamics in scripts/configs.

**3.1.5** Generic environment endpoint on node agent: landed in 3.1 (relay cutover) since the controller is shared. Subsequent role cutovers added inline switch cases; the controller code itself didn't change.

**3.1.6** `SystemVmReconciler.ActCreateAsync` (landed once with relay cutover, applies to all roles after): pure executor — reads `obligation.VmId`/`VmName`, reads cached rendered cloud-init from local SQLite, performs single deferred substitution (`__VM_ID__`) after libvirt UUID minting (BLOCKSTORE-FIX §6), prefetches artifacts, builds `VmSpec`, calls `_pipeline.DeployAsync`. No further substitution. Labels reduced to `system-vm-role`, `system-vm-revision`, `node-arch`.

#### 3.2 Per-role acceptance

For each role, before moving to the next:

- Fresh deploy via new pipeline succeeds. VM reaches Ready.
- All declared statics in the cloud-init render correctly (no `__FOO__` remnants).
- `/etc/decloud-{role}/environment` populated correctly at boot.
- Watcher timer running. Mutate one of each scope category in turn (see §7.5):
  - **Restart-scoped** mutation triggers service restart within 60s; values reflected.
  - **Reload-scoped** mutation triggers service reload within 60s; values reflected.
  - **Noop-scoped** mutation rewrites env file but no service action.
- Recovery test: `virsh destroy` + undefine, reconciler redeploys, identity preserved.
- 24-48 hours production observation, no regressions.

### Phase 4 — Cleanup

> **Status: COMPLETE 2026-05-07.** All 5 numbered tasks done. Net deletion: ~470 lines (P4.1 STEP 5.5/5.6/5.7 = 147 lines; P4.2 `MergeCustomUserDataWithBaseConfig` = 145 lines; P4.3 `CloudInitTemplateService` ~180 lines; P4.4 `decloud-agent-*.b64` and legacy `CloudInit/Templates/`; P4.5 surfaced welcome page leak — fixed via consumer-render convention split). Migration is closed.

Executed bold-cutover style after the gating release-cycle elapsed during the closing session arc with the user explicitly accepting the risk of bypassing the slower telemetry-first observation period.

**4.1** Deleted `LibvirtVmManager.PrepareCloudInitVariables` and its role-specific blocks (5.5/5.6/5.7). Replaced with breadcrumb comment.

**4.2** Deleted `MergeCustomUserDataWithBaseConfig`. `CreateCloudInitIsoAsync` collapses to:
```csharp
var cloudInitYaml = spec.CloudInitUserData;
// defensive variables-Replace pass for marketplace templates with undeclared vars
foreach (var (placeholder, value) in variables) { cloudInitYaml = cloudInitYaml.Replace(placeholder, value); }
// leak detector — warns on any surviving __VARNAME__
// generate ISO, attach
```
The defensive Replace pass and leak detector remain as safety nets for marketplace templates with undeclared variables.

**4.3** Deleted `CloudInitTemplateService.cs`, its DI registration, and the `_templateService` field/parameter/assignment from `LibvirtVmManager`. The else branch in `CreateCloudInitIsoAsync` (taken when `spec.CloudInitUserData` is null) now throws `InvalidOperationException` with a message pointing at the originating CreateVm caller.

**4.4** Deleted `src/DeCloud.NodeAgent/CloudInit/Templates/general/` and `decloud-agent-*.b64` files.

**4.5** Final testbed sweep: tenant + system VM deploys all pass. Surfaced one cosmetic compound bug (welcome page leak — directory routing in `general-api.py`'s `do_GET` + syntax-key mismatch with `index.html`'s `{{VM_NAME}}` convention). Both fixes applied; `__VARNAME__` vs `{{VARNAME}}` convention split documented in `DeCloud.Builds/README.md`.

---

## 6. Sequencing summary

Shipped in this exact order:

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

Phase 0 and Phase 1 were independent. Phase 2 and Phase 3 were independent
of each other (tenant and system flows touch different code paths once
shared infra landed). Within Phase 3, roles cut over sequentially.

Rollback was per-phase, per-role. Until Phase 4 deleted the legacy paths,
any cutover could be reverted by reverting the seeder and YAML changes.

---

## 7. Validation & test plan

Reference for future maintenance — these tests should pass for any future
template revision or pipeline change.

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
- Rendered cloud-init has no `__PLACEHOLDER__` remnants (post-P3.x.4 audits, `__WG_TUNNEL_IP__` is also resolved at render time as a Static with empty default — see §2.3).
- Identity fetch works. Environment fetch works. Scope file populated correctly.

### 7.4 Recovery test (per role)

`virsh destroy` + undefine. Reconciler reads cached rendered template from local SQLite (no re-pull needed unless template revision bumped). VM boots with same identity (peer ID preserved). Same `obligation.VmId`/`VmName`.

### 7.5 Environment-drift test (per role) — the critical new test

Without bumping any template revision, mutate underlying state on the
orchestrator and verify the watcher behaves correctly:

- **Restart-scoped variable** (e.g., `DHT_ADVERTISE_IP`): simulate IP change. Within 60s, watcher detects, env file rewritten, service restarted, new value in use. **No redeploy.**
- **Noop-scoped variable** (e.g., `DHT_BOOTSTRAP_PEERS`): mutate. Within 60s, env file rewritten. Confirm by inspection. No service signal sent. Confirm bootstrap-poll script picks up the new value on its next cycle.
- **Dual-tamper drift test** (added during P3.2.5 — exercises the slow path): mutate both `ENV_GENERATION` and a tracked dynamic value. Verify `journalctl -t decloud-env-watcher` logs the change and the role service `ActiveEnterTimestamp` advances.

Note: the original design expected a Reload-scoped scenario, but no shipped role declares a Reload-scoped dynamic. If a future role does, it should mutate the value, verify SIGHUP delivered, no socket reset.

### 7.6 Scope-correctness audit (per role)

For each declared dynamic, confirm with the binary owner that the declared
scope matches actual binary behaviour. Use the diagnostics endpoint or log
inspection to verify:
- Restart-scoped: did the binary actually rebind / re-init the relevant subsystem?
- Reload-scoped: did the binary actually re-read the value? (Many won't — escalate to Restart.)
- Noop-scoped: is it actually safe to ignore? Did some downstream consumer pick up the new value via its own mechanism?

Corrections bump the template revision and trigger re-render on next pull.
The Phase 3 audits (P3.1.7, P3.2.4, P3.3.4) demoted 5 originally-Dynamic
variables per mesh role to Static when the audit found that the underlying
runtime path doesn't actually observe runtime changes for those variables —
either because the value is runtime-managed by another path (`wg-config-fetch.sh`
for WG_*) or because no observation mechanism exists in the binary
(region in `dht-metadata.json`).

### 7.7 Template-revision invalidation test

Bump a template revision (e.g., add a new artifact). Heartbeat returns role
in `systemTemplatesPending`. Node pulls fresh; orchestrator renders fresh.
Reconciler redeploys the role. New `Variables` list, new scope policy,
new rendered cloud-init.

---

## 8. Out of scope

Items explicitly out of the migration scope; tracked but not addressed.

- **Author-resolver webhooks** (tenant authors providing custom Dynamic resolution endpoints) — possible future extension.
- **Converting `relay-api.py` to read environment variables** instead of having values substituted into the `.py` source. Would expand relay's runtime-mutable surface; deferrable. Once this lands, the `RELAY_REGION` and `RELAY_CAPACITY` scopes change from Noop to Restart.
- **Generalizing the watcher** to support actions beyond `noop`/`reload`/`restart` (e.g., "regenerate metadata file then restart"). Current design covers all roles' needs; extend only if a future role requires it.
- **Removing `Labels` from `VmRecord` entirely.** Only its substitution-side-channel role disappeared.
- **Tenant VM re-rendering on `node.PublicIp` or other host-state change.** Tenant VMs that need this should declare the relevant value as `Dynamic`; existing marketplace templates can opt in incrementally.
- **Generic post-fetch artifact substitution scanner.** Considered during P4.5 when the welcome page leak surfaced; explicitly rejected in favor of the per-template consumer-render convention (`{{VARNAME}}` substituted by the template's own server). Adds infrastructure where a convention suffices.
- **Migrating DHT / BlockStore dashboard `_SUBSTITUTIONS` dicts to `{{VARNAME}}`.** They predate the convention and use `__VARNAME__` for both render-time and consumer-side substitution. Harmless because their dictionaries are scoped to artifact serving and never collide with orchestrator-render placeholders. A revision bump per role to migrate is deferrable.

---

## 9. Anti-patterns to avoid

These remain authoritative for new work touching the pipeline.

- **Resolvers returning `""` on missing required input.** Throw with a clear message. Empty strings become silent runtime failures.
- **Hard-coding scope policy outside the template.** Scope is template metadata. The watcher reads the scope file. Never hard-code scopes in C# or in `DeCloud.Builds` config files.
- **Special-casing variable names in the validator.** The validator consults the template; nothing is hard-coded.
- **Fallback chains in resolvers** (`labels?.GetValueOrDefault(x) ?? metadata.X ?? ""`). Resolvers take typed inputs from `ResolutionContext` and throw on missing required data.
- **Caching rendered cloud-init on the obligation document.** Render is fresh on each pull. The earlier draft proposed caching; rejected.
- **Mixing static and dynamic resolvers in one class.** They answer different questions; keep them separate. Better diagnostics, easier testing.
- **Treating the legacy node-side path as "fine for now."** It's gone (Phase 4). Don't re-introduce node-side substitution; fix the issue at the resolver or template layer.
- **Putting `__VARNAME__` placeholders inside artifact bytes shipped to browsers.** Artifacts are content-addressed by SHA256; the orchestrator's render layer does not reach into artifact bytes. Use `{{VARNAME}}` for consumer-side substitution by the serving Python process, OR have the consumer fetch its values from a documented endpoint at startup. The DHT/BlockStore dashboards work correctly via the former pattern; the welcome page was fixed in P4.5 to use the same.
- **Adding a second deferred placeholder without thinking about it.** `__VM_ID__` is the single deferred placeholder, by design. Adding more requires designing a general "render-late" mechanism in `TemplateVariable` rather than ad-hoc `Replace` calls in the reconciler.

---

## 10. Definition of done

Closed scoreboard.

### Phase 0 ✓ (closed 2026-05-04)
- [x] `base-tenant.yaml`, `base-system.yaml`, `base-system-mesh.yaml` exist and contain no role-specific content.
- [x] Role YAMLs contain no base content.
- [ ] `decloud-agent` Go source in `DeCloud.Builds`; CI job builds and releases the binary. **Deferred (P0.10)** — source missing; manual upload procedure documented in `release-binaries.yml`.
- [x] `decloud-env-watcher.sh` + systemd units in `system-vms/shared/assets/`.

### Phase 1 ✓ (closed 2026-05-04)
- [x] `TemplateVariable`, `TemplateComposer`, `IVariableResolverRegistry`, `CloudInitRenderer`, `CloudInitValidator` shipped with full unit-test coverage.
- [x] All platform-common resolvers registered (15 shipped, count expanded from plan's 10).
- [x] `Node.SshCaPublicKey` populated on registration.
- [x] `SystemVmObligation.VmName` field added; obligations carry both `VmId` and `VmName` from creation.
- [x] Existing live VMs unaffected.

### Phase 2 ✓ (closed 2026-05-04)
- [x] `platform-general` seeded; new general VMs use it.
- [x] `VmService.AssignVmToNodeAsync` uses `CloudInitRenderer`.
- [x] Fresh general VM and marketplace VM deploys succeed via new pipeline.

### Phase 3 ✓ (closed 2026-05-06; relay 2026-05-04, DHT 2026-05-05, BlockStore 2026-05-06)
- [x] Each role's template declares full `Variables` set with audited scopes per §2.3.
- [x] Each role's resolvers registered (orchestrator and node sides).
- [x] Each role's cloud-init YAML in `DeCloud.Builds` updated: identity-fetch, environment-fetch, watcher install.
- [x] Generic environment endpoint serves declared dynamics for each role.
- [x] Each role deploys cleanly via new pipeline. Recovery and environment-drift tests pass (§7.4, §7.5).
- [x] §7.6 scope-correctness audit complete for each role; corrections applied (5 demotions per mesh role).
- [x] 24-48 hours production observation, no regressions.

### Phase 4 ✓ (closed 2026-05-07)
- [x] All three roles on new path for one full release cycle.
- [x] `LibvirtVmManager.PrepareCloudInitVariables`, `MergeCustomUserDataWithBaseConfig`, `CloudInitTemplateService` deleted.
- [x] `CloudInit/Templates/` and `decloud-agent-*.b64` files deleted from NodeAgent repo.
- [x] Final testbed sweep clean.

After all phases: adding a new system VM role requires authoring a template (declare `Variables`, write the role cloud-init YAML in `DeCloud.Builds`) and registering any resolvers the role's variables need that aren't already registered. Zero changes to `LibvirtVmManager`, `SystemVmReconciler`, or the environment endpoint controller.

---

## 11. Open questions resolved

| Question | Resolution |
|---|---|
| `__ORCHESTRATOR_URL__` source? | `OrchestratorUrlResolver` reads `IConfiguration["OrchestratorClient:BaseUrl"]` with co-located guard (substitute `node.PublicIp` if host is localhost). |
| Resolver inputs? | `ResolutionContext` — `Node`, `Obligation` (with deserialized `StateJson`), `Template`, etc. No labels lookup, no fallback chains. |
| Orchestrator's WireGuard public key? | `IWireGuardManager.GetOrchestratorPublicKeyAsync()`. Injected into `OrchestratorPublicKeyResolver`. |
| `ObligationRole` (Shared) vs `SystemVmRole` (Orchestrator)? | Use `SystemVmRole` enum inside the orchestrator. Convert at the wire boundary via `SystemVmRoleMap`. Resolver keys use canonical strings. |
| Per-variable scopes? | Starting set declared per role in §2.3; verified by §7.6 audits during Phase 3 (5 demotions per mesh role applied). |
| Backward compatibility for existing marketplace templates? | Templates with no `Variables` list use legacy compatibility: all referenced placeholders treated as statics, resolved by platform-common resolvers + `DefaultEnvironmentVariables`. Migration is opt-in per template. |
| `VmName` field on `SystemVmObligation`? | Added. Nullable string for backwards compat with obligations created before this change. Set by orchestrator at obligation creation. |
| `VmId` source of truth? | The libvirt UUID (post-BLOCKSTORE-FIX §6, 2026-05-06). Pre-assignment removed; orchestrator and node converge via heartbeat-based adoption. The single deferred render-time placeholder `__VM_ID__` is substituted by `SystemVmReconciler.ActCreateAsync` after the libvirt UUID is minted. |
| Consumer-side substitution syntax? | `{{VARNAME}}` for new artifact bytes shipped to browsers (P4.5 convention). DHT/BlockStore dashboards predate the convention and use `__VARNAME__`; harmless. See §2 decisions table and §9 anti-patterns. |

---

## 12. Handoff log expectations

Phase-by-phase decision log lives in
`UNIFIED_CLOUDINIT_PIPELINE_IMPLEMENTATION_PLAN.md` §2. Per-task acceptance
results and surprises are recorded there. Future maintenance work that
materially changes the pipeline shape should append to that doc's §2 with
a fresh dated entry, or open a new design-doc revision if the change
contradicts a §2 decision row above.

---

## 13. Contact and escalation

Authoritative for ongoing maintenance.

If you find a contradiction between this design and the live code: read the
live code, decide which is correct, log the decision in the implementation
plan §2.

If you find an ambiguity neither this document nor the code resolves:
surface it; do not silently pick.

If you find yourself wanting to bake a value derived from `node.PublicIp`,
`node.Region`, `node.RelayInfo`, or any heartbeat-cached value into a
`Static` variable: **stop.** That is the boundary this work corrects. Re-read
§2.1 and §9. The variable should be declared `Dynamic`.

If you find yourself wanting to hard-code scope policy somewhere outside
the template: **stop.** Scope is template data. Declare it on the variable.

If you find yourself wanting to add a node-side substitution layer: **stop.**
The orchestrator's `CloudInitRenderer` is the single render authority. The
single deferred placeholder (`__VM_ID__`) is the documented exception, not
a precedent for new ones.

---

## 14. Migration closure summary

Final-state architecture reference. Start here if you're picking up this
code cold.

### 14.1 Three substitution boundaries

The pipeline has three explicit, well-bounded substitution layers. Boundaries
are documented in code (`TemplateVariable.cs` xmldocs, `IVmDeploymentPipeline.cs`
header, `SystemVmReconciler` comments) and respected by both Orchestrator
and NodeAgent.

| Layer | When | Where (code) | Syntax | Authoritative for |
|---|---|---|---|---|
| **Render-time** | Template push to node (`/api/nodes/me/system-templates/{role}` or tenant `CreateVm` command construction) | Orchestrator's `CloudInitRenderer.RenderAsync` → `ResolveStaticsAsync` | `__VARNAME__` | Cloud-init body content (yaml that becomes `spec.CloudInitUserData`). Single authoritative substitution layer for both system and tenant VMs. |
| **Deploy-time** | Node receives template, mints libvirt UUID | `SystemVmReconciler.ActCreateAsync` → `template.CloudInitContent.Replace("__VM_ID__", vmSpec.Id)` | `__VM_ID__` only | A single deferred placeholder. The libvirt UUID is minted on the node, not the orchestrator. Only deferred variable in the entire pipeline. |
| **Runtime** | Inside the VM, post-boot, polling `/api/obligations/{role}/environment` every 60s | `decloud-env-watcher.sh` (in VM) reads response, diffs against `/etc/decloud-{role}/environment`, applies `max(scope)` action across changed variables | `$VARNAME` (shell-source via `EnvironmentFile=`) | Dynamic values that change without redeploy. Currently 2 dynamics per mesh-participant role: `*_ADVERTISE_IP` (Restart), `*_BOOTSTRAP_PEERS` (Noop). |

A separate, **independent** substitution boundary exists inside artifact serving:

| Layer | When | Where | Syntax | Authoritative for |
|---|---|---|---|---|
| **Consumer-render** | Browser request to a VM's served HTTP endpoint | Per-template Python server (e.g., `dht-dashboard.py` `_send_file_with_substitution`, `general-api.py` HTML branch) | `{{VARNAME}}` (new convention; DHT/BlockStore dashboards still use `__VARNAME__` for legacy reasons) | Per-VM content inside artifact bytes (HTML/JS files served from VMs). The orchestrator's render layer cannot reach into artifacts because they're content-addressed by SHA256; the consumer reads its own values from `os.environ` or local files (`/etc/decloud/{role}-metadata.json`). |

### 14.2 Key invariants

1. **One render authority.** `CloudInitRenderer` is the only layer that substitutes `__VARNAME__` in cloud-init bodies. Node-side substitution is reduced to a single deferred placeholder (`__VM_ID__`).
2. **One file authority per scope.** Statics live in `/etc/decloud-{role}/{role}.env` (written by cloud-init `write_files`, never mutated post-boot). Dynamics live in `/etc/decloud-{role}/environment` (watcher-managed, never written by anyone else). Service units load both via two separate `EnvironmentFile=` directives — systemd's later-wins merge keeps them disambiguated by file path, not by key.
3. **Content-addressed artifacts.** Every artifact is fetched by SHA256 from the node-local cache (`http://192.168.122.1:5100/api/artifacts/{sha256}`). Verified on cache write and re-verified on serve. SHA256 baked into cloud-init at render time via `${ARTIFACT_URL:name}` and `${ARTIFACT_SHA256:name}` substitutions.
4. **Identity persistence across redeploys.** `ObligationStateBase` subclasses (`RelayObligationState`, `DhtObligationState`, `BlockStoreObligationState`) preserve role identity (WG keypair, libp2p Ed25519 keypair, peerId, authToken) across `virsh destroy` + redeploy. Cloud-init fetches identity from `/api/obligations/{role}/state` before binary starts.
5. **Single VmId.** Post-BLOCKSTORE-FIX §6, both orchestrator and node converge on the libvirt UUID. No more pre-assigned phantom GUIDs. Heartbeat-based adoption is now the live convergence path; role-specific self-heal callbacks are belt-and-suspenders.
6. **Fast-fail at boundaries.** Required Statics throw on missing input (e.g., `CaPublicKeyResolver` if `Node.SshCaPublicKey` is null). Tenant userdata throws if `spec.CloudInitUserData` is empty (post-P4.3). No silent fallbacks that mask systemic gaps.

### 14.3 Key files

**Shared (`DeCloud.Shared`):**
- `Models/TemplateVariable.cs` — first-class metadata declaring variable kind + scope. Single source of truth for render-time, env-endpoint, and watcher behavior.
- `Models/SystemVmTemplate.cs` — node-side projection of `VmTemplate` pushed via SQLite-cached payload.
- `Models/TemplateArtifact.cs` — content-addressed artifact metadata (SHA256, source URL or data: URI).
- `Models/ObligationState.cs` — `ObligationStateBase` + role subclasses for identity persistence.

**Orchestrator (`DeCloud.Orchestrator`):**
- `Services/CloudInit/CloudInitRenderer.cs` — render engine. `ResolveStaticsAsync` iterates `template.Variables`, dispatches to registered resolvers.
- `Services/CloudInit/Resolvers/PlatformCommon/*.cs` — 15 platform-common resolvers (VM_ID, VM_NAME, NODE_ID, ORCHESTRATOR_URL, CA_PUBLIC_KEY, etc.).
- `Services/SystemVm/SystemVmTemplateSeeder.cs` — composes base + role layers, declares Variables and Artifacts, bumps `Revision` on cloud-init/binary changes.
- `Services/SystemVm/SystemVmTemplateSeeder.Artifacts.cs` — auto-generated SHA256 constants (regenerated by `system-vms/compute-artifact-constants.sh`).
- `Controllers/NodeSelfController.cs` `GetSystemTemplate` — endpoint that renders + serves the template payload.

**NodeAgent (`DeCloud.NodeAgent`):**
- `Infrastructure/Services/SystemVm/SystemVmReconciler.cs` — consumes pushed templates, performs the single deferred substitution (`__VM_ID__`), dispatches to `_pipeline.DeployAsync()`.
- `Infrastructure/Libvirt/LibvirtVmManager.CreateCloudInitIsoAsync` — post-P4 minimal flow: take `spec.CloudInitUserData` as-is, defensive variables-Replace pass, leak detector, ISO generation.
- `Controllers/ObligationEnvironmentController.cs` — serves `/api/obligations/{role}/environment` with inline `(role, name)` switch resolving 2 dynamics per role.
- `Infrastructure/Services/VmDeploymentPipeline.cs` — unified deploy entry point (artifact prefetch + binary verification + manager dispatch) for both system and tenant VMs.

**Build outputs (`DeCloud.Builds`):**
- `system-vms/base-system-mesh.yaml` — base layer for mesh-participant system VMs (DHT/BlockStore).
- `system-vms/base-system.yaml` — base layer for non-mesh system VMs (relay).
- `system-vms/{role}/cloud-init.yaml` — role layers, composed with base by `TemplateComposer` at seed time.
- `system-vms/shared/assets/decloud-env-watcher.{sh,service,timer}` — generic watcher (parameterised by role argument).
- `system-vms/shared/assets/wg-config-fetch.sh` — WG mesh enrollment script (shared between mesh roles).
- `tenant-vms/general/cloud-init.yaml` + `tenant-vms/general/assets/general-api.py` + `index.html` — tenant general VM template + welcome page.

### 14.4 Latent issues deferred from migration scope

Tracked but not blocking. Low likelihood of biting in normal operation.

1. **Auto-generated SHA256 constants drift.** `SystemVmTemplateSeeder.Artifacts.cs` is regenerated by `compute-artifact-constants.sh`. No CI gate enforces regeneration when shared scripts change. A developer modifying a `.sh` file but forgetting to run the script causes orchestrator/node SHA mismatch at deploy time → `sha256sum -c` fails inside cloud-init → VM bootstrap aborts. Worth a future CI hook (`git diff --exit-code` on the generated file after running the script).

2. **Single deferred placeholder is fragile design.** `__VM_ID__` is currently the only render-late variable. Adding a second deferred placeholder requires editing `SystemVmReconciler` (no general "render-late" mechanism in `TemplateVariable`). Acceptable as long as the count stays at 1.

3. **Required-Static resolvers don't fall through on throw.** `CloudInitRenderer.ResolveStaticsAsync` propagates exceptions; a single resolver throw fails the whole template fetch. Correct behavior per fail-fast philosophy, but it means every Required Static is a single point of failure. No "warn-and-substitute-empty" middle ground today.

4. **Tenant userdata fast-fail throw is unverified across all callers.** P4.3 replaced the legacy fallback with a throw. General tenant + marketplace template confirmed clean; not yet exercised by every possible orchestrator-side `CreateVm` caller. By design — if a future caller arrives without `userData`, the throw fires loudly and points at the originating code.

5. **WG_* runtime path bypasses the watcher pipeline.** `wg-config-fetch.sh` writes `/etc/decloud/wg-mesh.env` directly at boot rather than flowing through `decloud-env-watcher.sh`. That's why the 4 WG_* variables are Static-with-empty-default rather than Dynamic. Harmless today (the existing path works), but would need refactoring if WG_* values ever needed to participate in the watcher's max-scope arbitration alongside the role-specific dynamics.

6. **`__VARNAME__` vs `{{VARNAME}}` convention split.** Adopted in P4.5. DHT/BlockStore dashboards predate the convention and use `__VARNAME__` for both render-time and consumer-side substitution. Harmless because their dictionaries are scoped to artifact serving and never collide with orchestrator-render placeholders. Future artifacts should use `{{VARNAME}}` for consumer-side; documented in `DeCloud.Builds/README.md`.

### 14.5 Architectural review verdict

**The unified pipeline is well-architected and cleanly layered.** Three substitution
boundaries are real, distinct, and respected. Shared models in `DeCloud.Shared`
provide single-source-of-truth metadata. Code documentation (xmldocs in models,
header comments in pipeline files) makes the design intent visible to future
readers. Recent surgical fixes during the closing session arc (VmId pre-assignment
removal, bootstrap-poll source alignment, `EnvironmentFile=` discipline, P4 legacy
deletion) closed several boundary-mismatch issues; the pipeline now has fewer
concealed paths.

The six latent issues above are tractable. (1) is a CI gate. (2) is a design
consideration if the count grows. (3) is correct fail-fast behavior; no action
needed unless a specific scenario forces a middle ground. (4) self-surfaces if
a regression happens. (5) is a future refactor when the cost-benefit shifts.
(6) is a documented convention going forward.

### 14.6 Validated deployments at closure

| Node | Role(s) deployed | Status (2026-05-07) |
|---|---|---|
| srv022010 (relay node) | Relay v6.2 + DHT v8.3 + BlockStore v4.5 | All Active. Routing table populated. All peers discovered. |
| srv022010 (relay node) | Marketplace template VM (general) | Active. Welcome page renders correct VM_NAME after P4.5 fix. |
| MSI (CGNAT node) | DHT v8.3 + BlockStore v4.5 | All Active. peerId 12D3KooWHDYDiRCnmKSSJnQZ7SF5ctBWJ7EvMSQv1zAvKRoAWCrY (DHT) / 12D3KooWAXLXGUaEm9VMa8xuzJjDnJRZXKVKBh9NNZfvZLPjiaWD (BlockStore). Tunnel IPs 10.20.248.2 / 10.40.0.97. |
| MSI (CGNAT node) | General tenant VM (st4-3387) | Active. SSH cert auth working. Welcome page renders correct VM_NAME after P4.5 fix. |

---

_Migration closed 2026-05-07. This document is reference material going forward.
For execution detail, decision log, and per-task acceptance, see
`UNIFIED_CLOUDINIT_PIPELINE_IMPLEMENTATION_PLAN.md`._