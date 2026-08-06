import { createBrowserRouter, Navigate, Outlet } from "react-router-dom";
import { AppShell } from "./AppShell";
import { StatusGate } from "./StatusGate";
import { useAuth } from "../auth/AuthProvider";
import { canAccessAdmin } from "./guards";
import { RouteError } from "./RouteError";
import { SshKeysPage } from "../features/ssh-keys/SshKeysPage";
import { DashboardPage } from "../features/dashboard/DashboardPage";
import { VmsPage } from "../features/vms/VmsPage";
import { VmDetailPage } from "../features/vms/VmDetailPage";
import { DirectAccessModal } from "../features/direct-access/DirectAccessModal";
import { DomainsModal } from "../features/domains/DomainsModal";
import { TerminalPage } from "../features/vms/TerminalPage";
import { FileBrowserPage } from "../features/vms/FileBrowserPage";
import { MyTemplatesPage } from "../features/templates/MyTemplatesPage";
import { CreateTemplatePage } from "../features/templates/CreateTemplatePage";
import { AdminTemplatesPage } from "../features/templates/AdminTemplatesPage";
import { AdminTemplateInspectPage } from "../features/templates/AdminTemplateInspectPage";
import { DeployPage } from "../features/deploy/DeployPage";
import { DeploySourcePage } from "../features/deploy/DeploySourcePage";
import { RepoDeployPage } from "../features/deploy/RepoDeployPage";
import { MarketplacePage } from "../features/marketplace/MarketplacePage";
import { MarketplaceDetailPage } from "../features/marketplace/MarketplaceDetailPage";
import { NodesPage } from "../features/nodes/NodesPage";
import { NodeDetailPage } from "../features/nodes/NodeDetailPage";
import { AdminNodesPage } from "../features/nodes/AdminNodesPage";
import { AdminNodeInspectPage } from "../features/nodes/AdminNodeInspectPage";

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
      // Covers the whole /app tree: React Router bubbles a child's throw to the
      // nearest errorElement, and an unmatched URL arrives here as a 404 route
      // response. Without it the router renders its own developer fallback —
      // a minified stack trace — which is what production showed for the
      // DeployPage hooks crash.
      errorElement: <RouteError />,
      children: [
        { index: true, element: <DashboardPage /> },              // Phase 3 · operate+fund home
        { path: "deploy", element: <DeploySourcePage /> },        // Phase 5 · deployment-source chooser
        { path: "deploy/repository", element: <RepoDeployPage /> }, // Phase 5 · repo deploy (form WIP)
        { path: "marketplace", element: <MarketplacePage /> },    // Phase 5 · browse
        { path: "my-templates", element: <MyTemplatesPage /> },    // Phase 5 · authoring: my templates
        { path: "my-templates/new", element: <CreateTemplatePage /> },        // create (form WIP)
        { path: "my-templates/:id/edit", element: <CreateTemplatePage /> },   // edit (form WIP)
        { path: "vms", element: <VmsPage /> },                    // Phase 3 · list
        { path: "nodes", element: <NodesPage /> },                // Phase 5 · my nodes + search
        { path: "nodes/:id", element: <NodeDetailPage /> },        // Phase 5 · node detail
        {
          path: "vms/:id",                                        // Phase 3 · detail cockpit
          element: <VmDetailPage />,
          // Modal-routes (DESIGN §3): overlay the cockpit, URL survives reload,
          // Back closes. VmDetailPage renders <Outlet/> where these appear.
          children: [
            { path: "ports", element: <DirectAccessModal /> },   // Smart Port Allocation
            { path: "domains", element: <DomainsModal /> },       // custom domains (central ingress)
          ],
        },
        { path: "vms/:id/terminal", element: <TerminalPage /> },   // full-page terminal (new tab)
        { path: "vms/:id/files", element: <FileBrowserPage /> },    // full-page file browser (new tab)
        { path: "marketplace/:slug", element: <MarketplaceDetailPage /> },   // Phase 5 · detail
        { path: "marketplace/:slug/deploy", element: <DeployPage /> }, // Phase 3 · deploy
        {
          path: "settings/ssh-keys", // ← FIRST migrated page (Phase 2)
          element: <SshKeysPage />,
        },
        {
          path: "admin",
          element: <AdminGuard />,
          children: [
            { path: "templates", element: <AdminTemplatesPage /> },   // Phase 5 · review queue
            { path: "templates/:id", element: <AdminTemplateInspectPage /> },   // Phase 5 · read-only inspect
            { path: "nodes", element: <AdminNodesPage /> },   // Phase 5 · admin node manager
            { path: "nodes/:id", element: <AdminNodeInspectPage /> },   // Phase 5 · admin node inspect
            // { path: "compliance", element: <Compliance /> },   // Phase 5
          ],
        },
      ],
    },
  ],
  { basename: "/app" }
);
