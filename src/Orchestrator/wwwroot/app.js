// ============================================
// CONFIGURATION
// ============================================

// SECURITY: Replace with your WalletConnect Project ID from https://dashboard.reown.com
const WALLETCONNECT_PROJECT_ID = '708cede4d366aa77aead71dbc67d8ae5';

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

// ============================================
// LAZY SDK LOADING STATE
// ============================================
let sdkLoadState = {
    ethers: false,
    appKit: false,
    loading: false,
    error: null
};

// SDK CDN URLs - SECURITY: Version pinning
const SDK_URLS = {
    ethers: 'https://cdn.jsdelivr.net/npm/ethers@6.13.0/dist/ethers.umd.min.js',
    // Reown AppKit - Using ESM CDN
    appKit: 'https://esm.sh/@reown/appkit@1.5.2',
    appKitAdapter: 'https://esm.sh/@reown/appkit-adapter-ethers@1.5.2',
    appKitNetworks: 'https://esm.sh/@reown/appkit@1.5.2/networks'
};

// Password encryption cache
let cachedEncryptionKey = null;
const ENCRYPTION_MESSAGE = "DeCloud VM Password Encryption Key v1";

// AppKit unsubscribe functions
let appKitUnsubscribers = [];

// ============================================
// INITIALIZATION
// ============================================
document.addEventListener('DOMContentLoaded', async () => {
    const sessionRestored = await restoreSession();
    if (!sessionRestored) {
        showLogin();
    }
});

// ============================================
// LAZY SDK LOADING
// ============================================

/**
 * Dynamically loads a script from a CDN
 * SECURITY: Includes timeout and CORS protection
 */
function loadScript(url, timeout = 30000) {
    return new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = url;
        script.async = true;
        script.crossOrigin = 'anonymous';

        const timeoutId = setTimeout(() => {
            reject(new Error(`Script load timeout: ${url}`));
        }, timeout);

        script.onload = () => {
            clearTimeout(timeoutId);
            resolve();
        };

        script.onerror = () => {
            clearTimeout(timeoutId);
            reject(new Error(`Failed to load script: ${url}`));
        };

        document.head.appendChild(script);
    });
}

/**
 * Updates the loading progress bar
 */
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

/**
 * Loads ethers.js SDK
 */
async function loadEthersSDK() {
    if (sdkLoadState.ethers && window.ethers) {
        return window.ethers;
    }

    console.log('[SDK] Loading ethers.js...');
    updateLoadingProgress(20);

    await loadScript(SDK_URLS.ethers);

    if (!window.ethers) {
        throw new Error('ethers.js failed to load');
    }

    sdkLoadState.ethers = true;
    updateLoadingProgress(40);
    console.log('[SDK] ethers.js loaded successfully');
    return window.ethers;
}

/**
 * Initialize Reown AppKit with modern createAppKit approach
 * SECURITY: Proper network configuration and metadata
 */
async function initializeAppKit() {
    if (appKitModal) {
        return appKitModal;
    }

    try {
        console.log('[AppKit] Initializing...');
        updateLoadingProgress(50);

        // Load ethers.js first (required dependency)
        if (!window.ethers) {
            await loadEthersSDK();
        }

        updateLoadingProgress(60);

        // Load AppKit modules via ESM CDN
        const [
            { createAppKit },
            { EthersAdapter },
            { mainnet, polygon, arbitrum }
        ] = await Promise.all([
            import(SDK_URLS.appKit),
            import(SDK_URLS.appKitAdapter),
            import(SDK_URLS.appKitNetworks)
        ]);

        updateLoadingProgress(80);

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

        sdkLoadState.appKit = true;
        updateLoadingProgress(100);
        console.log('[AppKit] Initialized successfully');

        // Set up event listeners
        setupAppKitListeners();

        return appKitModal;

    } catch (error) {
        console.error('[AppKit] Initialization error:', error);
        sdkLoadState.error = error.message;
        updateLoadingProgress(0);
        throw new Error('Failed to initialize wallet connection. Please refresh and try again.');
    }
}

// ============================================
// APPKIT EVENT LISTENERS
// ============================================

/**
 * Subscribe to AppKit provider state changes
 * SECURITY: Validates address and handles disconnections
 */
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

                    ethersProvider = new window.ethers.BrowserProvider(walletProvider);
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
        // For DeCloud, we primarily use Polygon for payments
        // You can add network validation here if needed
    });

    // Store unsubscribe functions
    appKitUnsubscribers = [unsubscribeAccount, unsubscribeNetwork];
}

// ============================================
// WALLET CONNECTION - SIMPLIFIED
// ============================================

/**
 * Unified wallet connection handler
 * SECURITY: Proper error handling and user feedback
 */
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

/**
 * Check if wallet is already connected (for session restoration)
 */
async function checkExistingConnection() {
    try {
        if (!appKitModal) {
            await initializeAppKit();
        }

        const address = appKitModal.getAddress();
        const isConnected = appKitModal.getIsConnected();

        if (isConnected && address) {
            console.log('[AppKit] Existing connection found:', address);
            connectedAddress = address;

            const walletProvider = appKitModal.getWalletProvider();
            if (walletProvider) {
                ethersProvider = new window.ethers.BrowserProvider(walletProvider);
                ethersSigner = await ethersProvider.getSigner();
            }

            return true;
        }

        return false;
    } catch (error) {
        console.error('[AppKit] Connection check error:', error);
        return false;
    }
}

// ============================================
// AUTHENTICATION FLOW
// ============================================

/**
 * Proceed with authentication after wallet connection
 */
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
                initializeAppKit()
                    .then(() => checkExistingConnection())
                    .catch(e => console.log('[AppKit] Background init failed:', e));

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
// ERROR HANDLING
// ============================================

function handleConnectionError(error) {
    let errorMsg = 'Connection failed. ';

    // SECURITY: Sanitize error messages
    if (error.code === 4001 || error.message?.includes('rejected') || error.message?.includes('User rejected')) {
        errorMsg = 'Connection rejected. Please approve the connection request.';
    } else if (error.message?.includes('cancelled')) {
        errorMsg = 'Connection cancelled.';
    } else if (error.message?.includes('projectId') || error.message?.includes('Project ID')) {
        errorMsg = 'Wallet connection service unavailable. Please contact support.';
    } else if (error.message?.includes('timeout')) {
        errorMsg = 'Connection timeout. Please check your network and try again.';
    } else if (error.message?.includes('network')) {
        errorMsg = 'Network error. Please check your connection and try again.';
    } else if (error.message?.includes('Failed to load')) {
        errorMsg = 'Failed to load wallet libraries. Please refresh the page and try again.';
    } else {
        // SECURITY: Generic message for unknown errors
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
    // Refresh token every 50 minutes (tokens expire at 60 minutes)
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

    // Handle 401 Unauthorized - token expired
    if (response.status === 401) {
        console.log('[API] 401 received, attempting token refresh...');
        const refreshed = await refreshAuthToken();

        if (refreshed) {
            // Retry the request with new token
            headers['Authorization'] = `Bearer ${authToken}`;
            return fetch(`${CONFIG.orchestratorUrl}${endpoint}`, {
                ...options,
                headers
            });
        } else {
            // Refresh failed, redirect to login
            disconnect();
            throw new Error('Session expired. Please log in again.');
        }
    }

    return response;
}

// ============================================
// PASSWORD ENCRYPTION
// ============================================

async function getEncryptionKey() {
    if (cachedEncryptionKey) {
        return cachedEncryptionKey;
    }

    if (!ethersSigner) {
        throw new Error('Wallet not connected');
    }

    const signature = await ethersSigner.signMessage(ENCRYPTION_MESSAGE);
    const keyMaterial = window.ethers.getBytes(window.ethers.keccak256(window.ethers.toUtf8Bytes(signature)));

    cachedEncryptionKey = await crypto.subtle.importKey(
        'raw',
        keyMaterial.slice(0, 32),
        { name: 'AES-GCM', length: 256 },
        false,
        ['encrypt', 'decrypt']
    );

    return cachedEncryptionKey;
}

async function encryptPassword(password) {
    const key = await getEncryptionKey();
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const encoded = new TextEncoder().encode(password);

    const encrypted = await crypto.subtle.encrypt(
        { name: 'AES-GCM', iv },
        key,
        encoded
    );

    const combined = new Uint8Array(iv.length + encrypted.byteLength);
    combined.set(iv);
    combined.set(new Uint8Array(encrypted), iv.length);

    return btoa(String.fromCharCode(...combined));
}

async function decryptPassword(encryptedPassword) {
    const key = await getEncryptionKey();
    const combined = Uint8Array.from(atob(encryptedPassword), c => c.charCodeAt(0));

    const iv = combined.slice(0, 12);
    const encrypted = combined.slice(12);

    const decrypted = await crypto.subtle.decrypt(
        { name: 'AES-GCM', iv },
        key,
        encrypted
    );

    return new TextDecoder().decode(decrypted);
}

// ============================================
// UI HELPERS
// ============================================

function showLogin() {
    document.getElementById('login-overlay').classList.add('active');
    document.getElementById('app-container').style.display = 'none';
}

function showDashboard() {
    document.getElementById('login-overlay').classList.remove('active');
    document.getElementById('app-container').style.display = 'flex';

    // Update wallet display
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
    // Hide all pages
    document.querySelectorAll('.page').forEach(page => {
        page.classList.remove('active');
    });

    // Remove active class from nav items
    document.querySelectorAll('.nav-item').forEach(item => {
        item.classList.remove('active');
    });

    // Show selected page
    const selectedPage = document.getElementById(`page-${pageName}`);
    if (selectedPage) {
        selectedPage.classList.add('active');
    }

    // Add active class to nav item
    const selectedNav = document.querySelector(`.nav-item[data-page="${pageName}"]`);
    if (selectedNav) {
        selectedNav.classList.add('active');
    }

    // Refresh data for certain pages
    if (pageName === 'dashboard' || pageName === 'virtual-machines') {
        refreshData();
    } else if (pageName === 'nodes') {
        loadNodes();
    } else if (pageName === 'ssh-keys') {
        loadSSHKeys();
    }
}

// ============================================
// DATA LOADING
// ============================================

async function refreshData() {
    await Promise.all([
        loadDashboardStats(),
        loadVirtualMachines()
    ]);
}

async function loadDashboardStats() {
    try {
        const response = await api('/api/dashboard/stats');
        const data = await response.json();

        if (data.success) {
            const stats = data.data;

            document.getElementById('stat-vms').textContent = stats.totalVMs || 0;
            document.getElementById('stat-nodes').textContent = stats.totalNodes || 0;
            document.getElementById('stat-cpu').textContent = `${stats.totalCPU || 0} cores`;
            document.getElementById('stat-memory').textContent = `${(stats.totalMemory / 1024).toFixed(1)} GB`;
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
            const vms = data.data;
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

    if (!vms || vms.length === 0) {
        tbody.innerHTML = '<tr><td colspan="7" style="text-align: center; padding: 40px; color: #6b7280;">No virtual machines found. Create your first VM to get started.</td></tr>';
        return;
    }

    tbody.innerHTML = vms.map(vm => `
        <tr>
            <td>
                <div class="vm-name">
                    <div class="vm-status ${vm.status}"></div>
                    ${vm.name}
                </div>
            </td>
            <td>${vm.nodeId ? (nodesCache[vm.nodeId] || vm.nodeId) : 'Unknown'}</td>
            <td>${vm.specs?.cpu || 0} cores</td>
            <td>${vm.specs?.memory || 0} MB</td>
            <td>${vm.specs?.disk || 0} GB</td>
            <td>
                <span class="status-badge status-${vm.status}">
                    ${vm.status}
                </span>
            </td>
            <td>
                <div class="table-actions">
                    <button class="btn-icon" onclick="showConnectInfo('${vm.nodeIp}', '${vm.ipAddress}', '${vm.name}')" title="Connect">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/>
                        </svg>
                    </button>
                    <button class="btn-icon" onclick="openTerminal('${vm.id}')" title="Terminal">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="4" width="20" height="16" rx="2"/>
                            <path d="M6 8l4 4-4 4M12 16h6"/>
                        </svg>
                    </button>
                    <button class="btn-icon" onclick="revealPassword('${vm.id}')" title="Show Password">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/>
                            <circle cx="12" cy="12" r="3"/>
                        </svg>
                    </button>
                    <button class="btn-icon btn-icon-danger" onclick="deleteVM('${vm.id}', '${vm.name}')" title="Delete">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                        </svg>
                    </button>
                </div>
            </td>
        </tr>
    `).join('');
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
                    <span class="spec-value">${vm.specs?.cpu || 0} cores</span>
                </div>
                <div class="spec-item">
                    <span class="spec-label">Memory</span>
                    <span class="spec-value">${vm.specs?.memory || 0} MB</span>
                </div>
                <div class="spec-item">
                    <span class="spec-label">Disk</span>
                    <span class="spec-value">${vm.specs?.disk || 0} GB</span>
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

            // Cache node names
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
            <td>${node.ipAddress}</td>
            <td>${node.resources?.totalCPU || 0} cores</td>
            <td>${((node.resources?.totalMemory || 0) / 1024).toFixed(1)} GB</td>
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
                    <button class="btn-icon btn-icon-danger" onclick="deleteSSHKey('${key.id}', '${key.name}')" title="Delete">
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
                specs: { cpu, memory, disk },
                image
            })
        });

        const data = await response.json();

        if (data.success) {
            showToast('Virtual machine created successfully', 'success');
            closeModal('create-vm-modal');
            refreshData();

            // Clear form
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
            showToast('Virtual machine deleted', 'success');
            refreshData();
        } else {
            showToast(data.message || 'Failed to delete VM', 'error');
        }
    } catch (error) {
        console.error('[VM] Delete error:', error);
        showToast('Failed to delete virtual machine', 'error');
    }
}

async function revealPassword(vmId) {
    try {
        const response = await api(`/api/vms/${vmId}`);
        const data = await response.json();

        if (!data.success || !data.data) {
            showToast('Failed to load VM details', 'error');
            return;
        }

        const vm = data.data;

        if (!vm.encryptedPassword) {
            showToast('No password set for this VM', 'error');
            return;
        }

        // Decrypt password
        const password = await decryptPassword(vm.encryptedPassword);

        // Show modal with password
        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.innerHTML = `
            <div class="modal" style="max-width: 500px;">
                <div class="modal-header">
                    <h2 class="modal-title">VM Password - ${vm.name}</h2>
                    <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <line x1="18" y1="6" x2="6" y2="18" />
                            <line x1="6" y1="6" x2="18" y2="18" />
                        </svg>
                    </button>
                </div>
                <div class="modal-body">
                    <p style="margin-bottom: 12px; color: #9ca3af;">Username: <strong style="color: #fff;">decloud</strong></p>
                    <p style="margin-bottom: 12px; color: #9ca3af;">Password:</p>
                    <div style="background: #1e1f2e; padding: 16px; border-radius: 8px; font-family: 'JetBrains Mono', monospace; font-size: 18px; color: #10b981; word-break: break-all; margin-bottom: 16px;">
                        ${password}
                    </div>
                    <p style="font-size: 12px; color: #ef4444;">⚠️ Keep this password secure. It will not be shown again.</p>
                </div>
                <div class="modal-footer">
                    <button class="btn btn-secondary" onclick="navigator.clipboard.writeText('${password}'); showToast('Password copied!', 'success');">📋 Copy</button>
                    <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">Close</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);
        modal.onclick = (e) => { if (e.target === modal) modal.remove(); };

    } catch (error) {
        console.error('[VM] Password reveal error:', error);
        showToast('Failed to decrypt password. Ensure same wallet is connected.', 'error');
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

            // Clear form
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
// TERMINAL
// ============================================

function openTerminal(vmId) {
    showToast('Terminal feature coming soon', 'info');
    // TODO: Implement WebSocket terminal connection
}

function showConnectInfo(nodeIp, vmIp, vmName) {
    const modal = document.createElement('div');
    modal.className = 'modal-overlay active';
    modal.innerHTML = `
        <div class="modal" style="max-width: 600px;">
            <div class="modal-header">
                <h2 class="modal-title">Connect to ${vmName}</h2>
                <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">
                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
                    </svg>
                </button>
            </div>
            <div class="modal-body connect-info">
                <div class="connect-section">
                    <div class="connect-section-title">SSH via Jump Host</div>
                    <div class="connect-code">
                        ssh -J decloud@${nodeIp} decloud@${vmIp}
                        <button class="connect-code-copy" onclick="navigator.clipboard.writeText('ssh -J decloud@${nodeIp} decloud@${vmIp}'); this.textContent='Copied!'; setTimeout(() => this.textContent='Copy', 2000)">Copy</button>
                    </div>
                </div>
                <div class="connect-section">
                    <div class="connect-section-title">Direct Connection Info</div>
                    <p style="color: #9ca3af; margin-bottom: 8px;">Node IP: <code>${nodeIp}</code></p>
                    <p style="color: #9ca3af;">VM IP: <code>${vmIp}</code></p>
                </div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">Close</button>
            </div>
        </div>
    `;
    document.body.appendChild(modal);
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };
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

// Load settings on settings page
document.addEventListener('DOMContentLoaded', () => {
    const settingsUrl = document.getElementById('settings-orchestrator-url');
    if (settingsUrl) {
        settingsUrl.value = CONFIG.orchestratorUrl;
    }
});