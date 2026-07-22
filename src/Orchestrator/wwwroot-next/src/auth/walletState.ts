// The ONLY module that talks to AppKit/ethers. Maps AppKit account/chain events to
// our small WalletState and lazily acquires the ethers signer (needed after a
// session-restore where no fresh connect happened — mirrors app.js getReadySigner).
import { createAppKit } from "@reown/appkit";
import { EthersAdapter } from "@reown/appkit-adapter-ethers";
import { polygonAmoy, polygon, mainnet, arbitrum } from "@reown/appkit/networks";
import { BrowserProvider } from "ethers";
import type { WalletState } from "./types";
import { EXPECTED_CHAIN_ID } from "./types";

const PROJECT_ID =
    (import.meta.env.VITE_WALLETCONNECT_PROJECT_ID as string) || "708cede4d366aa77aead71dbc67d8ae5";

const metadata = {
    name: "DeCloud",
    description: "Decentralized Cloud Computing Platform",
    url: window.location.origin,
    icons: [`${window.location.origin}/favicon.ico`],
};

export interface WalletAdapter {
    getState(): WalletState;
    subscribe(onChange: (state: WalletState) => void): () => void;
    connect(): Promise<void>;
    disconnect(): Promise<void>;
    switchChain(chainId: number): Promise<void>;
    signMessage(message: string): Promise<string>;
}

export function createWalletAdapter(siweConfig: unknown): WalletAdapter {
    const modal = createAppKit({
        adapters: [new EthersAdapter()],
        networks: [polygonAmoy, polygon, mainnet, arbitrum],
        projectId: PROJECT_ID,
        metadata,
        siweConfig: siweConfig as never,
        features: { analytics: false, email: false, socials: [], swaps: false, onramp: false },
    });

    let signer: Awaited<ReturnType<BrowserProvider["getSigner"]>> | null = null;
    let lastAddress: string | null = null;
    const unsubscribers: Array<() => void> = [];

    const chainId = (): number => {
        try {
            return Number((modal as any).getChainId?.()) || EXPECTED_CHAIN_ID;
        } catch {
            return EXPECTED_CHAIN_ID;
        }
    };

    function readState(): WalletState {
        const address = (modal as any).getAddress?.();
        if (!address) return { kind: "disconnected" };
        return { kind: "connected", address, chainId: chainId() };
    }

    async function ensureSigner() {
        if (signer) return signer;
        const provider = (modal as any).getWalletProvider?.();
        if (!provider) throw new Error("Wallet not connected");
        signer = await new BrowserProvider(provider).getSigner();
        return signer;
    }

    return {
        getState: readState,

        subscribe(onChange) {
            const unsub = (modal as any).subscribeAccount((account: { isConnected?: boolean; address?: string }) => {
                if (account.isConnected && account.address) {
                    if (lastAddress !== account.address) {
                        lastAddress = account.address;
                        signer = null; // new address → re-acquire signer (also the account-switch trigger)
                        void ensureSigner().catch(() => { });
                    }
                } else if (!account.isConnected) {
                    lastAddress = null;
                    signer = null;
                }
                onChange(readState());
            });
            unsubscribers.push(unsub);
            return () => {
                unsubscribers.forEach((u) => u());
                unsubscribers.length = 0;
            };
        },

        connect: async () => {
            await (modal as any).open();
        },
        disconnect: async () => {
            await (modal as any).disconnect();
        },
        switchChain: async (id) => {
            await (modal as any).switchNetwork?.({ id });
        },

        async signMessage(message) {
            const s = await ensureSigner();
            return s.signMessage(message);
        },
    };
}