import type { Api } from "../../api/client";

// The ONE deploy submission path, ported from the legacy deploy-submit.js
// ("if you find yourself copying the fetch into a new form, stop"). Any deploy
// UI (one-click, customize) builds its payload and hands it here. GROUNDED
// against MarketplaceController + deploy-submit.js.

// ── Legacy types (grounded) ──────────────────────────────────────────────
export interface TemplateSpec {
  virtualCpuCores: number;
  memoryBytes: number;
  diskBytes: number;
  imageId?: string;
  gpuMode?: number;
  gpuVramBytes?: number;
  requiresGpu?: boolean;
  qualityTier?: number;
  bandwidthTier?: number;
  replicationFactor?: number;
  constraints?: Constraint[];
}

/** A scheduling constraint: hard AND-filter over candidate nodes. Value type
 *  depends on (target type, operator) — scalar or list; the server validates. */
export interface Constraint {
  target: string;
  operator: string;
  value: unknown;
}

/** GET /api/vms/constraint-vocabulary — drives the builder so targets/operators
 *  are never hardcoded client-side. operatorTargetTypes maps each operator to
 *  the value-type names it accepts, matching server validation exactly. */
export interface ConstraintVocabulary {
  targets: string[];
  targetTypes: Record<string, string>;              // target → "String" | "Numeric" | "Boolean" | "StringList"
  operators: string[];
  operatorTargetTypes: Record<string, string[]>;    // operator → accepted type names
}

export interface ServiceCheck {
  strategy: number;          // CheckStrategy: 0=TcpPort, 1=HttpGet, 2=ExecCommand
  httpPath?: string;         // for HttpGet
  execCommand?: string;      // for ExecCommand (via qemu-guest-agent)
  timeoutSeconds?: number;   // default 300
  livenessCheck?: boolean;   // periodic re-check after Ready
}

export interface TemplatePort {
  port: number;
  protocol: string;
  description?: string;
  isPublic?: boolean;
  readinessCheck?: ServiceCheck;
}

export interface TemplateArtifact {
  name: string;
  type: number;              // ArtifactType 0..5
  description?: string;
  architecture?: string | null; // null=any, "amd64", "arm64"
  sourceUrl?: string;        // https URL, or a data: URI for inline
  sha256?: string;           // required for external; server computes for inline
  content?: string;          // raw text; server wraps as data:{contentType};base64,…
  contentType?: string;
  sizeBytes?: number;
}

export interface VmTemplate {
  id: string;
  slug: string;
  name: string;
  description?: string;
  longDescription?: string;
  category?: string;
  iconUrl?: string;
  minimumSpec?: TemplateSpec | null;
  recommendedSpec?: TemplateSpec | null;
  pricingModel?: string;        // "Free" | "PerDeploy"
  templatePrice?: number;
  estimatedCostPerHour?: number;
  requiresGpu?: boolean;
  defaultGpuMode?: number;
  // Forwarded explicitly when a customSpec is sent: the server applies these
  // template defaults ONLY when customSpec is null (BuildVmRequestFromTemplateAsync),
  // so customising without them silently downgrades bandwidth/GPU.
  defaultBandwidthTier?: number;
  variables?: TemplateVariable[];
  status?: string | number;
  // ── authoring fields (present on the full VmTemplate; My Templates + edit) ──
  version?: string;
  tags?: string[];
  authorName?: string;
  authorRevenueWallet?: string;
  license?: string;
  sourceUrl?: string;
  gpuRequirement?: string;
  containerImage?: string;
  cloudInitTemplate?: string;
  defaultEnvironmentVariables?: Record<string, string>;
  exposedPorts?: TemplatePort[];
  artifacts?: TemplateArtifact[];
  defaultAccessUrl?: string;
  defaultUsername?: string;
  useGeneratedPassword?: boolean;
  visibility?: string | number;
}

export interface TemplateVariable {
  name: string;
  description?: string;
  defaultValue?: string;
  required?: boolean;
  // VariableKind ("Static" | "Dynamic") — resolution TIMING, not a user/platform
  // flag. Platform-vs-user is NOT this field: a variable is platform-managed if
  // its name is a resolver key from GET /api/marketplace/platform-variables, and
  // user-facing (must be collected at deploy) only if it has no resolver.
  kind?: string | number;
  // WatcherScope (Noop|Reload|Restart) — Dynamic-only reaction when the value
  // changes at runtime. Ignored for statics.
  scope?: string | number;
  // Override the resolver key used to look up the value; defaults to name.
  resolverKey?: string;
}

export interface VmImage {
  id: string;
  name: string;
  description?: string;
  osFamily?: string;   // "linux" | "windows" — server orders the list by this
  osName?: string;     // "ubuntu", "debian", …
  version?: string;
}

export interface DeployPayload {
  vmName: string;
  environmentVariables?: Record<string, string>;
  customSpec?: TemplateSpec | null;   // omit/null → server uses RecommendedSpec
}

// CreateVmResponse (positional record on the wire → camelCase fields).
export interface DeployResult {
  vmId: string;
  status?: string | number;
  message?: string;
  error?: string;
  password?: string;            // present on success for generated-password templates
}

/**
 * Resolve a template by slug OR id. The deploy endpoint takes IDs only
 * (GetTemplateByIdAsync); GET /templates/{slugOrId} accepts both — so a slug
 * costs one extra GET. Returns the template doc.
 */
export async function resolveTemplate(api: Api, slugOrId: string): Promise<VmTemplate> {
  return api<VmTemplate>(`/api/marketplace/templates/${encodeURIComponent(slugOrId)}`);
}

/** Public VM image catalogue. GET /api/system/images (AllowAnonymous), already
 *  ordered by OsFamily then Name and filtered to IsPublic on the server. */
export async function fetchImages(api: Api): Promise<VmImage[]> {
  return api<VmImage[]>("/api/system/images");
}

/** Scheduling-constraint vocabulary. GET /api/vms/constraint-vocabulary. */
export async function fetchConstraintVocabulary(api: Api): Promise<ConstraintVocabulary> {
  return api<ConstraintVocabulary>("/api/vms/constraint-vocabulary");
}

/**
 * Submit a template deploy. POST /api/marketplace/templates/{id}/deploy.
 * ToS-retry-once: if the deploy is blocked and the (bridged) ToS gate resolves
 * acceptance, retry exactly once. See DEPLOY_MIGRATION.md for the window bridge.
 */
export async function submitTemplateDeploy(
  api: Api,
  templateId: string,
  payload: DeployPayload,
  opts: { retried?: boolean } = {}
): Promise<DeployResult> {
  try {
    return await api<DeployResult>(`/api/marketplace/templates/${templateId}/deploy`, {
      method: "POST",
      body: JSON.stringify({
        vmName: payload.vmName,
        environmentVariables: payload.environmentVariables || {},
        customSpec: payload.customSpec ?? null,
      }),
    });
  } catch (e) {
    // ── LEGACY BRIDGE (v1, tracked debt — see DEPLOY_MIGRATION.md) ──────────
    // ToS acceptance can lapse on a version bump and block the deploy. The
    // acceptance flow (wallet signature) is not yet ported to React; we reuse
    // the legacy global. Retry EXACTLY once, guarded, matching deploy-submit.js.
    // If the bridge is absent (legacy bundle not loaded), we do NOT silently
    // swallow — we rethrow the original error so the user sees a real message.
    type TosBridge = { handleDeployTosGate?: () => Promise<boolean> };
    const bridge = window as unknown as TosBridge;
    if (!opts.retried && typeof bridge.handleDeployTosGate === "function") {
      const accepted = await bridge.handleDeployTosGate();
      if (accepted) return submitTemplateDeploy(api, templateId, payload, { retried: true });
    }
    throw e;
  }
}

/**
 * Should the one-time password be revealed? Ported VERBATIM from the legacy
 * afterDeploySuccess sniff: reveal only for the human-readable *memorable*
 * format (contains '-', no '_'). System/other formats are not surfaced.
 */
export function shouldRevealPassword(password: string | undefined): password is string {
  return !!password && !password.includes("_") && password.includes("-");
}
