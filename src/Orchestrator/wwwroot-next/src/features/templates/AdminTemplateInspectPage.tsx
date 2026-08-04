import { useState } from "react";
import type { ReactNode, CSSProperties } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import type { TemplateArtifact } from "../deploy/deploySubmit";
import { useApproveTemplate, useRejectTemplate, templateStatus } from "./useTemplates";
import {
    QUALITY_TIERS, GPU_MODES, BANDWIDTH_TIERS, VARIABLE_KINDS, WATCHER_SCOPES,
    ARTIFACT_TYPES, CHECK_STRATEGIES, VISIBILITIES, PRICING_MODELS, enumNum,
} from "./templateForm";

// Phase 5 · Admin template inspect (read-only). Reviewers land here from the
// queue to read the full template — specs, variables (as a table), artifacts
// (full SHA + decode modal), ports, and the cloud-init (role vs composed, as
// tabs) that will actually run — before approving or rejecting. Fetches by id
// (status-agnostic), never opens the author's editable form.

const label = (pairs: [number, string][], v?: string | number) =>
    pairs.find(([n]) => n === enumNum(v, NaN))?.[1] ?? (v == null ? "—" : String(v));
const mb = (b?: number | null) => (b ? `${Math.round(b / 1024 ** 2)} MB` : "—");
const gb = (b?: number | null) => (b ? `${Math.round(b / 1024 ** 3)} GB` : "—");
const mono: CSSProperties = { fontFamily: "var(--font-mono)" };

const preStyle: CSSProperties = {
    whiteSpace: "pre-wrap", wordBreak: "break-word", margin: 0,
    padding: "var(--space-3)", background: "var(--surface-2)", borderRadius: "var(--radius-sm)",
    fontFamily: "var(--font-mono)", fontSize: "var(--text-xs)", maxHeight: 360, overflow: "auto",
};
const th: CSSProperties = { padding: "6px 10px", fontWeight: "var(--fw-medium)", whiteSpace: "nowrap" };
const td: CSSProperties = { padding: "6px 10px", verticalAlign: "top" };

const sec = (title: string, body: ReactNode) => (
    <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
        <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{title}</strong>
        {body}
    </section>
);

// Parse a data: URI into its media type + base64 payload (null for HTTPS URLs).
function inlineParts(sourceUrl?: string): { mime: string; b64: string } | null {
    if (!sourceUrl || !sourceUrl.startsWith("data:")) return null;
    const comma = sourceUrl.indexOf(",");
    if (comma < 0) return null;
    const header = sourceUrl.slice(5, comma);          // e.g. "text/x-sh;base64"
    const mime = header.split(";")[0] || "text/plain";
    return { mime, b64: sourceUrl.slice(comma + 1) };
}
function decodeB64(b64: string): string {
    try {
        const bin = atob(b64);
        return new TextDecoder().decode(Uint8Array.from(bin, (c) => c.charCodeAt(0)));
    } catch {
        return "(unable to decode)";
    }
}

function TabBtn({ active, onClick, children }: { active: boolean; onClick: () => void; children: ReactNode }) {
    return (
        <button onClick={onClick} style={{
            padding: "6px 12px", background: "none", border: "none", cursor: "pointer",
            fontSize: "var(--text-sm)", fontWeight: active ? "var(--fw-medium)" : "normal",
            color: active ? "var(--accent)" : "var(--text-tertiary)",
            borderBottom: active ? "2px solid var(--accent)" : "2px solid transparent",
        }}>{children}</button>
    );
}

function ArtifactModal({ artifact, onClose }: { artifact: TemplateArtifact; onClose: () => void }) {
    const parts = inlineParts(artifact.sourceUrl);
    const isImage = parts?.mime.startsWith("image/") ?? false;
    const subHead: CSSProperties = { fontSize: "var(--text-xs)", color: "var(--text-tertiary)", marginBottom: 4 };
    return (
        <div onClick={onClose} style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.6)", display: "flex", alignItems: "center", justifyContent: "center", padding: "var(--space-4)", zIndex: 50 }}>
            <div onClick={(e) => e.stopPropagation()} style={{ background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: "var(--space-4)", maxWidth: 760, width: "100%", maxHeight: "80vh", overflow: "auto", display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: "var(--space-2)" }}>
                    <strong style={mono}>{artifact.name}</strong>
                    <button className="btn-ghost" onClick={onClose} aria-label="Close">✕</button>
                </div>
                <div style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)", ...mono, wordBreak: "break-all" }}>
                    {label(ARTIFACT_TYPES, artifact.type)} · {artifact.sizeBytes ? `${artifact.sizeBytes} bytes` : "size —"} · sha256 {artifact.sha256 || "—"}
                </div>

                {!parts && (
                    <div>
                        <div style={subHead}>External URL — bytes fetched &amp; verified by the node at deploy time</div>
                        <a href={artifact.sourceUrl} target="_blank" rel="noreferrer" style={{ color: "var(--accent)", wordBreak: "break-all" }}>{artifact.sourceUrl}</a>
                    </div>
                )}
                {parts && isImage && (
                    <div>
                        <div style={subHead}>Decoded (image)</div>
                        <img src={artifact.sourceUrl} alt={artifact.name} style={{ maxWidth: "100%", borderRadius: "var(--radius-sm)" }} />
                    </div>
                )}
                {parts && !isImage && (
                    <div>
                        <div style={subHead}>Decoded content</div>
                        <pre style={preStyle}>{decodeB64(parts.b64)}</pre>
                    </div>
                )}
                {parts && (
                    <div>
                        <div style={subHead}>Encoded (data URI)</div>
                        <pre style={{ ...preStyle, maxHeight: 160 }}>{artifact.sourceUrl}</pre>
                    </div>
                )}
            </div>
        </div>
    );
}

export function AdminTemplateInspectPage() {
    const { id = "" } = useParams();
    const { api } = useAuth();
    const navigate = useNavigate();
    const { data: t, isLoading, isError } = useTemplate(api, id);
    const approve = useApproveTemplate(api);
    const reject = useRejectTemplate(api);
    const [tab, setTab] = useState<"role" | "composed">("role");
    const [artifact, setArtifact] = useState<TemplateArtifact | null>(null);

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
    const activeTab = tab === "role" && !t.roleCloudInit ? "composed" : tab;

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
                <div style={{ overflowX: "auto", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)" }}>
                    <table style={{ width: "100%", borderCollapse: "collapse", fontSize: "var(--text-sm)" }}>
                        <thead>
                            <tr style={{ textAlign: "left", color: "var(--text-tertiary)", background: "var(--surface-2)" }}>
                                <th style={th}>Name</th><th style={th}>Kind</th><th style={th}>Req / scope</th><th style={th}>Default</th><th style={th}>Description</th>
                            </tr>
                        </thead>
                        <tbody>
                            {t.variables.map((v, i) => {
                                const kind = enumNum(v.kind, 0);
                                return (
                                    <tr key={i} style={{ borderTop: "1px solid var(--border-subtle)" }}>
                                        <td style={{ ...td, ...mono }}>{v.name}</td>
                                        <td style={td}>{label(VARIABLE_KINDS, kind)}</td>
                                        <td style={td}>{kind === 1 ? label(WATCHER_SCOPES, v.scope) : v.required ? "required" : "optional"}</td>
                                        <td style={{ ...td, ...mono, color: "var(--text-secondary)" }}>{v.defaultValue ?? "—"}</td>
                                        <td style={{ ...td, color: "var(--text-secondary)" }}>{v.description || "—"}</td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </table>
                </div>
            ))}

            {t.artifacts && t.artifacts.length > 0 && sec("Artifacts", (
                <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
                    {t.artifacts.map((a, i) => {
                        const inline = (a.sourceUrl ?? "").startsWith("data:");
                        return (
                            <div key={i} style={{ display: "flex", flexDirection: "column", gap: 3, padding: "8px 10px", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)", fontSize: "var(--text-sm)" }}>
                                <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
                                    <code style={mono}>{a.name}</code>
                                    <span style={{ color: "var(--text-secondary)" }}>{label(ARTIFACT_TYPES, a.type)} · {inline ? "inline" : "external"}{a.architecture ? ` · ${a.architecture}` : ""}</span>
                                    <button className="btn-ghost" style={{ fontSize: "var(--text-sm)", marginLeft: "auto" }} onClick={() => setArtifact(a)}>View content</button>
                                </div>
                                <div style={{ color: "var(--text-tertiary)", ...mono, fontSize: "var(--text-xs)", wordBreak: "break-all" }}>sha256 {a.sha256 || "—"}</div>
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

            {sec("Cloud-init", (
                <div>
                    <div style={{ display: "flex", gap: 2, borderBottom: "1px solid var(--border-subtle)", marginBottom: "var(--space-2)" }}>
                        {t.roleCloudInit && <TabBtn active={activeTab === "role"} onClick={() => setTab("role")}>Role (authored)</TabBtn>}
                        <TabBtn active={activeTab === "composed"} onClick={() => setTab("composed")}>Composed (what runs)</TabBtn>
                    </div>
                    <pre style={preStyle}>{activeTab === "role" ? t.roleCloudInit : (t.cloudInitTemplate || "—")}</pre>
                </div>
            ))}

            {artifact && <ArtifactModal artifact={artifact} onClose={() => setArtifact(null)} />}
        </div>
    );
}