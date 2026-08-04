import { useParams, useNavigate, Link } from "react-router-dom";
import type { ReactNode } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import { useApproveTemplate, useRejectTemplate, templateStatus } from "./useTemplates";
import {
    QUALITY_TIERS, GPU_MODES, BANDWIDTH_TIERS, VARIABLE_KINDS, WATCHER_SCOPES,
    ARTIFACT_TYPES, CHECK_STRATEGIES, VISIBILITIES, PRICING_MODELS, enumNum,
} from "./templateForm";

// Phase 5 · Admin template inspect (read-only). Reviewers land here from the
// queue to read the full template — specs, variables, artifacts, ports, and the
// cloud-init that will actually run — before approving or rejecting. It fetches
// by id (status-agnostic), never opens the author's editable form, and cannot
// mutate the template except via the approve/reject actions.

const label = (pairs: [number, string][], v?: string | number) =>
    pairs.find(([n]) => n === enumNum(v, NaN))?.[1] ?? (v == null ? "—" : String(v));
const mb = (b?: number | null) => (b ? `${Math.round(b / 1024 ** 2)} MB` : "—");
const gb = (b?: number | null) => (b ? `${Math.round(b / 1024 ** 3)} GB` : "—");

const preStyle = {
    whiteSpace: "pre-wrap" as const, wordBreak: "break-word" as const, margin: 0,
    padding: "var(--space-3)", background: "var(--surface-2)", borderRadius: "var(--radius-sm)",
    fontFamily: "var(--font-mono)", fontSize: "var(--text-xs)", maxHeight: 360, overflow: "auto",
};
const sec = (title: string, body: ReactNode) => (
    <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
        <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{title}</strong>
        {body}
    </section>
);

export function AdminTemplateInspectPage() {
    const { id = "" } = useParams();
    const { api } = useAuth();
    const navigate = useNavigate();
    const { data: t, isLoading, isError } = useTemplate(api, id);
    const approve = useApproveTemplate(api);
    const reject = useRejectTemplate(api);
    const busy = approve.isPending || reject.isPending;
    const err = (approve.error || reject.error) as Error | undefined;

    function onApprove() {
        if (!t) return;
        if (window.confirm(`Approve and publish “${t.name}”?`)) approve.mutate(t.id, { onSuccess: () => navigate("/admin/templates") });
    }
    function onReject() {
        if (!t) return;
        const reason = window.prompt(`Reject “${t.name}” — reason (shown to the author):`);
        if (reason == null) return;
        if (!reason.trim()) { window.alert("A rejection reason is required."); return; }
        reject.mutate({ templateId: t.id, reason: reason.trim() }, { onSuccess: () => navigate("/admin/templates") });
    }

    if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
    if (isError || !t) return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
            <p style={{ color: "var(--danger)" }}>Couldn't load this template.</p>
            <Link className="btn-ghost" to="/admin/templates" style={{ alignSelf: "start" }}>← Back to review queue</Link>
        </div>
    );

    const st = templateStatus(t.status);
    const rec = t.recommendedSpec, min = t.minimumSpec;

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
            <Link className="btn-ghost" to="/admin/templates" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>← Back to review queue</Link>

            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)" }}>
                <div>
                    <div style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
                        <h1 style={{ margin: 0 }}>{t.name}</h1>
                        <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-sm)" }}>v{t.version}</span>
                        <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
                    </div>
                    <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>{t.description}</p>
                    <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
                        {t.category ? `${t.category} · ` : ""}by {t.authorName || "unknown"} · {label(VISIBILITIES, t.visibility)} · {label(PRICING_MODELS, t.pricingModel)}
                    </p>
                </div>
                <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                    <button className="btn-primary" disabled={busy} onClick={onApprove}>Approve</button>
                    <button className="btn-ghost" style={{ color: "var(--danger)" }} disabled={busy} onClick={onReject}>Reject</button>
                </div>
            </div>

            {err && <p style={{ color: "var(--danger)" }}>{err.message || "Action failed."}</p>}

            {sec("Resources", (
                <div style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)", display: "flex", flexDirection: "column", gap: 4 }}>
                    <span>Recommended: {rec?.virtualCpuCores ?? "—"} vCPU · {mb(rec?.memoryBytes)} · {gb(rec?.diskBytes)} · image {rec?.imageId || "OS-agnostic"} · tier {label(QUALITY_TIERS, rec?.qualityTier)}</span>
                    <span>Minimum: {min?.virtualCpuCores ?? "—"} vCPU · {mb(min?.memoryBytes)} · {gb(min?.diskBytes)} · tier {label(QUALITY_TIERS, min?.qualityTier)}</span>
                    <span>GPU: {t.requiresGpu ? label(GPU_MODES, t.defaultGpuMode) : "None"} · Bandwidth: {label(BANDWIDTH_TIERS, t.defaultBandwidthTier)}</span>
                </div>
            ))}

            {t.variables && t.variables.length > 0 && sec("Variables", (
                <div style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: "var(--text-sm)" }}>
                    {t.variables.map((v, i) => (
                        <div key={i} style={{ color: "var(--text-secondary)" }}>
                            <code style={{ fontFamily: "var(--font-mono)" }}>{v.name}</code> · {label(VARIABLE_KINDS, v.kind)}
                            {enumNum(v.kind, 0) === 1 ? ` · ${label(WATCHER_SCOPES, v.scope)}` : v.required ? " · required" : " · optional"}
                            {v.defaultValue ? ` · default “${v.defaultValue}”` : ""}{v.description ? ` — ${v.description}` : ""}
                        </div>
                    ))}
                </div>
            ))}

            {t.artifacts && t.artifacts.length > 0 && sec("Artifacts", (
                <div style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: "var(--text-sm)" }}>
                    {t.artifacts.map((a, i) => {
                        const inline = (a.sourceUrl ?? "").startsWith("data:");
                        return (
                            <div key={i} style={{ color: "var(--text-secondary)" }}>
                                <code style={{ fontFamily: "var(--font-mono)" }}>{a.name}</code> · {label(ARTIFACT_TYPES, a.type)} · {inline ? "inline" : "external"}
                                {a.architecture ? ` · ${a.architecture}` : ""}{a.sha256 ? ` · sha256 ${a.sha256.slice(0, 12)}…` : ""}
                            </div>
                        );
                    })}
                </div>
            ))}

            {t.exposedPorts && t.exposedPorts.length > 0 && sec("Ports", (
                <div style={{ display: "flex", flexDirection: "column", gap: 4, fontSize: "var(--text-sm)" }}>
                    {t.exposedPorts.map((p, i) => (
                        <div key={i} style={{ color: "var(--text-secondary)" }}>
                            {p.port}/{p.protocol} · {p.isPublic ? "public" : "internal"}
                            {p.readinessCheck ? ` · readiness ${label(CHECK_STRATEGIES, p.readinessCheck.strategy)}${p.readinessCheck.httpPath ? ` ${p.readinessCheck.httpPath}` : ""}` : ""}
                        </div>
                    ))}
                </div>
            ))}

            {t.roleCloudInit && sec("Role cloud-init (authored)", <pre style={preStyle}>{t.roleCloudInit}</pre>)}
            {sec("Composed cloud-init (what runs)", <pre style={preStyle}>{t.cloudInitTemplate || "—"}</pre>)}
        </div>
    );
}