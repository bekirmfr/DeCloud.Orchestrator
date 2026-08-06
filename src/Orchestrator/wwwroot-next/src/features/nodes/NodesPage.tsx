import { useState } from "react";
import type { ReactNode, CSSProperties } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import {
  useMyNodes, useNodeSearch, nodeStatus, SORT_OPTIONS,
  type OrchNode, type NodeAdvertisement, type NodeSearchCriteria,
} from "./useNodes";

// Phase 5 · Nodes (slice 1). Tabbed: "My nodes" (owner view over the fleet) and
// "Search" (public marketplace advertisements with filters).

const gb = (b?: number) => (b ? `${Math.round(b / 1024 ** 3)} GB` : "—");
const num = (n?: number) => (n == null ? "—" : String(n));
const pct = (n?: number) => (n == null ? "—" : `${n.toFixed(1)}%`);
const usdc = (n?: number) => (n == null ? "—" : `${Number(n).toFixed(4)} USDC`);
const ago = (iso?: string) => {
  if (!iso) return "—";
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 90) return `${Math.round(s)}s ago`;
  if (s < 5400) return `${Math.round(s / 60)}m ago`;
  if (s < 129600) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
};

const card: CSSProperties = { padding: "var(--space-3)", border: "1px solid var(--border)", borderRadius: "var(--radius)", background: "var(--surface-1)" };
const metaRow: CSSProperties = { display: "flex", gap: "var(--space-3)", flexWrap: "wrap", color: "var(--text-secondary)", fontSize: "var(--text-sm)" };
const mono: CSSProperties = { fontFamily: "var(--font-mono)" };

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

function MyNodeCard({ n }: { n: OrchNode }) {
  const st = nodeStatus(n.status);
  return (
    <Link to={`/nodes/${n.id}`} style={{ ...card, display: "block", textDecoration: "none", color: "inherit" }}>
      <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
        <strong style={{ fontSize: "var(--text-md)" }}>{n.name}</strong>
        <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
        {n.publicIp && <span style={{ ...mono, color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{n.publicIp}</span>}
      </div>
      <div style={{ ...metaRow, marginTop: 6 }}>
        <span>Uptime {pct(n.uptimePercentage)}</span>
        <span>VMs hosted {num(n.totalVmsHosted)}</span>
        <span>Memory {gb(n.totalResources?.memoryBytes)}</span>
        <span>Storage {gb(n.totalResources?.storageBytes)}</span>
        <span>Heartbeat {ago(n.lastHeartbeat)}</span>
      </div>
      <div style={{ ...metaRow, marginTop: 4 }}>
        <span>Earned {usdc(n.totalEarned)}</span>
        <span>Pending payout {usdc(n.pendingPayout)}</span>
        {n.agentVersion && <span>Agent v{n.agentVersion}</span>}
        {n.architecture && <span>{n.architecture}</span>}
      </div>
    </Link>
  );
}

function MyNodes({ wallet }: { wallet?: string }) {
  const { api } = useAuth();
  const { data: nodes, isLoading, isError } = useMyNodes(api, wallet);
  if (!wallet) return <p style={{ color: "var(--text-secondary)" }}>Connect a wallet to see your nodes.</p>;
  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
  if (isError) return <p style={{ color: "var(--danger)" }}>Couldn't load your nodes.</p>;
  if (!nodes || nodes.length === 0) return <p style={{ color: "var(--text-secondary)" }}>No nodes registered to this wallet.</p>;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
      {nodes.map((n) => <MyNodeCard key={n.id} n={n} />)}
    </div>
  );
}

function AdCard({ a }: { a: NodeAdvertisement }) {
  const cap = a.capabilities;
  const place = [a.region, a.country].filter(Boolean).join(" · ");
  return (
    <Link to={`/nodes/${a.nodeId}`} style={{ ...card, display: "block", textDecoration: "none", color: "inherit" }}>
      <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
        <strong style={{ fontSize: "var(--text-md)" }}>{a.operatorName || "Node"}</strong>
        <span style={{ color: a.isOnline ? "var(--success)" : "var(--text-tertiary)", fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {a.isOnline ? "Online" : "Offline"}</span>
        {place && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{place}</span>}
      </div>
      {a.description && <p style={{ margin: "4px 0 0", color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{a.description}</p>}
      <div style={{ ...metaRow, marginTop: 6 }}>
        {cap?.cpuCores != null && <span>{cap.cpuCores} vCPU{cap.cpuModel ? ` · ${cap.cpuModel}` : ""}</span>}
        {cap?.hasGpu && <span>GPU{cap.gpuModel ? ` · ${cap.gpuModel}` : ""}</span>}
        {cap?.hasNvmeStorage && <span>NVMe</span>}
        <span>Uptime {pct(a.uptimePercentage)}</span>
        <span>VMs hosted {num(a.totalVmsHosted)}</span>
      </div>
    </Link>
  );
}

function NodeSearch() {
  const { api } = useAuth();
  const [c, setC] = useState<NodeSearchCriteria>({ region: "", requiresGpu: false, onlineOnly: false, sortBy: "uptime" });
  const { data: ads, isLoading, isError, isFetching } = useNodeSearch(api, c, true);
  const patch = (p: Partial<NodeSearchCriteria>) => setC((prev) => ({ ...prev, ...p }));

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
      <div style={{ display: "flex", gap: "var(--space-2)", alignItems: "center", flexWrap: "wrap" }}>
        <input style={{ minWidth: 160 }} placeholder="region" value={c.region} onChange={(e) => patch({ region: e.target.value })} />
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={c.requiresGpu} onChange={(e) => patch({ requiresGpu: e.target.checked })} />GPU</label>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)" }}><input type="checkbox" checked={c.onlineOnly} onChange={(e) => patch({ onlineOnly: e.target.checked })} />Online only</label>
        <label style={{ display: "flex", gap: 4, alignItems: "center", fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
          Sort
          <select value={c.sortBy} onChange={(e) => patch({ sortBy: e.target.value })}>
            {SORT_OPTIONS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>
        {isFetching && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>searching…</span>}
      </div>

      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Search failed.</p>}
      {ads && ads.length === 0 && <p style={{ color: "var(--text-secondary)" }}>No nodes match those filters.</p>}
      {ads && ads.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
          {ads.map((a) => <AdCard key={a.nodeId} a={a} />)}
        </div>
      )}
    </div>
  );
}

export function NodesPage() {
  const { session } = useAuth();
  const wallet = session.kind === "authenticated" ? session.address : undefined;
  const [tab, setTab] = useState<"mine" | "search">("mine");

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
      <div>
        <h1 style={{ margin: 0 }}>Nodes</h1>
        <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
          Nodes you operate, and the wider fleet available for scheduling.
        </p>
      </div>

      <div style={{ display: "flex", gap: 2, borderBottom: "1px solid var(--border-subtle)" }}>
        <TabBtn active={tab === "mine"} onClick={() => setTab("mine")}>My nodes</TabBtn>
        <TabBtn active={tab === "search"} onClick={() => setTab("search")}>Search</TabBtn>
      </div>

      {tab === "mine" ? <MyNodes wallet={wallet} /> : <NodeSearch />}
    </div>
  );
}
