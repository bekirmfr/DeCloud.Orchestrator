import { useState, type CSSProperties } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate, useDeploy } from "./useDeploy";
import type { TemplateSpec } from "./deploySubmit";
import {
  validRepoUrl, validEnvKey, parseDotEnv, buildEnvironmentVariables,
} from "./repoDeploy";

// Phase 5 · "Deploy from GitHub". A faithful React port of repo-deploy.js: the
// friendly fields (URL / ref / port / database / env / deploy key) are composed
// into the base64 DEPLOY_CONF_B64 / APP_ENV_B64 / DEPLOY_KEY_B64 payloads in
// repoDeploy.ts and sent as environmentVariables on the platform-repo-deploy
// template — the same user-supplied-variable channel every template uses.

const DATABASES = [
  { value: "none", label: "None" },
  { value: "postgres", label: "PostgreSQL (DATABASE_URL injected)" },
  { value: "redis", label: "Redis (REDIS_URL injected)" },
];

const input: CSSProperties = {
  padding: "6px 10px", borderRadius: "var(--radius-sm)",
  border: "1px solid var(--border)", background: "var(--surface-0)", color: "var(--text)",
  fontSize: "var(--text-sm)", width: "100%",
};
const errText: CSSProperties = { color: "var(--danger)", fontSize: "var(--text-xs)" };

interface EnvRow { key: string; value: string }

/** Per-row env-key error (empty string = ok). Mirrors validateEnvRows(). */
function envRowError(key: string, index: number, rows: EnvRow[]): string {
  const k = key.trim();
  if (!k) return "";
  if (!validEnvKey(k)) return "Letters, digits and underscores only; cannot start with a digit.";
  if (k.toUpperCase() === "PORT") return "PORT comes from the App port field above.";
  const firstIdx = rows.findIndex((r) => r.key.trim().toLowerCase() === k.toLowerCase());
  if (firstIdx !== index) return "Duplicate key.";
  return "";
}

export function RepoDeployPage() {
  const { api } = useAuth();
  const navigate = useNavigate();
  const template = useTemplate(api, "platform-repo-deploy");
  const deploy = useDeploy(api);

  const [vmName, setVmName] = useState("");
  const [sourceUrl, setSourceUrl] = useState("");
  const [sourceRef, setSourceRef] = useState("");
  const [appPort, setAppPort] = useState("");
  const [database, setDatabase] = useState("none");
  const [isPrivate, setIsPrivate] = useState(false);
  const [deployKey, setDeployKey] = useState("");
  const [envRows, setEnvRows] = useState<EnvRow[]>([]);
  const [envPaste, setEnvPaste] = useState("");
  const [showAdvanced, setShowAdvanced] = useState(false);
  const [cpu, setCpu] = useState(2);
  const [memMb, setMemMb] = useState(4096);
  const [diskGb, setDiskGb] = useState(25);

  // ── validation (mirrors repo-deploy.js validateForm) ──────────────────────
  const url = sourceUrl.trim();
  const urlErr = !url ? "Repository URL is required." : !validRepoUrl(url) ? "Use https://host/owner/repo or git@host:owner/repo." : "";
  const portRaw = appPort.trim();
  const portNum = parseInt(portRaw, 10);
  const portErr = portRaw === "" ? "" : (!Number.isInteger(portNum) || portNum < 1 || portNum > 65535) ? "Port must be between 1 and 65535." : "";
  const keyErr = !isPrivate ? "" : !deployKey.trim() ? "Paste your private deploy key, or turn off “Private repository”." : !deployKey.includes("BEGIN") ? "This doesn’t look like a private key (missing BEGIN header)." : "";
  const envErrs = envRows.map((r, i) => envRowError(r.key, i, envRows));
  const envOk = envErrs.every((e) => !e);
  const canDeploy = !!vmName.trim() && !urlErr && !portErr && !keyErr && envOk && !!template.data && !deploy.isPending;

  function addEnvRow() { setEnvRows((rows) => [...rows, { key: "", value: "" }]); }
  function updateEnvRow(i: number, patch: Partial<EnvRow>) {
    setEnvRows((rows) => rows.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  }
  function removeEnvRow(i: number) { setEnvRows((rows) => rows.filter((_, idx) => idx !== i)); }
  function commitPaste() {
    if (!envPaste.trim()) return;
    setEnvRows((rows) => {
      const merged = [...rows];
      for (const { key, value } of parseDotEnv(envPaste)) {
        const existing = merged.find((r) => r.key.trim() === key);
        if (existing) existing.value = value;
        else merged.push({ key, value });
      }
      return merged;
    });
    setEnvPaste("");
  }

  async function onDeploy() {
    if (!canDeploy || !template.data) return;
    const env: Record<string, string> = {};
    for (const r of envRows) { const k = r.key.trim(); if (k) env[k] = r.value; }

    const environmentVariables = buildEnvironmentVariables(
      { sourceUrl: url, sourceRef: sourceRef.trim() || "HEAD", appPort: portRaw || "8080", database },
      env,
      isPrivate ? deployKey.trim() : undefined,
    );
    const customSpec: TemplateSpec = {
      virtualCpuCores: cpu || 2,
      memoryBytes: (memMb || 4096) * 1024 ** 2,
      diskBytes: (diskGb || 25) * 1024 ** 3,
      imageId: "ubuntu-22.04",
      gpuMode: 0,
      requiresGpu: false,
      qualityTier: 1,
      bandwidthTier: 3,
      replicationFactor: 0,
    };
    try {
      const result = await deploy.mutateAsync({
        templateId: template.data.id,
        payload: { vmName: vmName.trim(), environmentVariables, customSpec },
      });
      navigate(`/vms/${result.vmId}`);
    } catch {
      /* deploy.error is surfaced below */
    }
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: 640 }}>
      <Link to="/deploy" className="nav-link" style={{ alignSelf: "start" }}>← Deploy</Link>
      <h1 style={{ margin: 0 }}>Deploy from Repository</h1>
      <p style={{ margin: 0, color: "var(--text-secondary)", lineHeight: 1.5 }}>
        Point at a Git repository and the port your app listens on; the platform builds your code
        — a Dockerfile wins, then a compose file, then Nixpacks — and runs it.
      </p>

      <label className="field">
        <span>VM name</span>
        <input style={input} value={vmName} onChange={(e) => setVmName(e.target.value)} placeholder="my-app" />
      </label>

      <label className="field">
        <span>Repository URL</span>
        <input style={input} value={sourceUrl} onChange={(e) => setSourceUrl(e.target.value)}
          placeholder="https://github.com/owner/repo" />
        {urlErr && sourceUrl && <span style={errText}>{urlErr}</span>}
      </label>

      <div style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap" }}>
        <label className="field" style={{ flex: "1 1 160px" }}>
          <span>Branch / ref</span>
          <input style={input} value={sourceRef} onChange={(e) => setSourceRef(e.target.value)} placeholder="HEAD" />
        </label>
        <label className="field" style={{ flex: "1 1 120px" }}>
          <span>App port</span>
          <input style={input} value={appPort} onChange={(e) => setAppPort(e.target.value)} placeholder="8080" />
          {portErr && <span style={errText}>{portErr}</span>}
        </label>
        <label className="field" style={{ flex: "1 1 200px" }}>
          <span>Database</span>
          <select style={input} value={database} onChange={(e) => setDatabase(e.target.value)}>
            {DATABASES.map((d) => <option key={d.value} value={d.value}>{d.label}</option>)}
          </select>
        </label>
      </div>

      {/* Private repo */}
      <label className="field">
        <span style={{ display: "flex", gap: 8, alignItems: "center" }}>
          <input type="checkbox" checked={isPrivate} onChange={(e) => setIsPrivate(e.target.checked)} />
          Private repository (paste a read-only deploy key)
        </span>
        {isPrivate && (
          <>
            <textarea style={{ ...input, minHeight: 90, fontFamily: "var(--font-mono)" }}
              value={deployKey} onChange={(e) => setDeployKey(e.target.value)}
              placeholder="-----BEGIN OPENSSH PRIVATE KEY-----" />
            {keyErr && <span style={errText}>{keyErr}</span>}
          </>
        )}
      </label>

      {/* Env vars */}
      <div className="field">
        <span>Environment variables</span>
        {envRows.map((r, i) => (
          <div key={i} style={{ display: "flex", gap: 8, alignItems: "flex-start", flexWrap: "wrap" }}>
            <div style={{ flex: "1 1 160px" }}>
              <input style={input} placeholder="KEY" value={r.key} onChange={(e) => updateEnvRow(i, { key: e.target.value })} />
              {envErrs[i] && <span style={errText}>{envErrs[i]}</span>}
            </div>
            <input style={{ ...input, flex: "1 1 200px" }} placeholder="value" value={r.value} onChange={(e) => updateEnvRow(i, { value: e.target.value })} />
            <button type="button" className="btn-ghost" onClick={() => removeEnvRow(i)} aria-label="Remove variable">✕</button>
          </div>
        ))}
        <div style={{ display: "flex", gap: 8 }}>
          <button type="button" className="btn-ghost" onClick={addEnvRow} style={{ fontSize: "var(--text-sm)" }}>+ Add variable</button>
        </div>
        <textarea style={{ ...input, minHeight: 60, fontFamily: "var(--font-mono)" }}
          placeholder="…or paste a .env block here" value={envPaste}
          onChange={(e) => setEnvPaste(e.target.value)} onBlur={commitPaste} />
        {envPaste.trim() && (
          <button type="button" className="btn-ghost" onClick={commitPaste} style={{ fontSize: "var(--text-sm)", alignSelf: "start" }}>
            Add from .env
          </button>
        )}
      </div>

      {/* Resources */}
      <button type="button" className="btn-ghost" onClick={() => setShowAdvanced((s) => !s)} style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>
        {showAdvanced ? "▾" : "▸"} Resources
      </button>
      {showAdvanced && (
        <div style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap" }}>
          <label className="field" style={{ flex: "1 1 100px" }}>
            <span>vCPU</span>
            <input style={input} type="number" min={1} value={cpu} onChange={(e) => setCpu(Number(e.target.value))} />
          </label>
          <label className="field" style={{ flex: "1 1 120px" }}>
            <span>Memory (MB)</span>
            <input style={input} type="number" min={512} value={memMb} onChange={(e) => setMemMb(Number(e.target.value))} />
          </label>
          <label className="field" style={{ flex: "1 1 120px" }}>
            <span>Disk (GB)</span>
            <input style={input} type="number" min={10} value={diskGb} onChange={(e) => setDiskGb(Number(e.target.value))} />
          </label>
        </div>
      )}

      {deploy.isError && (
        <p style={errText}>{(deploy.error as Error)?.message || "Deployment failed."}</p>
      )}

      <button className="btn-primary" disabled={!canDeploy} onClick={onDeploy} style={{ alignSelf: "start" }}>
        {deploy.isPending ? "Deploying…" : "Deploy"}
      </button>
    </div>
  );
}
