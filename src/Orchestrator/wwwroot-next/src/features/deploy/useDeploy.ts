import { keepPreviousData, useMutation, useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
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

/**
 * Validate a customised spec against the template's MinimumSpec. UX pre-check —
 * the server merges MinimumSpec floors and rejects a too-low QualityTier with
 * TIER_TOO_LOW regardless. Returns human-readable messages, empty when valid.
 */
export function specFloorErrors(
  spec: { virtualCpuCores: number; memoryBytes: number; diskBytes: number },
  minimum?: { virtualCpuCores?: number; memoryBytes?: number; diskBytes?: number } | null
): string[] {
  if (!minimum) return [];
  const gb = (b: number) => Math.round(b / 1024 ** 3);
  const errors: string[] = [];
  if (minimum.virtualCpuCores && spec.virtualCpuCores < minimum.virtualCpuCores)
    errors.push(`This template needs at least ${minimum.virtualCpuCores} vCPU.`);
  if (minimum.memoryBytes && spec.memoryBytes < minimum.memoryBytes)
    errors.push(`This template needs at least ${gb(minimum.memoryBytes)} GB of memory.`);
  if (minimum.diskBytes && spec.diskBytes < minimum.diskBytes)
    errors.push(`This template needs at least ${gb(minimum.diskBytes)} GB of disk.`);
  return errors;
}

// ── Tier vocabularies (GROUNDED against DeCloud.Shared.Enums) ──────────────
//
// QualityTier values run INVERSE to quality: Guaranteed=0 (best) … Burstable=3
// (worst). The C# enum carries an explicit warning against raw </> comparison
// and centralises it in QualityTierComparison.MeetsFloor. This is the mirror of
// that — the ONE place the inversion is encoded on the client.
//
// Deliberately NO price multipliers here. The legacy UI hardcodes
// (2.5/1.0/0.6/0.4) which matches neither SchedulingConfig (0.5/0.7/1.0) nor the
// live node's TierCapabilities (tier 0 = 1.8). Porting those constants would port
// a bug; tiers are described by CPU ratio, which is stable and grounded.
export const QUALITY_TIERS: Record<number, string> = {
  0: "Guaranteed — dedicated, 1:1 CPU",
  1: "Standard — 1.6:1 CPU",
  2: "Balanced — 2.7:1 CPU",
  3: "Burstable — best effort, 4:1 CPU",
};

// BandwidthTier is NOT inverted: 0=Basic (lowest) … 3=Unmetered (highest).
export const BANDWIDTH_TIERS: Record<number, string> = {
  0: "Basic — 10 Mbps",
  1: "Standard — 50 Mbps",
  2: "Performance — 200 Mbps",
  3: "Unmetered",
};

export const GPU_MODES: Record<number, string> = {
  0: "None",
  1: "Passthrough — dedicated GPU",
  2: "Proxied — shared GPU",
};

/**
 * Quality tiers at least as good as the template's floor. Because higher quality
 * is a LOWER value, that is `tier <= floor` — mirroring MeetsFloor in C#.
 * Undefined floor defaults to Standard(1), as the legacy deploy modal does.
 */
export function allowedQualityTiers(minimumTier?: number | null): number[] {
  const floor = minimumTier ?? 1;
  return [0, 1, 2, 3].filter((t) => t <= floor);
}

/**
 * Bandwidth tiers at or above the template's default, which the deploy form
 * treats as the MINIMUM ("this template needs at least this much"). Not inverted,
 * so it is a plain `>=`. Undefined defaults to Unmetered(3), as legacy does.
 */
export function allowedBandwidthTiers(minimumTier?: number | null): number[] {
  const floor = minimumTier ?? 3;
  return [0, 1, 2, 3].filter((t) => t >= floor);
}

// ── Live price estimate ───────────────────────────────────────────────────
// POST /api/system/pricing/calculate (AllowAnonymous), body = VmSpec.
// Delegates to HourlyRateCalculator — the SAME formula that stamps
// VmBillingInfo.HourlyRateCrypto at scheduling time. We do NOT reimplement the
// formula client-side: the legacy modal does, which is how a client copy drifts
// from what billing actually charges.
//
// Rates are platform DEFAULTS (the endpoint passes nodePricing: null). Real
// billing on a node with operator-set rates can be higher — never below floor.
export interface PriceCalculation {
  cpuCost: number;
  memoryCost: number;
  storageCost: number;
  gpuCost: number;
  bandwidthCost: number;
  replicationCost: number;
  hourlyTotal: number;
  dailyTotal: number;
  monthlyTotal: number;
  currency: string;
}

/** `specJson` is a pre-serialised VmSpec — it doubles as the cache key and body. */
export function usePriceEstimate(api: Api, specJson: string | null) {
  return useQuery({
    queryKey: ["price-estimate", specJson],
    queryFn: () =>
      api<PriceCalculation>("/api/system/pricing/calculate", {
        method: "POST",
        body: specJson!,
      }),
    enabled: !!specJson,
    staleTime: 5 * 60_000,
    // Keep the last figure on screen while a new one is fetched — the number
    // shifting is fine, blanking on every keystroke is not.
    placeholderData: keepPreviousData,
  });
}

/** Settle a rapidly-changing value before it drives a request. */
export function useDebounced<T>(value: T, ms = 400): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setSettled(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return settled;
}

export type { VmTemplate };
