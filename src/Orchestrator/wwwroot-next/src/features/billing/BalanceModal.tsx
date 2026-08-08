import { useState } from "react";
import { Link } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../../auth/AuthProvider";
import { useBalance, useDepositInfo } from "./useBalance";
import { withdrawEarnings, readOnChain, addUsdcToWallet, type TxProgress } from "./paymentClient";
import { DepositModal, ModalHeader, StatRow, overlay, modalCard, modalBody, infoCard, bigBtn } from "./DepositModal";

// Compact balance popover from the sidebar card. Minimal info + the two on-chain
// actions; Deposit opens the shared DepositModal. Full detail lives at /wallet.

const rejected = (e: unknown) => {
  const c = (e as { code?: unknown })?.code;
  return c === "ACTION_REJECTED" || c === 4001;
};

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

  const [depositOpen, setDepositOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const sym = bal?.tokenSymbol ?? "USDC";
  const money = (x?: number) => `${(x ?? 0).toFixed(2)} ${sym}`;
  const pendingPayout = onchain?.pendingPayout ?? 0;

  async function onWithdraw() {
    if (!info) return;
    setBusy(true); setErr(null); setProgress(null);
    try {
      await withdrawEarnings(await getSigner(), info, (p: TxProgress) => setProgress(p.message));
      qc.invalidateQueries({ queryKey: ["balance"] });
      qc.invalidateQueries({ queryKey: ["onchain-balances"] });
    } catch (e) {
      setErr(rejected(e) ? "Cancelled in wallet." : (e as Error).message || "Withdrawal failed.");
    } finally {
      setBusy(false);
    }
  }

  async function onAddUsdc() {
    if (!info) return;
    setBusy(true); setErr(null); setProgress(null);
    try {
      const added = await addUsdcToWallet(await getSigner(), info, sym);
      setProgress(added ? "USDC added to your wallet." : "The token may already be in your wallet.");
    } catch (e) {
      setErr(rejected(e) ? "Cancelled in wallet." : (e as Error).message || "Couldn't add the token.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <div onClick={() => !busy && onClose()} style={overlay}>
        <div onClick={(e) => e.stopPropagation()} style={modalCard}>
          <ModalHeader icon="💵" title="Balance" onClose={onClose} busy={busy} />
          <div style={modalBody}>
            <div style={{ textAlign: "center" }}>
              <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)", letterSpacing: "var(--track-eyebrow)", textTransform: "uppercase" }}>Available balance</div>
              <div style={{ display: "flex", alignItems: "baseline", justifyContent: "center", gap: 8, marginTop: 4 }}>
                <span style={{ fontSize: "var(--text-xl)", fontFamily: "var(--font-display)", fontWeight: 600 }}>{(bal?.balance ?? 0).toFixed(2)}</span>
                <span style={{ color: "var(--text-secondary)" }}>{sym}</span>
              </div>
            </div>

            <div style={infoCard}>
              <StatRow label="Confirmed" value={money(bal?.confirmedBalance)} />
              <StatRow label="Unpaid usage" value={money(bal?.unpaidUsage)} tone={(bal?.unpaidUsage ?? 0) > 0 ? "var(--warning)" : undefined} />
              {connected && <StatRow label="Pending earnings" value={money(pendingPayout)} />}
            </div>

            {connected ? (
              <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                <button className="btn-primary" style={bigBtn} disabled={busy} onClick={() => setDepositOpen(true)}>+ Deposit</button>
                <button className="btn-ghost" style={bigBtn} disabled={busy || pendingPayout <= 0} onClick={onWithdraw}>Withdraw earnings</button>
                <button className="btn-ghost" style={bigBtn} disabled={busy || !info} onClick={onAddUsdc}>Add USDC to wallet</button>
              </div>
            ) : (
              <p style={{ margin: 0, color: "var(--text-tertiary)", fontSize: "var(--text-xs)", textAlign: "center" }}>Connect your wallet to deposit or withdraw.</p>
            )}

            {progress && <p style={{ margin: 0, color: "var(--text-accent)", fontSize: "var(--text-sm)", textAlign: "center" }}>{progress}</p>}
            {err && <p style={{ margin: 0, color: "var(--danger)", fontSize: "var(--text-sm)", textAlign: "center" }}>{err}</p>}

            <Link to="/wallet" onClick={onClose} style={{ color: "var(--accent)", fontSize: "var(--text-sm)", textAlign: "center" }}>View full wallet →</Link>
          </div>
        </div>
      </div>

      {depositOpen && <DepositModal onClose={() => setDepositOpen(false)} />}
    </>
  );
}
