import type { VmTemplate, TemplateSpec, ServiceCheck } from "../deploy/deploySubmit";

// Pure mapping between the authoring form and the API. Create posts
// CreateTemplateRequest; edit puts the full VmTemplate. Enums go on the wire as
// numbers (accepted even where the backend serializes them as names). Specs are
// VmSpec bytes. Artifacts are AddArtifactRequest rows (inline data: URI or
// external HTTPS+SHA256), built here and sent in CreateTemplateRequest.artifacts.

export const GPU_MODES: [number, string][] = [[0, "None"], [1, "Passthrough (dedicated)"], [2, "Proxied (shared)"]];
export const BANDWIDTH_TIERS: [number, string][] = [[0, "Basic"], [1, "Standard"], [2, "Performance"], [3, "Unmetered"]];
export const VISIBILITIES: [number, string][] = [[0, "Public"], [1, "Private"]];
export const PRICING_MODELS: [number, string][] = [[0, "Free"], [1, "Per deploy (one-time fee)"]];
// QualityTier is INVERTED: 0 = best. MinimumSpec.QualityTier is the worst tier allowed.
export const QUALITY_TIERS: [number, string][] = [[0, "Guaranteed (best)"], [1, "Standard"], [2, "Balanced"], [3, "Burstable (cheapest)"]];
export const VARIABLE_KINDS: [number, string][] = [[0, "Static (resolved at render)"], [1, "Dynamic (resolved at runtime)"]];
export const WATCHER_SCOPES: [number, string][] = [[0, "Noop (refresh only)"], [1, "Reload (SIGHUP)"], [2, "Restart service"]];
export const ARTIFACT_TYPES: [number, string][] = [[0, "Binary"], [1, "Script"], [2, "Web asset"], [3, "Config"], [4, "Archive"], [5, "Image"]];
export const CHECK_STRATEGIES: [number, string][] = [[0, "TCP port"], [1, "HTTP GET"], [2, "Exec command"]];
export const PROTOCOLS: string[] = ["http", "https", "tcp", "udp", "ws", "wss"];
export const ARCHITECTURES: [string, string][] = [["", "Any"], ["amd64", "amd64"], ["arm64", "arm64"]];
// Registry image IDs → RecommendedSpec.ImageId (the OS boot image). Empty =
// OS-agnostic; the platform default (ubuntu-22.04) is applied at deploy.
export const IMAGE_OPTIONS: [string, string][] = [["ubuntu-22.04", "Ubuntu 22.04"], ["ubuntu-24.04", "Ubuntu 24.04"], ["debian-12", "Debian 12"], ["", "OS-agnostic (platform default)"]];
// Allowed marketplace categories (value → label), matching the legacy set.
export const CATEGORIES: [string, string][] = [["", "Select a category…"], ["gaming", "Games"], ["dev-tools", "Dev Tools"], ["ai-ml", "AI/ML"], ["databases", "Databases"], ["web-apps", "Web Apps"], ["privacy-security", "Privacy & Security"]];

const NAME_TO_NUM: Record<string, number> = {
  public: 0, private: 1, free: 0, perdeploy: 1,
  none: 0, passthrough: 1, proxied: 2,
  basic: 0, standard: 1, performance: 2, unmetered: 3,
  guaranteed: 0, balanced: 2, burstable: 3,
  static: 0, dynamic: 1, noop: 0, reload: 1, restart: 2,
  binary: 0, script: 1, webasset: 2, config: 3, archive: 4, image: 5,
  tcpport: 0, httpget: 1, execcommand: 2,
};
/** Enum value from the wire (number or name) → number. */
export function enumNum(v: string | number | undefined | null, fallback: number): number {
  if (typeof v === "number") return v;
  if (typeof v === "string") { const n = NAME_TO_NUM[v.toLowerCase()]; return n ?? fallback; }
  return fallback;
}

export interface EnvRow { key: string; value: string }
export interface PortRow {
  port: string; protocol: string; description: string; isPublic: boolean;
  hasReadiness: boolean; strategy: number; httpPath: string; execCommand: string; timeoutSeconds: number; livenessCheck: boolean;
}
export interface VarRow {
  name: string; kind: number; scope: number; defaultValue: string; required: boolean; description: string; resolverKey: string;
}
export type ArtifactMode = "external" | "inlineText" | "inlineFile";
export interface ArtifactRow {
  name: string; type: number; description: string; architecture: string;
  mode: ArtifactMode; sourceUrl: string; sha256: string; content: string; contentType: string; fileName: string;
}

export interface TemplateForm {
  name: string; slug: string; description: string; longDescription: string;
  category: string; version: string; tags: string; iconUrl: string;
  authorName: string; authorRevenueWallet: string; license: string; sourceUrl: string;
  recCpu: number; recMemMb: number; recDiskGb: number; recImageId: string; recGpuVramGb: number;
  minCpu: number; minMemMb: number; minDiskGb: number; minQualityTier: number;
  requiresGpu: boolean; defaultGpuMode: number; gpuRequirement: string; defaultBandwidthTier: number;
  containerImage: string; cloudInitTemplate: string;
  defaultAccessUrl: string; defaultUsername: string; useGeneratedPassword: boolean;
  visibility: number; pricingModel: number; templatePrice: number; estimatedCostPerHour: number;
  envVars: EnvRow[]; ports: PortRow[]; variables: VarRow[]; artifacts: ArtifactRow[];
}

export const emptyVar: VarRow = { name: "", kind: 0, scope: 2, defaultValue: "", required: false, description: "", resolverKey: "" };
export const emptyPort: PortRow = { port: "", protocol: "http", description: "", isPublic: true, hasReadiness: false, strategy: 0, httpPath: "", execCommand: "", timeoutSeconds: 300, livenessCheck: false };
export const emptyArtifact: ArtifactRow = { name: "", type: 1, description: "", architecture: "", mode: "external", sourceUrl: "", sha256: "", content: "", contentType: "", fileName: "" };

export const emptyForm: TemplateForm = {
  name: "", slug: "", description: "", longDescription: "",
  category: "", version: "1.0.0", tags: "", iconUrl: "",
  authorName: "", authorRevenueWallet: "", license: "", sourceUrl: "",
  recCpu: 1, recMemMb: 1024, recDiskGb: 10, recImageId: "ubuntu-22.04", recGpuVramGb: 0,
  minCpu: 1, minMemMb: 512, minDiskGb: 10, minQualityTier: 1,
  requiresGpu: false, defaultGpuMode: 0, gpuRequirement: "", defaultBandwidthTier: 3,
  containerImage: "", cloudInitTemplate: "",
  defaultAccessUrl: "", defaultUsername: "", useGeneratedPassword: true,
  visibility: 0, pricingModel: 0, templatePrice: 0, estimatedCostPerHour: 0,
  envVars: [], ports: [], variables: [], artifacts: [],
};

const mb = (bytes?: number | null) => (bytes ? Math.round(bytes / 1024 ** 2) : 0);
const gb = (bytes?: number | null) => (bytes ? Math.round(bytes / 1024 ** 3) : 0);

/**
 * Platform-managed variables injected by the server when it composes an authored
 * template over base-tenant. They round-trip on the stored template but are not
 * author-authored, so the Variables editor hides them (mirrors the deploy form
 * hiding platform-bound vars). Compose re-attaches them on save, so stripping
 * them here is safe.
 */
const BASE_TENANT_VAR_NAMES = new Set([
    "VM_ID", "VM_NAME", "HOSTNAME", "ORCHESTRATOR_URL", "CA_PUBLIC_KEY",
    "SSH_AUTHORIZED_KEYS_BLOCK", "PASSWORD_CONFIG_BLOCK", "ADMIN_PASSWORD", "SSH_PASSWORD_AUTH",
]);

/** VmTemplate → form (edit prefill). */
export function fromTemplate(t: VmTemplate): TemplateForm {
  const rec = t.recommendedSpec, min = t.minimumSpec;
  return {
    name: t.name ?? "", slug: t.slug ?? "", description: t.description ?? "", longDescription: t.longDescription ?? "",
    category: t.category ?? "", version: t.version ?? "1.0.0", tags: (t.tags ?? []).join(", "), iconUrl: t.iconUrl ?? "",
    authorName: t.authorName ?? "", authorRevenueWallet: t.authorRevenueWallet ?? "", license: t.license ?? "", sourceUrl: t.sourceUrl ?? "",
    recCpu: rec?.virtualCpuCores ?? 1, recMemMb: mb(rec?.memoryBytes) || 1024, recDiskGb: gb(rec?.diskBytes) || 10, recImageId: rec?.imageId ?? "", recGpuVramGb: gb(rec?.gpuVramBytes),
    minCpu: min?.virtualCpuCores ?? 1, minMemMb: mb(min?.memoryBytes) || 512, minDiskGb: gb(min?.diskBytes) || 10, minQualityTier: enumNum(min?.qualityTier, 1),
    requiresGpu: t.requiresGpu ?? false, defaultGpuMode: enumNum(t.defaultGpuMode, 0), gpuRequirement: t.gpuRequirement ?? "", defaultBandwidthTier: enumNum(t.defaultBandwidthTier, 3),
    containerImage: t.containerImage ?? "", cloudInitTemplate: t.roleCloudInit ?? t.cloudInitTemplate ?? "",
    defaultAccessUrl: t.defaultAccessUrl ?? "", defaultUsername: t.defaultUsername ?? "", useGeneratedPassword: t.useGeneratedPassword ?? true,
    visibility: enumNum(t.visibility, 0), pricingModel: enumNum(t.pricingModel, 0), templatePrice: t.templatePrice ?? 0, estimatedCostPerHour: t.estimatedCostPerHour ?? 0,
    envVars: Object.entries(t.defaultEnvironmentVariables ?? {}).map(([key, value]) => ({ key, value })),
    ports: (t.exposedPorts ?? []).map((p) => ({
      port: String(p.port), protocol: p.protocol ?? "http", description: p.description ?? "", isPublic: p.isPublic ?? true,
      hasReadiness: !!p.readinessCheck, strategy: enumNum(p.readinessCheck?.strategy, 0), httpPath: p.readinessCheck?.httpPath ?? "",
      execCommand: p.readinessCheck?.execCommand ?? "", timeoutSeconds: p.readinessCheck?.timeoutSeconds ?? 300, livenessCheck: p.readinessCheck?.livenessCheck ?? false,
    })),
      variables: (t.variables ?? []).filter((v) => !BASE_TENANT_VAR_NAMES.has(v.name)).map((v) => ({
      name: v.name, kind: enumNum(v.kind, 0), scope: enumNum(v.scope, 2),
      defaultValue: v.defaultValue ?? "", required: v.required ?? false, description: v.description ?? "", resolverKey: v.resolverKey ?? "",
    })),
    artifacts: (t.artifacts ?? []).map((a) => {
      const inline = (a.sourceUrl ?? "").startsWith("data:");
      return {
        name: a.name, type: enumNum(a.type, 1), description: a.description ?? "", architecture: a.architecture ?? "",
        mode: (inline ? "inlineFile" : "external") as ArtifactMode, sourceUrl: a.sourceUrl ?? "", sha256: a.sha256 ?? "",
        content: "", contentType: a.contentType ?? "", fileName: "",
      };
    }),
  };
}

function recSpec(f: TemplateForm): TemplateSpec {
  return { virtualCpuCores: f.recCpu, memoryBytes: f.recMemMb * 1024 ** 2, diskBytes: f.recDiskGb * 1024 ** 3, imageId: f.recImageId || undefined, gpuMode: f.requiresGpu ? f.defaultGpuMode : 0, requiresGpu: f.requiresGpu, gpuVramBytes: f.requiresGpu && f.recGpuVramGb > 0 ? f.recGpuVramGb * 1024 ** 3 : undefined, qualityTier: f.minQualityTier };
}
function minSpec(f: TemplateForm): TemplateSpec {
  return { virtualCpuCores: f.minCpu, memoryBytes: f.minMemMb * 1024 ** 2, diskBytes: f.minDiskGb * 1024 ** 3, qualityTier: f.minQualityTier };
}
const clean = (s: string) => (s.trim() ? s.trim() : undefined);

function portPayload(p: PortRow): Record<string, unknown> {
  const base: Record<string, unknown> = { port: Number(p.port), protocol: p.protocol || "tcp", description: p.description, isPublic: p.isPublic };
  if (p.hasReadiness) {
    const rc: ServiceCheck = { strategy: p.strategy, timeoutSeconds: p.timeoutSeconds || 300, livenessCheck: p.livenessCheck };
    if (p.strategy === 1) rc.httpPath = p.httpPath.trim() || "/";
    if (p.strategy === 2) rc.execCommand = clean(p.execCommand);
    base.readinessCheck = rc;
  }
  return base;
}

function varPayload(v: VarRow): Record<string, unknown> {
  const isStatic = v.kind === 0;
  const out: Record<string, unknown> = { name: v.name.trim(), kind: v.kind, description: clean(v.description), resolverKey: clean(v.resolverKey) };
  if (isStatic) { out.defaultValue = clean(v.defaultValue); out.required = v.required; }
  else { out.scope = v.scope; }
  return out;
}

function artifactPayload(a: ArtifactRow): Record<string, unknown> {
  const base: Record<string, unknown> = { name: a.name.trim(), type: a.type, description: clean(a.description), architecture: a.architecture || undefined };
  if (a.mode === "inlineText") { base.content = a.content; base.contentType = clean(a.contentType); base.sha256 = clean(a.sha256); }
  else { base.sourceUrl = a.sourceUrl.trim(); base.sha256 = clean(a.sha256); } // external OR inlineFile (data: URI already in sourceUrl)
  return base;
}

/** Form → CreateTemplateRequest (create) / VmTemplate field subset (edit merge). */
export function toPayload(f: TemplateForm): Record<string, unknown> {
  return {
    name: f.name.trim(), slug: f.slug.trim().toLowerCase(), description: f.description.trim(), longDescription: clean(f.longDescription),
    category: f.category.trim(), version: f.version.trim() || "1.0.0",
    tags: f.tags.split(",").map((t) => t.trim()).filter(Boolean),
    iconUrl: clean(f.iconUrl), authorName: clean(f.authorName), authorRevenueWallet: clean(f.authorRevenueWallet),
    license: clean(f.license), sourceUrl: clean(f.sourceUrl),
    recommendedSpec: recSpec(f), minimumSpec: minSpec(f),
    requiresGpu: f.requiresGpu, defaultGpuMode: f.requiresGpu ? f.defaultGpuMode : 0, gpuRequirement: clean(f.gpuRequirement), defaultBandwidthTier: f.defaultBandwidthTier,
    containerImage: clean(f.containerImage), roleCloudInit: f.cloudInitTemplate,
    defaultEnvironmentVariables: Object.fromEntries(f.envVars.filter((e) => e.key.trim()).map((e) => [e.key.trim(), e.value])),
    exposedPorts: f.ports.filter((p) => p.port.trim()).map(portPayload),
    defaultAccessUrl: clean(f.defaultAccessUrl), defaultUsername: clean(f.defaultUsername), useGeneratedPassword: f.useGeneratedPassword,
    visibility: f.visibility, pricingModel: f.pricingModel, templatePrice: f.templatePrice, estimatedCostPerHour: f.estimatedCostPerHour,
    variables: f.variables.filter((v) => v.name.trim()).map(varPayload),
    artifacts: f.artifacts.filter((a) => a.name.trim()).map(artifactPayload),
  };
}

/** Client-side guard: RecommendedSpec must meet or exceed MinimumSpec (server rejects otherwise). */
export function specError(f: TemplateForm): string | null {
  if (f.recCpu < f.minCpu) return "Recommended vCPU is below the minimum.";
  if (f.recMemMb < f.minMemMb) return "Recommended memory is below the minimum.";
  if (f.recDiskGb < f.minDiskGb) return "Recommended disk is below the minimum.";
  return null;
}
