import { useEffect, useState } from "react";
import type { CSSProperties } from "react";
import { Outlet, NavLink, Link, useLocation } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";
import { ProfileMenu } from "../features/profile/ProfileMenu";
import { useBalance, runwayDays, formatRunway, LOW_RUNWAY_DAYS } from "../features/billing/useBalance";
import { BalanceModal } from "../features/billing/BalanceModal";
import { useMediaQuery, MOBILE_QUERY } from "./useMediaQuery";

// The authenticated layout. A horizontal top bar (the Meridian reference's cbar)
// on desktop; on narrow screens the nav collapses into a hamburger → slide-in
// drawer. Content sits in a centred column below. Nav homes for non-top-level
// items: Wallet → balance chip → modal; SSH Keys / Settings → ProfileMenu;
// My Templates → the Marketplace "My Templates" tab.

// Router basename is "/app", so `to` is relative to /app.
const NAV: { to: string; label: string; end?: boolean }[] = [
    { to: "/", label: "Overview", end: true },
    { to: "/marketplace", label: "Marketplace" },
    { to: "/nodes", label: "Nodes" },
    { to: "/vms", label: "Virtual Machines" },
];
const ADMIN_NAV: { to: string; label: string; legacy?: boolean }[] = [
    { to: "/?page=admin-compliance", label: "Compliance", legacy: true },
    { to: "/admin/templates", label: "Templates" },
    { to: "/admin/nodes", label: "Nodes" },
    { to: "/?page=admin-abuse", label: "Abuse Reports", legacy: true },
];

export function AppShell() {
    const mobile = useMediaQuery(MOBILE_QUERY);
    return (
        <div style={{ minHeight: "100vh" }}>
            <div style={{ maxWidth: 1200, margin: "0 auto", padding: mobile ? "12px 16px" : "20px 28px", display: "flex", flexDirection: "column", gap: mobile ? 16 : 20 }}>
                <TopBar mobile={mobile} />
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

function Wordmark({ size = 17 }: { size?: number }) {
    return (
        <div className="mark" style={{ fontFamily: "var(--font-display)", fontWeight: 600, fontSize: size, display: "flex", alignItems: "center", gap: 9, letterSpacing: "var(--track-tight)" }}>
            <span aria-hidden style={{ width: 9, height: 9, background: "var(--accent)", borderRadius: "50%", boxShadow: "0 0 0 4px var(--accent-soft)", flex: "none" }} />
            DeCloud
        </div>
    );
}

function TopBar({ mobile }: { mobile: boolean }) {
    // "authenticated" and "uncertain" both carry the user; "uncertain" is a refresh
    // in flight, not an identity in doubt — hide admin only when truly absent.
    const { session } = useAuth();
    const user = session.kind === "authenticated" || session.kind === "uncertain" ? session.user : null;
    const isAdmin = canAccessAdmin(user);
    const [drawer, setDrawer] = useState(false);

    if (mobile) {
        return (
            <>
                <header className="card" style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 12, padding: "10px 14px" }}>
                    <div style={{ display: "flex", alignItems: "center", gap: 12, minWidth: 0 }}>
                        <button aria-label="Menu" onClick={() => setDrawer(true)} style={{ background: "none", border: "none", cursor: "pointer", padding: 4, color: "var(--text-primary)", fontSize: 20, lineHeight: 1 }}>☰</button>
                        <Wordmark size={16} />
                    </div>
                    <ProfileMenu />
                </header>
                {drawer && <Drawer isAdmin={isAdmin} onClose={() => setDrawer(false)} />}
            </>
        );
    }

    return (
        <header className="card" style={{ display: "flex", alignItems: "center", justifyContent: "space-between", gap: 20, padding: "13px 18px", flexWrap: "wrap" }}>
            <div style={{ display: "flex", alignItems: "center", gap: 24 }}>
                <Wordmark />
                <nav style={{ display: "flex", alignItems: "center", gap: 20 }}>
                    {NAV.map((n) => <NavLink key={n.to} to={n.to} end={n.end} style={seg}>{n.label}</NavLink>)}
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

// Slide-in nav drawer for mobile. Closes on route change (below) and on any
// link tap; the overlay closes it too.
function Drawer({ isAdmin, onClose }: { isAdmin: boolean; onClose: () => void }) {
    const loc = useLocation();
    const first = useState(loc.key)[0];
    // Close whenever the location changes (a nav link was followed).
    useEffect(() => { if (loc.key !== first) onClose(); }, [loc.key, first, onClose]);

    const item: CSSProperties = { fontSize: "var(--text-md)" };
    return (
        <div onClick={onClose} style={{ position: "fixed", inset: 0, background: "rgba(0,0,0,0.4)", zIndex: 60 }}>
            <aside
                onClick={(e) => e.stopPropagation()}
                style={{ position: "fixed", top: 0, left: 0, bottom: 0, width: "min(84vw, 300px)", background: "var(--surface-1)", borderRight: "1px solid var(--border)", padding: 18, display: "flex", flexDirection: "column", gap: 14, overflowY: "auto" }}
            >
                <div style={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                    <Wordmark />
                    <button aria-label="Close menu" onClick={onClose} style={{ background: "none", border: "none", cursor: "pointer", color: "var(--text-secondary)", fontSize: 18 }}>✕</button>
                </div>

                <Link className="btn-primary" to="/deploy" onClick={onClose} style={{ textAlign: "center" }}>Deploy</Link>
                <DrawerBalance />

                <nav style={{ display: "flex", flexDirection: "column", gap: 2 }}>
                    {NAV.map((n) => <NavLink key={n.to} to={n.to} end={n.end} className="nav-link" onClick={onClose} style={item}>{n.label}</NavLink>)}
                    {isAdmin && (
                        <>
                            <span className="eyebrow muted" style={{ padding: "12px var(--space-3) 4px" }}>Admin</span>
                            {ADMIN_NAV.map((n) => n.legacy
                                ? <a key={n.to} className="nav-link" href={n.to} style={item}>{n.label}</a>
                                : <NavLink key={n.to} to={n.to} className="nav-link" onClick={onClose} style={item}>{n.label}</NavLink>)}
                        </>
                    )}
                </nav>
            </aside>
        </div>
    );
}

// Admin group → a dropdown (desktop). Visibility only; every admin endpoint is
// enforced server-side with [Authorize(Roles="Admin")].
function AdminMenu() {
    const [open, setOpen] = useState(false);
    const close = () => setOpen(false);
    return (
        <div style={{ position: "relative" }}>
            <button onClick={() => setOpen((o) => !o)} style={{ ...seg({ isActive: open }), background: "none", border: "none", cursor: "pointer", display: "inline-flex", alignItems: "center", gap: 5, padding: 0, font: "inherit" }}>
                Admin <span aria-hidden style={{ fontSize: 10 }}>▾</span>
            </button>
            {open && (
                <>
                    <div onClick={close} style={{ position: "fixed", inset: 0, zIndex: 40 }} />
                    <div style={{ position: "absolute", top: "100%", left: 0, marginTop: 10, minWidth: 190, background: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: "var(--radius)", padding: 6, boxShadow: "0 8px 24px rgba(0,0,0,0.12)", zIndex: 41, display: "flex", flexDirection: "column", gap: 2 }}>
                        {ADMIN_NAV.map((n) => n.legacy
                            ? <a key={n.to} className="nav-link" href={n.to}>{n.label}</a>
                            : <NavLink key={n.to} className="nav-link" to={n.to} onClick={close}>{n.label}</NavLink>)}
                    </div>
                </>
            )}
        </div>
    );
}

// Balance + runway (the reference's top-bar .bal). Opens the compact BalanceModal;
// full detail at /wallet. Shares the ["balance"] query, no extra fetch.
function HeaderBalance() {
    const { api } = useAuth();
    const { data: bal } = useBalance(api);
    const [open, setOpen] = useState(false);
    const sym = bal?.tokenSymbol ?? "USDC";
    const days = runwayDays(bal?.balance, bal?.hourlyBurnRate);
    const low = days != null && days < LOW_RUNWAY_DAYS;
    return (
        <>
            <button onClick={() => setOpen(true)} aria-label="Balance and runway" style={{ background: "none", border: "none", cursor: "pointer", color: "inherit", display: "flex", flexDirection: "column", alignItems: "flex-end", gap: 1, padding: 0 }}>
                <span className="mono" style={{ fontWeight: 500, fontSize: "var(--text-sm)" }}>{bal ? `${bal.balance.toFixed(2)} ${sym}` : "—"}</span>
                {days != null && <span style={{ fontSize: "var(--text-xs)", color: low ? "var(--warning)" : "var(--success)" }}>{formatRunway(days)} runway</span>}
            </button>
            {open && <BalanceModal onClose={() => setOpen(false)} />}
        </>
    );
}

// Balance row for the mobile drawer (left-aligned, opens the modal).
function DrawerBalance() {
    const { api } = useAuth();
    const { data: bal } = useBalance(api);
    const [open, setOpen] = useState(false);
    const sym = bal?.tokenSymbol ?? "USDC";
    const days = runwayDays(bal?.balance, bal?.hourlyBurnRate);
    const low = days != null && days < LOW_RUNWAY_DAYS;
    return (
        <>
            <button onClick={() => setOpen(true)} style={{ textAlign: "left", background: "var(--surface-2)", border: "1px solid var(--border-subtle)", borderRadius: "var(--radius)", padding: "var(--space-3)", cursor: "pointer", color: "inherit", display: "flex", flexDirection: "column", gap: 2 }}>
                <span style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)" }}>Balance</span>
                <span className="mono" style={{ fontWeight: 500, fontSize: "var(--text-md)" }}>{bal ? `${bal.balance.toFixed(2)} ${sym}` : "—"}</span>
                {days != null && <span style={{ fontSize: "var(--text-xs)", color: low ? "var(--warning)" : "var(--success)" }}>{formatRunway(days)} runway</span>}
            </button>
            {open && <BalanceModal onClose={() => setOpen(false)} />}
        </>
    );
}