import { useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import { useTemplate, useBalance, useDeploy, runwayDays, fundGateBlocks, specFloorErrors } from "./useDeploy";
import { shouldRevealPassword, type DeployResult, type TemplateSpec } from "./deploySubmit";
import type { AppError } from "../../api/errors";

// Phase 3 · deploy sub-step 2 — the ONE-CLICK deploy page.
// Route: /app/marketplace/:slug/deploy. The hot path is a single button:
// "Deploy with recommended settings" sends ONLY { vmName } — the server merges
// template.RecommendedSpec (BuildVmRequestFromTemplateAsync). Customize is a
// later sub-step. Fund gate + runway are UX pre-checks; the server enforces.

const gib = (b?: number) => (b ? Math.round(b / 1024 ** 3) : 0);

function Card({ title, children }: { title?: string; children: React.ReactNode }) {
  return (
    <div style={{ border: "1px solid var(--border-subtle)", borderRadius: "var(--radius-card)", padding: "var(--space-5)", background: "var(--surface-1)" }}>
      {title && <h2 style={{ fontFamily: "var(--font-display)", fontSize: "var(--text-md)", fontWeight: 600, marginBottom: "var(--space-3)" }}>{title}</h2>}
      {children}
    </div>
  );
}

/**
 * One-time password reveal. Held in COMPONENT STATE ONLY — never persisted, so a
 * reload loses it (design: "must not survive reload"). The server stores only a
 * hash-equivalent; this is the single moment the user can copy it.
 */
function PasswordReveal({ vmName, password, onDone }: { vmName: string; password: string; onDone: () => void }) {
  const [copied, setCopied] = useState(false);
  return (
    <div className="dialog-overlay" style={{ display: "grid", placeItems: "center" }}>
      <div className="dialog-content" style={{ position: "static", transform: "none" }}>
        <h2 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>Save your password</h2>
        <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
          This is the only time <strong>{vmName}</strong>’s root password is shown. Copy it now — it can’t be retrieved later.
        </p>
        <code style={{ display: "block", padding: "var(--space-3)", background: "var(--canvas)", border: "1px solid var(--border-strong)", borderRadius: "var(--radius)", fontFamily: "var(--font-mono)", fontSize: 14, wordBreak: "break-all" }}>
          {password}
        </code>
        <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: "var(--space-3)" }}>
          <button
            className="btn-ghost"
            onClick={() => { navigator.clipboard?.writeText(password); setCopied(true); }}
          >
            {copied ? "Copied" : "Copy"}
          </button>
          <button className="btn-primary" onClick={onDone}>I’ve saved it — continue</button>
        </div>
      </div>
    </div>
  );
}

export function DeployPage() {
  const { slug = "" } = useParams();
  const { api } = useAuth();
  const navigate = useNavigate();

  const { data: template, isLoading, error } = useTemplate(api, slug);
  const { data: balance } = useBalance(api);
  const deploy = useDeploy(api);

  const [vmName, setVmName] = useState("");
  const [customize, setCustomize] = useState(false);
  // Spec inputs live in human units (cores / GB); converted on submit.
  const [cpu, setCpu] = useState<number | null>(null);
  const [memGb, setMemGb] = useState<number | null>(null);
  const [diskGb, setDiskGb] = useState<number | null>(null);
  const [revealed, setRevealed] = useState<{ vmId: string; password: string } | null>(null);

  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading template…</p>;
  if (error) return <p role="alert" style={{ color: "var(--danger)" }}>{(error as AppError)?.message ?? "Couldn't load this template."}</p>;
  if (!template) return null;

  const costPerHour = template.estimatedCostPerHour;
  const days = runwayDays(balance?.balance, costPerHour);
  const blocked = fundGateBlocks(balance?.balance, costPerHour);
  const rec = template.recommendedSpec;
  const gbOf = (b?: number) => (b ? Math.round(b / 1024 ** 3) : 0);

  // Seeded from RecommendedSpec until the user edits a field.
  const effCpu = cpu ?? rec?.virtualCpuCores ?? 1;
  const effMemGb = memGb ?? gbOf(rec?.memoryBytes) ?? 1;
  const effDiskGb = diskGb ?? gbOf(rec?.diskBytes) ?? 10;

  const customSpec: TemplateSpec = {
    virtualCpuCores: effCpu,
    memoryBytes: effMemGb * 1024 ** 3,
    diskBytes: effDiskGb * 1024 ** 3,
    // Carry the rest of RecommendedSpec forward, then re-apply the template
    // defaults the server would have applied itself had we sent no customSpec.
    ...(rec?.imageId ? { imageId: rec.imageId } : {}),
    ...(rec?.qualityTier != null ? { qualityTier: rec.qualityTier } : {}),
    gpuMode: rec?.gpuMode ?? template.defaultGpuMode,
    bandwidthTier: rec?.bandwidthTier ?? template.defaultBandwidthTier,
  };

  const floorErrors = customize ? specFloorErrors(customSpec, template.minimumSpec) : [];

  async function onDeploy() {
    const name = vmName.trim();
    if (!name) return;
    try {
      // One-click: NO customSpec — the server applies template.RecommendedSpec.
      const result: DeployResult = await deploy.mutateAsync({
        templateId: template!.id,
        // One-click sends NO customSpec so the server applies RecommendedSpec
        // *and* its own template defaults. Customise sends a full spec — which
        // is why customSpec above re-applies bandwidth/GPU explicitly.
        payload: { vmName: name, customSpec: customize ? customSpec : null },
      });
      // Reveal only the human-readable generated password (legacy sniff).
      if (shouldRevealPassword(result.password)) {
        setRevealed({ vmId: result.vmId, password: result.password });
      } else {
        navigate(`/vms/${result.vmId}`);
      }
    } catch {
      // Error surfaced below via deploy.error — nothing to do here.
    }
  }

  return (
    <section style={{ display: "flex", flexDirection: "column", gap: "var(--space-5)", maxWidth: 720 }}>
      <div>
        <Link to="/vms" style={{ color: "var(--text-accent)", fontSize: "var(--text-sm)" }}>← VMs</Link>
      </div>

      <header>
        <h1 style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>Deploy {template.name}</h1>
        {template.description && (
          <p style={{ color: "var(--text-secondary)", marginTop: 4 }}>{template.description}</p>
        )}
      </header>

      {/* Hard fund gate — intercept BEFORE the form (design §: don't let someone
          configure a deploy they can't pay for). Server also enforces. */}
      {blocked ? (
        <Card title="Add funds to deploy">
          <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>
            Your available balance is {balance?.balance?.toFixed(2) ?? "0.00"} {balance?.tokenSymbol ?? "USDC"}.
            Running this template costs about {costPerHour?.toFixed(4)} USDC/hour, so there’s no runway yet.
          </p>
          {/* LEGACY BRIDGE (v1, tracked debt — see DEPLOY_MIGRATION.md): the
              on-chain deposit flow is not ported to React. Send the user to the
              legacy app's top-up rather than fork an ethers escrow deposit. */}
          <a className="btn-primary" href="/" style={{ marginTop: "var(--space-3)", display: "inline-block" }}>
            Add funds in the classic app
          </a>
        </Card>
      ) : (
        <>
          <Card title="What you’ll get">
            {rec && (
              <p style={{ fontFamily: "var(--font-mono)", fontSize: 13 }}>
                {rec.virtualCpuCores} vCPU · {gib(rec.memoryBytes)} GB RAM · {gib(rec.diskBytes)} GB disk
              </p>
            )}
            <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", marginTop: 8 }}>
              {days != null
                ? `Runs about ${days < 1 ? "less than a day" : `${Math.floor(days)} days`} on your current balance.`
                : "This template has no hourly cost estimate."}
            </p>
          </Card>

          <Card>
            <label className="field">
              <span>Name your VM</span>
              <input
                value={vmName}
                onChange={(e) => setVmName(e.target.value)}
                placeholder="my-vm"
                maxLength={40}
              />
            </label>
            <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)", marginTop: 6 }}>
              A unique suffix is appended automatically.
            </p>

            {deploy.error && (
              <p role="alert" style={{ color: "var(--danger)", fontSize: "var(--text-sm)", marginTop: 12 }}>
                {(deploy.error as AppError)?.message ?? "Deployment failed."}
              </p>
            )}

            <div style={{ marginTop: "var(--space-4)" }}>
              <button
                className="btn-ghost"
                onClick={() => setCustomize((c) => !c)}
                aria-expanded={customize}
                style={{ fontSize: "var(--text-sm)" }}
              >
                {customize ? "Use recommended settings" : "Customize resources"}
              </button>
            </div>

            {customize && (
              <div style={{ marginTop: "var(--space-4)", display: "grid", gap: "var(--space-3)" }}>
                <label className="field">
                  <span>vCPU</span>
                  <input type="number" min={1} max={32} value={effCpu}
                    onChange={(e) => setCpu(Number(e.target.value))} />
                </label>
                <label className="field">
                  <span>Memory (GB)</span>
                  <input type="number" min={1} max={128} value={effMemGb}
                    onChange={(e) => setMemGb(Number(e.target.value))} />
                </label>
                <label className="field">
                  <span>Disk (GB)</span>
                  <input type="number" min={10} max={2000} value={effDiskGb}
                    onChange={(e) => setDiskGb(Number(e.target.value))} />
                </label>

                {template.minimumSpec && (
                  <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
                    Minimum for this template: {template.minimumSpec.virtualCpuCores ?? 1} vCPU ·{" "}
                    {gbOf(template.minimumSpec.memoryBytes)} GB · {gbOf(template.minimumSpec.diskBytes)} GB
                  </p>
                )}

                {floorErrors.map((err) => (
                  <p key={err} role="alert" style={{ color: "var(--danger)", fontSize: "var(--text-sm)" }}>{err}</p>
                ))}
              </div>
            )}

            <div style={{ display: "flex", gap: 8, marginTop: "var(--space-4)" }}>
              <button
                className="btn-primary"
                onClick={onDeploy}
                disabled={!vmName.trim() || deploy.isPending || floorErrors.length > 0}
              >
                {deploy.isPending
                  ? "Deploying…"
                  : customize
                    ? "Deploy with these settings"
                    : "Deploy with recommended settings"}
              </button>
            </div>
            {/* TODO (later): template Variables (user-facing env vars). Needs the
                platform-vs-user variable discriminator grounded first. */}
          </Card>
        </>
      )}

      {revealed && (
        <PasswordReveal
          vmName={vmName.trim()}
          password={revealed.password}
          onDone={() => navigate(`/vms/${revealed.vmId}`)}
        />
      )}
    </section>
  );
}
