// ============================================
// IMPORTS - Reuse packages from main app
// ============================================
import { createAppKit } from '@reown/appkit';
import { EthersAdapter } from '@reown/appkit-adapter-ethers';
import { mainnet, polygon, arbitrum, sepolia } from '@reown/appkit/networks';
import { BrowserProvider } from 'ethers';

// ============================================
// CONFIGURATION
// ============================================

const WALLETCONNECT_PROJECT_ID = import.meta.env.VITE_WALLETCONNECT_PROJECT_ID || '708cede4d366aa77aead71dbc67d8ae5';

const metadata = {
    name: 'DeCloud Node Authorization',
    description: 'Authorize your compute node with wallet signature',
    url: window.location.origin,
    icons: [`${window.location.origin}/favicon.ico`]
};

// ============================================
// STATE
// ============================================

let appKitModal = null;
let ethersProvider = null;
let ethersSigner = null;
let connectedAddress = null;
let appKitUnsubscribers = [];

// Parse URL parameters
const params = new URLSearchParams(window.location.search);
const messageB64 = params.get('message');
const nodeId = params.get('nodeId');
const expectedWallet = params.get('wallet');
const hardware = params.get('hardware');

let message = '';

// ============================================
// INITIALIZATION
// ============================================

document.addEventListener('DOMContentLoaded', async () => {
    // Parse message
    if (messageB64) {
        try {
            message = atob(messageB64);
        } catch (e) {
            showError('Invalid message parameter');
        }
    }

    // Display info
    document.getElementById('nodeId').textContent = nodeId || 'Unknown';
    document.getElementById('expectedWallet').textContent = expectedWallet || 'Any';
    document.getElementById('hardware').textContent = hardware || 'Unknown';
    document.getElementById('messageText').textContent = message;

    // Setup event listeners
    document.getElementById('connectButton').addEventListener('click', connectWallet);
    document.getElementById('signButton').addEventListener('click', signMessage);
    document.getElementById('copyButton').addEventListener('click', copySignature);

    // Initialize AppKit
    await initializeAppKit();
});

// ============================================
// APPKIT INITIALIZATION
// ============================================

async function initializeAppKit() {
    if (appKitModal) {
        return appKitModal;
    }

    try {
        console.log('[AppKit] Initializing...');

        appKitModal = createAppKit({
            adapters: [new EthersAdapter()],
            networks: [polygon, mainnet, arbitrum, sepolia],
            projectId: WALLETCONNECT_PROJECT_ID,
            metadata: metadata,
            features: {
                analytics: false,
                email: false,
                socials: [],
                swaps: false,
                onramp: false
            },
            themeMode: 'light',
            themeVariables: {
                '--w3m-accent': '#667eea',
                '--w3m-border-radius-master': '8px'
            }
        });

        console.log('[AppKit] Initialized successfully');
        setupAppKitListeners();

        return appKitModal;

    } catch (error) {
        console.error('[AppKit] Initialization error:', error);
        throw new Error('Failed to initialize wallet connection. Please refresh and try again.');
    }
}

// ============================================
// APPKIT EVENT LISTENERS
// ============================================

function setupAppKitListeners() {
    if (!appKitModal) return;

    if (appKitUnsubscribers.length > 0) {
        appKitUnsubscribers.forEach(unsub => unsub());
        appKitUnsubscribers = [];
    }

    const unsubscribeAccount = appKitModal.subscribeAccount(async (account) => {
        console.log('[AppKit] Account state:', account);

        if (account.isConnected && account.address) {
            if (connectedAddress !== account.address) {
                connectedAddress = account.address;

                try {
                    const walletProvider = appKitModal.getWalletProvider();
                    if (!walletProvider) {
                        throw new Error('Wallet provider not available');
                    }

                    ethersProvider = new BrowserProvider(walletProvider);
                    ethersSigner = await ethersProvider.getSigner();

                    console.log('[AppKit] Provider and signer ready');
                    showWalletConnected(account.address);

                    if (expectedWallet && expectedWallet.toLowerCase() !== account.address.toLowerCase()) {
                        showError(`Warning: Connected wallet (${account.address}) does not match expected wallet (${expectedWallet})`, false);
                    }

                } catch (error) {
                    console.error('[AppKit] Provider error:', error);
                    showError('Failed to setup wallet provider: ' + error.message);
                }
            }
        } else if (!account.isConnected && connectedAddress) {
            console.log('[AppKit] User disconnected');
            location.reload();
        }
    });

    const unsubscribeNetwork = appKitModal.subscribeNetwork((network) => {
        console.log('[AppKit] Network changed:', network);
    });

    appKitUnsubscribers = [unsubscribeAccount, unsubscribeNetwork];
}

// ============================================
// WALLET CONNECTION
// ============================================

async function connectWallet() {
    try {
        showLoading(true);
        hideError();

        await initializeAppKit();

        console.log('[AppKit] Opening connection modal...');
        await appKitModal.open();

        showLoading(false);

    } catch (error) {
        console.error('[Connect] Error:', error);
        showError('Failed to connect wallet: ' + error.message);
        showLoading(false);
    }
}

// ============================================
// SIGNING
// ============================================

async function signMessage() {
    try {
        if (!ethersSigner) {
            throw new Error('Wallet not connected');
        }

        if (!message) {
            throw new Error('No message to sign');
        }

        showLoading(true);
        hideError();

        console.log('[Sign] Requesting signature...');

        const signature = await ethersSigner.signMessage(message);

        console.log('[Sign] Signature received:', signature);

        // SECURITY: Validate signature format
        if (!signature || !signature.match(/^0x[a-fA-F0-9]{130}$/)) {
            throw new Error('Invalid signature format');
        }

        document.getElementById('signatureText').textContent = signature;
        document.getElementById('signatureResult').style.display = 'block';
        document.getElementById('walletConnected').style.display = 'none';
        showLoading(false);

        localStorage.setItem('lastSignature', signature);
        localStorage.setItem('lastSignatureTime', new Date().toISOString());

    } catch (error) {
        console.error('[Sign] Error:', error);
        showError('Failed to sign message: ' + error.message);
        showLoading(false);
    }
}

// ============================================
// CLIPBOARD
// ============================================

function copySignature() {
    const signature = document.getElementById('signatureText').textContent;
    
    if (navigator.clipboard && window.isSecureContext) {
        navigator.clipboard.writeText(signature).then(() => {
            updateCopyButton('✅ Copied!');
        }).catch(err => {
            fallbackCopy(signature);
        });
    } else {
        fallbackCopy(signature);
    }
}

function fallbackCopy(text) {
    const textarea = document.createElement('textarea');
    textarea.value = text;
    textarea.style.position = 'fixed';
    textarea.style.left = '-999999px';
    document.body.appendChild(textarea);
    textarea.select();
    
    try {
        const successful = document.execCommand('copy');
        if (successful) {
            updateCopyButton('✅ Copied!');
        } else {
            alert('Failed to copy. Please copy manually.');
        }
    } catch (err) {
        alert('Failed to copy. Please copy manually.');
    }
    
    document.body.removeChild(textarea);
}

function updateCopyButton(text) {
    const btn = document.getElementById('copyButton');
    const originalText = btn.textContent;
    btn.textContent = text;
    setTimeout(() => {
        btn.textContent = originalText;
    }, 2000);
}

// ============================================
// UI HELPERS
// ============================================

function showWalletConnected(address) {
    document.getElementById('connectedWallet').textContent = address;
    document.getElementById('walletSelection').style.display = 'none';
    document.getElementById('walletConnected').style.display = 'block';
    showLoading(false);
}

function showLoading(show) {
    document.getElementById('loading').style.display = show ? 'block' : 'none';
}

function showError(msg, hideLoading = true) {
    document.getElementById('error').textContent = msg;
    document.getElementById('error').style.display = 'block';
    if (hideLoading) showLoading(false);
}

function hideError() {
    document.getElementById('error').style.display = 'none';
}

// ============================================
// AUTO-CONNECT CHECK
// ============================================

window.addEventListener('load', async () => {
    try {
        const state = appKitModal?.getState();
        if (state?.selectedNetworkId && state?.address) {
            console.log('[AppKit] Auto-connecting to existing session...');
            
            const walletProvider = appKitModal.getWalletProvider();
            if (walletProvider) {
                ethersProvider = new BrowserProvider(walletProvider);
                ethersSigner = await ethersProvider.getSigner();
                connectedAddress = await ethersSigner.getAddress();
                showWalletConnected(connectedAddress);
            }
        }
    } catch (e) {
        console.log('[AppKit] No existing connection to restore');
    }
});
