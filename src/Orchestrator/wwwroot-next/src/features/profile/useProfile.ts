import { useQuery } from "@tanstack/react-query";
import type { Api } from "../../api/client";

// GET /api/user/me → UserProfileResponse. Identity + quotas + key counts + VM
// counts. Roles aren't in this response — read those from the auth session.

export interface UserQuotas {
  maxVms: number;
  maxVirtualCpuCores: number;
  maxMemoryBytes: number;
  maxStorageBytes: number;
  currentVms: number;
  currentVirtualCpuCores: number;
  currentMemoryBytes: number;
  currentStorageBytes: number;
}

export interface KeySummary {
  id: string;
  name: string;
}

export interface UserProfile {
  id: string;
  walletAddress: string;
  displayName?: string | null;
  email?: string | null;
  status?: string | number; // UserStatus: Active=0, Suspended=1
  createdAt?: string;
  lastLoginAt?: string;
  quotas?: UserQuotas;
  sshKeys?: KeySummary[];
  apiKeys?: KeySummary[];
  totalVms?: number;
  runningVms?: number;
}

const STATUS: Record<string, string> = { "0": "Active", "1": "Suspended" };
export function statusLabel(s?: string | number): string {
  if (s == null) return "—";
  if (typeof s === "number") return STATUS[String(s)] ?? String(s);
  return s;
}

export function useProfile(api: Api) {
  return useQuery({
    queryKey: ["profile"],
    queryFn: () => api<UserProfile>("/api/user/me"),
  });
}
