import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useNode, useRemoveNode, nodeStatus } from "./useNodes";
import { NodeFullSections, NodeEarnings } from "./NodeSections";

// Phase 5 · Admin node inspect (/admin/nodes/:id). Full detail regardless of
// ownership, plus admin control. Remove is the only admin-callable node action
// (DELETE /api/nodes/{id}, admin-only); deregister is node-self and there's no
// suspend endpoint, so neither is offered — see AdminNodesPage.

export function AdminNodeInspectPage() {
  const { id = "" } = useParams();
  const { api } = useAuth();
  const navigate = useNavigate();
  const { data: n, isLoading, isError } = useNode(api, id);
  const remove = useRemoveNode(api);
  const err = remove.error as Error | undefined;

  function onRemove() {
    if (!n) return;
    if (window.confirm(`Remove node “${n.name}” from the network? This is a hard delete and can't be undone.`)) {
      remove.mutate(n.id, { onSuccess: () => navigate("/admin/nodes") });
    }
  }

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
  if (isError || !n) return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
      <p style={{ color: "var(--danger)" }}>Couldn't load this node.</p>
      <Link className="btn-ghost" to="/admin/nodes" style={{ alignSelf: "start" }}>← Back to nodes</Link>
    </div>
  );

  const st = nodeStatus(n.status);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
      <Link className="btn-ghost" to="/admin/nodes" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>← Back to nodes</Link>

      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)" }}>
        <div>
          <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
            <h1 style={{ margin: 0 }}>{n.name}</h1>
            <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
            {n.isSchedulingReady && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>scheduling ready</span>}
          </div>
          <div style={{ fontFamily: "var(--font-mono)", color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 4 }}>{n.id}</div>
          <div style={{ fontFamily: "var(--font-mono)", color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>owner {n.walletAddress}</div>
          {n.description && <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>{n.description}</p>}
        </div>
        <button className="btn-ghost" style={{ color: "var(--danger)", whiteSpace: "nowrap" }} disabled={remove.isPending} onClick={onRemove}>Remove</button>
      </div>

      {err && <p style={{ color: "var(--danger)" }}>{err.message || "Remove failed."}</p>}

      <NodeFullSections node={n} />
      <NodeEarnings node={n} />
    </div>
  );
}
