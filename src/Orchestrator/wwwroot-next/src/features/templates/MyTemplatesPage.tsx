import { Link, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import {
    useMyTemplates, useDeleteTemplate, templateStatus, statusNum,
    usePublishTemplate, useCancelReview, useReviseTemplate, useMyTemplateEarnings,
} from "./useTemplates";

// Phase 5 · My Templates. List + lifecycle (submit / cancel / revise / delete)
// + a read-only View. Re-skinned to the Meridian component layer (global.css):
// eyebrow kicker, display-face title, refined cards, haloed status dots, tinted
// status badges, mono earnings — instead of the earlier flat inline cards.

const sm = { fontSize: "var(--text-sm)" } as const;

// Template status (0 Draft · 1 Published · 2 Archived · 3 PendingReview · 4 Rejected)
// → the shared status-dot / badge tones.
const dotClass = (s: number) => (s === 1 ? "ok" : s === 3 ? "warn" : s === 4 ? "err" : "idle");
const badgeClass = (s: number) => (s === 1 ? "ok" : s === 3 ? "warn" : s === 4 ? "danger" : "neutral");

export function MyTemplatesPage() {
    const { api } = useAuth();
    const navigate = useNavigate();
    const { data: templates, isLoading, isError } = useMyTemplates(api);
    const del = useDeleteTemplate(api);
    const publish = usePublishTemplate(api);
    const cancel = useCancelReview(api);
    const revise = useReviseTemplate(api);
    const { data: earnings } = useMyTemplateEarnings(api);

    const busy = del.isPending || publish.isPending || cancel.isPending || revise.isPending;
    const actionErr = (publish.error || cancel.error || revise.error || del.error) as Error | undefined;

    async function onRevise(id: string, name: string) {
        if (!window.confirm(`Start a new draft revision of “${name}”? The published version stays live until you publish the new one.`)) return;
        const rev = await revise.mutateAsync(id).catch(() => null);
        if (rev?.id) navigate(`/my-templates/${rev.id}/edit`);
    }

    const count = templates?.length ?? 0;

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-5)", maxWidth: 900 }}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-end", gap: "var(--space-3)" }}>
                <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                    <span className="eyebrow">Marketplace · authoring</span>
                    <h1 style={{ margin: 0 }}>My Templates</h1>
                    <p style={{ margin: 0, color: "var(--text-secondary)", maxWidth: 540 }}>
                        Templates you've authored. Drafts and in-review templates are deployable only by you, for testing.
                    </p>
                </div>
                <Link className="btn-primary" to="/my-templates/new" style={{ whiteSpace: "nowrap" }}>+ New template</Link>
            </div>

            {actionErr && <p style={{ color: "var(--danger)" }}>{actionErr.message || "Action failed."}</p>}
            {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
            {isError && <p style={{ color: "var(--danger)" }}>Couldn't load your templates.</p>}
            {templates && count === 0 && (
                <div className="card" style={{ padding: "var(--space-6)", textAlign: "center", color: "var(--text-secondary)" }}>
                    You haven't authored any templates yet. <Link to="/my-templates/new" style={{ color: "var(--text-accent)" }}>Create one</Link> to get started.
                </div>
            )}

            {templates && count > 0 && (
                <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
                    {templates.map((t) => {
                        const st = templateStatus(t.status);
                        const s = statusNum(t.status);
                        const e = earnings?.[t.id];
                        const canEdit = s === 0 || s === 4;        // Draft or Rejected
                        const canSubmit = s === 0 || s === 4;
                        return (
                            <div key={t.id} className="card" style={{ display: "flex", gap: "var(--space-4)", alignItems: "flex-start", padding: "var(--space-4) var(--space-5)" }}>
                                <span className={`status-dot ${dotClass(s)}`} style={{ marginTop: 7 }} aria-hidden="true" />
                                <div style={{ flex: 1, minWidth: 0 }}>
                                    <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
                                        <Link to={`/my-templates/${t.id}`} style={{ fontFamily: "var(--font-display)", fontWeight: "var(--fw-medium)", fontSize: "var(--text-md)", color: "var(--text-primary)" }}>{t.name}</Link>
                                        <span className={`badge ${badgeClass(s)}`}>{st.label}</span>
                                    </div>
                                    <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-sm)", marginTop: 2, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                                        {t.category ? `${t.category} · ` : ""}{t.description}
                                    </div>
                                    {e && e.net > 0 && (
                                        <div className="mono" style={{ color: "var(--success)", fontSize: "var(--text-sm)", marginTop: 4 }}>
                                            Earned {e.net.toFixed(2)} USDC · {e.deploys} paid {e.deploys === 1 ? "deploy" : "deploys"}
                                        </div>
                                    )}
                                </div>
                                <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap", flexShrink: 0, flexWrap: "wrap", justifyContent: "flex-end" }}>
                                    <Link className="btn-ghost" to={`/my-templates/${t.id}`} style={sm}>View</Link>
                                    {canEdit && <Link className="btn-ghost" to={`/my-templates/${t.id}/edit`} style={sm}>Edit</Link>}
                                    {canSubmit && (
                                        <button className="btn-ghost" style={sm} disabled={busy}
                                            onClick={() => { if (window.confirm(`Submit “${t.name}” for review? You won't be able to edit it while it's in review.`)) publish.mutate(t.id); }}>
                                            {s === 4 ? "Resubmit" : "Submit for review"}
                                        </button>
                                    )}
                                    {s === 3 && <button className="btn-ghost" style={sm} disabled={busy} onClick={() => cancel.mutate(t.id)}>Cancel review</button>}
                                    {s === 1 && <button className="btn-ghost" style={sm} disabled={busy} onClick={() => onRevise(t.id, t.name)}>Revise</button>}
                                    <button className="btn-ghost" style={{ ...sm, color: "var(--danger)" }} disabled={busy}
                                        onClick={() => { if (window.confirm(`Delete “${t.name}”? This can't be undone.`)) del.mutate(t.id); }}>
                                        Delete
                                    </button>
                                </div>
                            </div>
                        );
                    })}
                </div>
            )}
        </div>
    );
}