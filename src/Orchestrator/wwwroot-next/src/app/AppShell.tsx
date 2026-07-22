import { Outlet, NavLink } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";

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

      <a className="btn-primary" href="/app">{/* TODO: real Deploy action */}Deploy</a>

      {/* migrated → client routes */}
      <nav style={{ display: "flex", flexDirection: "column", gap: 4 }}>
        {/* <NavLink to="/" end className="nav-link">Overview</NavLink> */}
        <NavLink to="/settings/ssh-keys" className="nav-link">SSH Keys</NavLink>
        {/* un-migrated → legacy deep-links, e.g. <a href="/?page=nodes">Nodes</a> */}
        {/* admin section: render only if canAccessAdmin(user) */}
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