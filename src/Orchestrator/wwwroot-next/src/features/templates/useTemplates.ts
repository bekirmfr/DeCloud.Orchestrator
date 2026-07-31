import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import type { VmTemplate } from "../deploy/deploySubmit";
import { toPayload, type TemplateForm } from "./templateForm";

interface MutationResult { template: VmTemplate; warnings: string[] }

// Template authoring (Phase 5). The author's own templates across the lifecycle
// (Draft → PendingReview → Published / Rejected / Archived). GET returns full
// VmTemplate rows so the list can show status + basics.

export function useMyTemplates(api: Api) {
  return useQuery({
    queryKey: ["my-templates"],
    queryFn: () => api<VmTemplate[]>("/api/marketplace/templates/my"),
  });
}

export function useDeleteTemplate(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (templateId: string) =>
      api<unknown>(`/api/marketplace/templates/${templateId}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

// ── Lifecycle (slice 3) ──────────────────────────────────────────────────
// All three take no body and return the updated VmTemplate. Transitions are
// enforced server-side (409 INVALID_OPERATION on a wrong-status call).

// Draft/Rejected → submit for review (community → PendingReview, else Published).
export function usePublishTemplate(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (templateId: string) =>
      api<VmTemplate>(`/api/marketplace/templates/${templateId}/publish`, { method: "PATCH" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

// PendingReview → back to Draft.
export function useCancelReview(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (templateId: string) =>
      api<VmTemplate>(`/api/marketplace/templates/${templateId}/cancel-review`, { method: "POST" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

// Published (community) → new Draft revision (a new template row to edit).
export function useReviseTemplate(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (templateId: string) =>
      api<VmTemplate>(`/api/marketplace/templates/${templateId}/revise`, { method: "POST" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

export function useCreateTemplate(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (form: TemplateForm) =>
      api<MutationResult>("/api/marketplace/templates/create", { method: "POST", body: JSON.stringify(toPayload(form)) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

export function useUpdateTemplate(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    // Edit PUTs the FULL VmTemplate: overlay the form fields onto the loaded row,
    // deep-merging specs so VmSpec sub-fields the form doesn't expose (constraints,
    // quality tier, …) survive the edit.
    mutationFn: ({ loaded, form }: { loaded: VmTemplate; form: TemplateForm }) => {
      const p = toPayload(form);
      const merged = {
        ...loaded, ...p,
        recommendedSpec: { ...(loaded.recommendedSpec ?? {}), ...(p.recommendedSpec as object) },
        minimumSpec: { ...(loaded.minimumSpec ?? {}), ...(p.minimumSpec as object) },
      };
      return api<MutationResult>(`/api/marketplace/templates/${loaded.id}`, { method: "PUT", body: JSON.stringify(merged) });
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: ["my-templates"] }),
  });
}

// TemplateStatus may serialize as a string ("Draft") or its numeric ordinal
// (Draft=0, Published=1, Archived=2, PendingReview=3, Rejected=4) — normalize both.
const STATUS: Record<string, { label: string; tone: string }> = {
  draft: { label: "Draft", tone: "var(--text-secondary)" },
  published: { label: "Published", tone: "var(--success)" },
  archived: { label: "Archived", tone: "var(--text-tertiary)" },
  pendingreview: { label: "In review", tone: "var(--warning)" },
  rejected: { label: "Rejected", tone: "var(--danger)" },
};
const NUM: Record<number, string> = { 0: "draft", 1: "published", 2: "archived", 3: "pendingreview", 4: "rejected" };

export function templateStatus(status?: string | number): { label: string; tone: string } {
    const key = typeof status === "number" ? (NUM[status] ?? "") : String(status ?? "").toLowerCase();
  return STATUS[key] ?? { label: String(status ?? "—"), tone: "var(--text-secondary)" };
}


// TemplateStatus as a number regardless of wire form (int or name), for driving
// which lifecycle actions apply. -1 = unknown.
const KEY_TO_NUM: Record<string, number> = { draft: 0, published: 1, archived: 2, pendingreview: 3, rejected: 4 };
export function statusNum(status?: string | number): number {
  if (typeof status === "number") return status;
  return KEY_TO_NUM[String(status ?? "").toLowerCase()] ?? -1;
}
