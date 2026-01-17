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

/**
 * Check if wallet is on correct network and switch if needed
 * @returns {Promise<boolean>} True if on correct network or successfully switched
 */
export async function ensureCorrectNetwork() {
    if (!currentSigner) {
        throw new Error('Wallet not connected');
    }

    if (!depositConfig) {
        throw new Error('Payment not initialized');
    }

    const provider = currentSigner.provider;
    const network = await provider.getNetwork();
    const currentChainId = network.chainId.toString();
    const expectedChainId = depositConfig.chainId;

    console.log('[Payment] Current network:', currentChainId, 'Expected:', expectedChainId);

    if (currentChainId === expectedChainId) {
        console.log('[Payment] ✓ On correct network');
        return true;
    }

    // Wrong network - try to fix it automatically
    const networkName = depositConfig.chainName || `Chain ID ${expectedChainId}`;
    const currentNetworkName = getNetworkName(currentChainId);

    console.warn(`[Payment] Wrong network! Current: ${currentNetworkName}, Expected: ${networkName}`);

    try {
        // STEP 1: Try to switch to existing network
        const chainIdHex = '0x' + parseInt(expectedChainId).toString(16);

        await provider.send('wallet_switchEthereumChain', [
            { chainId: chainIdHex }
        ]);

        console.log('[Payment] ✓ Switched to correct network');
        return true;

    } catch (switchError) {
        console.log('[Payment] Switch failed:', switchError.code, switchError.message);

        // STEP 2: If network doesn't exist (error 4902), try to add it
        if (switchError.code === 4902) {
            console.log('[Payment] Network not in wallet, attempting to add...');

            try {
                await addNetworkToWallet(expectedChainId);
                console.log('[Payment] ✓ Network added and switched');
                return true;
            } catch (addError) {
                console.error('[Payment] Failed to add network:', addError);
                // Fall through to show manual instructions
            }
        } else if (switchError.code === 4001) {
            // User rejected the request
            throw new NetworkMismatchError(
                'user_rejected',
                currentChainId,
                expectedChainId,
                networkName
            );
        }

        // STEP 3: Auto-switch/add failed - need manual intervention
        throw new NetworkMismatchError(
            'switch_failed',
            currentChainId,
            expectedChainId,
            networkName
        );
    }
}

/**
 * Add network to wallet
 */
async function addNetworkToWallet(chainId) {
    const provider = currentSigner.provider;

    // Network configurations
    const networks = {
        '80002': {
            chainId: '0x13882',
            chainName: 'Polygon Amoy Testnet',
            nativeCurrency: {
                name: 'MATIC',
                symbol: 'MATIC',
                decimals: 18
            },
            rpcUrls: ['https://rpc-amoy.polygon.technology'],
            blockExplorerUrls: ['https://amoy.polygonscan.com']
        },
        '137': {
            chainId: '0x89',
            chainName: 'Polygon Mainnet',
            nativeCurrency: {
                name: 'MATIC',
                symbol: 'MATIC',
                decimals: 18
            },
            rpcUrls: ['https://polygon-rpc.com'],
            blockExplorerUrls: ['https://polygonscan.com']
        }
    };

    const networkConfig = networks[chainId];

    if (!networkConfig) {
        throw new Error('Unsupported network: ' + chainId);
    }

    await provider.send('wallet_addEthereumChain', [networkConfig]);
}

/**
 * Get human-readable network name
 */
function getNetworkName(chainId) {
    const names = {
        '1': 'Ethereum Mainnet',
        '137': 'Polygon Mainnet',
        '80002': 'Polygon Amoy Testnet',
        '11155111': 'Sepolia Testnet',
        '42161': 'Arbitrum One'
    };
    return names[chainId] || `Chain ID ${chainId}`;
}

/**
 * Custom error for network mismatch
 */
class NetworkMismatchError extends Error {
    constructor(reason, currentChainId, expectedChainId, networkName) {
        super(`Network mismatch: ${reason}`);
        this.name = 'NetworkMismatchError';
        this.reason = reason;
        this.currentChainId = currentChainId;
        this.expectedChainId = expectedChainId;
        this.networkName = networkName;
    }
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

    // Check and switch network if needed
    onProgress({ step: 'checking-network', message: 'Checking network...' });
    try {
        await ensureCorrectNetwork();
    } catch (error) {
        console.error('[Payment] Network check failed:', error);
        throw error;
    }

    // Get current gas prices
    async function getGasPrice() {
        try {
            const feeData = await currentSigner.provider.getFeeData();

            // If network returns valid gas prices, use them with a 20% buffer
            if (feeData.maxFeePerGas && feeData.maxPriorityFeePerGas) {
                return {
                    maxFeePerGas: (feeData.maxFeePerGas * 120n) / 100n,
                    maxPriorityFeePerGas: (feeData.maxPriorityFeePerGas * 120n) / 100n
                };
            }
        } catch (error) {
            console.warn('[Payment] Failed to fetch gas prices:', error);
        }

        // Fallback: Use safe defaults for Polygon Amoy
        return {
            maxFeePerGas: ethers.parseUnits('50', 'gwei'),
            maxPriorityFeePerGas: ethers.parseUnits('30', 'gwei')
        };
    }

    const gasPrice = await getGasPrice();
    console.log('[Payment] Using gas prices:', {
        maxFeePerGas: ethers.formatUnits(gasPrice.maxFeePerGas, 'gwei') + ' gwei',
        maxPriorityFeePerGas: ethers.formatUnits(gasPrice.maxPriorityFeePerGas, 'gwei') + ' gwei'
    });

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

        // Approve exact amount with explicit gas prices
        const approveTx = await usdcContract.approve(
            depositConfig.escrowContractAddress,
            amountWei,
            gasPrice
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
    const depositTx = await escrowContract.deposit(amountWei, gasPrice);

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
        resultDiv.classList.add('hidden');

        let errorMessage = error.message;

        // Special handling for network errors
        if (error.name === 'NetworkMismatchError') {
            const currentName = getNetworkName(error.currentChainId);
            const expectedName = error.networkName;

            // Get network details for instructions
            const networkDetails = getNetworkInstructions(error.expectedChainId);

            statusP.innerHTML = `
                <div style="color: #ef4444; text-align: left;">
                    <div style="display: flex; align-items: center; gap: 8px; margin-bottom: 12px;">
                        <span style="font-size: 24px;">🔄</span>
                        <strong style="font-size: 16px;">Wrong Network</strong>
                    </div>
                    
                    <div style="background: rgba(239, 68, 68, 0.1); border: 1px solid rgba(239, 68, 68, 0.3); border-radius: 8px; padding: 12px; margin-bottom: 12px;">
                        <p style="margin: 4px 0;"><strong>Current:</strong> ${currentName}</p>
                        <p style="margin: 4px 0;"><strong>Required:</strong> ${expectedName}</p>
                    </div>
                    
                    <p style="font-size: 14px; margin-bottom: 12px;">
                        Please switch to <strong>${expectedName}</strong> in your wallet.
                    </p>
                    
                    <button 
                        onclick="document.getElementById('network-instructions').classList.toggle('hidden')"
                        style="
                            background: none;
                            border: 1px solid #ef4444;
                            color: #ef4444;
                            padding: 8px 16px;
                            border-radius: 6px;
                            cursor: pointer;
                            font-size: 13px;
                            font-weight: 500;
                            transition: all 0.2s;
                        "
                        onmouseover="this.style.background='rgba(239, 68, 68, 0.1)'"
                        onmouseout="this.style.background='none'"
                    >
                        📖 Show me how
                    </button>
                    
                    <div id="network-instructions" class="hidden" style="margin-top: 16px; padding-top: 16px; border-top: 1px solid rgba(239, 68, 68, 0.3);">
                        <p style="font-size: 14px; font-weight: 600; margin-bottom: 12px;">Manual Setup Instructions:</p>
                        
                        <ol style="font-size: 13px; line-height: 1.8; padding-left: 20px; margin: 0;">
                            <li>Open your wallet (MetaMask, Rainbow, etc.)</li>
                            <li>Click on the <strong>network selector</strong> at the top</li>
                            <li>Click <strong>"Add Network"</strong> or <strong>"Custom RPC"</strong></li>
                            <li>Enter these details:
                                <div style="background: #1a1a2e; border-radius: 6px; padding: 12px; margin: 8px 0; font-family: 'JetBrains Mono', monospace; font-size: 12px;">
                                    <div style="margin: 4px 0;"><strong>Network Name:</strong> ${networkDetails.name}</div>
                                    <div style="margin: 4px 0;"><strong>RPC URL:</strong> ${networkDetails.rpc}</div>
                                    <div style="margin: 4px 0;"><strong>Chain ID:</strong> ${networkDetails.chainId}</div>
                                    <div style="margin: 4px 0;"><strong>Currency:</strong> ${networkDetails.currency}</div>
                                    <div style="margin: 4px 0;"><strong>Explorer:</strong> ${networkDetails.explorer}</div>
                                </div>
                            </li>
                            <li>Click <strong>"Save"</strong> or <strong>"Add"</strong></li>
                            <li>Switch to <strong>${expectedName}</strong></li>
                            <li>Try depositing again</li>
                        </ol>
                    </div>
                </div>
            `;
        } else if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            errorMessage = 'Transaction rejected by user';
            statusP.innerHTML = `<span style="color: #ef4444;">❌ ${errorMessage}</span>`;
        } else {
            statusP.innerHTML = `<span style="color: #ef4444;">❌ ${errorMessage}</span>`;
        }

        console.error('[Payment] Deposit error:', error);
    } finally {
        depositBtn.disabled = false;
    }
};

/**
 * Get network setup instructions
 */
function getNetworkInstructions(chainId) {
    const instructions = {
        '80002': {
            name: 'Polygon Amoy Testnet',
            rpc: 'https://rpc-amoy.polygon.technology',
            chainId: '80002',
            currency: 'MATIC',
            explorer: 'https://amoy.polygonscan.com'
        },
        '137': {
            name: 'Polygon Mainnet',
            rpc: 'https://polygon-rpc.com',
            chainId: '137',
            currency: 'MATIC',
            explorer: 'https://polygonscan.com'
        }
    };

    return instructions[chainId] || {
        name: 'Unknown Network',
        rpc: 'N/A',
        chainId: chainId,
        currency: 'N/A',
        explorer: 'N/A'
    };
}

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