import { useEffect, useState, type ReactNode, type CSSProperties } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import { useCreateTemplate, useUpdateTemplate } from "./useTemplates";
import {
  emptyForm, fromTemplate, specError, type TemplateForm,
  GPU_MODES, BANDWIDTH_TIERS, VISIBILITIES, PRICING_MODELS,
  QUALITY_TIERS, VARIABLE_KINDS, WATCHER_SCOPES, ARTIFACT_TYPES, CHECK_STRATEGIES,
  PROTOCOLS, ARCHITECTURES, IMAGE_OPTIONS, CATEGORIES,
  emptyVar, emptyPort, emptyArtifact,
  type EnvRow, type PortRow, type VarRow, type ArtifactRow, type ArtifactMode,
} from "./templateForm";

// Phase 5 · authoring slice 2 — the full create/edit form. Covers every
// CreateTemplateRequest field except Artifacts (their own endpoint). Create
// POSTs CreateTemplateRequest; edit PUTs the full VmTemplate (see useTemplates).

// Minimal working cloud-config the "↓ Starter" button drops into the cloud-init
// field — ported verbatim from the legacy my-templates.js so authors get the
// same known-good scaffold.
const CLOUD_INIT_STARTER = `#cloud-config
package_update: true
packages: []

write_files:
  - path: /usr/local/bin/setup.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      set -euo pipefail
      exec >> /var/log/setup.log 2>&1
      echo "=== Setup started $(date) ==="

      # --- your setup commands here ---

      echo "=== Setup complete $(date) ==="

runcmd:
  - /usr/local/bin/setup.sh
`;

// Per-field help shown behind an (i) icon (same token language as the deploy
// form's Hint copy, just presented as a hover/focus popup to keep this dense
// form compact).
const HELP = {
  name: "Display name shown on the marketplace card.",
  slug: "URL-safe identifier — lowercase and hyphens. Must be unique across the marketplace.",
  description: "One-line summary shown on cards and in search results.",
  longDescription: "Full write-up (Markdown) shown on the template's detail page.",
  category: "Groups the template in the marketplace.",
  recGpuVramGb: "Minimum GPU memory the workload needs, in GB (e.g. 16, 24). 0 = unspecified.",
  version: "Semantic version of this template (e.g. 1.0.0). Bump it whenever you change the template.",
  tags: "Comma-separated keywords that help people find the template.",
  iconUrl: "URL of a square icon image shown on the card.",
  authorName: "Name credited as the author. Defaults to your wallet address.",
  authorRevenueWallet: "Wallet that receives revenue for paid templates. Defaults to your wallet.",
  license: "License the template is published under, e.g. MIT.",
  sourceUrl: "Link to the source repository or a homepage.",
  recSpec: "The resources a deployment gets by default. Deployers can adjust down to the minimum below.",
  recImageId: "OS image the VM boots from. Leave OS-agnostic to use the platform default (ubuntu-22.04).",
  minSpec: "The lowest resources a deployer may pick — the template won't run reliably below this.",
  minQualityTier: "Worst scheduling tier a deploy may use (0=Guaranteed best … 3=Burstable cheapest). Higher tiers are always allowed.",
  requiresGpu: "Tick if the workload needs a GPU to run.",
  defaultGpuMode: "How the GPU is attached: None, dedicated Passthrough, or shared Proxied.",
  gpuRequirement: "Free-text note about the GPU needed, e.g. nvidia, 16GB VRAM.",
  defaultBandwidthTier: "Network tier applied by default. Unmetered has no data cap.",
  containerImage: "Optional Docker image for GPU container deployment (nodes without IOMMU). Not the OS image — leave blank for normal VMs.",
  cloudInitTemplate: "cloud-init config that provisions the VM on first boot. Reference user variables as ${NAME}.",
  defaultAccessUrl: "URL shown to the user to reach the app once deployed. Supports ${DECLOUD_DOMAIN}.",
  defaultUsername: "Default login username for the deployed app, if any.",
  useGeneratedPassword: "Generate a random password at deploy and show it to the user once.",
  envVars: "Environment variables baked into every deployment of this template.",
  ports: "Ports the app listens on. Public ports are reachable from the internet; others stay internal.",
  readiness: "Optional: how the node decides the service on this port is up. TCP connect, an HTTP GET path, or a command run in the VM.",
  variables: "Template variables. Static values are substituted into cloud-init at render; Dynamic values are resolved by the platform at runtime.",
  varKind: "Static resolves once at render (has a default / can be required). Dynamic is platform-bound and resolved at runtime.",
  varScope: "Dynamic-only: what the in-VM watcher does when this value changes — nothing, reload (SIGHUP), or restart the service.",
  varResolverKey: "Advanced: bind to a platform resolver with a different name. Defaults to the variable name.",
  artifacts: "Files attached to the template. External (HTTPS + SHA256) or inline (small text/config, or an uploaded file). Binaries must be external.",
  visibility: "Public appears in the marketplace; Private is visible only to you.",
  pricingModel: "Free, or a one-time fee charged each time someone deploys it.",
  templatePrice: "Amount charged per deploy, used when pricing is Per deploy.",
  estimatedCostPerHour: "Your estimate of the hourly running cost, shown to deployers.",
};

// (i) icon with a hover/focus popup. Self-contained (no CSS file, no dep).
function Help({ text }: { text: string }) {
  const [open, setOpen] = useState(false);
  return (
    <span style={{ position: "relative", display: "inline-flex" }}
      onMouseEnter={() => setOpen(true)} onMouseLeave={() => setOpen(false)}>
      <span role="button" tabIndex={0} aria-label={text}
        onFocus={() => setOpen(true)} onBlur={() => setOpen(false)}
        style={{ cursor: "help", width: 15, height: 15, borderRadius: "50%", border: "1px solid var(--border)", color: "var(--text-tertiary)", fontSize: 10, fontStyle: "italic", lineHeight: "13px", display: "inline-flex", alignItems: "center", justifyContent: "center", userSelect: "none", flex: "none" }}>i</span>
      {open && (
        <span role="tooltip" style={{ position: "absolute", bottom: "calc(100% + 6px)", left: 0, zIndex: 20, width: 250, padding: "8px 10px", background: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: "var(--radius-sm)", color: "var(--text-secondary)", fontSize: "var(--text-xs)", lineHeight: 1.45, boxShadow: "0 8px 24px rgba(0,0,0,0.4)", fontWeight: 400 }}>{text}</span>
      )}
    </span>
  );
}

// Read an uploaded file as a data: URI (data:{mime};base64,…) — exactly the
// inline-artifact form the backend expects in SourceUrl.
function readFileAsDataUrl(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(String(r.result));
    r.onerror = () => reject(r.error);
    r.readAsDataURL(file);
  });
}

const inputStyle: CSSProperties = { width: "100%" };
const sectionStyle: CSSProperties = { display: "flex", flexDirection: "column", gap: "var(--space-2)", padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" };
const headStyle: CSSProperties = { display: "inline-flex", alignItems: "center", gap: 6 };

export function CreateTemplatePage() {
  const { id } = useParams();
  const isEdit = !!id;
  const { api } = useAuth();
  const navigate = useNavigate();
  const existing = useTemplate(api, id ?? "");     // enabled only when id present
  const create = useCreateTemplate(api);
  const update = useUpdateTemplate(api);

  const [form, setForm] = useState<TemplateForm>(emptyForm);
  const [prefilled, setPrefilled] = useState(false);
  const [warnings, setWarnings] = useState<string[] | null>(null);

  useEffect(() => {
    if (isEdit && existing.data && !prefilled) { setForm(fromTemplate(existing.data)); setPrefilled(true); }
  }, [isEdit, existing.data, prefilled]);

  function set(k: keyof TemplateForm, v: unknown) { setForm((f) => ({ ...f, [k]: v }) as TemplateForm); }
  const busy = create.isPending || update.isPending;
  const err = (create.error || update.error) as Error | undefined;
  const specErr = specError(form);   // RecommendedSpec must meet/exceed MinimumSpec

  function insertStarter() {
    if (form.cloudInitTemplate.trim() && !window.confirm("Replace current cloud-init content with the starter template?")) return;
    set("cloudInitTemplate", CLOUD_INIT_STARTER);
  }

  async function save() {
    if (specError(form)) return;
    try {
      const result = isEdit && existing.data
        ? await update.mutateAsync({ loaded: existing.data, form })
        : await create.mutateAsync(form);
      if (result.warnings?.length) setWarnings(result.warnings);
      else navigate("/my-templates");
    } catch { /* err shown below */ }
  }

  // Field helpers return JSX (not components) so inputs don't remount / lose focus.
  const field = (label: string, node: ReactNode, help?: string) =>
    <label className="field" style={{ flex: 1 }}>
      <span style={headStyle}>{label}{help ? <Help text={help} /> : null}</span>{node}
    </label>;
  const txt = (label: string, k: keyof TemplateForm, ph = "", help?: string) =>
    field(label, <input style={inputStyle} value={form[k] as string} placeholder={ph} onChange={(e) => set(k, e.target.value)} />, help);
  const num = (label: string, k: keyof TemplateForm, min = 0, help?: string) =>
    field(label, <input style={inputStyle} type="number" min={min} value={form[k] as number} onChange={(e) => set(k, Number(e.target.value))} />, help);
  const chk = (label: string, k: keyof TemplateForm, help?: string) =>
    <label className="field" style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
      <input type="checkbox" checked={form[k] as boolean} onChange={(e) => set(k, e.target.checked)} /><span style={headStyle}>{label}{help ? <Help text={help} /> : null}</span>
    </label>;
  const sel = (label: string, k: keyof TemplateForm, opts: [number, string][], help?: string) =>
    field(label, <select style={inputStyle} value={form[k] as number} onChange={(e) => set(k, Number(e.target.value))}>
      {opts.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
    </select>, help);
  const selStr = (label: string, k: keyof TemplateForm, opts: [string, string][], help?: string) =>
    field(label, <select style={inputStyle} value={form[k] as string} onChange={(e) => set(k, e.target.value)}>
      {opts.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
    </select>, help);
  const row = (children: ReactNode) => <div style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap" }}>{children}</div>;
  const subLabel = (label: string, help: string) =>
    <span style={{ ...headStyle, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{label} <Help text={help} /></span>;
  // Patch one row in a list field (ports/variables/artifacts) immutably.
  function updRow<T>(list: T[], i: number, patch: Partial<T>): T[] { return list.map((r, j) => (j === i ? { ...r, ...patch } : r)); }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: 760 }}>
      <Link to="/my-templates" className="nav-link" style={{ alignSelf: "start" }}>← My Templates</Link>
      <h1 style={{ margin: 0 }}>{isEdit ? "Edit template" : "New template"}</h1>
      {isEdit && existing.isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}

      {/* Basics */}
      <div style={sectionStyle}>
        <strong>Basics</strong>
        {row(<>{txt("Name", "name", "My App", HELP.name)}{txt("Slug", "slug", "my-app", HELP.slug)}</>)}
        {field("Description", <textarea style={{ ...inputStyle, minHeight: 44 }} value={form.description} placeholder="One-line summary shown on cards." onChange={(e) => set("description", e.target.value)} />, HELP.description)}
        {field("Long description (Markdown)", <textarea style={{ ...inputStyle, minHeight: 110 }} value={form.longDescription} onChange={(e) => set("longDescription", e.target.value)} />, HELP.longDescription)}
        {row(<>{selStr("Category", "category", CATEGORIES, HELP.category)}{txt("Version", "version", "", HELP.version)}{txt("Tags (comma-separated)", "tags", "web, node", HELP.tags)}</>)}
        {txt("Icon URL", "iconUrl", "https://…/icon.png", HELP.iconUrl)}
      </div>

      {/* Author & links */}
      <div style={sectionStyle}>
        <strong>Author & links</strong>
        {row(<>{txt("Author name", "authorName", "", HELP.authorName)}{txt("Revenue wallet", "authorRevenueWallet", "", HELP.authorRevenueWallet)}</>)}
        {row(<>{txt("License", "license", "MIT", HELP.license)}{txt("Source URL", "sourceUrl", "https://github.com/…", HELP.sourceUrl)}</>)}
      </div>

      {/* Resources */}
      <div style={sectionStyle}>
        <strong>Resources</strong>
        {row(<>{selStr("OS image", "recImageId", IMAGE_OPTIONS, HELP.recImageId)}{sel("Min quality tier", "minQualityTier", QUALITY_TIERS, HELP.minQualityTier)}{sel("Bandwidth tier", "defaultBandwidthTier", BANDWIDTH_TIERS, HELP.defaultBandwidthTier)}</>)}
        {subLabel("Recommended spec (the default at deploy)", HELP.recSpec)}
        {row(<>{num("vCPU", "recCpu", 1)}{num("Memory (MB)", "recMemMb", 128)}{num("Disk (GB)", "recDiskGb", 1)}</>)}
        {subLabel("Minimum spec (the floor a deployer can't go below)", HELP.minSpec)}
        {row(<>{num("vCPU", "minCpu", 1)}{num("Memory (MB)", "minMemMb", 128)}{num("Disk (GB)", "minDiskGb", 1)}</>)}
        {chk("Requires GPU", "requiresGpu", HELP.requiresGpu)}
        {form.requiresGpu && (
          <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)", paddingLeft: 22 }}>
            {row(<>{sel("GPU mode", "defaultGpuMode", GPU_MODES, HELP.defaultGpuMode)}{num("GPU VRAM (GB)", "recGpuVramGb", 0, HELP.recGpuVramGb)}</>)}
            {txt("GPU requirement", "gpuRequirement", "e.g. nvidia, 16GB VRAM", HELP.gpuRequirement)}
          </div>
        )}
      </div>

      {/* Runtime */}
      <div style={sectionStyle}>
        <strong>Runtime</strong>
        {txt("GPU container image", "containerImage", "optional — Docker image for GPU containers", HELP.containerImage)}
        <label className="field">
          <span style={{ display: "flex", alignItems: "center", gap: 6 }}>
            Cloud-init template <Help text={HELP.cloudInitTemplate} />
            <button type="button" className="btn-ghost" style={{ marginLeft: "auto", fontSize: "var(--text-xs)", padding: "2px 8px" }} onClick={insertStarter}>↓ Starter</button>
          </span>
          <textarea style={{ width: "100%", minHeight: 180, fontFamily: "var(--font-mono)" }} value={form.cloudInitTemplate} placeholder="#cloud-config …" onChange={(e) => set("cloudInitTemplate", e.target.value)} />
        </label>
        {row(<>{txt("Default access URL", "defaultAccessUrl", "https://${DECLOUD_DOMAIN}:8080", HELP.defaultAccessUrl)}{txt("Default username", "defaultUsername", "", HELP.defaultUsername)}</>)}
        {chk("Generate a password at deploy", "useGeneratedPassword", HELP.useGeneratedPassword)}
      </div>

      {/* Environment variables */}
      <div style={sectionStyle}>
        <strong style={headStyle}>Default environment variables <Help text={HELP.envVars} /></strong>
        {form.envVars.map((e, i) => (
          <div key={i} style={{ display: "flex", gap: 8 }}>
            <input style={{ flex: 1 }} placeholder="KEY" value={e.key} onChange={(ev) => set("envVars", form.envVars.map((r, j) => j === i ? { ...r, key: ev.target.value } : r))} />
            <input style={{ flex: 2 }} placeholder="value" value={e.value} onChange={(ev) => set("envVars", form.envVars.map((r, j) => j === i ? { ...r, value: ev.target.value } : r))} />
            <button className="btn-ghost" onClick={() => set("envVars", form.envVars.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("envVars", [...form.envVars, { key: "", value: "" } as EnvRow])}>+ Add variable</button>
      </div>

      {/* Exposed ports */}
      <div style={sectionStyle}>
        <strong style={headStyle}>Exposed ports <Help text={HELP.ports} /></strong>
        {form.ports.map((p, i) => (
          <div key={i} style={{ display: "flex", flexDirection: "column", gap: 6, padding: "8px", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)" }}>
            <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <input style={{ width: 80 }} placeholder="port" value={p.port} onChange={(ev) => set("ports", updRow(form.ports, i, { port: ev.target.value }))} />
              <select style={{ width: 110 }} value={p.protocol} onChange={(ev) => set("ports", updRow(form.ports, i, { protocol: ev.target.value }))}>
                {PROTOCOLS.map((pr) => <option key={pr} value={pr}>{pr}</option>)}
              </select>
              <input style={{ flex: 1, minWidth: 120 }} placeholder="description" value={p.description} onChange={(ev) => set("ports", updRow(form.ports, i, { description: ev.target.value }))} />
              <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={p.isPublic} onChange={(ev) => set("ports", updRow(form.ports, i, { isPublic: ev.target.checked }))} />public</label>
              <button className="btn-ghost" onClick={() => set("ports", form.ports.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
            </div>
            <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}>
              <input type="checkbox" checked={p.hasReadiness} onChange={(ev) => set("ports", updRow(form.ports, i, { hasReadiness: ev.target.checked }))} />
              <span style={headStyle}>Readiness check <Help text={HELP.readiness} /></span>
            </label>
            {p.hasReadiness && (
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap", paddingLeft: 22 }}>
                <select style={{ width: 130 }} value={p.strategy} onChange={(ev) => set("ports", updRow(form.ports, i, { strategy: Number(ev.target.value) }))}>
                  {CHECK_STRATEGIES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
                </select>
                {p.strategy === 1 && <input style={{ flex: 1, minWidth: 100 }} placeholder="/health" value={p.httpPath} onChange={(ev) => set("ports", updRow(form.ports, i, { httpPath: ev.target.value }))} />}
                {p.strategy === 2 && <input style={{ flex: 1, minWidth: 100, fontFamily: "var(--font-mono)" }} placeholder="pg_isready -U postgres" value={p.execCommand} onChange={(ev) => set("ports", updRow(form.ports, i, { execCommand: ev.target.value }))} />}
                <input style={{ width: 100 }} type="number" min={30} placeholder="timeout s" value={p.timeoutSeconds} onChange={(ev) => set("ports", updRow(form.ports, i, { timeoutSeconds: Number(ev.target.value) }))} />
                <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={p.livenessCheck} onChange={(ev) => set("ports", updRow(form.ports, i, { livenessCheck: ev.target.checked }))} />liveness</label>
              </div>
            )}
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("ports", [...form.ports, { ...emptyPort } as PortRow])}>+ Add port</button>
      </div>

      {/* Template variables */}
      <div style={sectionStyle}>
        <strong style={headStyle}>User variables <Help text={HELP.variables} /></strong>
        {form.variables.map((v, i) => (
          <div key={i} style={{ display: "flex", flexDirection: "column", gap: 6, padding: "8px", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)" }}>
            <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <input style={{ width: 160, fontFamily: "var(--font-mono)" }} placeholder="NAME" value={v.name} onChange={(ev) => set("variables", updRow(form.variables, i, { name: ev.target.value }))} />
              <select style={{ width: 200 }} value={v.kind} onChange={(ev) => set("variables", updRow(form.variables, i, { kind: Number(ev.target.value) }))}>
                {VARIABLE_KINDS.map(([val, l]) => <option key={val} value={val}>{l}</option>)}
              </select>
              <span style={{ ...headStyle }}><Help text={HELP.varKind} /></span>
              <button className="btn-ghost" style={{ marginLeft: "auto" }} onClick={() => set("variables", form.variables.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
            </div>
            <input style={{ flex: 1, minWidth: 120 }} placeholder="description" value={v.description} onChange={(ev) => set("variables", updRow(form.variables, i, { description: ev.target.value }))} />
            {v.kind === 0 ? (
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <input style={{ width: 150 }} placeholder="default value" value={v.defaultValue} onChange={(ev) => set("variables", updRow(form.variables, i, { defaultValue: ev.target.value }))} />
                <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={v.required} onChange={(ev) => set("variables", updRow(form.variables, i, { required: ev.target.checked }))} />required</label>
              </div>
            ) : (
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}>
                  <span style={headStyle}>Scope <Help text={HELP.varScope} /></span>
                  <select style={{ width: 170 }} value={v.scope} onChange={(ev) => set("variables", updRow(form.variables, i, { scope: Number(ev.target.value) }))}>
                    {WATCHER_SCOPES.map(([val, l]) => <option key={val} value={val}>{l}</option>)}
                  </select>
                </label>
              </div>
            )}
            <label className="field" style={{ flexDirection: "row", alignItems: "center", gap: 6 }}>
              <span style={{ ...headStyle, fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>Resolver key <Help text={HELP.varResolverKey} /></span>
              <input style={{ flex: 1, fontFamily: "var(--font-mono)" }} placeholder="(defaults to name)" value={v.resolverKey} onChange={(ev) => set("variables", updRow(form.variables, i, { resolverKey: ev.target.value }))} />
            </label>
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("variables", [...form.variables, { ...emptyVar } as VarRow])}>+ Add variable</button>
      </div>

      {/* Artifacts */}
      <div style={sectionStyle}>
        <strong style={headStyle}>Artifacts <Help text={HELP.artifacts} /></strong>
        {form.artifacts.map((a, i) => (
          <div key={i} style={{ display: "flex", flexDirection: "column", gap: 6, padding: "8px", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)" }}>
            <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
              <input style={{ width: 150, fontFamily: "var(--font-mono)" }} placeholder="name" value={a.name} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { name: ev.target.value }))} />
              <select style={{ width: 110 }} value={a.type} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { type: Number(ev.target.value) }))}>
                {ARTIFACT_TYPES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
              <select style={{ width: 90 }} value={a.architecture} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { architecture: ev.target.value }))}>
                {ARCHITECTURES.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
              </select>
              <select style={{ width: 120 }} value={a.mode} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { mode: ev.target.value as ArtifactMode }))}>
                <option value="external">External URL</option>
                <option value="inlineText">Inline text</option>
                <option value="inlineFile">Inline file</option>
              </select>
              <button className="btn-ghost" style={{ marginLeft: "auto" }} onClick={() => set("artifacts", form.artifacts.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
            </div>
            <input style={{ width: "100%" }} placeholder="description" value={a.description} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { description: ev.target.value }))} />
            {a.mode === "external" && (<>
              <input style={{ width: "100%" }} placeholder="https://…/binary-amd64" value={a.sourceUrl} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { sourceUrl: ev.target.value }))} />
              <input style={{ width: "100%", fontFamily: "var(--font-mono)" }} placeholder="sha256 — 64 hex, required" value={a.sha256} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { sha256: ev.target.value }))} />
            </>)}
            {a.mode === "inlineText" && (<>
              <input style={{ width: 240 }} placeholder="content type (e.g. text/x-sh)" value={a.contentType} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { contentType: ev.target.value }))} />
              <textarea style={{ width: "100%", minHeight: 90, fontFamily: "var(--font-mono)" }} placeholder="paste text content…" value={a.content} onChange={(ev) => set("artifacts", updRow(form.artifacts, i, { content: ev.target.value }))} />
            </>)}
            {a.mode === "inlineFile" && (
              <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                <input type="file" onChange={async (ev) => {
                  const file = ev.target.files?.[0];
                  if (!file) return;
                  const dataUrl = await readFileAsDataUrl(file);
                  set("artifacts", updRow(form.artifacts, i, { sourceUrl: dataUrl, fileName: file.name }));
                }} />
                {a.fileName
                  ? <span style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{a.fileName}</span>
                  : (a.sourceUrl.startsWith("data:") && <span style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>inline data (~{Math.round(a.sourceUrl.length * 0.75 / 1024)} KB)</span>)}
              </div>
            )}
            {a.type === 0 && a.mode !== "external" && <span style={{ fontSize: "var(--text-xs)", color: "var(--warning)" }}>Binary artifacts must use an external HTTPS URL.</span>}
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("artifacts", [...form.artifacts, { ...emptyArtifact } as ArtifactRow])}>+ Add artifact</button>
      </div>

      {/* Pricing & visibility */}
      <div style={sectionStyle}>
        <strong>Pricing & visibility</strong>
        {row(<>{sel("Visibility", "visibility", VISIBILITIES, HELP.visibility)}{sel("Pricing model", "pricingModel", PRICING_MODELS, HELP.pricingModel)}</>)}
        {row(<>{num("Template price", "templatePrice", 0, HELP.templatePrice)}{num("Est. cost / hour", "estimatedCostPerHour", 0, HELP.estimatedCostPerHour)}</>)}
      </div>

      {warnings && (
        <div style={sectionStyle}>
          <strong style={{ color: "var(--warning)" }}>Saved as draft, with warnings:</strong>
          <ul style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
          <button className="btn-primary" style={{ alignSelf: "start" }} onClick={() => navigate("/my-templates")}>Continue to My Templates</button>
        </div>
      )}
      {err && <p style={{ color: "var(--danger)" }}>{err.message || "Save failed."}</p>}
      {specErr && <p style={{ color: "var(--danger)" }}>{specErr}</p>}

      {!warnings && (
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn-primary" disabled={busy || !!specErr || !form.name.trim() || !form.slug.trim() || !form.category} onClick={save}>
            {busy ? "Saving…" : isEdit ? "Save changes" : "Create draft"}
          </button>
          <Link className="btn-ghost" to="/my-templates">Cancel</Link>
        </div>
      )}
    </div>
  );
}
