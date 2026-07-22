// AES-GCM encryption with a wallet-derived key. PORT wallet-crypto.js VERBATIM —
// the exact key-derivation and IV/format must stay byte-compatible, because data
// encrypted by the legacy app must still decrypt here (and vice versa) during the
// migration. Do NOT "improve" the scheme; match it exactly, then test round-trip.
//
// Security (DESIGN §10): the derived key is cached in memory for the session only.
// The wallet signature that seeds derivation is never persisted.

export interface WalletCrypto {
  /** Derive (and cache) the AES-GCM key from a wallet signature over a fixed message. */
  init(signMessage: (message: string) => Promise<string>, address: string): Promise<void>;
  encrypt(plaintext: string): Promise<string>; // returns the legacy-compatible envelope (iv+ciphertext)
  decrypt(envelope: string): Promise<string>;
  isReady(): boolean;
  clear(): void;
}

// TODO: implement by porting wallet-crypto.js exactly:
//   - fixed derivation message (must equal the legacy string)
//   - signMessage(derivationMessage) → signature
//   - SHA-256(signature) → raw key bytes → crypto.subtle.importKey('raw', ..., 'AES-GCM')
//   - encrypt: random 12-byte IV; crypto.subtle.encrypt; concat+base64 in the legacy layout
//   - decrypt: reverse; authentication failure must throw (never return garbage)
export function createWalletCrypto(): WalletCrypto {
  throw new Error("TODO: createWalletCrypto — port wallet-crypto.js verbatim");
}
