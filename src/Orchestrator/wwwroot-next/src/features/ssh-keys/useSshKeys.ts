import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";

// Server data = TanStack Query, keyed by resource (DESIGN §6.3). No SSH-key state
// in React/Context — it's a cache of the server.
//
// Endpoints GROUNDED against UserController.cs (the SSH keys hang off the user,
// not a standalone controller): /api/user/me/ssh-keys. Confirmed live: a tokenless
// GET 401s (route exists, needs bearer), /api/ssh-keys 404s (not registered).

export interface SshKey {
  id: string;
  name: string;
  publicKey: string;
  fingerprint: string;
  createdAt: string;
}

const BASE = "/api/user/me/ssh-keys";
const KEY = ["ssh-keys"] as const;

export function useSshKeys(api: Api) {
  return useQuery({
    queryKey: KEY,
    queryFn: () => api<SshKey[]>(BASE),
  });
}

export function useAddSshKey(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; publicKey: string }) =>
      api<SshKey>(BASE, { method: "POST", body: JSON.stringify(input) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteSshKey(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<boolean>(`${BASE}/${id}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}
