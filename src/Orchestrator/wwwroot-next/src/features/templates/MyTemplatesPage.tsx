import { Link, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import {
  useMyTemplates, useDeleteTemplate, templateStatus, statusNum,
  usePublishTemplate, useCancelReview, useReviseTemplate,
} from "./useTemplates";

// Phase 5 · My Templates. Slice 1: list + status + delete + create/edit entry.
// Slice 3: author-side lifecycle — submit-for-review / cancel-review / revise,
// shown per status. Transitions:
//   Draft(0)/Rejected(4) → Submit for review (PATCH /publish)
//   PendingReview(3)     → Cancel review (POST /cancel-review) → Draft
//   Published(1)         → Revise (POST /revise) → new Draft revision to edit

const sm = { fontSize: "var(--text-sm)" } as const;

export function MyTemplatesPage() {
  const { api } = useAuth();
  const navigate = useNavigate();
  const { data: templates, isLoading, isError } = useMyTemplates(api);
  const del = useDeleteTemplate(api);
  const publish = usePublishTemplate(api);
  const cancel = useCancelReview(api);
  const revise = useReviseTemplate(api);

  const busy = del.isPending || publish.isPending || cancel.isPending || revise.isPending;
  const actionErr = (publish.error || cancel.error || revise.error || del.error) as Error | undefined;

  async function onRevise(id: string, name: string) {
    if (!window.confirm(`Start a new draft revision of “${name}”? The published version stays live until you publish the new one.`)) return;
    const rev = await revise.mutateAsync(id).catch(() => null);
    if (rev?.id) navigate(`/my-templates/${rev.id}/edit`);
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 900 }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)" }}>
        <div>
          <h1 style={{ margin: 0 }}>My Templates</h1>
          <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
            Templates you've authored. Drafts and in-review templates are deployable only by you, for testing.
          </p>
        </div>
        <Link className="btn-primary" to="/my-templates/new" style={{ whiteSpace: "nowrap" }}>+ New template</Link>
      </div>

      {actionErr && <p style={{ color: "var(--danger)" }}>{actionErr.message || "Action failed."}</p>}
      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Couldn't load your templates.</p>}
      {templates && templates.length === 0 && (
        <p style={{ color: "var(--text-secondary)" }}>You haven't authored any templates yet. Create one to get started.</p>
      )}

      {templates && templates.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
          {templates.map((t) => {
            const st = templateStatus(t.status);
            const s = statusNum(t.status);
            const canEdit = s === 0 || s === 4;        // Draft or Rejected
            const canSubmit = s === 0 || s === 4;      // → submit/resubmit for review
            return (
              <div key={t.id} style={{ display: "flex", alignItems: "center", gap: "var(--space-3)", padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" }}>
                <div style={{ flex: 1, minWidth: 0 }}>
                  <div style={{ display: "flex", gap: 8, alignItems: "baseline" }}>
                    <strong style={{ fontSize: "var(--text-md)" }}>{t.name}</strong>
                    <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
                  </div>
                  <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                    {t.category ? `${t.category} · ` : ""}{t.description}
                  </div>
                </div>
                <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                  {canEdit && <Link className="btn-ghost" to={`/my-templates/${t.slug || t.id}/edit`} style={sm}>Edit</Link>}
                  {canSubmit && (
                    <button className="btn-ghost" style={sm} disabled={busy}
                      onClick={() => { if (window.confirm(`Submit “${t.name}” for review? You won't be able to edit it while it's in review.`)) publish.mutate(t.id); }}>
                      {s === 4 ? "Resubmit" : "Submit for review"}
                    </button>
                  )}
                  {s === 3 && (
                    <button className="btn-ghost" style={sm} disabled={busy} onClick={() => cancel.mutate(t.id)}>Cancel review</button>
                  )}
                  {s === 1 && (
                    <button className="btn-ghost" style={sm} disabled={busy} onClick={() => onRevise(t.id, t.name)}>Revise</button>
                  )}
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
