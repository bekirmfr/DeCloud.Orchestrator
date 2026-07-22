// Add-SSH-key modal. Uses Radix Dialog (accessible primitive, DESIGN §6.8) — no
// hand-rolled focus trap. The VALIDATION is split into a pure function so it can
// be unit-tested without rendering (see __tests__/validateKey.test.ts).

export interface AddKeyInput {
  name: string;
  publicKey: string;
}

export type ValidationResult = { ok: true } | { ok: false; field: "name" | "publicKey"; message: string };

/** Pure validation — mirror the legacy rules; confirm against the backend. */
export function validateSshKey(input: AddKeyInput): ValidationResult {
  if (!input.name.trim()) return { ok: false, field: "name", message: "Name is required" };
  const pk = input.publicKey.trim();
  // Public keys start with a known type token (ssh-ed25519 / ssh-rsa / ecdsa-…).
  if (!/^(ssh-ed25519|ssh-rsa|ecdsa-sha2-\S+)\s+\S+/.test(pk)) {
    return { ok: false, field: "publicKey", message: "That doesn't look like a valid public key" };
  }
  return { ok: true };
}

// TODO: <Dialog.Root>… form calling validateSshKey on submit, then useAddSshKey.
// Keep the exact rules aligned with the backend so client/server agree.
export function AddKeyModal(_props: { open: boolean; onClose: () => void }) {
  throw new Error("TODO: AddKeyModal — Radix Dialog + form wired to useAddSshKey");
}
