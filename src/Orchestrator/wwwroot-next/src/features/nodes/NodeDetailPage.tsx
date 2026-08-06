import { useParams, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { sameAddress } from "../../auth/deriveStatus";
import { useNode, nodeStatus } from "./useNodes";
import { NodeFullSections, NodeAvailability, NodeEarnings, Section, pct } from "./NodeSections";

// Phase 5 · Node detail (owner-aware). The owner sees the full breakdown +
// earnings; anyone else sees only what a prospective tenant needs — status,
// location, uptime, and free-vs-total capacity — not the operator's ops detail.
// Admin gets a separate, fuller inspect page (/admin/nodes/:id).

export function NodeDetailPage() {
  const { id = "" } = useParams();
  const { api, session } = useAuth();
  const wallet = session.kind === "authenticated" ? session.address : undefined;
  const { data: n, isLoading, isError } = useNode(api, id);

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
  if (isError || !n) return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
      <p style={{ color: "var(--danger)" }}>Couldn't load this node.</p>
      <Link className="btn-ghost" to="/nodes" style={{ alignSelf: "start" }}>← Back to nodes</Link>
    </div>
  );

  const st = nodeStatus(n.status);
  const owner = sameAddress(n.walletAddress, wallet);
  const loc = n.locality;
  const place = [loc?.region, loc?.country].filter(Boolean).join(" · ");

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
      <Link className="btn-ghost" to="/nodes" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>← Back to nodes</Link>

      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)" }}>
        <div>
          <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
            <h1 style={{ margin: 0 }}>{n.name}</h1>
            <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
            {n.isSchedulingReady && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>scheduling ready</span>}
            {place && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{place}</span>}
          </div>
          {owner && <div style={{ fontFamily: "var(--font-mono)", color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 4 }}>{n.id}</div>}
          {n.description && <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>{n.description}</p>}
        </div>
        <Link className="btn-primary" to={`/marketplace/platform-general/deploy?node=${n.id}`} style={{ whiteSpace: "nowrap" }}>Deploy here</Link>
      </div>

      {owner ? (
        <>
          <NodeFullSections node={n} />
          <NodeEarnings node={n} />
        </>
      ) : (
        <>
          <NodeAvailability node={n} />
          <Section title="Reliability">
            <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Uptime {pct(n.uptimePercentage)}</div>
          </Section>
        </>
      )}
    </div>
  );
}
