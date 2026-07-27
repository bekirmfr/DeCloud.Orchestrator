import { useEffect, useRef, useState } from "react";
import { useParams } from "react-router-dom";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { tokenStore } from "../../auth/tokenStore";
import "@xterm/xterm/css/xterm.css";

// Phase 3 tail · full-page in-app terminal (opened in a new tab from the VM
// page). PASSWORD path through the orchestrator proxy — the SAME proven flow as
// the legacy terminal.html: /api/terminal-proxy/{vmId}?user=&password=&token=,
// with no /access call and no ephemeral key. The JWT rides in the `token` query
// param (a WebSocket can't send an auth header). Keystrokes are raw frames,
// resize is a {type:"resize",cols,rows} JSON frame, and the node may send text
// OR Blob frames (handle both). Reveal the VM password on the VM page if needed.

export function TerminalPage() {
  const { id = "" } = useParams();
  const [phase, setPhase] = useState<"form" | "live">("form");
  const [username, setUsername] = useState("root");
  const [password, setPassword] = useState("");
  const [status, setStatus] = useState("");
  const containerRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (phase !== "live" || !containerRef.current) return;

    const token = tokenStore.get();
    if (!token) { setStatus("Not authenticated — sign in again."); return; }

    const term = new Terminal({
      cursorBlink: true,
      fontFamily: '"JetBrains Mono", ui-monospace, monospace',
      fontSize: 13,
      theme: { background: "#0b0f14", foreground: "#e6e6e6" },
    });
    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(containerRef.current);
    fit.fit();

    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = `${proto}//${window.location.host}/api/terminal-proxy/${id}`
      + `?user=${encodeURIComponent(username)}`
      + `&password=${encodeURIComponent(password)}`
      + `&token=${encodeURIComponent(token)}`;

    const ws = new WebSocket(url);
    setStatus("connecting…");

    const sendResize = () => {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
      }
    };
    ws.onopen = () => { setStatus("connected"); fit.fit(); sendResize(); term.focus(); };
    ws.onmessage = (e) => {
      if (e.data instanceof Blob) e.data.text().then((t) => term.write(t));
      else term.write(e.data as string);
    };
    ws.onclose = (e) => setStatus(`disconnected (code ${e.code})`);
    ws.onerror = () => setStatus("connection error");

    const dataSub = term.onData((d) => { if (ws.readyState === WebSocket.OPEN) ws.send(d); });
    const onWinResize = () => { fit.fit(); sendResize(); };
    window.addEventListener("resize", onWinResize);

    return () => {
      window.removeEventListener("resize", onWinResize);
      dataSub.dispose();
      ws.close();
      term.dispose();
    };
  }, [phase, id, username, password]);

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh", padding: "var(--space-3)", gap: "var(--space-2)", boxSizing: "border-box" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1 style={{ margin: 0, fontSize: "var(--text-lg)" }}>Terminal</h1>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", fontFamily: "var(--font-mono)" }}>{status}</span>
      </div>

      {phase === "form" ? (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)", maxWidth: 420 }}>
          <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
            Connect over SSH (password auth). Reveal the VM password on the VM page if you need it.
          </p>
          <label className="field">
            <span>Username</span>
            <input value={username} onChange={(e) => setUsername(e.target.value)} />
          </label>
          <label className="field">
            <span>Password</span>
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && password) setPhase("live"); }} />
          </label>
          <button className="btn-primary" disabled={!password} onClick={() => setPhase("live")} style={{ alignSelf: "start" }}>
            Connect
          </button>
        </div>
      ) : (
        <div ref={containerRef} style={{ flex: 1, minHeight: 0, background: "#0b0f14", borderRadius: "var(--radius-sm)", padding: 8 }} />
      )}
    </div>
  );
}
