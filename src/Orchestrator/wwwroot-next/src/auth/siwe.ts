import type { Api } from "../api/client";
import type { AuthUser } from "./types";
import { classifyWalletError } from "../api/errors";

// SIWE (Sign-In With Ethereum) flow. PORT of the legacy siwe-config.js:
//   1. request a nonce for the address           GET  /api/auth/nonce?address=
//   2. build the SIWE message, wallet signs it    (ethers signer.signMessage)
//   3. POST message+signature to verify           POST /api/auth/verify
//      → returns { token, user }; refresh token is set as an httpOnly cookie.
//
// Confirm the exact endpoints/param names against the backend auth controller
// and the legacy siwe-config.js before wiring. Keep the message builder byte-for-
// byte compatible with what the backend verifies.

export interface SignInDeps {
  api: Api;
  /** Signs an arbitrary message with the connected wallet (ethers signer.signMessage). */
  signMessage(message: string): Promise<string>;
  address: string;
  chainId: number;
}

export interface VerifyResult {
  token: string;
  user: AuthUser;
}

/** Step 1: get a fresh nonce for this address (single-use; backend NonceStore). */
export async function requestNonce(api: Api, address: string): Promise<string> {
  // TODO: return (await api<{ nonce: string }>(`/api/auth/nonce?address=${address}`)).nonce;
  throw new Error("TODO: requestNonce — port from siwe-config.js");
}

/** Step 2: build the exact SIWE message the backend will re-verify. */
export function buildSiweMessage(_params: { address: string; chainId: number; nonce: string }): string {
  // TODO: reproduce the legacy message format EXACTLY (domain, uri, version,
  //       chainId, nonce, issuedAt). A mismatch here = every verify fails.
  throw new Error("TODO: buildSiweMessage — must match backend verification");
}

/**
 * Full sign-in. Returns the verified session material.
 * A REJECTED signature must surface as a `cancel` (not an error) — hence the
 * classifyWalletError wrap. See §5.
 */
export async function signIn(deps: SignInDeps): Promise<VerifyResult> {
  const { api, signMessage, address, chainId } = deps;
  const nonce = await requestNonce(api, address);
  const message = buildSiweMessage({ address, chainId, nonce });

  let signature: string;
  try {
    signature = await signMessage(message);
  } catch (err) {
    throw classifyWalletError(err); // rejected → cancel; network → uncertain
  }

  // TODO: return await api<VerifyResult>("/api/auth/verify", {
  //   method: "POST",
  //   body: JSON.stringify({ message, signature }),
  //   retryOn401: false,
  // });
  void signature;
  throw new Error("TODO: signIn verify — port from siwe-config.js");
}

/** Server-side sign-out (revoke refresh token). Client also clears tokenStore. */
export async function signOut(_api: Api): Promise<void> {
  // TODO: await api("/api/auth/logout", { method: "POST", retryOn401: false });
  throw new Error("TODO: signOut — port from siwe-config.js");
}
