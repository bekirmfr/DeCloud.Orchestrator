import { type CSSProperties } from "react";
import { Link } from "react-router-dom";
import { useTheme, type ThemeChoice } from "./useTheme";

/**
 * Settings — the last supporting page (Phase 5).
 *
 * Deliberately thin, and honestly so:
 *  - Appearance (theme) is the one genuinely functional control; the token
 *    layer already supports light/dark and the pre-paint script reads the
 *    choice, so this just writes it.
 *  - Account / SSH keys / Profile are navigation to surfaces that already exist.
 *  - Language is intentionally NOT a live selector: the app has no i18n layer
 *    yet, so a language picker would be UI for data that doesn't exist (same
 *    call as the deferred deploy "template Variables"). Shown as coming-with-
 *    localization rather than faked.
 */

const THEME_OPTIONS: { value: ThemeChoice; label: string; hint: string }[] = [
  { value: "light", label: "Light", hint: "Always light" },
  { value: "dark", label: "Dark", hint: "Always dark" },
  { value: "system", label: "System", hint: "Match your OS" },
];

const page: CSSProperties = {
  maxWidth: 720,
  margin: "0 auto",
  padding: "var(--space-4)",
  display: "flex",
  flexDirection: "column",
  gap: "var(--space-4)",
};

const h1: CSSProperties = {
  fontFamily: "var(--font-display)",
  fontSize: "var(--text-xl)",
  color: "var(--text-primary)",
  margin: 0,
};

const card: CSSProperties = {
  background: "var(--surface-2)",
  border: "1px solid var(--border-subtle)",
  borderRadius: "var(--radius-card)",
  padding: "var(--space-4)",
  display: "flex",
  flexDirection: "column",
  gap: "var(--space-3)",
};

const sectionLabel: CSSProperties = {
  fontSize: "var(--text-sm)",
  fontWeight: "var(--fw-medium)" as CSSProperties["fontWeight"],
  color: "var(--text-secondary)",
  textTransform: "uppercase",
  letterSpacing: "0.04em",
};

const rowLink: CSSProperties = {
  display: "flex",
  justifyContent: "space-between",
  alignItems: "center",
  padding: "var(--space-3) 0",
  color: "var(--text-primary)",
  textDecoration: "none",
  borderTop: "1px solid var(--border-subtle)",
  fontSize: "var(--text-md)",
};

const muted: CSSProperties = {
  fontSize: "var(--text-sm)",
  color: "var(--text-tertiary)",
};

export function SettingsPage() {
  const { theme, setTheme } = useTheme();

  return (
    <div style={page}>
      <h1 style={h1}>Settings</h1>

      {/* Appearance — the one live control */}
      <section style={card}>
        <span style={sectionLabel}>Appearance</span>
        <div
          role="radiogroup"
          aria-label="Theme"
          style={{ display: "flex", gap: "var(--space-2)", flexWrap: "wrap" }}
        >
          {THEME_OPTIONS.map((opt) => {
            const active = theme === opt.value;
            return (
              <button
                key={opt.value}
                type="button"
                role="radio"
                aria-checked={active}
                onClick={() => setTheme(opt.value)}
                style={{
                  flex: "1 1 120px",
                  cursor: "pointer",
                  padding: "var(--space-3)",
                  borderRadius: "var(--radius)",
                  border: active
                    ? "1px solid var(--accent)"
                    : "1px solid var(--border)",
                  background: active ? "var(--surface-1)" : "transparent",
                  color: active ? "var(--text-primary)" : "var(--text-secondary)",
                  textAlign: "left",
                  display: "flex",
                  flexDirection: "column",
                  gap: 4,
                }}
              >
                <span
                  style={{
                    fontSize: "var(--text-md)",
                    fontWeight: "var(--fw-medium)" as CSSProperties["fontWeight"],
                  }}
                >
                  {opt.label}
                </span>
                <span style={muted}>{opt.hint}</span>
              </button>
            );
          })}
        </div>
      </section>

      {/* Account — navigation to existing surfaces */}
      <section style={card}>
        <span style={sectionLabel}>Account</span>
        <Link to="/profile" style={{ ...rowLink, borderTop: "none" }}>
          <span>Profile &amp; quotas</span>
          <span style={muted}>View →</span>
        </Link>
        <Link to="/settings/ssh-keys" style={rowLink}>
          <span>SSH keys</span>
          <span style={muted}>Manage →</span>
        </Link>
      </section>

      {/* Language — deferred until an i18n layer exists (not a fake selector) */}
      <section style={card}>
        <span style={sectionLabel}>Language</span>
        <p style={{ ...muted, margin: 0 }}>
          English. Additional languages arrive with localization.
        </p>
      </section>

      {/* Legal */}
      <section style={card}>
        <span style={sectionLabel}>Legal</span>
        <a
          href="/tos.html"
          target="_blank"
          rel="noopener noreferrer"
          style={{ ...rowLink, borderTop: "none" }}
        >
          <span>Terms of Service</span>
          <span style={muted}>Open ↗</span>
        </a>
      </section>
    </div>
  );
}
