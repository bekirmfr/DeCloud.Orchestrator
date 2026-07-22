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

    // CROSS-COMPAT: a REAL triple captured from the running legacy app (wallet-crypto.js).
    // Proves the noble-v2 port decrypts genuine legacy-produced ciphertext byte-for-byte —
    // the actual migration-safety guarantee, not a self-consistency round-trip.
    // Verified: sha256(utf8(signature)) key + base64(nonce[12] ++ ct) envelope → plaintext.
    it("decrypts a real legacy-produced ciphertext (byte-compatible with wallet-crypto.js)", async () => {
        const LEGACY = {
            signature:
                "0x9915952ede5c9fb8e6f93e6cd4469f031bcd48e91406d34cd513880e40185ddc02372daf1a3f840cfb643695d79b49740917484af5222e7969df47ffb8add6aa1b",
            plaintext: "fixture-plaintext-v1",
            ciphertext: "6tupANKJDQcBF+cIIKE3RaBuPQmeU4LsCEYsvj8cfkrrWdGDQrAUJqwegfKU7eVH",
        };
        const wc = createWalletCrypto();
        await wc.init(signerReturning(LEGACY.signature));
        expect(await wc.decrypt(LEGACY.ciphertext)).toBe(LEGACY.plaintext);
    });
});