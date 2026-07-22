import { useAuth } from "../../auth/AuthProvider";

// Full-screen gate: wallet present but no valid session, OR the connected address
// differs from the session (fail closed — sign in AS THE NEW ADDRESS).
export function ReauthGate({ reason }: { reason: "auth" | "address-mismatch" }) {
  const { signIn } = useAuth();
  const mismatch = reason === "address-mismatch";
  return (
    <div className="gate">
      <h1 style={{ fontFamily: "var(--font-display)" }}>
        {mismatch ? "Wallet changed — sign in again" : "Sign in"}
      </h1>
      {mismatch && (
        <p style={{ color: "var(--text-secondary)" }}>
          You're connected with a different address. Sign in to continue as it.
        </p>
      )}
      <button className="btn-primary" onClick={() => void signIn()}>
        Sign in
      </button>
    </div>
  );
}
