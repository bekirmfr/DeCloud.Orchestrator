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
