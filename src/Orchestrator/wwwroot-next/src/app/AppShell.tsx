import { useState } from "react";
import { Outlet, NavLink, Link } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";
import { ProfileMenu } from "../features/profile/ProfileMenu";
import { useBalance, runwayDays, formatRunway, LOW_RUNWAY_DAYS } from "../features/billing/useBalance";
import { BalanceModal } from "../features/billing/BalanceModal";

// The authenticated layout, rendered INSIDE StatusGate (so it only mounts when
// the app surface is chosen). Sidebar (nav) + header (balance + wallet) + content.
// Balance and wallet live together in the header — the Meridian reference's top
// bar keeps them side by side rather than split across the shell.

export function AppShell() {
    return (
        <div className="app-shell" style={{ display: "grid", gridTemplateColumns: "240px 1fr", minHeight: "100vh" }}>
            <Sidebar />
            <div>
                <Header />
                <main style={{ padding: "24px 28px" }}>
                    <Outlet />
                </main>
            </div>
        </div>
    );
}

function Sidebar() {
    // Both "authenticated" and "uncertain" carry the user (SessionState in
    // auth/types.ts) — "uncertain" means a refresh is in flight, not that the
    // identity is in doubt, so hiding admin links there would make them flicker
    // on every token refresh.
    const { session } = useAuth();
    const user = session.kind === "authenticated" || session.kind === "uncertain"
        ? session.user
        : null;
    const isAdmin = canAccessAdmin(user);

    // Primary action = Deploy (promoted, DESIGN §2). During migration, links to
    // not-yet-migrated pages point at the legacy app via /?page=… (Change 2);
    // migrated pages are client routes (NavLink). Swing each link as its page lands.
    // NOTE: router basename is "/app", so NavLink `to` is RELATIVE to /app
    // (use "/settings/ssh-keys", not "/app/settings/ssh-keys").
    return (
        <aside style={{ borderRight: "1px solid var(--border-subtle)", padding: 16, display: "flex", flexDirection: "column", gap: 16 }}>
            {/* Wordmark with the reference's glowing accent dot. */}
            <div className="mark" style={{ fontFamily: "var(--font-display)", fontWeight: 600, fontSize: 18, display: "flex", alignItems: "center", gap: 9 }}>
                <span aria-hidden style={{ width: 9, height: 9, background: "var(--accent)", borderRadius: "50%", boxShadow: "0 0 0 4px var(--accent-soft)" }} />
                DeCloud
            </div>

            {/* Primary action → the marketplace browse, where the user picks a template
          (Phase 5). Previously hard-coded to platform-general; that fixed target
          is retired now that browse exists (General Purpose VM is a template there
          for anyone who just wants a plain VM). */}
            <Link className="btn-primary" to="/deploy">Deploy</Link>

            {/* migrated → client routes */}
            <nav style={{ display: "flex", flexDirection: "column", gap: 4 }}>
                {/* MIGRATED → client routes (relative to the /app basename). */}
                <NavLink to="/" end className="nav-link">Overview</NavLink>
                <NavLink to="/marketplace" className="nav-link">Marketplace</NavLink>
                <NavLink to="/vms" className="nav-link">Virtual Machines</NavLink>
                <NavLink to="/wallet" className="nav-link">Wallet</NavLink>
                <NavLink to="/settings/ssh-keys" className="nav-link">SSH Keys</NavLink>

                <hr style={{ border: 0, borderTop: "1px solid var(--border-subtle)", margin: "8px 0" }} />

                {/* UN-MIGRATED → deep-link into the legacy app (Pre-req #2 reads ?page=
            on load). Plain <a>, not NavLink: these leave the SPA entirely, so a
            client-side navigation would be wrong. Swing each one to a /app route
            as its page lands, and delete the legacy page in the same change. */}
                <NavLink to="/nodes" className="nav-link">Nodes</NavLink>
                <NavLink to="/my-templates" className="nav-link">My Templates</NavLink>
                <NavLink to="/settings" className="nav-link">Settings</NavLink>

                {/* Admin — visibility only. Every admin endpoint is enforced server-side
            with [Authorize(Roles="Admin")], and routes.tsx guards the /app admin
            tree with the same predicate. Hiding these is courtesy, not security. */}
                {isAdmin && (
                    <>
                        <hr style={{ border: 0, borderTop: "1px solid var(--border-subtle)", margin: "8px 0" }} />
                        <span className="eyebrow muted" style={{ padding: "0 var(--space-3)", marginBottom: 2 }}>Admin</span>
                        <a className="nav-link" href="/?page=admin-compliance">Compliance</a>
                        <NavLink to="/admin/templates" className="nav-link">Templates</NavLink>
                        <NavLink to="/admin/nodes" className="nav-link">Nodes</NavLink>
                        <a className="nav-link" href="/?page=admin-abuse">Abuse Reports</a>
                    </>
                )}
            </nav>
        </aside>
    );
}

function Header() {
    return (
        <header style={{ display: "flex", justifyContent: "flex-end", alignItems: "center", gap: 18, padding: "12px 28px", borderBottom: "1px solid var(--border-subtle)" }}>
            <HeaderBalance />
            <ProfileMenu />
        </header>
    );
}

// Balance + runway, right-aligned in the header (the reference's top-bar .bal).
// Opens the compact BalanceModal on click; full detail lives at /wallet. Shares
// the ["balance"] query with the wallet page, so no extra fetch.
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
                    <span style={{ fontSize: "var(--text-xs)", color: low ? "var(--warning)" : "var(--success)" }}>~{formatRunway(days)} runway</span>
                )}
            </button>
            {open && <BalanceModal onClose={() => setOpen(false)} />}
        </>
    );
}