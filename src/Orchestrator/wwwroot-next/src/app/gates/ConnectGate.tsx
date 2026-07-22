import { useAuth } from "../../auth/AuthProvider";

// Full-screen gate: no wallet connected.
export function ConnectGate() {
  const { connect } = useAuth();
  return (
    <div className="gate">
      {/* TODO: Meridian-styled full-screen surface (mesh + headline). */}
      <h1 style={{ fontFamily: "var(--font-display)" }}>Connect your wallet</h1>
      <button className="btn-primary" onClick={() => void connect()}>
        Connect
      </button>
    </div>
  );
}
