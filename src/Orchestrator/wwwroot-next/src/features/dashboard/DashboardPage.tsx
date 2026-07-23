import { Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useVms, type VmSummary } from "../vms/useVms";
import { vmStatusBadge, normalizeStatus, type BadgeTone } from "../vms/vmStatus";
import { useBalance, runwayDays, formatRunway, LOW_RUNWAY_DAYS } from "../billing/useBalance";
import { useUserRealtime } from "../../realtime/useUserRealtime";
import type { AppError } from "../../api/errors";

// Phase 3 · step 4 — the DASHBOARD: operate + fund home (DESIGN §2).
// Running workloads and their runway, with Deploy promoted to a primary action
// instead of buried three levels deep. Status is LIVE (SubscribeToUser);
// balance/runway POLLS, because the hub has no BalanceUpdated emit yet (§6.9).

const TONE: Record<BadgeTone, string> = {
  active: "var(--success-solid)",
  transitional: "var(--warning-solid)",
  inert: "var(--text-disabled)",
  error: "var(--danger)",
};

/** Status dot with a soft halo — the Meridian `.sdot` primitive, inlined. */
function StatusDot({ status }: { status: VmSummary["status"] }) {
  const { tone } = vmStatusBadge(status);
  const c = TONE[tone];
  return (
    <span
      aria-hidden
      style={{
        width: 8, height: 8, borderRadius: "50%", display: "inline-block",
        background: c, boxShadow: `0 0 0 3px color-mix(in srgb, ${c} 22%, transparent)`,
        flexShrink: 0,
      }}
    />
  );
}

function Card({ title, count, children }: { title: string; count?: string; children: React.ReactNode }) {
  return (
    <section style={{ background: "var(--surface-2)", border: "1px solid var(--border)", borderRadius: "var(--radius-card)" }}>
      <header style={{ display: "flex", alignItems: "center", justifyContent: "space-between", padding: "15px 18px", borderBottom: "1px solid var(--border-subtle)" }}>
        <h2 style={{ fontFamily: "var(--font-display)", fontWeight: 500, fontSize: "14.5px" }}>{title}</h2>
        {count && <span style={{ fontFamily: "var(--font-mono)", fontSize: 12, color: "var(--text-tertiary)" }}>{count}</span>}
      </header>
      {children}
    </section>
  );
}

const gib = (b: number) => Math.round(b / 1024 ** 3);

export function DashboardPage() {
  const { api, wallet } = useAuth();

  // OwnerId IS the wallet address on the VM record, and the server broadcasts
  // to user:{OwnerId} — so this is the right subscription key.
  const userId = wallet.kind === "connected" ? wallet.address : "";
  useUserRealtime(userId);

  const { data: vms, isLoading, error } = useVms(api, 1);
  const { data: balance } = useBalance(api);

  const days = runwayDays(balance?.balance, balance?.hourlyBurnRate);
  const lowRunway = days != null && days < LOW_RUNWAY_DAYS;

  // "Active" = anything not inert/terminal — what's actually costing money or
  // on its way to. Uses the same normaliser as everything else.
  const active = (vms?.items ?? []).filter((v) => {
    const s = normalizeStatus(v.status);
    return s !== "Deleted" && s !== "Stopped" && s !== "Suspended";
  });

  return (
    <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-5)" }}>
      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "center", gap: 16, flexWrap: "wrap" }}>
        <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>Overview</h1>
        {/* Deploy promoted: primary and always available (DESIGN §2). */}
        <Link className="btn-primary" to="/marketplace/platform-general/deploy">Deploy a workload</Link>
      </header>

      {/* ── Money ─────────────────────────────────────────────────────── */}
      <Card title="Balance">
        <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: "var(--space-4)", padding: "18px" }}>
          <div>
            <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Available</div>
            <div style={{ fontFamily: "var(--font-mono)", fontSize: "var(--text-xl)", marginTop: 2 }}>
              {balance ? `${balance.balance.toFixed(2)} ${balance.tokenSymbol}` : "—"}
            </div>
            {!!balance?.pendingDeposits && (
              <div style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 2 }}>
                +{balance.pendingDeposits.toFixed(2)} confirming
              </div>
            )}
          </div>

          <div>
            <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Burn rate</div>
            <div style={{ fontFamily: "var(--font-mono)", fontSize: "var(--text-xl)", marginTop: 2 }}>
              {balance ? `${balance.hourlyBurnRate.toFixed(4)}/hr` : "—"}
            </div>
          </div>

          <div>
            <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Runway</div>
            <div style={{
              fontFamily: "var(--font-mono)", fontSize: "var(--text-xl)", marginTop: 2,
              color: lowRunway ? "var(--danger)" : "var(--text-primary)",
            }}>
              {balance ? formatRunway(days) : "—"}
            </div>
          </div>
        </div>

        {lowRunway && (
          <div role="alert" style={{ padding: "12px 18px", borderTop: "1px solid var(--border-subtle)", background: "var(--warning-soft)", color: "var(--warning)", fontSize: "var(--text-sm)" }}>
            Your workloads will stop when the balance runs out.{" "}
            {/* LEGACY BRIDGE (v1) — the on-chain deposit flow is not ported to
                React yet. See features/deploy/DEPLOY_MIGRATION.md. */}
            <a href="/" style={{ color: "inherit", textDecoration: "underline" }}>Add funds in the classic app</a>.
          </div>
        )}
      </Card>

      {/* ── Workloads ─────────────────────────────────────────────────── */}
      <Card title="Running workloads" count={vms ? `${active.length} of ${vms.totalCount}` : undefined}>
        {isLoading && <p style={{ padding: 18, color: "var(--text-secondary)" }}>Loading…</p>}

        {error && (
          <p role="alert" style={{ padding: 18, color: "var(--danger)" }}>
            {(error as AppError)?.message ?? "Couldn't load your workloads."}
          </p>
        )}

        {!isLoading && !error && active.length === 0 && (
          <div style={{ padding: "28px 18px", textAlign: "center" }}>
            <p style={{ color: "var(--text-secondary)" }}>Nothing running yet.</p>
            <Link className="btn-primary" to="/marketplace/platform-general/deploy" style={{ marginTop: 12 }}>
              Deploy your first workload
            </Link>
          </div>
        )}

        {active.map((vm) => (
          <div key={vm.id} style={{ display: "grid", gridTemplateColumns: "1.4fr 1fr auto", gap: 12, alignItems: "center", padding: "15px 18px", borderBottom: "1px solid var(--border-subtle)" }}>
            <div style={{ display: "flex", alignItems: "center", gap: 10, minWidth: 0 }}>
              <StatusDot status={vm.status} />
              <Link to={`/vms/${vm.id}`} style={{ color: "var(--text-accent)", fontWeight: "var(--fw-medium)", overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" }}>
                {vm.name}
              </Link>
              {vm.complianceHold && (
                <span style={{ fontSize: "var(--text-xs)", color: "var(--warning)" }}>held</span>
              )}
            </div>
            <div style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
              {vmStatusBadge(vm.status).label}
            </div>
            <div style={{ fontFamily: "var(--font-mono)", fontSize: 12.5, color: "var(--text-secondary)", textAlign: "right" }}>
              {vm.spec.virtualCpuCores} vCPU · {gib(vm.spec.memoryBytes)} GB
            </div>
          </div>
        ))}

        {vms && vms.totalCount > active.length && (
          <div style={{ padding: "12px 18px" }}>
            <Link to="/vms" style={{ color: "var(--text-accent)", fontSize: "var(--text-sm)" }}>
              View all {vms.totalCount} virtual machines →
            </Link>
          </div>
        )}
      </Card>
    </section>
  );
}
