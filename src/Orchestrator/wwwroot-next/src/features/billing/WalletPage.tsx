import type { ReactNode, CSSProperties } from "react";
import { useAuth } from "../../auth/AuthProvider";
import {
  useBalance, useDepositInfo, runwayDays, formatRunway, LOW_RUNWAY_DAYS,
} from "./useBalance";

// Phase 6 · Wallet (Slice 1, read-only). Balance, runway, pending deposits and
// recent usage from GET /payment/balance, plus the deposit target from
// /payment/deposit-info. Deposit/withdraw are on-chain (escrow.deposit /
// withdrawBalance / nodeWithdraw) and not yet ported to React — those buttons
// bridge to the classic app (same as the deploy fund-gate). Slice 2 builds the
// native ethers flow. See DEPLOY_MIGRATION.md.

const card: CSSProperties = {
  padding: "var(--space-4)", border: "1px solid var(--border)",
  borderRadius: "var(--radius)", background: "var(--surface-1)",
  display: "flex", flexDirection: "column", gap: "var(--space-3)",
};
const mono: CSSProperties = { fontFamily: "var(--font-mono)" };
const trunc = (s?: string) => (s ? `${s.slice(0, 6)}…${s.slice(-4)}` : "—");
const dt = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section style={card}>
      <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{title}</strong>
      {children}
    </section>
  );
}

function Stat({ label, value, tone }: { label: string; value: string; tone?: string }) {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 2 }}>
      <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>{label}</span>
      <span style={{ color: tone ?? "var(--text-primary)", fontSize: "var(--text-md)" }}>{value}</span>
    </div>
  );
}

export function WalletPage() {
  const { api } = useAuth();
  const { data: bal, isLoading, isError } = useBalance(api);
  const { data: info } = useDepositInfo(api);

  const sym = bal?.tokenSymbol ?? "USDC";
  const money = (n?: number) => `${(n ?? 0).toFixed(2)} ${sym}`;
  const days = runwayDays(bal?.balance, bal?.hourlyBurnRate);
  const lowRunway = days != null && days < LOW_RUNWAY_DAYS;
  const explorerTx = (hash: string) => (info?.explorerUrl ? `${info.explorerUrl}/tx/${hash}` : undefined);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 760 }}>
      <div>
        <h1 style={{ margin: 0 }}>Wallet</h1>
        <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
          Your deposit balance funds running VMs. Top up before your runway runs out.
        </p>
      </div>

      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}
      {isError && <p style={{ color: "var(--danger)" }}>Couldn't load your balance.</p>}

      {bal && (
        <>
          <section style={card}>
            <div style={{ display: "flex", justifyContent: "space-between", alignItems: "flex-start", gap: "var(--space-3)", flexWrap: "wrap" }}>
              <div>
                <span style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>Available balance</span>
                <div style={{ display: "flex", alignItems: "baseline", gap: 8 }}>
                  <span style={{ fontSize: "var(--text-xl)", fontFamily: "var(--font-display)", fontWeight: 600 }}>{(bal.balance ?? 0).toFixed(2)}</span>
                  <span style={{ color: "var(--text-secondary)" }}>{sym}</span>
                </div>
                <div style={{ marginTop: 4, fontSize: "var(--text-sm)", color: lowRunway ? "var(--warning)" : "var(--text-secondary)" }}>
                  {days == null ? "No active spend" : `Runway: ${formatRunway(days)}`}
                  {bal.hourlyBurnRate > 0 ? ` · ${bal.hourlyBurnRate.toFixed(4)} ${sym}/hr` : ""}
                </div>
              </div>
              <div style={{ display: "flex", gap: 8, whiteSpace: "nowrap" }}>
                {/* LEGACY BRIDGE: on-chain deposit/withdraw not yet in React (Slice 2). */}
                <a className="btn-primary" href="/">Deposit</a>
                <a className="btn-ghost" href="/">Withdraw</a>
              </div>
            </div>

            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(120px, 1fr))", gap: "var(--space-3)", borderTop: "1px solid var(--border-subtle)", paddingTop: "var(--space-3)" }}>
              <Stat label="Confirmed" value={money(bal.confirmedBalance)} />
              <Stat label="Pending deposits" value={money(bal.pendingDeposits)} />
              <Stat label="Unpaid usage" value={money(bal.unpaidUsage)} tone={bal.unpaidUsage > 0 ? "var(--warning)" : undefined} />
              <Stat label="Total" value={money(bal.totalBalance)} />
            </div>

            <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
              Deposit and withdraw run on-chain and currently open the classic app.
            </p>
          </section>

          {bal.pendingDepositsList && bal.pendingDepositsList.length > 0 && (
            <Section title="Pending deposits">
              <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                {bal.pendingDepositsList.map((d, i) => (
                  <div key={i} style={{ display: "flex", justifyContent: "space-between", gap: "var(--space-3)", fontSize: "var(--text-sm)", flexWrap: "wrap" }}>
                    <span style={{ ...mono, color: "var(--text-secondary)" }}>
                      {explorerTx(d.txHash) ? <a href={explorerTx(d.txHash)} target="_blank" rel="noreferrer" style={{ color: "var(--accent)" }}>{trunc(d.txHash)}</a> : trunc(d.txHash)}
                    </span>
                    <span style={{ color: "var(--text-secondary)" }}>{money(d.amount)}</span>
                    <span style={{ color: "var(--text-tertiary)" }}>{d.confirmations}/{d.requiredConfirmations} confirmations</span>
                  </div>
                ))}
              </div>
            </Section>
          )}

          {bal.recentUsage && bal.recentUsage.length > 0 && (
            <Section title="Recent usage">
              <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                {bal.recentUsage.map((u, i) => (
                  <div key={i} style={{ display: "flex", justifyContent: "space-between", gap: "var(--space-3)", fontSize: "var(--text-sm)", flexWrap: "wrap" }}>
                    <span style={{ ...mono, color: "var(--text-secondary)" }}>{trunc(u.vmId)}</span>
                    <span style={{ color: "var(--text-tertiary)" }}>{u.duration}</span>
                    <span style={{ color: "var(--text-secondary)" }}>{money(u.cost)}</span>
                    <span style={{ color: "var(--text-tertiary)" }}>{dt(u.createdAt)}</span>
                  </div>
                ))}
              </div>
            </Section>
          )}
        </>
      )}

      {info && (
        <Section title="Deposit details">
          <div style={{ display: "flex", flexDirection: "column", gap: 6, fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
              <span style={{ color: "var(--text-tertiary)", minWidth: 130 }}>Escrow address</span>
              <span style={{ ...mono, wordBreak: "break-all" }}>
                {info.explorerUrl ? <a href={`${info.explorerUrl}/address/${info.escrowContractAddress}`} target="_blank" rel="noreferrer" style={{ color: "var(--accent)" }}>{info.escrowContractAddress}</a> : info.escrowContractAddress}
              </span>
            </div>
            <div style={{ display: "flex", gap: 8 }}><span style={{ color: "var(--text-tertiary)", minWidth: 130 }}>Network</span><span>{info.chainName}</span></div>
            <div style={{ display: "flex", gap: 8 }}><span style={{ color: "var(--text-tertiary)", minWidth: 130 }}>Minimum deposit</span><span>{money(info.minDeposit)}</span></div>
            <div style={{ display: "flex", gap: 8 }}><span style={{ color: "var(--text-tertiary)", minWidth: 130 }}>Confirmations</span><span>{info.requiredConfirmations}</span></div>
          </div>
          <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>
            Deposits are USDC sent to the escrow on {info.chainName}. Use the Deposit button, or send {sym} to the address above.
          </p>
        </Section>
      )}
    </div>
  );
}
