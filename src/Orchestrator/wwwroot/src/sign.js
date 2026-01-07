<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>DeCloud Node Authorization</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }

        body {
            font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Oxygen, Ubuntu, Cantarell, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            display: flex;
            align-items: center;
            justify-content: center;
            padding: 20px;
        }

        .container {
            background: white;
            border-radius: 20px;
            box-shadow: 0 20px 60px rgba(0,0,0,0.3);
            max-width: 600px;
            width: 100%;
            padding: 40px;
        }

        .logo {
            text-align: center;
            margin-bottom: 30px;
        }

        .logo h1 {
            color: #667eea;
            font-size: 32px;
            margin-bottom: 10px;
        }

        .logo p {
            color: #666;
            font-size: 14px;
        }

        .status {
            background: #f8f9fa;
            border-radius: 10px;
            padding: 20px;
            margin-bottom: 20px;
        }

        .status-item {
            display: flex;
            justify-content: space-between;
            padding: 10px 0;
            border-bottom: 1px solid #e0e0e0;
        }

        .status-item:last-child {
            border-bottom: none;
        }

        .status-label {
            color: #666;
            font-weight: 500;
        }

        .status-value {
            color: #333;
            font-weight: 600;
            word-break: break-all;
            text-align: right;
            max-width: 60%;
        }

        .message-box {
            background: #f8f9fa;
            border-radius: 10px;
            padding: 20px;
            margin: 20px 0;
            font-family: 'Courier New', monospace;
            font-size: 12px;
            color: #333;
            max-height: 200px;
            overflow-y: auto;
            white-space: pre-wrap;
            word-break: break-word;
            min-height: 60px;
        }

        .button {
            width: 100%;
            padding: 16px;
            border: none;
            border-radius: 10px;
            font-size: 16px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.3s;
            margin: 10px 0;
        }

        .button-primary {
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
        }

        .button-primary:hover {
            transform: translateY(-2px);
            box-shadow: 0 10px 20px rgba(102, 126, 234, 0.4);
        }

        .button-primary:disabled {
            background: #ccc;
            cursor: not-allowed;
            transform: none;
        }

        .button-secondary {
            background: white;
            color: #667eea;
            border: 2px solid #667eea;
        }

        .button-secondary:hover {
            background: #667eea;
            color: white;
        }

        .signature-result {
            background: #e8f5e9;
            border: 2px solid #4caf50;
            border-radius: 10px;
            padding: 20px;
            margin: 20px 0;
        }

        .signature-result h3 {
            color: #2e7d32;
            margin-bottom: 10px;
        }

        .signature-text {
            background: white;
            padding: 15px;
            border-radius: 5px;
            font-family: 'Courier New', monospace;
            font-size: 12px;
            word-break: break-all;
            margin: 10px 0;
        }

        .warning {
            background: #fff3cd;
            border-left: 4px solid #ffc107;
            padding: 15px;
            margin: 20px 0;
            border-radius: 5px;
        }

        .warning h4 {
            color: #856404;
            margin-bottom: 5px;
        }

        .warning p {
            color: #856404;
            font-size: 14px;
        }

        .spinner {
            border: 3px solid #f3f3f3;
            border-top: 3px solid #667eea;
            border-radius: 50%;
            width: 40px;
            height: 40px;
            animation: spin 1s linear infinite;
            margin: 20px auto;
        }

        @keyframes spin {
            0% { transform: rotate(0deg); }
            100% { transform: rotate(360deg); }
        }

        .error {
            background: #ffebee;
            border-left: 4px solid #f44336;
            padding: 15px;
            margin: 20px 0;
            border-radius: 5px;
            color: #c62828;
        }

        .success {
            text-align: center;
            color: #4caf50;
            font-size: 48px;
            margin: 20px 0;
        }

        .instructions {
            background: #e3f2fd;
            border-left: 4px solid #2196f3;
            padding: 15px;
            margin: 20px 0;
            border-radius: 5px;
        }

        .instructions h4 {
            color: #1565c0;
            margin-bottom: 10px;
        }

        .instructions ol {
            margin-left: 20px;
            color: #1565c0;
        }

        .instructions li {
            margin: 5px 0;
        }

        .wallet-options {
            display: grid;
            gap: 12px;
            margin: 20px 0;
        }

        .wallet-button {
            display: flex;
            align-items: center;
            gap: 12px;
            padding: 16px;
            border: 2px solid #e5e7eb;
            border-radius: 10px;
            background: white;
            cursor: pointer;
            transition: all 0.3s;
            font-size: 16px;
            font-weight: 600;
            color: #1f2937;
            text-align: left;
        }

        .wallet-button:hover {
            border-color: #667eea;
            background: #f9fafb;
            transform: translateX(4px);
        }

        .wallet-button.featured {
            border-color: #3b82f6;
            background: linear-gradient(135deg, #f0f9ff 0%, #e0f2fe 100%);
        }

        .wallet-button.featured:hover {
            border-color: #2563eb;
            background: linear-gradient(135deg, #dbeafe 0%, #bfdbfe 100%);
        }

        .wallet-button .wallet-icon {
            width: 32px;
            height: 32px;
            flex-shrink: 0;
            display: block;
            object-fit: contain;
        }

        .wallet-button .wallet-info {
            flex: 1;
            min-width: 0;
        }

        .wallet-button .wallet-name {
            display: block;
            font-weight: 600;
            font-size: 16px;
            color: #1f2937;
            line-height: 1.3;
        }

        .wallet-button .wallet-desc {
            display: block;
            font-size: 12px;
            color: #6b7280;
            font-weight: 400;
            margin-top: 2px;
            line-height: 1.2;
        }

        .wallet-button.featured .wallet-desc {
            color: #2563eb;
        }

        .connected-wallet-info {
            background: #f0fdf4;
            border: 2px solid #10b981;
            border-radius: 10px;
            padding: 15px;
            margin: 20px 0;
        }

        .connected-wallet-label {
            color: #065f46;
            font-size: 12px;
            font-weight: 600;
            margin-bottom: 5px;
        }

        .connected-wallet-value {
            color: #059669;
            font-family: 'Courier New', monospace;
            font-size: 14px;
            word-break: break-all;
        }

        .no-wallet-detected {
            text-align: center;
            padding: 30px 20px;
            color: #666;
        }

        .no-wallet-detected p {
            margin-bottom: 15px;
            font-size: 16px;
        }

        .no-wallet-detected .install-links {
            margin-top: 20px;
            display: flex;
            gap: 10px;
            justify-content: center;
            flex-wrap: wrap;
        }

        .no-wallet-detected .install-link {
            padding: 12px 24px;
            background: #667eea;
            color: white;
            text-decoration: none;
            border-radius: 8px;
            font-size: 14px;
            font-weight: 600;
            transition: all 0.3s;
        }

        .no-wallet-detected .install-link:hover {
            background: #5568d3;
            transform: translateY(-2px);
        }

        @media (max-width: 640px) {
            .container {
                padding: 20px;
            }

            .logo h1 {
                font-size: 24px;
            }

            .status-item {
                flex-direction: column;
                gap: 5px;
            }

            .status-value {
                text-align: left;
                max-width: 100%;
            }

            .wallet-button {
                gap: 10px;
                padding: 14px;
            }

            .wallet-button .wallet-icon {
                width: 28px;
                height: 28px;
            }
        }
    </style>
</head>
<body>
    <div class="container">
        <div class="logo">
            <h1>🔐 DeCloud Node Authorization</h1>
            <p>Secure Wallet Authentication</p>
        </div>

        <div id="loading" style="display: none;">
            <div class="spinner"></div>
            <p style="text-align: center; color: #666; margin-top: 10px;">Processing...</p>
        </div>

        <div id="error" class="error" style="display: none;"></div>

        <!-- Node Information -->
        <div class="status">
            <div class="status-item">
                <span class="status-label">Node ID:</span>
                <span class="status-value" id="nodeId">-</span>
            </div>
            <div class="status-item">
                <span class="status-label">Expected Wallet:</span>
                <span class="status-value" id="expectedWallet">-</span>
            </div>
            <div class="status-item">
                <span class="status-label">Hardware:</span>
                <span class="status-value" id="hardware">-</span>
            </div>
        </div>

        <!-- Message to Sign -->
        <div class="warning">
            <h4>⚠️ Message to Sign</h4>
            <p>Verify this message before signing:</p>
        </div>
        <div class="message-box" id="messageText"></div>

        <!-- Wallet Selection -->
        <div id="walletSelection" class="wallet-options"></div>

        <!-- Connected Wallet Info -->
        <div id="walletConnected" style="display: none;">
            <div class="connected-wallet-info">
                <div class="connected-wallet-label">Connected Wallet</div>
                <div class="connected-wallet-value" id="connectedWallet">-</div>
            </div>

            <button id="signButton" class="button button-primary" onclick="signMessage()">
                Sign Message
            </button>
        </div>

        <!-- Signature Result -->
        <div id="signatureResult" style="display: none;">
            <div class="success">✅</div>
            <div class="signature-result">
                <h3>Signature Generated Successfully!</h3>
                <p style="margin: 10px 0;">Copy this signature and paste it into your terminal:</p>
                <div class="signature-text" id="signatureText"></div>
                <button class="button button-secondary" onclick="copySignature()">
                    Copy to Clipboard
                </button>
            </div>

            <div class="instructions">
                <h4>Next Steps:</h4>
                <ol>
                    <li>Copy the signature above</li>
                    <li>Go back to your terminal</li>
                    <li>Paste it where it says "Signature (0x...):"</li>
                    <li>Press Enter</li>
                </ol>
            </div>
        </div>
    </div>

    <!-- Ethers.js from jsdelivr CDN -->
    <script type="module">
        import { BrowserProvider } from 'https://cdn.jsdelivr.net/npm/ethers@6.7.0/dist/ethers.min.js';
        import EthereumProvider from 'https://cdn.jsdelivr.net/npm/@walletconnect/ethereum-provider@2.17.2/+esm';

        // Configuration
        const WALLETCONNECT_PROJECT_ID = '708cede4d366aa77aead71dbc67d8ae5';

        // Parse URL parameters
        const params = new URLSearchParams(window.location.search);
        const messageB64 = params.get('message');
        const nodeId = params.get('nodeId');
        const expectedWallet = params.get('wallet');
        const hardware = params.get('hardware');

        let message = '';
        if (messageB64) {
            try {
                message = atob(messageB64);
            } catch (e) {
                console.error('Failed to decode message:', e);
                message = '[Failed to decode message]';
            }
        }

        // Display info
        document.getElementById('nodeId').textContent = nodeId || 'Unknown';
        document.getElementById('expectedWallet').textContent = expectedWallet || 'Any';
        document.getElementById('hardware').textContent = hardware || 'Unknown';
        document.getElementById('messageText').textContent = message || '[No message provided]';

        // State
        let provider = null;
        let signer = null;
        let connectedAddress = null;
        let wcProvider = null;

        // Detect available wallets and render options
        function renderWalletOptions() {
            const container = document.getElementById('walletSelection');
            const isMobile = /mobile/i.test(navigator.userAgent);
            const hasEthereum = typeof window.ethereum !== 'undefined';

            const wallets = [];

            // WalletConnect - ALWAYS show this first (featured)
            wallets.push({
                id: 'walletconnect',
                name: 'WalletConnect',
                desc: 'Scan QR with mobile wallet (350+ supported)',
                icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Cpath d="M7.5 11c3.8-3.7 10-3.7 13.8 0l.5.4a.5.5 0 0 1 0 .7l-1.6 1.6a.2.2 0 0 1-.4 0l-.6-.6c-2.7-2.6-7-2.6-9.6 0l-.7.6a.2.2 0 0 1-.4 0l-1.5-1.6a.5.5 0 0 1 0-.7l.5-.4zm17 3.3l1.4 1.4a.5.5 0 0 1 0 .7l-6.3 6.2a.5.5 0 0 1-.7 0l-4.5-4.4a.1.1 0 0 0-.2 0l-4.4 4.4a.5.5 0 0 1-.7 0l-6.3-6.2a.5.5 0 0 1 0-.7l1.4-1.4a.5.5 0 0 1 .7 0l4.5 4.4a.1.1 0 0 0 .2 0l4.4-4.4a.5.5 0 0 1 .7 0l4.5 4.4a.1.1 0 0 0 .2 0l4.4-4.4a.5.5 0 0 1 .7 0z" fill="%233B99FC"/%3E%3C/svg%3E',
                action: connectWalletConnect,
                featured: true
            });

            // MetaMask
            if (hasEthereum && window.ethereum.isMetaMask) {
                wallets.push({
                    id: 'metamask',
                    name: 'MetaMask',
                    desc: 'Browser extension',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Cpath d="M29.5 10.2l-12-8.5-12 8.5L7 14.3l5-1.8 5.5 4 5.5-4 5 1.8z" fill="%23E2761B"/%3E%3Cpath d="M5.5 10.2l2.5 4.1-2.5 1.5v6l7-1 .5-7.5-5.5-3.1z" fill="%23E4761B"/%3E%3Cpath d="M26.5 10.2l-2.5 4.1 2.5 1.5v6l-7-1-.5-7.5 5.5-3.1z" fill="%23E4761B"/%3E%3C/svg%3E',
                    action: connectMetaMask,
                    featured: false
                });
            } else if (isMobile) {
                wallets.push({
                    id: 'metamask',
                    name: 'MetaMask',
                    desc: 'Open in MetaMask app',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Cpath d="M29.5 10.2l-12-8.5-12 8.5L7 14.3l5-1.8 5.5 4 5.5-4 5 1.8z" fill="%23E2761B"/%3E%3C/svg%3E',
                    action: () => openMobileWallet('metamask'),
                    featured: false
                });
            }

            // Coinbase Wallet
            if (hasEthereum && window.ethereum.isCoinbaseWallet) {
                wallets.push({
                    id: 'coinbase',
                    name: 'Coinbase Wallet',
                    desc: 'Browser extension',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Crect width="32" height="32" rx="16" fill="%230052FF"/%3E%3Crect x="10" y="10" width="12" height="12" rx="2" fill="white"/%3E%3C/svg%3E',
                    action: connectCoinbase,
                    featured: false
                });
            } else if (isMobile) {
                wallets.push({
                    id: 'coinbase',
                    name: 'Coinbase Wallet',
                    desc: 'Open in Coinbase app',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Crect width="32" height="32" rx="16" fill="%230052FF"/%3E%3Crect x="10" y="10" width="12" height="12" rx="2" fill="white"/%3E%3C/svg%3E',
                    action: () => openMobileWallet('coinbase'),
                    featured: false
                });
            }

            // Generic wallet (if ethereum available but not identified)
            if (hasEthereum && !window.ethereum.isMetaMask && !window.ethereum.isCoinbaseWallet) {
                wallets.push({
                    id: 'browser',
                    name: 'Browser Wallet',
                    desc: 'Connect detected wallet',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Crect width="32" height="32" rx="8" fill="%2310b981"/%3E%3Cpath d="M21 12V7a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v10a2 2 0 0 0 2 2h7" stroke="white" stroke-width="2" fill="none"/%3E%3C/svg%3E',
                    action: connectBrowserWallet,
                    featured: false
                });
            }

            // Mobile-specific wallets
            if (isMobile) {
                wallets.push({
                    id: 'trust',
                    name: 'Trust Wallet',
                    desc: 'Open in Trust Wallet app',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Ccircle cx="16" cy="16" r="16" fill="%233375BB"/%3E%3Cpath d="M16 8l-6 4v6c0 3.5 2.5 6.5 6 7 3.5-.5 6-3.5 6-7v-6l-6-4z" fill="white"/%3E%3C/svg%3E',
                    action: () => openMobileWallet('trust'),
                    featured: false
                });

                wallets.push({
                    id: 'rainbow',
                    name: 'Rainbow',
                    desc: 'Open in Rainbow app',
                    icon: 'data:image/svg+xml,%3Csvg width="32" height="32" viewBox="0 0 32 32" xmlns="http://www.w3.org/2000/svg"%3E%3Cdefs%3E%3ClinearGradient id="r" x1="0" y1="0" x2="1" y2="1"%3E%3Cstop offset="0%25" stop-color="%23FF4D4D"/%3E%3Cstop offset="50%25" stop-color="%23FFD700"/%3E%3Cstop offset="100%25" stop-color="%234D79FF"/%3E%3C/linearGradient%3E%3C/defs%3E%3Crect width="32" height="32" rx="8" fill="url(%23r)"/%3E%3C/svg%3E',
                    action: () => openMobileWallet('rainbow'),
                    featured: false
                });
            }

            // Render wallet buttons
            container.innerHTML = wallets.map(wallet => `
                <button class="wallet-button ${wallet.featured ? 'featured' : ''}" onclick="window.walletActions['${wallet.id}']()">
                    <img class="wallet-icon" src="${wallet.icon}">
                    <div class="wallet-info">
                        <span class="wallet-name">${wallet.name}</span>
                        <span class="wallet-desc">${wallet.desc}</span>
                    </div>
                </button>
            `).join('');

            // Store actions
            window.walletActions = {};
            wallets.forEach(wallet => {
                window.walletActions[wallet.id] = wallet.action;
            });
        }

        // Connect WalletConnect
        async function connectWalletConnect() {
            try {
                showLoading(true);
                hideError();

                console.log('[WalletConnect] Initializing...');

                // Initialize WalletConnect Ethereum Provider
                wcProvider = await EthereumProvider.init({
                    projectId: WALLETCONNECT_PROJECT_ID,
                    chains: [1], // Ethereum Mainnet
                    optionalChains: [137, 42161], // Polygon, Arbitrum
                    showQrModal: true,
                    qrModalOptions: {
                        themeMode: 'light',
                        themeVariables: {
                            '--wcm-z-index': '10000'
                        }
                    },
                    metadata: {
                        name: 'DeCloud Node Authorization',
                        description: 'Sign message to authorize your DeCloud node',
                        url: window.location.origin,
                        icons: []
                    }
                });

                console.log('[WalletConnect] Provider initialized');

                // Enable session (shows QR code modal)
                await wcProvider.enable();

                console.log('[WalletConnect] Session enabled');

                // Get accounts
                const accounts = wcProvider.accounts;
                if (!accounts || accounts.length === 0) {
                    throw new Error('No accounts found');
                }

                // Setup ethers provider
                provider = new BrowserProvider(wcProvider);
                signer = await provider.getSigner();
                connectedAddress = await signer.getAddress();

                console.log('[WalletConnect] Address:', connectedAddress);

                showWalletConnected(connectedAddress);

                // Listen for disconnect
                wcProvider.on('disconnect', () => {
                    console.log('[WalletConnect] Disconnected');
                    location.reload();
                });

                wcProvider.on('accountsChanged', (accounts) => {
                    console.log('[WalletConnect] Accounts changed');
                    location.reload();
                });

            } catch (error) {
                console.error('WalletConnect error:', error);
                
                if (error.message && error.message.includes('User rejected')) {
                    showError('Connection cancelled by user');
                } else if (error.message && error.message.includes('User closed modal')) {
                    showError('QR modal closed - please try again');
                } else {
                    showError('Failed to connect via WalletConnect: ' + error.message);
                }
                
                showLoading(false);
            }
        }

        // Connect MetaMask
        async function connectMetaMask() {
            try {
                showLoading(true);
                hideError();

                const accounts = await window.ethereum.request({
                    method: 'eth_requestAccounts'
                });

                if (accounts.length === 0) {
                    throw new Error('No accounts found');
                }

                provider = new BrowserProvider(window.ethereum);
                signer = await provider.getSigner();
                connectedAddress = await signer.getAddress();

                showWalletConnected(connectedAddress);

            } catch (error) {
                console.error('MetaMask error:', error);
                if (error.code === 4001) {
                    showError('Connection rejected by user');
                } else {
                    showError('Failed to connect: ' + error.message);
                }
                showLoading(false);
            }
        }

        // Connect Coinbase
        async function connectCoinbase() {
            try {
                showLoading(true);
                hideError();

                const accounts = await window.ethereum.request({
                    method: 'eth_requestAccounts'
                });

                if (accounts.length === 0) {
                    throw new Error('No accounts found');
                }

                provider = new BrowserProvider(window.ethereum);
                signer = await provider.getSigner();
                connectedAddress = await signer.getAddress();

                showWalletConnected(connectedAddress);

            } catch (error) {
                console.error('Coinbase error:', error);
                showError('Failed to connect: ' + error.message);
                showLoading(false);
            }
        }

        // Connect generic browser wallet
        async function connectBrowserWallet() {
            try {
                showLoading(true);
                hideError();

                const accounts = await window.ethereum.request({
                    method: 'eth_requestAccounts'
                });

                if (accounts.length === 0) {
                    throw new Error('No accounts found');
                }

                provider = new BrowserProvider(window.ethereum);
                signer = await provider.getSigner();
                connectedAddress = await signer.getAddress();

                showWalletConnected(connectedAddress);

            } catch (error) {
                console.error('Browser wallet error:', error);
                showError('Failed to connect: ' + error.message);
                showLoading(false);
            }
        }

        // Open mobile wallet via deep link
        function openMobileWallet(walletType) {
            const currentUrl = window.location.href;
            const encodedUrl = encodeURIComponent(currentUrl);

            const deepLinks = {
                metamask: `https://metamask.app.link/dapp/${currentUrl.replace(/^https?:\/\//, '')}`,
                coinbase: `https://go.cb-w.com/dapp?cb_url=${encodedUrl}`,
                trust: `https://link.trustwallet.com/open_url?coin_id=60&url=${encodedUrl}`,
                rainbow: `https://rnbwapp.com/wc?uri=${encodedUrl}`
            };

            const deepLink = deepLinks[walletType];
            if (deepLink) {
                window.location.href = deepLink;
            }
        }

        // Sign message
        window.signMessage = async function() {
            try {
                if (!signer) {
                    throw new Error('Wallet not connected');
                }

                if (!message) {
                    throw new Error('No message to sign');
                }

                showLoading(true);
                hideError();

                const signature = await signer.signMessage(message);
                console.log('[Sign] Signature:', signature);

                document.getElementById('signatureText').textContent = signature;
                document.getElementById('signatureResult').style.display = 'block';
                document.getElementById('walletConnected').style.display = 'none';
                showLoading(false);

                try {
                    localStorage.setItem('lastSignature', signature);
                    localStorage.setItem('lastSignatureTime', new Date().toISOString());
                } catch (e) {}

            } catch (error) {
                console.error('Signing error:', error);
                
                if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
                    showError('Signature rejected by user');
                } else {
                    showError('Failed to sign: ' + error.message);
                }
                
                showLoading(false);
            }
        };

        // Copy signature
        window.copySignature = function() {
            const signature = document.getElementById('signatureText').textContent;
            
            if (navigator.clipboard && window.isSecureContext) {
                navigator.clipboard.writeText(signature).then(() => {
                    updateButtonText('✅ Copied!');
                }).catch(() => {
                    fallbackCopy(signature);
                });
            } else {
                fallbackCopy(signature);
            }
        };

        function fallbackCopy(text) {
            const textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.style.position = 'fixed';
            textarea.style.left = '-999999px';
            document.body.appendChild(textarea);
            textarea.select();
            
            try {
                document.execCommand('copy');
                updateButtonText('✅ Copied!');
            } catch (err) {
                showError('Failed to copy. Please select and copy manually.');
            }
            
            document.body.removeChild(textarea);
        }

        function updateButtonText(text) {
            const btn = event.target;
            const originalText = btn.textContent;
            btn.textContent = text;
            setTimeout(() => {
                btn.textContent = originalText;
            }, 2000);
        }

        // Helper functions
        function showWalletConnected(address) {
            document.getElementById('connectedWallet').textContent = address;
            document.getElementById('walletSelection').style.display = 'none';
            document.getElementById('walletConnected').style.display = 'block';

            if (expectedWallet && expectedWallet.toLowerCase() !== address.toLowerCase()) {
                showError(
                    `⚠️ Warning: Connected wallet (${address}) doesn't match expected (${expectedWallet})`,
                    false
                );
            }
        }

        function showLoading(show) {
            document.getElementById('loading').style.display = show ? 'block' : 'none';
        }

        function showError(msg, hideLoading = true) {
            const errorEl = document.getElementById('error');
            errorEl.textContent = msg;
            errorEl.style.display = 'block';
            if (hideLoading) showLoading(false);
        }

        function hideError() {
            document.getElementById('error').style.display = 'none';
        }

        // Initialize
        window.addEventListener('load', async () => {
            // Render wallet options
            renderWalletOptions();

            // Auto-connect if already connected
            if (typeof window.ethereum !== 'undefined') {
                try {
                    const accounts = await window.ethereum.request({
                        method: 'eth_accounts'
                    });

                    if (accounts.length > 0) {
                        provider = new BrowserProvider(window.ethereum);
                        signer = await provider.getSigner();
                        connectedAddress = await signer.getAddress();
                        showWalletConnected(connectedAddress);
                    }
                } catch (e) {}
            }
        });

        // Listen for account changes
        if (typeof window.ethereum !== 'undefined') {
            window.ethereum.on('accountsChanged', (accounts) => {
                location.reload();
            });

            window.ethereum.on('chainChanged', () => {
                location.reload();
            });
        }
    </script>
</body>
</html>