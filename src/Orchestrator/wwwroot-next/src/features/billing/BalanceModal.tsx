import { useState } from "react";
import type { ReactNode } from "react";
import { Link } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../../auth/AuthProvider";
import { useBalance, useDepositInfo } from "./useBalance";
import { depositUSDC, withdrawEarnings, readOnChain, type TxProgress } from "./paymentClient";

// Compact balance popover opened from the sidebar card. Minimal info + the two
// on-chain actions (reusing paymentClient), plus a link to the full Wallet page.

const rejected = (e: unknown) => {
  const c = (e as { code?: unknown })?.code;
  return c === "ACTION_REJECTED" || c === 4001;
};

function Row({ k, v, tone }: { k: string; v: ReactNode; tone?: string }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", fontSize: "var(--text-sm)" }}>
      <span style={{ color: "var(--text-tertiary)" }}>{k}</span>
      <span style={{ color: tone ?? "var(--text-secondary)" }}>{v}</span>
    </div>
  );
}

export function BalanceModal({ onClose }: { onClose: () => void }) {
  const { api, wallet, getSigner } = useAuth();
  const qc = useQueryClient();
  const { data: bal } = useBalance(api);
  const { data: info } = useDepositInfo(api);
  const connected = wallet.kind === "connected";

  const { data: onchain } = useQuery({
    queryKey: ["onchain-balances", info?.escrowContractAddress, wallet.kind === "connected" ? wallet.address : null],
    queryFn: async () => readOnChain(await getSigner(), info!),
    enabled: !!info && connected,
    staleTime: 20_000,
  });

  const [amount, setAmount] = useState("");
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const sym = bal?.tokenSymbol ?? "USDC";
  const money = (x?: number) => `${(x ?? 0).toFixed(2)} ${sym}`;
  const pendingPayout = onchain?.pendingPayout ?? 0;

  async function run(fn: () => Promise<unknown>) {
    setBusy(true); setErr(null); setProgress(null);
    try {
      await fn();
      qc.invalidateQueries({ queryKey: ["balance"] });
      qc.invalidateQueries({ queryKey: ["onchain-balances"] });
    } catch (e) {
      setErr(rejected(e) ? "Cancelled in wallet." : (e as Error).message || "Action failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div onClick={() => !busy && onClose()} style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.6)", display: "flex", alignItems: "center", justifyContent: "center", padding: "var(--space-4)", zIndex: 60 }}>
      <div onClick={(e) => e.stopPropagation()} style={{ background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: "var(--space-4)", maxWidth: 420, width: "100%", display: "flex", flexDirection: "column", gap: "var(--space-3)" }}>
        <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
          <strong>Balance</strong>
          <button className="btn-ghost" disabled={busy} onClick={onClose} aria-label="Close">✕</button>
        </div>

        <div>
          <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>Available balance</div>
          <div style={{ display: "flex", alignItems: "baseline", gap: 8 }}>
            <span style={{ fontSize: "var(--text-xl)", fontFamily: "var(--font-display)", fontWeight: 600 }}>{(bal?.balance ?? 0).toFixed(2)}</span>
            <span style={{ color: "var(--text-secondary)" }}>{sym}</span>
          </div>
        </div>

        <div style={{ display: "flex", flexDirection: "column", gap: 4, borderTop: "1px solid var(--border-subtle)", paddingTop: "var(--space-2)" }}>
          <Row k="Confirmed" v={money(bal?.confirmedBalance)} />
          <Row k="Unpaid usage" v={money(bal?.unpaidUsage)} tone={(bal?.unpaidUsage ?? 0) > 0 ? "var(--warning)" : undefined} />
          {connected && <Row k="Pending earnings" v={money(pendingPayout)} />}
        </div>

        {connected && info ? (
          <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
            <div style={{ display: "flex", gap: 8 }}>
              <input
                type="number" min={info.minDeposit} step="any" placeholder={`Amount (min ${info.minDeposit})`}
                value={amount} onChange={(e) => setAmount(e.target.value)} disabled={busy}
                style={{ flex: 1, padding: "var(--space-2)" }}
              />
              <button className="btn-primary" disabled={busy || !amount.trim()}
                onClick={() => run(async () => { await depositUSDC(await getSigner(), info, amount.trim(), (p: TxProgress) => setProgress(p.message)); setAmount(""); })}>
                Deposit
              </button>
            </div>
            <button className="btn-ghost" disabled={busy || pendingPayout <= 0}
              onClick={() => run(async () => withdrawEarnings(await getSigner(), info, (p: TxProgress) => setProgress(p.message)))}>
              Withdraw earnings
            </button>
          </div>
        ) : (
          <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)" }}>Connect your wallet to deposit or withdraw.</p>
        )}

        {progress && <p style={{ margin: 0, color: "var(--text-accent)", fontSize: "var(--text-sm)" }}>{progress}</p>}
        {err && <p style={{ margin: 0, color: "var(--danger)", fontSize: "var(--text-sm)" }}>{err}</p>}

        <Link to="/wallet" onClick={onClose} style={{ color: "var(--accent)", fontSize: "var(--text-sm)", textAlign: "center" }}>View full wallet →</Link>
      </div>
    </div>
  );
}
