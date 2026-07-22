import { describe, it, expect } from "vitest";
import { vmStatusBadge, allowedActions, type VmStatus } from "../vmStatus";

describe("vmStatusBadge — all 12 statuses map to a tone", () => {
  const cases: Array<[VmStatus, string]> = [
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
  it.each(cases)("%s → %s", (status, tone) => {
    expect(vmStatusBadge(status).tone).toBe(tone);
  });

  it("unknown status falls back to inert (never crashes)", () => {
    expect(vmStatusBadge("Wat" as VmStatus).tone).toBe("inert");
  });
});

describe("allowedActions — UX button gating (server is the authority)", () => {
  it("Stopped → only Start", () => {
    expect(allowedActions("Stopped", "Off", false)).toEqual(["Start"]);
  });

  it("Running (not paused) → Stop/Restart/ForceStop/Pause", () => {
    expect(allowedActions("Running", "Running", false)).toEqual(["Stop", "Restart", "ForceStop", "Pause"]);
  });

  it("Running + power-paused → Resume instead of Pause", () => {
    const a = allowedActions("Running", "Paused", false);
    expect(a).toContain("Resume");
    expect(a).not.toContain("Pause");
  });

  it("compliance hold → no actions (only admin can lift)", () => {
    expect(allowedActions("Stopped", "Off", true)).toEqual([]);
    expect(allowedActions("Running", "Running", true)).toEqual([]);
  });

  it("transitional/terminal states → no actions", () => {
    expect(allowedActions("Provisioning", "Off", false)).toEqual([]);
    expect(allowedActions("Deleting", "Off", false)).toEqual([]);
    expect(allowedActions("Error", "Off", false)).toEqual([]);
    expect(allowedActions("Deleted", "Off", false)).toEqual([]);
  });
});
