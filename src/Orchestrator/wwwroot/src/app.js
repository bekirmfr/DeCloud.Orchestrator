// ============================================
// IMPORTS - ES6 Modules
// ============================================
import { createAppKit } from '@reown/appkit';
import { EthersAdapter } from '@reown/appkit-adapter-ethers';
import { mainnet, polygon, arbitrum } from '@reown/appkit/networks';
import { BrowserProvider } from 'ethers';

// @noble/ciphers - Works on HTTP and HTTPS (no secure context required)
import { gcm } from '@noble/ciphers/aes';
import { randomBytes } from '@noble/ciphers/webcrypto';
import { sha256 } from '@noble/hashes/sha256';
import {
    getSSHCertificate,
    showSSHConnectionModal,
    downloadSSHBundle
} from './ssh-wallet.js';

// ============================================
// CONFIGURATION
// ============================================

// SECURITY: Load from environment or use default
const WALLETCONNECT_PROJECT_ID = import.meta.env.VITE_WALLETCONNECT_PROJECT_ID || '708cede4d366aa77aead71dbc67d8ae5';

const CONFIG = {
    orchestratorUrl: localStorage.getItem('orchestratorUrl') || window.location.origin,
    wallet: null,
    metadata: {
        name: 'DeCloud Platform',
        description: 'Decentralized Cloud Computing Platform',
        url: window.location.origin,
        icons: [`${window.location.origin}/favicon.ico`]
    }
};

// ============================================
// STATE
// ============================================
let authToken = null;
let refreshToken = null;
let currentUser = null;
let tokenRefreshTimer = null;
let nodesCache = {};

// Wallet connection state
let ethersProvider = null;
let ethersSigner = null;
let connectedAddress = null;
let appKitModal = null;

// Password encryption cache
let cachedEncryptionKey = null;
const ENCRYPTION_MESSAGE = "DeCloud VM Password Encryption Key v1";

// AppKit unsubscribe functions
let appKitUnsubscribers = [];

// ============================================
// INITIALIZATION
// ============================================
document.addEventListener('DOMContentLoaded', async () => {
    console.log('[App] Initializing DeCloud v' + __APP_VERSION__);
    const sessionRestored = await restoreSession();
    if (!sessionRestored) {
        showLogin();
    }
});

// ============================================
// APPKIT INITIALIZATION
// ============================================

/**
 * Initialize Reown AppKit with modern createAppKit approach
 */
async function initializeAppKit() {
    if (appKitModal) {
        return appKitModal;
    }

    try {
        console.log('[AppKit] Initializing...');
        updateLoadingProgress(50);

        // Create AppKit instance with unified configuration
        appKitModal = createAppKit({
            adapters: [new EthersAdapter()],
            networks: [polygon, mainnet, arbitrum],
            projectId: WALLETCONNECT_PROJECT_ID,
            metadata: CONFIG.metadata,
            features: {
                analytics: true,
                email: false,
                socials: [],
                swaps: false,
                onramp: false
            },
            themeMode: 'dark',
            themeVariables: {
                '--w3m-accent': '#10b981',
                '--w3m-border-radius-master': '8px'
            }
        });

        updateLoadingProgress(100);
        console.log('[AppKit] Initialized successfully');

        // Set up event listeners
        setupAppKitListeners();

        return appKitModal;

    } catch (error) {
        console.error('[AppKit] Initialization error:', error);
        updateLoadingProgress(0);
        throw new Error('Failed to initialize wallet connection. Please refresh and try again.');
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
        console.log('[AppKit] Account state:', account);

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

                    console.log('[AppKit] Provider and signer ready');

                    // Proceed with authentication
                    await proceedWithAuthentication(account.address, 'appkit');
                } catch (error) {
                    console.error('[AppKit] Provider error:', error);
                    handleConnectionError(error);
                }
            }
        } else if (!account.isConnected && connectedAddress) {
            // User disconnected
            console.log('[AppKit] User disconnected');
            disconnect();
        }
    });

    // Subscribe to network changes
    const unsubscribeNetwork = appKitModal.subscribeNetwork((network) => {
        console.log('[AppKit] Network changed:', network);
    });

    // Store unsubscribe functions
    appKitUnsubscribers = [unsubscribeAccount, unsubscribeNetwork];
}

// ============================================
// WALLET CONNECTION
// ============================================

async function connectWallet() {
    const btn = document.getElementById('connect-wallet-btn');

    try {
        btn.disabled = true;
        btn.innerHTML = '<div class="spinner"></div> Loading...';
        showLoginStatus('info', 'Initializing wallet connection...');

        // Initialize AppKit if not already done
        if (!appKitModal) {
            await initializeAppKit();
        }

        // Open AppKit modal - handles everything automatically
        console.log('[Connection] Opening AppKit modal...');
        await appKitModal.open();

        // Reset button after modal opens (it may be closed without connecting)
        setTimeout(() => {
            const currentAddress = appKitModal.getAddress();
            if (!currentAddress) {
                resetConnectButton(btn);
                hideLoginStatus();
            }
        }, 1000);

    } catch (error) {
        console.error('[Connection] Error:', error);
        handleConnectionError(error);
        resetConnectButton(btn);
    }
}

// ============================================
// AUTHENTICATION FLOW
// ============================================

async function proceedWithAuthentication(walletAddress, connectionType) {
    const btn = document.getElementById('connect-wallet-btn');

    try {
        showLoginStatus('info', 'Requesting signature...');
        btn.innerHTML = '<div class="spinner"></div> Sign Message...';

        const authResult = await authenticateWithWallet(walletAddress);

        if (authResult.success) {
            showLoginStatus('success', 'Authentication successful!');
            localStorage.setItem('connectionType', connectionType);
            localStorage.setItem('wallet', walletAddress);
            CONFIG.wallet = walletAddress;

            setTimeout(() => {
                // Close the AppKit modal
                if (appKitModal) {
                    appKitModal.close();
                }

                showDashboard();
                setupTokenRefresh();
                refreshData();
            }, 500);
        } else {
            throw new Error(authResult.error || 'Authentication failed');
        }
    } catch (error) {
        console.error('[Auth] Error:', error);
        handleConnectionError(error);
        resetConnectButton(btn);
    }
}

/**
 * Authenticates with the backend using wallet signature
 * SECURITY: Message signing for proof of ownership
 */
async function authenticateWithWallet(walletAddress) {
    try {
        // SECURITY: Validate address format
        if (!walletAddress || !walletAddress.match(/^0x[a-fA-F0-9]{40}$/)) {
            return { success: false, error: 'Invalid wallet address format' };
        }

        console.log('[Auth] Requesting authentication message for:', walletAddress);

        // Step 1: Get message to sign from server
        const messageResponse = await fetch(
            `${CONFIG.orchestratorUrl}/api/auth/message?walletAddress=${walletAddress}`,
            {
                method: 'GET',
                headers: {
                    'Content-Type': 'application/json'
                }
            }
        );

        if (!messageResponse.ok) {
            const errorText = await messageResponse.text();
            console.error('[Auth] Message endpoint error:', messageResponse.status, errorText);
            return { success: false, error: `Server error: ${messageResponse.status}` };
        }

        const messageData = await messageResponse.json();

        if (!messageData.success) {
            return { success: false, error: messageData.message || 'Failed to get authentication message' };
        }

        const { message, timestamp } = messageData.data;

        // SECURITY: Validate message contains expected components
        if (!message || !timestamp) {
            return { success: false, error: 'Invalid authentication message from server' };
        }

        console.log('[Auth] Requesting signature from wallet...');

        // Step 2: Sign the message with the wallet
        const signature = await ethersSigner.signMessage(message);

        // SECURITY: Validate signature format
        if (!signature || !signature.match(/^0x[a-fA-F0-9]{130}$/)) {
            return { success: false, error: 'Invalid signature format' };
        }

        console.log('[Auth] Signature received, authenticating with server...');

        // Step 3: Authenticate with the server
        const authResponse = await fetch(`${CONFIG.orchestratorUrl}/api/auth/wallet`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({
                walletAddress: walletAddress,
                signature: signature,
                message: message,
                timestamp: timestamp
            })
        });

        if (!authResponse.ok) {
            const errorText = await authResponse.text();
            console.error('[Auth] Authentication endpoint error:', authResponse.status, errorText);
            return { success: false, error: `Authentication failed: ${authResponse.status}` };
        }

        const authData = await authResponse.json();

        if (authData.success && authData.data) {
            // SECURITY: Store tokens
            authToken = authData.data.accessToken;
            refreshToken = authData.data.refreshToken;
            currentUser = authData.data.user;

            localStorage.setItem('authToken', authToken);
            localStorage.setItem('refreshToken', refreshToken);

            console.log('[Auth] Authentication successful');
            return { success: true };
        } else {
            return { success: false, error: authData.message || 'Authentication failed' };
        }
    } catch (error) {
        console.error('[Auth] Authentication error:', error);

        // SECURITY: Don't expose internal error details
        let errorMessage = 'Authentication failed. Please try again.';

        if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            errorMessage = 'Signature request rejected';
        } else if (error.message?.includes('User rejected')) {
            errorMessage = 'Signature request rejected';
        }

        return {
            success: false,
            error: errorMessage
        };
    }
}

// ============================================
// DISCONNECT
// ============================================

async function disconnect() {
    console.log('[Disconnect] Disconnecting wallet...');

    // Disconnect AppKit
    if (appKitModal) {
        try {
            await appKitModal.disconnect();
        } catch (e) {
            console.log('[AppKit] Disconnect error:', e);
        }
    }

    // Cleanup listeners
    if (appKitUnsubscribers.length > 0) {
        appKitUnsubscribers.forEach(unsub => {
            try {
                unsub();
            } catch (e) {
                console.log('[AppKit] Unsubscribe error:', e);
            }
        });
        appKitUnsubscribers = [];
    }

    // Reset state
    ethersProvider = null;
    ethersSigner = null;
    connectedAddress = null;
    clearEncryptionKey();  // Clear cached encryption key

    clearSession();
    showLogin();
    showToast('Disconnected', 'info');
}

function clearSession() {
    authToken = null;
    refreshToken = null;
    currentUser = null;
    CONFIG.wallet = null;
    cachedEncryptionKey = null;

    // SECURITY: Clear sensitive data
    localStorage.removeItem('authToken');
    localStorage.removeItem('refreshToken');
    localStorage.removeItem('wallet');
    localStorage.removeItem('connectionType');

    if (tokenRefreshTimer) {
        clearInterval(tokenRefreshTimer);
        tokenRefreshTimer = null;
    }

    // Update UI
    const walletDisplay = document.getElementById('wallet-display');
    const walletBadge = document.getElementById('wallet-badge');
    const disconnectBtn = document.getElementById('disconnect-btn');

    if (walletDisplay) walletDisplay.textContent = 'Not connected';
    if (walletBadge) walletBadge.classList.remove('connected');
    if (disconnectBtn) disconnectBtn.style.display = 'none';
}

// ============================================
// SESSION RESTORATION
// ============================================

async function restoreSession() {
    const storedToken = localStorage.getItem('authToken');
    const storedRefreshToken = localStorage.getItem('refreshToken');
    const storedWallet = localStorage.getItem('wallet');

    console.log('[Session] Attempting to restore session...');

    if (storedToken && storedRefreshToken && storedWallet) {
        authToken = storedToken;
        refreshToken = storedRefreshToken;
        CONFIG.wallet = storedWallet;

        try {
            // Verify token is still valid
            console.log('[Session] Verifying stored token...');
            const refreshed = await refreshAuthToken();

            if (refreshed) {
                console.log('[Session] Token valid, restoring session');

                // Initialize AppKit in background (non-blocking)
                initializeAppKit().catch(e => console.log('[AppKit] Background init failed:', e));

                showDashboard();
                refreshData();
                return true;
            }
        } catch (e) {
            console.error('[Session] Verification failed:', e);
        }
    }

    console.log('[Session] No valid session found');
    return false;
}

// ============================================
// ERROR HANDLING & UI HELPERS
// ============================================

function handleConnectionError(error) {
    let errorMsg = 'Connection failed. ';

    if (error.code === 4001 || error.message?.includes('rejected')) {
        errorMsg = 'Connection rejected. Please approve the connection request.';
    } else if (error.message?.includes('cancelled')) {
        errorMsg = 'Connection cancelled.';
    } else if (error.message?.includes('timeout')) {
        errorMsg = 'Connection timeout. Please check your network and try again.';
    } else {
        errorMsg = 'Connection failed. Please try again or contact support.';
    }

    showLoginStatus('error', errorMsg);
    console.error('[Connection] Error details:', error);
}

function resetConnectButton(btn) {
    if (!btn) return;

    btn.disabled = false;
    btn.innerHTML = `
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            <path d="M21 12V7a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h7" />
            <path d="M16 12h4a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2h-4a2 2 0 0 1-2-2v-3a2 2 0 0 1 2-2z" />
            <circle cx="18" cy="15" r="1" />
        </svg>
        Connect Wallet
    `;
}

function updateLoadingProgress(percent) {
    const bar = document.getElementById('sdk-loading-progress');
    const container = document.getElementById('sdk-loading-bar');

    if (bar && container) {
        if (percent > 0 && percent < 100) {
            container.style.opacity = '1';
            bar.style.width = percent + '%';
        } else {
            setTimeout(() => {
                container.style.opacity = '0';
                setTimeout(() => {
                    bar.style.width = '0%';
                }, 300);
            }, 500);
        }
    }
}

// ============================================
// TOKEN REFRESH
// ============================================

async function refreshAuthToken() {
    if (!refreshToken) {
        return false;
    }

    try {
        const response = await fetch(`${CONFIG.orchestratorUrl}/api/auth/refresh`, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ refreshToken })
        });

        if (!response.ok) {
            console.error('[Auth] Token refresh failed:', response.status);
            return false;
        }

        const data = await response.json();

        if (data.success && data.data) {
            authToken = data.data.accessToken;
            refreshToken = data.data.refreshToken;
            currentUser = data.data.user;

            localStorage.setItem('authToken', authToken);
            localStorage.setItem('refreshToken', refreshToken);

            console.log('[Auth] Token refreshed successfully');
            return true;
        }

        return false;
    } catch (error) {
        console.error('[Auth] Token refresh error:', error);
        return false;
    }
}

function setupTokenRefresh() {
    if (tokenRefreshTimer) {
        clearInterval(tokenRefreshTimer);
    }

    tokenRefreshTimer = setInterval(async () => {
        console.log('[Auth] Auto-refreshing token...');
        const refreshed = await refreshAuthToken();
        if (!refreshed) {
            console.error('[Auth] Auto-refresh failed, logging out');
            disconnect();
        }
    }, 50 * 60 * 1000);
}

// ============================================
// API HELPER
// ============================================

async function api(endpoint, options = {}) {
    const headers = {
        'Content-Type': 'application/json',
        ...(authToken ? { 'Authorization': `Bearer ${authToken}` } : {}),
        ...options.headers
    };

    const response = await fetch(`${CONFIG.orchestratorUrl}${endpoint}`, {
        ...options,
        headers
    });

    if (response.status === 401) {
        console.log('[API] 401 received, attempting token refresh...');
        const refreshed = await refreshAuthToken();

        if (refreshed) {
            headers['Authorization'] = `Bearer ${authToken}`;
            return fetch(`${CONFIG.orchestratorUrl}${endpoint}`, {
                ...options,
                headers
            });
        } else {
            disconnect();
            throw new Error('Session expired. Please log in again.');
        }
    }

    return response;
}

// ============================================
// PASSWORD ENCRYPTION - @noble/ciphers
// Works on HTTP and HTTPS - No secure context required!
// ============================================

/**
 * Get encryption key from wallet signature
 * Uses @noble/ciphers - works everywhere (HTTP, HTTPS, localhost)
 * 
 * @returns {Promise<Uint8Array>} 32-byte AES-256 key
 */
async function getEncryptionKey() {
    if (cachedEncryptionKey) {
        return cachedEncryptionKey;
    }

    if (!ethersSigner) {
        throw new Error('Wallet not connected');
    }

    try {
        console.log('[Encryption] Deriving key from wallet signature...');

        // Get wallet signature
        const signature = await ethersSigner.signMessage(ENCRYPTION_MESSAGE);

        // Hash the signature to get a consistent 32-byte key using SHA-256
        // This is secure and deterministic (same wallet = same key)
        const keyMaterial = sha256(new TextEncoder().encode(signature));

        // Cache the key (32 bytes for AES-256)
        cachedEncryptionKey = keyMaterial;

        console.log('[Encryption] ✓ Key derived successfully');
        return cachedEncryptionKey;
    } catch (error) {
        console.error('[Encryption] Failed to generate key:', error);
        throw new Error(`Key generation failed: ${error.message}`);
    }
}

/**
 * Encrypt password using AES-256-GCM
 * Uses @noble/ciphers - works everywhere (HTTP, HTTPS, localhost)
 * 
 * @param {string} password - Plain text password to encrypt
 * @returns {Promise<string>} Base64-encoded encrypted data (nonce + ciphertext + tag)
 */
async function encryptPassword(password) {
    try {
        console.log('[Encryption] Encrypting password...');

        // Get encryption key (32 bytes for AES-256)
        const key = await getEncryptionKey();

        // Generate random 12-byte nonce (IV) for GCM mode
        // Using @noble/ciphers randomBytes - works on HTTP!
        const nonce = randomBytes(12);

        // Convert password to bytes
        const plaintext = new TextEncoder().encode(password);

        // Create AES-256-GCM cipher using @noble/ciphers
        // This works on HTTP, HTTPS, and localhost - no crypto.subtle needed!
        const cipher = gcm(key, nonce);

        // Encrypt (produces ciphertext + 16-byte authentication tag)
        const ciphertext = cipher.encrypt(plaintext);

        // Combine nonce + ciphertext (which includes auth tag)
        // Format: [12-byte nonce][ciphertext + 16-byte tag]
        const combined = new Uint8Array(nonce.length + ciphertext.length);
        combined.set(nonce, 0);
        combined.set(ciphertext, nonce.length);

        // Convert to base64 for storage/transmission
        const encrypted = btoa(String.fromCharCode(...combined));

        console.log('[Encryption] ✓ Password encrypted successfully');
        return encrypted;
    } catch (error) {
        console.error('[Encryption] Failed to encrypt:', error);
        throw new Error(`Encryption failed: ${error.message}`);
    }
}

/**
 * Decrypt password using AES-256-GCM
 * Uses @noble/ciphers - works everywhere (HTTP, HTTPS, localhost)
 * 
 * @param {string} encryptedPassword - Base64-encoded encrypted data
 * @returns {Promise<string>} Decrypted plain text password
 */
async function decryptPassword(encryptedPassword) {
    try {
        console.log('[Decryption] Decrypting password...');

        // Get encryption key (must be same wallet that encrypted)
        const key = await getEncryptionKey();

        // Decode base64 to bytes
        const combined = Uint8Array.from(atob(encryptedPassword), c => c.charCodeAt(0));

        // Split into nonce and ciphertext
        const nonce = combined.slice(0, 12);
        const ciphertext = combined.slice(12);

        // Create AES-256-GCM cipher using @noble/ciphers
        const cipher = gcm(key, nonce);

        // Decrypt (automatically verifies authentication tag)
        const plaintext = cipher.decrypt(ciphertext);

        // Convert bytes to string
        const password = new TextDecoder().decode(plaintext);

        console.log('[Decryption] ✓ Password decrypted successfully');
        return password;
    } catch (error) {
        console.error('[Decryption] Failed to decrypt:', error);

        // Provide helpful error messages
        if (error.message.includes('Invalid')) {
            throw new Error('Decryption failed: Invalid key or corrupted data. Make sure you\'re using the same wallet.');
        }

        throw new Error(`Decryption failed: ${error.message}`);
    }
}

/**
 * Clear cached encryption key
 * Call this when wallet disconnects
 */
function clearEncryptionKey() {
    cachedEncryptionKey = null;
    console.log('[Encryption] Key cache cleared');
}

// ============================================
// UI FUNCTIONS
// ============================================

function showLogin() {
    document.getElementById('login-overlay').classList.add('active');
    document.getElementById('login-overlay').style.display = 'flex';
    document.getElementById('app-container').style.display = 'none';
}

function showDashboard() {
    document.getElementById('login-overlay').classList.remove('active');
    document.getElementById('login-overlay').style.display = 'none';
    document.getElementById('app-container').style.display = 'flex';

    if (CONFIG.wallet) {
        const shortAddress = `${CONFIG.wallet.slice(0, 6)}...${CONFIG.wallet.slice(-4)}`;
        const walletDisplay = document.getElementById('wallet-display');
        const walletBadge = document.getElementById('wallet-badge');
        const disconnectBtn = document.getElementById('disconnect-btn');
        const settingsWallet = document.getElementById('settings-wallet');

        if (walletDisplay) walletDisplay.textContent = shortAddress;
        if (walletBadge) walletBadge.classList.add('connected');
        if (disconnectBtn) disconnectBtn.style.display = 'block';
        if (settingsWallet) settingsWallet.value = CONFIG.wallet;
    }
}

function showLoginStatus(type, message) {
    const status = document.getElementById('login-status');
    if (!status) return;

    status.className = `login-status ${type}`;
    status.textContent = message;
    status.style.display = 'block';
}

function hideLoginStatus() {
    const status = document.getElementById('login-status');
    if (status) {
        status.style.display = 'none';
    }
}

function showToast(message, type = 'info') {
    const container = document.getElementById('toast-container');
    if (!container) return;

    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.textContent = message;

    container.appendChild(toast);

    setTimeout(() => toast.classList.add('show'), 10);

    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}

function showPage(pageName) {
    document.querySelectorAll('.page').forEach(page => {
        page.classList.remove('active');
    });

    document.querySelectorAll('.nav-item').forEach(item => {
        item.classList.remove('active');
    });

    const selectedPage = document.getElementById(`page-${pageName}`);
    if (selectedPage) {
        selectedPage.classList.add('active');
    }

    const selectedNav = document.querySelector(`.nav-item[data-page="${pageName}"]`);
    if (selectedNav) {
        selectedNav.classList.add('active');
    }

    if (pageName === 'dashboard' || pageName === 'virtual-machines') {
        refreshData();
    } else if (pageName === 'nodes') {
        loadNodes();
    } else if (pageName === 'ssh-keys') {
        loadSSHKeys();
    }
}

// ============================================
// DATA LOADING FUNCTIONS
// ============================================

async function refreshData() {
    await Promise.all([
        loadDashboardStats(),
        loadVirtualMachines()
    ]);
}

async function loadDashboardStats() {
    try {
        const response = await api('/api/system/stats');  // ← Fix endpoint
        const data = await response.json();

        if (data.success) {
            const stats = data.data;

            document.getElementById('stat-vms').textContent = stats.totalVms || 0;
            document.getElementById('stat-nodes').textContent = stats.onlineNodes || 0;
            document.getElementById('stat-cpu').textContent = `${stats.availableCpuCores || 0} cores`;
            document.getElementById('stat-memory').textContent = `${((stats.availableMemoryMb || 0) / 1024).toFixed(1)} GB`;
        }
    } catch (error) {
        console.error('[Dashboard] Failed to load stats:', error);
    }
}

async function loadVirtualMachines() {
    try {
        const response = await api('/api/vms');
        const data = await response.json();

        if (data.success) {
            const vms = data.data.items;
            renderVMsTable(vms);
            renderDashboardVMs(vms);
        }
    } catch (error) {
        console.error('[VMs] Failed to load:', error);
        showToast('Failed to load virtual machines', 'error');
    }
}

function renderVMsTable(vms) {
    const tbody = document.getElementById('vms-table-body');
    if (!tbody) return;

    if (vms.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align: center; padding: 40px; color: #6b7280;">No VMs found. Create your first VM to get started.</td></tr>';
        return;
    }

    tbody.innerHTML = vms.map(vm => {
        const networkConfig = vm.networkConfig || {};

        // VM network details
        const vmIp = networkConfig.privateIp || 'pending';
        const hostname = networkConfig.hostname || vm.name;

        // Node connection details (for SSH and web terminal)
        const sshJumpHost = networkConfig.sshJumpHost || 'pending';
        const sshJumpPort = networkConfig.sshJumpPort || 22;
        const nodeAgentHost = networkConfig.nodeAgentHost || 'pending';
        const nodeAgentPort = networkConfig.nodeAgentPort || 5100;

        // Display node name from cache
        const nodeName = vm.nodeId ? (nodesCache[vm.nodeId] || vm.nodeId.substring(0, 8)) : 'None';

        // Only enable connect button if VM is running and has connection details
        const canConnect = vm.status === 3 &&
            sshJumpHost !== 'pending' &&
            vmIp !== 'pending';

        return `
        <tr>
            <td>
                <div class="vm-name">
                    <div class="vm-status ${getStatusClass(vm.status)}"></div>
                    ${escapeHtml(vm.name)}
                </div>
            </td>
            <td>${escapeHtml(nodeName)}</td>
            <td>${vm.spec?.cpuCores || 0} cores</td>
            <td>${vm.spec?.memoryMb || 0} MB</td>
            <td>${vm.spec?.diskGb || 0} GB</td>
            <td>
                <span class="status-badge status-${getStatusClass(vm.status)}">
                    ${getStatusText(vm.status)}
                </span>
            </td>
            <td>
                <div class="table-actions">
                    <!-- Connect Info -->
            <button class="btn btn-sm btn-primary" 
                    onclick="showConnectInfo('${sshJumpHost}', ${sshJumpPort}, '${vmIp}', '${vm.name}', '${nodeAgentHost}', ${nodeAgentPort})" 
                    title="Connection Info">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>
                    <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>
                </svg>
            </button>

            <!-- Terminal -->
            <button class="btn btn-sm" 
                    onclick="openTerminal('${vm.name}', '${nodeAgentHost}', ${nodeAgentPort}, '${vmIp}')" 
                    title="Open Terminal">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <polyline points="4 17 10 11 4 5"/>
                    <line x1="12" y1="19" x2="20" y2="19"/>
                </svg>
            </button>

            <!-- File Browser -->
            <button class="btn btn-sm" 
                    onclick="openFileBrowser('${vm.name}', '${nodeAgentHost}', ${nodeAgentPort}, '${vmIp}')" 
                    title="File Browser">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                </svg>
            </button>
            <!-- Reveal Password -->
            <button class="btn-icon" 
                            onclick="window.revealPassword('${vm.id}')" 
                            title="Show Password">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/>
                            <circle cx="12" cy="12" r="3"/>
                        </svg>
                    </button>
            <!-- Start/Stop -->
            ${vm.state === 'Running' 
                ? `<button class="btn btn-sm btn-warning" onclick="stopVm('${vm.id}')" title="Stop">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg>
                   </button>`
                : `<button class="btn btn-sm btn-success" onclick="startVm('${vm.id}')" title="Start">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><polygon points="5 3 19 12 5 21 5 3"/></svg>
                   </button>`
            }

            <!-- Delete -->
            <button class="btn btn-sm btn-danger" onclick="deleteVM('${vm.id}', '${vm.name}')" title="Delete">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <polyline points="3 6 5 6 21 6"/>
                    <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                </svg>
            </button>
                </div>
            </td>
        </tr>
    `}).join('');
}

function renderDashboardVMs(vms) {
    const container = document.getElementById('recent-vms');
    if (!container) return;

    const recentVMs = vms.slice(0, 5);

    if (recentVMs.length === 0) {
        container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 20px;">No virtual machines yet</p>';
        return;
    }

    container.innerHTML = recentVMs.map(vm => `
        <div class="vm-card">
            <div class="vm-card-header">
                <div class="vm-name">
                    <div class="vm-status ${vm.status}"></div>
                    ${vm.name}
                </div>
                <span class="status-badge status-${vm.status}">${vm.status}</span>
            </div>
            <div class="vm-card-specs">
                <div class="spec-item">
                    <span class="spec-label">CPU</span>
                    <span class="spec-value">${vm.spec?.cpuCores || 0} cores</span>
                </div>
                <div class="spec-item">
                    <span class="spec-label">Memory</span>
                    <span class="spec-value">${vm.spec?.memoryMb || 0} MB</span>
                </div>
                <div class="spec-item">
                    <span class="spec-label">Disk</span>
                    <span class="spec-value">${vm.spec?.diskGb || 0} GB</span>
                </div>
            </div>
        </div>
    `).join('');
}

async function loadNodes() {
    try {
        const response = await api('/api/nodes');
        const data = await response.json();

        if (data.success) {
            const nodes = data.data;

            nodes.forEach(node => {
                nodesCache[node.id] = node.name;
            });

            renderNodesTable(nodes);
        }
    } catch (error) {
        console.error('[Nodes] Failed to load:', error);
        showToast('Failed to load nodes', 'error');
    }
}

function renderNodesTable(nodes) {
    const tbody = document.getElementById('nodes-table-body');
    if (!tbody) return;

    if (!nodes || nodes.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align: center; padding: 40px; color: #6b7280;">No nodes registered</td></tr>';
        return;
    }

    tbody.innerHTML = nodes.map(node => {
        const lastSeen = node.lastHeartbeat ? new Date(node.lastHeartbeat).toLocaleString() : 'Never';
        const isOnline = node.status === 'online';

        return `
        <tr>
            <td>
                <div class="vm-name">
                    <div class="vm-status ${isOnline ? 'running' : 'stopped'}"></div>
                    ${node.name}
                </div>
            </td>
            <td>${node.publicIp || 'N/A'}</td>
            <td>${node.totalResources?.cpuCores || 0} cores</td>
            <td>${((node.totalResources?.memoryMb || 0) / 1024).toFixed(1)} GB</td>
            <td>${node.activeVMs || 0}</td>
            <td>${lastSeen}</td>
            <td>
                <span class="status-badge status-${isOnline ? 'running' : 'stopped'}">
                    ${node.status}
                </span>
            </td>
        </tr>
    `}).join('');
}

async function loadSSHKeys() {
    try {
        const response = await api('/api/ssh-keys');
        const data = await response.json();

        if (data.success) {
            renderSSHKeysTable(data.data);
        }
    } catch (error) {
        console.error('[SSH Keys] Failed to load:', error);
        showToast('Failed to load SSH keys', 'error');
    }
}

function renderSSHKeysTable(keys) {
    const tbody = document.getElementById('ssh-keys-table-body');
    if (!tbody) return;

    if (!keys || keys.length === 0) {
        tbody.innerHTML = '<tr><td colspan="4" style="text-align: center; padding: 40px; color: #6b7280;">No SSH keys added. Add a key to connect to your VMs.</td></tr>';
        return;
    }

    tbody.innerHTML = keys.map(key => {
        const added = new Date(key.createdAt).toLocaleDateString();
        const fingerprint = key.fingerprint || 'N/A';

        return `
        <tr>
            <td>${key.name}</td>
            <td><code style="font-size: 12px; color: #9ca3af;">${fingerprint}</code></td>
            <td>${added}</td>
            <td>
                <div class="table-actions">
                    <button class="btn-icon btn-icon-danger" onclick="window.deleteSSHKey('${key.id}', '${key.name}')" title="Delete">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                        </svg>
                    </button>
                </div>
            </td>
        </tr>
    `}).join('');
}

// ============================================
// VM OPERATIONS
// ============================================

function openCreateVMModal() {
    document.getElementById('create-vm-modal').classList.add('active');
}

function closeModal(modalId) {
    document.getElementById(modalId).classList.remove('active');
}

async function createVM() {
    const name = document.getElementById('vm-name').value.trim();
    const cpu = parseInt(document.getElementById('vm-cpu').value);
    const memory = parseInt(document.getElementById('vm-memory').value);
    const disk = parseInt(document.getElementById('vm-disk').value);
    const image = document.getElementById('vm-image').value;

    if (!name) {
        showToast('Please enter a VM name', 'error');
        return;
    }

    try {
        const response = await api('/api/vms', {
            method: 'POST',
            body: JSON.stringify({
                name,
                spec: {
                    cpuCores: cpu,
                    memoryMb: memory,
                    diskGb: disk,
                    imageId: image,
                    requiresGpu: false
                }
            })
        });

        const data = await response.json();

        if (data.success) {
            const vmId = data.data.vmId;
            const password = data.data.password

            // Only show password modal if we got a valid password (not an error code)
            if (password && !password.includes('_') && password.includes('-')) {
                await showPasswordModal(vmId, name, password);
            }
            showToast('Virtual machine created successfully', 'success');
            closeModal('create-vm-modal');
            refreshData();

            document.getElementById('vm-name').value = '';
            document.getElementById('vm-cpu').value = '2';
            document.getElementById('vm-memory').value = '2048';
            document.getElementById('vm-disk').value = '20';
        } else {
            showToast(data.message || 'Failed to create VM', 'error');
        }
    } catch (error) {
        console.error('[VM] Create error:', error);
        showToast('Failed to create virtual machine', 'error');
    }
}

async function deleteVM(vmId, vmName) {
    if (!confirm(`Are you sure you want to delete "${vmName}"? This action cannot be undone.`)) {
        return;
    }

    try {
        const response = await api(`/api/vms/${vmId}`, {
            method: 'DELETE'
        });

        const data = await response.json();

        if (data.success) {
            showToast(`Deleting virtual machine ${vmName}`, 'success');
            refreshData();
        } else {
            showToast(data.message || 'Failed to delete VM', 'error');
        }
    } catch (error) {
        console.error('[VM] Delete error:', error);
        showToast(`Failed to delete virtual machine ${vmName}`, 'error');
    }
}

/**
* Show password modal and handle encryption
*/
async function showPasswordModal(vmId, vmName, password) {
    return new Promise((resolve) => {
        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.id = 'password-modal';
        modal.innerHTML = `
                    <div class="modal-content" style="max-width: 550px;">
                        <h3>🔐 Save Your VM Password</h3>
                        <p>Your VM <strong>${vmName}</strong> has been created with this password:</p>
                
                        <div style="background: #1a1b26; padding: 20px; border-radius: 8px; margin: 20px 0; text-align: center;">
                            <code style="font-size: 1.5em; color: #10b981; letter-spacing: 1px;" id="password-display">${password}</code>
                        </div>
                
                        <div style="background: #2d1f1f; border: 1px solid #7f1d1d; padding: 15px; border-radius: 8px; margin: 15px 0;">
                            <p style="color: #fca5a5; margin: 0;">
                                <strong>⚠️ Important:</strong> This password will be encrypted with your wallet and stored securely. 
                                You can always retrieve it by signing with your wallet, but <strong>save it now</strong> as a backup.
                            </p>
                        </div>
                
                        <div style="margin-top: 20px; display: flex; gap: 10px; justify-content: flex-end;">
                            <button onclick="copyToClipboard('${password}')" class="btn btn-secondary">
                                📋 Copy Password
                            </button>
                            <button onclick="secureAndClose('${vmId}', '${password}')" class="btn btn-primary">
                                🔒 Secure & Continue
                            </button>
                        </div>
                    </div>
                `;
        document.body.appendChild(modal);

        window.secureAndClose = async (vmId, password) => {
            try {
                // Encrypt password (key derived from wallet internally)
                const encryptedPassword = await encryptPassword(password);

                // Store encrypted password on server
                await api(`/api/vms/${vmId}/secure-password`, {
                    method: 'POST',
                    body: JSON.stringify({ encryptedPassword })
                });

                showToast('Password secured with your wallet!', 'success');
                modal.remove();
                resolve();
            } catch (error) {
                console.error('Failed to secure password:', error);
                showToast('Failed to encrypt - please save password manually!', 'error');
            }
        };
    });
}

/**
 * Reveal password for a VM (requires wallet signature)
 */
async function revealPassword(vmId, vmName) {
    try {
        // Get encrypted password from server
        const response = await api(`/api/vms/${vmId}/encrypted-password`);
        const data = await response.json();

        if (!data.success || !data.data || !data.data.encryptedPassword) {
            showToast('Password not available', 'error');
            return;
        }

        // Decrypt (key derived from wallet internally)
        const password = await decryptPassword(data.data.encryptedPassword);

        // Show in modal
        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.innerHTML = `
                    <div class="modal-content" style="max-width: 450px;">
                        <h3>🔑 VM Password</h3>
                        <p>Password for <strong>${vmName}</strong>:</p>
                
                        <div style="background: #1a1b26; padding: 20px; border-radius: 8px; margin: 15px 0; text-align: center;">
                            <code style="font-size: 1.4em; color: #10b981;">${password}</code>
                        </div>
                
                        <div style="display: flex; gap: 10px; justify-content: flex-end;">
                            <button onclick="copyToClipboard('${password}')" class="btn btn-secondary">
                                📋 Copy
                            </button>
                            <button onclick="this.closest('.modal-overlay').remove()" class="btn btn-primary">
                                Close
                            </button>
                        </div>
                    </div>
                `;
        document.body.appendChild(modal);
        modal.onclick = (e) => { if (e.target === modal) modal.remove(); };

    } catch (error) {
        console.error('Failed to reveal password:', error);
        showToast('Failed to decrypt password. Make sure you\'re using the same wallet.', 'error');
    }
}

// ============================================
// SSH KEYS
// ============================================

function openAddSSHKeyModal() {
    document.getElementById('add-ssh-key-modal').classList.add('active');
}

async function addSSHKey() {
    const name = document.getElementById('ssh-key-name').value.trim();
    const publicKey = document.getElementById('ssh-key-public').value.trim();

    if (!name || !publicKey) {
        showToast('Please fill in all fields', 'error');
        return;
    }

    try {
        const response = await api('/api/ssh-keys', {
            method: 'POST',
            body: JSON.stringify({ name, publicKey })
        });

        const data = await response.json();

        if (data.success) {
            showToast('SSH key added successfully', 'success');
            closeModal('add-ssh-key-modal');
            loadSSHKeys();

            document.getElementById('ssh-key-name').value = '';
            document.getElementById('ssh-key-public').value = '';
        } else {
            showToast(data.message || 'Failed to add SSH key', 'error');
        }
    } catch (error) {
        console.error('[SSH] Add key error:', error);
        showToast('Failed to add SSH key', 'error');
    }
}

async function deleteSSHKey(keyId, keyName) {
    if (!confirm(`Delete SSH key "${keyName}"?`)) {
        return;
    }

    try {
        const response = await api(`/api/ssh-keys/${keyId}`, {
            method: 'DELETE'
        });

        const data = await response.json();

        if (data.success) {
            showToast('SSH key deleted', 'success');
            loadSSHKeys();
        } else {
            showToast(data.message || 'Failed to delete SSH key', 'error');
        }
    } catch (error) {
        console.error('[SSH] Delete key error:', error);
        showToast('Failed to delete SSH key', 'error');
    }
}

// ============================================
// FILE BROWSER INTEGRATION
// ============================================

/**
* Open file browser for VM
* @param {string} vmId - VM ID
* @param {string} nodeAgentHost - Node Agent host (from networkConfig)
* @param {number} nodeAgentPort - Node Agent port (from networkConfig)
* @param {string} vmIp - VM private IP
*/
function openFileBrowser(vmId, nodeAgentHost, nodeAgentPort, vmIp) {
    // Build file browser URL with connection parameters
    const params = new URLSearchParams({
        vmId: vmId,
        nodeIp: nodeAgentHost,
        nodePort: nodeAgentPort,
        vmIp: vmIp
    });

    window.open(`/file-browser.html?${params.toString()}`, '_blank');
}

/**
* Open file browser with password (for VMs using password auth)
* This retrieves the decrypted password and passes it to the file browser
* @param {string} vmId - VM ID
* @param {string} nodeAgentHost - Node Agent host
* @param {number} nodeAgentPort - Node Agent port
* @param {string} vmIp - VM private IP
*/
async function openFileBrowserWithAuth(vmId, nodeAgentHost, nodeAgentPort, vmIp) {
    try {
        // Try to get the VM password from the API
        const response = await api(`/api/vms/${vmId}/password`);
        const data = await response.json();
        
        if (data.success && data.data?.password) {
            // Open file browser with password
            const params = new URLSearchParams({
                vmId: vmId,
                nodeIp: nodeAgentHost,
                nodePort: nodeAgentPort,
                vmIp: vmIp,
                password: data.data.password
            });
            window.open(`/file-browser.html?${params.toString()}`, '_blank');
        } else {
            // Fall back to opening without password (user will need to enter it)
            openFileBrowser(vmId, nodeAgentHost, nodeAgentPort, vmIp);
        }
    } catch (error) {
        console.error('[FileBrowser] Failed to get VM password:', error);
        // Fall back to opening without password
        openFileBrowser(vmId, nodeAgentHost, nodeAgentPort, vmIp);
    }
}

// ============================================
// TERMINAL & CONNECT INFO
// ============================================

/**
 * Open web terminal for VM
 * @param {string} vmId - VM ID
 * @param {string} nodeAgentHost - Node Agent host (from networkConfig)
 * @param {number} nodeAgentPort - Node Agent port (from networkConfig)
 * @param {string} vmIp - VM private IP
 */
function openTerminal(vmId, nodeAgentHost, nodeAgentPort, vmIp) {
    // Build terminal URL with connection parameters
    const params = new URLSearchParams({
        vmId: vmId,
        nodeIp: nodeAgentHost,
        nodePort: nodeAgentPort,
        vmIp: vmIp,
        //autoConnect: 'true'
    });

    window.open(`/terminal.html?${params.toString()}`, '_blank');
}

/**
    * Display SSH connection information modal (UPDATED WITH FILE BROWSER)
    * @param {string} sshJumpHost - Node's public IP for SSH jump host
    * @param {number} sshJumpPort - SSH port on node (typically 22)
    * @param {string} vmIp - VM's private IP address
    * @param {string} vmName - VM's display name
    * @param {string} nodeAgentHost - Node Agent API host (for web terminal)
    * @param {number} nodeAgentPort - Node Agent API port (for web terminal)
    */
function showConnectInfo(sshJumpHost, sshJumpPort, vmIp, vmName, nodeAgentHost, nodeAgentPort) {
    // Remove existing modal
    const existing = document.getElementById('connect-info-modal');
    if (existing) existing.remove();

    const modal = document.createElement('div');
    modal.id = 'connect-info-modal';
    modal.style.cssText = `
        position: fixed; inset: 0; background: rgba(0,0,0,0.7);
        display: flex; align-items: center; justify-content: center; z-index: 1000;
    `;

    modal.innerHTML = `
        <div style="background: #1a1d26; border: 1px solid #2a2d36; border-radius: 12px; padding: 28px; width: 520px; max-width: 90vw; color: #f0f2f5;">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">
                <h3 style="margin: 0; font-size: 1.25rem; color: #00d4aa;">🔗 Connect to ${vmName}</h3>
                <button onclick="this.closest('#connect-info-modal').remove()" style="background: none; border: none; color: #6b7280; cursor: pointer; font-size: 1.5rem;">&times;</button>
            </div>

            <!-- Quick Actions -->
            <div class="connect-section" style="background: #12141a; padding: 16px; border-radius: 8px; margin-bottom: 16px;">
                <div style="color: #10b981; font-weight: 600; margin-bottom: 12px;">⚡ Quick Actions</div>
                <div style="display: flex; gap: 10px; flex-wrap: wrap;">
                    <button class="btn btn-sm btn-primary" onclick="showSshInstructions('${sshJumpHost}', '${sshJumpPort}', ${vmIp}, '${vmName}', '${nodeAgentHost}', '${nodeAgentPort}')" style="padding: 8px 16px; font-size: 0.9rem;">
                        🖥️ Open Terminal
                    </button>
                    <button class="btn btn-sm btn-primary" onclick="openTerminal('${vmName}', '${nodeAgentHost}', ${nodeAgentPort}, '${vmIp}')" style="padding: 8px 16px; font-size: 0.9rem;">
                        🖥️ Open Terminal
                    </button>
                    <button class="btn btn-sm btn-secondary" onclick="openFileBrowser('${vmName}', '${nodeAgentHost}', ${nodeAgentPort}, '${vmIp}')" style="padding: 8px 16px; font-size: 0.9rem; background: #1e3a8a; border-color: #3b82f6;">
                        📁 File Browser
                    </button>
                </div>
            </div>

            <!-- SSH Connection Details -->
            <div class="connect-section" style="background: #12141a; padding: 16px; border-radius: 8px; margin-bottom: 16px;">
                <div style="color: #93c5fd; font-weight: 600; margin-bottom: 12px;">🔐 SSH Connection</div>
                <table style="width: 100%; font-size: 0.9rem;">
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af; width: 120px;">Bastion Host:</td>
                        <td style="padding: 6px 0;"><code style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;">${sshJumpHost}:${sshJumpPort}</code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">VM IP:</td>
                        <td style="padding: 6px 0;"><code style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;">${vmIp}</code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">Username:</td>
                        <td style="padding: 6px 0;"><code style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;">ubuntu</code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">Auth:</td>
                        <td style="padding: 6px 0;"><span style="color: #10b981;">✓ SSH Certificate (wallet-derived)</span></td>
                    </tr>
                </table>
            </div>

            <!-- Security Info -->
            <div class="connect-section" style="background: #1e3a8a; border-left: 4px solid #3b82f6; padding: 15px; border-radius: 8px;">
                <div style="color: #93c5fd; font-weight: 600; margin-bottom: 8px;">🔒 Security</div>
                <ul style="color: #bfdbfe; font-size: 0.875rem; margin: 0; padding-left: 20px;">
                    <li>All file transfers use SFTP (encrypted over SSH)</li>
                    <li>Certificates are valid for 1 hour and can be renewed anytime</li>
                    <li>Multi-tenant isolation: Your access only works for your VMs</li>
                </ul>
            </div>
        </div>
    `;

    document.body.appendChild(modal);
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };
}

function showSshInstructions(sshJumpHost, sshJumpPort, vmIp, vmName, nodeAgentHost, nodeAgentPort) {
    // ========================================================================
    // SECURITY VALIDATION: Input Sanitization
    // ========================================================================

    if (!isValidIp(sshJumpHost) || !isValidIp(vmIp)) {
        showToast('Invalid IP address format', 'error');
        return;
    }

    if (!isValidIp(nodeAgentHost)) {
        showToast('Invalid Node Agent host format', 'error');
        return;
    }

    if (sshJumpPort < 1 || sshJumpPort > 65535 || nodeAgentPort < 1 || nodeAgentPort > 65535) {
        showToast('Invalid port number', 'error');
        return;
    }

    // Build SSH commands using SSH config (recommended approach)
    const sshConfigCommand = `ssh ${escapeHtml(vmIp)}`;

    // Build direct ProxyJump command (alternative)
    const proxyJumpCommand = `
        ssh -p ${sshJumpPort} -i ~/.ssh/decloud-wallet.pem \\
        -o CertificateFile=~/.ssh/decloud-XXXXX-cert.pub \\
        -J decloud@${escapeHtml(sshJumpHost)}:${sshJumpPort} \\
        ubuntu@${escapeHtml(vmIp)}`;

    // Build SSH config file content
    const sshConfigContent = `
        # DeCloud SSH Configuration
        # Add this to ~/.ssh/config (or C:\\\\Users\\\\USERNAME\\\\.ssh\\\\config on Windows)
        Host decloud-bastion
        HostName ${sshJumpHost}
        Port ${sshJumpPort}
        User decloud
        IdentityFile ~/.ssh/decloud-wallet.pem
        CertificateFile ~/.ssh/decloud-XXXXX-cert.pub

        Host ${vmIp}
        User ubuntu
        ProxyJump decloud-bastion
        IdentityFile ~/.ssh/decloud-wallet.pem
        CertificateFile ~/.ssh/decloud-XXXXX-cert.pub`;

    const modal = document.createElement('div');
    modal.className = 'modal-overlay active';
    modal.innerHTML = `
        <div class="modal" style="max-width: 850px;">
            <div class="modal-header">
                <h2 class="modal-title">🔗 Connect to ${escapeHtml(vmName)}</h2>
                <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">
                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
                    </svg>
                </button>
            </div>
            <div class="modal-body connect-info">

                <!-- Recommended: SSH Config Method -->
                <div class="connect-section" style="background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 20px; border-radius: 12px; margin-bottom: 20px;">
                    <div class="connect-section-title" style="color: white; font-size: 1.1rem; font-weight: 600;">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="display: inline; margin-right: 8px;">
                            <path d="M9 11l3 3L22 4"/>
                            <path d="M21 12v7a2 2 0 01-2 2H5a2 2 0 01-2-2V5a2 2 0 012-2h11"/>
                        </svg>
                        ✨ Recommended: One-Time SSH Config Setup
                    </div>
                    <p style="color: rgba(255,255,255,0.9); font-size: 0.9rem; margin: 12px 0;">
                        Set up SSH config once, then connect with a single command!
                    </p>

                    <!-- Step 1: Setup SSH Config -->
                    <div style="background: rgba(0,0,0,0.2); padding: 15px; border-radius: 8px; margin: 15px 0;">
                        <div style="color: white; font-weight: 600; margin-bottom: 8px;">📝 Step 1: Add to ~/.ssh/config</div>
                        <div class="connect-code" style="background: #1f2937; margin: 0;">
                            <pre style="margin: 0; color: #e5e7eb; font-size: 0.85rem; overflow-x: auto;">${escapeHtml(sshConfigContent)}</pre>
                            <button class="connect-code-copy" onclick="copyToClipboard(\`${sshConfigContent.replace(/`/g, '\\`')}\`)" style="background: #10b981;">
                                Copy Config
                            </button>
                        </div>
                        <button class="btn btn-secondary" onclick="downloadSSHConfig('${escapeHtml(vmIp)}', '${sshJumpHost}', ${sshJumpPort})" style="margin-top: 10px; background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3);">
                            💾 Download config file
                        </button>
                    </div>

                    <!-- Step 2: Connect -->
                    <div style="background: rgba(0,0,0,0.2); padding: 15px; border-radius: 8px;">
                        <div style="color: white; font-weight: 600; margin-bottom: 8px;">🚀 Step 2: Connect with Simple Command</div>
                        <div class="connect-code" style="background: #1f2937; margin: 0;">
                            <pre style="margin: 0; color: #e5e7eb; font-size: 0.9rem;">${escapeHtml(sshConfigCommand)}</pre>
                            <button class="connect-code-copy" onclick="copyToClipboard('${sshConfigCommand}')" style="background: #10b981;">
                                Copy
                            </button>
                        </div>
                        <p style="color: rgba(255,255,255,0.8); font-size: 0.85rem; margin: 8px 0 0 0;">
                            ✅ That's it! Just <code style="background: rgba(0,0,0,0.3); padding: 2px 6px; border-radius: 4px;">ssh ${escapeHtml(vmIp)}</code> from now on
                        </p>
                    </div>
                </div>

                <!-- Alternative: Direct ProxyJump Command -->
                <div class="connect-section">
                    <div class="connect-section-title">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="display: inline; margin-right: 8px;">
                            <rect x="2" y="2" width="20" height="20" rx="2" ry="2"/>
                            <line x1="7" y1="7" x2="7" y2="7"/>
                            <line x1="7" y1="12" x2="17" y2="12"/>
                            <line x1="7" y1="17" x2="17" y2="17"/>
                        </svg>
                        Alternative: Direct ProxyJump Command
                    </div>
                    <div class="connect-code">
                        <pre style="margin: 0; font-size: 0.85rem;">${escapeHtml(proxyJumpCommand)}</pre
                        <button class="connect-code-copy" onclick="copyToClipboard(\`${proxyJumpCommand.replace(/`/g, '\\`')}\`)">Copy</button>
                    </div>
                    <p style="color: #9ca3af; font-size: 0.875rem; margin-top: 8px;">
                        ⚠️ Don't forget to replace <code>XXXXX</code> with your certificate ID!
                    </p>
                </div>

                <!-- Connection Details -->
                <div class="connect-section">
                    <div class="connect-section-title">📊 Connection Details</div>
                    <table style="width: 100%; color: #9ca3af; font-size: 0.875rem;">
                        <tr>
                            <td style="padding: 6px 0;"><strong>Bastion Host:</strong></td>
                            <td style="padding: 6px 0;"><code>decloud@${escapeHtml(sshJumpHost)}:${sshJumpPort}</code></td>
                        </tr>
                        <tr>
                            <td style="padding: 6px 0;"><strong>VM IP Address:</strong></td>
                            <td style="padding: 6px 0;"><code>ubuntu@${escapeHtml(vmIp)}</code></td>
                        </tr>
                        <tr>
                            <td style="padding: 6px 0;"><strong>VM Hostname:</strong></td>
                            <td style="padding: 6px 0;"><code>${escapeHtml(vmName.toLowerCase())}</code></td>
                        </tr>
                        <tr>
                            <td style="padding: 6px 0;"><strong>Authentication:</strong></td>
                            <td style="padding: 6px 0;"><span style="color: #10b981;">✓ SSH Certificate (wallet-derived)</span></td>
                        </tr>
                        <tr>
                            <td style="padding: 6px 0;"><strong>Web Terminal:</strong></td>
                            <td style="padding: 6px 0;">
                                <button class="btn btn-sm btn-primary" onclick="openTerminal('${vmName}', '${nodeAgentHost}', ${nodeAgentPort}, '${vmIp}')" style="padding: 4px 12px; font-size: 0.85rem;">
                                    Open Terminal
                                </button>
                            </td>
                        </tr>
                    </table>
                </div>

                <!-- Security Info -->
                <div class="connect-section" style="background: #1e3a8a; border-left: 4px solid #3b82f6; padding: 15px; border-radius: 8px;">
                    <div style="color: #93c5fd; font-weight: 600; margin-bottom: 8px;">🔒 Security</div>
                    <ul style="color: #bfdbfe; font-size: 0.875rem; margin: 0; padding-left: 20px;">
                        <li>Certificates are valid for 1 hour and can be renewed anytime</li>
                        <li>Your private key (~/.ssh/decloud-wallet.pem) is derived from your wallet and never changes</li>
                        <li>Multi-tenant isolation: Your certificate only works for your VMs</li>
                        <li>Port ${sshJumpPort} is used to bypass common ISP restrictions</li>
                    </ul>
                </div
            </div>
        </div>
    `;

    document.body.appendChild(modal);
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };
}


/**
 * Download SSH config file
 */
function downloadSSHConfig(vmIp, bastionHost, bastionPort) {
    const config = `
        # DeCloud SSH Configuration
        # Add this to ~/.ssh/config (or C:\\\\Users\\\\USERNAME\\\\.ssh\\\\config on Windows)

        Host decloud-bastion
            HostName ${bastionHost}
            Port ${bastionPort}
            User decloud
            IdentityFile ~/.ssh/decloud-wallet.pem
            CertificateFile ~/.ssh/decloud-XXXXX-cert.pub

        Host ${vmIp}
            User ubuntu
            ProxyJump decloud-bastion
            IdentityFile ~/.ssh/decloud-wallet.pem
            CertificateFile ~/.ssh/decloud-XXXXX-cert.pub

        # Remember to replace XXXXX with your actual certificate ID!
        # Get your certificate from the VM dashboard.
        `;

    const blob = new Blob([config], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'decloud-ssh-config';
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);

    showToast('SSH config downloaded! Add it to ~/.ssh/config', 'success');
}

// ============================================
// SETTINGS
// ============================================

function saveSettings() {
    const orchestratorUrl = document.getElementById('settings-orchestrator-url').value.trim();

    if (orchestratorUrl && orchestratorUrl !== CONFIG.orchestratorUrl) {
        CONFIG.orchestratorUrl = orchestratorUrl;
        localStorage.setItem('orchestratorUrl', orchestratorUrl);
        showToast('Settings saved. Please reconnect your wallet.', 'success');
        setTimeout(() => disconnect(), 2000);
    } else {
        showToast('No changes to save', 'info');
    }
}

document.addEventListener('DOMContentLoaded', () => {
    const settingsUrl = document.getElementById('settings-orchestrator-url');
    if (settingsUrl) {
        settingsUrl.value = CONFIG.orchestratorUrl;
    }
});

// ============================================
// HELPER METHODS
// ============================================

/**
 * Copy text to clipboard with HTTP fallback
 * navigator.clipboard requires HTTPS, so we use execCommand as fallback
 * 
 * @param {string} text - Text to copy to clipboard
 * @returns {Promise<boolean>} - True if successful
 */
async function copyToClipboard(text) {
    // Try modern API on HTTPS
    var isSuccess = false;
    if (navigator.clipboard && window.isSecureContext) {
        try {
            await navigator.clipboard.writeText(text);
            isSuccess = true;
        } catch (err) {
            console.log('Modern API failed, trying fallback');
        }
    } else {
        // Fallback for HTTP: Use execCommand
        const textarea = document.createElement('textarea');
        textarea.value = text;
        textarea.style.position = 'fixed';
        textarea.style.left = '-999999px';
        document.body.appendChild(textarea);
        textarea.select();
        const successful = document.execCommand('copy');  // ✅ Works on HTTP!
        document.body.removeChild(textarea);
        isSuccess = successful;
    }

    if (isSuccess) {
        showToast('Copied!', 'success');
        return true;
    } else {
        showToast('Could not copy - please select manually', 'warning');
        return false;
    }
}

/**
 * Escape HTML to prevent XSS
 */
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

/**
 * Validate IPv4 address format
 */
function isValidIp(ip) {
    if (!ip || typeof ip !== 'string') return false;

    const ipv4Regex = /^(\d{1,3}\.){3}\d{1,3}$/;
    if (!ipv4Regex.test(ip)) return false;

    const octets = ip.split('.');
    return octets.every(octet => {
        const num = parseInt(octet, 10);
        return num >= 0 && num <= 255;
    });
}

/**
 * Get CSS class for VM status
 */
function getStatusClass(status) {
    const statusMap = {
        0: 'pending', 1: 'scheduling', 2: 'provisioning',
        3: 'running', 4: 'stopping', 5: 'stopped',
        6: 'deleting', 7: 'migrating', 8: 'error', 9: 'deleted'
    };
    return statusMap[status] || 'unknown';
}

/**
 * Get human-readable status text
 */
function getStatusText(status) {
    const statusMap = {
        0: 'Pending', 1: 'Scheduling', 2: 'Provisioning',
        3: 'Running', 4: 'Stopping', 5: 'Stopped',
        6: 'Deleting', 7: 'Migrating', 8: 'Error', 9: 'Deleted'
    };
    return statusMap[status] || 'Unknown';
}

// ============================================
// EXPOSE FUNCTIONS TO WINDOW (for onclick handlers)
// ============================================
window.api = api;
window.escapeHtml = escapeHtml;
window.connectWallet = connectWallet;
window.disconnect = disconnect;
window.showPage = showPage;
window.openCreateVMModal = openCreateVMModal;
window.closeModal = closeModal;
window.createVM = createVM;
window.deleteVM = deleteVM;
window.copyToClipboard = copyToClipboard;
window.revealPassword = revealPassword;
window.openAddSSHKeyModal = openAddSSHKeyModal;
window.addSSHKey = addSSHKey;
window.deleteSSHKey = deleteSSHKey;
window.openTerminal = openTerminal;
window.openFileBrowser = openFileBrowser;
window.showConnectInfo = showConnectInfo;
window.showSSHConnectionModal = showSSHConnectionModal;
window.downloadSSHBundle = downloadSSHBundle;
window.downloadSSHConfig = downloadSSHConfig;
window.saveSettings = saveSettings;
window.refreshData = refreshData;
window.showToast = showToast;
window.ethersSigner = () => ethersSigner;