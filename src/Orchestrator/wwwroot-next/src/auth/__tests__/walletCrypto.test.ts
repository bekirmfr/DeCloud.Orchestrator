// src/auth/__tests__/walletCrypto.test.ts
import { describe, it, expect } from "vitest";
import { createWalletCrypto } from "../walletCrypto";

const signerReturning = (sig: string) => async (_msg: string) => sig;

describe("walletCrypto (noble AES-GCM, wallet-derived key)", () => {
  it("round-trips plaintext", async () => {
    const wc = createWalletCrypto();
    await wc.init(signerReturning("0xdeadbeef-signature"));
    const env = await wc.encrypt("hunter2");
    expect(await wc.decrypt(env)).toBe("hunter2");
  });

  it("a different signature (different wallet) cannot decrypt", async () => {
    const a = createWalletCrypto(); await a.init(signerReturning("sigA"));
    const b = createWalletCrypto(); await b.init(signerReturning("sigB"));
    const env = await a.encrypt("secret");
    await expect(b.decrypt(env)).rejects.toThrow(/same wallet|invalid/i);
  });

  it("tampered ciphertext throws (never returns garbage)", async () => {
    const wc = createWalletCrypto(); await wc.init(signerReturning("sig"));
    const env = await wc.encrypt("secret");
    const bytes = Uint8Array.from(atob(env), (c) => c.charCodeAt(0));
    bytes[bytes.length - 1]! ^= 0xff; // flip a bit in the ciphertext
    const tampered = btoa(String.fromCharCode(...bytes));
    await expect(wc.decrypt(tampered)).rejects.toThrow();
  });

  it("isReady() false before init, true after", async () => {
    const wc = createWalletCrypto();
    expect(wc.isReady()).toBe(false);
    await wc.init(signerReturning("sig"));
    expect(wc.isReady()).toBe(true);
  });

  it("encrypt before init throws", async () => {
    const wc = createWalletCrypto();
    await expect(wc.encrypt("x")).rejects.toThrow(/not initialized/i);
  });
});