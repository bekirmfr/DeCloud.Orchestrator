import { useEffect, useRef, useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useParams, useNavigate } from "react-router-dom";
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { tokenStore } from "../../auth/tokenStore";
import "@xterm/xterm/css/xterm.css";

// Phase 3 tail · in-app terminal (modal-route /vms/:id/terminal), replacing the
// stranded legacy terminal.html. Connects through the orchestrator PROXY
// (/api/terminal-proxy/{vmId}) — NOT the node-direct WebSocketUrl the access
// endpoint returns, which a browser behind CGNAT can't reach. Proven password
// path (matches the working legacy client): the JWT rides in the `token` query
// param (a WebSocket can't send an Authorization header); keystrokes are raw
// frames, resize is a {type:"resize",cols,rows} JSON frame.

export function TerminalModal() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const close = () => navigate(`/vms/${id}`);

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
    ws.binaryType = "arraybuffer";
    setStatus("connecting…");

    const sendResize = () => {
      if (ws.readyState === WebSocket.OPEN) {
        ws.send(JSON.stringify({ type: "resize", cols: term.cols, rows: term.rows }));
      }
    };

    ws.onopen = () => { setStatus("connected"); sendResize(); term.focus(); };
    ws.onmessage = (e) => term.write(typeof e.data === "string" ? e.data : new Uint8Array(e.data));
    ws.onclose = () => setStatus("disconnected");
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
    <Dialog.Root open onOpenChange={(o) => !o && close()}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content className="dialog-content" style={{ maxWidth: 900, width: "92vw" }}>
          <Dialog.Title style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>
            Terminal
          </Dialog.Title>

          {phase === "form" ? (
            <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)", marginTop: "var(--space-2)" }}>
              <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
                Connect over SSH through the orchestrator. Password authentication.
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
              <div style={{ display: "flex", gap: "var(--space-2)" }}>
                <button className="btn-primary" disabled={!password} onClick={() => setPhase("live")}>Connect</button>
                <button className="btn-ghost" onClick={close}>Cancel</button>
              </div>
            </div>
          ) : (
            <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-1)", marginTop: "var(--space-2)" }}>
              <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center" }}>
                <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>{status}</span>
                <button className="btn-ghost" style={{ fontSize: "var(--text-xs)" }} onClick={() => setPhase("form")}>Disconnect</button>
              </div>
              <div ref={containerRef} style={{ height: "60vh", width: "100%", background: "#0b0f14", borderRadius: "var(--radius-sm)", padding: 6 }} />
            </div>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
