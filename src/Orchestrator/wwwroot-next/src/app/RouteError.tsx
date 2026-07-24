import { Link, isRouteErrorResponse, useRouteError } from "react-router-dom";

// Rendered by React Router when a route throws or no route matches. Without an
// errorElement the router falls back to its own developer page — a raw stack
// trace and "Hey developer 👋" — which is what production showed when the
// DeployPage hooks crash (React #310) landed. Neither tsc nor vitest sees this
// class of failure, so the boundary is the difference between a legible message
// and a white screen with a minified stack.
//
// Deliberately NOT wired to a reporting service: there isn't one yet, and a
// silent console.error is closer to honest than a pretend integration.

function Frame({ title, detail, children }: { title: string; detail?: string; children?: React.ReactNode }) {
  return (
    <div
      role="alert"
      style={{
        minHeight: "60vh",
        display: "flex",
        flexDirection: "column",
        alignItems: "center",
        justifyContent: "center",
        gap: "var(--space-4)",
        padding: "var(--space-8)",
        textAlign: "center",
      }}
    >
      <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600, fontSize: "var(--text-2xl)" }}>
        {title}
      </h1>
      {detail && (
        <p style={{ color: "var(--text-secondary)", maxWidth: 480 }}>{detail}</p>
      )}
      {children}
      <div style={{ display: "flex", gap: "var(--space-3)", marginTop: "var(--space-2)" }}>
        <Link className="btn-primary" to="/">Back to overview</Link>
        <button className="btn-ghost" onClick={() => window.location.reload()}>Reload</button>
      </div>
    </div>
  );
}

export function RouteError() {
  const error = useRouteError();

  // A 404 here means the URL matched no route — a stale link or a typo, not a
  // fault. Separated because "we couldn't find that page" and "something broke"
  // call for different words and different expectations.
  if (isRouteErrorResponse(error) && error.status === 404) {
    return (
      <Frame
        title="Page not found"
        detail="That address doesn't match anything in the app. It may be an old link, or a page that has since moved."
      />
    );
  }

  if (isRouteErrorResponse(error)) {
    return (
      <Frame
        title={`${error.status} ${error.statusText}`}
        detail={typeof error.data === "string" ? error.data : undefined}
      />
    );
  }

  // A genuine thrown error. The message is shown because this is an operator-
  // facing tool and a bare "something went wrong" wastes the one clue the user
  // could paste into a bug report. Stack traces stay in the console.
  const message = error instanceof Error ? error.message : String(error ?? "Unknown error");
  if (error) console.error("[route error]", error);

  return (
    <Frame
      title="Something went wrong"
      detail="This page failed to render. The details below may help if you report it."
    >
      <code
        style={{
          display: "block",
          maxWidth: 560,
          padding: "var(--space-3)",
          background: "var(--surface-1)",
          border: "1px solid var(--border-subtle)",
          borderRadius: "var(--radius)",
          fontFamily: "var(--font-mono)",
          fontSize: "var(--text-sm)",
          color: "var(--text-secondary)",
          textAlign: "left",
          wordBreak: "break-word",
        }}
      >
        {message}
      </code>
    </Frame>
  );
}
