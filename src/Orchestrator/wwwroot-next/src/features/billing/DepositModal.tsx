import { useState } from "react";
import type { CSSProperties, ReactNode } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { useAuth } from "../../auth/AuthProvider";
import { useBalance, useDepositInfo } from "./useBalance";
import { depositUSDC, type TxProgress } from "./paymentClient";

// Shared, polished deposit modal — used by the Wallet page and the sidebar
// balance modal. Structure mirrors the legacy deposit modal (info card → amount
// → prominent action), styled with the app's tokens.

const rejected = (e: unknown) => {
  const c = (e as { code?: unknown })?.code;
  return c === "ACTION_REJECTED" || c === 4001;
};

export const overlay: CSSProperties = { position: "fixed", inset: 0, background: "rgba(0,0,0,0.65)", display: "flex", alignItems: "center", justifyContent: "center", padding: "var(--space-4)", zIndex: 60 };
export const modalCard: CSSProperties = { background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius-card)", width: "100%", maxWidth: 440, overflow: "hidden", boxShadow: "0 20px 60px rgba(0,0,0,0.5)" };
export const modalHead: CSSProperties = { display: "flex", alignItems: "center", justifyContent: "space-between", padding: "var(--space-4)", borderBottom: "1px solid var(--border-subtle)" };
export const modalBody: CSSProperties = { padding: "var(--space-4)", display: "flex", flexDirection: "column", gap: "var(--space-4)" };
export const infoCard: CSSProperties = { background: "var(--surface-2)", borderRadius: "var(--radius)", padding: "var(--space-3) var(--space-4)", display: "flex", flexDirection: "column", gap: "var(--space-2)" };
const infoRow: CSSProperties = { display: "flex", justifyContent: "space-between", alignItems: "center", gap: "var(--space-3)", fontSize: "var(--text-sm)" };
const infoLabel: CSSProperties = { color: "var(--text-tertiary)" };
const pill: CSSProperties = { fontFamily: "var(--font-mono)", color: "var(--accent)", background: "rgba(0,0,0,0.28)", padding: "3px 8px", borderRadius: "var(--radius-sm)", fontSize: "var(--text-sm)" };
const inputStyle: CSSProperties = { width: "100%", boxSizing: "border-box", padding: "var(--space-3)", fontSize: "var(--text-lg)", fontFamily: "var(--font-mono)", background: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: "var(--radius)", color: "var(--text-primary)" };
export const bigBtn: CSSProperties = { width: "100%", padding: "12px", fontSize: "var(--text-md)", fontWeight: 600 };

export function ModalHeader({ icon, title, onClose, busy }: { icon: string; title: string; onClose: () => void; busy?: boolean }) {
  return (
    <div style={modalHead}>
      <strong style={{ fontSize: "var(--text-lg)", display: "flex", alignItems: "center", gap: 8 }}><span>{icon}</span>{title}</strong>
      <button className="btn-ghost" disabled={busy} onClick={onClose} aria-label="Close" style={{ fontSize: "var(--text-lg)", lineHeight: 1 }}>✕</button>
    </div>
  );
}

export function DepositModal({ onClose }: { onClose: () => void }) {
  const { api, getSigner } = useAuth();
  const qc = useQueryClient();
  const { data: info } = useDepositInfo(api);
  const { data: bal } = useBalance(api);
  const sym = bal?.tokenSymbol ?? "USDC";

  const [amount, setAmount] = useState("");
  const [busy, setBusy] = useState(false);
  const [progress, setProgress] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [doneTx, setDoneTx] = useState<string | null>(null);

  const shortAddr = info ? `${info.escrowContractAddress.slice(0, 10)}…${info.escrowContractAddress.slice(-8)}` : "";
  const txUrl = info && doneTx ? `${info.explorerUrl}/tx/${doneTx}` : undefined;

  async function onDeposit() {
    if (!info) return;
    setBusy(true); setErr(null); setProgress(null); setDoneTx(null);
    try {
      const { txHash } = await depositUSDC(await getSigner(), info, amount.trim(), (p: TxProgress) => setProgress(p.message));
      setDoneTx(txHash); setAmount(""); setProgress(null);
      qc.invalidateQueries({ queryKey: ["balance"] });
      qc.invalidateQueries({ queryKey: ["onchain-balances"] });
    } catch (e) {
      setErr(rejected(e) ? "Cancelled in wallet." : (e as Error).message || "Deposit failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div onClick={() => !busy && onClose()} style={overlay}>
      <div onClick={(e) => e.stopPropagation()} style={modalCard}>
        <ModalHeader icon="💰" title={`Deposit ${sym}`} onClose={onClose} busy={busy} />
        <div style={modalBody}>
          {info && (
            <div style={infoCard}>
              <div style={infoRow}><span style={infoLabel}>Network</span><strong>{info.chainName}</strong></div>
              <div style={infoRow}><span style={infoLabel}>Contract</span>
                {info.explorerUrl
                  ? <a href={`${info.explorerUrl}/address/${info.escrowContractAddress}`} target="_blank" rel="noreferrer" style={pill}>{shortAddr}</a>
                  : <code style={pill}>{shortAddr}</code>}
              </div>
              <div style={infoRow}><span style={infoLabel}>Min deposit</span><strong>{info.minDeposit} {sym}</strong></div>
            </div>
          )}

          <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
            <label style={{ fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)" }}>Amount ({sym})</label>
            <input type="number" min={info?.minDeposit} step="any" placeholder="10.00" value={amount} disabled={busy || !info}
              onChange={(e) => setAmount(e.target.value)} style={inputStyle} />
          </div>

          {doneTx ? (
            <div style={{ display: "flex", flexDirection: "column", gap: 6 }}>
              <p style={{ margin: 0, color: "var(--success)", fontWeight: "var(--fw-medium)" }}>✓ Deposit confirmed</p>
              {txUrl && <a href={txUrl} target="_blank" rel="noreferrer" style={{ color: "var(--accent)", fontSize: "var(--text-sm)" }}>View transaction ↗</a>}
              <button className="btn-ghost" style={{ ...bigBtn, marginTop: 4 }} onClick={onClose}>Done</button>
            </div>
          ) : (
            <button className="btn-primary" style={bigBtn} disabled={busy || !info || !amount.trim()} onClick={onDeposit}>
              {busy ? "Working…" : `Deposit ${sym}`}
            </button>
          )}

          {progress && <p style={{ margin: 0, color: "var(--text-accent)", fontSize: "var(--text-sm)", textAlign: "center" }}>{progress}</p>}
          {err && <p style={{ margin: 0, color: "var(--danger)", fontSize: "var(--text-sm)", textAlign: "center" }}>{err}</p>}
        </div>
      </div>
    </div>
  );
}

// Small helper so callers can render a labelled stat row consistently.
export function StatRow({ label, value, tone }: { label: string; value: ReactNode; tone?: string }) {
  return (
    <div style={infoRow}>
      <span style={infoLabel}>{label}</span>
      <span style={{ color: tone ?? "var(--text-secondary)" }}>{value}</span>
    </div>
  );
}
