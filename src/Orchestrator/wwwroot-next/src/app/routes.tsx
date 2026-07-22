import { createBrowserRouter, Navigate, Outlet } from "react-router-dom";
import { AppShell } from "./AppShell";
import { StatusGate } from "./StatusGate";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";
import { SshKeysPage } from "../features/ssh-keys/SshKeysPage";
import { VmsPage } from "../features/vms/VmsPage";

// React Router v7. base is '/app/' (Vite), so route paths are relative to /app.
// The whole tree lives behind StatusGate → AppShell. Add pages here as they're
// migrated; each addition retires a legacy page (swing its sidebar link + delete
// the old module — see PHASE2_SHELL.md).

function ShellRoot() {
  return (
    <StatusGate>
      <AppShell />
    </StatusGate>
  );
}

function AdminGuard() {
  const { session } = useAuth();
  const user = session.kind === "authenticated" || session.kind === "uncertain" ? session.user : null;
  return canAccessAdmin(user) ? <Outlet /> : <Navigate to="/app" replace />;
}

export const router = createBrowserRouter(
  [
    {
      path: "/",
      element: <ShellRoot />,
      children: [
        // { index: true, element: <Dashboard /> },              // Phase 3
        // { path: "marketplace", element: <Marketplace /> },     // Phase 5
        { path: "vms", element: <VmsPage /> },                    // Phase 3 · list
        // { path: "vms/:id", element: <VmDetail /> },            // Phase 3 · detail (next)
        {
          path: "settings/ssh-keys", // ← FIRST migrated page (Phase 2)
          element: <SshKeysPage />,
        },
        {
          path: "admin",
          element: <AdminGuard />,
          children: [
            // { path: "compliance", element: <Compliance /> },   // Phase 5
          ],
        },
      ],
    },
  ],
  { basename: "/app" }
);