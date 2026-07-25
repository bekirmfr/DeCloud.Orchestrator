// Pure PortProtocol wire logic — the single boundary home for this enum's quirk,
// same pattern as features/vms/vmStatus.ts. No React, no API.
//
// GROUNDED against the real backend (Models/DirectAccess.cs): PortProtocol has
// NO per-enum [JsonConverter] attribute, so — like VmStatus/VmPowerState/VmAction
// and UNLIKE CustomDomainStatus — it serializes as a raw NUMBER on the wire, both
// ways. Confirmed against the WORKING legacy client (direct-access.js), which
// sends `protocol` as an integer (allocatePort default 1) and reads mapping
// protocol back as a number (protocolToString(1|2|3)).
//
// Ordinals are the enum's DECLARED values (NOT a 0-based array — there is no 0):
//   TCP = 1, UDP = 2, Both = 3
// So map explicitly rather than index an array, or Both/TCP would shift.

export type PortProtocol = "TCP" | "UDP" | "Both";

const ORDINAL: Record<PortProtocol, number> = { TCP: 1, UDP: 2, Both: 3 };
const BY_ORDINAL: Record<number, PortProtocol> = { 1: "TCP", 2: "UDP", 3: "Both" };

/** Accept the numeric ordinal, a numeric string, or the name → canonical name. */
export function normalizeProtocol(raw: PortProtocol | number | string): PortProtocol {
  if (typeof raw === "number") return BY_ORDINAL[raw] ?? "TCP";
  if (/^\d+$/.test(raw)) return BY_ORDINAL[Number(raw)] ?? "TCP";
  return raw as PortProtocol;
}

/** Wire value for a protocol. The name stays in the UI; the number goes out. */
export function protocolOrdinal(p: PortProtocol): number {
  return ORDINAL[p];
}

export const PROTOCOLS: readonly PortProtocol[] = ["TCP", "UDP", "Both"];
