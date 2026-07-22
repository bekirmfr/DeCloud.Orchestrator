import type { ReactNode } from "react";
import { useAuth } from "../auth/AuthProvider";
import { resolveShellView } from "./resolveShellView";
import { ConnectGate } from "./gates/ConnectGate";
import { ReauthGate } from "./gates/ReauthGate";
import { StaleBanner, NetworkBanner } from "./gates/Banners";

// The single seam between auth state and what the user sees. Reads the ONE
// derived status (Phase 1) → resolveShellView → renders connect / reauth / app.
// When the app is shown, `escrowBlocked` is provided to fund/escrow actions via
// context or a prop (wire per your state approach); do NOT scatter status checks
// into feature code — they read this decision.

export function StatusGate({ children }: { children: ReactNode }) {
  const { status } = useAuth();
  const view = resolveShellView(status);

  if (view.surface === "connect") return <ConnectGate />;
  if (view.surface === "reauth") return <ReauthGate reason={view.reason} />;

  // surface === "app"
  return (
    <>
      {view.banner === "stale" && <StaleBanner />}
      {view.banner === "wrong-network" && <NetworkBanner />}
      {/* TODO: provide view.escrowBlocked to fund/escrow actions (context or prop). */}
      {children}
    </>
  );
}
