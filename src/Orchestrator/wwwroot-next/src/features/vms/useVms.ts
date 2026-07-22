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
