import type { ReactNode, CSSProperties } from "react";
import { Link } from "react-router-dom";
import type { OrchNode, NodeResources } from "./useNodes";

// Shared node-detail building blocks, used by the user detail page (owner sees
// NodeFullSections + NodeEarnings; a non-owner sees only NodeAvailability) and by
// the admin inspect page (always full). Keeping these here avoids duplicating the
// resource table / health / network markup across the two pages.

export const gb = (b?: number) => (b ? `${Math.round(b / 1024 ** 3)} GB` : "—");
export const num = (n?: number) => (n == null ? "—" : String(n));
export const pct = (n?: number) => (n == null ? "—" : `${n.toFixed(1)}%`);
export const usdc = (n?: number) => (n == null ? "—" : `${Number(n).toFixed(4)} USDC`);
export const dt = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");

const th: CSSProperties = { padding: "6px 10px", fontWeight: "var(--fw-medium)", whiteSpace: "nowrap", textAlign: "left" };
const td: CSSProperties = { padding: "6px 10px", verticalAlign: "top" };

export function Section({ title, children }: { title: string; children: ReactNode }) {
    return (
        <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
            <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{title}</strong>
            {children}
        </section>
    );
}

function KV({ k, v }: { k: string; v: ReactNode }) {
    return (
        <div style={{ display: "flex", gap: 8, fontSize: "var(--text-sm)" }}>
            <span style={{ color: "var(--text-tertiary)", minWidth: 150 }}>{k}</span>
            <span style={{ color: "var(--text-secondary)" }}>{v}</span>
        </div>
    );
}

// Available = Total − Allocated, per resource, clamped at 0.
function avail(total?: number, allocated?: number): number {
    return Math.max(0, (total ?? 0) - (allocated ?? 0));
}

/** Non-owner view: just what a prospective tenant needs — free vs total capacity. */
export function NodeAvailability({ node }: { node: OrchNode }) {
    const t = node.totalResources, a = node.allocatedResources;
    const rows: [string, string][] = [
        ["Compute pts", `${avail(t?.computePoints, a?.computePoints)} of ${num(t?.computePoints)}`],
        ["Memory", `${gb(avail(t?.memoryBytes, a?.memoryBytes))} of ${gb(t?.memoryBytes)}`],
        ["Storage", `${gb(avail(t?.storageBytes, a?.storageBytes))} of ${gb(t?.storageBytes)}`],
        ["GPU VRAM", `${gb(avail(t?.gpuVramBytes, a?.gpuVramBytes))} of ${gb(t?.gpuVramBytes)}`],
    ];
    return (
        <Section title="Available capacity">
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                {rows.map(([k, v]) => <KV key={k} k={k} v={<span><span style={{ color: "var(--text-primary)" }}>{v.split(" of ")[0]}</span> free of {v.split(" of ")[1]}</span>} />)}
            </div>
        </Section>
    );
}

/** Owner / admin view: the full resource breakdown across all pools. */
export function NodeFullSections({ node: n }: { node: OrchNode }) {
    const roles = [n.relayInfo ? "Relay" : null, n.dhtInfo ? "DHT" : null, n.blockStoreInfo ? "Block store" : null].filter(Boolean) as string[];
    const loc = n.locality;
    const place = [loc?.region, loc?.country, loc?.zone].filter(Boolean).join(" · ");
    const rows: [string, NodeResources | undefined][] = [
        ["Total", n.totalResources], ["Allocated", n.allocatedResources], ["Used", n.usedResources], ["Reserved", n.reservedResources],
    ];
    return (
        <>
            <Section title="Resources">
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
            </Section>

            <Section title="Health">
                <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                    <KV k="Uptime" v={pct(n.uptimePercentage)} />
                    <KV k="VMs hosted" v={num(n.totalVmsHosted)} />
                    <KV k="Successful completions" v={num(n.successfulVmCompletions)} />
                    <KV k="Agent version" v={n.agentVersion || "—"} />
                    <KV k="Architecture" v={n.architecture || "—"} />
                    <KV k="Registered" v={dt(n.registeredAt)} />
                    <KV k="Last heartbeat" v={dt(n.lastHeartbeat)} />
                </div>
            </Section>

            <Section title="Network">
                <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                    <KV k="Public address" v={n.publicIp ? `${n.publicIp}${n.agentPort ? `:${n.agentPort}` : ""}` : "—"} />
                    <KV k="Behind CGNAT" v={n.isBehindCgnat ? "yes" : "no"} />
                    {place && <KV k="Location" v={place} />}
                    {loc?.jurisdictionTags && loc.jurisdictionTags.length > 0 && <KV k="Jurisdiction" v={loc.jurisdictionTags.join(", ")} />}
                    {loc?.locationMismatch && <KV k="Location mismatch" v="declared vs IP-derived differ" />}
                </div>
            </Section>

            {roles.length > 0 && (
                <Section title="Roles">
                    <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{roles.join(" · ")}</div>
                </Section>
            )}
        </>
    );
}

export function NodeEarnings({ unsettled }: { unsettled?: number }) {
    // Per-node earnings from the settlement ledger are only reliable for the
    // UNSETTLED (accruing) amount — settled records are pruned, and settled
    // payouts are withdrawn from the escrow (address-level) via the Wallet.
    return (
        <Section title="Earnings">
            <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                <KV k="Unsettled" v={usdc(unsettled)} />
            </div>
            <p style={{ margin: "8px 0 0", color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
                Node share of usage awaiting on-chain settlement. Settled earnings are withdrawable from your{" "}
                <Link to="/wallet" style={{ color: "var(--text-accent)" }}>Wallet</Link>.
            </p>
        </Section>
    );
}