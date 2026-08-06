import { useQuery } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import { sameAddress } from "../../auth/deriveStatus";

// Phase 5 · Nodes. Two user-facing views over the node fleet:
//   • My nodes  — GET /api/nodes (whole fleet) filtered to the caller's wallet.
//                 No dedicated "my nodes" endpoint exists; Node.walletAddress is
//                 the owner key, matched case-insensitively (checksum vs lower).
//   • Search    — GET /api/nodes/search → NodeAdvertisement (public marketplace
//                 view), with region / GPU / online / sort filters.

export interface NodeResources {
  computePoints?: number;
  memoryBytes?: number;
  storageBytes?: number;
  gpuVramBytes?: number;
}

export interface NodeLocalityInfo {
  region?: string;
  country?: string;
  zone?: string;
  jurisdictionTags?: string[];
  locationMismatch?: boolean;
}

// Subset of the backend Node model we actually render (owner-facing).
export interface OrchNode {
  id: string;
  name: string;
  walletAddress: string;
  description?: string;
  status?: string | number;        // NodeStatus
  isSchedulingReady?: boolean;
  publicIp?: string;
  agentPort?: number;
  agentVersion?: string;
  architecture?: string;
  registeredAt?: string;
  lastHeartbeat?: string;
  uptimePercentage?: number;
  totalVmsHosted?: number;
  successfulVmCompletions?: number;
  pendingPayout?: number;
  totalEarned?: number;
  totalResources?: NodeResources;
  usedResources?: NodeResources;
  allocatedResources?: NodeResources;
  reservedResources?: NodeResources;
  locality?: NodeLocalityInfo;
  isBehindCgnat?: boolean;
  tags?: string[];
  // Role participation — presence (non-null) is what we render; shapes live in Shared.
  relayInfo?: unknown;
  dhtInfo?: unknown;
  blockStoreInfo?: unknown;
}

export interface NodeCapabilities {
  hasGpu?: boolean;
  gpuModel?: string | null;
  gpuCount?: number | null;
  cpuCores?: number;
  cpuModel?: string;
  hasNvmeStorage?: boolean;
  highBandwidth?: boolean;
}

// Public marketplace advertisement returned by /search.
export interface NodeAdvertisement {
  nodeId: string;
  operatorName?: string;
  description?: string;
  region?: string;
  zone?: string;
  country?: string;
  tags?: string[];
  capabilities?: NodeCapabilities;
  uptimePercentage?: number;
  totalVmsHosted?: number;
  successfulVmCompletions?: number;
  registeredAt?: string;
  isOnline?: boolean;
  schedulingReady?: boolean;
}

// NodeStatus: Offline=0, Online=1, Maintenance=2, Draining=3, Suspended=4.
const STATUS: Record<string, { label: string; tone: string }> = {
  offline: { label: "Offline", tone: "var(--text-tertiary)" },
  online: { label: "Online", tone: "var(--success)" },
  maintenance: { label: "Maintenance", tone: "var(--warning)" },
  draining: { label: "Draining", tone: "var(--warning)" },
  suspended: { label: "Suspended", tone: "var(--danger)" },
};
const NUM: Record<number, string> = { 0: "offline", 1: "online", 2: "maintenance", 3: "draining", 4: "suspended" };

export function nodeStatus(status?: string | number): { label: string; tone: string } {
  const key = typeof status === "number" ? (NUM[status] ?? "") : String(status ?? "").toLowerCase();
  return STATUS[key] ?? { label: String(status ?? "—"), tone: "var(--text-secondary)" };
}

export function useMyNodes(api: Api, wallet?: string) {
  return useQuery({
    queryKey: ["nodes", "all"],
    queryFn: () => api<OrchNode[]>("/api/nodes"),
    select: (all) => (wallet ? all.filter((n) => sameAddress(n.walletAddress, wallet)) : []),
    enabled: !!wallet,
  });
}

// Single node detail — GET /api/nodes/{id} returns the full Node ([Authorize]).
export function useNode(api: Api, id: string) {
  return useQuery({
    queryKey: ["node", id],
    queryFn: () => api<OrchNode>(`/api/nodes/${id}`),
    enabled: !!id,
  });
}

export interface NodeSearchCriteria {
  region: string;
  requiresGpu: boolean;
  onlineOnly: boolean;
  sortBy: string;         // uptime | vms | registered
}

export const SORT_OPTIONS: [string, string][] = [
  ["uptime", "Uptime"],
  ["vms", "VMs hosted"],
  ["registered", "Newest"],
];

export function useNodeSearch(api: Api, c: NodeSearchCriteria, enabled: boolean) {
  return useQuery({
    queryKey: ["nodes", "search", c],
    queryFn: () => {
      const qs = new URLSearchParams();
      if (c.region.trim()) qs.set("region", c.region.trim());
      if (c.requiresGpu) qs.set("requiresGpu", "true");
      if (c.onlineOnly) qs.set("onlineOnly", "true");
      qs.set("sortBy", c.sortBy);
      return api<NodeAdvertisement[]>(`/api/nodes/search?${qs.toString()}`);
    },
    enabled,
  });
}
