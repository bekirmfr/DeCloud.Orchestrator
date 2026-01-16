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
    "function decimals() view returns (uint8)"
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

// ═══════════════════════════════════════════════════════════════════════════
// INITIALIZATION
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Initialize payment module
 */
export async function initializePayment(signer) {
    // Fetch deposit config from orchestrator
    const response = await fetch('/api/payment/deposit-info', {
        headers: {
            'Authorization': `Bearer ${authToken}`
        }
    });
    
    if (!response.ok) {
        throw new Error('Failed to fetch deposit info');
    }
    
    const result = await response.json();
    depositConfig = result.data;
    
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
    
    console.log('[Payment] Initialized with config:', depositConfig);
}

// ═══════════════════════════════════════════════════════════════════════════
// DEPOSIT FLOW
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Deposit USDC to escrow contract
 * @param {string} amount - Amount in USDC (e.g., "10.50")
 * @param {function} onProgress - Progress callback
 */
export async function depositUSDC(amount, onProgress = () => {}) {
    if (!depositConfig || !usdcContract || !escrowContract) {
        throw new Error('Payment not initialized');
    }
    
    const decimals = await usdcContract.decimals();
    const amountWei = ethers.parseUnits(amount, decimals);
    
    onProgress({ step: 'checking', message: 'Checking USDC balance...' });
    
    // Check balance
    const signer = await usdcContract.runner.getAddress();
    const balance = await usdcContract.balanceOf(signer);
    
    if (balance < amountWei) {
        throw new Error(`Insufficient USDC balance. Have: ${ethers.formatUnits(balance, decimals)}, Need: ${amount}`);
    }
    
    onProgress({ step: 'approving', message: 'Approving USDC spend...' });
    
    // Check allowance
    const allowance = await usdcContract.allowance(signer, depositConfig.escrowContractAddress);
    
    if (allowance < amountWei) {
        // Approve exact amount (or could approve max for convenience)
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
    }
    
    onProgress({ step: 'depositing', message: 'Depositing USDC to escrow...' });
    
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
    
    return {
        txHash: depositTx.hash,
        blockNumber: receipt.blockNumber,
        amount: amount
    };
}

/**
 * Get user's balance info
 */
export async function getBalance() {
    const response = await fetch('/api/payment/balance', {
        headers: {
            'Authorization': `Bearer ${authToken}`
        }
    });
    
    if (!response.ok) {
        throw new Error('Failed to fetch balance');
    }
    
    const result = await response.json();
    return result.data;
}

/**
 * Get on-chain escrow balance
 */
export async function getOnChainBalance(walletAddress) {
    if (!escrowContract) {
        throw new Error('Payment not initialized');
    }
    
    const balance = await escrowContract.userBalances(walletAddress);
    return ethers.formatUnits(balance, 6); // USDC has 6 decimals
}

// ═══════════════════════════════════════════════════════════════════════════
// UI HELPERS
// ═══════════════════════════════════════════════════════════════════════════

/**
 * Show deposit modal
 */
export function showDepositModal() {
    const modal = document.getElementById('deposit-modal');
    if (!modal) {
        createDepositModal();
    }
    document.getElementById('deposit-modal').classList.remove('hidden');
}

/**
 * Create deposit modal HTML
 */
function createDepositModal() {
    const modalHtml = `
        <div id="deposit-modal" class="modal-overlay hidden">
            <div class="modal-content">
                <div class="modal-header">
                    <h2>Deposit USDC</h2>
                    <button onclick="closeDepositModal()" class="close-btn">&times;</button>
                </div>
                
                <div class="modal-body">
                    <div class="deposit-info">
                        <p>Network: <strong>${depositConfig?.chainName || 'Polygon Amoy'}</strong></p>
                        <p>Contract: <code>${depositConfig?.escrowContractAddress?.slice(0, 10)}...</code></p>
                        <p>Min Deposit: <strong>${depositConfig?.minDeposit || 1} USDC</strong></p>
                    </div>
                    
                    <div class="deposit-form">
                        <label for="deposit-amount">Amount (USDC)</label>
                        <input type="number" id="deposit-amount" min="1" step="0.01" placeholder="10.00">
                        
                        <button id="deposit-btn" onclick="handleDeposit()" class="btn-primary">
                            Deposit
                        </button>
                    </div>
                    
                    <div id="deposit-progress" class="deposit-progress hidden">
                        <div class="progress-spinner"></div>
                        <p id="deposit-status"></p>
                    </div>
                    
                    <div id="deposit-result" class="deposit-result hidden">
                        <p class="success">✓ Deposit successful!</p>
                        <p>Transaction: <a id="deposit-tx-link" href="#" target="_blank"></a></p>
                    </div>
                </div>
            </div>
        </div>
    `;
    
    document.body.insertAdjacentHTML('beforeend', modalHtml);
}

/**
 * Handle deposit button click
 */
window.handleDeposit = async function() {
    const amount = document.getElementById('deposit-amount').value;
    
    if (!amount || parseFloat(amount) < 1) {
        alert('Please enter a valid amount (minimum 1 USDC)');
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
        });
        
        // Show success
        progressDiv.classList.add('hidden');
        resultDiv.classList.remove('hidden');
        
        const txLink = document.getElementById('deposit-tx-link');
        txLink.href = `https://amoy.polygonscan.com/tx/${result.txHash}`;
        txLink.textContent = result.txHash.slice(0, 16) + '...';
        
        // Refresh balance display
        refreshBalanceDisplay();
        
    } catch (error) {
        statusP.textContent = `Error: ${error.message}`;
        console.error('Deposit error:', error);
    } finally {
        depositBtn.disabled = false;
    }
};

/**
 * Close deposit modal
 */
window.closeDepositModal = function() {
    document.getElementById('deposit-modal').classList.add('hidden');
};

/**
 * Refresh balance display in UI
 */
async function refreshBalanceDisplay() {
    try {
        const balance = await getBalance();
        const balanceEl = document.getElementById('user-balance');
        if (balanceEl) {
            balanceEl.textContent = `${balance.balance.toFixed(2)} ${balance.tokenSymbol}`;
        }
    } catch (error) {
        console.error('Failed to refresh balance:', error);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EXPORTS
// ═══════════════════════════════════════════════════════════════════════════

export {
    depositConfig,
    refreshBalanceDisplay
};
