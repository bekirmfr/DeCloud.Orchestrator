import { useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { useParams, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import type { AppError } from "../../api/errors";
import { domainStatusBadge, canVerify, type DomainTone } from "./domainStatus";
import {
  useCustomDomains,
  useAddDomain,
  useVerifyDomain,
  useRemoveDomain,
  type CustomDomain,
} from "./useDomains";

// Modal-ROUTE at /app/vms/:id/domains (DESIGN §3 route tree + §7 Operate parity:
// "custom domains (add/verify/remove, DNS/CNAME instructions, status labels)").
// Point your own hostname at a VM's web service over the central ingress (Caddy
// terminates TLS). Add → CNAME to the shown target → Verify → Active.

const TONE_STYLE: Record<DomainTone, { bg: string; fg: string }> = {
  active:  { bg: "var(--success-soft)", fg: "var(--success)" },
  pending: { bg: "var(--warning-soft)", fg: "var(--warning)" },
  inert:   { bg: "var(--surface-1)",    fg: "var(--text-secondary)" },
  error:   { bg: "var(--danger-soft)",  fg: "var(--danger)" },
};

function StatusBadge({ status }: { status: CustomDomain["status"] }) {
  const { label, tone } = domainStatusBadge(status);
  const s = TONE_STYLE[tone];
  return (
    <span style={{ display: "inline-block", padding: "2px 10px", borderRadius: "var(--radius-pill)", fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)", background: s.bg, color: s.fg }}>
      {label}
    </span>
  );
}

function CopyButton({ text }: { text: string }) {
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
          /* clipboard blocked — value is still visible */
        }
      }}
    >
      {copied ? "Copied" : "Copy"}
    </button>
  );
}

function DomainRow({
  d,
  onVerify,
  onRemove,
  verifying,
  removing,
}: {
  d: CustomDomain;
  onVerify: () => void;
  onRemove: () => void;
  verifying: boolean;
  removing: boolean;
}) {
  return (
    <div style={{ padding: "10px 0", borderTop: "1px solid var(--border-subtle)" }}>
      <div style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
        <div style={{ minWidth: 0 }}>
          {d.status === "Active" && d.publicUrl ? (
            <a href={d.publicUrl} target="_blank" rel="noreferrer" style={{ fontFamily: "var(--font-mono)", fontSize: 13, color: "var(--text-accent)" }}>
              {d.domain}
            </a>
          ) : (
            <code style={{ fontFamily: "var(--font-mono)", fontSize: 13 }}>{d.domain}</code>
          )}
          <span style={{ color: "var(--text-tertiary)", fontSize: 12, marginLeft: 8 }}>→ port {d.targetPort}</span>
        </div>
        <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <StatusBadge status={d.status} />
          {canVerify(d.status) && (
            <button type="button" className="btn-ghost" style={{ padding: "2px 8px", fontSize: 12 }} onClick={onVerify} disabled={verifying}>
              {verifying ? "Checking…" : "Verify"}
            </button>
          )}
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
      </div>

      {/* CNAME instructions while not Active */}
      {d.status !== "Active" && d.dnsTarget && (
        <div style={{ marginTop: 6, display: "flex", alignItems: "center", gap: 8, flexWrap: "wrap" }}>
          <code style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--text-secondary)" }}>
            CNAME {d.domain} → {d.dnsTarget}
          </code>
          <CopyButton text={d.dnsTarget} />
        </div>
      )}
      {d.dnsInstructions && d.status !== "Active" && (
        <p style={{ fontSize: 12, color: "var(--text-tertiary)", marginTop: 4 }}>{d.dnsInstructions}</p>
      )}
    </div>
  );
}

export function DomainsModal() {
  const { id = "" } = useParams();
  const navigate = useNavigate();
  const { api } = useAuth();

  const domains = useCustomDomains(api, id);
  const add = useAddDomain(api, id);
  const verify = useVerifyDomain(api, id);
  const remove = useRemoveDomain(api, id);

  const [domain, setDomain] = useState("");
  const [targetPort, setTargetPort] = useState("80");
  const [formError, setFormError] = useState<string | null>(null);

  const close = () => navigate(`/vms/${id}`);
  const mutationError = (add.error || verify.error || remove.error) as AppError | null;

  async function addDomain() {
    setFormError(null);
    const name = domain.trim().toLowerCase();
    // Light client pre-check for UX; the server is the authority on validity.
    if (!/^([a-z0-9-]+\.)+[a-z]{2,}$/.test(name)) {
      setFormError("Enter a domain like app.example.com.");
      return;
    }
    const port = Number(targetPort) || 80;
    if (port < 1 || port > 65535) {
      setFormError("Target port must be between 1 and 65535.");
      return;
    }
    try {
      await add.mutateAsync({ domain: name, targetPort: port });
      setDomain("");
      setTargetPort("80");
    } catch {
      /* surfaced via mutationError */
    }
  }

  const list = domains.data ?? [];

  return (
    <Dialog.Root open onOpenChange={(o) => !o && close()}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content
          className="dialog-content"
          aria-describedby={undefined}
          style={{ width: "min(640px, calc(100vw - 32px))", maxHeight: "calc(100vh - 48px)", overflowY: "auto" }}
        >
          <Dialog.Title style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>
            Custom domains
          </Dialog.Title>
          <p style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>
            Point your own hostname at this VM. Add it, create the CNAME record it shows you,
            then verify — TLS is handled automatically once it's live.
          </p>

          {domains.isLoading ? (
            <p style={{ color: "var(--text-secondary)" }}>Loading…</p>
          ) : domains.error ? (
            <p role="alert" style={{ color: "var(--danger)", fontSize: "var(--text-sm)" }}>
              {(domains.error as AppError)?.message ?? "Couldn't load custom domains."}
            </p>
          ) : list.length === 0 ? (
            <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
              No custom domains yet. Add one below.
            </p>
          ) : (
            <div>
              {list.map((d) => (
                <DomainRow
                  key={d.id}
                  d={d}
                  verifying={verify.isPending}
                  removing={remove.isPending}
                  onVerify={() => verify.mutate(d.id)}
                  onRemove={() => remove.mutate(d.id)}
                />
              ))}
            </div>
          )}

          {/* Add domain */}
          <div style={{ borderTop: "1px solid var(--border-subtle)", paddingTop: "var(--space-3)", marginTop: 4 }}>
            <div style={{ display: "flex", gap: 8, flexWrap: "wrap", alignItems: "flex-end" }}>
              <label className="field" style={{ flex: "1 1 200px" }}>
                <span>Domain</span>
                <input value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="app.example.com" />
              </label>
              <label className="field" style={{ flex: "0 0 110px" }}>
                <span>Target port</span>
                <input type="number" inputMode="numeric" min={1} max={65535} value={targetPort} onChange={(e) => setTargetPort(e.target.value)} />
              </label>
              <button className="btn-primary" type="button" onClick={addDomain} disabled={add.isPending}>
                {add.isPending ? "Adding…" : "Add domain"}
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
