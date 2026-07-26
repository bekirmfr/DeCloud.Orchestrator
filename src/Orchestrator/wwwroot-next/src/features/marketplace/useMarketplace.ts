import { keepPreviousData, useQuery } from "@tanstack/react-query";
import type { Api } from "../../api/client";

// Marketplace browse. GROUNDED against the ORCHESTRATOR MarketplaceController:
//   GET /api/marketplace/templates?category=&requiresGpu=&tags=&search=&featured=&sortBy=&limit=
//        → ApiResponse<VmTemplateSummary[]>
//   GET /api/marketplace/categories → ApiResponse<TemplateCategory[]>
// EstimatedCostPerHour is SERVER-computed on the summary — cards show it directly,
// never a client-side price (the detail page uses usePriceEstimate for a live
// per-spec figure). This is what retires template-detail.js's stale pricing.

export interface VmTemplateSummary {
  id: string;
  slug: string;
  name: string;
  description: string;
  category: string;
  tags: string[];
  iconUrl?: string | null;
  authorName: string;
  isCommunity: boolean;
  isVerified: boolean;
  isFeatured: boolean;
  requiresGpu: boolean;
  recommendedCpuCores: number;
  recommendedMemoryBytes: number;
  recommendedDiskBytes: number;
  estimatedCostPerHour: number;   // server-computed
  templatePrice: number;
  deploymentCount: number;
  averageRating: number;
  totalReviews: number;
}

export interface TemplateCategory {
  id: string;
  name: string;
  slug: string;
  description: string;
  iconEmoji: string;
  displayOrder: number;
  templateCount: number;
}

export interface TemplateFilters {
  search?: string;
  category?: string;
  requiresGpu?: boolean;
  sortBy?: string;
}

/** Build the GET /templates query string from filters (pure → testable). Only
 *  non-empty filters are emitted, so the URL and the query key stay minimal and
 *  a bare browse hits the endpoint with no params. */
export function buildTemplateQuery(f: TemplateFilters): string {
  const p = new URLSearchParams();
  if (f.search) p.set("search", f.search);
  if (f.category) p.set("category", f.category);
  if (f.requiresGpu) p.set("requiresGpu", "true");
  if (f.sortBy && f.sortBy !== "popular") p.set("sortBy", f.sortBy); // popular is the server default
  const s = p.toString();
  return s ? `?${s}` : "";
}

export function useTemplates(api: Api, filters: TemplateFilters) {
  const qs = buildTemplateQuery(filters);
  return useQuery({
    queryKey: ["marketplace-templates", qs],
    queryFn: () => api<VmTemplateSummary[]>(`/api/marketplace/templates${qs}`),
    placeholderData: keepPreviousData,   // don't flash empty while filters change
  });
}

export function useCategories(api: Api) {
  return useQuery({
    queryKey: ["marketplace-categories"],
    queryFn: () => api<TemplateCategory[]>(`/api/marketplace/categories`),
    staleTime: 10 * 60_000,
  });
}

// ── Reviews (detail page) ───────────────────────────────────────────────────
// GET /api/marketplace/reviews/{resourceType}/{resourceId} → MarketplaceReview[]

export interface MarketplaceReview {
  id: string;
  reviewerId: string;          // wallet
  reviewerName?: string | null;
  rating: number;              // 1..5
  title?: string | null;
  comment?: string | null;
  createdAt: string;
}

export function useReviews(api: Api, resourceId: string | undefined) {
  return useQuery({
    queryKey: ["marketplace-reviews", "template", resourceId],
    queryFn: () => api<MarketplaceReview[]>(`/api/marketplace/reviews/template/${resourceId}`),
    enabled: !!resourceId,
    staleTime: 5 * 60_000,
  });
}

/** Short wallet for display: 0x1234…cdef. Falls back to the raw string if it's
 *  not a long hex address. */
export function shortWallet(addr: string): string {
  return addr.length > 12 ? `${addr.slice(0, 6)}…${addr.slice(-4)}` : addr;
}
