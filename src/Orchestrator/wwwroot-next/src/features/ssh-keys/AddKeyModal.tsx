import { useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import type { Api } from "../../api/client";
import { useAddSshKey } from "./useSshKeys";
import type { AppError } from "../../api/errors";

// Add-SSH-key modal. Radix Dialog (accessible primitive, DESIGN §6.8). The pure
// validateSshKey is a fast client pre-check for UX; the SERVER is the authority —
// it returns INVALID_KEY (definitive) which we surface. Behavior matches legacy
// addSSHKey(): two fields, non-empty check, POST {name, publicKey}, close+refresh.

export interface AddKeyInput {
  name: string;
  publicKey: string;
}

export type ValidationResult = { ok: true } | { ok: false; field: "name" | "publicKey"; message: string };

/** Pure client-side pre-check (already unit-tested). Server re-validates. */
export function validateSshKey(input: AddKeyInput): ValidationResult {
  if (!input.name.trim()) return { ok: false, field: "name", message: "Name is required" };
  const pk = input.publicKey.trim();
  if (!/^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+)\s+\S+/.test(pk)) {
    return { ok: false, field: "publicKey", message: "That doesn't look like a valid public key" };
  }
  return { ok: true };
}

export function AddKeyModal({ api, open, onClose }: { api: Api; open: boolean; onClose: () => void }) {
  const [name, setName] = useState("");
  const [publicKey, setPublicKey] = useState("");
  const [error, setError] = useState<string | null>(null);
  const add = useAddSshKey(api);

  function reset() {
    setName("");
    setPublicKey("");
    setError(null);
  }

  async function submit() {
    setError(null);
    const check = validateSshKey({ name, publicKey });
    if (!check.ok) {
      setError(check.message);
      return;
    }
    try {
      await add.mutateAsync({ name: name.trim(), publicKey: publicKey.trim() });
      reset();
      onClose();
    } catch (e) {
      // Server is the authority — surface its message (e.g. INVALID_KEY).
      setError((e as AppError)?.message ?? "Failed to add SSH key");
    }
  }

  return (
    <Dialog.Root open={open} onOpenChange={(o) => !o && onClose()}>
      <Dialog.Portal>
        <Dialog.Overlay className="dialog-overlay" />
        <Dialog.Content className="dialog-content" aria-describedby={undefined}>
          <Dialog.Title style={{ fontFamily: "var(--font-display)", fontWeight: 600 }}>Add SSH key</Dialog.Title>

          <label className="field">
            <span>Name</span>
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. laptop" />
          </label>

          <label className="field">
            <span>Public key</span>
            <textarea
              value={publicKey}
              onChange={(e) => setPublicKey(e.target.value)}
              placeholder="ssh-ed25519 AAAA…"
              rows={4}
            />
          </label>

          {error && (
            <p role="alert" style={{ color: "var(--danger)", fontSize: 13 }}>
              {error}
            </p>
          )}

          <div style={{ display: "flex", gap: 8, justifyContent: "flex-end", marginTop: 12 }}>
            <Dialog.Close asChild>
              <button className="btn-ghost" type="button">Cancel</button>
            </Dialog.Close>
            <button className="btn-primary" type="button" onClick={submit} disabled={add.isPending}>
              {add.isPending ? "Adding…" : "Add key"}
            </button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
