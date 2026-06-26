// ============================================================================
// siwe-config.js — Sign-In With Ethereum (EIP-4361) configuration for AppKit.
//
// Collapses "connect wallet" + "sign message" into AppKit's One-Click Auth
// flow (a single prompt on WalletConnect wallets; a connect-then-sign sequence
// with no extra UI button on injected wallets). All four backend hooks are
// wired to the orchestrator's /api/auth endpoints.
//
// SECURITY:
//   - getNonce fetches a single-use, server-issued nonce (EIP-4361).
//   - The refresh token is set by the server as an httpOnly cookie inside
//     verifyMessage's response; it is never seen by this code. Every request
//     uses credentials:'include' so that cookie is sent/received.
//   - The short-lived access token returned by verifyMessage is handed to the
//     app via onAuthenticated; the app keeps it in memory (+ localStorage
//     mirror) for the Authorization header.
//
// Requires: npm i @reown/appkit-siwe
// ============================================================================

import { createSIWEConfig, formatMessage } from '@reown/appkit-siwe';

/**
 * Build the SIWE config object to pass to createAppKit({ siweConfig }).
 *
 * @param {Object}   opts
 * @param {string}   opts.orchestratorUrl           Base URL of the API.
 * @param {Function} opts.getChainId                () => number — current EVM chain id.
 * @param {Function} opts.onAuthenticated           (accessToken, user) => void — called after a verified sign-in.
 * @param {Function} [opts.onSignOut]               () => void — called after sign-out.
 */
export function createDecloudSiweConfig({ orchestratorUrl, getChainId, onAuthenticated, onSignOut }) {
    const url = (path) => `${orchestratorUrl}${path}`;

    return createSIWEConfig({
        // EIP-4361 message parameters. AppKit fills in address, chainId, nonce.
        getMessageParams: async () => ({
            domain: window.location.host,
            uri: window.location.origin,
            chains: [getChainId()],
            statement: 'Sign in to DeCloud. This request will not trigger a transaction or cost any gas.'
        }),

        createMessage: ({ address, ...args }) => formatMessage(args, address),

        // Single-use nonce from the server.
        getNonce: async () => {
            const res = await fetch(url('/api/auth/nonce'), {
                method: 'GET',
                credentials: 'include',
                headers: { 'Content-Type': 'application/json' }
            });
            if (!res.ok) throw new Error('Failed to get nonce');
            const json = await res.json();
            const nonce = json?.data?.nonce;
            if (!nonce) throw new Error('Malformed nonce response');
            return nonce;
        },

        // Verify the signed message server-side. On success the server sets the
        // httpOnly refresh cookie and returns the access token in the body.
        verifyMessage: async ({ message, signature }) => {
            // address is embedded in the EIP-4361 message and recovered server-side;
            // we also send it to satisfy the existing request model.
            const address = parseAddressFromSiwe(message);
            const res = await fetch(url('/api/auth/wallet'), {
                method: 'POST',
                credentials: 'include',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message, signature, walletAddress: address })
            });
            if (!res.ok) return false;
            const json = await res.json();
            if (!json?.success || !json?.data?.accessToken) return false;

            onAuthenticated?.(json.data.accessToken, json.data.user);
            return true;
        },

        // Restore signed-in state from the httpOnly cookie on reload.
        getSession: async () => {
            const res = await fetch(url('/api/auth/session'), {
                method: 'GET',
                credentials: 'include',
                headers: { 'Content-Type': 'application/json' }
            });
            if (!res.ok) return null;
            const json = await res.json();
            const address = json?.data?.address;
            if (!address) return null;
            return { address, chainId: getChainId() };
        },

        // Server-side logout: revokes refresh + access token and clears the cookie.
        signOut: async () => {
            try {
                await fetch(url('/api/auth/logout'), {
                    method: 'POST',
                    credentials: 'include',
                    headers: { 'Content-Type': 'application/json' }
                });
            } catch (e) {
                // Best-effort; local state is cleared regardless.
                console.warn('[SIWE] signOut request failed:', e?.message);
            }
            onSignOut?.();
            return true;
        },

        signOutOnDisconnect: true
    });
}

// EIP-4361 line 2 is the address. Used only to populate the legacy request
// field; the authoritative address is recovered from the signature server-side.
function parseAddressFromSiwe(message) {
    const match = message.match(/0x[a-fA-F0-9]{40}/);
    return match ? match[0] : null;
}
