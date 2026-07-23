import { useState } from "react";
import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useUserRealtime } from "../../realtime/useUserRealtime";
import { useVms, VMS_PAGE_SIZE, type VmSummary } from "./useVms";
import { vmStatusBadge, type BadgeTone } from "./vmStatus";
import type { AppError } from "../../api/errors";

// Phase 3 · step 1a — the VM list. Paged (GET /api/vms), status-badged, links to
// the detail cockpit. Retires the legacy Virtual Machines page. Live metrics and
// lifecycle actions live on the detail page (steps 1b + 3), not here.

// Badge colors are INLINE (token-driven) so they can never render unstyled.
const TONE_STYLE: Record<BadgeTone, { bg: string; fg: string }> = {
  active:       { bg: "var(--success-soft)", fg: "var(--success)" },
  transitional: { bg: "var(--warning-soft)", fg: "var(--warning)" },
  inert:        { bg: "var(--surface-1)",    fg: "var(--text-secondary)" },
  error:        { bg: "var(--danger-soft)",  fg: "var(--danger)" },
};

function StatusBadge({ status }: { status: VmSummary["status"] }) {
  const { label, tone } = vmStatusBadge(status);
  const s = TONE_STYLE[tone];
  return (
    <span
      style={{
        display: "inline-block", padding: "2px 10px", borderRadius: "var(--radius-pill)",
        fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)",
        background: s.bg, color: s.fg,
      }}
    >
      {label}
    </span>
  );
}

const gib = (bytes: number) => Math.round(bytes / 1024 ** 3);

export function VmsPage() {
  const { api, wallet } = useAuth();

  // Live status, same as the dashboard. useUserRealtime patches every cached
  // ["vms", *] page, and this page reads that cache — so subscribing here makes
  // the list update on its own instead of only when the dashboard happens to be
  // mounted alongside it.
  useUserRealtime(wallet.kind === "connected" ? wallet.address : "");
  const [page, setPage] = useState(1);
  const { data, isLoading, error, isPlaceholderData } = useVms(api, page);

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / VMS_PAGE_SIZE)) : 1;

  return (
    <section>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline", marginBottom: 20 }}>
        <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>Virtual Machines</h1>
        {/* TODO (step 2): <Link className="btn-primary" to="/deploy">Deploy</Link> */}
      </header>

      {isLoading && <p style={{ color: "var(--text-secondary)" }}>Loading…</p>}

      {error && (
        <p role="alert" style={{ color: "var(--danger)" }}>
          {(error as AppError)?.message ?? "Couldn't load your VMs."}
        </p>
      )}

      {!isLoading && !error && data && data.items.length === 0 && (
        <p style={{ color: "var(--text-secondary)" }}>
          No virtual machines yet. Deploy one to get started.
        </p>
      )}

      {!isLoading && !error && data && data.items.length > 0 && (
        <>
          <table style={{ width: "100%", borderCollapse: "collapse", opacity: isPlaceholderData ? 0.6 : 1 }}>
            <thead>
              <tr style={{ textAlign: "left", color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
                <th style={{ padding: "8px 12px" }}>Name</th>
                <th style={{ padding: "8px 12px" }}>Status</th>
                <th style={{ padding: "8px 12px" }}>Resources</th>
                <th style={{ padding: "8px 12px" }}>Created</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((vm) => (
                <tr key={vm.id} style={{ borderTop: "1px solid var(--border-subtle)" }}>
                  <td style={{ padding: "10px 12px" }}>
                    <Link to={`/vms/${vm.id}`} style={{ color: "var(--text-accent)", fontWeight: "var(--fw-medium)" }}>
                      {vm.name}
                    </Link>
                    {vm.complianceHold && (
                      <span style={{ marginLeft: 8, fontSize: "var(--text-xs)", color: "var(--warning)" }}>held</span>
                    )}
                  </td>
                  <td style={{ padding: "10px 12px" }}><StatusBadge status={vm.status} /></td>
                  <td style={{ padding: "10px 12px", color: "var(--text-secondary)", fontFamily: "var(--font-mono)", fontSize: 12.5 }}>
                    {vm.spec.virtualCpuCores} vCPU · {gib(vm.spec.memoryBytes)} GB · {gib(vm.spec.diskBytes)} GB
                  </td>
                  <td style={{ padding: "10px 12px", color: "var(--text-secondary)" }}>
                    {new Date(vm.createdAt).toLocaleDateString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>

          {totalPages > 1 && (
            <div style={{ display: "flex", gap: 12, alignItems: "center", justifyContent: "flex-end", marginTop: 16 }}>
              <button className="btn-ghost" onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page <= 1}>
                Previous
              </button>
              <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
                Page {page} of {totalPages}
              </span>
              <button
                className="btn-ghost"
                onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
                disabled={page >= totalPages || isPlaceholderData}
              >
                Next
              </button>
            </div>
          )}
        </>
      )}
    </section>
  );
}
