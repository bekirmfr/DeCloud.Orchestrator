import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import {
    useDeleteTemplate, usePublishTemplate, useCancelReview, useReviseTemplate,
    templateStatus, statusNum,
} from "./useTemplates";
import { TemplateInspectSections } from "./TemplateInspectSections";

// Phase 5 · Author template view (/my-templates/:id). Read-only detail of the
// author's OWN template at any status — fetched by id, which is status-agnostic,
// so drafts and in-review templates open here too. Lets an author inspect a
// template without having to Revise first, and carries the same lifecycle
// actions as the My Templates card. Shares the read-only body (specs / variables
// / artifacts / ports / cloud-init) with the admin inspect page via
// TemplateInspectSections.

const sm = { fontSize: "var(--text-sm)" } as const;

export function MyTemplateViewPage() {
    const { id = "" } = useParams();
    const { api } = useAuth();
    const navigate = useNavigate();
    const { data: t, isLoading, isError } = useTemplate(api, id);
    const del = useDeleteTemplate(api);
    const publish = usePublishTemplate(api);
    const cancel = useCancelReview(api);
    const revise = useReviseTemplate(api);

    const busy = del.isPending || publish.isPending || cancel.isPending || revise.isPending;
    const err = (del.error || publish.error || cancel.error || revise.error) as Error | undefined;

    async function onRevise() {
        if (!t) return;
        if (!window.confirm(`Start a new draft revision of “${t.name}”? The published version stays live until you publish the new one.`)) return;
        const rev = await revise.mutateAsync(t.id).catch(() => null);
        if (rev?.id) navigate(`/my-templates/${rev.id}/edit`);
    }
    function onDelete() {
        if (!t) return;
        if (window.confirm(`Delete “${t.name}”? This can't be undone.`))
            del.mutate(t.id, { onSuccess: () => navigate("/my-templates") });
    }
    function onSubmit() {
        if (!t) return;
        if (window.confirm(`Submit “${t.name}” for review? You won't be able to edit it while it's in review.`))
            publish.mutate(t.id, { onSuccess: () => navigate("/my-templates") });
    }
    function onCancel() {
        if (!t) return;
        cancel.mutate(t.id, { onSuccess: () => navigate("/my-templates") });
    }

    if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
    if (isError || !t) return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
            <p style={{ color: "var(--danger)" }}>Couldn't load this template.</p>
            <Link className="btn-ghost" to="/my-templates" style={{ alignSelf: "start" }}>← Back to my templates</Link>
        </div>
    );

    const st = templateStatus(t.status);
    const s = statusNum(t.status);
    const canEdit = s === 0 || s === 4;     // Draft or Rejected
    const canSubmit = s === 0 || s === 4;

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
            <Link className="btn-ghost" to="/my-templates" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>← Back to my templates</Link>

            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)" }}>
                <div>
                    <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
                        <h1 style={{ margin: 0 }}>{t.name}</h1>
                        <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-sm)" }}>v{t.version}</span>
                        <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
                    </div>
                    <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>{t.description}</p>
                    {t.category && <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{t.category}</p>}
                </div>
                <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap", flexWrap: "wrap", justifyContent: "flex-end" }}>
                    {canEdit && <Link className="btn-ghost" to={`/my-templates/${t.id}/edit`} style={sm}>Edit</Link>}
                    {canSubmit && <button className="btn-ghost" style={sm} disabled={busy} onClick={onSubmit}>{s === 4 ? "Resubmit" : "Submit for review"}</button>}
                    {s === 3 && <button className="btn-ghost" style={sm} disabled={busy} onClick={onCancel}>Cancel review</button>}
                    {s === 1 && <button className="btn-ghost" style={sm} disabled={busy} onClick={onRevise}>Revise</button>}
                    <button className="btn-ghost" style={{ ...sm, color: "var(--danger)" }} disabled={busy} onClick={onDelete}>Delete</button>
                </div>
            </div>

            {err && <p style={{ color: "var(--danger)" }}>{err.message || "Action failed."}</p>}

            <TemplateInspectSections template={t} />
        </div>
    );
}
