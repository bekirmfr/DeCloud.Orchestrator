import { describe, it, expect } from "vitest";
import { shouldRevealPassword } from "../deploySubmit";
import { runwayDays, fundGateBlocks, specFloorErrors } from "../useDeploy";

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
