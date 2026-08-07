import { useState } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useBalance } from "./useBalance";
import { BalanceModal } from "./BalanceModal";

// Pinned to the bottom of the sidebar (marginTop:auto). Shows the available
// balance; opens the compact BalanceModal on click. Full detail lives at /wallet.
export function SidebarBalance() {
  const { api } = useAuth();
  const { data: bal } = useBalance(api);
  const [open, setOpen] = useState(false);
  const sym = bal?.tokenSymbol ?? "USDC";

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        style={{ marginTop: "auto", textAlign: "left", background: "var(--surface-1)", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius)", padding: "var(--space-3)", cursor: "pointer", display: "flex", flexDirection: "column", gap: 2, color: "inherit" }}
      >
        <span style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)" }}>Balance</span>
        <span style={{ fontSize: "var(--text-md)", fontFamily: "var(--font-display)", fontWeight: 600 }}>
          {bal ? `${bal.balance.toFixed(2)} ${sym}` : "—"}
        </span>
      </button>
      {open && <BalanceModal onClose={() => setOpen(false)} />}
    </>
  );
}
