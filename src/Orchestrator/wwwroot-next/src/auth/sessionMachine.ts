import type { AuthUser, SessionState } from "./types";

// The session lifecycle as a small, pure-ish reducer + a refresh routine whose
// TRI-STATE result is the whole point (DESIGN §4/§5):
//   true  → refreshed OK        → authenticated
//   false → definitively dead   → expired
//   null  → could not verify    → uncertain (KEEP last-known token/user)
//
// Keeping transitions in a pure reducer makes them unit-testable without mocks
// (see sessionMachine.test.ts). Side effects (calling the refresh endpoint,
// writing tokenStore) live in the thin async wrapper, not the reducer.

export type SessionEvent =
  | { type: "AUTH_START" }
  | { type: "AUTH_SUCCESS"; token: string; address: string; user: AuthUser }
  | { type: "AUTH_FAIL" }
  | { type: "REFRESH_OK"; token: string }
  | { type: "REFRESH_EXPIRED" } // tri-state false
  | { type: "REFRESH_UNVERIFIABLE" } // tri-state null
  | { type: "SIGN_OUT" };

/** Pure transition. No I/O. Every arm has a test. */
export function sessionReducer(state: SessionState, event: SessionEvent): SessionState {
  switch (event.type) {
    case "AUTH_START":
      return { kind: "authenticating" };

    case "AUTH_SUCCESS":
      return { kind: "authenticated", token: event.token, address: event.address, user: event.user };

    case "AUTH_FAIL":
      return { kind: "expired" };

    case "REFRESH_OK":
      // Only meaningful if we hold a session; re-arm as authenticated with the new token.
      if (state.kind === "authenticated" || state.kind === "uncertain") {
        return { kind: "authenticated", token: event.token, address: state.address, user: state.user };
      }
      return state;

    case "REFRESH_EXPIRED":
      return { kind: "expired" };

    case "REFRESH_UNVERIFIABLE":
      // KEEP last-known — never destroy state on unverifiable evidence.
      if (state.kind === "authenticated") {
        return { kind: "uncertain", token: state.token, address: state.address, user: state.user };
      }
      return state; // if we weren't authenticated, nothing to keep

    case "SIGN_OUT":
      return { kind: "anonymous" };

    default:
      return state;
  }
}

export interface RefreshDeps {
  /** Calls the refresh endpoint. Resolve with a new token, false=expired, null=unverifiable. */
  callRefresh(): Promise<{ token: string } | false | null>;
}

/**
 * The tri-state refresh used by api/client.ts. Returns true/false/null and emits
 * the matching event so the machine + tokenStore stay in sync.
 */
export async function performRefresh(
  deps: RefreshDeps,
  dispatch: (e: SessionEvent) => void,
  setToken: (t: string | null) => void
): Promise<boolean | null> {
  let result: { token: string } | false | null;
  try {
    result = await deps.callRefresh();
  } catch {
    // Transport failure verifying the session = unverifiable, NOT expired.
    result = null;
  }

  if (result === false) {
    setToken(null);
    dispatch({ type: "REFRESH_EXPIRED" });
    return false;
  }
  if (result === null) {
    dispatch({ type: "REFRESH_UNVERIFIABLE" }); // keep last-known token in store
    return null;
  }
  setToken(result.token);
  dispatch({ type: "REFRESH_OK", token: result.token });
  return true;
}
