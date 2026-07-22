import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import type { VmStatus, VmPowerState, VmAction } from "./vmStatus";

// Server data = TanStack Query. Endpoints GROUNDED against the ORCHESTRATOR
// VmsController (NOT the NodeAgent one): /api/vms.
//   GET  /api/vms?page=&pageSize=  → ApiResponse<PagedResult<VmSummaryDto>>
//   POST /api/vms/{id}/action { action } → ApiResponse<bool>
//   DELETE /api/vms/{id}            → ApiResponse<bool>

export interface VmSpec {
  virtualCpuCores: number;
  memoryBytes: number;
  diskBytes: number;
  // (gpuMode, qualityTier, etc. exist server-side; add when a view needs them)
}

export interface VmSummary {
  id: string;
  name: string;
  status: VmStatus;
  powerState: VmPowerState;
  nodeId?: string | null;
  nodePublicIp?: string | null;
  spec: VmSpec;
  createdAt: string;
  updatedAt: string;
  templateId?: string | null;
  complianceHold: boolean;
}

export interface PagedResult<T> {
  items: T[];
  totalCount: number;
  page: number;
  pageSize: number;
}

export const VMS_PAGE_SIZE = 20;

// ── Detail shapes (GET /api/vms/{id} → ApiResponse<VmDetailResponse>) ──
// VmDetailResponse returns the FULL VirtualMachine + host Node. We type only the
// owner-facing subset the cockpit renders (not reconciler/migration internals).
export interface VmAccessInfo {
  sshHost?: string | null;
  sshPort?: number;
  vncHost?: string | null;
  vncPort?: number;
  consoleWebSocketUrl?: string | null;
}

export interface VmNetworkConfig {
  privateIp?: string | null;
  publicIp?: string | null;
  hostname?: string | null;
  sshJumpHost?: string | null;
  sshJumpPort?: number | null;
}

export interface VmServiceModel {
  name: string;
  port?: number | null;
  protocol?: string | null;
  status: string | number; // ServiceStatus (may serialize numeric — normalize on render)
  statusMessage?: string | null;
}

export interface VmDetail {
  id: string;
  name: string;
  status: VmStatus | number;
  powerState: VmPowerState | number;
  statusMessage?: string | null;
  complianceHold: boolean;
  nodeId?: string | null;
  spec: VmSpec;
  networkConfig?: VmNetworkConfig | null;
  accessInfo?: VmAccessInfo | null;
  services?: VmServiceModel[] | null;
  subdomainTier?: string | number;
  createdAt: string;
  updatedAt?: string;
  startedAt?: string | null;
  stoppedAt?: string | null;
}

export interface Node {
  id: string;
  publicIp?: string | null;
  hostname?: string | null;
}

export interface VmDetailResponse {
  vm: VmDetail;
  hostNode?: Node | null;
}

export function useVm(api: Api, id: string) {
  return useQuery({
    queryKey: ["vm", id],
    queryFn: () => api<VmDetailResponse>(`/api/vms/${id}`),
    enabled: !!id,
  });
}

// Live metrics. GROUNDED: VmMetrics (SignalR-pushed AND persisted as vm.LatestMetrics,
// exposed at GET /api/vms/{id}/metrics → ApiResponse<VmMetrics>, 404 NO_METRICS if none).
// Strategy: REST seeds the last-known snapshot on load (no blank panel); the SignalR
// VmMetricsUpdated handler patches THIS SAME cache key live (see useVmRealtime).
export interface VmMetrics {
  timestamp: string;
  cpuUsagePercent: number;
  memoryUsagePercent: number;
  diskReadBytes: number;
  diskWriteBytes: number;
  networkRxBytes: number;
  networkTxBytes: number;
}

export function useVmMetrics(api: Api, id: string) {
  return useQuery({
    queryKey: ["vm-metrics", id],
    // 404 NO_METRICS is not an error state for the panel — treat "no metrics yet"
    // as null rather than throwing (the node may simply not be reporting yet).
    queryFn: async () => {
      try {
        return await api<VmMetrics>(`/api/vms/${id}/metrics`);
      } catch {
        return null;
      }
    },
    enabled: !!id,
    // Live updates arrive via SignalR; a slow poll is just a safety net.
    refetchInterval: 60_000,
  });
}

export function useVms(api: Api, page: number) {
  return useQuery({
    queryKey: ["vms", page],
    queryFn: () => api<PagedResult<VmSummary>>(`/api/vms?page=${page}&pageSize=${VMS_PAGE_SIZE}`),
    // Keep the current page on screen while the next loads — no flash to empty.
    placeholderData: keepPreviousData,
  });
}

/** Perform a lifecycle action. Server validates the transition (INVALID_ACTION/VM_HELD). */
export function useVmAction(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, action }: { id: string; action: VmAction }) =>
      api<boolean>(`/api/vms/${id}/action`, { method: "POST", body: JSON.stringify({ action }) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vms"] }),
  });
}

export function useDeleteVm(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<boolean>(`/api/vms/${id}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["vms"] }),
  });
}
