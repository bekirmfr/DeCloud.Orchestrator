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
 * UPDATED: SSH config support, port 2222, ProxyJump
 */
function generateWalletDerivedSSHInstructions(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);
    const certId = `decloud-${vmIdShort}`;
    const nodePort = cert.nodePort || 2222;

    // Build SSH config content
    const sshConfigContent = `# DeCloud SSH Configuration for ${vmName}
# Add to ~/.ssh/config (or C:\\\\Users\\\\USERNAME\\\\.ssh\\\\config on Windows)

Host decloud-bastion
    HostName ${cert.nodeIp}
    Port ${nodePort}
    User decloud
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub

Host ${cert.vmIp}
    User root
    ProxyJump decloud-bastion
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub`;

    // Simple SSH command (after config setup)
    const simpleSSHCommand = `ssh ${cert.vmIp}`;

    // Direct ProxyJump command (no config needed)
    const proxyJumpCommand = `ssh -p ${nodePort} -i ~/.ssh/decloud-wallet.pem \\
  -o CertificateFile=~/.ssh/${certId}-cert.pub \\
  -J decloud@${cert.nodeIp}:${nodePort} \\
  ubuntu@${cert.vmIp}`;

    return `
        <div class="modal-content" style="max-width: 900px;">
            <div class="modal-header">
                <h2>🔐 SSH Certificate for ${escapeHtml(vmName)}</h2>
                <button class="modal-close" onclick="this.closest('.modal-overlay').remove()">×</button>
            </div>
            
            <div class="modal-body" style="max-height: 80vh; overflow-y: auto;">
                
                <!-- Certificate Status -->
                <div class="cert-info" style="background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 20px; border-radius: 12px; margin-bottom: 25px; color: white;">
                    <div style="display: flex; justify-content: space-between; align-items: center; flex-wrap: wrap; gap: 10px;">
                        <div>
                            <div style="font-size: 1.2rem; font-weight: 600; margin-bottom: 5px;">✓ Certificate Issued Successfully</div>
                            <div style="font-size: 0.9rem; opacity: 0.9;">Fingerprint: <code style="background: rgba(0,0,0,0.2); padding: 2px 8px; border-radius: 4px;">${escapeHtml(cert.fingerprint.substring(0, 50))}...</code></div>
                        </div>
                        <div style="text-align: right;">
                            <div style="font-size: 0.85rem; opacity: 0.8;">Valid for:</div>
                            <div style="font-size: 1.5rem; font-weight: 700;">${Math.floor((new Date(cert.validUntil) - new Date()) / 1000 / 60)} minutes</div>
                        </div>
                    </div>
                </div>

                <!-- Important Notice -->
                <div style="background: #fef3c7; border: 2px solid #f59e0b; padding: 15px; border-radius: 8px; margin-bottom: 25px;">
                    <div style="color: #92400e; font-weight: 600; margin-bottom: 8px;">📌 One-Time Setup (5 minutes)</div>
                    <p style="color: #78350f; margin: 0; font-size: 0.9rem;">
                        Your wallet signature generates your SSH key! <strong>No need to manage keys manually.</strong> 
                        Save these files once, then request new certificates anytime with just a wallet signature.
                    </p>
                </div>

                <!-- Step 1: Save Private Key -->
                <div class="code-section" style="background: #f9fafb; padding: 20px; border-radius: 12px; margin-bottom: 20px; border-left: 4px solid #3b82f6;">
                    <h3 style="margin-top: 0; color: #1e40af;">
                        <span style="background: #3b82f6; color: white; padding: 4px 12px; border-radius: 20px; margin-right: 8px;">1</span>
                        Save Private Key (reusable)
                    </h3>
                    <p style="color: #6b7280; font-size: 0.9rem; margin-bottom: 15px;">
                        💾 This key is derived from your wallet and <strong>never changes</strong>. Save it once and keep it safe!
                    </p>
                    <pre style="background: #1f2937; color: #e5e7eb; padding: 15px; border-radius: 8px; overflow-x: auto; font-size: 0.85rem;"><code id="key-code"># Save your wallet-derived private key
cat > ~/.ssh/decloud-wallet.pem << 'EOF'
${cert.privateKey}
EOF
chmod 600 ~/.ssh/decloud-wallet.pem</code></pre>
                    <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('key-code').textContent)" style="margin-top: 10px;">
                        📋 Copy Private Key Setup
                    </button>
                </div>

                <!-- Step 2: Save Certificate -->
                <div class="code-section" style="background: #f9fafb; padding: 20px; border-radius: 12px; margin-bottom: 20px; border-left: 4px solid #f59e0b;">
                    <h3 style="margin-top: 0; color: #92400e;">
                        <span style="background: #f59e0b; color: white; padding: 4px 12px; border-radius: 20px; margin-right: 8px;">2</span>
                        Save Certificate (renew hourly)
                    </h3>
                    <p style="color: #6b7280; font-size: 0.9rem; margin-bottom: 15px;">
                        🔄 Certificates expire after 1 hour. Request a new one anytime (no password needed, just wallet signature!)
                    </p>
                    <pre style="background: #1f2937; color: #e5e7eb; padding: 15px; border-radius: 8px; overflow-x: auto; font-size: 0.85rem;"><code id="cert-code"># Save certificate (valid for 1 hour)
cat > ~/.ssh/${certId}-cert.pub << 'EOF'
${cert.certificate}
EOF</code></pre>
                    <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('cert-code').textContent)" style="margin-top: 10px;">
                        📋 Copy Certificate Setup
                    </button>
                </div>

                <!-- Step 3: Connect (Two Options) -->
                <div style="background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 25px; border-radius: 12px; margin-bottom: 20px;">
                    <h3 style="margin-top: 0; color: white;">
                        <span style="background: rgba(255,255,255,0.3); color: white; padding: 4px 12px; border-radius: 20px; margin-right: 8px;">3</span>
                        Connect to Your VM
                    </h3>
                    
                    <!-- Option A: SSH Config (Recommended) -->
                    <div style="background: rgba(0,0,0,0.2); padding: 20px; border-radius: 8px; margin-bottom: 15px;">
                        <div style="color: white; font-weight: 600; font-size: 1.1rem; margin-bottom: 12px;">
                            ✨ Option A: One-Time Config Setup (Recommended)
                        </div>
                        <p style="color: rgba(255,255,255,0.9); font-size: 0.9rem; margin-bottom: 15px;">
                            Set up SSH config once, then connect with a simple <code style="background: rgba(0,0,0,0.3); padding: 2px 6px; border-radius: 4px;">ssh ${cert.vmIp}</code> command!
                        </p>
                        
                        <pre style="background: #1f2937; color: #e5e7eb; padding: 15px; border-radius: 8px; overflow-x: auto; font-size: 0.8rem; margin-bottom: 12px;"><code id="config-code">${sshConfigContent}</code></pre>
                        
                        <div style="display: flex; gap: 10px; flex-wrap: wrap;">
                            <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('config-code').textContent)" style="background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3);">
                                📋 Copy Config
                            </button>
                            <button class="btn btn-secondary" onclick="downloadWalletSSHConfig('${cert.vmIp}', '${cert.nodeIp}', ${nodePort}, '${certId}')" style="background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3);">
                                💾 Download Config File
                            </button>
                        </div>
                        
                        <div style="margin-top: 15px; padding: 12px; background: rgba(16, 185, 129, 0.2); border-radius: 6px;">
                            <div style="color: white; font-weight: 600; margin-bottom: 6px;">Then connect with:</div>
                            <pre style="background: #1f2937; color: #10b981; padding: 12px; border-radius: 6px; margin: 0; font-size: 1rem;"><code id="simple-ssh">${simpleSSHCommand}</code></pre>
                            <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('simple-ssh').textContent)" style="margin-top: 8px; background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3); font-size: 0.85rem;">
                                📋 Copy
                            </button>
                        </div>
                    </div>
                    
                    <!-- Option B: Direct Command -->
                    <div style="background: rgba(0,0,0,0.2); padding: 20px; border-radius: 8px;">
                        <div style="color: white; font-weight: 600; font-size: 1.1rem; margin-bottom: 12px;">
                            Option B: Direct ProxyJump Command
                        </div>
                        <p style="color: rgba(255,255,255,0.9); font-size: 0.9rem; margin-bottom: 15px;">
                            No config needed, but requires typing the full command each time
                        </p>
                        <pre style="background: #1f2937; color: #e5e7eb; padding: 15px; border-radius: 8px; overflow-x: auto; font-size: 0.8rem; margin-bottom: 12px;"><code id="proxy-jump">${proxyJumpCommand}</code></pre>
                        <button class="btn btn-secondary" onclick="copyToClipboard(document.getElementById('proxy-jump').textContent)" style="background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3);">
                            📋 Copy Command
                        </button>
                    </div>
                </div>

                <!-- Future Connections Info -->
                <div class="info-box" style="background: #eff6ff; border: 2px solid #3b82f6; padding: 20px; border-radius: 12px; margin-bottom: 20px;">
                    <div style="color: #1e40af; font-weight: 600; font-size: 1.1rem; margin-bottom: 12px;">
                        💡 For Future Connections
                    </div>
                    <ul style="color: #1e3a8a; font-size: 0.9rem; margin: 0; padding-left: 20px; line-height: 1.6;">
                        <li><strong>Your private key never changes</strong> - saved once in step 1</li>
                        <li><strong>Only certificates expire</strong> - renew hourly with just a wallet signature</li>
                        <li><strong>No password needed</strong> - your wallet signature is your authentication</li>
                        <li><strong>Same wallet = same key</strong> - works across all your devices</li>
                    </ul>
                </div>
                
                <!-- Action Buttons -->
                <div class="button-group" style="display: flex; gap: 12px; justify-content: flex-end; flex-wrap: wrap;">
                    <button class="btn btn-secondary" onclick="downloadSSHBundle('${vmId}', '${vmName}', ${JSON.stringify(cert).replace(/'/g, "&#39;")})">
                        📥 Download Complete Bundle
                    </button>
                    <button class="btn btn-primary" onclick="this.closest('.modal-overlay').remove()">
                        Got It! 🚀
                    </button>
                </div>
            </div>
        </div>
    `;
}

/**
 * Generate instructions for user-registered SSH keys
 * UPDATED: Port 2222 and ProxyJump
 */
function generateUserKeySSHInstructions(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);
    const nodePort = cert.nodePort || 2222;

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
                <pre><code id="ssh-command">ssh -p ${nodePort} -i ~/.ssh/id_ed25519 \\
    -o CertificateFile=~/.ssh/decloud-${vmIdShort}-cert.pub \\
    -J decloud@${escapeHtml(cert.nodeIp)}:${nodePort} \\
    ubuntu@${escapeHtml(cert.vmIp)}</code></pre>
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
 * Download SSH config for wallet-based auth
 */
function downloadWalletSSHConfig(vmIp, bastionHost, bastionPort, certId) {
    const config = `# DeCloud SSH Configuration
# Add to ~/.ssh/config (or C:\\Users\\USERNAME\\.ssh\\config on Windows)

Host decloud-bastion
    HostName ${bastionHost}
    Port ${bastionPort}
    User decloud
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub

Host ${vmIp}
    User ubuntu
    ProxyJump decloud-bastion
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub

# After adding this to your SSH config, connect with: ssh ${vmIp}
`;

    const blob = new Blob([config], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `decloud-${certId}-config`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);
    
    showToast('SSH config downloaded! Add it to ~/.ssh/config', 'success');
}

/**
 * Download SSH bundle as text file
 * UPDATED: Includes SSH config and port 2222
 */
function downloadSSHBundle(vmId, vmName, cert) {
    const vmIdShort = vmId.substring(0, 8);
    const certId = `decloud-${vmIdShort}`;
    const nodePort = cert.nodePort || 2222;

    // Updated README with config file instructions
    const readmeContent = `DeCloud SSH Access Bundle for ${vmName}
============================================

This bundle contains your SSH credentials for accessing ${vmName}.

FILES:
------
- decloud-wallet.pem: Your wallet-derived private key (KEEP SECURE!)
- ${certId}-cert.pub: Your SSH certificate (valid for 1 hour)
- ssh-config: SSH configuration file
- ssh-command.sh: Ready-to-use connection script
- README.txt: This file

QUICK START:
------------
1. Move files to ~/.ssh/
   $ mv decloud-wallet.pem ${certId}-cert.pub ~/.ssh/
   $ chmod 600 ~/.ssh/decloud-wallet.pem

2. Add ssh-config to ~/.ssh/config
   $ cat ssh-config >> ~/.ssh/config

3. Connect!
   $ ssh ${cert.vmIp}

ALTERNATIVE (No Config):
-------------------------
$ ssh -p ${nodePort} -i ~/.ssh/decloud-wallet.pem \\
  -o CertificateFile=~/.ssh/${certId}-cert.pub \\
  -J decloud@${cert.nodeIp}:${nodePort} \\
  ubuntu@${cert.vmIp}

CERTIFICATE RENEWAL:
--------------------
Certificates expire after 1 hour. To renew:
1. Go to the VM dashboard
2. Click "Get SSH Certificate"
3. Sign with your wallet
4. Save the new certificate (same filename: ${certId}-cert.pub)
5. Your private key (decloud-wallet.pem) never changes!

SECURITY:
---------
- Private key: wallet-derived, same for all your VMs
- Certificate: unique per VM, expires hourly
- Multi-tenant isolation: your cert only works for your VMs
- Port ${nodePort}: bypasses common ISP restrictions

SUPPORT:
--------
For help, visit: https://github.com/bekirmfr/DeCloud

Generated: ${new Date().toISOString()}
`;

    // Create SSH config content
    const sshConfigContent = `# DeCloud SSH Configuration for ${vmName}
Host decloud-bastion
    HostName ${cert.nodeIp}
    Port ${nodePort}
    User decloud
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub

Host ${cert.vmIp}
    User ubuntu
    ProxyJump decloud-bastion
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/${certId}-cert.pub
`;

    // Create connection script
    const sshScriptContent = `#!/bin/bash
# DeCloud SSH Connection Script
# VM: ${vmName} (${vmId})

ssh -p ${nodePort} -i ~/.ssh/decloud-wallet.pem \\
    -o CertificateFile=~/.ssh/${certId}-cert.pub \\
    -J decloud@${cert.nodeIp}:${nodePort} \\
    ubuntu@${cert.vmIp}
`;

    // Create files
    const files = {
        'decloud-wallet.pem': cert.privateKey,
        [`${certId}-cert.pub`]: cert.certificate,
        'ssh-config': sshConfigContent,
        'ssh-command.sh': sshScriptContent,
        'README.txt': readmeContent
    };

    // Bundle everything into one text file
    let bundleContent = `DeCloud SSH Bundle for ${vmName}
${'='.repeat(60)}

Extract these files to ~/.ssh/

`;

    for (const [filename, content] of Object.entries(files)) {
        bundleContent += `\n${'='.repeat(60)}\n`;
        bundleContent += `FILE: ${filename}\n`;
        bundleContent += `${'='.repeat(60)}\n`;
        bundleContent += content;
        bundleContent += `\n`;
    }

    const blob = new Blob([bundleContent], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `decloud-${vmName}-ssh-bundle.txt`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);

    showToast('SSH bundle downloaded!', 'success');
}

// Make functions globally available for onclick handlers
window.downloadWalletSSHConfig = downloadWalletSSHConfig;

// Export functions as ES6 module
export {
    getSSHCertificate,
    showSSHConnectionModal,
    downloadSSHBundle,
    downloadWalletSSHConfig,
    SSH_KEY_DERIVATION_MESSAGE
};