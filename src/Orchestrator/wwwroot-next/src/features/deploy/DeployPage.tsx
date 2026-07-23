import { useState } from "react";
import { useParams, useNavigate, Link } from "react-router-dom";
import { useAuth } from "../../auth/AuthProvider";
import {
  useTemplate, useBalance, useDeploy, runwayDays, fundGateBlocks, specFloorErrors,
  allowedQualityTiers, allowedBandwidthTiers, QUALITY_TIERS, BANDWIDTH_TIERS, GPU_MODES,
  usePriceEstimate, useDebounced,
} from "./useDeploy";
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

/** Label/value line used by the cost breakdown. Local to this page — the VM
 *  cockpit has its own; sharing one would couple two unrelated layouts. */
function Row({ k, v }: { k: string; v: React.ReactNode }) {
  return (
    <div style={{ display: "flex", justifyContent: "space-between", gap: 16, padding: "4px 0" }}>
      <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>{k}</span>
      <span style={{ fontFamily: "var(--font-mono)", fontSize: 12.5 }}>{v}</span>
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
  const [tier, setTier] = useState<number | null>(null);
  const [bwTier, setBwTier] = useState<number | null>(null);
  const [gpuMode, setGpuMode] = useState<number | null>(null);
  const [gpuVramGb, setGpuVramGb] = useState<number | null>(null);
  const [revealed, setRevealed] = useState<{ vmId: string; password: string } | null>(null);

  // ── EVERYTHING ABOVE THE EARLY RETURNS MUST BE UNCONDITIONAL ────────────
  // Rules of Hooks: the hook count has to be identical on every render. The
  // early returns below run only while the template loads, so any hook placed
  // after them is skipped on the loading render and called on the loaded one —
  // React then throws #310 ("rendered more hooks than during the previous
  // render"). It presents as intermittent because a cached template skips the
  // loading render entirely. Hence `template` is optional here and narrowed
  // only after the guards.
  const rec = template?.recommendedSpec;
  const gbOf = (b?: number) => (b ? Math.round(b / 1024 ** 3) : 0);

  // Seeded from RecommendedSpec until the user edits a field.
  const effCpu = cpu ?? rec?.virtualCpuCores ?? 1;
  const effMemGb = memGb ?? gbOf(rec?.memoryBytes) ?? 1;
  const effDiskGb = diskGb ?? gbOf(rec?.diskBytes) ?? 10;

  const qualityOptions = allowedQualityTiers(template?.minimumSpec?.qualityTier);
  const bandwidthOptions = allowedBandwidthTiers(template?.minimumSpec?.bandwidthTier);
  const effTier = tier ?? rec?.qualityTier ?? qualityOptions[qualityOptions.length - 1] ?? 1;
  const effBwTier = bwTier ?? rec?.bandwidthTier ?? template?.defaultBandwidthTier ?? bandwidthOptions[0] ?? 0;
  const effGpuMode = gpuMode ?? rec?.gpuMode ?? template?.defaultGpuMode ?? 0;
  const effGpuVramGb = gpuVramGb ?? gbOf(rec?.gpuVramBytes) ?? 4;
  // Legacy rule: the GPU section appears only when the template needs one.
  const showGpu = !!template?.requiresGpu || (template?.defaultGpuMode ?? 0) !== 0;

  const customSpec: TemplateSpec = {
    virtualCpuCores: effCpu,
    memoryBytes: effMemGb * 1024 ** 3,
    diskBytes: effDiskGb * 1024 ** 3,
    // Carry the rest of RecommendedSpec forward, then re-apply the template
    // defaults the server would have applied itself had we sent no customSpec.
    ...(rec?.imageId ? { imageId: rec.imageId } : {}),
    qualityTier: effTier,
    bandwidthTier: effBwTier,
    gpuMode: effGpuMode,
    ...(showGpu && effGpuMode !== 0 ? { gpuVramBytes: effGpuVramGb * 1024 ** 3, requiresGpu: true } : {}),
  };

  // Price the spec that would actually be deployed — the recommended one when
  // not customising. Debounced so typing in a number field doesn't fire per
  // keystroke; the JSON doubles as the query key and the request body. Null
  // until the template arrives, which the query treats as disabled.
  const specJson = template ? JSON.stringify(customSpec) : null;
  const debouncedSpecJson = useDebounced(specJson, 400);
  const { data: price } = usePriceEstimate(api, debouncedSpecJson);

  // ── Hooks done. Guards and plain derivations from here. ─────────────────
  if (isLoading) return <p style={{ color: "var(--text-secondary)" }}>Loading template…</p>;
  if (error) return <p role="alert" style={{ color: "var(--danger)" }}>{(error as AppError)?.message ?? "Couldn't load this template."}</p>;
  if (!template) return null;

  const floorErrors = customize ? specFloorErrors(customSpec, template.minimumSpec) : [];

  // Runway and the fund gate run off the SERVER-COMPUTED cost. The template's
  // own estimatedCostPerHour is frequently unset (platform-general has none),
  // which is why the page used to say "no hourly cost estimate" for a VM that
  // does in fact cost money.
  const costPerHour = price?.hourlyTotal ?? template.estimatedCostPerHour;
  const days = runwayDays(balance?.balance, costPerHour);
  const blocked = fundGateBlocks(balance?.balance, costPerHour);

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
            {price && (
              <div style={{ marginTop: "var(--space-3)", paddingTop: "var(--space-3)", borderTop: "1px solid var(--border-subtle)" }}>
                <div style={{ display: "flex", justifyContent: "space-between", alignItems: "baseline" }}>
                  <span style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)" }}>Estimated cost</span>
                  <span style={{ fontFamily: "var(--font-mono)", fontWeight: "var(--fw-medium)" }}>
                    {price.hourlyTotal.toFixed(4)} {price.currency}/hr
                  </span>
                </div>
                <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)", marginTop: 4, fontFamily: "var(--font-mono)" }}>
                  ≈ {price.dailyTotal.toFixed(2)}/day · {price.monthlyTotal.toFixed(2)}/month
                </p>
                {/* The estimator prices at PLATFORM DEFAULT rates (nodePricing: null).
                    A node whose operator set higher rates bills more — never below floor. */}
                <p style={{ color: "var(--text-tertiary)", fontSize: "var(--text-xs)", marginTop: 4 }}>
                  At platform default rates. The node you land on may charge more.
                </p>
              </div>
            )}

            <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-sm)", marginTop: 8 }}>
              {days != null
                ? `Runs about ${days < 1 ? "less than a day" : `${Math.floor(days)} days`} on your current balance.`
                : "Estimating cost…"}
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

                <label className="field">
                  <span>Performance tier</span>
                  <select value={effTier} onChange={(e) => setTier(Number(e.target.value))}>
                    {qualityOptions.map((t) => (
                      <option key={t} value={t}>{QUALITY_TIERS[t]}</option>
                    ))}
                  </select>
                </label>

                <label className="field">
                  <span>Bandwidth</span>
                  <select value={effBwTier} onChange={(e) => setBwTier(Number(e.target.value))}>
                    {bandwidthOptions.map((t) => (
                      <option key={t} value={t}>{BANDWIDTH_TIERS[t]}</option>
                    ))}
                  </select>
                </label>

                {showGpu && (
                  <>
                    <label className="field">
                      <span>GPU</span>
                      <select value={effGpuMode} onChange={(e) => setGpuMode(Number(e.target.value))}>
                        {[0, 1, 2].map((m) => (
                          <option key={m} value={m}>{GPU_MODES[m]}</option>
                        ))}
                      </select>
                    </label>
                    {effGpuMode !== 0 && (
                      <label className="field">
                        {/* Passthrough dedicates the whole GPU, so VRAM is only a
                            scheduling/billing estimate; Proxied enforces it as a quota. */}
                        <span>
                          {effGpuMode === 1 ? "Min. VRAM (GB) — estimate only" : "VRAM (GB)"}
                        </span>
                        <input type="number" min={1} max={80} value={effGpuVramGb}
                          onChange={(e) => setGpuVramGb(Number(e.target.value))} />
                      </label>
                    )}
                  </>
                )}

                {template.minimumSpec && (
                  <p style={{ color: "var(--text-secondary)", fontSize: "var(--text-xs)" }}>
                    Minimum for this template: {template.minimumSpec.virtualCpuCores ?? 1} vCPU ·{" "}
                    {gbOf(template.minimumSpec.memoryBytes)} GB · {gbOf(template.minimumSpec.diskBytes)} GB
                  </p>
                )}

                {price && (
                  <div style={{ borderTop: "1px solid var(--border-subtle)", paddingTop: "var(--space-3)" }}>
                    <Row k="CPU" v={`${price.cpuCost.toFixed(4)} ${price.currency}/hr`} />
                    <Row k="Memory" v={`${price.memoryCost.toFixed(4)} ${price.currency}/hr`} />
                    <Row k="Storage" v={`${price.storageCost.toFixed(4)} ${price.currency}/hr`} />
                    <Row k="Bandwidth" v={`${price.bandwidthCost.toFixed(4)} ${price.currency}/hr`} />
                    {price.gpuCost > 0 && <Row k="GPU" v={`${price.gpuCost.toFixed(4)} ${price.currency}/hr`} />}
                    {price.replicationCost > 0 && <Row k="Replication" v={`${price.replicationCost.toFixed(4)} ${price.currency}/hr`} />}
                    <Row k="Total" v={`${price.hourlyTotal.toFixed(4)} ${price.currency}/hr`} />
                  </div>
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