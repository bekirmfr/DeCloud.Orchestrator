import { useCallback, useState } from "react";

/**
 * Phase 0 proof — not the real shell (that's Phase 2).
 * It exists only to prove the pipeline end to end: React + TS + Vite building to
 * wwwroot/dist-app, served under /app, with Meridian tokens + self-hosted fonts,
 * and light/dark theming working (pre-paint in index.html, toggle here).
 */

type Theme = "light" | "dark";

function currentTheme(): Theme {
  return document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light";
}

export default function App() {
  const [theme, setTheme] = useState<Theme>(currentTheme);

  const toggle = useCallback(() => {
    const next: Theme = theme === "dark" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", next);
    try {
      localStorage.setItem("dc-theme", next);
    } catch {
      /* ignore */
    }
    setTheme(next);
  }, [theme]);

  return (
    <main style={{ minHeight: "100vh", position: "relative", overflow: "hidden" }}>
      <Mesh />
      <div
        style={{
          position: "relative",
          maxWidth: 720,
          margin: "0 auto",
          padding: "96px 28px",
        }}
      >
        <header
          style={{
            display: "flex",
            alignItems: "center",
            justifyContent: "space-between",
            marginBottom: 64,
          }}
        >
          <span
            style={{
              fontFamily: "var(--font-display)",
              fontWeight: 600,
              fontSize: 19,
              letterSpacing: "-.02em",
              display: "flex",
              alignItems: "center",
              gap: 9,
            }}
          >
            <span
              style={{
                width: 9,
                height: 9,
                borderRadius: "50%",
                background: "var(--accent)",
                boxShadow: "0 0 0 4px var(--accent-soft)",
              }}
            />
            DeCloud
          </span>
          <button
            onClick={toggle}
            style={{
              fontFamily: "var(--font-mono)",
              fontSize: 13,
              padding: "8px 14px",
              borderRadius: "var(--radius)",
              border: "1px solid var(--border-strong)",
              background: "transparent",
              color: "var(--text-primary)",
              cursor: "pointer",
            }}
          >
            {theme === "dark" ? "◑ Light" : "◐ Dark"}
          </button>
        </header>

        <p
          style={{
            fontFamily: "var(--font-mono)",
            fontSize: 12.5,
            letterSpacing: ".14em",
            textTransform: "uppercase",
            color: "var(--text-accent)",
            marginBottom: 20,
          }}
        >
          Phase 0 · /app is live
        </p>
        <h1
          style={{
            fontFamily: "var(--font-display)",
            fontWeight: 600,
            fontSize: "clamp(40px,6.4vw,72px)",
            lineHeight: 1.02,
            letterSpacing: "-.035em",
          }}
        >
          Compute that answers to{" "}
          <span style={{ color: "var(--accent)" }}>no one.</span>
        </h1>
        <p
          style={{
            fontSize: 18,
            color: "var(--text-secondary)",
            maxWidth: 500,
            marginTop: 24,
          }}
        >
          The new app scaffold is running — React + TypeScript + Vite, served under
          /app, styled entirely from Meridian tokens, in {theme} mode. The shell and
          the real routes come next.
        </p>
      </div>
    </main>
  );
}

function Mesh() {
  return (
    <svg
      viewBox="0 0 1120 620"
      preserveAspectRatio="xMidYMid slice"
      aria-hidden="true"
      style={{ position: "absolute", inset: 0, width: "100%", height: "100%", opacity: 0.5 }}
    >
      <g stroke="var(--mesh-line)" strokeWidth={1} fill="none">
        <path d="M60 120 L300 240 L180 430 M300 240 L560 160 L820 300 M560 160 L640 420 L820 300 L1040 210 M640 420 L900 520 M180 430 L420 540 L640 420 M820 300 L1000 470" />
      </g>
      <g fill="var(--mesh-node)">
        {[
          [60, 120], [300, 240], [180, 430], [560, 160], [820, 300],
          [640, 420], [1040, 210], [900, 520], [420, 540], [1000, 470],
        ].map(([cx, cy], i) => (
          <circle key={i} cx={cx} cy={cy} r={3} />
        ))}
      </g>
      <g fill="var(--mesh-node-accent)">
        <circle cx={560} cy={160} r={5} />
        <circle cx={640} cy={420} r={5} />
      </g>
    </svg>
  );
}
