import { describe, it, expect } from "vitest";
import { normalizeProtocol, protocolOrdinal } from "../portProtocol";

// PortProtocol is a NUMERIC-wire enum (TCP=1, UDP=2, Both=3 — no 0), like
// VmAction. These tests pin the exact ordinals so a future array-based rewrite
// (which would 0-index and shift everything) fails loudly here.

describe("normalizeProtocol — accepts name, ordinal, or numeric string", () => {
  it("passes through a valid name", () => {
    expect(normalizeProtocol("TCP")).toBe("TCP");
    expect(normalizeProtocol("UDP")).toBe("UDP");
    expect(normalizeProtocol("Both")).toBe("Both");
  });
  it("maps the numeric ordinal (1=TCP, 2=UDP, 3=Both)", () => {
    expect(normalizeProtocol(1)).toBe("TCP");
    expect(normalizeProtocol(2)).toBe("UDP");
    expect(normalizeProtocol(3)).toBe("Both");
  });
  it("maps a numeric string ('3' = Both)", () => {
    expect(normalizeProtocol("3")).toBe("Both");
  });
  it("there is no ordinal 0 — it falls back to TCP rather than crashing", () => {
    expect(normalizeProtocol(0)).toBe("TCP");
    expect(normalizeProtocol(99)).toBe("TCP");
  });
});

describe("protocolOrdinal — sends the number, not the name", () => {
  it("maps each name to its declared ordinal", () => {
    expect(protocolOrdinal("TCP")).toBe(1);
    expect(protocolOrdinal("UDP")).toBe(2);
    expect(protocolOrdinal("Both")).toBe(3);
  });
  it("round-trips name → ordinal → name", () => {
    for (const p of ["TCP", "UDP", "Both"] as const) {
      expect(normalizeProtocol(protocolOrdinal(p))).toBe(p);
    }
  });
});
