// ============================================
// IMPORTS - Reuse packages from main app
// ============================================
import { createAppKit } from '@reown/appkit';
import { EthersAdapter } from '@reown/appkit-adapter-ethers';
import { mainnet, polygon, polygonAmoy, arbitrum, sepolia } from '@reown/appkit/networks';
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
    // Parse message from URL
    if (messageB64) {
        try {
            message = atob(messageB64);
        } catch (e) {
            showError('Invalid message parameter');
            return;
        }
    }

    // Display node information
    document.getElementById('nodeId').textContent = nodeId || 'Unknown';
    document.getElementById('expectedWallet').textContent = expectedWallet || 'Any';
    document.getElementById('hardware').textContent = hardware || 'Unknown';
    document.getElementById('messageText').textContent = message;

    // Setup event listeners
    document.getElementById('connectButton').addEventListener('click', connectWallet);
    document.getElementById('signButton').addEventListener('click', signMessage);
    document.getElementById('copyButton').addEventListener('click', copySignature);

    // Initialize AppKit for better UX
    console.log('[Sign] Initializing AppKit...');
    await initializeAppKit();
});

// ============================================
// APPKIT INITIALIZATION
// ============================================

/**
 * Initialize Reown AppKit with WalletConnect support
 * SECURITY: Provides secure wallet connection with mobile support
 */
async function initializeAppKit() {
    if (appKitModal) {
        return appKitModal;
    }

    try {
        updateLoadingProgress(50);

        // Create AppKit instance - supports both mobile and desktop
        appKitModal = createAppKit({
            adapters: [new EthersAdapter()],
            networks: [polygonAmoy, polygon, mainnet, arbitrum, sepolia],
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
                '--w3m-border-radius-master': '10px'
            }
        });

        updateLoadingProgress(100);
        console.log('[Sign] AppKit initialized successfully');

        // Set up event listeners
        setupAppKitListeners();

        return appKitModal;

    } catch (error) {
        console.error('[Sign] AppKit initialization error:', error);
        updateLoadingProgress(0);

        // If AppKit fails, we can still use browser wallet as fallback
        console.log('[Sign] AppKit failed, browser wallet will be used as fallback');
    }
}

// ============================================
// APPKIT EVENT LISTENERS
// ============================================

function setupAppKitListeners() {
    if (!appKitModal) return;

    // Clean up previous listeners
    if (appKitUnsubscribers.length > 0) {
        appKitUnsubscribers.forEach(unsub => unsub());
        appKitUnsubscribers = [];
    }

    // Subscribe to account changes
    const unsubscribeAccount = appKitModal.subscribeAccount(async (account) => {
        console.log('[Sign] Account state:', account);

        if (account.isConnected && account.address) {
            // User just connected
            if (connectedAddress !== account.address) {
                connectedAddress = account.address;

                try {
                    // Get provider and signer
                    const walletProvider = appKitModal.getWalletProvider();
                    if (!walletProvider) {
                        throw new Error('Wallet provider not available');
                    }

                    ethersProvider = new BrowserProvider(walletProvider);
                    ethersSigner = await ethersProvider.getSigner();

                    console.log('[Sign] Provider and signer ready');

                    // Close modal and show connected state
                    appKitModal.close();
                    showWalletConnected(account.address);

                } catch (error) {
                    console.error('[Sign] Provider error:', error);
                    showError('Failed to setup wallet provider: ' + error.message);
                }
            }
        } else if (!account.isConnected && connectedAddress) {
            // User disconnected
            console.log('[Sign] User disconnected');
            location.reload();
        }
    });

    // Store unsubscribe function
    appKitUnsubscribers = [unsubscribeAccount];
}

// ============================================
// WALLET CONNECTION
// ============================================

/**
 * Connect wallet using AppKit (supports WalletConnect, MetaMask, etc.)
 * SECURITY: Uses industry-standard wallet connection patterns
 */
async function connectWallet() {
    const btn = document.getElementById('connectButton');

    try {
        btn.disabled = true;
        btn.innerHTML = '<div class="spinner"></div> Loading...';
        showLoading(true);
        hideError();

        // Try to open AppKit modal first
        if (appKitModal) {
            console.log('[Sign] Opening AppKit modal...');
            await appKitModal.open();

            // Check if user closed modal without connecting
            setTimeout(() => {
                const currentAddress = appKitModal.getAddress();
                if (!currentAddress) {
                    resetConnectButton(btn);
                    showLoading(false);
                }
            }, 1000);

        } else {
            // Fallback: Use browser wallet directly (MetaMask, etc.)
            console.log('[Sign] Using browser wallet as fallback...');
            await connectBrowserWallet();
        }

    } catch (error) {
        console.error('[Sign] Connection error:', error);
        showError('Failed to connect wallet: ' + error.message);
        resetConnectButton(btn);
        showLoading(false);
    }
}

/**
 * Fallback: Connect using browser wallet (MetaMask, etc.)
 * SECURITY: Direct connection for desktop browsers
 */
async function connectBrowserWallet() {
    if (typeof window.ethereum === 'undefined') {
        throw new Error('No wallet detected. Please install MetaMask or use WalletConnect on mobile.');
    }

    // Request account access
    const accounts = await window.ethereum.request({
        method: 'eth_requestAccounts'
    });

    if (accounts.length === 0) {
        throw new Error('No accounts found');
    }

    ethersProvider = new BrowserProvider(window.ethereum);
    ethersSigner = await ethersProvider.getSigner();
    connectedAddress = await ethersSigner.getAddress();

    showWalletConnected(connectedAddress);
}

// ============================================
// SIGNATURE FLOW
// ============================================

/**
 * Sign the message with connected wallet
 * SECURITY: Message signature proves wallet ownership
 */
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

        // SECURITY: Sign the message
        const signature = await ethersSigner.signMessage(message);

        // SECURITY: Validate signature format
        if (!signature || !signature.match(/^0x[a-fA-F0-9]{130}$/)) {
            throw new Error('Invalid signature format');
        }

        // Display result
        document.getElementById('signatureText').textContent = signature;
        document.getElementById('signatureResult').style.display = 'block';
        document.getElementById('walletConnected').style.display = 'none';
        showLoading(false);

        // Store in localStorage for reference
        localStorage.setItem('lastSignature', signature);
        localStorage.setItem('lastSignatureTime', new Date().toISOString());

        console.log('[Sign] Signature generated successfully');

    } catch (error) {
        console.error('[Sign] Signing error:', error);

        let errorMessage = 'Failed to sign message: ' + error.message;

        // Handle user rejection
        if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            errorMessage = 'Signature request rejected by user';
        } else if (error.message?.includes('User rejected')) {
            errorMessage = 'Signature request rejected by user';
        }

        showError(errorMessage);
        showLoading(false);
    }
}

/**
 * Copy signature to clipboard
 */
function copySignature() {
    const signature = document.getElementById('signatureText').textContent;

    navigator.clipboard.writeText(signature).then(() => {
        const btn = document.getElementById('copyButton');
        const originalText = btn.textContent;
        btn.textContent = '✅ Copied!';
        btn.style.background = '#4caf50';
        btn.style.color = 'white';
        btn.style.border = '2px solid #4caf50';

        setTimeout(() => {
            btn.textContent = originalText;
            btn.style.background = '';
            btn.style.color = '';
            btn.style.border = '';
        }, 2000);
    }).catch(err => {
        showError('Failed to copy: ' + err.message);
    });
}

// ============================================
// UI HELPER FUNCTIONS
// ============================================

function showWalletConnected(address) {
    document.getElementById('connectedWallet').textContent = address;
    document.getElementById('walletSelection').style.display = 'none';
    document.getElementById('walletConnected').style.display = 'block';
    showLoading(false);

    // Check if wallet matches expected
    if (expectedWallet && expectedWallet.toLowerCase() !== address.toLowerCase()) {
        showError(
            `⚠️ Warning: Connected wallet (${address}) does not match expected wallet (${expectedWallet})`,
            false
        );
    }
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

function resetConnectButton(btn) {
    btn.disabled = false;
    btn.innerHTML = 'Connect Wallet & Sign';
}

function updateLoadingProgress(percent) {
    const bar = document.getElementById('sdk-loading-bar');
    const progress = document.getElementById('sdk-loading-progress');

    if (percent > 0 && percent < 100) {
        bar.classList.add('active');
    } else {
        bar.classList.remove('active');
    }

    progress.style.width = `${percent}%`;
}

// ============================================
// ACCOUNT CHANGE LISTENERS
// ============================================

// Listen for account changes in browser wallet
if (typeof window.ethereum !== 'undefined') {
    window.ethereum.on('accountsChanged', (accounts) => {
        if (accounts.length === 0) {
            // Disconnected
            location.reload();
        } else {
            // Account changed
            location.reload();
        }
    });
}

console.log('[Sign] Initialization complete');