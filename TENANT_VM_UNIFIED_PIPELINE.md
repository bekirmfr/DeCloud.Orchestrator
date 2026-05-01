# Tenant VM Unified Pipeline — Implementation Handout

**Status:** Design approved, not yet implemented.
**Goal:** Unify tenant VM deployment under the same artifact pipeline used by
system VMs. The node agent receives a complete, ready-to-deploy cloud-init
string. No file reads, no placeholder substitution, no YAML merging on the node.

---

## 1. Current State

### 1.1 Deployment flow — General VM (no TemplateId)

```
Orchestrator — VmService.AssignVmToNodeAsync
  vm.Spec.UserData = null  (no template, no custom cloud-init)
  processedUserData = null
  CreateVm command → { UserData: null, VmType: General, SshPublicKey, Password, Labels }

Node — LibvirtVmManager.CreateCloudInitIso
  CloudInitUserData is null → PATH B

  PATH B — CloudInitTemplateService.ProcessTemplateAsync(VmType.General)
    LoadTemplateAsync
      → reads CloudInit/Templates/general/general-vm-cloudinit.yaml from disk
    InjectGeneralExternalTemplatesAsync
      → reads CloudInit/Templates/general/assets/general-api.py from disk
      → reads CloudInit/Templates/general/assets/index.html from disk
      → injects __WELCOME_SERVER_PY__, __INDEX_HTML__
    BuildTemplateVariablesAsync
      → reads CloudInit/Templates/decloud-agent-{arch}.b64 from disk
      → injects __ATTESTATION_AGENT_BASE64__
      → injects __VM_ID__, __HOSTNAME__, __SSH_AUTHORIZED_KEYS_BLOCK__
      → injects __PASSWORD_CONFIG_BLOCK__, __ADMIN_PASSWORD__
      → reads /etc/ssh/decloud_ca.pub from disk → __CA_PUBLIC_KEY__
    Replace all __PLACEHOLDER__ → rendered cloud-init
```

### 1.2 Deployment flow — Marketplace VM (has TemplateId, UserData from template)

```
Orchestrator — VmService.AssignVmToNodeAsync
  vm.Spec.UserData = template.CloudInitTemplate  (raw, contains __PLACEHOLDER__ vars)
  Step 6.5:
    GetAvailableVariables(vm, node)
      → DECLOUD_VM_ID, DECLOUD_NODE_ID, DECLOUD_PASSWORD, DECLOUD_DOMAIN etc.
    Resolve ARTIFACT_URL:{name} variables (if template has artifacts)
    SubstituteCloudInitVariables(UserData, variables)
      → substitutes ${VAR} pattern only
      → __PLACEHOLDER__ pattern vars remain unsubstituted
  processedUserData = partially substituted cloud-init
  CreateVm command → { UserData: processedUserData, VmType, SshPublicKey, Password }

Node — LibvirtVmManager.CreateCloudInitIso
  CloudInitUserData is present → PATH A

  PATH A — MergeCustomUserDataWithBaseConfig(UserData, hostname, sshKeys, password)
    Line-by-line YAML parser:
      → injects hostname, disable_root if absent
      → injects SSH keys block before runcmd
      → injects password block (chpasswd.users) before runcmd
      → injects bootcmd for password + sshd_config.d
      → handles runcmd injection carefully (avoids PYTORCH bug)
    foreach __PLACEHOLDER__ → Replace(value)
      → __CA_PUBLIC_KEY__ read from /etc/ssh/decloud_ca.pub on disk
      → __ATTESTATION_AGENT_BASE64__ NOT injected (marketplace templates
        must include their own attestation agent or go without)
```

### 1.3 Deployment flow — System VM

```
Node — SystemVmReconciler.ActCreateAsync
  Reads SystemVmTemplate from SQLite (P9 channel)
  PrefetchAsync all artifacts → local cache
  SubstituteArtifactVariables (${ARTIFACT_URL:name})
  Builds VmSpec with CloudInitUserData = rendered content
  LibvirtVmManager.CreateVmAsync → PATH A (MergeCustomUserDataWithBaseConfig)
    → no-op merge (system VM cloud-init already complete)
    → __CA_PUBLIC_KEY__ still read from disk and injected
    → EnsureQemuGuestAgent runs (no-op — already in cloud-init)
```

### 1.4 Key structural problems

**Problem 1 — Split substitution engines.**
Two substitution patterns used simultaneously:
- Orchestrator: `${VAR}` via `SubstituteCloudInitVariables` (regex pattern)
- Node: `__PLACEHOLDER__` via `string.Replace` (LibvirtVmManager steps 1–5)

Both run on the same document at different times. Ordering bugs are silent.

**Problem 2 — CA public key is read from disk on the node.**
`/etc/ssh/decloud_ca.pub` is read at VM creation time inside
`LibvirtVmManager`. The CA key belongs to the orchestrator's domain
(it signs SSH certificates). The node should not be the source of this value.

**Problem 3 — MergeCustomUserDataWithBaseConfig is fragile.**
A line-by-line YAML parser that injects content at specific positions. Already
caused the PYTORCH_CLOUD_INIT bug (content landing outside runcmd when
`final_message:` followed runcmd). Has to know the structure of every template.

**Problem 4 — Marketplace VMs get an incomplete base layer.**
`MergeCustomUserDataWithBaseConfig` injects SSH keys and password but does NOT
inject the attestation agent or welcome server. Marketplace VMs run without
attestation unless the author explicitly includes it.

**Problem 5 — No single source of truth for base layer content.**
The base layer is assembled at runtime from disk files (`.b64`, YAML, CA key).
Changing the base layer (e.g. a new attestation agent version) requires updating
`decloud-agent.b64` files in the repo, which requires a node reinstall.

**Problem 6 — `CloudInitTemplateService` still exists.**
P11 removed system VM paths. Only General and Inference VM paths remain.
The Inference path is future work. So `CloudInitTemplateService` exists
for one path: General VMs.

---

## 2. Target State

### 2.1 Core principle
**The orchestrator produces a complete, ready-to-deploy cloud-init document.**
By the time the CreateVm command reaches the node, the cloud-init is fully
rendered — all variables substituted, all artifacts referenced by local cache
URL, all base layer content included. The node does nothing but write the
document to disk and call `genisoimage`.

### 2.2 Base template primitives

Two YAML fragments stored in `DeCloud.Builds/base-templates/`:

**`base-tenant.yaml`** — embedded in every general and marketplace VM
```
Provides:
  bootcmd:   machine-id regen, snapd/LXD mask, qemu-guest-agent early start
  packages:  qemu-guest-agent, curl, jq, openssl
  write_files:
    /etc/ssh/decloud_ca.pub          ← __CA_PUBLIC_KEY__ (substituted at seed)
    /etc/ssh/sshd_config.d/...       ← SSH hardening
    /etc/decloud/vm-id               ← __VM_ID__
    /etc/decloud/orchestrator-url    ← __ORCHESTRATOR_URL__
    /etc/systemd/system/decloud-agent.service
    /etc/systemd/system/welcome.service
  runcmd:
    qemu-guest-agent symlink
    ${ARTIFACT_URL:decloud-agent} download + sha256 verify
    ${ARTIFACT_URL:general-api-py} download
    ${ARTIFACT_URL:general-index-html} download
    decloud-agent start
    welcome server start
    SSH CA + principals setup
    sshd reload
```

**`base-system.yaml`** — embedded in every system VM (relay, dht, blockstore)
```
Provides:
  bootcmd:   machine-id regen, snapd/LXD mask, qemu-guest-agent early start
  packages:  qemu-guest-agent, curl, jq, openssl, wireguard, wireguard-tools,
             nginx-light, python3
  write_files:
    wg-mesh-watchdog.sh (inline static)
    wg-mesh-watchdog.service + timer
    wg-quick@wg-mesh override.conf
  runcmd:
    qemu-guest-agent symlink
    (role-specific artifact downloads come from role layer)
```

### 2.3 TemplateComposer

New orchestrator service that merges two cloud-init YAML fragments:

```csharp
// Orchestrator/Services/TemplateComposer.cs
public static class TemplateComposer
{
    public static string Compose(string baseYaml, string roleYaml)
}
```

Merge rules (simple, deterministic, no YAML parser needed):
- `packages:` — union of both lists (deduplicated)
- `write_files:` — base entries first, then role entries
- `bootcmd:` — base entries first, then role entries
- `runcmd:` — base entries first, then role entries
- Scalar keys (`hostname`, `manage_etc_hosts`, `disable_root`, `ssh_pwauth`,
  `final_message`) — role wins (role layer provides the specific value)
- `#cloud-config` header — emitted once

### 2.4 Seeded templates

`TemplateSeederService` seeds (in addition to existing marketplace templates):

**`platform-general`** (slug: `platform-general`)
```
= Compose(base-tenant.yaml, general-vm/cloud-init.yaml)
Artifacts:
  decloud-agent-amd64  (Binary, HTTPS — DeCloud.Builds release)
  decloud-agent-arm64  (Binary, HTTPS — DeCloud.Builds release)
  general-api-py       (Script, data: URI inline)
  general-index-html   (WebAsset, data: URI inline)
```

**`system-dht`, `system-blockstore`, `system-relay`** — already seeded by
`SystemVmTemplateSeeder`. Updated to use `TemplateComposer`:
```
= Compose(base-system.yaml, {role}/cloud-init.yaml)
```
This removes the duplication where bootcmd, qemu-guest-agent, and watchdog
content is currently copy-pasted into all three role YAMLs.

### 2.5 Orchestrator — variable substitution

`VmService.AssignVmToNodeAsync` step 6.5 builds a complete variable set:

```
variables = {
  // Runtime — VM identity
  __VM_ID__          = vm.Id
  __VM_NAME__        = vm.Name
  __HOSTNAME__       = vm.Name
  __ORCHESTRATOR_URL__ = node.OrchestratorUrl (or configured value)
  __TIMESTAMP__      = DateTime.UtcNow.ToString("O")
  __NODE_ID__        = node.Id

  // SSH
  __SSH_AUTHORIZED_KEYS_BLOCK__ = BuildSshKeysBlock(sshPublicKey)
  __SSH_PASSWORD_AUTH__         = "true" / "false"
  __PASSWORD_CONFIG_BLOCK__     = BuildPasswordBlock(password)
  __ADMIN_PASSWORD__            = password ?? ""
  __CA_PUBLIC_KEY__             = orchestrator.SshCaPublicKey (from config/DataStore)

  // Artifacts
  ARTIFACT_URL:{name}    = "http://192.168.122.1:5100/api/artifacts/{sha256}"
  ARTIFACT_SHA256:{name} = sha256
}
```

`SubstituteCloudInitVariables` runs once with this complete set.
Result: fully rendered cloud-init. No remaining placeholders.

### 2.6 CA public key — source of truth moves to orchestrator

Currently: node reads `/etc/ssh/decloud_ca.pub` at VM creation time.
Target: orchestrator reads the CA public key once at startup and stores it in
`IConfiguration` / a singleton. When a node registers, it sends its CA public
key in the registration payload. The orchestrator stores it on the `Node` record
(`Node.SshCaPublicKey`). `VmService.AssignVmToNodeAsync` reads it from
`selectedNode.SshCaPublicKey` when building variables.

No changes to how the CA key is generated or where it lives on the node. Only
the read location moves — from node-side disk read at deploy time to
orchestrator-side node record at deploy time.

### 2.7 General VMs always have TemplateId

`VmService.CreateVmAsync` — when no `TemplateId` is provided and `VmType` is
`General`, assign `TemplateId = platform-general-template-id`. This ensures
every general VM goes through the template path and receives the complete
base layer.

### 2.8 Node agent — simplified

`LibvirtVmManager.CreateCloudInitIso`:
- Step 6 becomes: `cloudInitYaml = spec.CloudInitUserData`
- Steps 1–5 (variable building) are deleted
- `MergeCustomUserDataWithBaseConfig` is deleted
- `CloudInitTemplateService` calls are deleted
- `EnsureQemuGuestAgent` stays — safety net, no-op for compliant templates
- GPU proxy shim injection stays — runtime concern, not template concern

### 2.9 DeCloud.Builds structure (final)

```
DeCloud.Builds/
  base-templates/
    base-tenant.yaml
    base-system.yaml
  system-vms/
    compute-artifact-constants.sh
    dht/
      src/              ← Go source → dht-node binary
      assets/           ← scripts, dashboards → data: URI artifacts
      cloud-init.yaml   ← role layer only (no base content)
    blockstore/
      src/
      assets/
      cloud-init.yaml
    relay/
      assets/
      cloud-init.yaml
    shared/
      assets/           ← wg-mesh-enroll.sh, wg-config-fetch.sh
  tenant-vms/
    compute-artifact-constants.sh
    general/
      src/              ← decloud-agent Go source → binary artifact
      assets/           ← general-api.py, index.html → data: URI artifacts
      cloud-init.yaml   ← role layer only (no base content)
```

---

## 3. Gap Analysis

| # | Gap | Current | Target | Repos affected |
|---|-----|---------|--------|----------------|
| G1 | Base YAML fragments | None | `base-tenant.yaml`, `base-system.yaml` | `DeCloud.Builds` |
| G2 | Role-only system VM YAMLs | Contain base content | Base content stripped | `DeCloud.Builds` |
| G3 | Role-only general VM YAML | Contains base content | Base content stripped | `DeCloud.Builds` |
| G4 | `decloud-agent` in CI | `.b64` files in NodeAgent repo | GitHub Release binary artifact | `DeCloud.Builds` |
| G5 | `general/assets/` in Builds | `general-api.py`, `index.html` in NodeAgent repo | `DeCloud.Builds/tenant-vms/general/assets/` | Both |
| G6 | `TemplateComposer` | None | New orchestrator service | `Orchestrator` |
| G7 | `platform-general` seeded | Not seeded | `TemplateSeederService` seeds it | `Orchestrator` |
| G8 | System VM seeders use Compose | Copy-paste base content | Call `TemplateComposer.Compose` | `Orchestrator` |
| G9 | CA key source | Node disk read (`/etc/ssh/decloud_ca.pub`) | `Node.SshCaPublicKey` from node record | `Orchestrator`, `NodeAgent` |
| G10 | SSH/password blocks built | Node (`LibvirtVmManager` steps 1–5) | Orchestrator (`VmService`) | `Orchestrator`, `NodeAgent` |
| G11 | All VMs have `TemplateId` | General VMs have none | `platform-general` assigned at create | `Orchestrator` |
| G12 | `MergeCustomUserDataWithBaseConfig` | Required | Deleted | `NodeAgent` |
| G13 | `CloudInitTemplateService` | Required for General VMs | Deleted | `NodeAgent` |
| G14 | `CloudInit/Templates/` | Has `general/` folder + `.b64` files | Deleted | `NodeAgent` |
| G15 | Node substitution engine | `__PLACEHOLDER__` Replace loop (steps 1–5) | Deleted | `NodeAgent` |

---

## 4. Implementation Plan

### Phase 1 — DeCloud.Builds: base templates and general VM assets

**1.1 Create `base-tenant.yaml`**
Path: `DeCloud.Builds/base-templates/base-tenant.yaml`

Content:
```yaml
#cloud-config
# base-tenant — base layer for all tenant VMs (general + marketplace)
# Composed with a role layer by TemplateComposer at seed/publish time.
# NOT deployed standalone.

bootcmd:
  - rm -f /etc/machine-id /var/lib/dbus/machine-id
  - systemd-machine-id-setup
  - systemctl mask snapd.service snapd.socket snapd.seeded.service
      snap.lxd.activate.service lxd.service lxd-agent.service 2>/dev/null || true
  - systemctl start qemu-guest-agent 2>/dev/null || true

disable_root: false
__SSH_AUTHORIZED_KEYS_BLOCK__
__PASSWORD_CONFIG_BLOCK__
ssh_pwauth: __SSH_PASSWORD_AUTH__

packages:
  - qemu-guest-agent
  - curl
  - jq
  - openssl

write_files:
  - path: /etc/ssh/decloud_ca.pub
    permissions: '0644'
    content: |
      __CA_PUBLIC_KEY__

  - path: /etc/ssh/sshd_config.d/99-decloud-password-auth.conf
    permissions: '0644'
    owner: root:root
    content: |
      PasswordAuthentication yes
      PermitRootLogin yes
      ChallengeResponseAuthentication no
      UsePAM yes

  - path: /etc/decloud/vm-id
    permissions: '0644'
    content: __VM_ID__

  - path: /etc/decloud/orchestrator-url
    permissions: '0644'
    content: __ORCHESTRATOR_URL__

  - path: /etc/systemd/system/decloud-agent.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud Ephemeral Attestation Agent
      After=network.target
      [Service]
      Type=simple
      ExecStart=/usr/local/bin/decloud-agent
      Restart=always
      RestartSec=5
      NoNewPrivileges=yes
      ProtectSystem=strict
      ProtectHome=yes
      PrivateTmp=yes
      ReadOnlyPaths=/proc /sys
      ReadWritePaths=/var/log
      StandardOutput=journal
      StandardError=journal
      SyslogIdentifier=decloud-agent
      [Install]
      WantedBy=multi-user.target

  - path: /etc/systemd/system/welcome.service
    permissions: '0644'
    content: |
      [Unit]
      Description=DeCloud Welcome Page
      After=network.target
      [Service]
      ExecStart=/usr/local/bin/general-api.py
      Restart=always
      WorkingDirectory=/var/www
      StandardOutput=journal
      StandardError=journal
      [Install]
      WantedBy=multi-user.target

runcmd:
  - ln -sf /lib/systemd/system/qemu-guest-agent.service
      /etc/systemd/system/multi-user.target.wants/qemu-guest-agent.service
  - systemctl start qemu-guest-agent || true

  # SSH Certificate Authority
  - |
    echo '' >> /etc/ssh/sshd_config
    echo '# DeCloud: SSH Certificate Authority' >> /etc/ssh/sshd_config
    echo 'TrustedUserCAKeys /etc/ssh/decloud_ca.pub' >> /etc/ssh/sshd_config
    echo 'AuthorizedPrincipalsFile /etc/ssh/auth_principals/%u' >> /etc/ssh/sshd_config
  - mkdir -p /etc/ssh/auth_principals
  - echo vm-__VM_ID__ > /etc/ssh/auth_principals/root
  - chmod 644 /etc/ssh/auth_principals/root

  # Attestation agent and welcome server (from artifact cache)
  - curl -sf ${ARTIFACT_URL:decloud-agent} -o /usr/local/bin/decloud-agent
  - echo "${ARTIFACT_SHA256:decloud-agent}  /usr/local/bin/decloud-agent" | sha256sum -c -
  - chmod +x /usr/local/bin/decloud-agent
  - curl -sf ${ARTIFACT_URL:general-api-py} -o /usr/local/bin/general-api.py && chmod +x /usr/local/bin/general-api.py
  - curl -sf ${ARTIFACT_URL:general-index-html} -o /var/www/index.html

  - systemctl daemon-reload
  - systemctl enable decloud-agent && systemctl start decloud-agent
  - ln -sf /etc/systemd/system/welcome.service
      /etc/systemd/system/multi-user.target.wants/welcome.service
  - systemctl start welcome

  # SSH reload (SIGHUP — no port downtime)
  - systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null || true
```

**1.2 Create `base-system.yaml`**
Path: `DeCloud.Builds/base-templates/base-system.yaml`

Content: extract the content currently duplicated across all three system VM
YAMLs — bootcmd, qemu-guest-agent package, watchdog write_files and timers,
wg-quick override.conf. The role YAMLs keep only their role-specific content.

**1.3 Strip base content from role YAMLs**

For each of `dht/cloud-init.yaml`, `blockstore/cloud-init.yaml`,
`relay/cloud-init.yaml`:
- Remove bootcmd block (moves to `base-system.yaml`)
- Remove qemu-guest-agent from packages
- Remove wg-mesh-watchdog.sh, watchdog.service, watchdog.timer write_files
- Remove wg-quick override.conf write_files
- Remove qemu-guest-agent runcmd symlink
- Keep: role env file, role systemd service, role nginx config, role runcmd

**1.4 Create `tenant-vms/general/cloud-init.yaml`** (role layer only)
Move `general-vm-cloudinit.yaml` content to `DeCloud.Builds/tenant-vms/general/`
and strip all content that moves to `base-tenant.yaml`:
- Remove machine-id, snapd, SSH CA, attestation agent, welcome server
- Keep: chpasswd bootcmd, general-specific write_files

**1.5 Move `general-api.py` and `index.html` to `DeCloud.Builds`**
Source:
  `NodeAgent/CloudInit/Templates/general/assets/general-api.py`
  `NodeAgent/CloudInit/Templates/general/assets/index.html`
Destination:
  `DeCloud.Builds/tenant-vms/general/assets/general-api.py`
  `DeCloud.Builds/tenant-vms/general/assets/index.html`

**1.6 Move `decloud-agent` Go source to `DeCloud.Builds`**
Source:
  Wherever the agent source currently lives (likely in a separate repo or
  embedded in NodeAgent)
Destination:
  `DeCloud.Builds/tenant-vms/general/src/`

**1.7 Add `build-attestation-agent` job to `release-binaries.yml`**
Follow exact same pattern as `build-dht` and `build-blockstore`:
```yaml
build-attestation-agent:
  defaults:
    run:
      working-directory: tenant-vms/general/src
  # same Go build + SHA256 + upload-artifact steps
```
Release assets:
  `decloud-agent-amd64`, `decloud-agent-amd64.sha256`
  `decloud-agent-arm64`, `decloud-agent-arm64.sha256`

**1.8 Create `tenant-vms/compute-artifact-constants.sh`**
Same pattern as `system-vms/compute-artifact-constants.sh` but processes
`tenant-vms/` directories. Or extend the existing script to cover both trees.

---

### Phase 2 — Orchestrator: TemplateComposer and seeder updates

**2.1 Implement `TemplateComposer`**
Path: `src/Orchestrator/Services/TemplateComposer.cs`

```csharp
public static class TemplateComposer
{
    /// <summary>
    /// Merge a base cloud-init YAML fragment with a role-specific layer.
    /// The base layer provides infrastructure primitives (packages, agents,
    /// SSH CA, watchdog). The role layer provides application services.
    ///
    /// Merge rules:
    ///   packages:    union (deduplicated, sorted)
    ///   write_files: base entries first, then role entries
    ///   bootcmd:     base entries first, then role entries
    ///   runcmd:      base entries first, then role entries
    ///   Scalar keys: role wins (hostname, manage_etc_hosts, disable_root,
    ///                ssh_pwauth, final_message, package_update, package_upgrade)
    ///   #cloud-config header: emitted once
    /// </summary>
    public static string Compose(string baseYaml, string roleYaml)
```

Implementation approach — string-based section detection, not a YAML parser:
```
1. Strip #cloud-config header from both inputs
2. Extract each top-level section by detecting unindented "key:" lines
3. For list sections (packages, write_files, bootcmd, runcmd):
     collect base items + role items, deduplicate (packages only), output
4. For scalar sections: role value wins if present, else base value
5. Prefix output with #cloud-config
```

This avoids a YAML library dependency while remaining deterministic.
Since we control both inputs (no user-submitted YAML at compose time),
the string-based approach is safe.

Unit test cases to write:
- Base only (role is empty)
- Role only (base is empty)
- Both have packages — union
- Both have runcmd — base first
- Role has hostname, base does not — role value appears
- Both have hostname — role value wins
- Neither has a section — section absent from output

**2.2 Update `SystemVmTemplateSeeder` to use `TemplateComposer`**

Currently: cloud-init string constants (`DHT_CLOUD_INIT`, `BLOCKSTORE_CLOUD_INIT`,
`RELAY_CLOUD_INIT`) embed the full cloud-init including base content.

Target: the seeder fetches `base-system.yaml` and `{role}/cloud-init.yaml`
from `DeCloud.Builds` (same pattern as `FetchCloudInitAsync` already
implemented), then calls `TemplateComposer.Compose`.

```csharp
private async Task<string> BuildCloudInitAsync(string role, CancellationToken ct)
{
    var baseYaml = await FetchCloudInitAsync("base-templates/base-system", ct);
    var roleYaml = await FetchCloudInitAsync($"system-vms/{role}", ct);
    return TemplateComposer.Compose(baseYaml, roleYaml);
}
```

Remove `DHT_CLOUD_INIT`, `BLOCKSTORE_CLOUD_INIT`, `RELAY_CLOUD_INIT` string
constants — they're replaced by the compose step.

**2.3 Seed `platform-general` template in `TemplateSeederService`**

Add to `TemplateSeederService.SeedAsync()` before marketplace templates:

```csharp
// Seed platform-general — base tenant VM template
var generalSeeder = new GeneralVmTemplateSeeder(_dataStore, _httpClient, _logger);
await generalSeeder.SeedAsync(ct);
```

`GeneralVmTemplateSeeder` follows the same pattern as `SystemVmTemplateSeeder`:
- Fetches `base-tenant.yaml` and `tenant-vms/general/cloud-init.yaml`
- Calls `TemplateComposer.Compose`
- Declares artifacts: `decloud-agent-amd64`, `decloud-agent-arm64` (HTTPS),
  `general-api-py`, `general-index-html` (data: URI)
- Revision-aware upsert

SHA256 and data: URI constants generated by:
```bash
cd DeCloud.Builds/tenant-vms
bash compute-artifact-constants.sh
```

**2.4 Store SSH CA public key on Node record**

**`Node` model** — add one property:
```csharp
public string? SshCaPublicKey { get; set; }
```

**`NodeRegistrationRequest`** — add one property:
```csharp
public string? SshCaPublicKey { get; init; }
```

**`NodeService.RegisterNodeAsync`** — stamp it:
```csharp
node.SshCaPublicKey = request.SshCaPublicKey;
```

**Node agent `OrchestratorClient.RegisterAsync`** — read and send:
```csharp
var caPublicKey = await File.ReadAllTextAsync("/etc/ssh/decloud_ca.pub", ct);
request.SshCaPublicKey = caPublicKey.Trim();
```

**2.5 Build complete variable set in `VmService`**

In `AssignVmToNodeAsync` step 6.5, extend variable building:

```csharp
// SSH Authorized Keys Block
variables["__SSH_AUTHORIZED_KEYS_BLOCK__"] = BuildSshKeysBlock(sshPublicKey);

// Password blocks
variables["__SSH_PASSWORD_AUTH__"] = hasPassword ? "true" : "false";
variables["__PASSWORD_CONFIG_BLOCK__"] = BuildPasswordBlock(password);
variables["__ADMIN_PASSWORD__"] = password ?? "";

// SSH CA public key (from node record — not from node disk)
variables["__CA_PUBLIC_KEY__"] = IndentForYaml(selectedNode.SshCaPublicKey ?? "", 6);

// Runtime identity
variables["__VM_ID__"] = vm.Id;
variables["__VM_NAME__"] = vm.Name;
variables["__HOSTNAME__"] = vm.Name;
variables["__ORCHESTRATOR_URL__"] = _configuration["OrchestratorUrl"] ?? "";
variables["__TIMESTAMP__"] = DateTime.UtcNow.ToString("O");
variables["__NODE_ID__"] = selectedNode.Id;
```

These are the same values currently built on the node in `LibvirtVmManager`
steps 1–5. Moving them here means the node receives a complete document.

Extract `BuildSshKeysBlock`, `BuildPasswordBlock` as private static methods
in `VmService`. Their logic is identical to what's in `LibvirtVmManager` today.

**2.6 Assign `platform-general` TemplateId to all general VMs**

In `VmService.CreateVmAsync`, when `TemplateId` is null and `VmType` is General:
```csharp
if (string.IsNullOrEmpty(request.TemplateId)
    && (request.Spec.VmType == VmType.General || request.Spec.VmType == null))
{
    var generalTemplate = await _dataStore.GetTemplateBySlugAsync("platform-general");
    if (generalTemplate is not null)
    {
        vm.TemplateId = generalTemplate.Id;
    }
}
```

---

### Phase 3 — Node agent: cleanup

**3.1 Delete `MergeCustomUserDataWithBaseConfig`**
All VMs now receive complete UserData. This method has no remaining callers.

**3.2 Delete `CloudInitTemplateService`**
PATH B in `LibvirtVmManager` is gone. `CloudInitTemplateService` has no
remaining callers. Delete the class and its interface.
Remove DI registration from `Program.cs`.

**3.3 Delete `CloudInit/Templates/general/` and `.b64` files**
```
CloudInit/
  Templates/
    general/              ← DELETE
    decloud-agent-amd64.b64  ← DELETE
    decloud-agent-arm64.b64  ← DELETE
    decloud-agent.b64        ← DELETE
```

**3.4 Simplify `LibvirtVmManager.CreateCloudInitIso`**

Delete steps 1–5 (variable building):
- Step 1: `var variables = new Dictionary<string, string>` block — DELETE
- Step 2: SSH keys block builder — DELETE
- Step 3: Password block builder — DELETE
- Step 4: Password SSH commands block — DELETE
- Step 5: CA public key reader from disk — DELETE

Step 6 becomes:
```csharp
// Cloud-init is delivered complete by the orchestrator.
// All variable substitution has already been performed.
string cloudInitYaml;
if (!string.IsNullOrEmpty(spec.CloudInitUserData))
{
    cloudInitYaml = spec.CloudInitUserData;
}
else
{
    // Fallback for legacy or direct deploys with no template.
    // This path should not be reached in normal operation.
    _logger.LogWarning(
        "VM {VmId}: No CloudInitUserData provided — using minimal config",
        spec.Id);
    cloudInitYaml = BuildMinimalCloudInit(spec.Name, spec.SshPublicKey);
}
```

Steps 6.5 (`EnsureQemuGuestAgent`) and 6.6 (GPU proxy shim) are unchanged.
Steps 7+ (ISO creation, domain definition, start) are unchanged.

---

## 5. Sequencing and dependencies

```
Phase 1 must complete before Phase 2:
  base-tenant.yaml and base-system.yaml must exist before TemplateComposer
  can compose them into templates.
  decloud-agent must be in CI before GeneralVmTemplateSeeder can reference
  its SHA256 and release URL.

Phase 2 must complete before Phase 3:
  All VMs must have complete UserData from the orchestrator before
  MergeCustomUserDataWithBaseConfig and CloudInitTemplateService are deleted
  from the node.

Phase 2.4 (CA key on Node) can run in parallel with Phase 1 —
  it is a pure additive change.

Phase 2.5 (variable building in VmService) must complete and be verified
  on a real deployment before Phase 3.4 (delete node-side steps 1–5).
```

---

## 6. Verification checklist

**After Phase 1:**
- [ ] `base-tenant.yaml` and `base-system.yaml` contain no role-specific content
- [ ] Role-layer YAMLs contain no base content (no bootcmd, no watchdog)
- [ ] `TemplateComposer.Compose(base-system, dht-role)` produces valid cloud-init
- [ ] `TemplateComposer.Compose(base-tenant, general-role)` produces valid cloud-init
- [ ] `compute-artifact-constants.sh` discovers `tenant-vms/general/assets/`

**After Phase 2:**
- [ ] `platform-general` template visible in MongoDB
- [ ] System VM templates no longer embed base content in their cloud-init string
- [ ] New general VM gets `TemplateId = platform-general`
- [ ] `selectedNode.SshCaPublicKey` is populated on node records
- [ ] Orchestrator-built UserData for general VM contains no `__PLACEHOLDER__`
      values after substitution

**After Phase 3:**
- [ ] Fresh general VM deploy succeeds with no `CloudInitTemplateService`
- [ ] Attestation agent running in VM (`systemctl status decloud-agent`)
- [ ] Welcome server running (`curl localhost`)
- [ ] SSH certificate auth working (`ssh -i decloud_ca.pub ...`)
- [ ] Marketplace VM deploy succeeds — base layer present
      (`decloud-agent` running, SSH CA set up)
- [ ] `CloudInit/Templates/` folder deleted
- [ ] `CloudInitTemplateService.cs` deleted
- [ ] `MergeCustomUserDataWithBaseConfig` deleted
- [ ] `LibvirtVmManager.CreateCloudInitIso` steps 1–5 deleted
