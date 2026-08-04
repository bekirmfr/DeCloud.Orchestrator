import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { usePendingTemplates, useApproveTemplate, useRejectTemplate } from "./useTemplates";

// Phase 5 · Admin review queue (slice 4). Community templates land in
// PendingReview on submit; this is the only place they become Published (approve)
// or Rejected (reject, with a reason shown to the author). Admin-gated by the
// AdminGuard route + canAccessAdmin; the server enforces [Authorize(Roles="Admin")]
// regardless, so this UI gate is courtesy, not security.

const sm = { fontSize: "var(--text-sm)" } as const;

export function AdminTemplatesPage() {
  const { api } = useAuth();
  const { data: pending, isLoading, isError } = usePendingTemplates(api);
  const approve = useApproveTemplate(api);
  const reject = useRejectTemplate(api);

  const busy = approve.isPending || reject.isPending;
  const err = (approve.error || reject.error) as Error | undefined;

  function onReject(id: string, name: string) {
    const reason = window.prompt(`Reject “${name}” — reason (shown to the author):`);
    if (reason == null) return;                       // cancelled
    if (!reason.trim()) { window.alert("A rejection reason is required."); return; }
    reject.mutate({ templateId: id, reason: reason.trim() });
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 900 }}>
      <div>
        <h1 style={{ margin: 0 }}>Review queue</h1>
        <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
          Community templates awaiting review. Approve to publish to the marketplace, or reject with a reason.
        </p>
      </div>

      {err && <p style={{ color: "var(--danger)" }}>{err.message || "Action failed."}</p>}
      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Couldn't load the review queue.</p>}
      {pending && pending.length === 0 && (
        <p style={{ color: "var(--text-secondary)" }}>Nothing awaiting review.</p>
      )}

      {pending && pending.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
          {pending.map((t) => (
            <div key={t.id} style={{ display: "flex", alignItems: "center", gap: "var(--space-3)", padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" }}>
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
                  <strong style={{ fontSize: "var(--text-md)" }}>{t.name}</strong>
                  <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>v{t.version}</span>
                </div>
                <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                  {t.category ? `${t.category} · ` : ""}{t.description}
                </div>
                <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
                  by {t.authorName || "unknown author"}
                </div>
              </div>
              <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                <Link className="btn-ghost" to={`/my-templates/${t.slug || t.id}/edit`} style={sm}>Inspect</Link>
                <button className="btn-ghost" style={sm} disabled={busy}
                  onClick={() => { if (window.confirm(`Approve and publish “${t.name}”?`)) approve.mutate(t.id); }}>
                  Approve
                </button>
                <button className="btn-ghost" style={{ ...sm, color: "var(--danger)" }} disabled={busy}
                  onClick={() => onReject(t.id, t.name)}>
                  Reject
                </button>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
