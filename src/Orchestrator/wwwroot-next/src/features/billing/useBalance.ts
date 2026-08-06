import { useQuery } from "@tanstack/react-query";
import type { Api } from "../../api/client";

// Money state. Lives here rather than in `deploy` because both the deploy fund
// gate and the dashboard runway panel need it — a billing concern, not a
// deploy one. GET /api/payment/balance → ApiResponse<BalanceResponse>.

export interface PendingDeposit {
  txHash: string;
  amount: number;
  confirmations: number;
  requiredConfirmations: number;
  createdAt: string;
}

export interface UsageSummary {
  vmId: string;
  cost: number;
  duration: string;
  createdAt: string;
}

export interface BalanceResponse {
  /** Available = confirmed − unpaid. What can actually be spent now. */
  balance: number;
  confirmedBalance: number;
  pendingDeposits: number;
  unpaidUsage: number;
  totalBalance: number;
  /** Sum of HourlyRateCrypto across Running, non-paused VMs (server-computed). */
  hourlyBurnRate: number;
  tokenSymbol: string;
  pendingDepositsList?: PendingDeposit[];
  recentUsage?: UsageSummary[];
}

export function useBalance(api: Api) {
  return useQuery({
    queryKey: ["balance"],
    queryFn: () => api<BalanceResponse>("/api/payment/balance"),
    staleTime: 20_000,
    // POLLED, not pushed: the hub has no BalanceUpdated emit yet (DESIGN §6.9
    // defers it). This is the one number on the dashboard that isn't real-time.
    refetchInterval: 30_000,
  });
}

/**
 * Days of runway: balance ÷ hourly burn. Null when nothing is burning (runway
 * is unbounded, not zero) or the balance is unknown. Pure — unit-tested.
 */
export function runwayDays(balance: number | undefined, hourlyRate: number | undefined): number | null {
  if (balance == null || !hourlyRate || hourlyRate <= 0) return null;
  return balance / (hourlyRate * 24);
}

/**
 * Human runway for a KNOWN number of days. The null case is deliberately NOT
 * handled here: zero burn means something different depending on whether any
 * workloads are running (nothing deployed vs. deployed but not being billed),
 * and only the caller knows which. See DashboardPage.
 */
export function formatRunway(days: number): string {
  if (days < 1) return "Less than a day";
  if (days < 2) return "About a day";
  if (days > 365) return "Over a year";
  return `About ${Math.floor(days)} days`;
}

/** Below this, the runway panel warns. Roughly "top up this week". */
export const LOW_RUNWAY_DAYS = 3;

// ── Deposit config ────────────────────────────────────────────────────────
// GET /api/payment/deposit-info → the on-chain deposit target + chain metadata
// (escrow address, USDC token, chain, explorer, minimum, confirmation depth).
// Config, so it's cached hard. The Wallet page shows it; the native deposit flow
// (Slice 2) will use the addresses to build the ethers txs.
export interface DepositInfoResponse {
  escrowContractAddress: string;
  usdcTokenAddress: string;
  chainId: string;
  chainName: string;
  explorerUrl: string;
  minDeposit: number;
  requiredConfirmations: number;
}

export function useDepositInfo(api: Api) {
  return useQuery({
    queryKey: ["deposit-info"],
    queryFn: () => api<DepositInfoResponse>("/api/payment/deposit-info"),
    staleTime: Infinity,
  });
}
