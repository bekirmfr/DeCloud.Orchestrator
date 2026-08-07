import { useState } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { avatarGradient } from "./avatar";

// Header identity control: avatar + address, click reveals profile / log out.
export function ProfileMenu() {
  const { wallet, signOut } = useAuth();
  const [open, setOpen] = useState(false);
  const address = wallet.kind === "connected" ? wallet.address : undefined;
  if (!address) return null;
  const short = `${address.slice(0, 6)}…${address.slice(-4)}`;

  return (
    <div style={{ position: "relative" }}>
      <button
        onClick={() => setOpen((o) => !o)}
        style={{ display: "flex", alignItems: "center", gap: 8, background: "none", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: "4px 10px 4px 4px", cursor: "pointer", color: "var(--text-primary)" }}
      >
        <span style={{ width: 24, height: 24, borderRadius: "50%", background: avatarGradient(address), display: "inline-block", flexShrink: 0 }} />
        <span style={{ fontFamily: "var(--font-mono)", fontSize: 12.5 }}>{short}</span>
      </button>

      {open && (
        <>
          <div onClick={() => setOpen(false)} style={{ position: "fixed", inset: 0, zIndex: 40 }} />
          <div style={{ position: "absolute", right: 0, top: "calc(100% + 6px)", minWidth: 220, background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: "var(--space-2)", zIndex: 41, display: "flex", flexDirection: "column", gap: 4, boxShadow: "0 8px 24px rgba(0,0,0,0.35)" }}>
            <div style={{ padding: "6px 10px", borderBottom: "1px solid var(--border-subtle)", marginBottom: 4 }}>
              <div style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)" }}>Signed in as</div>
              <div style={{ fontFamily: "var(--font-mono)", fontSize: "var(--text-xs)", color: "var(--text-secondary)", wordBreak: "break-all" }}>{address}</div>
            </div>
            <Link to="/profile" onClick={() => setOpen(false)} className="nav-link" style={{ fontSize: "var(--text-sm)" }}>Profile</Link>
            <Link to="/settings/ssh-keys" onClick={() => setOpen(false)} className="nav-link" style={{ fontSize: "var(--text-sm)" }}>SSH Keys</Link>
            <Link to="/settings" onClick={() => setOpen(false)} className="nav-link" style={{ fontSize: "var(--text-sm)" }}>Settings</Link>
            <hr style={{ border: 0, borderTop: "1px solid var(--border-subtle)", margin: "4px 0" }} />
            <button
              onClick={() => { setOpen(false); void signOut(); }}
              className="nav-link"
              style={{ textAlign: "left", background: "none", border: "none", cursor: "pointer", color: "var(--danger)", fontSize: "var(--text-sm)" }}
            >
              Log out
            </button>
          </div>
        </>
      )}
    </div>
  );
}
