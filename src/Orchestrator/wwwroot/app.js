// ============================================
// CONFIGURATION
// ============================================

// IMPORTANT: Replace with your WalletConnect Project ID from https://cloud.walletconnect.com
const WALLETCONNECT_PROJECT_ID = '708cede4d366aa77aead71dbc67d8ae5';

const CONFIG = {
    orchestratorUrl: localStorage.getItem('orchestratorUrl') || window.location.origin,
    wallet: null
};

const WALLET_CONFIG = {
    projectId: WALLETCONNECT_PROJECT_ID,
    chains: [1], // Mainnet
    optionalChains: [1, 137, 42161, 10], // Mainnet, Polygon, Arbitrum, Optimism
    metadata: {
        name: 'DeCloud',
        description: 'Decentralized Cloud Computing Platform',
        url: window.location.origin,
        icons: [window.location.origin + '/favicon.ico']
    },
    showQrModal: true,
    qrModalOptions: {
        themeMode: 'dark',
        themeVariables: {
            '--wcm-accent-color': '#10b981',
            '--wcm-background-color': '#111827'
        }
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
let walletConnectProvider = null;

// ============================================
// LAZY SDK LOADING STATE
// ============================================
let sdkLoadState = {
    ethers: false,
    walletConnect: false,
    loading: false,
    error: null
};

// SDK CDN URLs with version pinning for security
const SDK_URLS = {
    ethers: 'https://cdn.jsdelivr.net/npm/ethers@6.9.0/dist/ethers.umd.min.js',
    walletConnect: 'https://esm.sh/@walletconnect/ethereum-provider@2.17.0'
};

// Password encryption cache
let cachedEncryptionKey = null;
const ENCRYPTION_MESSAGE = "DeCloud VM Password Encryption Key v1";

// ============================================
// INITIALIZATION
// ============================================
document.addEventListener('DOMContentLoaded', async () => {
    const sessionRestored = await restoreSession();
    if (!sessionRestored) {
        showLogin();
    }
    loadSettings();
});

// ============================================
// LAZY SDK LOADING
// ============================================

/**
 * Shows/hides the SDK loading progress bar
 */
function updateLoadingProgress(percent) {
    const bar = document.getElementById('sdk-loading-bar');
    const progress = document.getElementById('sdk-loading-progress');

    if (percent > 0 && percent < 100) {
        bar.classList.add('active');
        progress.style.width = percent + '%';
    } else if (percent >= 100) {
        progress.style.width = '100%';
        setTimeout(() => {
            bar.classList.remove('active');
            progress.style.width = '0%';
        }, 300);
    } else {
        bar.classList.remove('active');
    }
}

/**
 * Dynamically loads a script and returns a promise
 */
function loadScript(url, globalName) {
    return new Promise((resolve, reject) => {
        // Check if already loaded
        if (globalName && window[globalName]) {
            resolve(window[globalName]);
            return;
        }

        const script = document.createElement('script');
        script.src = url;
        script.async = true;

        script.onload = () => {
            if (globalName && window[globalName]) {
                resolve(window[globalName]);
            } else {
                resolve(true);
            }
        };

        script.onerror = () => {
            reject(new Error(`Failed to load script: ${url}`));
        };

        document.head.appendChild(script);
    });
}

/**
 * Dynamically imports an ES module
 */
async function loadESModule(url) {
    try {
        const module = await import(url);
        return module;
    } catch (error) {
        throw new Error(`Failed to load module: ${url}`);
    }
}

/**
 * Loads ethers.js SDK lazily
 */
async function loadEthersSDK() {
    if (sdkLoadState.ethers) {
        return window.ethers;
    }

    console.log('[SDK] Loading ethers.js...');
    await loadScript(SDK_URLS.ethers, 'ethers');
    sdkLoadState.ethers = true;
    console.log('[SDK] ethers.js loaded successfully');

    return window.ethers;
}

/**
 * Loads WalletConnect SDK lazily
 */
async function loadWalletConnectSDK() {
    if (sdkLoadState.walletConnect && window.WalletConnectEthereumProvider) {
        return window.WalletConnectEthereumProvider;
    }

    console.log('[SDK] Loading WalletConnect...');
    const module = await loadESModule(SDK_URLS.walletConnect);

    window.WalletConnectEthereumProvider = module.EthereumProvider;
    sdkLoadState.walletConnect = true;
    console.log('[SDK] WalletConnect loaded successfully');

    return module.EthereumProvider;
}

/**
 * Loads all required wallet SDKs
 * @param {boolean} includeWalletConnect - Whether to load WalletConnect SDK
 * @returns {Promise<void>}
 */
async function loadWalletSDKs(includeWalletConnect = false) {
    if (sdkLoadState.loading) {
        // Wait for existing load to complete
        while (sdkLoadState.loading) {
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        return;
    }

    sdkLoadState.loading = true;
    sdkLoadState.error = null;

    try {
        updateLoadingProgress(10);

        // Always load ethers.js
        if (!sdkLoadState.ethers) {
            await loadEthersSDK();
            updateLoadingProgress(50);
        }

        // Optionally load WalletConnect
        if (includeWalletConnect && !sdkLoadState.walletConnect) {
            await loadWalletConnectSDK();
            updateLoadingProgress(90);
        }

        updateLoadingProgress(100);
    } catch (error) {
        sdkLoadState.error = error;
        updateLoadingProgress(0);
        console.error('[SDK] Failed to load wallet SDKs:', error);
        throw error;
    } finally {
        sdkLoadState.loading = false;
    }
}

/**
 * Checks if SDKs are loaded and ready
 */
function areSDKsReady(requireWalletConnect = false) {
    if (!sdkLoadState.ethers) return false;
    if (requireWalletConnect && !sdkLoadState.walletConnect) return false;
    return true;
}

// ============================================
// SESSION RESTORATION
// ============================================
async function restoreSession() {
    const savedToken = localStorage.getItem('authToken');
    const savedRefreshToken = localStorage.getItem('refreshToken');
    const savedWallet = localStorage.getItem('wallet');
    const connectionType = localStorage.getItem('connectionType');

    if (savedToken && savedRefreshToken && savedWallet) {
        authToken = savedToken;
        refreshToken = savedRefreshToken;
        CONFIG.wallet = savedWallet;
        connectedAddress = savedWallet;

        try {
            const response = await api('/api/user/me');
            if (response.success) {
                currentUser = response.data;

                // Lazy load SDKs for provider restoration (only if needed for signing)
                // This is deferred - we don't block session restoration
                restoreProviderConnection(connectionType).catch(e => {
                    console.log('Provider restoration deferred:', e.message);
                });

                showDashboard();
                setupTokenRefresh();
                refreshData();
                return true;
            } else {
                const refreshed = await refreshAuthToken();
                if (refreshed) {
                    showDashboard();
                    refreshData();
                    return true;
                }
            }
        } catch (e) {
            console.error('Session verification failed:', e);
        }
    }
    return false;
}

/**
 * Restores provider connection lazily (non-blocking)
 */
async function restoreProviderConnection(connectionType) {
    try {
        // Only load SDKs if we actually need provider functionality
        const walletConnectConfigured = WALLETCONNECT_PROJECT_ID !== 'YOUR_PROJECT_ID_HERE';

        if (connectionType === 'walletconnect' && walletConnectConfigured) {
            await loadWalletSDKs(true);

            walletConnectProvider = await window.WalletConnectEthereumProvider.init({
                projectId: WALLET_CONFIG.projectId,
                chains: WALLET_CONFIG.chains,
                optionalChains: WALLET_CONFIG.optionalChains,
                showQrModal: false,
                metadata: WALLET_CONFIG.metadata
            });

            if (walletConnectProvider.session) {
                ethersProvider = new window.ethers.BrowserProvider(walletConnectProvider);
                ethersSigner = await ethersProvider.getSigner();
                setupWalletConnectListeners();
                console.log('[Session] WalletConnect provider restored');
            }
        } else if (window.ethereum) {
            await loadWalletSDKs(false);
            ethersProvider = new window.ethers.BrowserProvider(window.ethereum);
            ethersSigner = await ethersProvider.getSigner();
            setupInjectedProviderListeners();
            console.log('[Session] Injected provider restored');
        }
    } catch (e) {
        console.log('WalletConnect session expired:', e);
    }
}

// ============================================
// UI STATE MANAGEMENT
// ============================================
function showLogin() {
    document.getElementById('login-overlay').classList.remove('hidden');
}

function showDashboard() {
    document.getElementById('login-overlay').classList.add('hidden');
    updateWalletDisplay();
}

function showLoginStatus(type, message) {
    const status = document.getElementById('login-status');
    status.className = `login-status ${type}`;
    status.textContent = message;
}

function hideLoginStatus() {
    const status = document.getElementById('login-status');
    status.className = 'login-status';
    status.style.display = 'none';
}

function updateWalletDisplay() {
    const walletDisplay = document.getElementById('wallet-display');
    const walletBadge = document.getElementById('wallet-badge');
    const disconnectBtn = document.getElementById('disconnect-btn');
    const settingsWallet = document.getElementById('settings-wallet');

    if (CONFIG.wallet && walletDisplay) {
        const shortWallet = CONFIG.wallet.slice(0, 6) + '...' + CONFIG.wallet.slice(-4);
        walletDisplay.textContent = shortWallet;

        if (walletBadge) walletBadge.classList.add('connected');
        if (disconnectBtn) disconnectBtn.style.display = 'block';
        if (settingsWallet) settingsWallet.value = CONFIG.wallet;
    }
}

function resetConnectButton(btn) {
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
// WALLET CONNECTION - UNIFIED APPROACH
// ============================================
async function connectWallet() {
    const btn = document.getElementById('connect-wallet-btn');

    try {
        btn.disabled = true;
        btn.innerHTML = '<div class="spinner"></div> Loading...';
        showLoginStatus('info', 'Initializing wallet connection...');

        // Check configuration
        const walletConnectConfigured = WALLETCONNECT_PROJECT_ID !== 'YOUR_PROJECT_ID_HERE';
        const hasInjectedProvider = typeof window.ethereum !== 'undefined';
        const isMobile = /iPhone|iPad|iPod|Android/i.test(navigator.userAgent);

        // Determine what SDKs we need and load them lazily
        const needWalletConnect = walletConnectConfigured && (isMobile || !hasInjectedProvider);

        btn.innerHTML = '<div class="spinner"></div> Loading wallet...';
        await loadWalletSDKs(needWalletConnect || walletConnectConfigured);

        btn.innerHTML = '<div class="spinner"></div> Connecting...';

        if (isMobile && walletConnectConfigured) {
            // Mobile - use WalletConnect
            await connectWithWalletConnect();
        } else if (!hasInjectedProvider && walletConnectConfigured) {
            // Desktop without extension - use WalletConnect QR
            await connectWithWalletConnect();
        } else if (hasInjectedProvider && walletConnectConfigured) {
            // Desktop with extension - show choice
            await showConnectionOptions();
        } else if (hasInjectedProvider) {
            // Desktop with extension, WalletConnect not configured
            await connectWithInjectedProvider();
        } else {
            // No options available
            showLoginStatus('error', 'No wallet detected. Please install MetaMask or another Web3 wallet.');
        }

    } catch (error) {
        console.error('Connection error:', error);
        handleConnectionError(error);
    } finally {
        resetConnectButton(btn);
    }
}

// ============================================
// WALLETCONNECT CONNECTION
// ============================================
async function connectWithWalletConnect() {
    showLoginStatus('info', 'Opening wallet selector...');

    // Ensure WalletConnect SDK is loaded
    if (!areSDKsReady(true)) {
        await loadWalletSDKs(true);
    }

    walletConnectProvider = await window.WalletConnectEthereumProvider.init({
        projectId: WALLET_CONFIG.projectId,
        chains: WALLET_CONFIG.chains,
        optionalChains: WALLET_CONFIG.optionalChains,
        showQrModal: WALLET_CONFIG.showQrModal,
        metadata: WALLET_CONFIG.metadata,
        qrModalOptions: WALLET_CONFIG.qrModalOptions
    });

    await walletConnectProvider.enable();

    const accounts = walletConnectProvider.accounts;
    if (!accounts || accounts.length === 0) {
        throw new Error('No accounts found');
    }

    connectedAddress = accounts[0];

    ethersProvider = new window.ethers.BrowserProvider(walletConnectProvider);
    ethersSigner = await ethersProvider.getSigner();

    setupWalletConnectListeners();

    await proceedWithAuthentication(connectedAddress, 'walletconnect');
}

// ============================================
// INJECTED PROVIDER (MetaMask, etc.)
// ============================================
async function connectWithInjectedProvider() {
    showLoginStatus('info', 'Requesting wallet connection...');

    // Ensure ethers SDK is loaded
    if (!areSDKsReady(false)) {
        await loadWalletSDKs(false);
    }

    const accounts = await window.ethereum.request({ method: 'eth_requestAccounts' });

    if (!accounts || accounts.length === 0) {
        throw new Error('No accounts found');
    }

    connectedAddress = accounts[0];

    ethersProvider = new window.ethers.BrowserProvider(window.ethereum);
    ethersSigner = await ethersProvider.getSigner();

    setupInjectedProviderListeners();

    await proceedWithAuthentication(connectedAddress, 'injected');
}

// ============================================
// CONNECTION OPTIONS MODAL (Desktop)
// ============================================
async function showConnectionOptions() {
    const modal = document.createElement('div');
    modal.id = 'wallet-options-modal';
    modal.className = 'wallet-options-overlay';
    modal.innerHTML = `
        <div class="wallet-options-modal">
            <h3 class="wallet-options-title">Connect Wallet</h3>
            <p class="wallet-options-subtitle">Choose how you want to connect</p>

            <div class="wallet-options-list">
                <button class="wallet-option" id="opt-injected">
                    <div class="wallet-option-icon">
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="4" width="20" height="16" rx="2"/>
                            <path d="M6 8h12M6 12h12M6 16h6"/>
                        </svg>
                    </div>
                    <div class="wallet-option-info">
                        <span class="wallet-option-name">Browser Wallet</span>
                        <span class="wallet-option-desc">MetaMask, Coinbase, etc.</span>
                    </div>
                </button>

                <button class="wallet-option" id="opt-walletconnect">
                    <div class="wallet-option-icon walletconnect-icon">
                        <svg viewBox="0 0 24 24" fill="currentColor">
                            <path d="M6.09 10.11c3.3-3.23 8.65-3.23 11.95 0l.4.39c.16.16.16.42 0 .58l-1.36 1.33c-.08.08-.21.08-.3 0l-.55-.53c-2.3-2.25-6.03-2.25-8.33 0l-.59.57c-.08.08-.21.08-.3 0L5.65 11.1c-.16-.16-.16-.42 0-.58l.44-.41zm14.77 2.74l1.21 1.18c.16.16.16.42 0 .58l-5.46 5.34c-.16.16-.43.16-.59 0l-3.88-3.79c-.04-.04-.11-.04-.15 0l-3.88 3.79c-.16.16-.43.16-.59 0L2.07 14.6c-.16-.16-.16-.42 0-.58l1.21-1.18c.16-.16.43-.16.59 0l3.88 3.79c.04.04.11.04.15 0l3.88-3.79c.16-.16.43-.16.59 0l3.88 3.79c.04.04.11.04.15 0l3.88-3.79c.16-.15.43-.15.59 0z"/>
                        </svg>
                    </div>
                    <div class="wallet-option-info">
                        <span class="wallet-option-name">WalletConnect</span>
                        <span class="wallet-option-desc">Scan with mobile wallet</span>
                    </div>
                </button>
            </div>

            <button class="wallet-options-cancel" id="opt-cancel">Cancel</button>
        </div>
    `;

    document.body.appendChild(modal);

    return new Promise((resolve, reject) => {
        document.getElementById('opt-injected').onclick = async () => {
            modal.remove();
            try {
                await connectWithInjectedProvider();
                resolve();
            } catch (e) {
                reject(e);
            }
        };

        document.getElementById('opt-walletconnect').onclick = async () => {
            modal.remove();
            try {
                await connectWithWalletConnect();
                resolve();
            } catch (e) {
                reject(e);
            }
        };

        document.getElementById('opt-cancel').onclick = () => {
            modal.remove();
            hideLoginStatus();
            reject(new Error('Connection cancelled'));
        };
    });
}

// ============================================
// AUTHENTICATION FLOW
// ============================================
async function proceedWithAuthentication(walletAddress, connectionType) {
    const btn = document.getElementById('connect-wallet-btn');
    showLoginStatus('info', 'Requesting signature...');
    btn.innerHTML = '<div class="spinner"></div> Sign Message...';

    const authResult = await authenticateWithWallet(walletAddress);

    if (authResult.success) {
        showLoginStatus('success', 'Authentication successful!');
        localStorage.setItem('connectionType', connectionType);

        setTimeout(() => {
            showDashboard();
            setupTokenRefresh();
            refreshData();
        }, 500);
    } else {
        throw new Error(authResult.error || 'Authentication failed');
    }
}

/**
 * Authenticates with the backend using wallet signature
 * Uses the backend's /api/auth/message and /api/auth/wallet endpoints
 */
async function authenticateWithWallet(walletAddress) {
    try {
        // Step 1: Get message to sign from server
        const messageResponse = await fetch(`${CONFIG.orchestratorUrl}/api/auth/message?walletAddress=${walletAddress}`);

        if (!messageResponse.ok) {
            const errorText = await messageResponse.text();
            console.error('Message endpoint error:', messageResponse.status, errorText);
            return { success: false, error: `Server error: ${messageResponse.status}` };
        }

        const messageData = await messageResponse.json();

        if (!messageData.success) {
            return { success: false, error: messageData.message || 'Failed to get authentication message' };
        }

        const { message, timestamp } = messageData.data;

        // Step 2: Sign the message with the wallet
        const signature = await ethersSigner.signMessage(message);

        // Step 3: Authenticate with the server
        const authResponse = await fetch(`${CONFIG.orchestratorUrl}/api/auth/wallet`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                walletAddress: walletAddress,
                signature: signature,
                message: message,
                timestamp: timestamp
            })
        });

        if (!authResponse.ok) {
            const errorText = await authResponse.text();
            console.error('Auth endpoint error:', authResponse.status, errorText);
            return { success: false, error: `Authentication failed: ${authResponse.status}` };
        }

        const authData = await authResponse.json();

        if (authData.success && authData.data) {
            authToken = authData.data.accessToken;
            refreshToken = authData.data.refreshToken;
            currentUser = authData.data.user;
            CONFIG.wallet = walletAddress;

            // Save to localStorage
            localStorage.setItem('authToken', authToken);
            localStorage.setItem('refreshToken', refreshToken);
            localStorage.setItem('wallet', walletAddress);

            return { success: true };
        } else {
            return { success: false, error: authData.message || 'Authentication failed' };
        }
    } catch (error) {
        console.error('Authentication error:', error);
        return { success: false, error: error.message };
    }
}

// ============================================
// WALLET EVENT LISTENERS
// ============================================
function setupWalletConnectListeners() {
    if (!walletConnectProvider) return;

    walletConnectProvider.on('accountsChanged', (accounts) => {
        if (accounts.length === 0) {
            disconnect();
        } else if (accounts[0].toLowerCase() !== CONFIG.wallet?.toLowerCase()) {
            showToast('Wallet changed. Please reconnect.', 'info');
            disconnect();
        }
    });

    walletConnectProvider.on('chainChanged', (chainId) => {
        console.log('Chain changed to:', chainId);
    });

    walletConnectProvider.on('disconnect', () => {
        disconnect();
    });
}

function setupInjectedProviderListeners() {
    if (!window.ethereum) return;

    window.ethereum.on('accountsChanged', (accounts) => {
        if (accounts.length === 0) {
            disconnect();
        } else if (accounts[0].toLowerCase() !== CONFIG.wallet?.toLowerCase()) {
            showToast('Wallet changed. Please reconnect.', 'info');
            disconnect();
        }
    });

    window.ethereum.on('disconnect', () => {
        disconnect();
    });
}

// ============================================
// ERROR HANDLING
// ============================================
function handleConnectionError(error) {
    let errorMsg = 'Connection failed. ';

    if (error.code === 4001 || error.message?.includes('rejected')) {
        errorMsg = 'Connection rejected. Please approve the connection request.';
    } else if (error.message?.includes('cancelled')) {
        errorMsg = 'Connection cancelled.';
    } else if (error.message?.includes('projectId')) {
        errorMsg = 'WalletConnect not configured. Please set up a Project ID.';
    } else if (error.message?.includes('failed to load') || error.message?.includes('Failed to load')) {
        errorMsg = 'Failed to load wallet libraries. Please check your connection and refresh.';
    } else if (error.message?.includes('No accounts')) {
        errorMsg = 'No accounts found. Please unlock your wallet.';
    } else if (error.message) {
        errorMsg = error.message;
    }

    showLoginStatus('error', errorMsg);
}

// ============================================
// DISCONNECT
// ============================================
async function disconnect() {
    // Disconnect WalletConnect if active
    if (walletConnectProvider) {
        try {
            await walletConnectProvider.disconnect();
        } catch (e) {
            console.log('WalletConnect disconnect error:', e);
        }
        walletConnectProvider = null;
    }

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

    localStorage.removeItem('authToken');
    localStorage.removeItem('refreshToken');
    localStorage.removeItem('wallet');
    localStorage.removeItem('connectionType');

    if (tokenRefreshTimer) {
        clearInterval(tokenRefreshTimer);
        tokenRefreshTimer = null;
    }

    const walletDisplay = document.getElementById('wallet-display');
    const walletBadge = document.getElementById('wallet-badge');
    const disconnectBtn = document.getElementById('disconnect-btn');

    if (walletDisplay) walletDisplay.textContent = 'Not connected';
    if (walletBadge) walletBadge.classList.remove('connected');
    if (disconnectBtn) disconnectBtn.style.display = 'none';
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

    try {
        const response = await fetch(`${CONFIG.orchestratorUrl}${endpoint}`, {
            ...options,
            headers
        });

        if (response.status === 401) {
            const refreshed = await refreshAuthToken();
            if (refreshed) {
                headers['Authorization'] = `Bearer ${authToken}`;
                const retryResponse = await fetch(`${CONFIG.orchestratorUrl}${endpoint}`, {
                    ...options,
                    headers
                });
                return await retryResponse.json();
            } else {
                disconnect();
                throw new Error('Session expired');
            }
        }

        return await response.json();
    } catch (error) {
        console.error('API error:', error);
        throw error;
    }
}

// ============================================
// TOKEN REFRESH
// ============================================
function setupTokenRefresh() {
    if (tokenRefreshTimer) clearInterval(tokenRefreshTimer);
    tokenRefreshTimer = setInterval(refreshAuthToken, 45 * 60 * 1000);
}

async function refreshAuthToken() {
    try {
        const response = await fetch(`${CONFIG.orchestratorUrl}/api/auth/refresh`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ refreshToken: refreshToken })
        });

        const data = await response.json();

        if (data.success && data.data) {
            authToken = data.data.accessToken;
            refreshToken = data.data.refreshToken;
            currentUser = data.data.user;

            localStorage.setItem('authToken', authToken);
            localStorage.setItem('refreshToken', refreshToken);

            console.log('Token refreshed successfully');
            return true;
        }
    } catch (error) {
        console.error('Token refresh failed:', error);
    }

    return false;
}

// ============================================
// SETTINGS
// ============================================
function loadSettings() {
    const urlInput = document.getElementById('settings-orchestrator-url');
    if (urlInput) urlInput.value = CONFIG.orchestratorUrl;
    updateWalletDisplay();
}

function saveSettings() {
    const urlInput = document.getElementById('settings-orchestrator-url');
    if (urlInput) {
        CONFIG.orchestratorUrl = urlInput.value;
        localStorage.setItem('orchestratorUrl', CONFIG.orchestratorUrl);
        showToast('Settings saved', 'success');
        refreshData();
    }
}

// ============================================
// DATA LOADING
// ============================================
async function refreshData() {
    await Promise.all([
        loadStats(),
        loadVms(),
        loadNodes(),
        loadSshKeys()
    ]);
}

async function loadStats() {
    try {
        const data = await api('/api/system/stats');
        if (data.success) {
            const stats = data.data;
            document.getElementById('stat-nodes').textContent = `${stats.onlineNodes}/${stats.totalNodes}`;
            document.getElementById('stat-vms').textContent = stats.totalVms;
            document.getElementById('stat-vms-running').textContent = `${stats.runningVms} running`;
            document.getElementById('stat-cpu').textContent = stats.availableCpuCores;
            document.getElementById('stat-memory').textContent = formatMemory(stats.availableMemoryMb);
        }
    } catch (e) {
        console.error('Failed to load stats:', e);
    }
}

async function loadVms() {
    try {
        const data = await api('/api/vms');
        const vms = data.data?.items || [];

        const recentHtml = vms.slice(0, 5).map(vm => renderVmRow(vm)).join('');
        document.getElementById('recent-vms-table').innerHTML = recentHtml || '<tr><td colspan="4" style="text-align:center;padding:40px;color:var(--text-muted)">No VMs yet</td></tr>';

        const allHtml = vms.map(vm => renderVmRowFull(vm)).join('');
        document.getElementById('all-vms-table').innerHTML = allHtml || '<tr><td colspan="6" style="text-align:center;padding:40px;color:var(--text-muted)">No VMs yet</td></tr>';
    } catch (e) {
        console.error('Failed to load VMs:', e);
    }
}

async function loadNodes() {
    try {
        const data = await api('/api/nodes');
        const nodes = data.data || [];

        nodes.forEach(n => { nodesCache[n.id] = n; });

        const html = nodes.slice(0, 5).map(node => `
            <div class="node-item">
                <div class="node-status-indicator ${node.status === 'Online' ? '' : 'offline'}"></div>
                <div class="node-info">
                    <div class="node-name">${node.name}</div>
                    <div class="node-specs">${node.resources?.cpuCores || 0} CPU • ${formatMemory(node.resources?.memoryMb)}</div>
                </div>
                <div class="node-metrics">
                    <div class="node-usage">${node.activeVms || 0}</div>
                    <div class="node-usage-label">VMs</div>
                </div>
            </div>
        `).join('');

        document.getElementById('nodes-list').innerHTML = html || '<p style="color:var(--text-muted);text-align:center;padding:20px;">No nodes available</p>';

        const allHtml = nodes.map(node => `
            <div class="node-item">
                <div class="node-status-indicator ${node.status === 'Online' ? '' : 'offline'}"></div>
                <div class="node-info">
                    <div class="node-name">${node.name}</div>
                    <div class="node-specs">${node.resources?.cpuCores || 0} CPU • ${formatMemory(node.resources?.memoryMb)} • ${node.region || 'Unknown'}</div>
                </div>
                <div class="node-metrics">
                    <div class="node-usage">${node.activeVms || 0}</div>
                    <div class="node-usage-label">Active VMs</div>
                </div>
            </div>
        `).join('');

        document.getElementById('all-nodes-list').innerHTML = allHtml || '<p style="color:var(--text-muted);text-align:center;padding:20px;">No nodes available</p>';
    } catch (e) {
        console.error('Failed to load nodes:', e);
    }
}

async function loadSshKeys() {
    try {
        const data = await api('/api/user/me/ssh-keys');
        const keys = data.data || [];

        const html = keys.map(key => `
            <div class="node-item">
                <div class="node-info">
                    <div class="node-name">${key.name}</div>
                    <div class="node-specs">${key.fingerprint}</div>
                </div>
                <button class="action-btn danger" onclick="deleteSshKey('${key.id}')" title="Delete">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                    </svg>
                </button>
            </div>
        `).join('');

        document.getElementById('ssh-keys-list').innerHTML = html || '<p style="color:var(--text-muted);text-align:center;padding:20px;">No SSH keys added yet</p>';
    } catch (e) {
        console.error('Failed to load SSH keys:', e);
    }
}

function getNodePublicIp(nodeId) {
    const node = nodesCache[nodeId];
    return node?.publicIp || null;
}

// ============================================
// VM RENDERING
// ============================================
// Must match VmStatus enum in backend
const VM_STATUS_MAP = {
    0: 'Pending',
    1: 'Scheduling',
    2: 'Provisioning',
    3: 'Running',
    4: 'Stopping',
    5: 'Stopped',
    6: 'Migrating',
    7: 'Error',
    8: 'Deleted'
};

function getStatusString(status) {
    if (typeof status === 'string') return status;
    if (typeof status === 'number') return VM_STATUS_MAP[status] || 'Unknown';
    return 'Unknown';
}

function renderVmRow(vm) {
    const statusStr = getStatusString(vm.status);
    const statusClass = statusStr.toLowerCase();
    return `
        <tr>
            <td>
                <div class="vm-name">
                    <div class="vm-icon">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="3" width="20" height="14" rx="2"/>
                            <path d="M8 21h8M12 17v4"/>
                        </svg>
                    </div>
                    <div>
                        ${vm.name}
                        <div class="vm-id">${vm.id.slice(0, 8)}...</div>
                    </div>
                </div>
            </td>
            <td>
                <span class="status-badge ${statusClass}">
                    <span class="status-dot"></span>
                    ${statusStr}
                </span>
            </td>
            <td>${vm.spec?.cpuCores || 1} CPU • ${formatMemory(vm.spec?.memoryMb)}</td>
            <td>
                ${statusStr === 'Running' ? `
                    <button class="action-btn" onclick="stopVm('${vm.id}')" title="Stop">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>
                    </button>
                ` : statusStr === 'Stopped' ? `
                    <button class="action-btn" onclick="startVm('${vm.id}')" title="Start">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><polygon points="5,3 19,12 5,21"/></svg>
                    </button>
                ` : ''}
                <button class="action-btn danger" onclick="deleteVm('${vm.id}')" title="Delete">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                    </svg>
                </button>
            </td>
        </tr>
    `;
}

function renderVmRowFull(vm) {
    const statusStr = getStatusString(vm.status);
    const statusClass = statusStr.toLowerCase();
    const created = new Date(vm.createdAt).toLocaleDateString();
    const vmIp = vm.networkConfig?.privateIp;
    const canConnect = statusStr === 'Running' && vmIp;
    const nodeIp = vm.nodeId ? getNodePublicIp(vm.nodeId) : null;

    return `
        <tr>
            <td>
                <div class="vm-name">
                    <div class="vm-icon">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="3" width="20" height="14" rx="2"/>
                            <path d="M8 21h8M12 17v4"/>
                        </svg>
                    </div>
                    <div>
                        ${vm.name}
                        <div class="vm-id">${vm.id.slice(0, 8)}...</div>
                    </div>
                </div>
            </td>
            <td>
                <span class="status-badge ${statusClass}">
                    <span class="status-dot"></span>
                    ${statusStr}
                </span>
            </td>
            <td class="vm-id">${vm.nodeId ? vm.nodeId.slice(0, 8) + '...' : '-'}</td>
            <td>${vm.spec?.cpuCores || 1} CPU • ${formatMemory(vm.spec?.memoryMb)} • ${vm.spec?.diskGb || 20} GB</td>
            <td>${created}</td>
            <td>
                ${canConnect && nodeIp ? `
                    <button class="action-btn" onclick="showConnectInfo('${nodeIp}', '${vmIp}', '${vm.name}')" title="Connect">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/>
                        </svg>
                    </button>
                    <button class="action-btn" onclick="openTerminal('${nodeIp}', '${vmIp}', '${vm.name}', '${vm.id}')" title="Terminal">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="4,17 10,11 4,5"/>
                            <line x1="12" y1="19" x2="20" y2="19"/>
                        </svg>
                    </button>
                    <button class="action-btn" onclick="revealPassword('${vm.id}', '${vm.name}')" title="Show Password">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M21 2l-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4"/>
                        </svg>
                    </button>
                ` : ''}
                ${statusStr === 'Running' ? `
                    <button class="action-btn" onclick="stopVm('${vm.id}')" title="Stop">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="6" width="12" height="12" rx="1"/></svg>
                    </button>
                ` : statusStr === 'Stopped' ? `
                    <button class="action-btn" onclick="startVm('${vm.id}')" title="Start">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><polygon points="5,3 19,12 5,21"/></svg>
                    </button>
                ` : ''}
                <button class="action-btn danger" onclick="deleteVm('${vm.id}')" title="Delete">
                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                    </svg>
                </button>
            </td>
        </tr>
    `;
}

// ============================================
// VM ACTIONS
// ============================================
async function createVm() {
    const name = document.getElementById('vm-name').value.trim();
    const cpuCores = parseInt(document.getElementById('vm-cpu').value);
    const memoryMb = parseInt(document.getElementById('vm-memory').value);
    const diskGb = parseInt(document.getElementById('vm-disk').value);
    const imageId = document.getElementById('vm-image').value;

    if (!name) {
        showToast('Please enter a VM name', 'error');
        return;
    }

    try {
        const response = await api('/api/vms', {
            method: 'POST',
            body: JSON.stringify({
                name,
                spec: { cpuCores, memoryMb, diskGb, imageId }
            })
        });

        if (response.success && response.data) {
            const vmId = response.data.vmId;
            // WORKAROUND: Backend has password/error fields swapped
            // Check both fields until backend is fixed
            const password = response.data.password || response.data.error;

            closeModal('create-vm-modal');

            // Only show password modal if we got a valid password (not an error code)
            if (password && !password.includes('_') && password.includes('-')) {
                await showPasswordModal(vmId, name, password);
            }

            refreshData();
            showToast('VM created successfully', 'success');
        } else {
            showToast(response.message || 'Failed to create VM', 'error');
        }
    } catch (error) {
        console.error('Create VM error:', error);
        showToast('Failed to create VM', 'error');
    }
}

async function startVm(vmId) {
    try {
        const data = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            body: JSON.stringify({ action: 'Start' })
        });
        if (data.success) {
            showToast('VM starting...', 'success');
            refreshData();
        } else {
            showToast(data.message || 'Failed to start VM', 'error');
        }
    } catch (e) {
        console.error('Start VM error:', e);
        showToast('Failed to start VM', 'error');
    }
}

async function stopVm(vmId) {
    try {
        const data = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            body: JSON.stringify({ action: 'Stop' })
        });
        if (data.success) {
            showToast('VM stopping...', 'success');
            refreshData();
        } else {
            showToast(data.message || 'Failed to stop VM', 'error');
        }
    } catch (e) {
        console.error('Stop VM error:', e);
        showToast('Failed to stop VM', 'error');
    }
}

async function deleteVm(vmId) {
    if (!confirm('Are you sure you want to delete this VM?')) return;

    try {
        const data = await api(`/api/vms/${vmId}`, { method: 'DELETE' });
        if (data.success) {
            showToast('VM deleted', 'success');
            refreshData();
        } else {
            showToast('Failed to delete VM', 'error');
        }
    } catch (e) {
        showToast('Failed to delete VM', 'error');
    }
}

// ============================================
// SSH KEY ACTIONS
// ============================================
async function addSshKey() {
    const name = document.getElementById('ssh-key-name').value;
    const publicKey = document.getElementById('ssh-key-value').value;

    if (!name || !publicKey) {
        showToast('Please fill in all fields', 'error');
        return;
    }

    try {
        const data = await api('/api/user/me/ssh-keys', {
            method: 'POST',
            body: JSON.stringify({ name, publicKey })
        });

        if (data.success) {
            showToast('SSH key added', 'success');
            closeModal('add-ssh-key-modal');
            document.getElementById('ssh-key-name').value = '';
            document.getElementById('ssh-key-value').value = '';
            loadSshKeys();
        } else {
            showToast(data.message || 'Failed to add SSH key', 'error');
        }
    } catch (e) {
        showToast('Failed to add SSH key', 'error');
    }
}

async function deleteSshKey(keyId) {
    if (!confirm('Are you sure you want to delete this SSH key?')) return;

    try {
        const data = await api(`/api/user/me/ssh-keys/${keyId}`, { method: 'DELETE' });
        if (data.success) {
            showToast('SSH key deleted', 'success');
            loadSshKeys();
        } else {
            showToast('Failed to delete SSH key', 'error');
        }
    } catch (e) {
        showToast('Failed to delete SSH key', 'error');
    }
}

// ============================================
// WALLET-BASED PASSWORD ENCRYPTION
// ============================================

// Check if we're in a secure context (HTTPS or localhost)
function isSecureContext() {
    return window.isSecureContext ||
        location.protocol === 'https:' ||
        location.hostname === 'localhost' ||
        location.hostname === '127.0.0.1';
}

// Simple XOR-based obfuscation for non-secure contexts (DEV ONLY)
// NOT cryptographically secure - only for development/testing
function simpleObfuscate(text, key) {
    let result = '';
    for (let i = 0; i < text.length; i++) {
        result += String.fromCharCode(text.charCodeAt(i) ^ key.charCodeAt(i % key.length));
    }
    return btoa(result); // Base64 encode
}

function simpleDeobfuscate(encoded, key) {
    const text = atob(encoded); // Base64 decode
    let result = '';
    for (let i = 0; i < text.length; i++) {
        result += String.fromCharCode(text.charCodeAt(i) ^ key.charCodeAt(i % key.length));
    }
    return result;
}

async function deriveEncryptionKey(signer) {
    const signature = await signer.signMessage(ENCRYPTION_MESSAGE);

    // If not in secure context, return the signature as a simple key
    if (!isSecureContext() || !crypto.subtle) {
        console.warn('⚠️ Not in secure context - using basic obfuscation (NOT secure for production)');
        return { type: 'simple', key: signature };
    }

    // Secure context - use proper AES encryption
    const encoder = new TextEncoder();
    const sigBytes = encoder.encode(signature);
    const hashBuffer = await crypto.subtle.digest('SHA-256', sigBytes);
    const aesKey = await crypto.subtle.importKey('raw', hashBuffer, { name: 'AES-GCM' }, false, ['encrypt', 'decrypt']);
    return { type: 'aes', key: aesKey };
}

async function encryptPassword(password, keyObj) {
    if (keyObj.type === 'simple') {
        // Simple obfuscation for dev/HTTP
        return 'SIMPLE:' + simpleObfuscate(password, keyObj.key);
    }

    // AES-GCM encryption for production/HTTPS
    const encoder = new TextEncoder();
    const data = encoder.encode(password);
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const ciphertext = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv }, keyObj.key, data);
    const ivBase64 = btoa(String.fromCharCode(...iv));
    const ctBase64 = btoa(String.fromCharCode(...new Uint8Array(ciphertext)));
    return `${ivBase64}:${ctBase64}`;
}

async function decryptPassword(encryptedData, keyObj) {
    // Check if it's simple obfuscation
    if (encryptedData.startsWith('SIMPLE:')) {
        if (keyObj.type !== 'simple') {
            throw new Error('Password was encrypted in dev mode, need same wallet to decrypt');
        }
        return simpleDeobfuscate(encryptedData.substring(7), keyObj.key);
    }

    // AES-GCM decryption
    if (keyObj.type === 'simple') {
        throw new Error('Password was encrypted with HTTPS. Please access via HTTPS to decrypt.');
    }

    const [ivBase64, ctBase64] = encryptedData.split(':');
    const iv = Uint8Array.from(atob(ivBase64), c => c.charCodeAt(0));
    const ciphertext = Uint8Array.from(atob(ctBase64), c => c.charCodeAt(0));
    const decrypted = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: iv }, keyObj.key, ciphertext);
    return new TextDecoder().decode(decrypted);
}

async function getEncryptionKey() {
    if (cachedEncryptionKey) return cachedEncryptionKey;
    if (!ethersSigner) throw new Error('No wallet connected');
    cachedEncryptionKey = await deriveEncryptionKey(ethersSigner);
    return cachedEncryptionKey;
}

async function showPasswordModal(vmId, vmName, password) {
    const isSecure = isSecureContext();
    const securityWarning = !isSecure ? `
        <div style="background: rgba(245, 158, 11, 0.1); border: 1px solid rgba(245, 158, 11, 0.3); padding: 12px; border-radius: 8px; margin: 10px 0;">
            <p style="color: #fbbf24; margin: 0; font-size: 13px;">
                <strong>⚠️ Development Mode:</strong> Using basic encryption over HTTP.
                Use HTTPS in production for secure encryption.
            </p>
        </div>
    ` : '';

    return new Promise((resolve) => {
        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.innerHTML = `
            <div class="modal" style="max-width: 500px;">
                <div class="modal-header">
                    <h2 class="modal-title">🔐 Save Your VM Password</h2>
                </div>
                <div class="modal-body">
                    <p>Your VM <strong>${vmName}</strong> has been created with this password:</p>
                    <div style="background: var(--bg-deep); padding: 20px; border-radius: 8px; margin: 20px 0; text-align: center;">
                        <code style="font-size: 1.5em; color: var(--accent-primary); letter-spacing: 1px;">${password}</code>
                    </div>
                    ${securityWarning}
                    <div style="background: rgba(239, 68, 68, 0.1); border: 1px solid rgba(239, 68, 68, 0.3); padding: 15px; border-radius: 8px; margin: 15px 0;">
                        <p style="color: #fca5a5; margin: 0;">
                            <strong>⚠️ Important:</strong> Save this password now. It will be encrypted with your wallet for later retrieval.
                        </p>
                    </div>
                </div>
                <div class="modal-footer">
                    <button class="btn btn-secondary" onclick="navigator.clipboard.writeText('${password}'); showToast('Copied!', 'success');">📋 Copy</button>
                    <button class="btn btn-primary" id="encrypt-save-btn">🔒 Encrypt & Save</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);

        document.getElementById('encrypt-save-btn').onclick = async () => {
            try {
                const keyObj = await getEncryptionKey();
                const encrypted = await encryptPassword(password, keyObj);

                await api(`/api/vms/${vmId}/secure-password`, {
                    method: 'POST',
                    body: JSON.stringify({ encryptedPassword: encrypted })
                });

                showToast('Password encrypted and saved', 'success');
                modal.remove();
                resolve();
            } catch (error) {
                console.error('Failed to encrypt:', error);
                showToast('Failed to encrypt - save password manually!', 'error');
            }
        };

        modal.onclick = (e) => { if (e.target === modal) { modal.remove(); resolve(); } };
    });
}

async function revealPassword(vmId, vmName) {
    try {
        const response = await api(`/api/vms/${vmId}/encrypted-password`);

        if (!response.success || !response.data?.encryptedPassword) {
            showToast('Password not available', 'error');
            return;
        }

        const keyObj = await getEncryptionKey();
        const password = await decryptPassword(response.data.encryptedPassword, keyObj);

        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.innerHTML = `
            <div class="modal" style="max-width: 450px;">
                <div class="modal-header">
                    <h2 class="modal-title">🔑 VM Password</h2>
                </div>
                <div class="modal-body">
                    <p>Password for <strong>${vmName}</strong>:</p>
                    <div style="background: var(--bg-deep); padding: 20px; border-radius: 8px; margin: 15px 0; text-align: center;">
                        <code style="font-size: 1.4em; color: var(--accent-primary);">${password}</code>
                    </div>
                </div>
                <div class="modal-footer">
                    <button class="btn btn-secondary" onclick="navigator.clipboard.writeText('${password}'); showToast('Copied!', 'success');">📋 Copy</button>
                    <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">Close</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);
        modal.onclick = (e) => { if (e.target === modal) modal.remove(); };

    } catch (error) {
        console.error('Failed to reveal password:', error);
        showToast('Failed to decrypt password. Ensure same wallet is connected.', 'error');
    }
}

// ============================================
// CONNECT INFO & TERMINAL
// ============================================
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
                        <button class="connect-code-copy" onclick="navigator.clipboard.writeText('ssh -J decloud@${nodeIp} decloud@${vmIp}'); this.textContent='Copied!'">Copy</button>
                    </div>
                </div>
                <div class="connect-section">
                    <div class="connect-section-title">VM Details</div>
                    <p class="connect-note">
                        <strong>Node IP:</strong> ${nodeIp}<br>
                        <strong>VM IP:</strong> ${vmIp}<br>
                        <strong>Username:</strong> decloud
                    </p>
                </div>
            </div>
        </div>
    `;
    document.body.appendChild(modal);
    modal.onclick = (e) => { if (e.target === modal) modal.remove(); };
}

function openTerminal(nodeIp, vmIp, vmName, vmId) {
    const terminalUrl = `${CONFIG.orchestratorUrl}/terminal.html?nodeIp=${nodeIp}&vmIp=${vmIp}&vmName=${encodeURIComponent(vmName)}&vmId=${vmId}`;
    window.open(terminalUrl, '_blank', 'width=900,height=600');
}

// ============================================
// NAVIGATION & MODALS
// ============================================
function showPage(pageId) {
    document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
    document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));

    document.getElementById(`page-${pageId}`).classList.add('active');
    document.querySelector(`.nav-item[data-page="${pageId}"]`).classList.add('active');
}

function openCreateVmModal() {
    document.getElementById('create-vm-modal').classList.add('active');
}

function openAddSshKeyModal() {
    document.getElementById('add-ssh-key-modal').classList.add('active');
    document.getElementById('ssh-key-name').value = '';
    document.getElementById('ssh-key-value').value = '';
}

function closeModal(modalId) {
    document.getElementById(modalId).classList.remove('active');
}

document.querySelectorAll('.modal-overlay').forEach(overlay => {
    overlay.addEventListener('click', (e) => {
        if (e.target === overlay) {
            overlay.classList.remove('active');
        }
    });
});

// ============================================
// HELPERS
// ============================================
function formatMemory(mb) {
    if (!mb) return '-';
    if (mb >= 1024) return (mb / 1024).toFixed(1) + ' GB';
    return mb + ' MB';
}

function showToast(message, type = 'success') {
    const container = document.getElementById('toast-container');
    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.innerHTML = `
        <svg class="toast-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
            ${type === 'success'
            ? '<path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/>'
            : type === 'error'
                ? '<circle cx="12" cy="12" r="10"/><line x1="15" y1="9" x2="9" y2="15"/><line x1="9" y1="9" x2="15" y2="15"/>'
                : '<circle cx="12" cy="12" r="10"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/>'}
        </svg>
        <span>${message}</span>
    `;
    container.appendChild(toast);
    setTimeout(() => toast.remove(), 4000);
}