# System VM Per-Deployment Rendering — Addendum to `TENANT_VM_UNIFIED_PIPELINE.md`

**Status:** Draft. Not yet started.
**Relationship:** This is an addendum, not a replacement. The umbrella document is `TENANT_VM_UNIFIED_PIPELINE.md`. This addendum addresses the system VM specifics that the umbrella plan does not walk through in detail.
**Scope:** System VMs (relay, dht, blockstore) — specifically the per-deployment rendering pathway from obligation creation to libvirt domain.

---

## 1. What the umbrella plan already covers

Cross-referenced gaps from `TENANT_VM_UNIFIED_PIPELINE.md` that are in scope and need no further specification here:

| Master plan item | Covers system VMs? | This addendum's role |
|---|---|---|
| G1 — `base-tenant.yaml`, `base-system.yaml` | Yes (`base-system.yaml` is system-VM-specific) | Defer entirely to umbrella |
| G2 — strip base content from role YAMLs | Yes (DHT, blockstore, relay role YAMLs) | Defer entirely to umbrella |
| G6 — `TemplateComposer` | Yes (used by both seeders) | Defer entirely to umbrella |
| G8 — `SystemVmTemplateSeeder` uses `Compose` | Yes (explicit) | Defer entirely to umbrella |
| G9 — CA public key from `Node.SshCaPublicKey` | Yes (Section 2.6) | Defer entirely to umbrella |
| G10 — SSH/password blocks built on orchestrator | Yes for tenant; **gap for system VMs** | Specified in §3.1 below |
| G12 — delete `MergeCustomUserDataWithBaseConfig` | Yes (after Phase 3) | Defer entirely to umbrella |
| G15 — delete node `__PLACEHOLDER__` engine | Yes — this addendum extends what "delete steps 1–5" means for system VMs |

The umbrella plan's Section 2.8 says `LibvirtVmManager.CreateCloudInitIso` collapses to `cloudInitYaml = spec.CloudInitUserData` after migration. That's the correct end state for both tenant and system VMs. The system VM specifics — what fills `spec.CloudInitUserData` for relay/dht/blockstore — are what this addendum specifies.

---

## 2. The gap

The umbrella plan's Section 2.5 specifies a complete variable set built by `VmService.AssignVmToNodeAsync`. That code path serves **tenant VMs only**. System VMs flow through a different path entirely:

```
Tenant VM:    VmService.CreateVmAsync
              → VmScheduler picks node
              → VmService.AssignVmToNodeAsync
              → builds variables, substitutes, sends complete UserData
              → CreateVm command to node
              → LibvirtVmManager (uses spec.CloudInitUserData directly, post-migration)

System VM:    Heartbeat-driven obligation reconciliation
              → SystemVmObligationService.CreateObligationAsync
              → P9 channel: template + Labels delivered to node
              → SystemVmReconciler.ActCreateAsync
              → SubstituteArtifactVariables (only ${ARTIFACT_URL:name})
              → spec.Labels carry deploy-time values (advertise IPs, ports, region, peer IDs)
              → LibvirtVmManager steps 5.5/5.6/5.7 read Labels and substitute __PLACEHOLDER__ variables
              → libvirt domain
```

The system VM path is where role-specific deployment policy still leaks into `LibvirtVmManager`. The umbrella plan deletes the node-side substitution engine (G15), but doesn't specify where the work moves to for system VMs. By process of elimination — since `VmService.AssignVmToNodeAsync` is the wrong code path for system VMs — it must move into the system VM obligation pipeline.

What's missing from the umbrella plan, specifically:

1. **A renderer for system VMs analogous to `VmService.AssignVmToNodeAsync`'s variable building.** Tenant VMs get full rendering at scheduling time. System VMs need an equivalent step.
2. **Where in the obligation lifecycle that rendering happens** (obligation creation? dispatch? P9 channel population?).
3. **What the P9 channel payload looks like** post-migration (rendered cloud-init vs. template + Labels).
4. **What the Labels dictionary becomes** once it's no longer a substitution side-channel.

---

## 3. Specifications

### 3.1 Single pipeline shared with tenant VMs

The umbrella plan (Section 2.5) specifies orchestrator-side variable substitution via a single function — `SubstituteCloudInitVariables(template, variables) → string`. That function is the pipeline. System VMs use the same one. There is no separate `SystemVmCloudInitRenderer` service.

What differs between tenant and system flows is the *variable-building step* that runs before the pipeline call. Both flows assemble a `Dictionary<string, string>` and hand it to the pipeline. The pipeline does not know or care which kind of VM produced the dictionary.

#### 3.1.1 Shared variable-building helper

A common helper produces the envelope that every VM receives — tenant or system, general or marketplace, dht or blockstore:

```csharp
// src/Orchestrator/Services/CloudInit/CommonVariableBuilder.cs
public static class CommonVariableBuilder
{
    /// <summary>
    /// Build the base variable set every VM receives.
    /// Caller adds VM-type-specific entries before invoking the substitution pipeline.
    /// </summary>
    public static Dictionary<string, string> Build(
        string vmId,
        string vmName,
        Node selectedNode,
        string orchestratorUrl,
        string? sshPublicKey,
        string? password,
        IEnumerable<TemplateArtifact> artifacts,
        string targetArchitecture)
    {
        var v = new Dictionary<string, string>
        {
            ["__VM_ID__"]              = vmId,
            ["__VM_NAME__"]            = vmName,
            ["__HOSTNAME__"]           = vmName,
            ["__NODE_ID__"]            = selectedNode.Id,
            ["__ORCHESTRATOR_URL__"]   = orchestratorUrl,
            ["__TIMESTAMP__"]          = DateTime.UtcNow.ToString("O"),
            ["__CA_PUBLIC_KEY__"]      = IndentForYaml(selectedNode.SshCaPublicKey ?? "", 6),
            ["__SSH_AUTHORIZED_KEYS_BLOCK__"] = BuildSshKeysBlock(sshPublicKey),
            ["__SSH_PASSWORD_AUTH__"]  = string.IsNullOrEmpty(password) ? "false" : "true",
            ["__PASSWORD_CONFIG_BLOCK__"] = BuildPasswordBlock(password),
            ["__ADMIN_PASSWORD__"]     = password ?? "",
        };

        foreach (var a in artifacts)
        {
            if (a.Architecture is not null && a.Architecture != targetArchitecture) continue;
            v[$"ARTIFACT_URL:{a.Name}"]    = $"http://192.168.122.1:5100/api/artifacts/{a.Sha256}";
            v[$"ARTIFACT_SHA256:{a.Name}"] = a.Sha256;
        }
        return v;
    }
}
```

Both `VmService.AssignVmToNodeAsync` (tenant) and the system VM obligation pipeline call `CommonVariableBuilder.Build` first, then add their own entries.

#### 3.1.2 System VM role-specific contributions

For each system VM role, a small static helper adds the role-specific variables. These helpers are pure functions of typed inputs — no reflection, no labels lookup:

```csharp
// src/Orchestrator/Services/SystemVm/Rendering/DhtVariableContributor.cs
public static class DhtVariableContributor
{
    public static void Contribute(
        Dictionary<string, string> variables,
        Node node,
        string region,
        WireGuardEndpoint? relayEndpoint)
    {
        variables["__DHT_REGION__"]          = region;
        variables["__DHT_LISTEN_PORT__"]     = DhtVmSpec.ListenPort.ToString();
        variables["__DHT_API_PORT__"]        = DhtVmSpec.ApiPort.ToString();
        variables["__WG_RELAY_ENDPOINT__"]   = relayEndpoint?.Endpoint ?? "";
        variables["__WG_RELAY_PUBKEY__"]     = relayEndpoint?.PublicKey ?? "";
        variables["__WG_RELAY_API__"]        = relayEndpoint?.ApiUrl    ?? "";
        // Note: __WG_TUNNEL_IP__ is NOT set here — it's a runtime value
        // filled by wg-config-fetch.sh inside the VM at first boot.
    }
}
```

Equivalent contributors for blockstore and relay. Each lives next to its obligation logic and is the single source of truth for "what variables does this role's cloud-init expect."

#### 3.1.3 The full system VM render flow

```
1. CommonVariableBuilder.Build(...)                           → envelope
2. {Role}VariableContributor.Contribute(envelope, ...)        → envelope + role vars
3. SubstituteCloudInitVariables(template, envelope)           → rendered string
4. Validate: scan for any remaining ${...} or __...__ tokens.
   Fail loudly if any are found other than runtime tokens
   (specifically the tunnel-IP placeholder filled in-VM by wg-config-fetch.sh).
```

The same three-step shape works for tenants — step 2 is the only thing that varies.

### 3.2 When rendering happens in the obligation lifecycle

**Decided: render lazily at first delivery, persist on the obligation, invalidate on template revision bump or CA rotation.**

Concrete behaviour:

```
SystemVmObligationService.GetOrRenderCloudInitAsync(obligation):
    if obligation.RenderedCloudInit is not null
       and obligation.RenderedTemplateRevision == currentTemplateRevision
       and obligation.RenderedCaFingerprint   == currentCaFingerprint:
        return obligation.RenderedCloudInit

    # Cache miss or invalidated — re-render
    rendered = RenderCloudInit(obligation, currentTemplate, currentNode)
    obligation.RenderedCloudInit         = rendered
    obligation.RenderedTemplateRevision  = currentTemplateRevision
    obligation.RenderedCaFingerprint     = currentCaFingerprint
    SaveObligation(obligation)
    return rendered
```

Invalidation triggers (the only two that matter):

- **Template revision bump.** `SystemVmTemplateSeeder` increments `Revision` when DeCloud.Builds changes a script, binary SHA, or cloud-init YAML. Any obligation referencing the previous revision re-renders on next delivery.
- **CA rotation.** When the CA public key changes on `Node.SshCaPublicKey`, the fingerprint stored on the obligation no longer matches and re-rendering occurs.

Everything else — heartbeat cadence, node restarts, network blips — is a no-op against the cache. The rendered cloud-init is stable until the orchestrator's authoritative inputs actually change.

The same lifecycle applies to tenant VMs once the umbrella plan ships. Tenants render at `VmService.AssignVmToNodeAsync` (which already happens once per VM creation), and the same invalidation triggers apply if a template-driven tenant VM gets re-scheduled.

### 3.3 P9 channel payload change

**Today.**

```json
{
  "templateId": "...",
  "templateRevision": 2,
  "templateJson": "{...}",
  "labels": {
    "node-id": "...",
    "blockstore-advertise-ip": "...",
    "blockstore-listen-port": "5001",
    ...
  }
}
```

**Target.**

```json
{
  "templateId": "...",
  "templateRevision": 2,
  "renderedCloudInit": "#cloud-config\n...",
  "vmId": "...",
  "vmName": "blockstore-...",
  "labels": {
    "node-id": "...",
    "system-vm-role": "blockstore",
    "system-vm-revision": "2"
  }
}
```

Three changes:

- `templateJson` retained transitionally for one release (debugging fallback), then dropped.
- `renderedCloudInit` becomes the authoritative content.
- `labels` shrinks dramatically — only labels that the node legitimately needs at runtime (artifact arch selection, role identification for diagnostics) remain. Deploy-time policy values (`blockstore-advertise-ip`, ports, region, etc.) leave the labels dict because they're inside `renderedCloudInit` now.

### 3.4 What `SystemVmReconciler.ActCreateAsync` does post-migration

```
1. Read obligation from local store
2. If obligation has renderedCloudInit, use it.
   Else fall back to legacy path (template + node-side substitution) for one release.
3. Verify artifacts cached / prefetch missing
4. Build VmSpec with CloudInitUserData = obligation.RenderedCloudInit
5. IVmManager.CreateVmAsync(spec)
```

The reconciler does **no** substitution. It is a pure executor: read the orchestrator's rendered document, prefetch the artifacts it references, hand the document to libvirt. The artifact URL `http://192.168.122.1:5100/api/artifacts/{sha256}` is the same literal string on every host (virbr0 gateway is `192.168.122.1` everywhere), so the orchestrator can render it with the rest of the variables — no host-locality leak.

### 3.5 What `LibvirtVmManager.PrepareCloudInitVariables` does post-migration

Nothing. The function deletes entirely. `LibvirtVmManager.CreateCloudInitIso` becomes:

```csharp
var cloudInitYaml = spec.CloudInitUserData;  // already complete
cloudInitYaml = EnsureQemuGuestAgent(cloudInitYaml);  // safety net, no-op for compliant templates
// hand to genisoimage
```

This is exactly what the umbrella plan's Section 2.8 specifies for tenant VMs, applied uniformly to system VMs as well. The role-specific blocks at steps 5.5/5.6/5.7 disappear along with the rest of the substitution machinery.

### 3.6 Labels become orchestrator-internal metadata

After the migration, the `Labels` dictionary on `VmRecord` keeps a smaller, well-defined set:

- `system-vm-role` — role identifier for orchestrator queries and dashboards.
- `system-vm-revision` — template revision that produced this VM, for diff/audit.
- `node-arch` — populated by node, for orchestrator-side observability.

Everything else currently in labels (deploy-time policy values) leaves the dictionary because it's now embedded in the rendered cloud-init that the orchestrator already produced and persisted.

---

## 4. Phase mapping

This addendum's work fits inside the umbrella plan's phases — it doesn't introduce new phases.

### Within Umbrella Phase 2 (Orchestrator: TemplateComposer and seeder updates)

Add: extract `CommonVariableBuilder` from the umbrella plan's tenant-side variable-building work into a shared helper (§3.1.1). Both tenant and system flows call it.
Add: implement role-specific contributors `DhtVariableContributor`, `BlockStoreVariableContributor`, `RelayVariableContributor` (§3.1.2). Pure functions of typed inputs.
Add: extend `SystemVmObligationService` with `GetOrRenderCloudInitAsync` implementing the lazy-render-and-cache lifecycle (§3.2).
Add: persist `RenderedCloudInit`, `RenderedTemplateRevision`, `RenderedCaFingerprint` on the obligation document.
Add: extend P9 channel payload schema (§3.3).

This is the bulk of the system-VM-specific work. It piggybacks on the umbrella's Phase 2 work because the same `SubstituteCloudInitVariables` function and the same `CommonVariableBuilder` serve both flows.

### Within Umbrella Phase 3 (Node agent simplified)

Add: `SystemVmReconciler.ActCreateAsync` reduces to executor-only — no substitution at all (§3.4).
Add: `LibvirtVmManager.PrepareCloudInitVariables` deletes entirely for both tenant and system VMs (§3.5).
Add: shrink Labels usage on VmRecord (§3.6).

These follow naturally once the umbrella's Phase 2 ships rendering on the orchestrator side.

### No new phases required

Every change in this addendum lives inside an existing umbrella phase. The addendum changes the *content* of those phases for system VMs but not their *shape*.

---

## 5. Open questions

The two largest design questions are now decided:

- **Pipeline unification:** one `SubstituteCloudInitVariables` function, one `CommonVariableBuilder` helper, one rendering shape used by both tenant and system flows. Role-specific deltas are isolated to small typed contributors that run before the substitution call.
- **Rendering lifecycle:** lazy at first delivery, persist on the obligation, invalidate only on template revision bump or CA rotation.

The remaining open items:

1. **Storage location of rendered cloud-init.** Storing it on the obligation document is simplest. Storing it separately (in a sibling collection keyed by obligation Id) keeps obligation documents small. Recommendation: store on the obligation. Rendered system VM cloud-init is ~30–50 KB; well within MongoDB document budgets. Revisit only if document size becomes a measured problem.

2. **CA fingerprint computation.** Need to pick a stable hash of the CA public key for the invalidation comparison. SHA-256 of the trimmed PEM body is fine; document the exact algorithm so the orchestrator and any audit tooling agree. Trivial decision, just needs to be made explicit.

3. **What about `wg-config-fetch.sh` allocating the WireGuard tunnel IP at first boot?** This stays exactly as it is. The orchestrator renders cloud-init *without* substituting `__WG_TUNNEL_IP__` — the placeholder remains in the rendered document and is replaced inside the VM by `wg-config-fetch.sh` writing to the env file. Runtime-after-deploy values are out of scope for orchestrator-side rendering by design. The validation step in §3.1.3 explicitly tolerates this one placeholder.

4. **Migration safety net for the transitional period.** Until Phase 3 ships, system VMs in the field can run through the legacy path (template + Labels + node-side substitution). The new path runs alongside. Cut over per role, validate on a testbed plus one production node, then delete the legacy code in one commit.

---

## 6. What this addendum does *not* do

- It does not propose a new phase structure. The umbrella plan's three phases are sufficient.
- It does not propose changes to artifact distribution, the obligation state machine, or WireGuard mesh enrollment.
- It does not address tenant VMs — those are fully covered by the umbrella plan.
- It does not address the typed-builder refactor I floated in an earlier draft. That was scope creep relative to the umbrella plan; if it's worth doing later, it's a post-umbrella concern.

---

## 7. Reading order

When implementing:

1. Read `TENANT_VM_UNIFIED_PIPELINE.md` end-to-end first.
2. Read this addendum.
3. Re-read the umbrella plan with this addendum's specifics in mind for Phases 2 and 3.

Anything contradictory between the two documents: the umbrella plan wins. This addendum specifies, never overrides.