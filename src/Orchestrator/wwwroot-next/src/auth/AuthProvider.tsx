import { createContext, useContext, type ReactNode } from "react";
import type { DerivedStatus, SessionState, WalletState } from "./types";

// The React seam that ties the two lifecycles together and exposes ONE derived
// status + the actions the shell needs. This is the only stateful auth surface
// components touch. Keep the wiring here; keep the logic in the pure modules
// (deriveStatus, sessionReducer) so it stays testable.
//
// Side effects this provider owns (DESIGN §4) — each should get a test once wired:
//   - wallet disconnects            → signOut (signOutOnDisconnect = true)
//   - wallet account switch A→B     → status ADDRESS_MISMATCH → prompt re-auth as B (fail closed)
//   - status NEEDS_AUTH + user acts → run SIWE signIn
//   - 401 anywhere                  → performRefresh (tri-state) via api/client deps
//   - WRONG_NETWORK                 → expose switchChain; gate fund/escrow actions only

export interface AuthContextValue {
  wallet: WalletState;
  session: SessionState;
  status: DerivedStatus;
  // actions
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  signIn(): Promise<void>;
  signOut(): Promise<void>;
  switchToExpectedChain(): Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within <AuthProvider>");
  return ctx;
}

/**
 * TODO (Phase 1 wiring):
 *  1. const wallet = useWalletAdapter()            // subscribe on mount, cleanup on unmount
 *  2. const [session, dispatch] = useReducer(sessionReducer, { kind: "anonymous" })
 *  3. const status = useMemo(() => deriveStatus(wallet, session, EXPECTED_CHAIN_ID), [wallet, session])
 *  4. const api = useMemo(() => createApi({ getToken: tokenStore.get, refresh: () => performRefresh(...) }))
 *  5. effects for the side effects listed above (disconnect→signOut, switch→re-auth, ...)
 *  6. session restore on mount: try a silent refresh; null→uncertain, false→anonymous, true→authed
 */
export function AuthProvider({ children }: { children: ReactNode }) {
  // TODO: assemble the value per the steps above and provide it.
  throw new Error("TODO: AuthProvider — wire wallet + session + api per the header steps");
  return <AuthContext.Provider value={null as never}>{children}</AuthContext.Provider>;
}
