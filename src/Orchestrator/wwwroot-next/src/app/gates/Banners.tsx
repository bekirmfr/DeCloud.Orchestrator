import { useAuth } from "../../auth/AuthProvider";

// In-app banner: session shown from last-known data, unverifiable (degraded).
export function StaleBanner() {
  return (
    <div role="status" style={{ background: "var(--warning-soft)", color: "var(--warning)", padding: "8px 16px" }}>
      Reconnecting — showing last-known data.
    </div>
  );
}

// In-app banner: wrong chain. App still works; fund/escrow actions are gated.
export function NetworkBanner() {
  const { switchToExpectedChain } = useAuth();
  return (
    <div role="status" style={{ background: "var(--accent-soft)", color: "var(--text-accent)", padding: "8px 16px" }}>
      Wrong network — switch to Polygon to fund or deploy.{" "}
      <button className="link" onClick={() => void switchToExpectedChain()}>
        Switch
      </button>
    </div>
  );
}
