import type { DerivedStatus } from "../auth/types";

// THE Phase 2 pure decision (analog of deriveStatus). Given the ONE derived
// status the shell reads, decide which surface to render — and, when the app IS
// shown, whether it's degraded and whether escrow/fund actions are blocked.
//
// Keeping this pure means the whole gate is unit-tested with no React, no auth,
// no mocks (see resolveShellView.test.ts). The visual gates just render whatever
// this returns.

export type ShellView =
  | { surface: "connect" } // NEEDS_CONNECT — full-screen connect gate
  | { surface: "reauth"; reason: "auth" | "address-mismatch" } // full-screen sign-in
  | {
      surface: "app"; // the app is usable
      banner: "none" | "stale" | "wrong-network";
      escrowBlocked: boolean; // gate ONLY fund/escrow actions (DESIGN §4: wrong network)
    };

/**
 * Decisions encoded here (all from DESIGN §4/§5):
 *  - NEEDS_CONNECT     → connect gate (nothing else possible)
 *  - NEEDS_AUTH        → reauth gate ("sign in" — wallet is present)
 *  - ADDRESS_MISMATCH  → reauth gate, but reason=address-mismatch (fail closed;
 *                        prompt sign-in AS THE NEW ADDRESS; B never operates A)
 *  - WRONG_NETWORK     → app IS shown (you can view/operate); banner + escrow blocked
 *  - UNCERTAIN         → app IS shown with last-known data; stale banner; NOT blocked
 *  - READY             → app, no banner, nothing blocked
 */
export function resolveShellView(status: DerivedStatus): ShellView {
  switch (status) {
    case "NEEDS_CONNECT":
      return { surface: "connect" };
    case "NEEDS_AUTH":
      return { surface: "reauth", reason: "auth" };
    case "ADDRESS_MISMATCH":
      return { surface: "reauth", reason: "address-mismatch" };
    case "WRONG_NETWORK":
      return { surface: "app", banner: "wrong-network", escrowBlocked: true };
    case "UNCERTAIN":
      return { surface: "app", banner: "stale", escrowBlocked: false };
    case "READY":
      return { surface: "app", banner: "none", escrowBlocked: false };
    default: {
      // Exhaustiveness guard — if DerivedStatus grows, TS errors here.
      const _never: never = status;
      return _never;
    }
  }
}
