// ============================================
// IMPORTS - ES6 Modules
// ============================================
import { createAppKit } from '@reown/appkit';
import { EthersAdapter } from '@reown/appkit-adapter-ethers';
import { mainnet, polygon, polygonAmoy, arbitrum } from '@reown/appkit/networks';
import { BrowserProvider } from 'ethers';
import { createDecloudSiweConfig } from './siwe-config.js';

import {
    getSSHCertificate,
    showSSHConnectionModal,
    downloadSSHBundle
} from './ssh-wallet.js';
import {
    initializePayment,
    setAuthToken,
    getBalance,
    showDepositModal,
    showBalanceModal,
    refreshBalanceDisplay,
    isInitialized as isPaymentInitialized
} from './payment.js';
import {
    initializeMarketplace,
    loadNodes as loadNodesFromMarketplace,
    searchNodes,
    clearNodeFilters
} from './marketplace.js';
import { initMarketplaceTemplates } from './marketplace-templates.js';
import { initMyTemplates } from './my-templates.js';
import './template-detail.js';
import './direct-access.js';

// Shared utilities + extracted modules
import {
    escapeHtml,
    sanitizeUrl,
    isValidIp,
    showToast,
    makeModalAccessible
} from './utils.js';
import {
    getStatusClass,
    getStatusText,
    renderServiceReadiness,
    renderServiceBadge
} from './status-helpers.js';
import {
    encryptPassword,
    decryptPassword,
    clearEncryptionKey
} from './wallet-crypto.js';
import {
    showPasswordModal,
    revealPassword,
    showConnectInfo,
    showSshInstructions,
    downloadSSHConfig,
    openTerminal,
    openFileBrowser,
    showVmMessages,
    showVmLogs
} from './vm-modals.js';
import {
    openCustomDomainsModal,
    closeCustomDomainsModal
} from './custom-domains.js';
import { ensureTosAccepted } from './tos.js';

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
// QUALITY TIER CONFIGURATION
// ============================================

const QUALITY_TIERS = {
    0: { // Guaranteed
        name: 'Guaranteed',
        pointsPerVCpu: 8,
        priceMultiplier: 2.5,
        description: 'Dedicated resources, guaranteed 1:1 CPU performance'
    },
    1: { // Standard
        name: 'Standard',
        pointsPerVCpu: 4,
        priceMultiplier: 1.0,
        description: 'Balanced performance and cost, 2:1 CPU overcommit'
    },
    2: { // Balanced
        name: 'Balanced',
        pointsPerVCpu: 2,
        priceMultiplier: 0.6,
        description: 'Cost-optimized for consistent workloads'
    },
    3: { // Burstable
        name: 'Burstable',
        pointsPerVCpu: 1,
        priceMultiplier: 0.4,
        description: 'Aggressive overcommit, lowest cost, variable performance'
    }
};

const BANDWIDTH_TIERS = {
    0: { name: 'Basic', speed: '10 Mbps', burst: '20 Mbps', hourlyRate: 0.002, description: 'Light browsing and text-based workloads' },
    1: { name: 'Standard', speed: '50 Mbps', burst: '100 Mbps', hourlyRate: 0.008, description: 'General browsing, video calls, moderate streaming' },
    2: { name: 'Performance', speed: '200 Mbps', burst: '400 Mbps', hourlyRate: 0.020, description: 'HD video streaming, large downloads, data-intensive tasks' },
    3: { name: 'Unmetered', speed: 'No cap', burst: 'No cap', hourlyRate: 0.040, description: 'No artificial bandwidth cap. Limited only by host NIC.' }
};

const REPLICATION_TIERS = {
    0: {
        label: 'Ephemeral',
        description: 'No replication. Data is lost permanently if the node fails. Use for stateless workloads, batch jobs, CI runners.',
        badge: 'No protection'
    },
    1: {
        label: 'Single replica (1×)',
        description: 'One replica. Survives if at least 1 block store provider holds the blocks.',
        badge: 'Basic protection'
    },
    3: {
        label: 'Standard (3×)',
        description: 'Three replicas. Survives loss of 2 provider nodes simultaneously. Recommended for all production workloads.',
        badge: 'Standard protection'
    },
    5: {
        label: 'High Availability (5×)',
        description: 'Five replicas. Survives loss of 4 provider nodes. Use for databases, ML checkpoints, critical stateful services.',
        badge: 'Maximum protection'
    }
};

const VmAction = {
    Start: 0,
    Stop: 1,
    Restart: 2,
    Pause: 3,
    Resume: 4,
    ForceStop: 5
}

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

// Shared in-flight refresh promise (single-flight) + access-token exp decoder.
let refreshInFlight = null;
function decodeJwtExpMs(token) {
    try {
        const b64 = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
        const payload = JSON.parse(atob(b64));
        return payload?.exp ? payload.exp * 1000 : 0;
    } catch { return 0; }
}

// Handles from constraint-builder.js module — one per mounted instance.
// _cbCreateHandle: create-VM modal
// _cbUpdateHandle: update-constraints modal (Error-state VM)
let _cbCreateHandle = null;
let _cbUpdateHandle = null;

// AppKit unsubscribe functions
let appKitUnsubscribers = [];

// ============================================
// INITIALIZATION
// ============================================
document.addEventListener('DOMContentLoaded', async () => {
    console.log('[App] Initializing DeCloud v' + __APP_VERSION__);

    // Initialize marketplace module with config
    initializeMarketplace(CONFIG.orchestratorUrl, escapeHtml);

    const sessionRestored = await restoreSession();
    if (!sessionRestored) {
        showLogin();
    }

    updateTierInfo();
    updateBandwidthInfo();
    updateReplicationInfo();
    updateEstimatedCost();
});

// ============================================
// Event listeners to CPU/Memory/Disk fields to update cost in real-time:
// ============================================
document.getElementById('vm-cpu').addEventListener('change', updateEstimatedCost);
document.getElementById('vm-memory').addEventListener('change', updateEstimatedCost);
document.getElementById('vm-disk').addEventListener('change', updateEstimatedCost);
document.getElementById('bandwidth-tier').addEventListener('change', updateBandwidthInfo);
document.getElementById('replication-factor')?.addEventListener('change', updateReplicationInfo);

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

        // SIWE One-Click Auth, wired to the /api/auth endpoints.
        const siweConfig = createDecloudSiweConfig({
            orchestratorUrl: CONFIG.orchestratorUrl,
            getChainId: () => {
                try { return Number(appKitModal?.getChainId?.()) || 137; } catch { return 137; }
            },
            onAuthenticated: (accessToken, user) => {
                authToken = accessToken;
                currentUser = user;
                localStorage.setItem('authToken', accessToken);
                CONFIG.wallet = user?.walletAddress || connectedAddress;
                if (CONFIG.wallet) localStorage.setItem('wallet', CONFIG.wallet);
                try { setAuthToken(accessToken); } catch { /* access-token mirror is best-effort */ }
                if (appKitModal) appKitModal.close();
                showDashboard();
                setupTokenRefresh();
                refreshData();
                if (ethersSigner) {
                    initializePayment(ethersSigner, accessToken)
                        .catch(e => console.warn('[Payment] init failed:', e?.message));
                }
            },
            onSignOut: () => { clearSession(); showLogin(); }
        });

        // Create AppKit instance with unified configuration
        appKitModal = createAppKit({
            adapters: [new EthersAdapter()],
            networks: [polygonAmoy, polygon, mainnet, arbitrum],
            projectId: WALLETCONNECT_PROJECT_ID,
            metadata: CONFIG.metadata,
            siweConfig,
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

                    // Authentication is driven by AppKit's SIWE flow
                    // (verifyMessage -> onAuthenticated). Do NOT trigger it here.
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
    clearEncryptionKey();

    // SECURITY: Clear sensitive data
    localStorage.removeItem('authToken');
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
    const storedWallet = localStorage.getItem('wallet');

    console.log('[Session] Attempting to restore session...');

    // Fast path: a still-valid access token -> no network, no refresh, no race.
    if (storedToken && storedWallet && decodeJwtExpMs(storedToken) > Date.now() + 30_000) {
        authToken = storedToken;
        CONFIG.wallet = storedWallet;
        try { setAuthToken(authToken); } catch { /* access-token mirror is best-effort */ }
        initializeAppKit().catch(e => console.log('[AppKit] Background init failed:', e));
        setupTokenRefresh();
        showDashboard();
        refreshData();
        return true;
    }

    // Expired/absent access token: cookie-based refresh (single-flight).
    if (storedWallet) {
        const refreshed = await refreshAuthToken();
        if (refreshed) {
            CONFIG.wallet = storedWallet;
            initializeAppKit().catch(e => console.log('[AppKit] Background init failed:', e));
            setupTokenRefresh();
            showDashboard();
            refreshData();
            return true;
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

function refreshAuthToken() {
    if (refreshInFlight) return refreshInFlight;
    refreshInFlight = (async () => {
        try {
            const response = await fetch(`${CONFIG.orchestratorUrl}/api/auth/refresh`, {
                method: 'POST',
                credentials: 'include',            // sends the httpOnly refresh cookie
                headers: { 'Content-Type': 'application/json' }
            });

            if (!response.ok) {
                console.error('[Auth] Token refresh failed:', response.status);
                return false;
            }

            const data = await response.json();
            if (data.success && data.data?.accessToken) {
                authToken = data.data.accessToken;
                currentUser = data.data.user;
                localStorage.setItem('authToken', authToken);
                try { setAuthToken(authToken); } catch { /* access-token mirror is best-effort */ }
                console.log('[Auth] Token refreshed successfully');
                return true;
            }
            return false;
        } catch (error) {
            console.error('[Auth] Token refresh error:', error);
            return false;
        } finally {
            refreshInFlight = null;
        }
    })();
    return refreshInFlight;
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
        credentials: 'include',
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

// Password encryption moved to wallet-crypto.js.

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
    loadUserBalance();
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

// showToast moved to utils.js.

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
    } else if (pageName === 'marketplace-templates') {
        initMarketplaceTemplates();
    } else if (pageName === 'my-templates') {
        initMyTemplates();
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
        loadVirtualMachines(),
        loadUserBalance()
    ]);
}

// ============================================
// PAYMENT & BALANCE
// ============================================

/**
 * Load and display user balance
 */
async function loadUserBalance() {
    try {
        const balance = await getBalance();
        updateBalanceDisplay(balance);
        return balance;
    } catch (error) {
        console.warn('[Balance] Failed to load:', error.message);
        return null;
    }
}

/**
 * Update balance in UI
 */
function updateBalanceDisplay(balance) {
    const balanceEl = document.getElementById('user-balance');
    const balanceContainer = document.getElementById('balance-container');

    if (balanceEl && balance) {
        balanceEl.textContent = `${balance.balance.toFixed(2)}`;

        // Show low balance warning (less than $5)
        if (balance.balance < 5) {
            balanceEl.classList.add('low-balance');
        } else {
            balanceEl.classList.remove('low-balance');
        }
    }

    if (balanceContainer) {
        balanceContainer.style.display = 'flex';
    }
}

/**
 * Handle balance card click - opens balance detail modal
 */
function handleBalanceCardClick() {
    console.debug('[Payment] Balance card clicked');
    if (!ethersSigner) {
        console.warn('[Payment] Please connect your wallet first.');
        showToast('Please connect your wallet first', 'error');
        return;
    }

    if (!isPaymentInitialized()) {
        console.warn('[Payment] Payment system not available. isPaymentInitialized:', isPaymentInitialized());
        showToast('Payment system not available', 'error');
        return;
    }

    showBalanceModal();
}

async function loadDashboardStats() {
    try {
        const response = await api('/api/system/stats');
        const data = await response.json();

        if (data.success) {
            const stats = data.data;

            // VM and Node counts
            document.getElementById('stat-vms').textContent = stats.totalVms || 0;
            document.getElementById('stat-nodes').textContent = stats.onlineNodes || 0;

            // ========================================
            // DISPLAY COMPUTE POINTS (not raw cores)
            // ========================================
            const totalPoints = stats.totalComputePoints || 0;
            const usedPoints = stats.usedComputePoints || 0;
            const availablePoints = stats.availableComputePoints || 0;
            const utilizationPercent = stats.computePointUtilizationPercent || 0;

            // Show "X / Y points (Z% used)"
            document.getElementById('stat-cpu').textContent =
                `${availablePoints} / ${totalPoints} points`;

            // Optional: Add a tooltip or secondary display showing percentage
            const cpuElement = document.getElementById('stat-cpu');
            cpuElement.title = `${utilizationPercent.toFixed(1)}% utilized (${usedPoints} points used)`;

            // Memory display
            document.getElementById('stat-memory').textContent =
                `${((stats.availableMemoryGb || 0)).toFixed(1)} GB`;
            // Storage display
            document.getElementById('stat-storage').textContent =
                `${((stats.availableStorageGb || 0)).toFixed(1)} GB`;
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
        tbody.innerHTML = '<tr><td colspan="7" class="empty-state" style="text-align: center; padding: 40px;">No VMs found. Create your first VM to get started.</td></tr>';
        return;
    }

    // Cache the most recent VM list so delegated handlers can resolve full objects by id.
    vmCache = {};
    for (const vm of vms) vmCache[vm.id] = vm;

    tbody.innerHTML = vms.map(vm => {
        const networkConfig = vm.networkConfig || {};
        const vmIp = networkConfig.isIpAssigned ? networkConfig.privateIp : 'pending';
        const sshJumpHost = networkConfig.sshJumpHost || 'pending';
        const sshJumpPort = networkConfig.sshJumpPort || 2222;
        const nodeAgentHost = networkConfig.nodeAgentHost || 'pending';
        const nodeAgentPort = networkConfig.nodeAgentPort || 5100;

        const nodeName = vm.nodeId ? (nodesCache[vm.nodeId] || vm.nodeId.substring(0, 8)) : 'None';

        const tierBadges = {
            0: '<span class="tier-badge tier-guaranteed">Guaranteed</span>',
            1: '<span class="tier-badge tier-standard">Standard</span>',
            2: '<span class="tier-badge tier-balanced">Balanced</span>',
            3: '<span class="tier-badge tier-burstable">Burstable</span>',
        };
        const tierBadge = tierBadges[vm.spec.qualityTier] || '';

        const constraintCount = vm.spec?.constraints?.length ?? 0;
        const constraintChip = constraintCount > 0
            ? `<span class="cb-chip" title="${escapeHtml(constraintCount + ' scheduling constraint(s)')}">⚙ ${constraintCount}</span>`
            : '';

        const messagesCount = vm.messages?.length ?? 0;
        const memoryMb = Math.round((vm.spec?.memoryBytes || 0) / (1024 * 1024));
        const diskGb = ((vm.spec?.diskBytes || 0) / (1024 * 1024 * 1024)).toFixed(2);

        return `
        <tr data-status="${escapeHtml(getStatusClass(vm.status))}" data-vm-id="${escapeHtml(vm.id)}">
            <td>
                <div class="vm-name">
                    <div class="vm-status ${escapeHtml(getStatusClass(vm.status))}"></div>
                    ${escapeHtml(vm.name)}
                    ${tierBadge}
                </div>
                ${constraintChip ? `<div style="margin-top:3px">${constraintChip}</div>` : ''}
            </td>
            <td>${escapeHtml(nodeName)}</td>
            <td>${vm.spec?.virtualCpuCores || 0} cores</td>
            <td>${memoryMb} MB</td>
            <td>${diskGb} GB</td>
            <td>
                <div class="vm-status-cell">
                    <span class="status-badge status-${escapeHtml(getStatusClass(vm.status))}">
                        ${escapeHtml(getStatusText(vm.status))}
                    </span>
                    ${renderServiceBadge(vm.services, vm.status)}
                </div>
                ${renderServiceReadiness(vm.services, vm.status)}
            </td>
            <td>
                <div class="table-actions">
                    <button class="btn btn-sm btn-primary" data-vm-action="connect-info"
                            data-ssh-host="${escapeHtml(sshJumpHost)}" data-ssh-port="${sshJumpPort}"
                            data-vm-ip="${escapeHtml(vmIp)}" data-node-host="${escapeHtml(nodeAgentHost)}"
                            data-node-port="${nodeAgentPort}" title="Connection Info">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71"/>
                            <path d="M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71"/>
                        </svg>
                    </button>

                    <button class="btn btn-sm btn-secondary" data-vm-action="direct-access" title="Direct Access (TCP/UDP Ports)">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <circle cx="12" cy="12" r="3"/>
                            <path d="M12 1v6m0 6v6M5.64 5.64l4.24 4.24m4.24 4.24l4.24 4.24M1 12h6m6 0h6M5.64 18.36l4.24-4.24m4.24-4.24l4.24-4.24"/>
                        </svg>
                    </button>

                    <button class="btn btn-sm btn-secondary" data-vm-action="custom-domains" title="Custom Domains">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <circle cx="12" cy="12" r="10"/>
                            <line x1="2" y1="12" x2="22" y2="12"/>
                            <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>
                        </svg>
                    </button>

                    <button class="btn btn-sm" data-vm-action="terminal"
                            data-node-host="${escapeHtml(nodeAgentHost)}" data-node-port="${nodeAgentPort}"
                            data-vm-ip="${escapeHtml(vmIp)}" title="Open Terminal">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="4 17 10 11 4 5"/>
                            <line x1="12" y1="19" x2="20" y2="19"/>
                        </svg>
                    </button>

                    <button class="btn btn-sm" data-vm-action="file-browser"
                            data-node-host="${escapeHtml(nodeAgentHost)}" data-node-port="${nodeAgentPort}"
                            data-vm-ip="${escapeHtml(vmIp)}" title="File Browser">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>
                        </svg>
                    </button>

                    <button class="btn-icon" data-vm-action="reveal-password" title="Show Password">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8z"/>
                            <circle cx="12" cy="12" r="3"/>
                        </svg>
                    </button>

                    ${vm.status === 3
                        ? `<button class="btn btn-sm btn-warning" data-vm-action="stop" title="Stop">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><rect x="6" y="4" width="4" height="16"/><rect x="14" y="4" width="4" height="16"/></svg>
                           </button>`
                        : `<button class="btn btn-sm btn-success" data-vm-action="start" title="Start">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="currentColor"><polygon points="5 3 19 12 5 21 5 3"/></svg>
                           </button>`
                    }
                    <button class="btn btn-sm btn-secondary" data-vm-action="logs" title="View boot log">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/>
                            <polyline points="14 2 14 8 20 8"/>
                            <line x1="16" y1="13" x2="8" y2="13"/>
                            <line x1="16" y1="17" x2="8" y2="17"/>
                            <polyline points="10 9 9 9 8 9"/>
                        </svg>
                    </button>

                    <button class="btn btn-sm btn-secondary" data-vm-action="messages" title="View events"
                        style="${messagesCount === 0 ? 'opacity:0.45' : ''}">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/>
                        </svg>
                        ${messagesCount > 0 ? `<span style="font-size:10px;font-weight:700;">${messagesCount}</span>` : ''}
                    </button>

                    <button class="btn btn-sm btn-danger" data-vm-action="delete" title="Delete">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="3 6 5 6 21 6"/>
                            <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                        </svg>
                    </button>

                    ${vm.status === 8 ? `
                    <button class="btn btn-sm btn-secondary" data-vm-action="update-constraints" title="Update Scheduling Constraints">
                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <circle cx="12" cy="12" r="3"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14M4.93 4.93a10 10 0 0 0 0 14.14"/>
                        </svg>
                    </button>` : ''}
                </div>
            </td>
        </tr>
    `}).join('');

    attachVmsTableDelegation(tbody);
}

let vmsTableDelegated = false;
let vmCache = {};
function attachVmsTableDelegation(tbody) {
    if (vmsTableDelegated) return;
    vmsTableDelegated = true;
    tbody.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-vm-action]');
        if (!btn) return;
        const row = btn.closest('tr[data-vm-id]');
        if (!row) return;
        const vmId = row.dataset.vmId;
        const vm = vmCache[vmId];
        if (!vm) return;
        const action = btn.dataset.vmAction;
        const vmName = vm.name;
        switch (action) {
            case 'connect-info':
                window.showConnectInfo(
                    btn.dataset.sshHost, parseInt(btn.dataset.sshPort, 10),
                    btn.dataset.vmIp, vmId, vmName,
                    btn.dataset.nodeHost, parseInt(btn.dataset.nodePort, 10)
                );
                break;
            case 'direct-access':
                window.openDirectAccessModal(vmId, vmName);
                break;
            case 'custom-domains':
                window.openCustomDomainsModal(vmId, vmName);
                break;
            case 'terminal':
                window.openTerminal(vmId, vmName, btn.dataset.nodeHost, parseInt(btn.dataset.nodePort, 10), btn.dataset.vmIp);
                break;
            case 'file-browser':
                window.openFileBrowser(vmId, vmName, btn.dataset.nodeHost, parseInt(btn.dataset.nodePort, 10), btn.dataset.vmIp);
                break;
            case 'reveal-password':
                window.revealPassword(vmId);
                break;
            case 'start':
                window.startVM(vmId);
                break;
            case 'stop':
                window.stopVM(vmId);
                break;
            case 'logs':
                window.showVmLogs(vmId, vmName);
                break;
            case 'messages':
                window.showVmMessages(vmId, vmName);
                break;
            case 'delete':
                window.deleteVM(vmId, vmName);
                break;
            case 'update-constraints':
                window.openUpdateConstraintsModal(vmId, JSON.stringify(vm.spec?.constraints ?? []));
                break;
        }
    });
}

function renderDashboardVMs(vms) {
    const container = document.getElementById('recent-vms');
    if (!container) return;

    const recentVMs = vms.slice(0, 5);

    if (recentVMs.length === 0) {
        container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 20px;">No virtual machines yet</p>';
        return;
    }

    container.innerHTML = recentVMs.map(vm => {
        // Calculate memory in MB
        const memoryMB = (vm.spec?.memoryBytes / (1024 * 1024)) || 0;

        // Calculate disk in GB - FIXED: diskBytes is already in bytes
        const diskGB = (vm.spec?.diskBytes / (1024 * 1024 * 1024)) || 0;

        return `
            <div class="vm-card">
                <div class="vm-card-header">
                    <div class="vm-name">
                        <div class="vm-status ${vm.status}"></div>
                        ${escapeHtml(vm.name)}
                    </div>
                    <span class="status-badge status-${getStatusClass(vm.status)}">
                        ${getStatusText(vm.status)}
                    </span>
                </div>
                <div class="vm-card-specs">
                    <div class="spec-item">
                        <span class="spec-label">CPU</span>
                        <span class="spec-value">${vm.spec?.virtualCpuCores || 0} cores</span>
                    </div>
                    <div class="spec-item">
                        <span class="spec-label">Memory</span>
                        <span class="spec-value">${memoryMB.toFixed(0)} MB</span>
                    </div>
                    <div class="spec-item">
                        <span class="spec-label">Disk</span>
                        <span class="spec-value">${diskGB.toFixed(2)} GB</span>
                    </div>
                </div>
                ${renderServiceReadiness(vm.services, vm.status)}
            </div>
        `;
    }).join('');
}

async function loadNodes() {
    await loadNodesFromMarketplace();
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

    sshKeyCache = {};
    for (const k of keys) sshKeyCache[k.id] = k;

    tbody.innerHTML = keys.map(key => {
        const added = new Date(key.createdAt).toLocaleDateString();
        const fingerprint = key.fingerprint || 'N/A';

        return `
        <tr data-key-id="${escapeHtml(key.id)}">
            <td>${escapeHtml(key.name)}</td>
            <td><code style="font-size: 12px; color: #9ca3af;">${escapeHtml(fingerprint)}</code></td>
            <td>${escapeHtml(added)}</td>
            <td>
                <div class="table-actions">
                    <button class="btn-icon btn-icon-danger" data-key-action="delete" title="Delete">
                        <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <path d="M3 6h18M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/>
                        </svg>
                    </button>
                </div>
            </td>
        </tr>
    `}).join('');

    attachSshKeysTableDelegation(tbody);
}

let sshKeysTableDelegated = false;
let sshKeyCache = {};
function attachSshKeysTableDelegation(tbody) {
    if (sshKeysTableDelegated) return;
    sshKeysTableDelegated = true;
    tbody.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-key-action]');
        if (!btn) return;
        const row = btn.closest('tr[data-key-id]');
        if (!row) return;
        const keyId = row.dataset.keyId;
        const key = sshKeyCache[keyId];
        if (!key) return;
        if (btn.dataset.keyAction === 'delete') {
            window.deleteSSHKey(keyId, key.name);
        }
    });
}

// ============================================
// VM OPERATIONS
// ============================================

async function openCreateVMModal() {
    openStaticModal(document.getElementById('create-vm-modal'));
    const container = document.getElementById('constraint-builder-container');
    if (container) {
        // Destroy the previous instance (modal may be opened multiple times).
        _cbCreateHandle?.destroy();
        const { mount } = await import('./constraint-builder.js');
        _cbCreateHandle = await mount(container, {
            apiFetch: api,
            qualityTier: document.getElementById('quality-tier')?.value ?? 'Standard',
        });
    }
}

// Tracks accessibility-cleanup functions per static modal so we can run them
// when the modal is closed (regardless of how it was closed).
const _modalA11yCleanups = new Map();

function openStaticModal(modalEl, options = {}) {
    if (!modalEl) return;
    modalEl.classList.add('active');
    if (_modalA11yCleanups.has(modalEl)) _modalA11yCleanups.get(modalEl)();
    const cleanup = makeModalAccessible(modalEl, () => closeModal(modalEl.id), options);
    _modalA11yCleanups.set(modalEl, cleanup);
}

function closeModal(modalId) {
    const modalEl = document.getElementById(modalId);
    if (!modalEl) return;
    closeStaticModal(modalEl);
}

function closeStaticModal(modalEl) {
    if (!modalEl) return;
    modalEl.classList.remove('active');
    const cleanup = _modalA11yCleanups.get(modalEl);
    if (cleanup) {
        cleanup();
        _modalA11yCleanups.delete(modalEl);
    }
}

window.openStaticModal = openStaticModal;
window.closeStaticModal = closeStaticModal;

/**
 * Sanitize a raw VM name to DNS-safe format (mirrors server-side VmNameService.Sanitize).
 */
function sanitizeVmName(raw) {
    if (!raw || !raw.trim()) return '';
    let result = raw.toLowerCase().replace(/[\s_]/g, '-').replace(/[^a-z0-9-]/g, '').replace(/-{2,}/g, '-').replace(/^-+|-+$/g, '');
    if (result.length > 40) result = result.substring(0, 40).replace(/-+$/, '');
    return result;
}

/**
 * Validate a sanitized VM name (mirrors server-side VmNameService.Validate).
 * Returns null on success or an error message string.
 */
function validateVmName(sanitized) {
    if (!sanitized) return 'VM name is required';
    if (sanitized.length < 2) return 'VM name must be at least 2 characters';
    if (sanitized.length > 40) return 'VM name must be at most 40 characters';
    if (!/^[a-z]/.test(sanitized)) return 'VM name must start with a letter';
    if (sanitized.endsWith('-')) return 'VM name must not end with a hyphen';
    if (!/^[a-z][a-z0-9-]*[a-z0-9]$/.test(sanitized)) return 'VM name must contain only lowercase letters, numbers, and hyphens';
    return null;
}

// Expose naming utils globally for module-based scripts (template-detail.js, etc.)
window.sanitizeVmName = sanitizeVmName;
window.validateVmName = validateVmName;
window.previewVmName = previewVmName;

/**
 * Live preview of the canonical VM name as the user types.
 * Shows the sanitized name + example suffix.
 */
function previewVmName(inputEl, previewElId) {
    const previewEl = document.getElementById(previewElId);
    if (!previewEl) return;

    const raw = inputEl.value;
    if (!raw.trim()) {
        previewEl.textContent = '';
        previewEl.style.color = '';
        return;
    }

    const sanitized = sanitizeVmName(raw);
    const error = validateVmName(sanitized);
    if (error) {
        previewEl.textContent = error;
        previewEl.style.color = 'var(--color-error, #e53e3e)';
    } else {
        previewEl.textContent = `Your VM will be named: ${sanitized}-xxxx`;
        previewEl.style.color = 'var(--color-text-secondary, #a0aec0)';
    }
}

async function createVM() {
    const name = document.getElementById('vm-name').value.trim();
    const cpuCores = parseInt(document.getElementById('vm-cpu').value);
    const memoryMb = parseInt(document.getElementById('vm-memory').value);
    const diskGb = parseInt(document.getElementById('vm-disk').value);
    const imageId = document.getElementById('vm-image').value;
    const qualityTier = parseInt(document.getElementById('quality-tier').value);
    const bandwidthTier = parseInt(document.getElementById('bandwidth-tier').value);
    const replicationFactor = parseInt(document.getElementById('replication-factor')?.value ?? '3');

    // GPU — read from the GPU Access section
    const gpuMode = parseInt(document.getElementById('vm-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('vm-gpu-vram')?.value ?? '0');
    const gpuVramBytes = gpuMode === 2 && gpuVramGb > 0
        ? Math.round(gpuVramGb * 1024 ** 3)
        : null;

    // Get target node ID if user selected a specific node from marketplace
    const targetNodeId = document.getElementById('vm-target-node-id')?.value || null;

    // Client-side name validation (server is the authority, this is for UX)
    const sanitized = sanitizeVmName(name);
    const nameError = validateVmName(sanitized);
    if (nameError) {
        showToast(nameError, 'error');
        return;
    }

    // Read constraints — returns null if any row is incomplete
    // Collect constraints from the module builder.
    // Incomplete rows (no target or no operator) are silently skipped —
    // the backend validates and rejects malformed entries before scheduling.
    const constraints = _cbCreateHandle ? _cbCreateHandle.getConstraints() : [];

    try {
        const requestBody = {
            name,
            spec: {
                virtualCpuCores: cpuCores,
                memoryBytes: memoryMb * (1024 * 1024),
                diskBytes: diskGb * (1024 * 1024 * 1024),
                imageId: imageId,
                gpuMode: gpuMode,
                gpuVramBytes: gpuVramBytes,
                qualityTier: qualityTier,
                bandwidthTier: bandwidthTier,
                replicationFactor: replicationFactor,
                constraints: constraints.length > 0 ? constraints : undefined
            }
        };

        // Add nodeId if user selected a specific node
        if (targetNodeId) {
            requestBody.nodeId = targetNodeId;
            console.log('[CreateVM] Deploying to selected node:', targetNodeId);
        }

        const response = await api('/api/vms', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(requestBody)
        });

        const data = await response.json();

        if (data.success) {
            const vmId = data.data.vmId;
            const password = data.data.password

            // Only show password modal if we got a valid password (not an error code)
            if (password && !password.includes('_') && password.includes('-')) {
                await showPasswordModal(vmId, name, password);
            }

            showToast(`VM "${name}" created successfully!`, 'success');
            closeModal('create-vm-modal');

            document.getElementById('vm-name').value = '';
            document.getElementById('quality-tier').value = '1';
            document.getElementById('bandwidth-tier').value = '3';
            document.getElementById('replication-factor').value = '3';
            const gpuModeEl = document.getElementById('vm-gpu-mode');
            if (gpuModeEl) gpuModeEl.value = '0';

            const gpuVramRow = document.getElementById('vm-gpu-vram-row');
            if (gpuVramRow) gpuVramRow.style.display = 'none';
            updateTierInfo();
            updateBandwidthInfo();
            updateReplicationInfo();
            // Reset constraint builder — destroy and remount empty.
            const cbContainer = document.getElementById('constraint-builder-container');
            if (cbContainer && _cbCreateHandle) {
                _cbCreateHandle.setConstraints([]);
            }

            await refreshData();
        } else {
            showToast(data?.error?.message ?? data?.message ?? `Failed to create VM (HTTP ${response.status})`, 'error');
        }
    } catch (error) {
        console.error('[Create VM] Error:', error);
        showToast(error.message || 'Failed to create VM', 'error');
    }
}

async function startVM(vmId) {

    if (!vmId) {
        showToast('Please enter a VM ID', 'error');
        return;
    }

    try {
        const response = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action: VmAction.Start
            })
        });

        const data = await response.json();

        if (data.success) {
            showToast(`VM "${vmId}" started successfully!`, 'success');
        } else {
            showToast(data?.error?.message ?? data?.message ?? `Failed to start VM (HTTP ${response.status})`, 'error');
        }
    } catch (error) {
        console.error('[Start VM] Error:', error);
        showToast(error.message || 'Failed to start VM', 'error');
    }
}

async function stopVM(vmId) {

    if (!vmId) {
        showToast('Please enter a VM ID', 'error');
        return;
    }

    try {
        const response = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action: VmAction.Stop
            })
        });

        const data = await response.json();

        if (data.success) {
            showToast(`VM "${vmId}" stopped successfully!`, 'success');
        } else {
            showToast(data?.error?.message ?? data?.message ?? `Failed to stop VM (HTTP ${response.status})`, 'error');
        }
    } catch (error) {
        console.error('[Stop VM] Error:', error);
        showToast(error.message || 'Failed to stop VM', 'error');
    }
}

async function restartVM(vmId) {

    if (!vmId) {
        showToast('Please enter a VM ID', 'error');
        return;
    }

    try {
        const response = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action: VmAction.Restart
            })
        });

        const data = await response.json();

        if (data.success) {
            showToast(`VM "${vmId}" restarted successfully!`, 'success');
        } else {
            showToast(data.message || 'Failed to restart VM', 'error');
        }
    } catch (error) {
        console.error('[Restart VM] Error:', error);
        showToast('Failed to restart VM', 'error');
    }
}

async function forceStopVM(vmId) {

    if (!vmId) {
        showToast('Please enter a VM ID', 'error');
        return;
    }

    try {
        const response = await api(`/api/vms/${vmId}/action`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                action: VmAction.ForceStop
            })
        });

        const data = await response.json();

        if (data.success) {
            showToast(`VM "${vmId}" force-stopped successfully!`, 'success');
        } else {
            showToast(data.message || 'Failed to force-stop VM', 'error');
        }
    } catch (error) {
        console.error('[Force-stop VM] Error:', error);
        showToast('Failed to force-stop VM', 'error');
    }
}

function updateTierInfo() {
    const tierSelect = document.getElementById('quality-tier');
    const tierId = parseInt(tierSelect.value);
    const tier = QUALITY_TIERS[tierId];

    document.getElementById('tier-help-text').textContent = tier.name;

    const tierInfo = document.getElementById('tier-info');
    tierInfo.innerHTML = `
        <div class="tier-description">${tier.description}</div>
        <div class="tier-pricing">
            <span class="tier-points">${tier.pointsPerVCpu} points per vCPU</span>
            <span class="tier-multiplier">Price: ${tier.priceMultiplier}x</span>
        </div>
        `;

    updateEstimatedCost();
}

function updateBandwidthInfo() {
    const bwSelect = document.getElementById('bandwidth-tier');
    const bwId = parseInt(bwSelect.value);
    const bw = BANDWIDTH_TIERS[bwId];

    document.getElementById('bandwidth-help-text').textContent = bw.name;

    const bandwidthInfo = document.getElementById('bandwidth-info');
    bandwidthInfo.innerHTML = `
        <div class="tier-description">${bw.description}</div>
        <div class="tier-pricing">
            <span class="tier-points">${bw.speed} avg / ${bw.burst} burst</span>
            <span class="tier-multiplier">+$${bw.hourlyRate.toFixed(3)}/hr</span>
        </div>
    `;

    updateEstimatedCost();
}

function updateReplicationInfo() {
    const select = document.getElementById('replication-factor');
    if (!select) return;
    const factor = parseInt(select.value);
    const tier = REPLICATION_TIERS[factor] || REPLICATION_TIERS[3];

    const helpText = document.getElementById('replication-help-text');
    if (helpText) helpText.textContent = tier.label;

    const infoDiv = document.getElementById('replication-info');
    if (infoDiv) {
        infoDiv.innerHTML = `
            <div class="tier-description">${tier.description}</div>
            <div class="tier-pricing">
                <span class="tier-points">${tier.badge}</span>
                ${factor === 0
                ? '<span class="tier-multiplier">No storage cost</span>'
                : `<span class="tier-multiplier">+storage replication cost (${factor}×)</span>`}
            </div>
        `;
    }

    updateEstimatedCost();
}

/**
 * Show/hide VRAM input when GPU mode changes, then refresh cost estimate.
 */
function onGpuModeChange() {
    const gpuMode = parseInt(document.getElementById('vm-gpu-mode')?.value ?? '0');
    const gpuVramRow = document.getElementById('vm-gpu-vram-row');
    const gpuVramLabel = document.getElementById('vm-gpu-vram-label');
    if (gpuVramRow) gpuVramRow.style.display = gpuMode !== 0 ? 'flex' : 'none';
    // For Passthrough the label clarifies this is a minimum scheduling requirement,
    // not an exact allocation. Actual billing uses the assigned GPU's full VRAM.
    if (gpuVramLabel) {
        gpuVramLabel.textContent = gpuMode === 1
            ? 'Min. VRAM (GB)  ·  estimate only'
            : 'VRAM (GB)';
    }
    updateEstimatedCost();
}

async function updateEstimatedCost() {
    const cpuCores = parseInt(document.getElementById('vm-cpu').value);
    const memoryMb = parseInt(document.getElementById('vm-memory').value);
    const diskGb = parseInt(document.getElementById('vm-disk').value);
    const qualityTier = parseInt(document.getElementById('quality-tier').value);
    const bandwidthTier = parseInt(document.getElementById('bandwidth-tier').value);
    const replicationFactor = parseInt(document.getElementById('replication-factor')?.value ?? '3');
    const gpuMode = parseInt(document.getElementById('vm-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('vm-gpu-vram')?.value ?? '0');
    const gpuVramBytes = gpuMode === 2 && gpuVramGb > 0
        ? Math.round(gpuVramGb * 1024 ** 3) : null;

    // Compute points display — local, no network needed.
    const tier = QUALITY_TIERS[qualityTier];
    document.getElementById('compute-points').textContent =
        `${cpuCores * (tier?.pointsPerVCpu ?? 1)} points`;

    const costEl = document.getElementById('estimated-cost');
    costEl.textContent = 'Calculating…';

    try {
        const res = await api('/api/system/pricing/calculate', {
            method: 'POST',
            body: JSON.stringify({
                virtualCpuCores: cpuCores,
                memoryBytes: memoryMb * 1024 * 1024,
                diskBytes: diskGb * 1024 * 1024 * 1024,
                qualityTier,
                bandwidthTier,
                replicationFactor,
                gpuMode,
                gpuVramBytes,
            }),
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const d = await res.json();
        const calc = d.data ?? d;
        const daily = Number(calc.dailyTotal);
        const weekly = daily * 7;
        const monthly = Number(calc.monthlyTotal);
        const isPassthrough = gpuMode === 1;
        const passthroughNote = isPassthrough
            ? `<br><span style="font-size:0.75em;opacity:0.5">` +
            `GPU estimate based on minimum VRAM — actual cost reflects assigned GPU</span>`
            : '';
        costEl.innerHTML =
            `~$${Number(calc.hourlyTotal).toFixed(4)}/hr (default rates)` +
            `<br><span style="font-size:0.8em;opacity:0.6">` +
            `~$${daily.toFixed(2)}/day&nbsp;&nbsp;·&nbsp;&nbsp;` +
            `~$${weekly.toFixed(2)}/wk&nbsp;&nbsp;·&nbsp;&nbsp;` +
            `~$${monthly.toFixed(2)}/mo` +
            `</span>` + passthroughNote;
    } catch (_e) {
        costEl.textContent = 'Pricing unavailable';
    }
}

// ============================================
// CONSTRAINT BUILDER
// ============================================

/**
 * Open the Update Scheduling Constraints modal for an Error-state VM.
 * @param {string} vmId
 * @param {string|Array} currentConstraintsJson — JSON string (from onclick attr) or array
 */
function openUpdateConstraintsModal(vmId, currentConstraintsJson) {
    const currentConstraints = typeof currentConstraintsJson === 'string'
        ? JSON.parse(currentConstraintsJson)
        : (currentConstraintsJson ?? []);

    let modal = document.getElementById('update-constraints-modal');
    if (!modal) {
        modal = document.createElement('div');
        modal.id = 'update-constraints-modal';
        modal.className = 'modal-overlay';
        modal.innerHTML = `
            <div class="modal" style="max-width:560px">
                <div class="modal-header">
                    <h2 class="modal-title">Update Scheduling Constraints</h2>
                    <button class="modal-close" onclick="closeModal('update-constraints-modal')">
                        <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/>
                        </svg>
                    </button>
                </div>
                <div class="modal-body">
                    <p class="form-help" style="margin-bottom:1rem">
                        Replaces all scheduling constraints. The migration scheduler picks up changes within 10 s.
                        Clear all rows to accept any eligible node.
                    </p>
                    <div id="update-constraints-builder"></div>
                    <p id="update-constraints-error" style="display:none;color:var(--color-error,#e53e3e);font-size:0.8rem;margin-top:0.5rem"></p>
                </div>
                <div class="modal-footer">
                    <button class="btn btn-secondary" onclick="closeModal('update-constraints-modal')">Cancel</button>
                    <button class="btn btn-primary" id="update-constraints-save">Save Constraints</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);
    }

    // Mount the module builder with the VM's current constraints.
    const builderEl = modal.querySelector('#update-constraints-builder');
    _cbUpdateHandle?.destroy();
    import('./constraint-builder.js').then(({ mount }) => {
        mount(builderEl, {
            initial: currentConstraints,
            apiFetch: api,
        }).then(h => { _cbUpdateHandle = h; });
    });

    // Reset UI
    const errorEl = modal.querySelector('#update-constraints-error');
    errorEl.style.display = 'none';
    openStaticModal(modal);

    // Override the save button each time (re-capture vmId in closure).
    const saveBtn = modal.querySelector('#update-constraints-save');
    saveBtn.onclick = async () => {
        errorEl.style.display = 'none';

        const constraints = _cbUpdateHandle ? _cbUpdateHandle.getConstraints() : [];

        saveBtn.disabled = true;
        saveBtn.textContent = 'Saving…';

        try {
            const res = await api(`/api/vms/${vmId}/scheduling`, {
                method: 'PATCH',
                body: JSON.stringify({ constraints })
            });
            const data = await res.json();

            if (data.success) {
                showToast('Scheduling constraints updated', 'success');
                closeModal('update-constraints-modal');
                await refreshData();
            } else {
                errorEl.textContent = data.message || `Error ${res.status}`;
                errorEl.style.display = 'block';
            }
        } catch (err) {
            errorEl.textContent = `Network error: ${err.message}`;
            errorEl.style.display = 'block';
        } finally {
            saveBtn.disabled = false;
            saveBtn.textContent = 'Save Constraints';
        }
    };
}

window.openUpdateConstraintsModal = openUpdateConstraintsModal;

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


// ============================================
// SSH KEYS
// ============================================

function openAddSSHKeyModal() {
    openStaticModal(document.getElementById('add-ssh-key-modal'));
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
// SETTINGS
// ============================================

function saveSettings() {
    const orchestratorUrl = document.getElementById('settings-orchestrator-url').value.trim();

    if (orchestratorUrl && orchestratorUrl !== CONFIG.orchestratorUrl) {
        CONFIG.orchestratorUrl = orchestratorUrl;
        localStorage.setItem('orchestratorUrl', orchestratorUrl);
        initializeMarketplace(orchestratorUrl, escapeHtml);
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
window.updateTierInfo = updateTierInfo;
window.updateEstimatedCost = updateEstimatedCost;
window.onGpuModeChange = onGpuModeChange;
window.updateBandwidthInfo = updateBandwidthInfo;
window.updateReplicationInfo = updateReplicationInfo;
window.startVM = startVM;
window.restartVM = restartVM;
window.stopVM = stopVM;
window.forceStopVM = forceStopVM;
window.deleteVM = deleteVM;
window.copyToClipboard = copyToClipboard;
window.showPasswordModal = showPasswordModal;
window.revealPassword = revealPassword;
window.openAddSSHKeyModal = openAddSSHKeyModal;
window.addSSHKey = addSSHKey;
window.deleteSSHKey = deleteSSHKey;
window.showSshInstructions = showSshInstructions;
window.openTerminal = openTerminal;
window.openFileBrowser = openFileBrowser;
window.showConnectInfo = showConnectInfo;
window.showVmMessages = showVmMessages;
window.showVmLogs = showVmLogs;
window.showSSHConnectionModal = showSSHConnectionModal;
window.downloadSSHBundle = downloadSSHBundle;
window.downloadSSHConfig = downloadSSHConfig;
window.saveSettings = saveSettings;
window.refreshData = refreshData;
window.showToast = showToast;
window.ethersSigner = () => ethersSigner;
window.handleBalanceCardClick = handleBalanceCardClick;
window.loadUserBalance = loadUserBalance;
window.loadNodes = loadNodes;
window.searchNodes = searchNodes;
window.clearNodeFilters = clearNodeFilters;

// Custom-domain helpers — implemented in custom-domains.js but referenced
// from inline VM actions, so we re-expose them here.
window.openCustomDomainsModal = openCustomDomainsModal;
window.closeCustomDomainsModal = closeCustomDomainsModal;