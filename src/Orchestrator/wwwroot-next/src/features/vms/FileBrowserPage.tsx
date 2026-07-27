import { useEffect, useRef, useState, type CSSProperties } from "react";
import { useParams } from "react-router-dom";
import { tokenStore } from "../../auth/tokenStore";

// Phase 3 tail · full-page in-app file browser (opened in a new tab from the VM
// page), replacing the stranded legacy file-browser.html. Same transport as the
// terminal: a WebSocket through the orchestrator proxy — /api/sftp-proxy/{vmId}
// ?user=&password=&token= (JWT in the token query param; password path, the
// proven flow). Protocol is JSON messages: the server sends {type:"connected"},
// then we drive {type:"list"|"download"|"mkdir"|"delete"|"rename"}. Download is
// chunked (download_start → download_chunk (base64) → download_complete). Upload
// (the chunked upload_start/ready/chunk/complete handshake) is the next slice —
// proving the simpler ops on the wire first.

interface Entry { name: string; path: string; size: number; isDirectory: boolean }

const fmtSize = (n: number) => {
  if (n < 1024) return `${n} B`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`;
  return `${(n / 1024 ** 3).toFixed(1)} GB`;
};

const parentOf = (p: string) => {
  const trimmed = p.replace(/\/+$/, "");
  const i = trimmed.lastIndexOf("/");
  return i <= 0 ? "/" : trimmed.slice(0, i);
};

export function FileBrowserPage() {
  const { id = "" } = useParams();
  const [phase, setPhase] = useState<"form" | "live">("form");
  const [username, setUsername] = useState("root");
  const [password, setPassword] = useState("");
  const [status, setStatus] = useState("");
  const [path, setPathState] = useState("/root");
  const [files, setFiles] = useState<Entry[]>([]);

  const wsRef = useRef<WebSocket | null>(null);
  const pathRef = useRef("/root");                                   // live path for the once-bound onmessage
  const dlRef = useRef<Map<string, { chunks: string[]; fileName?: string }>>(new Map());
  const pendingRef = useRef<Map<string, File>>(new Map());              // dest path → File awaiting upload_ready
  const sessionsRef = useRef<Map<string, { name: string; size: number }>>(new Map());
  const fileInputRef = useRef<HTMLInputElement>(null);

  const setPath = (p: string) => { pathRef.current = p; setPathState(p); };
  const send = (cmd: Record<string, unknown>) => {
    const ws = wsRef.current;
    if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(cmd));
  };
  const list = (p: string) => send({ type: "list", path: p });

  useEffect(() => {
    if (phase !== "live") return;
    const token = tokenStore.get();
    if (!token) { setStatus("Not authenticated — sign in again."); return; }

    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = `${proto}//${window.location.host}/api/sftp-proxy/${id}`
      + `?user=${encodeURIComponent(username)}`
      + `&password=${encodeURIComponent(password)}`
      + `&token=${encodeURIComponent(token)}`;
    const ws = new WebSocket(url);
    wsRef.current = ws;
    setStatus("connecting…");

    // Bound once — must read pathRef (not the stale `path` closure).
    const handle = (r: Record<string, unknown>) => {
      switch (r.type) {
        case "connected":
          setStatus("connected");
          list(pathRef.current);
          break;
        case "list":
          if (r.success) { setPath(r.path as string); setFiles((r.files as Entry[]) ?? []); }
          else setStatus((r.message as string) || "Failed to list directory");
          break;
        case "download_start":
          dlRef.current.set(r.path as string, { chunks: [] });
          setStatus(`downloading ${r.path}…`);
          break;
        case "download_chunk": {
          const t = dlRef.current.get(r.path as string);
          if (t) t.chunks.push(r.chunkData as string);
          break;
        }
        case "download_complete": {
          const t = dlRef.current.get(r.path as string);
          if (t) { saveBlob(t.chunks, (r.fileName as string) || "download"); dlRef.current.delete(r.path as string); }
          setStatus("connected");
          break;
        }
        case "upload_ready": {
          const file = pendingRef.current.get(r.path as string);
          if (file) {
            pendingRef.current.delete(r.path as string);
            sessionsRef.current.set(r.sessionId as string, { name: file.name, size: file.size });
            sendChunks(file, r.sessionId as string);
          }
          break;
        }
        case "upload_progress": {
          const s = sessionsRef.current.get(r.sessionId as string);
          if (s && s.size) setStatus(`uploading ${s.name} — ${Math.round(((r.bytesReceived as number) / s.size) * 100)}%`);
          break;
        }
        case "upload_complete":
          sessionsRef.current.delete(r.sessionId as string);
          setStatus("connected");
          list(pathRef.current);
          break;
        case "mkdir": case "delete": case "rename":
          if (r.success) list(pathRef.current);
          else setStatus((r.message as string) || `${r.type} failed`);
          break;
      }
    };

    ws.onopen = () => setStatus("connecting…");   // real "connected" arrives as a message
    ws.onmessage = (e) => { try { handle(JSON.parse(e.data)); } catch { /* ignore non-JSON */ } };
    ws.onclose = (e) => setStatus(`disconnected (code ${e.code})`);
    ws.onerror = () => setStatus("connection error");

    return () => { ws.close(); wsRef.current = null; };
  }, [phase, id, username, password]);

  function saveBlob(chunksB64: string[], fileName: string) {
    const parts = chunksB64.map((c) => {
      const bin = atob(c);
      const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      return bytes;
    });
    const url = URL.createObjectURL(new Blob(parts));
    const a = document.createElement("a");
    a.href = url; a.download = fileName;
    document.body.appendChild(a); a.click(); document.body.removeChild(a);
    URL.revokeObjectURL(url);
  }

  const join = (dir: string, name: string) => `${dir}/${name}`.replace(/\/+/g, "/");

  function abToB64(buf: ArrayBuffer): string {
    const bytes = new Uint8Array(buf);
    let bin = "";
    for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }
  function startUpload(file: File) {
    if (file.size > 500 * 1024 * 1024) { setStatus(`too large: ${file.name} (max 500 MB)`); return; }
    const dest = join(path, file.name);
    pendingRef.current.set(dest, file);
    setStatus(`uploading ${file.name}…`);
    send({ type: "upload_start", path: dest, fileSize: file.size });
  }
  // Read the file in 64 KB slices and stream them as base64 upload_chunk frames,
  // then upload_complete. setTimeout(…,0) between reads keeps the UI responsive.
  function sendChunks(file: File, sessionId: string) {
    const chunkSize = 64 * 1024;
    let offset = 0;
    const reader = new FileReader();
    const next = () => reader.readAsArrayBuffer(file.slice(offset, offset + chunkSize));
    reader.onload = (e) => {
      const buf = e.target!.result as ArrayBuffer;
      send({ type: "upload_chunk", sessionId, chunkData: abToB64(buf) });
      offset += buf.byteLength;
      if (offset < file.size) setTimeout(next, 0);
      else send({ type: "upload_complete", sessionId });
    };
    reader.onerror = () => setStatus(`upload failed: ${file.name}`);
    next();
  }

  function open(entry: Entry) {
    if (entry.isDirectory) list(entry.path);
    else send({ type: "download", path: entry.path });
  }
  function makeDir() {
    const name = window.prompt("New folder name")?.trim();
    if (name && !name.includes("/")) send({ type: "mkdir", path: join(path, name) });
  }
  function remove(entry: Entry) {
    if (window.confirm(`Delete ${entry.name}? This can't be undone.`)) send({ type: "delete", path: entry.path });
  }
  function rename(entry: Entry) {
    const name = window.prompt("Rename to", entry.name)?.trim();
    if (name && !name.includes("/")) send({ type: "rename", path: entry.path, newPath: join(parentOf(entry.path), name) });
  }

  const sorted = [...files].sort((a, b) =>
    a.isDirectory === b.isDirectory ? a.name.localeCompare(b.name) : a.isDirectory ? -1 : 1);

  const cell: CSSProperties = { padding: "6px 10px", borderBottom: "1px solid var(--border-subtle)", fontSize: "var(--text-sm)" };

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh", padding: "var(--space-3)", gap: "var(--space-2)", boxSizing: "border-box" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1 style={{ margin: 0, fontSize: "var(--text-lg)" }}>Files</h1>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", fontFamily: "var(--font-mono)" }}>{status}</span>
      </div>

      {phase === "form" ? (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)", maxWidth: 420 }}>
          <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
            Connect over SFTP (password auth). Reveal the VM password on the VM page if you need it.
          </p>
          <label className="field"><span>Username</span><input value={username} onChange={(e) => setUsername(e.target.value)} /></label>
          <label className="field"><span>Password</span>
            <input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
              onKeyDown={(e) => { if (e.key === "Enter" && password) setPhase("live"); }} />
          </label>
          <button className="btn-primary" disabled={!password} onClick={() => setPhase("live")} style={{ alignSelf: "start" }}>Connect</button>
        </div>
      ) : (
        <>
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <button className="btn-ghost" onClick={() => list(parentOf(path))} disabled={path === "/"}>↑ Up</button>
            <button className="btn-ghost" onClick={() => list(path)}>↻ Refresh</button>
            <button className="btn-ghost" onClick={makeDir}>+ New folder</button>
            <button className="btn-ghost" onClick={() => fileInputRef.current?.click()}>⬆ Upload</button>
            <input ref={fileInputRef} type="file" multiple style={{ display: "none" }}
              onChange={(e) => { for (const f of Array.from(e.target.files ?? [])) startUpload(f); e.target.value = ""; }} />
            <code style={{ fontFamily: "var(--font-mono)", fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>{path}</code>
          </div>
          <div
            onDragOver={(e) => e.preventDefault()}
            onDrop={(e) => { e.preventDefault(); for (const f of Array.from(e.dataTransfer.files)) startUpload(f); }}
            style={{ flex: 1, minHeight: 0, overflow: "auto", border: "1px solid var(--border)", borderRadius: "var(--radius-sm)" }}
          >
            <table style={{ width: "100%", borderCollapse: "collapse" }}>
              <tbody>
                {sorted.map((entry) => (
                  <tr key={entry.path}>
                    <td style={{ ...cell, cursor: "pointer" }} onClick={() => open(entry)}>
                      {entry.isDirectory ? "📁" : "📄"}&nbsp; {entry.name}
                    </td>
                    <td style={{ ...cell, textAlign: "right", color: "var(--text-secondary)", fontFamily: "var(--font-mono)" }}>
                      {entry.isDirectory ? "" : fmtSize(entry.size)}
                    </td>
                    <td style={{ ...cell, textAlign: "right", whiteSpace: "nowrap" }}>
                      {!entry.isDirectory && <button className="btn-ghost" style={{ fontSize: "var(--text-xs)" }} onClick={() => open(entry)}>Download</button>}
                      <button className="btn-ghost" style={{ fontSize: "var(--text-xs)" }} onClick={() => rename(entry)}>Rename</button>
                      <button className="btn-ghost" style={{ fontSize: "var(--text-xs)", color: "var(--danger)" }} onClick={() => remove(entry)}>Delete</button>
                    </td>
                  </tr>
                ))}
                {sorted.length === 0 && (
                  <tr><td style={{ ...cell, color: "var(--text-secondary)" }} colSpan={3}>Empty directory.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  );
}
