import { useMutation, useQuery } from "@tanstack/react-query";
import type { Api } from "../../api/client";
import {
  resolveTemplate, submitTemplateDeploy,
  type VmTemplate, type DeployPayload, type DeployResult,
} from "./deploySubmit";

// Balance (GET /api/payment/balance → ApiResponse<BalanceResponse>). `balance` is
// AVAILABLE (confirmed − unpaid) — the number the fund gate checks and runway divides.
export interface BalanceResponse {
  balance: number;
  confirmedBalance: number;
  pendingDeposits: number;
  unpaidUsage: number;
  tokenSymbol: string;   // "USDC"
}

export function useBalance(api: Api) {
  return useQuery({
    queryKey: ["balance"],
    queryFn: () => api<BalanceResponse>("/api/payment/balance"),
    staleTime: 30_000,
  });
}

export function useTemplate(api: Api, slugOrId: string) {
  return useQuery({
    queryKey: ["template", slugOrId],
    queryFn: () => resolveTemplate(api, slugOrId),
    enabled: !!slugOrId,
  });
}

export function useDeploy(api: Api) {
  return useMutation<DeployResult, Error, { templateId: string; payload: DeployPayload }>({
    mutationFn: ({ templateId, payload }) => submitTemplateDeploy(api, templateId, payload),
  });
}

/**
 * Runway in days: available balance / (est. cost per hour × 24). Null when cost
 * is unknown/zero (can't compute) or balance missing. Pure — safe to unit-test.
 */
export function runwayDays(balance: number | undefined, costPerHour: number | undefined): number | null {
  if (balance == null || !costPerHour || costPerHour <= 0) return null;
  return balance / (costPerHour * 24);
}

/**
 * Does the user have enough to meaningfully run this template? The SERVER is the
 * authority (INSUFFICIENT_BALANCE for paid templates; billing pauses a VM that
 * runs dry). This is a UX pre-check: block the form when there's effectively no
 * runway. Free templates (cost 0/unknown) never gate here — the server handles
 * the paid-template fee separately.
 */
export function fundGateBlocks(balance: number | undefined, costPerHour: number | undefined): boolean {
  if (balance == null) return false;              // unknown balance → don't false-block
  if (!costPerHour || costPerHour <= 0) return false; // free/unknown → no gate
  return balance <= 0;                            // has a cost but no funds → gate
}

export type { VmTemplate };
