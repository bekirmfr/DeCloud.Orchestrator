// Full SIWE config — PORT of legacy siwe-config.js. The EIP-4361 message is built
// by AppKit (@reown/appkit-siwe formatMessage), NOT hand-rolled, so it stays
// byte-identical to what the backend verifies. We only wire the four backend hooks.
import { createSIWEConfig, formatMessage, type SIWECreateMessageArgs } from "@reown/appkit-siwe";
import type { AuthUser } from "./types";

export interface DecloudSiweDeps {
  orchestratorUrl: string; // "" when same-origin (/app)
  getChainId: () => number;
  onAuthenticated: (accessToken: string, user: AuthUser) => void; // → sessionMachine AUTH_SUCCESS
  onSignOut?: () => void; // → sessionMachine SIGN_OUT
}

/** EIP-4361 line 2 is the address; authoritative address is recovered server-side. */
export function parseAddressFromSiwe(message: string): string | null {
  const m = message.match(/0x[a-fA-F0-9]{40}/);
  return m ? m[0] : null;
}

export function createDecloudSiweConfig({ orchestratorUrl, getChainId, onAuthenticated, onSignOut }: DecloudSiweDeps) {
  const url = (path: string) => `${orchestratorUrl}${path}`;
  const json = (extra?: RequestInit): RequestInit => ({
    credentials: "include",
    headers: { "Content-Type": "application/json" },
    ...extra,
  });

  return createSIWEConfig({
    getMessageParams: async () => ({
      domain: window.location.host,
      uri: window.location.origin,
      chains: [getChainId()],
      statement: "Sign in to DeCloud. This request will not trigger a transaction or cost any gas.",
    }),

    // AppKit passes the SIWE params + address; formatMessage builds the exact string.
    createMessage: ({ address, ...args }: SIWECreateMessageArgs) =>
      formatMessage(args as never, address),

    getNonce: async () => {
      const res = await fetch(url("/api/auth/nonce"), json());
      if (!res.ok) throw new Error("Failed to get nonce");
      const body = await res.json();
      const nonce = body?.data?.nonce;
      if (!nonce) throw new Error("Malformed nonce response");
      return nonce;
    },

    verifyMessage: async ({ message, signature }: { message: string; signature: string }) => {
      const address = parseAddressFromSiwe(message);
      const res = await fetch(
        url("/api/auth/wallet"),
        json({ method: "POST", body: JSON.stringify({ message, signature, walletAddress: address }) })
      );
      if (!res.ok) return false;
      const body = await res.json();
      if (!body?.success || !body?.data?.accessToken) return false;
      onAuthenticated(body.data.accessToken, body.data.user);
      return true;
    },

    getSession: async () => {
      const res = await fetch(url("/api/auth/session"), json());
      if (!res.ok) return null;
      const body = await res.json();
      const address = body?.data?.address;
      return address ? { address, chainId: getChainId() } : null;
    },

    signOut: async () => {
      try {
        await fetch(url("/api/auth/logout"), json({ method: "POST" }));
      } catch {
        /* best-effort; local state cleared regardless */
      }
      onSignOut?.();
      return true;
    },

    signOutOnDisconnect: true,
  });
}