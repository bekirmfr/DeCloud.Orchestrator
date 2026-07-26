import { Link } from "react-router-dom";

// Interim page for the repo-deploy form (template slug platform-repo-deploy).
// The full form — source URL / ref / port / database / env vars / private-repo
// deploy key, encoded into the base64 DEPLOY_CONF_B64 / APP_ENV_B64 /
// DEPLOY_KEY_B64 payloads and deployed via environmentVariables — is the next
// slice (a faithful port of the legacy repo-deploy.js). This stands in so the
// "From GitHub" source resolves instead of 404-ing or leading to a deploy that
// can't collect the repo config.
export function RepoDeployPage() {
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-3)", maxWidth: 640 }}>
      <Link to="/deploy" className="nav-link" style={{ alignSelf: "start" }}>← Deploy</Link>
      <h1 style={{ margin: 0 }}>Deploy from Repository</h1>
      <p style={{ margin: 0, color: "var(--text-secondary)", lineHeight: 1.5 }}>
        Point at a Git repository and the port your app listens on; the platform builds your
        code — a Dockerfile wins, then a compose file, then Nixpacks — and runs it.
      </p>
      <p style={{ margin: 0, color: "var(--text-secondary)" }}>
        This form is being built. In the meantime you can{" "}
        <Link to="/marketplace" style={{ color: "var(--accent)" }}>browse the marketplace</Link>.
      </p>
    </div>
  );
}
