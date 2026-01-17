// src/Orchestrator/wwwroot/src/payment.js
// Payment handling for DeCloud frontend

import { ethers } from 'ethers';

// ═══════════════════════════════════════════════════════════════════════════
// CONSTANTS
// ═══════════════════════════════════════════════════════════════════════════

// ERC20 ABI (minimal for approve + transfer)
const ERC20_ABI = [
    "function approve(address spender, uint256 amount) returns (bool)",
    "function allowance(address owner, address spender) view returns (uint256)",
    "function balanceOf(address account) view returns (uint256)",
    "function decimals() view returns (uint8)",
    "function symbol() view returns (string)"
];

// Escrow ABI (minimal for deposit)
const ESCROW_ABI = [
    "function deposit(uint256 amount)",
    "function userBalances(address) view returns (uint256)",
    "function withdrawBalance(uint256 amount)"
];

// ═══════════════════════════════════════════════════════════════════════════
// STATE
// ═══════════════════════════════════════════════════════════════════════════

let depositConfig = null;
let usdcContract = null;
let escrowContract = null;
let currentSigner = null;
let authToken = null;

// ═══════════════════════════════════════════════════════════════════════════
// INITIALIZATION
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Set the authentication token
 * @param {string} token - JWT auth token
 */
export function setAuthToken(token) {
    authToken = token;
}

/**
 * Initialize payment module
 * @param {ethers.Signer} signer - Ethers signer from connected wallet
 * @param {string} token - Optional auth token (can also use setAuthToken)
 */
export async function initializePayment(signer, token = null) {
    if (token) {
        authToken = token;
    }

    // Get token from localStorage if not provided
    if (!authToken) {
        authToken = localStorage.getItem('authToken');
    }

    if (!authToken) {
        throw new Error('No auth token available. Please authenticate first.');
    }

    currentSigner = signer;

    // Fetch deposit config from orchestrator
    const response = await fetch('/api/payment/deposit-info', {
        headers: {
            'Authorization': `Bearer ${authToken}`,
            'Content-Type': 'application/json'
        }
    });

    if (!response.ok) {
        const errorText = await response.text();
        console.error('[Payment] Failed to fetch deposit info:', response.status, errorText);
        throw new Error('Failed to fetch deposit info');
    }

    const result = await response.json();

    if (!result.success || !result.data) {
        throw new Error(result.message || 'Invalid deposit info response');
    }

    depositConfig = result.data;

    // Validate config
    if (!depositConfig.usdcTokenAddress || !depositConfig.escrowContractAddress) {
        throw new Error('Invalid deposit configuration: missing contract addresses');
    }

    // Initialize contracts
    usdcContract = new ethers.Contract(
        depositConfig.usdcTokenAddress,
        ERC20_ABI,
        signer
    );

    escrowContract = new ethers.Contract(
        depositConfig.escrowContractAddress,
        ESCROW_ABI,
        signer
    );

    console.log('[Payment] Initialized with config:', {
        chain: depositConfig.chainName,
        usdc: depositConfig.usdcTokenAddress,
        escrow: depositConfig.escrowContractAddress
    });

    return depositConfig;
}

/**
 * Check if payment module is initialized
 */
export function isInitialized() {
    return depositConfig !== null && usdcContract !== null && escrowContract !== null;
}

/**
 * Get current deposit configuration
 */
export function getDepositConfig() {
    return depositConfig;
}

// ═══════════════════════════════════════════════════════════════════════════
// DEPOSIT FLOW
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Deposit USDC to escrow contract
 * @param {string} amount - Amount in USDC (e.g., "10.50")
 * @param {function} onProgress - Progress callback
 * @returns {Promise<{txHash: string, blockNumber: number, amount: string}>}
 */
export async function depositUSDC(amount, onProgress = () => { }) {
    if (!isInitialized()) {
        throw new Error('Payment not initialized. Call initializePayment() first.');
    }

    // Validate amount
    const amountNum = parseFloat(amount);
    if (isNaN(amountNum) || amountNum <= 0) {
        throw new Error('Invalid amount');
    }

    const minDeposit = depositConfig.minDeposit || 1;
    if (amountNum < minDeposit) {
        throw new Error(`Minimum deposit is ${minDeposit} USDC`);
    }

    const decimals = await usdcContract.decimals();
    const amountWei = ethers.parseUnits(amount, decimals);

    onProgress({ step: 'checking', message: 'Checking USDC balance...' });

    // Check balance
    const signerAddress = await currentSigner.getAddress();
    const balance = await usdcContract.balanceOf(signerAddress);

    if (balance < amountWei) {
        const balanceFormatted = ethers.formatUnits(balance, decimals);
        throw new Error(`Insufficient USDC balance. Have: ${balanceFormatted}, Need: ${amount}`);
    }

    onProgress({ step: 'approving', message: 'Checking allowance...' });

    // Check allowance
    const allowance = await usdcContract.allowance(signerAddress, depositConfig.escrowContractAddress);

    if (allowance < amountWei) {
        onProgress({ step: 'approving', message: 'Approving USDC spend... Please confirm in wallet.' });

        // Approve exact amount
        const approveTx = await usdcContract.approve(
            depositConfig.escrowContractAddress,
            amountWei
        );

        onProgress({
            step: 'approving',
            message: 'Waiting for approval confirmation...',
            txHash: approveTx.hash
        });

        await approveTx.wait();

        onProgress({ step: 'approved', message: 'Approval confirmed!' });
    }

    onProgress({ step: 'depositing', message: 'Depositing USDC... Please confirm in wallet.' });

    // Deposit
    const depositTx = await escrowContract.deposit(amountWei);

    onProgress({
        step: 'confirming',
        message: 'Waiting for deposit confirmation...',
        txHash: depositTx.hash
    });

    const receipt = await depositTx.wait();

    onProgress({
        step: 'complete',
        message: 'Deposit successful!',
        txHash: depositTx.hash,
        blockNumber: receipt.blockNumber
    });

    // Notify backend to sync balance (fire and forget)
    syncDepositWithBackend(depositTx.hash).catch(err => {
        console.warn('[Payment] Backend sync failed (non-critical):', err);
    });

    return {
        txHash: depositTx.hash,
        blockNumber: receipt.blockNumber,
        amount: amount
    };
}

/**
 * Notify backend about the deposit so it can update platform balance
 */
async function syncDepositWithBackend(txHash) {
    if (!authToken) return;

    const response = await fetch('/api/payment/sync', {
        method: 'POST',
        headers: {
            'Authorization': `Bearer ${authToken}`,
            'Content-Type': 'application/json'
        },
        body: JSON.stringify({ txHash })
    });

    if (!response.ok) {
        console.warn('[Payment] Sync endpoint returned:', response.status);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// BALANCE QUERIES
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Get user's platform balance from backend
 * @returns {Promise<{balance: number, tokenSymbol: string, pendingDeposits: number}>}
 */
export async function getBalance() {
    if (!authToken) {
        authToken = localStorage.getItem('authToken');
    }

    if (!authToken) {
        throw new Error('Not authenticated');
    }

    const response = await fetch('/api/payment/balance', {
        headers: {
            'Authorization': `Bearer ${authToken}`,
            'Content-Type': 'application/json'
        }
    });

    if (!response.ok) {
        if (response.status === 401) {
            throw new Error('Authentication expired');
        }
        throw new Error('Failed to fetch balance');
    }

    const result = await response.json();

    if (!result.success) {
        throw new Error(result.message || 'Failed to get balance');
    }

    return result.data;
}

/**
 * Get on-chain escrow balance (directly from contract)
 * @param {string} walletAddress - Wallet address to check
 * @returns {Promise<string>} Balance formatted with 6 decimals
 */
export async function getOnChainBalance(walletAddress) {
    if (!escrowContract) {
        throw new Error('Payment not initialized');
    }

    const balance = await escrowContract.userBalances(walletAddress);
    return ethers.formatUnits(balance, 6); // USDC has 6 decimals
}

/**
 * Get wallet's USDC balance (not in escrow)
 * @param {string} walletAddress - Wallet address to check
 * @returns {Promise<string>} Balance formatted
 */
export async function getWalletUSDCBalance(walletAddress) {
    if (!usdcContract) {
        throw new Error('Payment not initialized');
    }

    const balance = await usdcContract.balanceOf(walletAddress);
    const decimals = await usdcContract.decimals();
    return ethers.formatUnits(balance, decimals);
}

// ═══════════════════════════════════════════════════════════════════════════
// UI HELPERS
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Show deposit modal
 */
export function showDepositModal() {
    let modal = document.getElementById('deposit-modal');
    if (!modal) {
        createDepositModal();
        modal = document.getElementById('deposit-modal');
    }

    // Reset state
    document.getElementById('deposit-amount').value = '';
    document.getElementById('deposit-progress').classList.add('hidden');
    document.getElementById('deposit-result').classList.add('hidden');
    document.getElementById('deposit-btn').disabled = false;

    modal.classList.add('active');
    //modal.classList.remove('hidden');
}

/**
 * Hide deposit modal
 */
export function hideDepositModal() {
    const modal = document.getElementById('deposit-modal');
    if (modal) {
        //modal.classList.add('hidden');
        modal.classList.remove('active');
    }
}

/**
 * Create deposit modal HTML
 */
function createDepositModal() {
    const chainName = depositConfig?.chainName || 'Polygon';
    const escrowAddr = depositConfig?.escrowContractAddress || '...';
    const minDeposit = depositConfig?.minDeposit || 1;
    const explorerUrl = depositConfig?.explorerUrl || 'https://polygonscan.com';

    const modal = document.createElement('div');
    modal.className = 'modal-overlay active';
    modal.id = 'deposit-modal';

    modal.innerHTML = `
        <div class="modal-content">
            <div class="modal-header">
                <h2>💰 Deposit USDC</h2>
                <button onclick="window.closeDepositModal()" class="close-btn">&times;</button>
            </div>
                
            <div class="modal-body">
                <div class="deposit-info">
                    <p>Network: <strong>${chainName}</strong></p>
                    <p>Contract: <code>${escrowAddr.slice(0, 10)}...${escrowAddr.slice(-8)}</code></p>
                    <p>Min Deposit: <strong>${minDeposit} USDC</strong></p>
                </div>
                    
                <div class="deposit-form">
                    <label for="deposit-amount">Amount (USDC)</label>
                    <input type="number" id="deposit-amount" min="${minDeposit}" step="0.01" placeholder="10.00">
                        
                    <button id="deposit-btn" onclick="window.handleDeposit()" class="btn-primary">
                        Deposit USDC
                    </button>
                </div>
                    
                <div id="deposit-progress" class="deposit-progress hidden">
                    <div class="progress-spinner"></div>
                    <p id="deposit-status">Processing...</p>
                </div>
                    
                <div id="deposit-result" class="deposit-result hidden">
                    <p class="success">✓ Deposit successful!</p>
                    <p>Transaction: <a id="deposit-tx-link" href="#" target="_blank" rel="noopener"></a></p>
                </div>
            </div>
        </div>
        `;
    document.body.appendChild(modal);
}

/**
 * Handle deposit button click
 */
window.handleDeposit = async function () {
    const amountInput = document.getElementById('deposit-amount');
    const amount = amountInput.value;
    const minDeposit = depositConfig?.minDeposit || 1;

    if (!amount || parseFloat(amount) < minDeposit) {
        alert(`Please enter a valid amount (minimum ${minDeposit} USDC)`);
        return;
    }

    const progressDiv = document.getElementById('deposit-progress');
    const resultDiv = document.getElementById('deposit-result');
    const statusP = document.getElementById('deposit-status');
    const depositBtn = document.getElementById('deposit-btn');

    try {
        depositBtn.disabled = true;
        progressDiv.classList.remove('hidden');
        resultDiv.classList.add('hidden');

        const result = await depositUSDC(amount, (progress) => {
            statusP.textContent = progress.message;

            // Show tx hash if available
            if (progress.txHash) {
                statusP.innerHTML = `${progress.message}<br><small>Tx: ${progress.txHash.slice(0, 16)}...</small>`;
            }
        });

        // Show success
        progressDiv.classList.add('hidden');
        resultDiv.classList.remove('hidden');

        const txLink = document.getElementById('deposit-tx-link');
        const explorerUrl = depositConfig?.explorerUrl || 'https://polygonscan.com';
        txLink.href = `${explorerUrl}/tx/${result.txHash}`;
        txLink.textContent = result.txHash.slice(0, 20) + '...';

        // Refresh balance display
        await refreshBalanceDisplay();

    } catch (error) {
        progressDiv.classList.remove('hidden');

        let errorMessage = error.message;
        if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            errorMessage = 'Transaction rejected by user';
        }

        statusP.innerHTML = `<span style="color: #ef4444;">❌ ${errorMessage}</span>`;
        console.error('[Payment] Deposit error:', error);
    } finally {
        depositBtn.disabled = false;
    }
};

/**
 * Close deposit modal
 */
window.closeDepositModal = function () {
    hideDepositModal();
};

/**
 * Refresh balance display in UI
 */
export async function refreshBalanceDisplay() {
    try {
        const balance = await getBalance();
        const balanceEl = document.getElementById('user-balance');
        if (balanceEl && balance) {
            balanceEl.textContent = `${balance.balance.toFixed(2)} ${balance.tokenSymbol}`;

            // Low balance warning
            if (balance.balance < 5) {
                balanceEl.classList.add('low-balance');
            } else {
                balanceEl.classList.remove('low-balance');
            }
        }
        return balance;
    } catch (error) {
        console.error('[Payment] Failed to refresh balance:', error);
        throw error;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EXPORTS
// ═══════════════════════════════════════════════════════════════════════════

export {
    depositConfig
};