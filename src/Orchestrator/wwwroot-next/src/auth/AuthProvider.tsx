import { createContext, useCallback, useContext, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import type { AuthUser, DerivedStatus, SessionState, WalletState } from "./types";
import { EXPECTED_CHAIN_ID } from "./types";
import { deriveStatus } from "./deriveStatus";
import { sessionReducer, performRefresh } from "./sessionMachine";
import { tokenStore } from "./tokenStore";
import { createApi, type Api } from "../api/client";
import { createWalletAdapter, type WalletAdapter } from "./walletState";
import { createDecloudSiweConfig } from "./siwe";

// Same-origin deployment (served at /app by ASP.NET), so requests are relative.
const ORCHESTRATOR_URL = "";

export interface AuthContextValue {
    wallet: WalletState;
    session: SessionState;
    status: DerivedStatus;
    api: Api;
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

/** Map the raw server `User` JSON to our AuthUser (roles, not isAdmin). */
function mapUser(raw: any): AuthUser {
    return {
        id: raw.id,
        walletAddress: raw.walletAddress ?? raw.id,
        roles: Array.isArray(raw.roles) ? raw.roles : ["User"],
        email: raw.email ?? null,
        username: raw.username ?? null,
        displayName: raw.displayName ?? null,
    };
}

export function AuthProvider({ children }: { children: ReactNode }) {
    const [session, dispatch] = useReducer(sessionReducer, { kind: "anonymous" } as SessionState);
    const [wallet, setWallet] = useState<WalletState>({ kind: "disconnected" });
    const adapterRef = useRef<WalletAdapter | null>(null);

    // Hits the refresh endpoint (httpOnly `dc_rt` cookie rides via credentials).
    // Returns full session material on success (restore needs the user), false when
    // definitively expired (401), null when unverifiable (transport/5xx).
    // CONFIRM endpoint path + response shape against AuthController (assumed
    // POST /api/auth/refresh → SessionResponse { accessToken, expiresAt, user }).
    const callRefresh = useCallback(async (): Promise<{ token: string; user: AuthUser } | false | null> => {
        try {
            const res = await fetch(`${ORCHESTRATOR_URL}/api/auth/refresh`, {
                method: "POST",
                credentials: "include",
            });
            if (res.status === 401) return false; // definitively expired
            if (!res.ok) return null; // transient → uncertain
            const body = await res.json();
            const token = body?.data?.accessToken;
            const user = body?.data?.user;
            if (!token || !user) return null;
            return { token, user: mapUser(user) };
        } catch {
            return null; // transport failure → uncertain, never "expired"
        }
    }, []);

    // performRefresh (api 401 path) only needs the token to re-arm; adapt the shape.
    const refreshDeps = useMemo(
        () => ({
            callRefresh: async () => {
                const r = await callRefresh();
                return r ? { token: r.token } : r; // {token} | false | null
            },
        }),
        [callRefresh]
    );

    // The single API boundary every feature uses. On 401 it runs the tri-state
    // refresh, which dispatches REFRESH_OK/EXPIRED/UNVERIFIABLE and updates the store.
    const api = useMemo(
        () =>
            createApi({
                getToken: () => tokenStore.get(),
                refresh: () => performRefresh(refreshDeps, dispatch, tokenStore.set),
            }),
        [refreshDeps]
    );

    // Create the AppKit adapter ONCE (createAppKit is a singleton), but (re)subscribe
    // on EVERY effect run so React StrictMode's mount→unmount→remount re-establishes
    // the wallet subscription instead of tearing it down for good. `dispatch` and
    // `callRefresh` are stable, so building the config once is safe.
    useEffect(() => {
        if (!adapterRef.current) {
            const siweConfig = createDecloudSiweConfig({
                orchestratorUrl: ORCHESTRATOR_URL,
                // Lazy: reads the adapter after it's assigned (breaks the config↔adapter cycle).
                getChainId: () => {
                    const w = adapterRef.current?.getState();
                    return w && w.kind === "connected" ? w.chainId : EXPECTED_CHAIN_ID;
                },
                // Fresh sign-in completion (AppKit SIWE verifyMessage → here).
                onAuthenticated: (accessToken: string, user: any) => {
                    const u = mapUser(user);
                    tokenStore.set(accessToken);
                    dispatch({ type: "AUTH_SUCCESS", token: accessToken, address: u.walletAddress, user: u });
                },
                onSignOut: () => {
                    tokenStore.clear();
                    dispatch({ type: "SIGN_OUT" });
                },
            });

            adapterRef.current = createWalletAdapter(siweConfig);

            // Restore-on-mount, ONCE: getSession restores the ADDRESS but never fires
            // onAuthenticated and returns no token. If a mirrored-token hint says this
            // user was signed in, ESTABLISH the session from the refresh cookie
            // (AUTH_SUCCESS with the user) — NOT performRefresh, which no-ops from `anonymous`.
            if (tokenStore.get()) {
                callRefresh().then((r) => {
                    if (r) {
                        tokenStore.set(r.token);
                        dispatch({ type: "AUTH_SUCCESS", token: r.token, address: r.user.walletAddress, user: r.user });
                    } else {
                        tokenStore.clear(); // stale hint; stay anonymous
                    }
                });
            }
        }

        // (Re)subscribe every run — StrictMode-safe.
        setWallet(adapterRef.current.getState());
        const unsub = adapterRef.current.subscribe(setWallet);
        return () => unsub();
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, []);

    const status = useMemo(() => deriveStatus(wallet, session, EXPECTED_CHAIN_ID), [wallet, session]);

    const value: AuthContextValue = {
        wallet,
        session,
        status,
        api,
        connect: () => adapterRef.current!.connect(),
        disconnect: () => adapterRef.current!.disconnect(),
        // Opening the AppKit modal drives the SIWE flow (connect → sign → onAuthenticated).
        // For the NEEDS_AUTH / ADDRESS_MISMATCH case (already connected, not signed in),
        // AppKit prompts the signature. CONFIRM this is the right trigger vs a dedicated API.
        signIn: () => adapterRef.current!.connect(),
        signOut: async () => {
            await adapterRef.current!.disconnect();
            tokenStore.clear();
            dispatch({ type: "SIGN_OUT" });
        },
        switchToExpectedChain: () => adapterRef.current!.switchChain(EXPECTED_CHAIN_ID),
    };

    return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}