// Auth core types — the contract for DESIGN §4 (wallet ↔ session dual lifecycle).
// These types are the source of truth the rest of the auth module is built on.

/** Mirrors the server User (the fields the client needs). Admin is a ROLE, 
 * not a bool — matches [Authorize(Roles="Admin")] and the JWT. */
export interface AuthUser {
  id: string;              // == walletAddress (checksum)
  walletAddress: string;
  roles: string[];         // e.g. ["User"] or ["User","Admin"]
  email?: string | null;
  username?: string | null;
  displayName?: string | null;
}

/**
 * Wallet lifecycle (client, from Reown AppKit + ethers).
 * Disconnected → Connecting → Connected(address, chainId).
 * "WrongNetwork" and "account-switched" are NOT separate kinds here — they are
 * derived by comparing `chainId`/`address` against the session + expected chain
 * (see deriveStatus). Keeping the raw wallet state small avoids double-encoding.
 */
export type WalletState =
  | { kind: "disconnected" }
  | { kind: "connecting" }
  | { kind: "connected"; address: string; chainId: number };

/**
 * Session lifecycle (server, from SIWE/JWT).
 * Anonymous → Authenticating → Authenticated → (Refreshing) → Authenticated | Uncertain | Expired.
 *
 * `uncertain` is the tri-state-refresh `null` outcome: the last refresh could not
 * be verified (RPC/transport blip), so we KEEP the last-known token/user and mark
 * it stale rather than destroying state. See DESIGN §4/§5 (the "uncertain" kind).
 */
// Session states now carry the token AND its expiry (SessionResponse.ExpiresAt),
// enabling proactive refresh.
export type SessionState =
  | { kind: "anonymous" }
  | { kind: "authenticating" }
  | { kind: "authenticated"; token: string; address: string; user: AuthUser }
  | { kind: "uncertain"; token: string; address: string; user: AuthUser }
  | { kind: "expired" };

/**
 * The ONE status the app shell reads. Derived purely from
 * (wallet, session, expectedChainId) — see deriveStatus.ts.
 */
export type DerivedStatus =
  | "READY" // connected + authenticated + address matches + right chain
  | "NEEDS_CONNECT" // no wallet connected
  | "NEEDS_AUTH" // wallet present, but no valid session — sign in (SIWE)
  | "WRONG_NETWORK" // authed, but wrong chain — blocks fund/escrow only
  | "ADDRESS_MISMATCH" // wallet address ≠ session address — fail closed, re-auth as new address
  | "UNCERTAIN"; // last-known session, unverifiable — keep showing, degraded, retry

/** The chain the app transacts on (Polygon). Wire from config; here as a named constant. */
export const EXPECTED_CHAIN_ID = 137; // Polygon mainnet. Amoy testnet = 80002 (see appsettings Payment:ChainId).
