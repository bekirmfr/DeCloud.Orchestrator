import type { VmTemplate, TemplateSpec } from "../deploy/deploySubmit";

// Pure mapping between the authoring form and the API. Create posts
// CreateTemplateRequest; edit puts the full VmTemplate. Enums go on the wire as
// numbers (GpuMode 0/1/2, Visibility Public=0/Private=1, Pricing Free=0/
// PerDeploy=1, Bandwidth Basic=0..Unmetered=3). Specs are VmSpec bytes.
// Artifacts are NOT here — they have a dedicated /templates/{id}/artifacts
// endpoint and are managed separately.

export const GPU_MODES: [number, string][] = [[0, "None"], [1, "Passthrough (dedicated)"], [2, "Proxied (shared)"]];
export const BANDWIDTH_TIERS: [number, string][] = [[0, "Basic"], [1, "Standard"], [2, "Performance"], [3, "Unmetered"]];
export const VISIBILITIES: [number, string][] = [[0, "Public"], [1, "Private"]];
export const PRICING_MODELS: [number, string][] = [[0, "Free"], [1, "Per deploy (one-time fee)"]];

const NAME_TO_NUM: Record<string, number> = { public: 0, private: 1, free: 0, perdeploy: 1, none: 0, passthrough: 1, proxied: 2, basic: 0, standard: 1, performance: 2, unmetered: 3 };
/** Enum value from the wire (number or name) → number. */
export function enumNum(v: string | number | undefined, fallback: number): number {
  if (typeof v === "number") return v;
  if (typeof v === "string") { const n = NAME_TO_NUM[v.toLowerCase()]; return n ?? fallback; }
  return fallback;
}

export interface EnvRow { key: string; value: string }
export interface PortRow { port: string; protocol: string; description: string; isPublic: boolean }
export interface VarRow { name: string; description: string; defaultValue: string; required: boolean }

export interface TemplateForm {
  name: string; slug: string; description: string; longDescription: string;
  category: string; version: string; tags: string; iconUrl: string;
  authorName: string; authorRevenueWallet: string; license: string; sourceUrl: string;
  recCpu: number; recMemMb: number; recDiskGb: number; recImageId: string;
  minCpu: number; minMemMb: number; minDiskGb: number;
  requiresGpu: boolean; defaultGpuMode: number; gpuRequirement: string; defaultBandwidthTier: number;
  containerImage: string; cloudInitTemplate: string;
  defaultAccessUrl: string; defaultUsername: string; useGeneratedPassword: boolean;
  visibility: number; pricingModel: number; templatePrice: number; estimatedCostPerHour: number;
  envVars: EnvRow[]; ports: PortRow[]; variables: VarRow[];
}

export const emptyForm: TemplateForm = {
  name: "", slug: "", description: "", longDescription: "",
  category: "", version: "1.0.0", tags: "", iconUrl: "",
  authorName: "", authorRevenueWallet: "", license: "", sourceUrl: "",
  recCpu: 1, recMemMb: 1024, recDiskGb: 10, recImageId: "",
  minCpu: 1, minMemMb: 512, minDiskGb: 10,
  requiresGpu: false, defaultGpuMode: 0, gpuRequirement: "", defaultBandwidthTier: 3,
  containerImage: "", cloudInitTemplate: "",
  defaultAccessUrl: "", defaultUsername: "", useGeneratedPassword: true,
  visibility: 0, pricingModel: 0, templatePrice: 0, estimatedCostPerHour: 0,
  envVars: [], ports: [], variables: [],
};

const mb = (bytes?: number | null) => (bytes ? Math.round(bytes / 1024 ** 2) : 0);
const gb = (bytes?: number | null) => (bytes ? Math.round(bytes / 1024 ** 3) : 0);

/** VmTemplate → form (edit prefill). */
export function fromTemplate(t: VmTemplate): TemplateForm {
  const rec = t.recommendedSpec, min = t.minimumSpec;
  return {
    name: t.name ?? "", slug: t.slug ?? "", description: t.description ?? "", longDescription: t.longDescription ?? "",
    category: t.category ?? "", version: t.version ?? "1.0.0", tags: (t.tags ?? []).join(", "), iconUrl: t.iconUrl ?? "",
    authorName: t.authorName ?? "", authorRevenueWallet: t.authorRevenueWallet ?? "", license: t.license ?? "", sourceUrl: t.sourceUrl ?? "",
    recCpu: rec?.virtualCpuCores ?? 1, recMemMb: mb(rec?.memoryBytes) || 1024, recDiskGb: gb(rec?.diskBytes) || 10, recImageId: rec?.imageId ?? "",
    minCpu: min?.virtualCpuCores ?? 1, minMemMb: mb(min?.memoryBytes) || 512, minDiskGb: gb(min?.diskBytes) || 10,
    requiresGpu: t.requiresGpu ?? false, defaultGpuMode: t.defaultGpuMode ?? 0, gpuRequirement: t.gpuRequirement ?? "", defaultBandwidthTier: t.defaultBandwidthTier ?? 3,
    containerImage: t.containerImage ?? "", cloudInitTemplate: t.cloudInitTemplate ?? "",
    defaultAccessUrl: t.defaultAccessUrl ?? "", defaultUsername: t.defaultUsername ?? "", useGeneratedPassword: t.useGeneratedPassword ?? true,
    visibility: enumNum(t.visibility, 0), pricingModel: enumNum(t.pricingModel, 0), templatePrice: t.templatePrice ?? 0, estimatedCostPerHour: t.estimatedCostPerHour ?? 0,
    envVars: Object.entries(t.defaultEnvironmentVariables ?? {}).map(([key, value]) => ({ key, value })),
    ports: (t.exposedPorts ?? []).map((p) => ({ port: String(p.port), protocol: p.protocol ?? "tcp", description: p.description ?? "", isPublic: p.isPublic ?? false })),
    variables: (t.variables ?? []).map((v) => ({ name: v.name, description: v.description ?? "", defaultValue: v.defaultValue ?? "", required: v.required ?? false })),
  };
}

function recSpec(f: TemplateForm): TemplateSpec {
  return { virtualCpuCores: f.recCpu, memoryBytes: f.recMemMb * 1024 ** 2, diskBytes: f.recDiskGb * 1024 ** 3, imageId: f.recImageId || undefined, gpuMode: f.defaultGpuMode, requiresGpu: f.requiresGpu };
}
function minSpec(f: TemplateForm): TemplateSpec {
  return { virtualCpuCores: f.minCpu, memoryBytes: f.minMemMb * 1024 ** 2, diskBytes: f.minDiskGb * 1024 ** 3 };
}
const clean = (s: string) => (s.trim() ? s.trim() : undefined);

/** Form → CreateTemplateRequest (create) / VmTemplate field subset (edit merge). */
export function toPayload(f: TemplateForm): Record<string, unknown> {
  return {
    name: f.name.trim(), slug: f.slug.trim(), description: f.description.trim(), longDescription: clean(f.longDescription),
    category: f.category.trim(), version: f.version.trim() || "1.0.0",
    tags: f.tags.split(",").map((t) => t.trim()).filter(Boolean),
    iconUrl: clean(f.iconUrl), authorName: clean(f.authorName), authorRevenueWallet: clean(f.authorRevenueWallet),
    license: clean(f.license), sourceUrl: clean(f.sourceUrl),
    recommendedSpec: recSpec(f), minimumSpec: minSpec(f),
    requiresGpu: f.requiresGpu, defaultGpuMode: f.defaultGpuMode, gpuRequirement: clean(f.gpuRequirement), defaultBandwidthTier: f.defaultBandwidthTier,
    containerImage: clean(f.containerImage), cloudInitTemplate: f.cloudInitTemplate,
    defaultEnvironmentVariables: Object.fromEntries(f.envVars.filter((e) => e.key.trim()).map((e) => [e.key.trim(), e.value])),
    exposedPorts: f.ports.filter((p) => p.port.trim()).map((p) => ({ port: Number(p.port), protocol: p.protocol || "tcp", description: p.description, isPublic: p.isPublic })),
    defaultAccessUrl: clean(f.defaultAccessUrl), defaultUsername: clean(f.defaultUsername), useGeneratedPassword: f.useGeneratedPassword,
    visibility: f.visibility, pricingModel: f.pricingModel, templatePrice: f.templatePrice, estimatedCostPerHour: f.estimatedCostPerHour,
    variables: f.variables.filter((v) => v.name.trim()).map((v) => ({ name: v.name.trim(), description: clean(v.description), defaultValue: clean(v.defaultValue), required: v.required })),
  };
}
