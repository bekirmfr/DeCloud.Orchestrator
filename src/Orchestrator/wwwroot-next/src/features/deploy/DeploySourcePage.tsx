import { Link, useSearchParams } from "react-router-dom";
import { type CSSProperties } from "react";

// Phase 5 · "Deploy" now opens a source chooser instead of hard-launching one
// template. Three sources: an empty general-purpose VM, a marketplace template,
// or a Git repository. GROUNDED slugs: platform-general (General Purpose VM) and
// platform-repo-deploy (Deploy from Repository).

interface Source {
  emoji: string;
  title: string;
  blurb: string;
  to: string;
  cta: string;
}

const SOURCES: Source[] = [
  {
    emoji: "🖥️",
    title: "Empty workload",
    blurb: "A blank General Purpose VM — SSH in and run whatever you like.",
    to: "/marketplace/platform-general/deploy",
    cta: "Configure & deploy",
  },
  {
    emoji: "🧩",
    title: "From marketplace",
    blurb: "Deploy a published, ready-to-run template — databases, AI, game servers, and more.",
    to: "/marketplace",
    cta: "Browse templates",
  },
  {
    emoji: "🐙",
    title: "From GitHub",
    blurb: "Point at a repository and port; it builds your code (Dockerfile, compose, or Nixpacks) and runs it.",
    to: "/deploy/repository",
    cta: "Deploy a repo",
  },
];

const card: CSSProperties = {
  display: "flex", flexDirection: "column", gap: "var(--space-2)",
  padding: "var(--space-4)", textDecoration: "none", color: "inherit",
  border: "1px solid var(--border)", borderRadius: "var(--radius-md)",
  background: "var(--surface-1)",
};

export function DeploySourcePage() {
  const [params] = useSearchParams();
  const node = params.get("node");
  const q = node ? `?node=${node}` : "";
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 900 }}>
      <div>
        <h1 style={{ margin: 0 }}>Deploy</h1>
        <p style={{ margin: "var(--space-1) 0 0", color: "var(--text-secondary)" }}>
          Choose where your workload comes from.
        </p>
      </div>

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))", gap: "var(--space-3)" }}>
        {SOURCES.map((s) => (
          <Link key={s.title} to={`${s.to}${q}`} className="card" style={card}>
            <span style={{ fontSize: 30 }}>{s.emoji}</span>
            <strong style={{ fontSize: "var(--text-md)" }}>{s.title}</strong>
            <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)", lineHeight: 1.4, flex: 1 }}>
              {s.blurb}
            </p>
            <span style={{ color: "var(--accent)", fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)" }}>
              {s.cta} →
            </span>
          </Link>
        ))}
      </div>
    </div>
  );
}
