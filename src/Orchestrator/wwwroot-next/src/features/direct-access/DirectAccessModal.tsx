import { useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useParams, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import type { AppError } from "../../api/errors";
import { normalizeProtocol, PROTOCOLS, type PortProtocol } from "./portProtocol";
import {
  useDirectAccessInfo,
  useAvailableServices,
  useAllocatePort,
  useQuickAddService,
  useRemovePort,
  type PortMappingInfo,
} from "./useDirectAccess";

// Modal-ROUTE at /app/vms/:id/ports (DESIGN §3 route tree + §7 Operate parity:
// "direct-access ports (quick-add + custom)"). The Dialog is open whenever the
// route matches; closing navigates back to the detail page (URL survives reload,
// back button closes — the modal-vs-route rule).
//
// "Direct access" = Smart Port Allocation (VmDirectAccess): a public hostname on
// *.direct.stackfi.tech plus TCP/UDP port mappings from the 40000–65535 pool to
// ports inside the VM. This is the surface that opens 25565 for a game server,
// 3306 for a database, etc. It is NOT the SSH/VNC "Connection" panel on the
// detail page (that's VmAccessInfo — a different, unrelated feature).

// Small parity nicety: the legacy modal showed an icon per quick-add service.
// Not load-bearing; unknown services fall back to no icon.
const SERVICE_ICON: Record<string, string> = {
  ssh: "🔐", rdp: "🖥️", mysql: "🗄️", postgresql: "🐘", mongodb: "🍃", redis: "⚡",
  http: "🌐", https: "🔒", minecraft: "⛏️", valheim: "🗡️", wireguard: "🔐",
  openvpn: "🔐", shadowsocks: "🕶️", teamspeak: "🎙️", mumble: "🎙️", ftp: "📁",
  sftp: "📁", smtp: "✉️", imap: "✉️", pop3: "✉️",
};

function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className="btn-ghost"
      style={{ padding: "2px 8px", fontSize: 12 }}
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(text);
          setCopied(true);
          setTimeout(() => setCopied(false), 1200);
        } catch {
          /* clipboard blocked — no-op, the value is still on screen */
        }
      }}
    >
      {copied ? "Copied" : label}
    </button>
  );
}

function PortRow({
  m,
  onRemove,
  removing,
}: {
  m: PortMappingInfo;
  onRemove: () => void;
  removing: boolean;
}) {
  return (
    <div
      style={{
        display: "grid",
        gridTemplateColumns: "minmax(70px, 1fr) auto auto",
        gap: 12,
        alignItems: "center",
        padding: "8px 0",
        borderTop: "1px solid var(--border-subtle)",
      }}
    >
      <div style={{ minWidth: 0 }}>
        <div style={{ fontWeight: "var(--fw-medium)", fontSize: "var(--text-sm)" }}>
          {m.label || `Port ${m.vmPort}`}
        </div>
        <code style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--text-secondary)" }}>
          {m.vmPort} → {m.publicPort} · {normalizeProtocol(m.protocol)}
        </code>
        <div
          style={{
            fontFamily: "var(--font-mono)",
            fontSize: 12,
            color: "var(--text-tertiary)",
            whiteSpace: "nowrap",
            overflow: "hidden",
            textOverflow: "ellipsis",
            marginTop: 2,
          }}
          title={m.connectionExample}
        >
          {m.connectionExample}
        </div>
      </div>
      <CopyButton text={m.connectionExample} />
      <button
        type="button"
        className="btn-ghost"
        style={{ padding: "2px 8px", fontSize: 12, color: "var(--danger)", borderColor: "var(--danger)" }}
        onClick={onRemove}
        disabled={removing}
      >
        Remove
      </button>
    </div>
  );
}

export function DirectAccessModal() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const { api } = useAuth();

  const info = useDirectAccessInfo(api, id);
  const services = useAvailableServices(api, id);
  const allocate = useAllocatePort(api, id);
  const quickAdd = useQuickAddService(api, id);
  const remove = useRemovePort(api, id);

  const [vmPort, setVmPort] = useState("");
  const [protocol, setProtocol] = useState<PortProtocol>("TCP");
  const [label, setLabel] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  const close = () => navigate(`/vms/${id}`);

  const mutationError =
    (allocate.error || quickAdd.error || remove.error) as AppError | null;

  async function openCustomPort() {
    setFormError(null);
    const port = Number(vmPort);
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
      setFormError("Enter a port between 1 and 65535.");
      return;
    }
    try {
      await allocate.mutateAsync({ vmPort: port, protocol, label: label.trim() || null });
      setVmPort("");
      setLabel("");
      setProtocol("TCP");
    } catch {
      /* surfaced via mutationError below */
    }
  }

  const mappings = info.data?.portMappings ?? [];
  const busy = allocate.isPending || quickAdd.isPending;

  return (
    <Dialog.Root open onOpenChange={(o) => !o && close()}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content
          className="dialog-content"
          aria-describedby={undefined}
          style={{ width: "min(680px, calc(100vw - 32px))", maxHeight: "calc(100vh - 48px)", overflowY: "auto" }}
        >
          <Dialog.Title style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>
            Ports &amp; direct access
          </Dialog.Title>

          {info.data?.dnsName ? (
            <div style={{ display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
              <span style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>Public hostname</span>
              <code style={{ fontFamily: "var(--font-mono)", fontSize: 12.5 }}>{info.data.dnsName}</code>
              <CopyButton text={info.data.dnsName} />
            </div>
          ) : (
            <p style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
              Open a port to reach a service inside this VM directly over the internet — a game
              server, database, or anything else. A public hostname is assigned automatically.
            </p>
          )}

          {info.isLoading ? (
            <p style={{ color: "var(--text-secondary)" }}>Loading…</p>
          ) : info.error ? (
            <p role="alert" style={{ color: "var(--danger)", fontSize: "var(--text-sm)" }}>
              {(info.error as AppError)?.message ?? "Couldn't load port mappings."}
            </p>
          ) : mappings.length === 0 ? (
            <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
              No ports open yet. Pick a service below or add a custom port.
            </p>
          ) : (
            <div>
              {mappings.map((m) => (
                <PortRow
                  key={m.id}
                  m={m}
                  removing={remove.isPending}
                  onRemove={() => remove.mutate(m.vmPort)}
                />
              ))}
            </div>
          )}

          {/* Quick-add catalogue (from GET .../services — the platform owns this list) */}
          {services.data && services.data.length > 0 && (
            <div>
              <div style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)", margin: "8px 0 6px" }}>
                Quick add
              </div>
              <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
                {services.data.map((s) => (
                  <button
                    key={s.name}
                    type="button"
                    className="btn-ghost"
                    style={{ padding: "4px 10px", fontSize: 12 }}
                    title={`${s.label} · port ${s.port} · ${normalizeProtocol(s.protocol)}`}
                    onClick={() => quickAdd.mutate(s.name)}
                    disabled={busy}
                  >
                    <span aria-hidden style={{ marginRight: 4 }}>{SERVICE_ICON[s.name] ?? ""}</span>
                    {s.label}
                  </button>
                ))}
              </div>
            </div>
          )}

          {/* Custom port */}
          <div style={{ borderTop: "1px solid var(--border-subtle)", paddingTop: "var(--space-3)", marginTop: 4 }}>
            <div style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)", marginBottom: 6 }}>
              Custom port
            </div>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
              <label className="field" style={{ flex: "0 0 120px" }}>
                <span>VM port</span>
                <input
                  type="number"
                  inputMode="numeric"
                  min={1}
                  max={65535}
                  value={vmPort}
                  onChange={(e) => setVmPort(e.target.value)}
                  placeholder="e.g. 25565"
                />
              </label>
              <label className="field" style={{ flex: "0 0 120px" }}>
                <span>Protocol</span>
                <select
                  value={protocol}
                  onChange={(e) => setProtocol(e.target.value as PortProtocol)}
                  style={{
                    padding: "var(--space-2) var(--space-3)",
                    border: "1px solid var(--border-strong)",
                    borderRadius: "var(--radius)",
                    font: "inherit",
                    background: "var(--canvas)",
                    color: "var(--text-primary)",
                  }}
                >
                  {PROTOCOLS.map((p) => (
                    <option key={p} value={p}>{p}</option>
                  ))}
                </select>
              </label>
              <label className="field" style={{ flex: "1 1 140px" }}>
                <span>Label (optional)</span>
                <input value={label} onChange={(e) => setLabel(e.target.value)} placeholder="e.g. Game server" />
              </label>
              <button className="btn-primary" type="button" onClick={openCustomPort} disabled={busy}>
                {allocate.isPending ? "Opening…" : "Open port"}
              </button>
            </div>
          </div>

          {(formError || mutationError) && (
            <p role="alert" style={{ color: "var(--danger)", fontSize: 13, marginTop: 4 }}>
              {formError ?? mutationError?.message ?? "That didn't work."}
            </p>
          )}

          <div style={{ display: "flex", justifyContent: "flex-end", marginTop: 8 }}>
            <Dialog.Close asChild>
              <button className="btn-ghost" type="button">Done</button>
            </Dialog.Close>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
