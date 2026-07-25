import { describe, it, expect } from "vitest";
import { normalizeDomainStatus, domainStatusBadge, canVerify } from "../domainStatus";

// CustomDomainStatus serializes as a STRING (it carries the per-enum converter),
// so the name path is the real one; the ordinal path is defensive insurance.

describe("normalizeDomainStatus", () => {
  it("passes through the string names it actually receives", () => {
    expect(normalizeDomainStatus("Active")).toBe("Active");
    expect(normalizeDomainStatus("PendingDns")).toBe("PendingDns");
    expect(normalizeDomainStatus("Paused")).toBe("Paused");
    expect(normalizeDomainStatus("Error")).toBe("Error");
  });
  it("defensively maps a numeric ordinal (declaration order)", () => {
    expect(normalizeDomainStatus(0)).toBe("PendingDns");
    expect(normalizeDomainStatus(1)).toBe("Active");
    expect(normalizeDomainStatus("3")).toBe("Error");
  });
});

describe("domainStatusBadge — user-facing labels, not enum names", () => {
  it("renders 'DNS pending' for PendingDns", () => {
    expect(domainStatusBadge("PendingDns")).toEqual({ label: "DNS pending", tone: "pending" });
  });
  it("renders Active with an active tone", () => {
    expect(domainStatusBadge("Active")).toEqual({ label: "Active", tone: "active" });
  });
});

describe("canVerify — only before it's live or after a failure", () => {
  it("is true for PendingDns and Error", () => {
    expect(canVerify("PendingDns")).toBe(true);
    expect(canVerify("Error")).toBe(true);
  });
  it("is false once Active or Paused", () => {
    expect(canVerify("Active")).toBe(false);
    expect(canVerify("Paused")).toBe(false);
  });
});
