import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useAllNodes, useRemoveNode, nodeStatus, type OrchNode } from "./useNodes";

// Phase 5 · Admin node manager (slice 3). Whole-fleet list with the one
// admin-callable node action: hard remove (DELETE /api/nodes/{id}, admin-only).
// Deregister is node-self (node_id JWT claim) and suspend has no endpoint, so
// neither is offered here — Remove is the honest extent of admin node control.
// Admin-gated by AdminGuard + canAccessAdmin; the endpoint enforces the role.

const sm = { fontSize: "var(--text-sm)" } as const;
const short = (s?: string) => (s ? `${s.slice(0, 6)}…${s.slice(-4)}` : "—");
const ago = (iso?: string) => {
  if (!iso) return "—";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 90) return `${Math.round(s)}s ago`;
  if (s < 5400) return `${Math.round(s / 60)}m ago`;
  if (s < 129600) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
};

function Row({ n, onRemove, busy }: { n: OrchNode; onRemove: () => void; busy: boolean }) {
  const st = nodeStatus(n.status);
  return (
    <div style={{ display: "flex", alignItems: "center", gap: "var(--space-3)", padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
          <strong style={{ fontSize: "var(--text-md)" }}>{n.name}</strong>
          <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
          <span style={{ fontFamily: "var(--font-mono)", color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{short(n.walletAddress)}</span>
        </div>
        <div style={{ display: "flex", gap: "var(--space-3)", flexWrap: "wrap", color: "var(--text-secondary)", fontSize: "var(--text-sm)", marginTop: 4 }}>
          <span>Uptime {n.uptimePercentage == null ? "—" : `${n.uptimePercentage.toFixed(1)}%`}</span>
          <span>VMs {n.totalVmsHosted ?? "—"}</span>
          {n.publicIp && <span style={{ fontFamily: "var(--font-mono)" }}>{n.publicIp}</span>}
          <span>Heartbeat {ago(n.lastHeartbeat)}</span>
        </div>
      </div>
      <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
        <Link className="btn-ghost" to={`/nodes/${n.id}`} style={sm}>Inspect</Link>
        <button className="btn-ghost" style={{ ...sm, color: "var(--danger)" }} disabled={busy} onClick={onRemove}>Remove</button>
      </div>
    </div>
  );
}

export function AdminNodesPage() {
  const { api } = useAuth();
  const { data: nodes, isLoading, isError } = useAllNodes(api);
  const remove = useRemoveNode(api);
  const err = remove.error as Error | undefined;

  function onRemove(n: OrchNode) {
    if (window.confirm(`Remove node “${n.name}” from the network? This is a hard delete and can't be undone.`)) {
      remove.mutate(n.id);
    }
  }

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 900 }}>
      <div>
        <h1 style={{ margin: 0 }}>Nodes</h1>
        <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
          Every registered node. Remove hard-deletes a node from the network.
        </p>
      </div>

      {err && <p style={{ color: "var(--danger)" }}>{err.message || "Remove failed."}</p>}
      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Couldn't load nodes.</p>}
      {nodes && nodes.length === 0 && <p style={{ color: "var(--text-secondary)" }}>No nodes registered.</p>}

      {nodes && nodes.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
          {nodes.map((n) => <Row key={n.id} n={n} busy={remove.isPending} onRemove={() => onRemove(n)} />)}
        </div>
      )}
    </div>
  );
}
