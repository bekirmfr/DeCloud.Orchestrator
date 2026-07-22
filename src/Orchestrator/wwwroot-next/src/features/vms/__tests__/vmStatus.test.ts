import { describe, it, expect } from "vitest";
import { vmStatusBadge, allowedActions, normalizeStatus, type VmStatus } from "../vmStatus";

describe("normalizeStatus — accepts name, ordinal, or numeric string", () => {
  it("passes through a valid name", () => {
    expect(normalizeStatus("Running")).toBe("Running");
  });
  it("maps the numeric ordinal (3 = Running)", () => {
    expect(normalizeStatus(3)).toBe("Running");
    expect(normalizeStatus(7)).toBe("Stopped");
    expect(normalizeStatus(11)).toBe("Error");
    expect(normalizeStatus(0)).toBe("Pending");
  });
  it("maps a numeric string ('3' = Running)", () => {
    expect(normalizeStatus("3")).toBe("Running");
  });
  it("out-of-range ordinal falls back to the raw value (never crashes)", () => {
    expect(normalizeStatus(99)).toBe("99");
  });
});

describe("vmStatusBadge — tones for names AND numeric ordinals", () => {
  const nameCases: Array<[VmStatus, string]> = [
    ["Running", "active"],
    ["Pending", "transitional"],
    ["Scheduling", "transitional"],
    ["Provisioning", "transitional"],
    ["Stopping", "transitional"],
    ["Deleting", "transitional"],
    ["Migrating", "transitional"],
    ["Paused", "inert"],
    ["Suspended", "inert"],
    ["Stopped", "inert"],
    ["Deleted", "inert"],
    ["Error", "error"],
  ];
  it.each(nameCases)("%s → %s", (status, tone) => {
    expect(vmStatusBadge(status).tone).toBe(tone);
  });

  it("numeric 3 (Running) → active, label 'Running'", () => {
    expect(vmStatusBadge(3)).toEqual({ label: "Running", tone: "active" });
  });
  it("numeric 7 (Stopped) → inert", () => {
    expect(vmStatusBadge(7).tone).toBe("inert");
  });
  it("numeric 11 (Error) → error", () => {
    expect(vmStatusBadge(11).tone).toBe("error");
  });
  it("unknown status falls back to inert (never crashes)", () => {
    expect(vmStatusBadge("Wat" as VmStatus).tone).toBe("inert");
  });
});

describe("allowedActions — UX button gating (server is the authority)", () => {
  it("Stopped → only Start", () => {
    expect(allowedActions("Stopped", "Off", false)).toEqual(["Start"]);
  });
  it("numeric Stopped (7) → only Start", () => {
    expect(allowedActions(7, "Off", false)).toEqual(["Start"]);
  });
  it("Running (not paused) → Stop/Restart/ForceStop/Pause", () => {
    expect(allowedActions("Running", "Running", false)).toEqual(["Stop", "Restart", "ForceStop", "Pause"]);
  });
  it("Running + power-paused → Resume instead of Pause", () => {
    const a = allowedActions("Running", "Paused", false);
    expect(a).toContain("Resume");
    expect(a).not.toContain("Pause");
  });
  it("compliance hold → no actions", () => {
    expect(allowedActions("Stopped", "Off", true)).toEqual([]);
    expect(allowedActions("Running", "Running", true)).toEqual([]);
  });
  it("transitional/terminal states → no actions", () => {
    expect(allowedActions("Provisioning", "Off", false)).toEqual([]);
    expect(allowedActions("Error", "Off", false)).toEqual([]);
    expect(allowedActions("Deleted", "Off", false)).toEqual([]);
  });
});
