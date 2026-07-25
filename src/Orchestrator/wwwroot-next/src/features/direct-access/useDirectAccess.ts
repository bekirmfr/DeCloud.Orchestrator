import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import { protocolOrdinal, type PortProtocol } from "./portProtocol";

// Server data = TanStack Query, keyed by resource (DESIGN §6.3). Endpoints
// GROUNDED against Controllers/VmDirectAccessController.cs (Smart Port Allocation):
//   GET    /api/vms/{id}/direct-access             → DirectAccessInfoResponse (404 if not configured)
//   POST   /api/vms/{id}/direct-access/ports       { vmPort, protocol:<ORDINAL>, label? } → AllocatePortResponse
//   DELETE /api/vms/{id}/direct-access/ports/{port}→ 204 No Content (404 if absent)
//   POST   /api/vms/{id}/direct-access/quick-add   { serviceName } → AllocatePortResponse
//   GET    /api/vms/{id}/direct-access/services    → DirectAccessServiceInfo[]
// `protocol` is the numeric ordinal on BOTH read and write (see portProtocol.ts).

// Wire shapes carry protocol as a number; the UI normalizes it at render.
export interface PortMappingInfo {
  id: string;
  vmPort: number;
  publicPort: number;
  protocol: number | string; // PortProtocol on the wire — normalize before display
  label?: string | null;
  connectionExample: string; // e.g. "mysql -h myvm-abc1.direct.stackfi.tech -P 42157"
}

export interface DirectAccessInfo {
  dnsName: string;
  portMappings: PortMappingInfo[];
  isDnsConfigured: boolean;
}

export interface DirectAccessServiceInfo {
  name: string; // "minecraft", "mysql", …
  port: number;
  protocol: number | string;
  label: string;
}

const key = (id: string) => ["vm-direct-access", id] as const;

/**
 * 404 here means "direct access not configured yet for this VM" — a normal empty
 * state, not an error. Treat it as an empty config so the modal can offer quick-add
 * instead of showing a failure. Other errors still surface.
 */
export function useDirectAccessInfo(api: Api, vmId: string) {
  return useQuery({
    queryKey: key(vmId),
    queryFn: async (): Promise<DirectAccessInfo> => {
      try {
        return await api<DirectAccessInfo>(`/api/vms/${vmId}/direct-access`);
      } catch (e) {
        if ((e as { status?: number })?.status === 404) {
          return { dnsName: "", portMappings: [], isDnsConfigured: false };
        }
        throw e;
      }
    },
    enabled: !!vmId,
  });
}

/** The platform's quick-add catalogue (SSH, MySQL, Minecraft, …). Rarely changes. */
export function useAvailableServices(api: Api, vmId: string) {
  return useQuery({
    queryKey: ["direct-access-services", vmId],
    queryFn: () => api<DirectAccessServiceInfo[]>(`/api/vms/${vmId}/direct-access/services`),
    enabled: !!vmId,
    staleTime: 60 * 60_000, // 1h — the catalogue is effectively static
  });
}

export interface AllocatePortResponse {
  mappingId: string;
  vmPort: number;
  publicPort: number;
  protocol: number | string;
  connectionString: string;
  success: boolean;
  error?: string | null;
}

export function useAllocatePort(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { vmPort: number; protocol: PortProtocol; label?: string | null }) =>
      api<AllocatePortResponse>(`/api/vms/${vmId}/direct-access/ports`, {
        method: "POST",
        // Ordinal, not name — PortProtocol has no string converter server-side.
        body: JSON.stringify({
          vmPort: input.vmPort,
          protocol: protocolOrdinal(input.protocol),
          label: input.label ?? null,
        }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}

export function useQuickAddService(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (serviceName: string) =>
      api<AllocatePortResponse>(`/api/vms/${vmId}/direct-access/quick-add`, {
        method: "POST",
        body: JSON.stringify({ serviceName }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}

export function useRemovePort(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    // Returns 204 No Content on success — api() unwraps that to undefined.
    mutationFn: (vmPort: number) =>
      api<void>(`/api/vms/${vmId}/direct-access/ports/${vmPort}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}
