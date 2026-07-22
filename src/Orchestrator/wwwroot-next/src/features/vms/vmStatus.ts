// Pure VM status/action logic — the new "muscle" for Phase 3, unit-tested like
// deriveStatus/resolveShellView were. No React, no API. Grounded in the real
// backend enums (DeCloud.Shared.Enums) and the VmsController transition rules.

// VmStatus — 12 states, serialized as strings (global JsonStringEnumConverter).
export type VmStatus =
  | "Pending" | "Scheduling" | "Provisioning" | "Running" | "Paused"
  | "Suspended" | "Stopping" | "Stopped" | "Deleting" | "Deleted"
  | "Migrating" | "Error";

// A separate axis from Status (a Running VM can be power-Paused).
export type VmPowerState = "Running" | "Paused" | "Off";

export type VmAction = "Start" | "Stop" | "Restart" | "Pause" | "Resume" | "ForceStop";

export type BadgeTone = "active" | "transitional" | "inert" | "error";

/** Map a status to a display label + tone. All 12 handled; unknown → inert. */
export function vmStatusBadge(status: VmStatus): { label: string; tone: BadgeTone } {
  switch (status) {
    case "Running":
      return { label: "Running", tone: "active" };
    case "Pending":
    case "Scheduling":
    case "Provisioning":
    case "Stopping":
    case "Deleting":
    case "Migrating":
      return { label: status, tone: "transitional" };
    case "Paused":
    case "Suspended":
    case "Stopped":
    case "Deleted":
      return { label: status, tone: "inert" };
    case "Error":
      return { label: "Error", tone: "error" };
    default:
      return { label: String(status), tone: "inert" };
  }
}

/**
 * UX-only: which lifecycle actions to SHOW for a VM. The SERVER is the authority
 * — it validates every (action, status) transition and returns INVALID_ACTION /
 * VM_HELD. This just avoids offering obviously-wrong buttons. Mirrors the
 * VmsController.Action validation switch, minus offering Pause when already paused.
 * (Delete is a separate endpoint, not an action — handled outside this.)
 */
export function allowedActions(
  status: VmStatus,
  powerState: VmPowerState,
  complianceHold: boolean
): VmAction[] {
  // A held VM is force-stopped and cannot be Started/Resumed by the owner
  // (only an admin resume-vm lifts it) — so nothing here applies.
  if (complianceHold) return [];

  if (status === "Stopped") return ["Start"];

  if (status === "Running") {
    const actions: VmAction[] = ["Stop", "Restart", "ForceStop"];
    // Resume only when power-paused; otherwise Pause is the meaningful toggle.
    actions.push(powerState === "Paused" ? "Resume" : "Pause");
    return actions;
  }

  // Transitional (Pending/Scheduling/Provisioning/Stopping/Deleting/Migrating),
  // Paused/Suspended/Deleted, and Error → no owner action via /action.
  return [];
}
