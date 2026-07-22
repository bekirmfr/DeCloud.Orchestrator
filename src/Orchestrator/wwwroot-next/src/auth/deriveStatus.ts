import type { DerivedStatus, SessionState, WalletState } from "./types";

/**
 * Ethereum addresses are compared case-insensitively (checksum vs lowercase are
 * the same address). The backend does the same (AddressUtil.ConvertToChecksum...).
 * Getting this wrong would produce false ADDRESS_MISMATCH → this helper is tested.
 */
export function sameAddress(a: string | undefined, b: string | undefined): boolean {
  if (!a || !b) return false;
  return a.toLowerCase() === b.toLowerCase();
}

/**
 * THE crown-jewel pure function (DESIGN §4). No async, no side effects, no mocks —
 * just (wallet, session, expectedChainId) → one status the shell renders from.
 *
 * The ORDER of these checks is a deliberate, security-first decision cascade.
 * Each `return` below has a corresponding case in deriveStatus.test.ts; if you
 * change the precedence, change the truth table with it.
 */
export function deriveStatus(
  wallet: WalletState,
  session: SessionState,
  expectedChainId: number
): DerivedStatus {
  // 1. No wallet → nothing else matters. (Post-disconnect, signOutOnDisconnect
  //    ends the session as a side effect; this snapshot is NEEDS_CONNECT either way.)
  if (wallet.kind !== "connected") return "NEEDS_CONNECT";

  const holdsSession = session.kind === "authenticated" || session.kind === "uncertain";

  // 2. SECURITY, fail closed: we hold a session, but the connected wallet is a
  //    DIFFERENT address (account switch A→B). Never operate as the old identity.
  //    Resolution (in AuthProvider) is to re-auth as the new address.
  if (holdsSession && !sameAddress(wallet.address, session.address)) {
    return "ADDRESS_MISMATCH";
  }

  // 3. Wallet present but no valid session → sign in (SIWE). Covers anonymous,
  //    expired, and in-progress authenticating (shell shows the auth affordance).
  if (session.kind === "anonymous" || session.kind === "expired" || session.kind === "authenticating") {
    return "NEEDS_AUTH";
  }

  // From here: authed AND address matches.

  // 4. Wrong chain → blocks fund/escrow only (not the whole app), but surface it
  //    so those actions can gate + prompt a switch.
  if (wallet.chainId !== expectedChainId) return "WRONG_NETWORK";

  // 5. Last-known session but the last refresh was unverifiable (tri-state null).
  //    Keep showing, mark degraded, retry — never destroy state on unverifiable evidence.
  //    NOTE: precedence of WRONG_NETWORK (4) over UNCERTAIN (5) is a judgment call —
  //    network is the more actionable block. Confirm against product intent; the
  //    test `uncertain + wrong chain → WRONG_NETWORK` pins whatever we choose.
  if (session.kind === "uncertain") return "UNCERTAIN";

  // 6. Everything lines up.
  return "READY";
}
