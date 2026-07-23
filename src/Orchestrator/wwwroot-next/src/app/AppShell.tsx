import { Outlet, NavLink, Link } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";

// The authenticated layout, rendered INSIDE StatusGate (so it only mounts when
// the app surface is chosen). Sidebar + header + routed content.

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
      <div className="mark" style={{ fontFamily: "var(--font-display)", fontWeight: 600, fontSize: 18 }}>
        DeCloud
      </div>

      {/* Primary action. Fixed-target entry into the platform's general-purpose
          template — mirrors the legacy "Create VM" button (→ platform-general),
          which is also what VmService defaults a General tenant VM to.
          A marketplace browse surface (Phase 5) can replace this later. */}
      <Link className="btn-primary" to="/marketplace/platform-general/deploy">Deploy</Link>

      {/* migrated → client routes */}
      <nav style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        {/* MIGRATED → client routes (relative to the /app basename). */}
        <NavLink to="/" end className="nav-link">Overview</NavLink>
        <NavLink to="/vms" className="nav-link">Virtual Machines</NavLink>
        <NavLink to="/settings/ssh-keys" className="nav-link">SSH Keys</NavLink>

        <hr style={{ border: 0, borderTop: "1px solid var(--border-subtle)", margin: "8px 0" }} />

        {/* UN-MIGRATED → deep-link into the legacy app (Pre-req #2 reads ?page=
            on load). Plain <a>, not NavLink: these leave the SPA entirely, so a
            client-side navigation would be wrong. Swing each one to a /app route
            as its page lands, and delete the legacy page in the same change. */}
        <a className="nav-link" href="/?page=nodes">Nodes</a>
        <a className="nav-link" href="/?page=marketplace-templates">Marketplace</a>
        <a className="nav-link" href="/?page=my-templates">My Templates</a>
        <a className="nav-link" href="/?page=settings">Settings</a>

        {/* Admin — visibility only. Every admin endpoint is enforced server-side
            with [Authorize(Roles="Admin")], and routes.tsx guards the /app admin
            tree with the same predicate. Hiding these is courtesy, not security. */}
        {isAdmin && (
          <>
            <hr style={{ border: 0, borderTop: "1px solid var(--border-subtle)", margin: "8px 0" }} />
            <span style={{ fontSize: "var(--text-xs)", color: "var(--text-tertiary)", padding: "0 var(--space-3)", letterSpacing: "var(--track-eyebrow)", fontFamily: "var(--font-mono)" }}>
              ADMIN
            </span>
            <a className="nav-link" href="/?page=admin-compliance">Compliance</a>
            <a className="nav-link" href="/?page=admin-templates">Templates</a>
            <a className="nav-link" href="/?page=admin-abuse">Abuse Reports</a>
          </>
        )}
      </nav>
    </aside>
  );
}

function Header() {
  const { wallet } = useAuth();
  // TODO: balance + runway (poll until the balance SignalR emit lands, §6.9),
  // theme toggle. Wallet address chip below.
  const address = wallet.kind === "connected" ? wallet.address : undefined;
  return (
    <header style={{ display: "flex", justifyContent: "flex-end", gap: 16, padding: "12px 28px", borderBottom: "1px solid var(--border-subtle)" }}>
      {/* TODO: <Balance /> · runway indicator */}
      <span className="mono" style={{ fontFamily: "var(--font-mono)", fontSize: 12.5 }}>
        {address ? `${address.slice(0, 6)}…${address.slice(-4)}` : ""}
      </span>
      {/* TODO: theme toggle (reuse the pre-paint theme approach) */}
    </header>
  );
}