import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate } from "../deploy/useDeploy";
import { useApproveTemplate, useRejectTemplate, templateStatus } from "./useTemplates";
import { VISIBILITIES, PRICING_MODELS } from "./templateForm";
import { TemplateInspectSections, label } from "./TemplateInspectSections";

// Phase 5 · Admin template inspect (read-only). Reviewers land here from the
// queue to read the full template — specs, variables, artifacts, ports, and the
// role-vs-composed cloud-init — before approving or rejecting. Fetches by id
// (status-agnostic), never opens the author's editable form. The read-only body
// is shared with the author view page (MyTemplateViewPage) via
// TemplateInspectSections; this page owns the review header + actions.

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

            <TemplateInspectSections template={t} />
        </div>
    );
}
