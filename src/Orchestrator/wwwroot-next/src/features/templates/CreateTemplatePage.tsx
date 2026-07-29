import { useEffect, useState, type ReactNode, type CSSProperties } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import { useCreateTemplate, useUpdateTemplate } from "./useTemplates";
import {
  emptyForm, fromTemplate, type TemplateForm,
  GPU_MODES, BANDWIDTH_TIERS, VISIBILITIES, PRICING_MODELS,
  type EnvRow, type PortRow, type VarRow,
} from "./templateForm";

// Phase 5 · authoring slice 2 — the full create/edit form. Covers every
// CreateTemplateRequest field except Artifacts (their own endpoint). Create
// POSTs CreateTemplateRequest; edit PUTs the full VmTemplate (see useTemplates).

const inputStyle: CSSProperties = { width: "100%" };
const sectionStyle: CSSProperties = { display: "flex", flexDirection: "column", gap: "var(--space-2)", padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" };

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

  async function save() {
    try {
      const result = isEdit && existing.data
        ? await update.mutateAsync({ loaded: existing.data, form })
        : await create.mutateAsync(form);
      if (result.warnings?.length) setWarnings(result.warnings);
      else navigate("/my-templates");
    } catch { /* err shown below */ }
  }

  // Field helpers return JSX (not components) so inputs don't remount / lose focus.
  const field = (label: string, node: ReactNode) => <label className="field" style={{ flex: 1 }}><span>{label}</span>{node}</label>;
  const txt = (label: string, k: keyof TemplateForm, ph = "") =>
    field(label, <input style={inputStyle} value={form[k] as string} placeholder={ph} onChange={(e) => set(k, e.target.value)} />);
  const num = (label: string, k: keyof TemplateForm, min = 0) =>
    field(label, <input style={inputStyle} type="number" min={min} value={form[k] as number} onChange={(e) => set(k, Number(e.target.value))} />);
  const area = (label: string, k: keyof TemplateForm, rows = 4, ph = "") =>
    field(label, <textarea style={{ ...inputStyle, minHeight: rows * 22, fontFamily: k === "cloudInitTemplate" ? "var(--font-mono)" : undefined }} value={form[k] as string} placeholder={ph} onChange={(e) => set(k, e.target.value)} />);
  const chk = (label: string, k: keyof TemplateForm) =>
    <label className="field" style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
      <input type="checkbox" checked={form[k] as boolean} onChange={(e) => set(k, e.target.checked)} /><span>{label}</span>
    </label>;
  const sel = (label: string, k: keyof TemplateForm, opts: [number, string][]) =>
    field(label, <select style={inputStyle} value={form[k] as number} onChange={(e) => set(k, Number(e.target.value))}>
      {opts.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
    </select>);
  const row = (children: ReactNode) => <div style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap" }}>{children}</div>;

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: 760 }}>
      <Link to="/my-templates" className="nav-link" style={{ alignSelf: "start" }}>← My Templates</Link>
      <h1 style={{ margin: 0 }}>{isEdit ? "Edit template" : "New template"}</h1>
      {isEdit && existing.isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}

      {/* Basics */}
      <div style={sectionStyle}>
        <strong>Basics</strong>
        {row(<>{txt("Name", "name", "My App")}{txt("Slug", "slug", "my-app")}</>)}
        {area("Description", "description", 2, "One-line summary shown on cards.")}
        {area("Long description (Markdown)", "longDescription", 5)}
        {row(<>{txt("Category", "category", "web")}{txt("Version", "version")}{txt("Tags (comma-separated)", "tags", "web, node")}</>)}
        {txt("Icon URL", "iconUrl", "https://…/icon.png")}
      </div>

      {/* Author & links */}
      <div style={sectionStyle}>
        <strong>Author & links</strong>
        {row(<>{txt("Author name", "authorName")}{txt("Revenue wallet", "authorRevenueWallet")}</>)}
        {row(<>{txt("License", "license", "MIT")}{txt("Source URL", "sourceUrl", "https://github.com/…")}</>)}
      </div>

      {/* Resources */}
      <div style={sectionStyle}>
        <strong>Resources</strong>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Recommended spec (the default at deploy).</span>
        {row(<>{num("vCPU", "recCpu", 1)}{num("Memory (MB)", "recMemMb", 128)}{num("Disk (GB)", "recDiskGb", 1)}{txt("Image ID", "recImageId", "ubuntu-22.04")}</>)}
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Minimum spec (the floor a deployer can't go below).</span>
        {row(<>{num("vCPU", "minCpu", 1)}{num("Memory (MB)", "minMemMb", 128)}{num("Disk (GB)", "minDiskGb", 1)}</>)}
        {row(<>{chk("Requires GPU", "requiresGpu")}{sel("GPU mode", "defaultGpuMode", GPU_MODES)}{sel("Bandwidth tier", "defaultBandwidthTier", BANDWIDTH_TIERS)}</>)}
        {txt("GPU requirement", "gpuRequirement", "e.g. nvidia, 16GB VRAM")}
      </div>

      {/* Runtime */}
      <div style={sectionStyle}>
        <strong>Runtime</strong>
        {txt("Container image", "containerImage", "optional")}
        {area("Cloud-init template", "cloudInitTemplate", 8, "#cloud-config …")}
        {row(<>{txt("Default access URL", "defaultAccessUrl", "https://${DECLOUD_DOMAIN}:8080")}{txt("Default username", "defaultUsername")}</>)}
        {chk("Generate a password at deploy", "useGeneratedPassword")}
      </div>

      {/* Environment variables */}
      <div style={sectionStyle}>
        <strong>Default environment variables</strong>
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
        <strong>Exposed ports</strong>
        {form.ports.map((p, i) => (
          <div key={i} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <input style={{ width: 90 }} placeholder="port" value={p.port} onChange={(ev) => set("ports", form.ports.map((r, j) => j === i ? { ...r, port: ev.target.value } : r))} />
            <input style={{ width: 90 }} placeholder="tcp" value={p.protocol} onChange={(ev) => set("ports", form.ports.map((r, j) => j === i ? { ...r, protocol: ev.target.value } : r))} />
            <input style={{ flex: 1, minWidth: 120 }} placeholder="description" value={p.description} onChange={(ev) => set("ports", form.ports.map((r, j) => j === i ? { ...r, description: ev.target.value } : r))} />
            <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={p.isPublic} onChange={(ev) => set("ports", form.ports.map((r, j) => j === i ? { ...r, isPublic: ev.target.checked } : r))} />public</label>
            <button className="btn-ghost" onClick={() => set("ports", form.ports.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("ports", [...form.ports, { port: "", protocol: "tcp", description: "", isPublic: false } as PortRow])}>+ Add port</button>
      </div>

      {/* Template variables */}
      <div style={sectionStyle}>
        <strong>User variables</strong>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Collected from the deployer and substituted into cloud-init.</span>
        {form.variables.map((v, i) => (
          <div key={i} style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <input style={{ width: 150 }} placeholder="NAME" value={v.name} onChange={(ev) => set("variables", form.variables.map((r, j) => j === i ? { ...r, name: ev.target.value } : r))} />
            <input style={{ flex: 1, minWidth: 120 }} placeholder="description" value={v.description} onChange={(ev) => set("variables", form.variables.map((r, j) => j === i ? { ...r, description: ev.target.value } : r))} />
            <input style={{ width: 130 }} placeholder="default" value={v.defaultValue} onChange={(ev) => set("variables", form.variables.map((r, j) => j === i ? { ...r, defaultValue: ev.target.value } : r))} />
            <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={v.required} onChange={(ev) => set("variables", form.variables.map((r, j) => j === i ? { ...r, required: ev.target.checked } : r))} />required</label>
            <button className="btn-ghost" onClick={() => set("variables", form.variables.filter((_, j) => j !== i))} aria-label="Remove">✕</button>
          </div>
        ))}
        <button className="btn-ghost" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }} onClick={() => set("variables", [...form.variables, { name: "", description: "", defaultValue: "", required: false } as VarRow])}>+ Add variable</button>
      </div>

      {/* Pricing & visibility */}
      <div style={sectionStyle}>
        <strong>Pricing & visibility</strong>
        {row(<>{sel("Visibility", "visibility", VISIBILITIES)}{sel("Pricing model", "pricingModel", PRICING_MODELS)}</>)}
        {row(<>{num("Template price", "templatePrice")}{num("Est. cost / hour", "estimatedCostPerHour")}</>)}
      </div>

      {warnings && (
        <div style={sectionStyle}>
          <strong style={{ color: "var(--warning)" }}>Saved as draft, with warnings:</strong>
          <ul style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{warnings.map((w, i) => <li key={i}>{w}</li>)}</ul>
          <button className="btn-primary" style={{ alignSelf: "start" }} onClick={() => navigate("/my-templates")}>Continue to My Templates</button>
        </div>
      )}
      {err && <p style={{ color: "var(--danger)" }}>{err.message || "Save failed."}</p>}

      {!warnings && (
        <div style={{ display: "flex", gap: 8 }}>
          <button className="btn-primary" disabled={busy || !form.name.trim() || !form.slug.trim()} onClick={save}>
            {busy ? "Saving…" : isEdit ? "Save changes" : "Create draft"}
          </button>
          <Link className="btn-ghost" to="/my-templates">Cancel</Link>
        </div>
      )}
    </div>
  );
}
