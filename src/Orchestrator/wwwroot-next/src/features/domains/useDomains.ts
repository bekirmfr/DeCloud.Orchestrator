import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import type { DomainStatus } from "./domainStatus";

// Server data = TanStack Query. Endpoints GROUNDED against
// Controllers/CentralIngressController.cs (custom domains ride the central ingress):
//   GET    /api/central-ingress/vm/{id}/domains                     → CustomDomainResponse[]
//   POST   /api/central-ingress/vm/{id}/domains  { domain, targetPort } → CustomDomainResponse
//   POST   /api/central-ingress/vm/{id}/domains/{domainId}/verify   → CustomDomainResponse (re-checks DNS)
//   DELETE /api/central-ingress/vm/{id}/domains/{domainId}          → ApiResponse<bool> (JSON, NOT 204)
// Status is a STRING on the wire (see domainStatus.ts). targetPort defaults to 80.

export interface CustomDomain {
  id: string;
  domain: string;
  targetPort: number;
  status: DomainStatus | string;
  publicUrl?: string | null;
  createdAt: string;
  verifiedAt?: string | null;
  dnsTarget?: string | null; // the CNAME target to point at
  dnsInstructions?: string | null; // human-readable, server-provided
}

const key = (vmId: string) => ["vm-domains", vmId] as const;
const base = (vmId: string) => `/api/central-ingress/vm/${vmId}/domains`;

export function useCustomDomains(api: Api, vmId: string) {
  return useQuery({
    queryKey: key(vmId),
    queryFn: () => api<CustomDomain[]>(base(vmId)),
    enabled: !!vmId,
  });
}

export function useAddDomain(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { domain: string; targetPort: number }) =>
      api<CustomDomain>(base(vmId), { method: "POST", body: JSON.stringify(input) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}

export function useVerifyDomain(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (domainId: string) =>
      api<CustomDomain>(`${base(vmId)}/${domainId}/verify`, { method: "POST" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}

export function useRemoveDomain(api: Api, vmId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (domainId: string) =>
      api<boolean>(`${base(vmId)}/${domainId}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: key(vmId) }),
  });
}
