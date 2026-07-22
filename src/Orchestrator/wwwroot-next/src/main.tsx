// Import order matters: fonts register @font-face, tokens define the CSS
// variables, global applies the reset/base that consumes them.
import "@fontsource-variable/space-grotesk";
import "@fontsource-variable/inter";
import "@fontsource/jetbrains-mono/400.css";
import "@fontsource/jetbrains-mono/500.css";
import "./styles/design-tokens.css";
import "./styles/global.css";

import React from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { RouterProvider } from "react-router-dom";
import { AuthProvider } from "./auth/AuthProvider";
import { HubProvider } from "./realtime/HubProvider";
import { router } from "./app/routes";

// Server data via TanStack Query. retry:1 keeps a single retry (transport blips);
// api() already handles the 401 refresh-retry, so query-level retries stay low.
const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, refetchOnWindowFocus: false, staleTime: 30_000 },
  },
});

const el = document.getElementById("root");
if (!el) throw new Error("#root not found");

// Provider order: QueryClient (features use useQuery) > Auth (routes read useAuth)
// > Hub (one SignalR connection, needs auth + query cache) > Router.
createRoot(el).render(
  <React.StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <HubProvider>
          <RouterProvider router={router} />
        </HubProvider>
      </AuthProvider>
    </QueryClientProvider>
  </React.StrictMode>
);
