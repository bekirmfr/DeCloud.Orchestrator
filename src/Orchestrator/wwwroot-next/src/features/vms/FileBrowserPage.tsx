import { useEffect, useRef, useState, type CSSProperties } from "react";
import { useParams } from "react-router-dom";
import { tokenStore } from "../../auth/tokenStore";

// Phase 3 tail · full-page in-app file browser (new tab), a faithful rebuild of
// the legacy file-browser.html: toolbar + breadcrumb, a columned file list with
// single-click select / double-click open / right-click menu, a toggleable
// transfer panel with per-file progress (concurrent up+down), and modal dialogs.
// Transport: WebSocket through the orchestrator proxy —
// /api/sftp-proxy/{vmId}?user=&password=&token= (password path, proven). JSON
// protocol; download and upload are chunked (base64, 64 KB).

interface Entry { name: string; path: string; size: number; isDirectory: boolean; modified?: string | number; permissions?: string }
interface Transfer { id: string; type: "download" | "upload"; name: string; size: number; done: number; state: "active" | "done" | "error" }

const fmtSize = (n: number) => {
  if (!n && n !== 0) return "";
  if (n < 1024) return `${n} B`;
  if (n < 1024 ** 2) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 ** 3) return `${(n / 1024 ** 2).toFixed(1)} MB`;
  return `${(n / 1024 ** 3).toFixed(1)} GB`;
};
const fmtDate = (m?: string | number) => {
  if (!m) return "";
  const d = new Date(typeof m === "number" ? (m < 1e12 ? m * 1000 : m) : m);
  return isNaN(d.getTime()) ? String(m) : d.toLocaleString();
};
const parentOf = (p: string) => { const t = p.replace(/\/+$/, ""); const i = t.lastIndexOf("/"); return i <= 0 ? "/" : t.slice(0, i); };
const join = (dir: string, name: string) => `${dir}/${name}`.replace(/\/+/g, "/");
const rid = () => Math.random().toString(36).slice(2, 9);

export function FileBrowserPage() {
  const { id = "" } = useParams();
  const [phase, setPhase] = useState<"form" | "live">("form");
  const [username, setUsername] = useState("root");
  const [password, setPassword] = useState("");
  const [status, setStatus] = useState("");
  const [path, setPathState] = useState("/root");
  const [files, setFiles] = useState<Entry[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [ctx, setCtx] = useState<{ x: number; y: number; entry: Entry } | null>(null);
  const [transfers, setTransfers] = useState<Transfer[]>([]);
  const [showTransfers, setShowTransfers] = useState(false);
  const [modal, setModal] = useState<{ kind: "newfolder" | "rename" | "delete"; entry?: Entry; value: string } | null>(null);

  const wsRef = useRef<WebSocket | null>(null);
  const pathRef = useRef("/root");
  const dlRef = useRef<Map<string, { id: string; chunks: string[] }>>(new Map());
  const ulPendingRef = useRef<Map<string, { file: File; id: string }>>(new Map());
  const ulSessionRef = useRef<Map<string, string>>(new Map());       // sessionId → transfer id
  const fileInputRef = useRef<HTMLInputElement>(null);

  const setPath = (p: string) => { pathRef.current = p; setPathState(p); };
  const send = (cmd: Record<string, unknown>) => { const ws = wsRef.current; if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(cmd)); };
  const list = (p: string) => { setSelected(null); send({ type: "list", path: p }); };
  const patchTransfer = (tid: string, patch: Partial<Transfer>) => setTransfers((prev) => prev.map((t) => (t.id === tid ? { ...t, ...patch } : t)));

  useEffect(() => {
    if (phase !== "live") return;
    const token = tokenStore.get();
    if (!token) { setStatus("Not authenticated — sign in again."); return; }

    const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
    const url = `${proto}//${window.location.host}/api/sftp-proxy/${id}`
      + `?user=${encodeURIComponent(username)}&password=${encodeURIComponent(password)}&token=${encodeURIComponent(token)}`;
    const ws = new WebSocket(url);
    wsRef.current = ws;
    setStatus("connecting…");

    const handle = (r: Record<string, unknown>) => {
      const type = r.type as string;
      switch (type) {
        case "connected": setStatus("connected"); list(pathRef.current); break;
        case "list":
          if (r.success) { setPath(r.path as string); setFiles((r.files as Entry[]) ?? []); }
          else setStatus((r.message as string) || "Failed to list directory");
          break;
        case "download_start": { const d = dlRef.current.get(r.path as string); if (d) d.chunks = []; break; }
        case "download_chunk": {
          const d = dlRef.current.get(r.path as string);
          if (d) { d.chunks.push(r.chunkData as string); patchTransfer(d.id, { done: (r.bytesSent as number) ?? 0 }); }
          break;
        }
        case "download_complete": {
          const d = dlRef.current.get(r.path as string);
          if (d) { saveBlob(d.chunks, (r.fileName as string) || "download"); patchTransfer(d.id, { state: "done" }); dlRef.current.delete(r.path as string); }
          break;
        }
        case "upload_ready": {
          const pend = ulPendingRef.current.get(r.path as string);
          if (pend) { ulPendingRef.current.delete(r.path as string); ulSessionRef.current.set(r.sessionId as string, pend.id); sendChunks(pend.file, r.sessionId as string); }
          break;
        }
        case "upload_progress": { const tid = ulSessionRef.current.get(r.sessionId as string); if (tid) patchTransfer(tid, { done: (r.bytesReceived as number) ?? 0 }); break; }
        case "upload_complete": {
          const tid = ulSessionRef.current.get(r.sessionId as string);
          if (tid) patchTransfer(tid, { state: "done" });
          ulSessionRef.current.delete(r.sessionId as string);
          list(pathRef.current);
          break;
        }
        case "mkdir": case "delete": case "rename":
          if (r.success) list(pathRef.current); else setStatus((r.message as string) || `${type} failed`);
          break;
      }
    };

    ws.onmessage = (e) => { try { handle(JSON.parse(e.data)); } catch { /* ignore */ } };
    ws.onclose = (e) => setStatus(`disconnected (code ${e.code})`);
    ws.onerror = () => setStatus("connection error");
      return () => { ws.close(); wsRef.current = null; };
      // list/sendChunks are stable helpers driven by the once-bound socket; adding
      // them would reconnect on every render. Intentional fixed deps.
      // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [phase, id, username, password]);

  function saveBlob(chunksB64: string[], fileName: string) {
    const parts = chunksB64.map((c) => { const b = atob(c); const u = new Uint8Array(b.length); for (let i = 0; i < b.length; i++) u[i] = b.charCodeAt(i); return u; });
    const url = URL.createObjectURL(new Blob(parts));
    const a = document.createElement("a"); a.href = url; a.download = fileName;
    document.body.appendChild(a); a.click(); document.body.removeChild(a); URL.revokeObjectURL(url);
  }
  function abToB64(buf: ArrayBuffer): string {
    const bytes = new Uint8Array(buf); let bin = "";
    for (let i = 0; i < bytes.length; i += 0x8000) bin += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }
  function sendChunks(file: File, sessionId: string) {
    const chunkSize = 64 * 1024; let offset = 0; const reader = new FileReader();
    const next = () => reader.readAsArrayBuffer(file.slice(offset, offset + chunkSize));
    reader.onload = (e) => {
      const buf = e.target!.result as ArrayBuffer;
      send({ type: "upload_chunk", sessionId, chunkData: abToB64(buf) });
      offset += buf.byteLength;
      if (offset < file.size) setTimeout(next, 0); else send({ type: "upload_complete", sessionId });
    };
    reader.onerror = () => setStatus(`upload failed: ${file.name}`);
    next();
  }

  function download(entry: Entry) {
    const tid = rid();
    dlRef.current.set(entry.path, { id: tid, chunks: [] });
    setTransfers((p) => [...p, { id: tid, type: "download", name: entry.name, size: entry.size, done: 0, state: "active" }]);
    setShowTransfers(true);
    send({ type: "download", path: entry.path });
  }
  function startUpload(file: File) {
    if (file.size > 500 * 1024 * 1024) { setStatus(`too large: ${file.name} (max 500 MB)`); return; }
    const dest = join(path, file.name); const tid = rid();
    ulPendingRef.current.set(dest, { file, id: tid });
    setTransfers((p) => [...p, { id: tid, type: "upload", name: file.name, size: file.size, done: 0, state: "active" }]);
    setShowTransfers(true);
    send({ type: "upload_start", path: dest, fileSize: file.size });
  }
  function openItem(entry: Entry) { if (entry.isDirectory) list(entry.path); else download(entry); }
  function submitModal() {
    if (!modal) return;
    const name = modal.value.trim();
    if (modal.kind === "newfolder" && name && !name.includes("/")) send({ type: "mkdir", path: join(path, name) });
    else if (modal.kind === "rename" && modal.entry && name && !name.includes("/")) send({ type: "rename", path: modal.entry.path, newPath: join(parentOf(modal.entry.path), name) });
    else if (modal.kind === "delete" && modal.entry) send({ type: "delete", path: modal.entry.path });
    setModal(null);
  }

  const sorted = [...files].sort((a, b) => (a.isDirectory === b.isDirectory ? a.name.localeCompare(b.name) : a.isDirectory ? -1 : 1));
  const crumbs = path === "/" ? [""] : path.split("/");

  const tbtn: CSSProperties = { fontSize: "var(--text-sm)" };
  const col: CSSProperties = { padding: "6px 10px", fontSize: "var(--text-sm)", borderBottom: "1px solid var(--border-subtle)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" };
  const gridCols = "minmax(160px,1fr) 90px 160px 110px";

  return (
    <div style={{ display: "flex", flexDirection: "column", height: "100vh", padding: "var(--space-3)", gap: "var(--space-2)", boxSizing: "border-box" }}
      onClick={() => ctx && setCtx(null)}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
        <h1 style={{ margin: 0, fontSize: "var(--text-lg)" }}>Files</h1>
        <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", fontFamily: "var(--font-mono)" }}>{status}</span>
      </div>

      {phase === "form" ? (
        <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-2)", maxWidth: 420 }}>
          <p style={{ margin: 0, color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Connect over SFTP (password auth). Reveal the VM password on the VM page if you need it.</p>
          <label className="field"><span>Username</span><input value={username} onChange={(e) => setUsername(e.target.value)} /></label>
          <label className="field"><span>Password</span><input type="password" value={password} onChange={(e) => setPassword(e.target.value)} onKeyDown={(e) => { if (e.key === "Enter" && password) setPhase("live"); }} /></label>
          <button className="btn-primary" disabled={!password} onClick={() => setPhase("live")} style={{ alignSelf: "start" }}>Connect</button>
        </div>
      ) : (
        <>
          {/* Toolbar */}
          <div style={{ display: "flex", gap: 8, alignItems: "center", flexWrap: "wrap" }}>
            <button className="btn-ghost" style={tbtn} onClick={() => list(parentOf(path))} disabled={path === "/"}>↑ Up</button>
            <button className="btn-ghost" style={tbtn} onClick={() => list(path)}>↻ Refresh</button>
            <button className="btn-primary" style={tbtn} onClick={() => fileInputRef.current?.click()}>⬆ Upload</button>
            <input ref={fileInputRef} type="file" multiple style={{ display: "none" }}
              onChange={(e) => { for (const f of Array.from(e.target.files ?? [])) startUpload(f); e.target.value = ""; }} />
            <button className="btn-ghost" style={tbtn} onClick={() => setModal({ kind: "newfolder", value: "" })}>+ New folder</button>
            <button className="btn-ghost" style={tbtn} onClick={() => setShowTransfers((s) => !s)}>
              Transfers{transfers.some((t) => t.state === "active") ? ` (${transfers.filter((t) => t.state === "active").length})` : ""}
            </button>
          </div>

          {/* Breadcrumb */}
          <div style={{ display: "flex", gap: 4, alignItems: "center", flexWrap: "wrap", fontFamily: "var(--font-mono)", fontSize: "var(--text-sm)" }}>
            {crumbs.map((seg, i) => {
              const target = i === 0 ? "/" : crumbs.slice(0, i + 1).join("/");
              return (
                <span key={i}>
                  {i > 0 && <span style={{ color: "var(--text-tertiary)" }}> / </span>}
                  <button className="btn-ghost" style={{ fontSize: "var(--text-sm)", padding: "0 4px" }} onClick={() => list(target || "/")}>{seg || "root"}</button>
                </span>
              );
            })}
          </div>

          <div style={{ display: "flex", gap: "var(--space-2)", flex: 1, minHeight: 0 }}>
            {/* File list */}
            <div style={{ flex: 1, minHeight: 0, display: "flex", flexDirection: "column", border: "1px solid var(--border)", borderRadius: "var(--radius-sm)", overflow: "hidden" }}
              onDragOver={(e) => e.preventDefault()}
              onDrop={(e) => { e.preventDefault(); for (const f of Array.from(e.dataTransfer.files)) startUpload(f); }}>
              <div style={{ display: "grid", gridTemplateColumns: gridCols, background: "var(--surface-1)", color: "var(--text-secondary)", fontSize: "var(--text-xs)", fontWeight: "var(--fw-medium)" }}>
                <span style={{ padding: "6px 10px" }}>Name</span><span style={{ padding: "6px 10px", textAlign: "right" }}>Size</span><span style={{ padding: "6px 10px" }}>Modified</span><span style={{ padding: "6px 10px" }}>Perms</span>
              </div>
              <div style={{ overflow: "auto", flex: 1 }}>
                {sorted.map((entry) => (
                  <div key={entry.path}
                    style={{ display: "grid", gridTemplateColumns: gridCols, cursor: "pointer", background: selected === entry.path ? "var(--surface-2)" : undefined }}
                    onClick={() => setSelected(entry.path)}
                    onDoubleClick={() => openItem(entry)}
                    onContextMenu={(e) => { e.preventDefault(); setSelected(entry.path); setCtx({ x: e.clientX, y: e.clientY, entry }); }}>
                    <span style={{ ...col }}>{entry.isDirectory ? "📁" : "📄"}&nbsp; {entry.name}</span>
                    <span style={{ ...col, textAlign: "right", fontFamily: "var(--font-mono)", color: "var(--text-secondary)" }}>{entry.isDirectory ? "--" : fmtSize(entry.size)}</span>
                    <span style={{ ...col, color: "var(--text-secondary)" }}>{fmtDate(entry.modified)}</span>
                    <span style={{ ...col, fontFamily: "var(--font-mono)", color: "var(--text-tertiary)" }}>{entry.permissions ?? ""}</span>
                  </div>
                ))}
                {sorted.length === 0 && <div style={{ padding: "var(--space-4)", color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>This folder is empty.</div>}
              </div>
            </div>

            {/* Transfer panel */}
            {showTransfers && (
              <aside style={{ width: 280, border: "1px solid var(--border)", borderRadius: "var(--radius-sm)", display: "flex", flexDirection: "column", overflow: "hidden" }}>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", padding: "6px 10px", background: "var(--surface-1)", fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)" }}>
                  <span>Transfers</span>
                  <button className="btn-ghost" style={{ fontSize: "var(--text-xs)" }} onClick={() => setTransfers((p) => p.filter((t) => t.state === "active"))}>Clear done</button>
                </div>
                <div style={{ overflow: "auto", flex: 1, padding: "var(--space-2)", display: "flex", flexDirection: "column", gap: "var(--space-2)" }}>
                  {transfers.length === 0 && <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>No transfers.</span>}
                  {transfers.map((t) => {
                    const pct = t.state === "done" ? 100 : t.size > 0 ? Math.min(100, Math.round((t.done / t.size) * 100)) : 0;
                    return (
                      <div key={t.id} style={{ display: "flex", flexDirection: "column", gap: 3 }}>
                        <div style={{ display: "flex", justifyContent: "space-between", fontSize: "var(--text-xs)", gap: 6 }}>
                          <span style={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>{t.type === "upload" ? "⬆" : "⬇"} {t.name}</span>
                          <span style={{ color: "var(--text-secondary)", fontFamily: "var(--font-mono)" }}>{fmtSize(t.size)}</span>
                        </div>
                        <div style={{ height: 4, background: "var(--surface-2)", borderRadius: 2, overflow: "hidden" }}>
                          <div style={{ width: `${pct}%`, height: "100%", background: t.state === "error" ? "var(--danger)" : "var(--accent)" }} />
                        </div>
                        <span style={{ fontSize: "var(--text-xs)", color: "var(--text-secondary)" }}>{t.state === "done" ? "Done" : t.state === "error" ? "Failed" : `${pct}%`}</span>
                      </div>
                    );
                  })}
                </div>
              </aside>
            )}
          </div>
        </>
      )}

      {/* Context menu */}
      {ctx && (
        <div style={{ position: "fixed", left: ctx.x, top: ctx.y, zIndex: 50, background: "var(--surface-1)", border: "1px solid var(--border)", borderRadius: "var(--radius-sm)", boxShadow: "0 8px 24px rgba(0,0,0,0.4)", minWidth: 150, padding: 4 }}>
          {!ctx.entry.isDirectory && <MenuItem label="Download" onClick={() => { download(ctx.entry); setCtx(null); }} />}
          <MenuItem label="Rename" onClick={() => { setModal({ kind: "rename", entry: ctx.entry, value: ctx.entry.name }); setCtx(null); }} />
          <MenuItem label="Delete" danger onClick={() => { setModal({ kind: "delete", entry: ctx.entry, value: "" }); setCtx(null); }} />
        </div>
      )}

      {/* Modal */}
      {modal && (
        <div className="dialog-overlay" style={{ display: "grid", placeItems: "center", zIndex: 60 }} onClick={() => setModal(null)}>
          <div className="dialog-content" style={{ position: "static", transform: "none", maxWidth: 360 }} onClick={(e) => e.stopPropagation()}>
            <h2 style={{ fontFamily: "var(--font-display)", fontWeight: 600, marginTop: 0 }}>
              {modal.kind === "newfolder" ? "New folder" : modal.kind === "rename" ? "Rename" : "Delete"}
            </h2>
            {modal.kind === "delete" ? (
              <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Delete <strong>{modal.entry?.name}</strong>? This can’t be undone.</p>
            ) : (
              <input autoFocus value={modal.value} onChange={(e) => setModal({ ...modal, value: e.target.value })}
                onKeyDown={(e) => { if (e.key === "Enter") submitModal(); }} style={{ width: "100%" }} />
            )}
            <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: "var(--space-3)" }}>
              <button className="btn-ghost" onClick={() => setModal(null)}>Cancel</button>
              <button className="btn-primary" style={modal.kind === "delete" ? { background: "var(--danger)" } : undefined} onClick={submitModal}>
                {modal.kind === "delete" ? "Delete" : "OK"}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function MenuItem({ label, onClick, danger }: { label: string; onClick: () => void; danger?: boolean }) {
  return (
    <button className="btn-ghost" onClick={onClick}
      style={{ display: "block", width: "100%", textAlign: "left", fontSize: "var(--text-sm)", color: danger ? "var(--danger)" : undefined, padding: "6px 10px" }}>
      {label}
    </button>
  );
}
