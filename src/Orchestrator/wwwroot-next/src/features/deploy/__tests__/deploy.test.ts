import { describe, it, expect } from "vitest";
import { shouldRevealPassword } from "../deploySubmit";
import { runwayDays, fundGateBlocks, specFloorErrors, allowedQualityTiers, allowedBandwidthTiers } from "../useDeploy";

describe("shouldRevealPassword — verbatim legacy sniff (memorable format only)", () => {
  it("reveals the memorable format (has '-', no '_')", () => {
    expect(shouldRevealPassword("brave-otter-1234")).toBe(true);
  });
  it("does NOT reveal system format (has '_')", () => {
    expect(shouldRevealPassword("sys_abc-123")).toBe(false);
  });
  it("does NOT reveal a format without '-'", () => {
    expect(shouldRevealPassword("plainpassword")).toBe(false);
  });
  it("handles undefined/empty", () => {
    expect(shouldRevealPassword(undefined)).toBe(false);
    expect(shouldRevealPassword("")).toBe(false);
  });
});

describe("runwayDays", () => {
  it("computes available / (cost/hr × 24)", () => {
    expect(runwayDays(48, 1)).toBe(2);        // 48 / 24 = 2 days
    expect(runwayDays(120, 0.5)).toBe(10);    // 120 / 12 = 10 days
  });
  it("returns null when cost is zero/unknown or balance missing", () => {
    expect(runwayDays(100, 0)).toBeNull();
    expect(runwayDays(100, undefined)).toBeNull();
    expect(runwayDays(undefined, 1)).toBeNull();
  });
});

describe("fundGateBlocks — UX pre-check (server is authority)", () => {
  it("blocks when there's a cost but no funds", () => {
    expect(fundGateBlocks(0, 1)).toBe(true);
  });
  it("does not block when funds exist", () => {
    expect(fundGateBlocks(10, 1)).toBe(false);
  });
  it("does not block free/unknown-cost templates", () => {
    expect(fundGateBlocks(0, 0)).toBe(false);
    expect(fundGateBlocks(0, undefined)).toBe(false);
  });
  it("does not false-block on unknown balance", () => {
    expect(fundGateBlocks(undefined, 1)).toBe(false);
  });
});

describe("specFloorErrors — customise pre-check (server enforces floors too)", () => {
  const GB = 1024 ** 3;
  const min = { virtualCpuCores: 2, memoryBytes: 4 * GB, diskBytes: 20 * GB };

  it("passes when the spec meets every floor", () => {
    expect(specFloorErrors({ virtualCpuCores: 2, memoryBytes: 4 * GB, diskBytes: 20 * GB }, min)).toEqual([]);
  });
  it("flags CPU below the floor", () => {
    const e = specFloorErrors({ virtualCpuCores: 1, memoryBytes: 4 * GB, diskBytes: 20 * GB }, min);
    expect(e).toHaveLength(1);
    expect(e[0]).toContain("2 vCPU");
  });
  it("flags every violated floor at once", () => {
    expect(specFloorErrors({ virtualCpuCores: 1, memoryBytes: 1 * GB, diskBytes: 5 * GB }, min)).toHaveLength(3);
  });
  it("no minimum declared → nothing to enforce", () => {
    expect(specFloorErrors({ virtualCpuCores: 1, memoryBytes: GB, diskBytes: GB }, null)).toEqual([]);
    expect(specFloorErrors({ virtualCpuCores: 1, memoryBytes: GB, diskBytes: GB }, undefined)).toEqual([]);
  });
  it("ignores floors the template leaves unset", () => {
    expect(specFloorErrors({ virtualCpuCores: 1, memoryBytes: GB, diskBytes: GB }, { virtualCpuCores: 1 })).toEqual([]);
  });
});

describe("allowedQualityTiers — INVERSE ordering (0=Guaranteed best, 3=Burstable worst)", () => {
  it("floor Burstable(3) allows every tier", () => {
    expect(allowedQualityTiers(3)).toEqual([0, 1, 2, 3]);
  });
  it("floor Standard(1) allows only Guaranteed and Standard", () => {
    expect(allowedQualityTiers(1)).toEqual([0, 1]);
  });
  it("floor Guaranteed(0) allows only Guaranteed — the strictest floor", () => {
    expect(allowedQualityTiers(0)).toEqual([0]);
  });
  it("undefined floor defaults to Standard(1), as the legacy modal does", () => {
    expect(allowedQualityTiers(undefined)).toEqual([0, 1]);
    expect(allowedQualityTiers(null)).toEqual([0, 1]);
  });
  it("never offers a tier the server would reject with TIER_TOO_LOW", () => {
    // MeetsFloor(tier, floor) => tier <= floor. Every offered tier must satisfy it.
    for (const floor of [0, 1, 2, 3])
      for (const t of allowedQualityTiers(floor)) expect(t <= floor).toBe(true);
  });
});

describe("allowedBandwidthTiers — NOT inverted (0=Basic lowest, 3=Unmetered)", () => {
  it("floor Basic(0) allows every tier", () => {
    expect(allowedBandwidthTiers(0)).toEqual([0, 1, 2, 3]);
  });
  it("floor Performance(2) allows Performance and Unmetered", () => {
    expect(allowedBandwidthTiers(2)).toEqual([2, 3]);
  });
  it("undefined = no declared requirement → every tier offered", () => {
    // Regression: previously defaulted to 3, so a template with no declared
    // bandwidth minimum offered only Unmetered.
    expect(allowedBandwidthTiers(undefined)).toEqual([0, 1, 2, 3]);
    expect(allowedBandwidthTiers(null)).toEqual([0, 1, 2, 3]);
  });
});
