import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import type { Api } from "../../api/client";

// Server data = TanStack Query, keyed by resource (DESIGN §6.3). No SSH-key state
// in React/Context — it's a cache of the server. Confirm the endpoints + shapes
// against the backend SSH controller and the generated schema.d.ts.

export interface SshKey {
  id: string;
  name: string;
  fingerprint: string;
  createdAt: string;
}

const KEY = ["ssh-keys"] as const;

export function useSshKeys(api: Api) {
  return useQuery({
    queryKey: KEY,
    queryFn: () => api<SshKey[]>("/api/ssh-keys"), // TODO: confirm path
  });
}

export function useAddSshKey(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (input: { name: string; publicKey: string }) =>
      api<SshKey>("/api/ssh-keys", { method: "POST", body: JSON.stringify(input) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}

export function useDeleteSshKey(api: Api) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api<void>(`/api/ssh-keys/${id}`, { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: KEY }),
  });
}
