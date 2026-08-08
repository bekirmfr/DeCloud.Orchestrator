import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useVms, type VmSummary } from "../vms/useVms";
import { vmStatusBadge, normalizeStatus, type BadgeTone } from "../vms/vmStatus";
import { useBalance, runwayDays, formatRunway, LOW_RUNWAY_DAYS } from "../billing/useBalance";
import { useMyNodes, useAllNodes } from "../nodes/useNodes";
import { useMyTemplates, statusNum } from "../templates/useTemplates";
import { QUALITY_TIERS, enumNum } from "../templates/templateForm";
import { useUserRealtime } from "../../realtime/useUserRealtime";
import { useMediaQuery, MOBILE_QUERY } from "../../app/useMediaQuery";
import type { AppError } from "../../api/errors";

// Phase 3 · the DASHBOARD: operate + fund home (DESIGN §2). Two-column on desktop
// (workloads left, balance right), stacked on mobile; plus Nodes / Templates
// summaries. Status is LIVE (SubscribeToUser); balance/runway POLLS (§6.9).
// Composes from the Meridian layer (.card / .mono / .track / .status-dot).
//
// NOTE: rows show name / locality (host node region · name, mapped from the
// fleet by vm.nodeId) / status / spec. Still missing vs the reference: tier
// (gpuMode/qualityTier) and per-VM hourly rate — add those to VmSummaryDto.

const DOT: Record<BadgeTone, string> = { active: "ok", transitional: "warn", inert: "idle", error: "err" };
const gib = (b: number) => Math.round(b / 1024 ** 3);

function StatusDot({ status }: { status: VmSummary["status"] }) {
    return <span aria-hidden className={`status-dot ${DOT[vmStatusBadge(status).tone]}`} style={{ marginTop: 1 }} />;
}

function CardHead({ title, cap }: { title: string; cap?: string }) {
    return (
        <div className="card-h">
            <span className="card-title">{title}</span>
            {cap && <span className="card-cap">{cap}</span>}
        </div>
    );
}

export function DashboardPage() {
    const { api, wallet } = useAuth();
    const mobile = useMediaQuery(MOBILE_QUERY);

    // OwnerId IS the wallet address; the server broadcasts to user:{OwnerId}.
    const address = wallet.kind === "connected" ? wallet.address : "";
    useUserRealtime(address);

    const { data: vms, isLoading, error } = useVms(api, 1);
    const { data: balance } = useBalance(api);
    const { data: nodes } = useMyNodes(api, address || undefined);
    // Whole fleet, from the SAME ["nodes","all"] cache useMyNodes populated — no
    // extra fetch. A workload runs on someone else's node, so the host isn't in
    // "my nodes"; map by id to show its locality on the row.
    const { data: allNodes } = useAllNodes(api);
    const { data: templates } = useMyTemplates(api);

    const sym = balance?.tokenSymbol ?? "USDC";
    const days = runwayDays(balance?.balance, balance?.hourlyBurnRate);
    const lowRunway = days != null && days < LOW_RUNWAY_DAYS;
    const runwayFill = days == null ? 100 : Math.max(3, Math.min(100, (days / 30) * 100));

    const nodeById = new Map((allNodes ?? []).map((n) => [n.id, n] as const));

    const active = (vms?.items ?? []).filter((v) => {
        const s = normalizeStatus(v.status);
        return s !== "Deleted" && s !== "Stopped" && s !== "Suspended";
    });

    const runwayLabel = !balance
        ? "—"
        : days != null ? formatRunway(days)
            : active.length > 0 ? "Not currently billed"
                : "No active workloads";

    const nodeCount = nodes?.length ?? 0;
    const nodesReady = (nodes ?? []).filter((n) => n.isSchedulingReady).length;
    const tplCount = templates?.length ?? 0;
    const tplPublished = (templates ?? []).filter((t) => statusNum(t.status) === 1).length;

    return (
        <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-5)" }}>
            <div>
                <span className="eyebrow muted">Dashboard</span>
                <h1 style={{ margin: "var(--space-2) 0 0" }}>Overview</h1>
            </div>

            {/* Primary row: workloads (wide) + balance (narrow); stacks on mobile. */}
            <div style={{ display: "grid", gridTemplateColumns: mobile ? "1fr" : "1.7fr 1fr", gap: "var(--space-4)", alignItems: "start" }}>
                {/* ── Running workloads ─────────────────────────────────────────── */}
                <section className="card">
                    <CardHead title="Running workloads" cap={vms ? `${active.length} of ${vms.totalCount}` : undefined} />

                    {isLoading && <p style={{ padding: 18, color: "var(--text-secondary)" }}>Loading…</p>}
                    {error && <p role="alert" style={{ padding: 18, color: "var(--danger)" }}>{(error as AppError)?.message ?? "Couldn't load your workloads."}</p>}
                    {!isLoading && !error && active.length === 0 && (
                        <div style={{ padding: "28px 18px", textAlign: "center" }}>
                            <p style={{ color: "var(--text-secondary)" }}>Nothing running yet.</p>
                            <Link className="btn-primary" to="/marketplace/platform-general/deploy" style={{ marginTop: 12, display: "inline-block" }}>Deploy your first workload</Link>
                        </div>
                    )}

                    {active.map((vm) => {
                        const host = vm.nodeId ? nodeById.get(vm.nodeId) : undefined;
                        const locality = host ? [host.locality?.region, host.name].filter(Boolean).join(" · ") : null;
                        const badge = vmStatusBadge(vm.status);
                        const gpuOn = enumNum(vm.spec.gpuMode, 0) !== 0;
                        const tier = gpuOn ? "GPU" : (QUALITY_TIERS.find(([n]) => n === enumNum(vm.spec.qualityTier, 0))?.[1] ?? null);
                        const rate = vm.hourlyRateCrypto;
                        return (
                            <div key={vm.id} style={{ display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap", padding: "14px 18px", borderBottom: "1px solid var(--border-subtle)" }}>
                                <div style={{ display: "flex", alignItems: "flex-start", gap: 10, minWidth: 0, flex: "1 1 200px" }}>
                                    <StatusDot status={vm.status} />
                                    <div style={{ minWidth: 0 }}>
                                        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
                                            <Link to={`/vms/${vm.id}`} style={{ color: "var(--text-accent)", fontWeight: "var(--fw-medium)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{vm.name}</Link>
                                            {/* dot conveys "running"; only spell out transitional/error states */}
                                            {badge.tone !== "active" && <span style={{ fontSize: "var(--text-xs)", color: "var(--text-secondary)" }}>{badge.label}</span>}
                                            {vm.complianceHold && <span style={{ fontSize: "var(--text-xs)", color: "var(--warning)" }}>held</span>}
                                        </div>
                                        {locality && <div style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)", marginTop: 1 }}>{locality}</div>}
                                    </div>
                                </div>
                                <div style={{ display: "flex", alignItems: "center", gap: 10, marginLeft: "auto", flexWrap: "wrap", justifyContent: "flex-end" }}>
                                    {tier && <span className={`badge ${gpuOn ? "accent" : "neutral"}`}>{tier}</span>}
                                    <span className="mono" style={{ fontSize: "var(--text-xs)", color: "var(--text-secondary)" }}>{vm.spec.virtualCpuCores} vCPU · {gib(vm.spec.memoryBytes)} GB</span>
                                    {rate != null && rate > 0 && <span className="mono" style={{ fontSize: "var(--text-xs)", color: "var(--text-secondary)", minWidth: 88, textAlign: "right" }}>≈ {rate.toFixed(4)}/hr</span>}
                                </div>
                            </div>
                        );
                    })}

                    {vms && vms.totalCount > active.length && (
                        <div style={{ padding: "12px 18px" }}>
                            <Link to="/vms" style={{ color: "var(--text-accent)", fontSize: "var(--text-sm)" }}>View all {vms.totalCount} virtual machines →</Link>
                        </div>
                    )}
                </section>

                {/* ── Balance ───────────────────────────────────────────────────── */}
                <section className="card">
                    <CardHead title="Balance" cap="escrow" />
                    <div style={{ padding: "var(--space-4) 18px", display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
                        <div>
                            <span className="mono" style={{ fontSize: 30, fontWeight: 500, letterSpacing: "var(--track-snug)" }}>{balance ? balance.balance.toFixed(2) : "—"}</span>
                            <span style={{ color: "var(--text-secondary)", marginLeft: 8 }}>{sym}</span>
                            {!!balance?.pendingDeposits && <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 2 }}>+{balance.pendingDeposits.toFixed(2)} confirming</div>}
                        </div>

                        <div>
                            <div style={{ display: "flex", justifyContent: "space-between", fontSize: "var(--text-sm)", color: lowRunway ? "var(--warning)" : "var(--text-secondary)", marginBottom: 7 }}>
                                <span>Runway at current usage</span>
                                <span>{runwayLabel}</span>
                            </div>
                            {days != null && (
                                <div className="track"><div className="track-fill" style={{ width: `${runwayFill}%`, background: lowRunway ? "var(--warning-solid)" : "var(--accent)" }} /></div>
                            )}
                        </div>

                        <Link className="btn-ghost" to="/wallet" style={{ width: "100%", justifyContent: "center" }}>Add funds</Link>

                        <div style={{ display: "grid", gridTemplateColumns: "1fr 1fr", gap: "var(--space-2)" }}>
                            <Tile label="Burn rate" value={balance ? `${balance.hourlyBurnRate.toFixed(4)}/hr` : "—"} />
                            <Tile label="Unpaid usage" value={balance ? `${balance.unpaidUsage.toFixed(2)} ${sym}` : "—"} tone={(balance?.unpaidUsage ?? 0) > 0 ? "var(--warning)" : undefined} />
                        </div>
                    </div>

                    {lowRunway && (
                        <div role="alert" style={{ padding: "12px 18px", borderTop: "1px solid var(--border-subtle)", background: "var(--warning-soft)", color: "var(--warning)", fontSize: "var(--text-sm)" }}>
                            Your workloads will stop when the balance runs out. <Link to="/wallet" style={{ color: "inherit", textDecoration: "underline" }}>Add funds</Link>.
                        </div>
                    )}
                </section>
            </div>

            {/* Secondary row: nodes + templates summaries; stacks on mobile. */}
            <div style={{ display: "grid", gridTemplateColumns: mobile ? "1fr" : "1fr 1fr", gap: "var(--space-4)" }}>
                <SummaryCard
                    title="My Nodes" to="/nodes" linkLabel="Manage nodes →"
                    value={String(nodeCount)} sub={nodeCount === 0 ? "You aren't running any nodes" : `${nodesReady} scheduling-ready`}
                    empty={nodeCount === 0}
                />
                <SummaryCard
                    title="My Templates" to="/marketplace?tab=mine" linkLabel="Open My Templates →"
                    value={String(tplCount)} sub={tplCount === 0 ? "You haven't authored any templates" : `${tplPublished} published`}
                    empty={tplCount === 0}
                />
            </div>
        </section>
    );
}

function Tile({ label, value, tone }: { label: string; value: string; tone?: string }) {
    return (
        <div style={{ background: "var(--surface-1)", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius)", padding: "var(--space-3)" }}>
            <div style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)" }}>{label}</div>
            <div className="mono" style={{ fontSize: "var(--text-md)", fontWeight: 500, marginTop: 3, color: tone ?? "var(--text-primary)" }}>{value}</div>
        </div>
    );
}

function SummaryCard({ title, value, sub, to, linkLabel, empty }: { title: string; value: string; sub: string; to: string; linkLabel: string; empty: boolean }) {
    return (
        <section className="card">
            <CardHead title={title} />
            <div style={{ padding: "var(--space-4) 18px", display: "flex", alignItems: "flex-end", justifyContent: "space-between", gap: "var(--space-3)", flexWrap: "wrap" }}>
                <div>
                    {!empty && <div className="mono" style={{ fontSize: "var(--text-2xl)", fontWeight: 600, letterSpacing: "var(--track-snug)" }}>{value}</div>}
                    <div style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)", marginTop: empty ? 0 : 2 }}>{sub}</div>
                </div>
                <Link to={to} style={{ color: "var(--text-accent)", fontSize: "var(--text-sm)", whiteSpace: "nowrap" }}>{linkLabel}</Link>
            </div>
        </section>
    );
}