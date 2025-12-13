// ssh-wallet.js - Wallet-derived SSH key management for DeCloud
// Import this in your main app.js

const SSH_KEY_DERIVATION_MESSAGE = "DeCloud SSH Key Derivation v1";

/**
 * Get SSH certificate for VM access
 * Automatically uses user's SSH key or derives from wallet
 */
async function getSSHCertificate(vmId) {
    try {
        console.log('[SSH] Requesting certificate for VM:', vmId);

        // Step 1: Check if user needs wallet signature
        const connectionInfoResponse = await api(`/api/vms/${vmId}/ssh/connection-info`);
        const connectionInfo = await connectionInfoResponse.json();

        if (!connectionInfo.success) {
            throw new Error(connectionInfo.error || 'Failed to get connection info');
        }

        const needsWalletSig = connectionInfo.data.requiresWalletSignature;

        let walletSignature = null;

        // Step 2: Get wallet signature if needed (no user SSH key)
        if (needsWalletSig) {
            console.log('[SSH] No SSH key registered - requesting wallet signature...');

            const signer = window.ethersSigner();
            if (!signer) {
                throw new Error('Wallet not connected. Please connect your wallet first.');
            }

            // Sign the SSH key derivation message
            walletSignature = await signer.signMessage(SSH_KEY_DERIVATION_MESSAGE);
            console.log('[SSH] ✓ Wallet signature obtained');
        } else {
            console.log('[SSH] Using registered SSH key');
        }

        // Step 3: Request certificate from API
        const certResponse = await api(`/api/vms/${vmId}/ssh/certificate`, {
            method: 'POST',
            body: JSON.stringify({
                walletSignature: walletSignature,
                ttlSeconds: 3600 // 1 hour validity
            })
        });

        const certData = await certResponse.json();

        if (!certData.success || !certData.data) {
            throw new Error(certData.error || 'Failed to get SSH certificate');
        }

        console.log('[SSH] ✓ Certificate issued, valid until:', certData.data.validUntil);

        return certData.data;
    } catch (error) {
        console.error('[SSH] Failed to get certificate:', error);

        // User-friendly error messages
        if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            throw new Error('Wallet signature rejected. Please sign the message to generate SSH credentials.');
        } else if (error.message?.includes('User rejected')) {
            throw new Error('Wallet signature rejected.');
        }

        throw error;
    }
}

/**
 * Show SSH connection modal with certificate and instructions
 */
async function showSSHConnectionModal(vmId, vmName) {
    try {
        showToast('Generating SSH credentials...', 'info');

        // Get certificate (may trigger wallet signature)
        const cert = await getSSHCertificate(vmId);

        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.id = 'ssh-connection-modal';

        let content;

        if (cert.isWalletDerived) {
            // WALLET-DERIVED KEYS: Show complete setup
            content = generateWalletDerivedSSHInstructions(vmId, vmName, cert);
        } else {
            // USER SSH KEY: Show standard certificate instructions
            content = generateUserKeySSHInstructions(vmId, vmName, cert);
        }

        modal.innerHTML = content;
        document.body.appendChild(modal);

        // Close on background click
        modal.onclick = (e) => {
            if (e.target === modal) modal.remove();
        };

        showToast('SSH credentials ready!', 'success');
    } catch (error) {
        console.error('[SSH] Error showing connection modal:', error);
        showToast(error.message || 'Failed to generate SSH credentials', 'error');
    }
}

/**
 * Generate instructions for wallet-derived SSH keys
 */
function generateWalletDerivedSSHInstructions(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);

    return `
        <div class="modal-content" style="max-width: 900px;">
            <div class="modal-header">
                <h2>🔐 SSH Access to ${escapeHtml(vmName)}</h2>
                <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">×</button>
            </div>
            
            <div class="modal-body">
                <div class="info-box" style="background: #1e40af; border: 1px solid #3b82f6; padding: 15px; border-radius: 8px; margin-bottom: 20px;">
                    <strong>✨ Wallet-Based SSH Credentials</strong>
                    <p style="margin: 10px 0 0 0; color: #93c5fd;">
                        Your wallet signature generates your SSH key! No need to manage keys manually.
                        Save these files once, then request new certificates anytime with just a wallet signature.
                    </p>
                </div>
                
                <div class="cert-info" style="background: #1f2937; padding: 15px; border-radius: 8px; margin-bottom: 20px;">
                    <div style="display: flex; justify-content: space-between; align-items: center;">
                        <span style="color: #10b981;">✓ Certificate Issued</span>
                        <span style="color: #9ca3af;">
                            Valid for: ${Math.floor((new Date(cert.validUntil) - new Date()) / 1000 / 60)} minutes
                        </span>
                    </div>
                    <div style="margin-top: 8px; font-size: 0.875rem; color: #6b7280;">
                        Fingerprint: <code style="color: #93c5fd;">${escapeHtml(cert.fingerprint)}</code>
                    </div>
                </div>
                
                <h3 style="margin-top: 30px;">📥 One-Time Setup (5 minutes)</h3>
                <p style="color: #9ca3af; margin-bottom: 15px;">
                    Save these files to your computer. You only need to do this once!
                </p>
                
                <div class="code-section">
                    <h4>1. Save Private Key (reusable!)</h4>
                    <pre><code id="private-key-code"># Save your wallet-derived private key
cat > ~/.ssh/decloud-wallet.pem << 'EOF'
${cert.privateKey}
EOF
chmod 600 ~/.ssh/decloud-wallet.pem</code></pre>
                    <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('private-key-code').textContent)" style="margin-top: 8px;">
                        📋 Copy Private Key Setup
                    </button>
                </div>
                
                <div class="code-section" style="margin-top: 20px;">
                    <h4>2. Save Certificate (renew hourly)</h4>
                    <pre><code id="cert-code"># Save certificate (valid for 1 hour)
cat > ~/.ssh/decloud-${vmIdShort}-cert.pub << 'EOF'
${cert.certificate}
EOF</code></pre>
                    <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('cert-code').textContent)" style="margin-top: 8px;">
                        📋 Copy Certificate Setup
                    </button>
                </div>
                
                <div class="code-section" style="margin-top: 20px;">
                    <h4>3. Connect to Your VM</h4>
                    <pre><code id="ssh-command">ssh -i ~/.ssh/decloud-wallet.pem \\
    -o CertificateFile=~/.ssh/decloud-${vmIdShort}-cert.pub \\
    decloud@${escapeHtml(cert.nodeIp)} \\
    ssh ubuntu@${escapeHtml(cert.vmIp)}</code></pre>
                    <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('ssh-command').textContent)" style="margin-top: 8px;">
                        📋 Copy SSH Command
                    </button>
                </div>
                
                <div class="info-box" style="background: #065f46; border: 1px solid #10b981; padding: 15px; border-radius: 8px; margin-top: 30px;">
                    <strong>💡 For Future Connections:</strong>
                    <p style="margin: 10px 0 0 0; color: #6ee7b7;">
                        1. Your private key (~/.ssh/decloud-wallet.pem) stays the same forever<br>
                        2. Just request a new certificate when the old one expires (1 hour)<br>
                        3. No need to download the private key again - only the certificate changes
                    </p>
                </div>
                
                <div class="button-group" style="display: flex; gap: 10px; justify-content: flex-end; margin-top: 30px;">
                    <button class="btn btn-secondary" onclick="downloadSSHBundle('${vmId}', '${vmName}', ${JSON.stringify(cert).replace(/'/g, "&#39;")})">
                        📥 Download Complete Bundle
                    </button>
                    <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">
                        Got It!
                    </button>
                </div>
            </div>
        </div>
    `;
}

/**
 * Generate instructions for user-registered SSH keys
 */
function generateUserKeySSHInstructions(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);

    return `
        <div class="modal-content" style="max-width: 700px;">
            <div class="modal-header">
                <h2>🔐 SSH Access to ${escapeHtml(vmName)}</h2>
                <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">×</button>
            </div>
            
            <div class="modal-body">
                <div class="cert-info" style="background: #1f2937; padding: 15px; border-radius: 8px; margin-bottom: 20px;">
                    <div style="display: flex; justify-content: space-between; align-items: center;">
                        <span style="color: #10b981;">✓ Certificate Issued</span>
                        <span style="color: #9ca3af;">
                            Valid for: ${Math.floor((new Date(cert.validUntil) - new Date()) / 1000 / 60)} minutes
                        </span>
                    </div>
                </div>
                
                <h3>📥 Save Certificate</h3>
                <pre><code id="cert-code">cat > ~/.ssh/decloud-${vmIdShort}-cert.pub << 'EOF'
${cert.certificate}
EOF</code></pre>
                <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('cert-code').textContent)" style="margin-top: 8px;">
                    📋 Copy
                </button>
                
                <h3 style="margin-top: 30px;">🔗 Connect</h3>
                <pre><code id="ssh-command">ssh -i ~/.ssh/id_ed25519 \\
    -o CertificateFile=~/.ssh/decloud-${vmIdShort}-cert.pub \\
    decloud@${escapeHtml(cert.nodeIp)} \\
    ssh ubuntu@${escapeHtml(cert.vmIp)}</code></pre>
                <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('ssh-command').textContent)" style="margin-top: 8px;">
                    📋 Copy
                </button>
                
                <div class="button-group" style="display: flex; justify-content: flex-end; margin-top: 30px;">
                    <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">
                        Close
                    </button>
                </div>
            </div>
        </div>
    `;
}

/**
 * Download SSH bundle as ZIP file
 */
function downloadSSHBundle(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);

    // Create files
    const files = {
        'decloud-wallet.pem': cert.privateKey,
        [`decloud-${vmIdShort}-cert.pub`]: cert.certificate,
        'README.txt': `DeCloud SSH Access Bundle for ${vmName}

This bundle contains your SSH credentials for accessing ${vmName}.

FILES:
- decloud-wallet.pem: Your wallet-derived private key (KEEP SECURE!)
- decloud-${vmIdShort}-cert.pub: SSH certificate (expires: ${cert.validUntil})
- ssh-command.sh: Ready-to-use SSH connection script

SETUP (One-Time):
1. Copy decloud-wallet.pem to ~/.ssh/
   chmod 600 ~/.ssh/decloud-wallet.pem

2. Copy the certificate:
   cp decloud-${vmIdShort}-cert.pub ~/.ssh/

3. Connect:
   bash ssh-command.sh

IMPORTANT:
- Your private key (decloud-wallet.pem) never expires
- Only the certificate expires (every hour)
- To get a new certificate, click "Connect" in the dashboard

SECURITY:
- Never share your private key
- The private key is derived from your wallet signature
- Same wallet = same SSH key across all sessions

Generated: ${new Date().toISOString()}
`,
        'ssh-command.sh': `#!/bin/bash
# DeCloud SSH Connection Script
# VM: ${vmName} (${vmId})

ssh -i ~/.ssh/decloud-wallet.pem \\
    -o CertificateFile=~/.ssh/decloud-${vmIdShort}-cert.pub \\
    decloud@${cert.nodeIp} \\
    ssh ubuntu@${cert.vmIp}
`
    };

    // For simplicity, create a text file with all content
    // In production, use a proper ZIP library like JSZip
    let bundleContent = '';
    for (const [filename, content] of Object.entries(files)) {
        bundleContent += `\n=== ${filename} ===\n${content}\n`;
    }

    const blob = new Blob([bundleContent], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `decloud-${vmName}-ssh-bundle.txt`;
    a.click();
    URL.revokeObjectURL(url);

    showToast('SSH bundle downloaded!', 'success');
}

// Export functions as ES6 module
export {
    getSSHCertificate,
    showSSHConnectionModal,
    downloadSSHBundle,
    SSH_KEY_DERIVATION_MESSAGE
};