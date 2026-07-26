import type { Api } from "../../api/client";
import { createWalletCrypto } from "../../auth/walletCrypto";

// VM root password, wallet-secured. The password is shown once at deploy; the
// user signs to store it encrypted (POST secure-password) and can later reveal
// it by signing again (GET encrypted-password → decrypt). Uses the existing
// walletCrypto, whose envelope is byte-compatible with the legacy app, so
// passwords saved by either UI decrypt in both.

interface EncryptedPasswordResponse {
  encryptedPassword: string | null;
  isSecured: boolean;
}

/** Encrypt the plaintext with a wallet-derived key and store the envelope.
 *  Called once, from the deploy password reveal. */
export async function saveEncryptedPassword(
  api: Api,
  vmId: string,
  plaintext: string,
  signMessage: (message: string) => Promise<string>,
): Promise<void> {
  const wc = createWalletCrypto();
  await wc.init(signMessage);
  const envelope = await wc.encrypt(plaintext);
  await api(`/api/vms/${vmId}/secure-password`, {
    method: "POST",
    body: JSON.stringify({ encryptedPassword: envelope }),
  });
}

/** Fetch the stored envelope and decrypt it with the wallet. Throws a friendly
 *  message if the VM has no saved password (it was only shown at deploy time). */
export async function revealPassword(
  api: Api,
  vmId: string,
  signMessage: (message: string) => Promise<string>,
): Promise<string> {
  let res: EncryptedPasswordResponse;
  try {
    res = await api<EncryptedPasswordResponse>(`/api/vms/${vmId}/encrypted-password`);
  } catch {
    // The endpoint 400s when nothing is stored — treat as "not saved".
    throw new Error("No saved password for this VM — it was only shown once, at deploy time.");
  }
  if (!res.isSecured || !res.encryptedPassword) {
    throw new Error("No saved password for this VM — it was only shown once, at deploy time.");
  }
  const wc = createWalletCrypto();
  await wc.init(signMessage);
  return wc.decrypt(res.encryptedPassword);
}
