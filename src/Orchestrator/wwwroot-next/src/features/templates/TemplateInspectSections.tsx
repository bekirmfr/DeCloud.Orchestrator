import { useState } from "react";
import type { ReactNode, CSSProperties } from "react";
import type { VmTemplate, TemplateArtifact } from "../deploy/deploySubmit";
import {
    QUALITY_TIERS, GPU_MODES, BANDWIDTH_TIERS, VARIABLE_KINDS, WATCHER_SCOPES,
    ARTIFACT_TYPES, CHECK_STRATEGIES, enumNum,
} from "./templateForm";

// Shared, read-only template inspection body — the specs / variables / artifacts
// / ports / cloud-init sections. Used by both the admin review page
// (AdminTemplateInspectPage, with an Approve/Reject header) and the author view
// page (MyTemplateViewPage, with a Revise/Delete header). Keeping it here avoids
// duplicating ~150 lines of render across the two, exactly like NodeSections.
// The page owns the header; this owns everything below it.

/** Map an enum ordinal (numeric or wire-name) to its label. Exported for page headers. */
export const label = (pairs: [number, string][], v?: string | number) =>
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

/** Read-only detail sections for a template. The caller renders the header. */
export function TemplateInspectSections({ template: t }: { template: VmTemplate }) {
    const [tab, setTab] = useState<"role" | "composed">("role");
    const [artifact, setArtifact] = useState<TemplateArtifact | null>(null);

    const rec = t.recommendedSpec, min = t.minimumSpec;
    const activeTab = tab === "role" && !t.roleCloudInit ? "composed" : tab;

    return (
        <>
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
        </>
    );
}
