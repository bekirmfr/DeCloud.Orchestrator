// Pure VM status/action logic — the new "muscle" for Phase 3, unit-tested like
// deriveStatus/resolveShellView were. No React, no API. Grounded in the real
// backend enums (DeCloud.Shared.Enums) and the VmsController transition rules.

// VmStatus — 12 states. NOTE ON THE WIRE: the design intends a global
// JsonStringEnumConverter (strings), and auth/ssh-keys arrive that way — but
// VmStatus.cs lacks the per-enum [JsonConverter] attribute that VmRole/VmCategory
// carry, and GET /api/vms was OBSERVED sending status as a NUMBER (e.g. 3 = Running).
// So we tolerate BOTH here, normalizing to the canonical string. Trust the wire
// over the doc. Ordinals are the enum's declaration order (confirmed in VmStatus.cs).
export type VmStatus =
  | "Pending" | "Scheduling" | "Provisioning" | "Running" | "Paused"
  | "Suspended" | "Stopping" | "Stopped" | "Deleting" | "Deleted"
  | "Migrating" | "Error";

const STATUS_BY_ORDINAL: readonly VmStatus[] = [
  "Pending", "Scheduling", "Provisioning", "Running", "Paused", "Suspended",
  "Stopping", "Stopped", "Deleting", "Deleted", "Migrating", "Error",
];

/** Accept the numeric ordinal, a numeric string, or the name → canonical name. */
export function normalizeStatus(raw: VmStatus | number | string): VmStatus {
  if (typeof raw === "number") return STATUS_BY_ORDINAL[raw] ?? (String(raw) as VmStatus);
  if (/^\d+$/.test(raw)) return STATUS_BY_ORDINAL[Number(raw)] ?? (raw as VmStatus);
  return raw as VmStatus;
}

// A separate axis from Status (a Running VM can be power-Paused).
export type VmPowerState = "Running" | "Paused" | "Off";

// VmPowerState ALSO lacks the [JsonConverter] attribute, so it serializes numeric
// too (Off=0, Running=1, Paused=2 — declaration order in VirtualMachine.cs).
const POWER_BY_ORDINAL: readonly VmPowerState[] = ["Off", "Running", "Paused"];

export function normalizePowerState(raw: VmPowerState | number | string): VmPowerState {
  if (typeof raw === "number") return POWER_BY_ORDINAL[raw] ?? "Off";
  if (/^\d+$/.test(raw)) return POWER_BY_ORDINAL[Number(raw)] ?? "Off";
  return raw as VmPowerState;
}

export type VmAction = "Start" | "Stop" | "Restart" | "Pause" | "Resume" | "ForceStop";

// VmAction.cs ALSO lacks [JsonConverter(typeof(JsonStringEnumConverter))] — the
// third enum in this API to do so — so POST /api/vms/{id}/action expects the
// ORDINAL, not the name. Sending {"action":"Stop"} is rejected with a 400:
//   "The JSON value could not be converted to ... VmActionRequest. Path: $.action"
// Order is the declaration order in VmAction.cs.
const ACTION_ORDINAL: Record<VmAction, number> = {
  Start: 0,
  Stop: 1,
  Restart: 2,
  Pause: 3,
  Resume: 4,
  ForceStop: 5,
};

/** Wire value for a lifecycle action. Names stay in the UI; the number goes out. */
export function vmActionOrdinal(action: VmAction): number {
  return ACTION_ORDINAL[action];
}

export type BadgeTone = "active" | "transitional" | "inert" | "error";

/** Map a status (name OR numeric ordinal) to a display label + tone. */
export function vmStatusBadge(status: VmStatus | number): { label: string; tone: BadgeTone } {
  switch (normalizeStatus(status)) {
    case "Running":
      return { label: "Running", tone: "active" };
    case "Pending":
    case "Scheduling":
    case "Provisioning":
    case "Stopping":
    case "Deleting":
    case "Migrating":
      return { label: normalizeStatus(status), tone: "transitional" };
    case "Paused":
    case "Suspended":
    case "Stopped":
    case "Deleted":
      return { label: normalizeStatus(status), tone: "inert" };
    case "Error":
      return { label: "Error", tone: "error" };
    default:
      return { label: String(normalizeStatus(status)), tone: "inert" };
  }
}

/**
 * UX-only: which lifecycle actions to SHOW for a VM. The SERVER is the authority
 * — it validates every (action, status) transition and returns INVALID_ACTION /
 * VM_HELD. This just avoids offering obviously-wrong buttons. Mirrors the
 * VmsController.Action validation switch, minus offering Pause when already paused.
 * (Delete is a separate endpoint, not an action — handled outside this.)
 *
 * `status` tolerates the numeric wire form; `powerState` is passed as its name —
 * the detail page normalizes it at the boundary (its wire form is grounded there).
 */
export function allowedActions(
  status: VmStatus | number,
  powerState: VmPowerState | number,
  complianceHold: boolean
): VmAction[] {
  if (complianceHold) return [];

  const s = normalizeStatus(status);
  if (s === "Stopped") return ["Start"];

  if (s === "Running") {
    const actions: VmAction[] = ["Stop", "Restart", "ForceStop"];
    actions.push(normalizePowerState(powerState) === "Paused" ? "Resume" : "Pause");
    return actions;
  }

  return [];
}
