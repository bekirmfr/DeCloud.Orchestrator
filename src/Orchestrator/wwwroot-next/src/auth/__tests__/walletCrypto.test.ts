import { describe, it } from "vitest";

// walletCrypto is a VERBATIM port of wallet-crypto.js — its tests need the real
// implementation + a deterministic signMessage stub. Enumerated here as the
// required cases so they aren't forgotten; fill in when createWalletCrypto lands.

describe("walletCrypto (AES-GCM, wallet-derived key)", () => {
  it.todo("encrypt→decrypt round-trips to the original plaintext");
  it.todo("decrypt fails (throws) on a tampered ciphertext — never returns garbage");
  it.todo("decrypt fails when the derived key differs (different signature/address)");
  it.todo("envelope layout (iv+ciphertext, base64) is byte-compatible with the legacy wallet-crypto.js");
  it.todo("isReady() is false before init() and true after");
});
