import { useState } from "react";
import type { ReactNode, CSSProperties } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../../auth/AuthProvider";
import {
    useBalance, useDepositInfo, runwayDays, formatRunway, LOW_RUNWAY_DAYS,
} from "./useBalance";
import { withdrawEarnings, readOnChain } from "./paymentClient";
import { DepositModal } from "./DepositModal";

// Phase 6 · Wallet. Balance/runway/usage (read-only, Slice 1) + native on-chain
// deposit & earnings-withdraw (Slice 2, via paymentClient — see its header for
// the safety guards). The wallet is the final confirmation gate.
//
// Presentation uses the Meridian layer (.card / .card-h / .mono / .track); the
// data + on-chain wiring below is unchanged.

const bodyPad: CSSProperties = {
    padding: "var(--space-4) var(--space-5)",
    display: "flex", flexDirection: "column", gap: "var(--space-3)",
};
const mono: CSSProperties = { fontFamily: "var(--font-mono)" };
const trunc = (s?: string) => (s ? `${s.slice(0, 6)}…${s.slice(-4)}` : "—");
const dt = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");
const rejected = (e: unknown) => {
    const c = (e as { code?: unknown })?.code;
    return c === "ACTION_REJECTED" || c === 4001;
};

/** A titled card: header (title + optional mono caption) over a padded body. */
function Card({ title, cap, children }: { title: string; cap?: string; children: ReactNode }) {
    return (
        <section className="card">
            <div className="card-h">
                <span className="card-title">{title}</span>
                {cap && <span className="card-cap">{cap}</span>}
            </div>
            <div style={bodyPad}>{children}</div>
        </section>
    );
}

/** Reference mini-tile: a mono figure over a small label. */
function Tile({ label, value, tone }: { label: string; value: string; tone?: string }) {
    return (
        <div style={{ background: "var(--surface-1)", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius)", padding: "var(--space-3)" }}>
            <div style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)" }}>{label}</div>
            <div className="mono" style={{ fontSize: "var(--text-lg)", fontWeight: 500, marginTop: 3, color: tone ?? "var(--text-primary)" }}>{value}</div>
        </div>
    );
}

/** A label → value line in the deposit-details card. */
function Line({ label, children }: { label: string; children: ReactNode }) {
    return (
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
            <span style={{ color: "var(--text-tertiary)", minWidth: 130 }}>{label}</span>
            <span>{children}</span>
        </div>
    );
}

export function WalletPage() {
    const { api, wallet, getSigner } = useAuth();
    const qc = useQueryClient();
    const { data: bal, isLoading, isError } = useBalance(api);
    const { data: info } = useDepositInfo(api);
    const connected = wallet.kind === "connected";

    // On-chain reads: pending payout (withdrawable earnings) — needs wallet + config.
    const { data: onchain } = useQuery({
        queryKey: ["onchain-balances", info?.escrowContractAddress, wallet.kind === "connected" ? wallet.address : null],
        queryFn: async () => readOnChain(await getSigner(), info!),
        enabled: !!info && connected,
        staleTime: 20_000,
    });

    const [depositOpen, setDepositOpen] = useState(false);
    const [busy, setBusy] = useState(false);
    const [progress, setProgress] = useState<string | null>(null);
    const [err, setErr] = useState<string | null>(null);

    const sym = bal?.tokenSymbol ?? "USDC";
    const money = (n?: number) => `${(n ?? 0).toFixed(2)} ${sym}`;
    const days = runwayDays(bal?.balance, bal?.hourlyBurnRate);
    const lowRunway = days != null && days < LOW_RUNWAY_DAYS;
    const explorerTx = (hash: string) => (info?.explorerUrl ? `${info.explorerUrl}/tx/${hash}` : undefined);
    const pendingPayout = onchain?.pendingPayout ?? 0;

    // Track fill: runway as a fraction of a 30-day target (min 3% so it's visible).
    const runwayFill = days == null ? 100 : Math.max(3, Math.min(100, (days / 30) * 100));

    function refresh() {
        qc.invalidateQueries({ queryKey: ["balance"] });
        qc.invalidateQueries({ queryKey: ["onchain-balances"] });
    }

    async function onWithdrawEarnings() {
        if (!info) return;
        if (!window.confirm(`Withdraw your ${money(pendingPayout)} in earnings to your wallet?`)) return;
        setBusy(true); setErr(null); setProgress(null);
        try {
            await withdrawEarnings(await getSigner(), info, (p) => setProgress(p.message));
            setProgress(null);
            refresh();
        } catch (e) {
            setErr(rejected(e) ? "Cancelled in wallet." : (e as Error).message || "Withdrawal failed.");
        } finally {
            setBusy(false);
        }
    }

    return (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 760 }}>
            <div>
                <div className="eyebrow muted" style={{ marginBottom: "var(--space-2)" }}>Billing · wallet</div>
                <h1 style={{ margin: 0 }}>Wallet</h1>
                <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
                    Your deposit balance funds running VMs. Top up before your runway runs out.
                </p>
            </div>

            {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
            {isError && <p style={{ color: "var(--danger)" }}>Couldn't load your balance.</p>}
            {err && <p style={{ color: "var(--danger)" }}>{err}</p>}
            {progress && !depositOpen && <p style={{ color: "var(--text-accent)" }}>{progress}</p>}

            {bal && (
                <>
                    {/* Balance hero — the reference Balance card. */}
                    <Card title="Balance" cap={info ? `${info.chainName} · escrow` : "escrow"}>
                        <div>
                            <span className="mono" style={{ fontSize: 32, fontWeight: 500, letterSpacing: "var(--track-snug)" }}>{(bal.balance ?? 0).toFixed(2)}</span>
                            <span style={{ color: "var(--text-secondary)", marginLeft: 8 }}>{sym}</span>
                        </div>

                        <div>
                            <div style={{ display: "flex", justifyContent: "space-between", fontSize: "var(--text-sm)", color: lowRunway ? "var(--warning)" : "var(--text-secondary)", marginBottom: 7 }}>
                                <span>Runway at current usage</span>
                                <span>{days == null ? "No active spend" : `~${formatRunway(days)}`}</span>
                            </div>
                            {days != null && (
                                <div className="track">
                                    <div className="track-fill" style={{ width: `${runwayFill}%`, background: lowRunway ? "var(--warning-solid)" : "var(--accent)" }} />
                                </div>
                            )}
                            {bal.hourlyBurnRate > 0 && (
                                <div className="mono" style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)", marginTop: 7 }}>{bal.hourlyBurnRate.toFixed(4)} {sym}/hr</div>
                            )}
                        </div>

                        <button className="btn-primary" style={{ width: "100%", justifyContent: "center" }}
                            disabled={!connected || !info || busy}
                            onClick={() => { setErr(null); setDepositOpen(true); }}>
                            Deposit
                        </button>
                        {!connected && <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)", textAlign: "center" }}>Connect your wallet to deposit or withdraw.</p>}

                        <div style={{ display: "grid", gridTemplateColumns: "repeat(2, 1fr)", gap: "var(--space-2)" }}>
                            <Tile label="Confirmed" value={money(bal.confirmedBalance)} />
                            <Tile label="Pending deposits" value={money(bal.pendingDeposits)} />
                            <Tile label="Unpaid usage" value={money(bal.unpaidUsage)} tone={bal.unpaidUsage > 0 ? "var(--warning)" : undefined} />
                            <Tile label="Total" value={money(bal.totalBalance)} />
                        </div>
                    </Card>

                    {connected && (
                        <Card title="Earnings" cap="escrow payout">
                            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: "var(--space-3)", flexWrap: "wrap" }}>
                                <div>
                                    <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>Withdrawable payout</div>
                                    <div className="mono" style={{ fontSize: "var(--text-lg)", fontWeight: 500, marginTop: 2 }}>{money(pendingPayout)}</div>
                                </div>
                                <button className="btn-ghost" disabled={busy || pendingPayout <= 0} onClick={onWithdrawEarnings}>Withdraw earnings</button>
                            </div>
                            <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
                                Node and template revenue, held in the escrow until you withdraw. Withdraws the full balance.
                            </p>
                        </Card>
                    )}

                    {bal.pendingDepositsList && bal.pendingDepositsList.length > 0 && (
                        <Card title="Pending deposits">
                            <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                                {bal.pendingDepositsList.map((d, i) => (
                                    <div key={i} style={{ display: "flex", justifyContent: "space-between", gap: "var(--space-3)", fontSize: "var(--text-sm)", flexWrap: "wrap" }}>
                                        <span style={{ ...mono, color: "var(--text-secondary)" }}>
                                            {explorerTx(d.txHash) ? <a href={explorerTx(d.txHash)} target="_blank" rel="noreferrer" style={{ color: "var(--accent)" }}>{trunc(d.txHash)}</a> : trunc(d.txHash)}
                                        </span>
                                        <span className="mono" style={{ color: "var(--text-secondary)" }}>{money(d.amount)}</span>
                                        <span className="mono" style={{ color: "var(--text-tertiary)" }}>{d.confirmations}/{d.requiredConfirmations} confirmations</span>
                                    </div>
                                ))}
                            </div>
                        </Card>
                    )}

                    {bal.recentUsage && bal.recentUsage.length > 0 && (
                        <Card title="Recent usage">
                            <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                                {bal.recentUsage.map((u, i) => (
                                    <div key={i} style={{ display: "flex", justifyContent: "space-between", gap: "var(--space-3)", fontSize: "var(--text-sm)", flexWrap: "wrap" }}>
                                        <span style={{ ...mono, color: "var(--text-secondary)" }}>{trunc(u.vmId)}</span>
                                        <span style={{ color: "var(--text-tertiary)" }}>{u.duration}</span>
                                        <span className="mono" style={{ color: "var(--text-secondary)" }}>{(u.cost ?? 0).toFixed(4)} {sym}</span>
                                        <span style={{ color: "var(--text-tertiary)" }}>{dt(u.createdAt)}</span>
                                    </div>
                                ))}
                            </div>
                        </Card>
                    )}
                </>
            )}

            {info && (
                <Card title="Deposit details" cap={info.chainName}>
                    <div style={{ display: "flex", flexDirection: "column", gap: 6, fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
                        <Line label="Escrow address">
                            <span style={{ ...mono, wordBreak: "break-all" }}>
                                {info.explorerUrl ? <a href={`${info.explorerUrl}/address/${info.escrowContractAddress}`} target="_blank" rel="noreferrer" style={{ color: "var(--accent)" }}>{info.escrowContractAddress}</a> : info.escrowContractAddress}
                            </span>
                        </Line>
                        <Line label="Network">{info.chainName}</Line>
                        <Line label="Minimum deposit"><span className="mono">{money(info.minDeposit)}</span></Line>
                        <Line label="Confirmations"><span className="mono">{info.requiredConfirmations}</span></Line>
                    </div>
                </Card>
            )}

            {depositOpen && <DepositModal onClose={() => setDepositOpen(false)} />}
        </div>
    );
}