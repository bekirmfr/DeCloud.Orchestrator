import { useState } from "react";
import type { CSSProperties } from "react";
import { Outlet, NavLink, Link } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";
import { ProfileMenu } from "../features/profile/ProfileMenu";
import { useBalance, runwayDays, formatRunway, LOW_RUNWAY_DAYS } from "../features/billing/useBalance";
import { BalanceModal } from "../features/billing/BalanceModal";

// The authenticated layout, rendered INSIDE StatusGate (so it only mounts when
// the app surface is chosen). A horizontal top bar (the Meridian reference's
// cbar) carries the nav + brand on the left and balance / wallet / Deploy on the
// right; content sits in a centred column below. No sidebar.
//
// Nav homes for what's NOT a top-level item:
//   Wallet     → the balance chip → BalanceModal → "View full wallet"
//   SSH Keys   → ProfileMenu
//   Settings   → ProfileMenu
//   My Templates → a tab on the Marketplace page (Search | My Templates)

export function AppShell() {
    return (
        <div style={{ minHeight: "100vh" }}>
            <div style={{ maxWidth: 1200, margin: "0 auto", padding: "20px 28px", display: "flex", flexDirection: "column", gap: 20 }}>
                <TopBar />
                <main>
                    <Outlet />
                </main>
            </div>
        </div>
    );
}

const seg = ({ isActive }: { isActive: boolean }): CSSProperties => ({
    fontSize: "var(--text-sm)",
    color: isActive ? "var(--text-primary)" : "var(--text-secondary)",
    fontWeight: isActive ? ("var(--fw-medium)" as CSSProperties["fontWeight"]) : "normal",
    textDecoration: "none",
    whiteSpace: "nowrap",
});

function TopBar() {
    // Both "authenticated" and "uncertain" carry the user; "uncertain" is a refresh
    // in flight, not an identity in doubt — so hide admin only when truly absent, or
    // the Admin menu would flicker on every token refresh.
    const { session } = useAuth();
    const user = session.kind === "authenticated" || session.kind === "uncertain"
        ? session.user
        : null;
    const isAdmin = canAccessAdmin(user);

    return (
        <header
            className="card"
            style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 20, padding: "13px 18px", flexWrap: "wrap" }}
        >
            <div style={{ display: "flex", alignItems: "center", gap: 24 }}>
                <div className="mark" style={{ fontFamily: "var(--font-display)", fontWeight: 600, fontSize: 17, display: "flex", alignItems: "center", gap: 9, letterSpacing: "var(--track-tight)" }}>
                    <span aria-hidden style={{ width: 9, height: 9, background: "var(--accent)", borderRadius: "50%", boxShadow: "0 0 0 4px var(--accent-soft)" }} />
                    DeCloud
                </div>
                {/* Router basename is "/app", so `to` is relative to /app. */}
                <nav style={{ display: "flex", alignItems: "center", gap: 20 }}>
                    <NavLink to="/" end style={seg}>Overview</NavLink>
                    <NavLink to="/marketplace" style={seg}>Marketplace</NavLink>
                    <NavLink to="/nodes" style={seg}>Nodes</NavLink>
                    <NavLink to="/vms" style={seg}>Virtual Machines</NavLink>
                    {isAdmin && <AdminMenu />}
                </nav>
            </div>

            <div style={{ display: "flex", alignItems: "center", gap: 16 }}>
                <HeaderBalance />
                <ProfileMenu />
                <Link className="btn-primary" to="/deploy">Deploy</Link>
            </div>
        </header>
    );
}

// Admin group → a dropdown. Visibility only; every admin endpoint is enforced
// server-side with [Authorize(Roles="Admin")] and routes.tsx guards the /app
// admin tree with the same predicate. Legacy pages still deep-link via ?page=.
function AdminMenu() {
    const [open, setOpen] = useState(false);
    const close = () => setOpen(false);
    return (
        <div style={{ position: "relative" }}>
            <button
                onClick={() => setOpen((o) => !o)}
                style={{ ...seg({ isActive: open }), background: "none", border: "none", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: 5, padding: 0, font: "inherit" }}
            >
                Admin <span aria-hidden style={{ fontSize: 10 }}>▾</span>
            </button>
            {open && (
                <>
                    <div onClick={close} style={{ position: "fixed", inset: 0, zIndex: 40 }} />
                    <div style={{ position: "absolute", top: "100%", left: 0, marginTop: 10, minWidth: 190, background: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: 6, boxShadow: "0 8px 24px rgba(0,0,0,0.12)", zIndex: 41, display: "flex", flexDirection: "column", gap: 2 }}>
                        <a className="nav-link" href="/?page=admin-compliance">Compliance</a>
                        <NavLink className="nav-link" to="/admin/templates" onClick={close}>Templates</NavLink>
                        <NavLink className="nav-link" to="/admin/nodes" onClick={close}>Nodes</NavLink>
                        <a className="nav-link" href="/?page=admin-abuse">Abuse Reports</a>
                    </div>
                </>
            )}
        </div>
    );
}

// Balance + runway (the reference's top-bar .bal). Opens the compact BalanceModal
// on click; full detail lives at /wallet. Shares the ["balance"] query, no extra fetch.
function HeaderBalance() {
    const { api } = useAuth();
    const { data: bal } = useBalance(api);
    const [open, setOpen] = useState(false);
    const sym = bal?.tokenSymbol ?? "USDC";
    const days = runwayDays(bal?.balance, bal?.hourlyBurnRate);
    const low = days != null && days < LOW_RUNWAY_DAYS;

    return (
        <>
            <button
                onClick={() => setOpen(true)}
                aria-label="Balance and runway"
                style={{ background: "none", border: "none", cursor: "pointer", color: "inherit", display: "flex", flexDirection: "column", alignItems: "flex-end", gap: 1, padding: 0 }}
            >
                <span className="mono" style={{ fontWeight: 500, fontSize: "var(--text-sm)" }}>{bal ? `${bal.balance.toFixed(2)} ${sym}` : "—"}</span>
                {days != null && (
                    <span style={{ fontSize: "var(--text-xs)", color: low ? "var(--warning)" : "var(--success)" }}>{formatRunway(days)} runway</span>
                )}
            </button>
            {open && <BalanceModal onClose={() => setOpen(false)} />}
        </>
    );
}