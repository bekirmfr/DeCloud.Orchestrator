// src/auth/walletCrypto.ts
// VERBATIM port of legacy wallet-crypto.js. Algorithm, derivation message, nonce
// size, and envelope layout are IDENTICAL so data encrypted by the legacy app
// still decrypts here (and vice versa). The ONLY change: the signer is injected
// (init) instead of read from window.ethersSigner.
import { randomBytes } from "@noble/hashes/utils.js";
import { sha256 } from "@noble/hashes/sha2.js";
import { gcm } from "@noble/ciphers/aes.js";

// MUST stay byte-identical to the legacy string, or existing ciphertexts break.
const ENCRYPTION_MESSAGE = "DeCloud VM Password Encryption Key v1";

export interface WalletCrypto {
  init(signMessage: (message: string) => Promise<string>): Promise<void>;
  encrypt(plaintext: string): Promise<string>;
  decrypt(envelope: string): Promise<string>;
  isReady(): boolean;
  clear(): void;
}

export function createWalletCrypto(): WalletCrypto {
  let key: Uint8Array | null = null;

  async function ensureKey(): Promise<Uint8Array> {
    if (!key) throw new Error("Wallet crypto not initialized (call init first)");
    return key;
  }

  return {
    async init(signMessage) {
      if (key) return; // cached for the session, like the legacy cachedEncryptionKey
      const signature = await signMessage(ENCRYPTION_MESSAGE);
      key = sha256(new TextEncoder().encode(signature));
    },

    async encrypt(plaintext) {
      const k = await ensureKey();
      const nonce = randomBytes(12);
      const ciphertext = gcm(k, nonce).encrypt(new TextEncoder().encode(plaintext));
      const combined = new Uint8Array(nonce.length + ciphertext.length);
      combined.set(nonce, 0);
      combined.set(ciphertext, nonce.length);
      return btoa(String.fromCharCode(...combined));
    },

    async decrypt(envelope) {
      const k = await ensureKey();
      const combined = Uint8Array.from(atob(envelope), (c) => c.charCodeAt(0));
      const nonce = combined.slice(0, 12);
      const ciphertext = combined.slice(12);
      try {
        return new TextDecoder().decode(gcm(k, nonce).decrypt(ciphertext));
      } catch (err) {
        if ((err as Error).message?.includes("Invalid")) {
          throw new Error(
            "Decryption failed: invalid key or corrupted data. Make sure you're using the same wallet."
          );
        }
        throw err;
      }
    },

    isReady() {
      return key !== null;
    },

    clear() {
      key = null;
    },
  };
}