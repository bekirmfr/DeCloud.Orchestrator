import { Outlet } from "react-router-dom";
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
  // migrated pages are client routes. Swing each link as its page lands.
  return (
    <nav className="sidebar" style={{ borderRight: "1px solid var(--border-subtle)", padding: 16 }}>
      <div className="mark" style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>DeCloud</div>
      <a className="btn-primary" href="/app">{/* TODO: Deploy action */}Deploy</a>
      {/* migrated → client routes */}
      {/* <NavLink to="/app">Overview</NavLink> ... */}
      {/* <NavLink to="/app/settings/ssh-keys">SSH Keys</NavLink>  ← first migrated (Phase 2) */}
      {/* un-migrated → legacy deep-links, e.g. <a href="/?page=nodes">Nodes</a> */}
      {/* admin section: render only if canAccessAdmin(user) */}
    </nav>
  );
}

function Header() {
  const { wallet, session } = useAuth();
  // TODO: balance + runway (poll until the balance SignalR emit lands, §6.9),
  // wallet address chip, theme toggle. Placeholder wiring below.
  const address = wallet.kind === "connected" ? wallet.address : undefined;
  void session;
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
