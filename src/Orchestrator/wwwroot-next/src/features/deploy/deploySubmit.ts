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
}

export interface TemplateVariable {
  name: string;
  description?: string;
  defaultValue?: string;
  required?: boolean;
  kind?: string;                // platform vars are hidden; user vars shown
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
