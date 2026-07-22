import { describe, it, expect } from "vitest";
import { deriveStatus, sameAddress } from "../deriveStatus";
import type { AuthUser, SessionState, WalletState } from "../types";

const CHAIN = 137;
const A = "0xAAAAaaaaAAAAaaaaAAAAaaaaAAAAaaaaAAAAaaaa";
const B = "0xBBBBbbbbBBBBbbbbBBBBbbbbBBBBbbbbBBBBbbbb";
const user = (addr: string): AuthUser => ({ id: "u_" + addr, walletAddress: addr, isAdmin: false });

const wDisconnected: WalletState = { kind: "disconnected" };
const wConnecting: WalletState = { kind: "connecting" };
const wConnected = (address: string, chainId = CHAIN): WalletState => ({ kind: "connected", address, chainId });

const sAnon: SessionState = { kind: "anonymous" };
const sAuthing: SessionState = { kind: "authenticating" };
const sExpired: SessionState = { kind: "expired" };
const sAuthed = (address: string): SessionState => ({ kind: "authenticated", token: "t", address, user: user(address) });
const sUncertain = (address: string): SessionState => ({ kind: "uncertain", token: "t", address, user: user(address) });

describe("sameAddress", () => {
  it("matches case-insensitively (checksum vs lowercase)", () => {
    expect(sameAddress(A, A.toLowerCase())).toBe(true);
  });
  it("is false for different addresses", () => {
    expect(sameAddress(A, B)).toBe(false);
  });
  it("is false when either side is missing", () => {
    expect(sameAddress(A, undefined)).toBe(false);
    expect(sameAddress(undefined, A)).toBe(false);
  });
});

describe("deriveStatus — the §4 truth table", () => {
  it("disconnected + anonymous → NEEDS_CONNECT", () => {
    expect(deriveStatus(wDisconnected, sAnon, CHAIN)).toBe("NEEDS_CONNECT");
  });

  it("disconnected + authenticated → NEEDS_CONNECT (disconnect ends the session as a side effect)", () => {
    expect(deriveStatus(wDisconnected, sAuthed(A), CHAIN)).toBe("NEEDS_CONNECT");
  });

  it("connecting → NEEDS_CONNECT (not usable yet)", () => {
    expect(deriveStatus(wConnecting, sAnon, CHAIN)).toBe("NEEDS_CONNECT");
  });

  it("connected + anonymous → NEEDS_AUTH", () => {
    expect(deriveStatus(wConnected(A), sAnon, CHAIN)).toBe("NEEDS_AUTH");
  });

  it("connected + expired → NEEDS_AUTH (wallet is right there, just re-sign)", () => {
    expect(deriveStatus(wConnected(A), sExpired, CHAIN)).toBe("NEEDS_AUTH");
  });

  it("connected + authenticating → NEEDS_AUTH (in progress)", () => {
    expect(deriveStatus(wConnected(A), sAuthing, CHAIN)).toBe("NEEDS_AUTH");
  });

  it("connected(A) + authenticated(A) + right chain → READY", () => {
    expect(deriveStatus(wConnected(A), sAuthed(A), CHAIN)).toBe("READY");
  });

  it("READY matches even if wallet is checksummed and session is lowercase", () => {
    expect(deriveStatus(wConnected(A), sAuthed(A.toLowerCase()), CHAIN)).toBe("READY");
  });

  it("connected(B) + authenticated(A) → ADDRESS_MISMATCH (fail closed; B never operates A)", () => {
    expect(deriveStatus(wConnected(B), sAuthed(A), CHAIN)).toBe("ADDRESS_MISMATCH");
  });

  it("connected(A) + authenticated(A) + WRONG chain → WRONG_NETWORK", () => {
    expect(deriveStatus(wConnected(A, 1), sAuthed(A), CHAIN)).toBe("WRONG_NETWORK");
  });

  it("connected(A) + uncertain(A) + right chain → UNCERTAIN (keep last-known, degraded)", () => {
    expect(deriveStatus(wConnected(A), sUncertain(A), CHAIN)).toBe("UNCERTAIN");
  });

  it("connected(B) + uncertain(A) → ADDRESS_MISMATCH (security beats uncertainty)", () => {
    expect(deriveStatus(wConnected(B), sUncertain(A), CHAIN)).toBe("ADDRESS_MISMATCH");
  });

  it("connected(A) + uncertain(A) + WRONG chain → WRONG_NETWORK (documented precedence: network before uncertain)", () => {
    expect(deriveStatus(wConnected(A, 1), sUncertain(A), CHAIN)).toBe("WRONG_NETWORK");
  });
});
