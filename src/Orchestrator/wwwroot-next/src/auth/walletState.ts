import type { WalletState } from "./types";

// Thin adapter that maps Reown AppKit + ethers events into our small WalletState.
// PORT the connect/disconnect/account-change/chain-change wiring from the legacy
// AppKit setup. Every subscription MUST be cleaned up (DESIGN §6.10) — return an
// unsubscribe from subscribe().
//
// This is the ONLY place that talks to AppKit/ethers directly; the rest of auth
// consumes WalletState. That keeps the wallet SDK swappable and the logic testable.

export interface WalletAdapter {
  getState(): WalletState;
  /** Subscribe to wallet changes; returns an unsubscribe. Fires on connect,
   *  disconnect, ACCOUNT SWITCH (address change), and chain change. */
  subscribe(onChange: (state: WalletState) => void): () => void;
  /** Open the connect modal. */
  connect(): Promise<void>;
  disconnect(): Promise<void>;
  /** Prompt a network switch to the expected chain (used by WRONG_NETWORK). */
  switchChain(chainId: number): Promise<void>;
  /** The signer used by SIWE + wallet-crypto (ethers signMessage). */
  signMessage(message: string): Promise<string>;
}

// TODO: implement over AppKit:
//   - map isConnected/address/chainId → WalletState
//   - on 'accountsChanged'  → emit connected(newAddress) (AuthProvider re-auths as new address)
//   - on 'chainChanged'     → emit connected(sameAddress, newChainId)
//   - on disconnect         → emit { kind: 'disconnected' } (AuthProvider signs out; signOutOnDisconnect)
//   - collect every unsubscribe (the legacy `unsubscribers` array) and run them on teardown
export function createWalletAdapter(): WalletAdapter {
  throw new Error("TODO: createWalletAdapter — port the AppKit/ethers wiring");
}
