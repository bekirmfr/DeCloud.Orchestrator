// Pure CustomDomainStatus logic — the boundary home for this enum, no React/API.
//
// GROUNDED against the real backend (Models/Ingress.cs): unlike PortProtocol and
// the three VM enums, CustomDomainResponse.Status carries the per-enum
// [property: JsonConverter(typeof(JsonStringEnumConverter))] attribute, so it
// serializes as a STRING ("PendingDns"|"Active"|"Paused"|"Error"). We normalize
// names directly, but stay defensive about a numeric ordinal (declaration order:
// PendingDns=0, Active=1, Paused=2, Error=3) in case the attribute is ever dropped
// — cheap insurance, same instinct as vmStatus.ts.

export type DomainStatus = "PendingDns" | "Active" | "Paused" | "Error";

const BY_ORDINAL: readonly DomainStatus[] = ["PendingDns", "Active", "Paused", "Error"];

export function normalizeDomainStatus(raw: DomainStatus | number | string): DomainStatus {
  if (typeof raw === "number") return BY_ORDINAL[raw] ?? "Error";
  if (/^\d+$/.test(raw)) return BY_ORDINAL[Number(raw)] ?? "Error";
  return raw as DomainStatus;
}

export type DomainTone = "active" | "pending" | "inert" | "error";

/** Display label + tone for a domain status. Labels are user-facing, not enum names. */
export function domainStatusBadge(raw: DomainStatus | number | string): { label: string; tone: DomainTone } {
  switch (normalizeDomainStatus(raw)) {
    case "Active":
      return { label: "Active", tone: "active" };
    case "PendingDns":
      return { label: "DNS pending", tone: "pending" };
    case "Paused":
      return { label: "Paused", tone: "inert" };
    case "Error":
    default:
      return { label: "Error", tone: "error" };
  }
}

/** Verify only makes sense before it's live (or after a failed check). */
export function canVerify(raw: DomainStatus | number | string): boolean {
  const s = normalizeDomainStatus(raw);
  return s === "PendingDns" || s === "Error";
}
