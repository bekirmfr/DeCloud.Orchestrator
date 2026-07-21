import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The new React app (frontend migration, Option A).
// - Served under /app in production, so base must be "/app/".
// - Builds into ../wwwroot/dist-app, which Program.cs serves at RequestPath "/app"
//   (see the /app static provider + the amended MapFallback).
// - Dev runs on its own port (3001) so the legacy app's Vite (:3000) is untouched;
//   /api, /hub and /ws are proxied to the .NET backend on :5050, matching the
//   backend's endpoints (REST, the SignalR hub /hub/orchestrator, and the
//   terminal/sftp WebSocket proxy).
const BACKEND = "http://localhost:5050";

export default defineConfig({
  base: "/app/",
  plugins: [react()],
  build: {
    outDir: "../wwwroot/dist-app",
    emptyOutDir: true,
    sourcemap: true,
  },
  server: {
    port: 3001,
    strictPort: true,
    proxy: {
      "/api": { target: BACKEND, changeOrigin: true },
      "/hub": { target: BACKEND, changeOrigin: true, ws: true },
      "/ws": { target: BACKEND, changeOrigin: true, ws: true },
    },
  },
});
