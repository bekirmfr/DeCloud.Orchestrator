// src/auth/siwe.ts
// SIWE lands with the AppKit slice (walletState + AuthProvider), because the real
// flow is an AppKit `createSIWEConfig` (see the grounded port to re-apply once
// `@reown/appkit-siwe` is installed). Until then only the pure helper lives here.
//
// The grounded port wires four backend hooks to /api/auth:
//   getNonce   → GET  /api/auth/nonce      (json.data.nonce)
//   verify     → POST /api/auth/wallet     { message, signature, walletAddress }
//                                          → json.data.accessToken + json.data.user
//   getSession → GET  /api/auth/session    (json.data.address; address-only!)
//   signOut    → POST /api/auth/logout
// createMessage delegates to @reown/appkit-siwe `formatMessage` — we do NOT
// hand-roll the EIP-4361 string, so it stays byte-identical to the backend.

/** EIP-4361 line 2 is the address; the authoritative address is recovered
 *  server-side from the signature. */
export function parseAddressFromSiwe(message: string): string | null {
  const m = message.match(/0x[a-fA-F0-9]{40}/);
  return m ? m[0] : null;
}