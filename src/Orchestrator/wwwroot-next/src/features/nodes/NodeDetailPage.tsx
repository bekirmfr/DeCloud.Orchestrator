import { useParams, Link } from "react-router-dom";
import type { ReactNode, CSSProperties } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { sameAddress } from "../../auth/deriveStatus";
import { useNode, nodeStatus, type NodeResources } from "./useNodes";

// Phase 5 · Node detail (slice 2). Read-only drill-in from the Nodes list/search.
// GET /api/nodes/{id} returns the full Node ([Authorize]); earnings are shown
// only to the node's owner (the endpoint returns them to any caller, but that's
// the operator's private figure, so the UI gates it).

const gb = (b?: number) => (b ? `${Math.round(b / 1024 ** 3)} GB` : "—");
const num = (n?: number) => (n == null ? "—" : String(n));
const pct = (n?: number) => (n == null ? "—" : `${n.toFixed(1)}%`);
const usdc = (n?: number) => (n == null ? "—" : `${Number(n).toFixed(4)} USDC`);
const dt = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");

const th: CSSProperties = { padding: "6px 10px", fontWeight: "var(--fw-medium)", whiteSpace: "nowrap", textAlign: "left" };
const td: CSSProperties = { padding: "6px 10px", verticalAlign: "top" };

const sec = (title: string, body: ReactNode) => (
  <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
    <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{title}</strong>
    {body}
  </section>
);

function KV({ k, v }: { k: string; v: ReactNode }) {
  return (
    <div style={{ display: "flex", gap: 8, fontSize: "var(--text-sm)" }}>
      <span style={{ color: "var(--text-tertiary)", minWidth: 150 }}>{k}</span>
      <span style={{ color: "var(--text-secondary)" }}>{v}</span>
    </div>
  );
}

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
  const place = [loc?.region, loc?.country, loc?.zone].filter(Boolean).join(" · ");
  const roles = [n.relayInfo ? "Relay" : null, n.dhtInfo ? "DHT" : null, n.blockStoreInfo ? "Block store" : null].filter(Boolean) as string[];
  const rows: [string, NodeResources | undefined][] = [
    ["Total", n.totalResources], ["Allocated", n.allocatedResources], ["Used", n.usedResources], ["Reserved", n.reservedResources],
  ];

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 820 }}>
      <Link className="btn-ghost" to="/nodes" style={{ alignSelf: "start", fontSize: "var(--text-sm)" }}>← Back to nodes</Link>

      <div>
        <div style={{ display: "flex", gap: 8, alignItems: "baseline", flexWrap: "wrap" }}>
          <h1 style={{ margin: 0 }}>{n.name}</h1>
          <span style={{ color: st.tone, fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>● {st.label}</span>
          {n.isSchedulingReady && <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>scheduling ready</span>}
        </div>
        <div style={{ ...({ fontFamily: "var(--font-mono)" }), color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 4 }}>{n.id}</div>
        {n.description && <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>{n.description}</p>}
      </div>

      {sec("Resources", (
        <div style={{ overflowX: "auto", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-sm)" }}>
          <table style={{ width: "100%", borderCollapse: "collapse", fontSize: "var(--text-sm)" }}>
            <thead>
              <tr style={{ color: "var(--text-tertiary)", background: "var(--surface-2)" }}>
                <th style={th}></th><th style={th}>Compute pts</th><th style={th}>Memory</th><th style={th}>Storage</th><th style={th}>GPU VRAM</th>
              </tr>
            </thead>
            <tbody>
              {rows.map(([k, r]) => (
                <tr key={k} style={{ borderTop: "1px solid var(--border-subtle)" }}>
                  <td style={{ ...td, color: "var(--text-tertiary)" }}>{k}</td>
                  <td style={td}>{num(r?.computePoints)}</td>
                  <td style={td}>{gb(r?.memoryBytes)}</td>
                  <td style={td}>{gb(r?.storageBytes)}</td>
                  <td style={td}>{gb(r?.gpuVramBytes)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}

      {sec("Health", (
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <KV k="Uptime" v={pct(n.uptimePercentage)} />
          <KV k="VMs hosted" v={num(n.totalVmsHosted)} />
          <KV k="Successful completions" v={num(n.successfulVmCompletions)} />
          <KV k="Agent version" v={n.agentVersion || "—"} />
          <KV k="Architecture" v={n.architecture || "—"} />
          <KV k="Registered" v={dt(n.registeredAt)} />
          <KV k="Last heartbeat" v={dt(n.lastHeartbeat)} />
        </div>
      ))}

      {sec("Network", (
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <KV k="Public address" v={n.publicIp ? `${n.publicIp}${n.agentPort ? `:${n.agentPort}` : ""}` : "—"} />
          <KV k="Behind CGNAT" v={n.isBehindCgnat ? "yes" : "no"} />
          {place && <KV k="Location" v={place} />}
          {loc?.jurisdictionTags && loc.jurisdictionTags.length > 0 && <KV k="Jurisdiction" v={loc.jurisdictionTags.join(", ")} />}
          {loc?.locationMismatch && <KV k="Location mismatch" v="declared vs IP-derived differ" />}
        </div>
      ))}

      {roles.length > 0 && sec("Roles", (
        <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{roles.join(" · ")}</div>
      ))}

      {owner && sec("Earnings (owner)", (
        <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
          <KV k="Total earned" v={usdc(n.totalEarned)} />
          <KV k="Pending payout" v={usdc(n.pendingPayout)} />
        </div>
      ))}

      {n.tags && n.tags.length > 0 && sec("Tags", (
        <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{n.tags.join(", ")}</div>
      ))}
    </div>
  );
}
