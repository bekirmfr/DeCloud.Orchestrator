import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useMyTemplates, useDeleteTemplate, templateStatus } from "./useTemplates";

// Phase 5 · My Templates — the author's own templates. Slice 1 of authoring:
// list + status + delete + entry points to the create/edit form (slice 2).

export function MyTemplatesPage() {
  const { api } = useAuth();
  const { data: templates, isLoading, isError } = useMyTemplates(api);
  const del = useDeleteTemplate(api);

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

      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Couldn't load your templates.</p>}
      {templates && templates.length === 0 && (
        <p style={{ color: "var(--text-secondary)" }}>You haven't authored any templates yet. Create one to get started.</p>
      )}

      {templates && templates.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
          {templates.map((t) => {
            const st = templateStatus(t.status);
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
                  <Link className="btn-ghost" to={`/my-templates/${t.slug || t.id}/edit`} style={{ fontSize: "var(--text-sm)" }}>Edit</Link>
                  <button
                    className="btn-ghost"
                    style={{ fontSize: "var(--text-sm)", color: "var(--danger)" }}
                    disabled={del.isPending}
                    onClick={() => { if (window.confirm(`Delete “${t.name}”? This can't be undone.`)) del.mutate(t.id); }}
                  >
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
