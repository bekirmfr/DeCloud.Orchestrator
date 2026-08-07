import type { CSSProperties, ReactNode } from "react";
import { useAuth } from "../../auth/AuthProvider";
import { useProfile, statusLabel } from "./useProfile";
import { avatarGradient } from "./avatar";

// Phase 6 · Profile page. Identity + quotas (Current vs Max) + key/VM counts.
// GET /api/user/me for everything except roles, which come from the session.

const card: CSSProperties = {
  padding: "var(--space-4)", border: "1px solid var(--border)", borderRadius: "var(--radius)",
  background: "var(--surface-1)", display: "flex", flexDirection: "column", gap: "var(--space-2)",
};
const gb = (b?: number) => (b == null ? "—" : `${Math.round(b / 1024 ** 3)} GB`);
const dt = (iso?: string) => (iso ? new Date(iso).toLocaleString() : "—");

function KV({ k, v }: { k: string; v: ReactNode }) {
  return (
    <div style={{ display: "flex", gap: 8, fontSize: "var(--text-sm)" }}>
      <span style={{ color: "var(--text-tertiary)", minWidth: 120 }}>{k}</span>
      <span style={{ color: "var(--text-secondary)" }}>{v}</span>
    </div>
  );
}
function Quota({ label, current, max, fmt }: { label: string; current: number; max: number; fmt: (n: number) => string }) {
  const pct = max > 0 ? Math.min(100, (current / max) * 100) : 0;
  return (
    <div style={{ display: "flex", flexDirection: "column", gap: 4 }}>
      <div style={{ display: "flex", justifyContent: "space-between", fontSize: "var(--text-sm)" }}>
        <span style={{ color: "var(--text-secondary)" }}>{label}</span>
        <span style={{ color: "var(--text-tertiary)" }}>{fmt(current)} / {fmt(max)}</span>
      </div>
      <div style={{ height: 6, background: "var(--surface-2)", borderRadius: 3, overflow: "hidden" }}>
        <div style={{ width: `${pct}%`, height: "100%", background: pct > 85 ? "var(--warning)" : "var(--accent)" }} />
      </div>
    </div>
  );
}

export function ProfilePage() {
  const { api, session } = useAuth();
  const { data: p, isLoading, isError } = useProfile(api);
  const roles = session.kind === "authenticated" || session.kind === "uncertain" ? session.user.roles : [];

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading…</p>;
  if (isError || !p) return <p style={{ color: "var(--danger)" }}>Couldn't load your profile.</p>;

  const q = p.quotas;
  const n = (x: number) => String(x);

  return (
    <div style={{ display: "flex", flexDirection: "column", gap: "var(--space-4)", maxWidth: 720 }}>
      <div style={{ display: "flex", gap: "var(--space-3)", alignItems: "center" }}>
        <span style={{ width: 56, height: 56, borderRadius: "50%", background: avatarGradient(p.walletAddress), flexShrink: 0 }} />
        <div style={{ minWidth: 0 }}>
          <h1 style={{ margin: 0 }}>{p.displayName || "Profile"}</h1>
          <div style={{ fontFamily: "var(--font-mono)", color: "var(--text-tertiary)", fontSize: "var(--text-sm)", wordBreak: "break-all" }}>{p.walletAddress}</div>
        </div>
      </div>

      <section style={card}>
        <KV k="Status" v={statusLabel(p.status)} />
        {p.email && <KV k="Email" v={p.email} />}
        <KV k="Roles" v={roles.length ? roles.join(", ") : "User"} />
        <KV k="Joined" v={dt(p.createdAt)} />
        <KV k="Last login" v={dt(p.lastLoginAt)} />
        <KV k="Virtual machines" v={`${p.runningVms ?? 0} running · ${p.totalVms ?? 0} total`} />
        <KV k="SSH keys" v={n(p.sshKeys?.length ?? 0)} />
        <KV k="API keys" v={n(p.apiKeys?.length ?? 0)} />
      </section>

      {q && (
        <section style={card}>
          <strong style={{ fontSize: "var(--text-sm)", color: "var(--text-secondary)" }}>Quotas</strong>
          <Quota label="Virtual machines" current={q.currentVms} max={q.maxVms} fmt={n} />
          <Quota label="vCPU cores" current={q.currentVirtualCpuCores} max={q.maxVirtualCpuCores} fmt={n} />
          <Quota label="Memory" current={q.currentMemoryBytes} max={q.maxMemoryBytes} fmt={gb} />
          <Quota label="Storage" current={q.currentStorageBytes} max={q.maxStorageBytes} fmt={gb} />
        </section>
      )}
    </div>
  );
}
