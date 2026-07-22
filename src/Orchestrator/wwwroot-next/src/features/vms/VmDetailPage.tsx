import { useParams, Link, useNavigate } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useVm, useVmAction, useDeleteVm, type VmDetail } from "./useVms";
import { vmStatusBadge, allowedActions, type BadgeTone, type VmAction } from "./vmStatus";
import type { AppError } from "../../api/errors";

// Phase 3 · step 1b — the VM detail cockpit (STATIC). Renders the owner-facing
// subset of VirtualMachine: status, spec, access ("how do I connect"), services,
// timestamps, and STATE-AWARE lifecycle actions. Live metrics (VmMetrics) arrive
// over SignalR — that's step 3, not here. Actions/delete are validated server-side.

const TONE_STYLE: Record<BadgeTone, { bg: string; fg: string }> = {
  active:       { bg: "var(--success-soft)", fg: "var(--success)" },
  transitional: { bg: "var(--warning-soft)", fg: "var(--warning)" },
  inert:        { bg: "var(--surface-1)",    fg: "var(--text-secondary)" },
  error:        { bg: "var(--danger-soft)",  fg: "var(--danger)" },
};

function StatusBadge({ status }: { status: VmDetail["status"] }) {
  const { label, tone } = vmStatusBadge(status);
  const s = TONE_STYLE[tone];
  return (
    <span style={{ display: "inline-block", padding: "2px 10px", borderRadius: "var(--radius-pill)", fontSize: "var(--text-sm)", fontWeight: "var(--fw-medium)", background: s.bg, color: s.fg }}>
      {label}
    </span>
  );
}

const gib = (b: number) => Math.round(b / 1024 ** 3);

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div style={{ border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-card)", padding: "var(--space-5)", background: "var(--surface-1)" }}>
      <h2 style={{ fontFamily: "var(--font-display)", fontSize: "var(--text-md)", fontWeight: 600, marginBottom: "var(--space-3)" }}>{title}</h2>
      {children}
    </div>
  );
}

const Row = ({ k, v }: { k: string; v: React.ReactNode }) => (
  <div style={{ display: "flex", justifyContent: "space-between", gap: 16, padding: "6px 0", borderTop: "1px solid var(--border-subtle)" }}>
    <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{k}</span>
    <span style={{ fontFamily: "var(--font-mono)", fontSize: 12.5, textAlign: "right" }}>{v ?? "—"}</span>
  </div>
);

export function VmDetailPage() {
  const { id = "" } = useParams();
  const { api } = useAuth();
  const navigate = useNavigate();
  const { data, isLoading, error } = useVm(api, id);
  const action = useVmAction(api);
  const del = useDeleteVm(api);

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
  if (error) return <p role="alert" style={{ color: "var(--danger)" }}>{(error as AppError)?.message ?? "Couldn't load this VM."}</p>;
  if (!data) return null;

  const vm = data.vm;
  const actions = allowedActions(vm.status, vm.powerState, vm.complianceHold);

  function onAction(a: VmAction) {
    action.mutate({ id, action: a });
  }
  function onDelete() {
    if (!window.confirm(`Delete VM "${vm.name}"? This cannot be undone.`)) return;
    del.mutate(id, { onSuccess: () => navigate("/vms") });
  }

  const ssh = vm.accessInfo?.sshHost
    ? `ssh -p ${vm.accessInfo.sshPort ?? 22} root@${vm.accessInfo.sshHost}`
    : null;

  return (
    <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-5)" }}>
      <div style={{ display: "flex", alignItems: "baseline", gap: 12 }}>
        <Link to="/vms" style={{ color: "var(--text-accent)", fontSize: "var(--text-sm)" }}>← VMs</Link>
      </div>

      <header style={{ display: "flex", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap", gap: 12 }}>
        <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
          <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>{vm.name}</h1>
          <StatusBadge status={vm.status} />
          {vm.complianceHold && <span style={{ fontSize: "var(--text-sm)", color: "var(--warning)" }}>administratively held</span>}
        </div>
        <div style={{ display: "flex", gap: 8, flexWrap: "wrap" }}>
          {actions.map((a) => (
            <button key={a} className="btn-ghost" onClick={() => onAction(a)} disabled={action.isPending}>{a}</button>
          ))}
          <button className="btn-ghost" onClick={onDelete} disabled={del.isPending || vm.complianceHold} style={{ color: "var(--danger)", borderColor: "var(--danger)" }}>Delete</button>
        </div>
      </header>

      {vm.statusMessage && (
        <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{vm.statusMessage}</p>
      )}

      <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))", gap: "var(--space-4)" }}>
        <Card title="Resources">
          <Row k="vCPU" v={vm.spec.virtualCpuCores} />
          <Row k="Memory" v={`${gib(vm.spec.memoryBytes)} GB`} />
          <Row k="Disk" v={`${gib(vm.spec.diskBytes)} GB`} />
        </Card>

        <Card title="Access">
          <Row k="SSH host" v={vm.accessInfo?.sshHost} />
          <Row k="SSH command" v={ssh} />
          <Row k="Private IP" v={vm.networkConfig?.privateIp} />
          {vm.networkConfig?.hostname && <Row k="Hostname" v={vm.networkConfig.hostname} />}
        </Card>

        <Card title="Details">
          <Row k="Node" v={vm.nodeId} />
          <Row k="Created" v={new Date(vm.createdAt).toLocaleString()} />
          {vm.startedAt && <Row k="Started" v={new Date(vm.startedAt).toLocaleString()} />}
          {vm.stoppedAt && <Row k="Stopped" v={new Date(vm.stoppedAt).toLocaleString()} />}
        </Card>
      </div>

      {vm.services && vm.services.length > 0 && (
        <Card title="Services">
          {vm.services.map((svc, i) => (
            <Row key={i} k={svc.name} v={String(svc.status)} />
          ))}
        </Card>
      )}

      {(action.error || del.error) && (
        <p role="alert" style={{ color: "var(--danger)", fontSize: "var(--text-sm)" }}>
          {((action.error || del.error) as AppError)?.message ?? "Action failed."}
        </p>
      )}
    </section>
  );
}
