// ============================================================================
// VM-related modals
// Password save/reveal, SSH connect info & instructions, terminal/file-browser
// launchers, VM event messages, download SSH config.
// ============================================================================

import { escapeHtml, isValidIp, showToast, makeModalAccessible } from './utils.js';
import { encryptPassword, decryptPassword } from './wallet-crypto.js';

function api(endpoint, options = {}) {
    if (!window.api) throw new Error('API function not available');
    return window.api(endpoint, options);
}

function copyToClipboard(text) {
    if (window.copyToClipboard) return window.copyToClipboard(text);
    return navigator.clipboard?.writeText(text);
}

// ── Password save (post VM creation) ────────────────────────────────────────

export async function showPasswordModal(vmId, vmName, password) {
    return new Promise((resolve) => {
        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.id = 'password-modal';
        modal.innerHTML = `
            <div class="modal-content" style="max-width: 550px;" role="document">
                <h3 id="pwd-modal-title">🔐 Save Your VM Password</h3>
                <p>Your VM <strong>${escapeHtml(vmName)}</strong> has been created with this password:</p>

                <div style="background: #1a1b26; padding: 20px; border-radius: 8px; margin: 20px 0; text-align: center;">
                    <code style="font-size: 1.5em; color: #10b981; letter-spacing: 1px;" id="password-display">${escapeHtml(password)}</code>
                </div>

                <div style="background: #2d1f1f; border: 1px solid #7f1d1d; padding: 15px; border-radius: 8px; margin: 15px 0;">
                    <p style="color: #fca5a5; margin: 0;">
                        <strong>⚠️ Important:</strong> This password will be encrypted with your wallet and stored securely.
                        You can always retrieve it by signing with your wallet, but <strong>save it now</strong> as a backup.
                    </p>
                </div>

                <div style="margin-top: 20px; display: flex; gap: 10px; justify-content: flex-end;">
                    <button data-action="copy" class="btn btn-secondary">📋 Copy Password</button>
                    <button data-action="secure" class="btn btn-primary">🔒 Secure & Continue</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);

        let cleanupA11y = null;
        const close = () => {
            cleanupA11y?.();
            modal.remove();
            resolve();
        };

        const doSecureAndClose = async () => {
            try {
                const encryptedPassword = await encryptPassword(password);
                await api(`/api/vms/${vmId}/secure-password`, {
                    method: 'POST',
                    body: JSON.stringify({ encryptedPassword })
                });
                showToast('Password secured with your wallet!', 'success');
                close();
            } catch (error) {
                console.error('Failed to secure password:', error);
                showToast('Failed to encrypt - please save password manually!', 'error');
            }
        };

        modal.querySelector('[data-action="copy"]').addEventListener('click', () => copyToClipboard(password));
        modal.querySelector('[data-action="secure"]').addEventListener('click', doSecureAndClose);
        cleanupA11y = makeModalAccessible(modal, close, { labelledBy: 'pwd-modal-title' });
    });
}

// ── Password reveal (wallet decrypt) ────────────────────────────────────────

export async function revealPassword(vmId, vmName) {
    try {
        const response = await api(`/api/vms/${vmId}/encrypted-password`);
        const data = await response.json();

        if (!data.success || !data.data || !data.data.encryptedPassword) {
            showToast('Password not available', 'error');
            return;
        }

        const password = await decryptPassword(data.data.encryptedPassword);

        const modal = document.createElement('div');
        modal.className = 'modal-overlay active';
        modal.innerHTML = `
            <div class="modal-content" style="max-width: 450px;" role="document">
                <h3 id="reveal-pw-title">🔑 VM Password</h3>
                <p>Password for <strong>${escapeHtml(vmName)}</strong>:</p>

                <div style="background: #1a1b26; padding: 20px; border-radius: 8px; margin: 15px 0; text-align: center;">
                    <code style="font-size: 1.4em; color: #10b981;" id="revealed-password">${escapeHtml(password)}</code>
                </div>

                <div style="display: flex; gap: 10px; justify-content: flex-end;">
                    <button class="btn btn-secondary" id="copy-revealed-pw-btn">📋 Copy</button>
                    <button class="btn btn-primary" data-action="close">Close</button>
                </div>
            </div>
        `;
        document.body.appendChild(modal);

        let cleanupA11y = null;
        const close = () => {
            cleanupA11y?.();
            modal.remove();
        };

        modal.querySelector('#copy-revealed-pw-btn')?.addEventListener('click', () => copyToClipboard(password));
        modal.querySelector('[data-action="close"]').addEventListener('click', close);
        cleanupA11y = makeModalAccessible(modal, close, { labelledBy: 'reveal-pw-title' });
    } catch (error) {
        console.error('Failed to reveal password:', error);
        showToast("Failed to decrypt password. Make sure you're using the same wallet.", 'error');
    }
}

// ── File browser / terminal launchers ───────────────────────────────────────

export function openFileBrowser(vmId, vmName, nodeAgentHost, nodeAgentPort, vmIp) {
    const params = new URLSearchParams({
        vmId, vmName, nodeIp: nodeAgentHost, nodePort: nodeAgentPort, vmIp
    });
    window.open(`/file-browser.html?${params.toString()}`, '_blank');
}

export function openTerminal(vmId, vmName, nodeAgentHost, nodeAgentPort, vmIp) {
    const params = new URLSearchParams({
        vmId, vmName, nodeIp: nodeAgentHost, nodePort: nodeAgentPort, vmIp
    });
    window.open(`/terminal.html?${params.toString()}`, '_blank');
}

// ── SSH connection info modal ───────────────────────────────────────────────

export function showConnectInfo(sshJumpHost, sshJumpPort, vmIp, vmId, vmName, nodeAgentHost, nodeAgentPort) {
    document.getElementById('connect-info-modal')?.remove();

    const modal = document.createElement('div');
    modal.id = 'connect-info-modal';
    modal.style.cssText = `
        position: fixed; inset: 0; background: rgba(0,0,0,0.7);
        display: flex; align-items: center; justify-content: center; z-index: 1000;
    `;

    modal.innerHTML = `
        <div style="background: #1a1d26; border: 1px solid #2a2d36; border-radius: 12px; padding: 28px; width: 520px; max-width: 90vw; color: #f0f2f5;" role="document">
            <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 20px;">
                <h3 id="connect-info-title" style="margin: 0; font-size: 1.25rem; color: #00d4aa;">🔗 Connect to </h3>
                <button data-action="close" aria-label="Close" style="background: none; border: none; color: #6b7280; cursor: pointer; font-size: 1.5rem;">&times;</button>
            </div>

            <div class="connect-section" style="background: #12141a; padding: 16px; border-radius: 8px; margin-bottom: 16px;">
                <div style="color: #10b981; font-weight: 600; margin-bottom: 12px;">⚡ Quick Actions</div>
                <div style="display: flex; gap: 10px; flex-wrap: wrap;">
                    <button class="btn btn-sm btn-primary" data-action="ssh" style="padding: 8px 16px; font-size: 0.9rem;">🖥️ Ssh Instructions</button>
                    <button class="btn btn-sm btn-primary" data-action="terminal" style="padding: 8px 16px; font-size: 0.9rem;">🖥️ Open Terminal</button>
                    <button class="btn btn-sm btn-secondary" data-action="file-browser" style="padding: 8px 16px; font-size: 0.9rem; background: #1e3a8a; border-color: #3b82f6;">📁 File Browser</button>
                </div>
            </div>

            <div class="connect-section" style="background: #12141a; padding: 16px; border-radius: 8px; margin-bottom: 16px;">
                <div style="color: #93c5fd; font-weight: 600; margin-bottom: 12px;">🔐 SSH Connection</div>
                <table style="width: 100%; font-size: 0.9rem;">
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af; width: 120px;">Bastion Host:</td>
                        <td style="padding: 6px 0;"><code id="connect-info-bastion" style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;"></code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">VM IP:</td>
                        <td style="padding: 6px 0;"><code id="connect-info-vmip" style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;"></code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">Username:</td>
                        <td style="padding: 6px 0;"><code style="background: #1a1d26; padding: 4px 8px; border-radius: 4px; font-family: 'JetBrains Mono', monospace;">root</code></td>
                    </tr>
                    <tr>
                        <td style="padding: 6px 0; color: #9ca3af;">Auth:</td>
                        <td style="padding: 6px 0;"><span style="color: #10b981;">✓ SSH Certificate (wallet-derived)</span></td>
                    </tr>
                </table>
            </div>

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

    modal.querySelector('#connect-info-title').textContent = `🔗 Connect to ${vmName}`;
    modal.querySelector('#connect-info-bastion').textContent = `${sshJumpHost}:${sshJumpPort}`;
    modal.querySelector('#connect-info-vmip').textContent = vmIp;

    document.body.appendChild(modal);

    let cleanupA11y = null;
    const close = () => {
        cleanupA11y?.();
        modal.remove();
    };

    modal.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-action]');
        if (!btn) return;
        e.stopPropagation();
        switch (btn.dataset.action) {
            case 'close': close(); break;
            case 'ssh': showSshInstructions(sshJumpHost, sshJumpPort, vmIp, vmId, vmName, nodeAgentHost, nodeAgentPort); break;
            case 'terminal': openTerminal(vmId, vmName, nodeAgentHost, nodeAgentPort, vmIp); break;
            case 'file-browser': openFileBrowser(vmId, vmName, nodeAgentHost, nodeAgentPort, vmIp); break;
        }
    });

    cleanupA11y = makeModalAccessible(modal, close, { labelledBy: 'connect-info-title' });
}

// ── SSH instructions modal ──────────────────────────────────────────────────

export function showSshInstructions(sshJumpHost, sshJumpPort, vmIp, vmId, vmName, nodeAgentHost, nodeAgentPort) {
    if (!isValidIp(sshJumpHost) || !isValidIp(vmIp)) { showToast('Invalid IP address format', 'error'); return; }
    if (!isValidIp(nodeAgentHost)) { showToast('Invalid Node Agent host format', 'error'); return; }
    if (sshJumpPort < 1 || sshJumpPort > 65535 || nodeAgentPort < 1 || nodeAgentPort > 65535) {
        showToast('Invalid port number', 'error');
        return;
    }

    const sshConfigCommand = `ssh ${vmIp}`;
    const proxyJumpCommand = `
        ssh -p ${sshJumpPort} -i ~/.ssh/decloud-wallet.pem \\
        -o CertificateFile=~/.ssh/decloud-XXXXX-cert.pub \\
        -J decloud@${sshJumpHost}:${sshJumpPort} \\
        root@${vmIp}`;
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
    User root
    ProxyJump decloud-bastion
    IdentityFile ~/.ssh/decloud-wallet.pem
    CertificateFile ~/.ssh/decloud-XXXXX-cert.pub`;

    const modal = document.createElement('div');
    modal.className = 'modal-overlay active';
    modal.innerHTML = `
        <div class="modal" style="max-width: 850px;" role="document">
            <div class="modal-header">
                <h2 class="modal-title" id="ssh-modal-title">🔗 Connect to </h2>
                <button class="modal-close" data-action="close" aria-label="Close">
                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="18" y1="6" x2="6" y2="18" /><line x1="6" y1="6" x2="18" y2="18" />
                    </svg>
                </button>
            </div>
            <div class="modal-body connect-info">
                <div class="connect-section" style="background: linear-gradient(135deg, #10b981 0%, #059669 100%); padding: 20px; border-radius: 12px; margin-bottom: 20px;">
                    <div class="connect-section-title" style="color: white; font-size: 1.1rem; font-weight: 600;">
                        ✨ Recommended: One-Time SSH Config Setup
                    </div>
                    <p style="color: rgba(255,255,255,0.9); font-size: 0.9rem; margin: 12px 0;">Set up SSH config once, then connect with a single command!</p>

                    <div style="background: rgba(0,0,0,0.2); padding: 15px; border-radius: 8px; margin: 15px 0;">
                        <div style="color: white; font-weight: 600; margin-bottom: 8px;">📝 Step 1: Add to ~/.ssh/config</div>
                        <div class="connect-code" style="background: #1f2937; margin: 0;">
                            <pre id="ssh-config-pre" style="margin: 0; color: #e5e7eb; font-size: 0.85rem; overflow-x: auto;"></pre>
                            <button class="connect-code-copy" data-action="copy-config" style="background: #10b981;">Copy Config</button>
                        </div>
                        <button class="btn btn-secondary" data-action="download-config" style="margin-top: 10px; background: rgba(255,255,255,0.2); color: white; border: 1px solid rgba(255,255,255,0.3);">💾 Download config file</button>
                    </div>

                    <div style="background: rgba(0,0,0,0.2); padding: 15px; border-radius: 8px;">
                        <div style="color: white; font-weight: 600; margin-bottom: 8px;">🚀 Step 2: Connect with Simple Command</div>
                        <div class="connect-code" style="background: #1f2937; margin: 0;">
                            <pre id="ssh-config-cmd" style="margin: 0; color: #e5e7eb; font-size: 0.9rem;"></pre>
                            <button class="connect-code-copy" data-action="copy-cmd" style="background: #10b981;">Copy</button>
                        </div>
                        <p style="color: rgba(255,255,255,0.8); font-size: 0.85rem; margin: 8px 0 0 0;">
                            ✅ That's it! Just <code id="ssh-config-cmd-inline" style="background: rgba(0,0,0,0.3); padding: 2px 6px; border-radius: 4px;"></code> from now on
                        </p>
                    </div>
                </div>

                <div class="connect-section">
                    <div class="connect-section-title">Alternative: Direct ProxyJump Command</div>
                    <div class="connect-code">
                        <pre id="ssh-proxyjump-pre" style="margin: 0; font-size: 0.85rem;"></pre>
                        <button class="connect-code-copy" data-action="copy-proxyjump">Copy</button>
                    </div>
                    <p style="color: #9ca3af; font-size: 0.875rem; margin-top: 8px;">
                        ⚠️ Don't forget to replace <code>XXXXX</code> with your certificate ID!
                    </p>
                </div>

                <div class="connect-section" style="background: #1e3a8a; border-left: 4px solid #3b82f6; padding: 15px; border-radius: 8px;">
                    <div style="color: #93c5fd; font-weight: 600; margin-bottom: 8px;">🔒 Security</div>
                    <ul style="color: #bfdbfe; font-size: 0.875rem; margin: 0; padding-left: 20px;">
                        <li>Certificates are valid for 1 hour and can be renewed anytime</li>
                        <li>Your private key (~/.ssh/decloud-wallet.pem) is derived from your wallet and never changes</li>
                        <li>Multi-tenant isolation: Your certificate only works for your VMs</li>
                        <li>Port ${sshJumpPort} is used to bypass common ISP restrictions</li>
                    </ul>
                </div>
            </div>
        </div>
    `;

    modal.querySelector('#ssh-modal-title').textContent = `🔗 Connect to ${vmName}`;
    modal.querySelector('#ssh-config-pre').textContent = sshConfigContent;
    modal.querySelector('#ssh-config-cmd').textContent = sshConfigCommand;
    modal.querySelector('#ssh-config-cmd-inline').textContent = `ssh ${vmIp}`;
    modal.querySelector('#ssh-proxyjump-pre').textContent = proxyJumpCommand;

    document.body.appendChild(modal);

    let cleanupA11y = null;
    const close = () => {
        cleanupA11y?.();
        modal.remove();
    };

    modal.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-action]');
        if (!btn) return;
        e.stopPropagation();
        switch (btn.dataset.action) {
            case 'close': close(); break;
            case 'copy-config': copyToClipboard(sshConfigContent); break;
            case 'copy-cmd': copyToClipboard(sshConfigCommand); break;
            case 'copy-proxyjump': copyToClipboard(proxyJumpCommand); break;
            case 'download-config': downloadSSHConfig(vmIp, sshJumpHost, sshJumpPort); break;
        }
    });

    cleanupA11y = makeModalAccessible(modal, close, { labelledBy: 'ssh-modal-title' });
}

export function downloadSSHConfig(vmIp, bastionHost, bastionPort) {
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
            User root
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

// ── VM messages modal (static modal in index.html) ──────────────────────────

export async function showVmMessages(vmId, vmName) {
    const modal = document.getElementById('vm-messages-modal');
    const body = document.getElementById('vm-messages-body');
    const title = document.getElementById('vm-messages-title');
    const sub = document.getElementById('vm-messages-subtitle');

    title.textContent = `${vmName} — Events`;
    sub.textContent = '';
    body.innerHTML = '<div class="loading-spinner">Loading messages...</div>';
    modal.classList.add('active');

    const cleanupA11y = makeModalAccessible(modal, () => {
        modal.classList.remove('active');
        cleanupA11y?.();
    });

    try {
        const res = await api(`/api/vms/${vmId}`);
        const data = await res.json();
        const vm = data?.data?.vm ?? data?.data ?? data;
        const msgs = Array.isArray(vm?.messages) ? vm.messages : [];

        sub.textContent = msgs.length
            ? `${msgs.length} event${msgs.length !== 1 ? 's' : ''} — newest first`
            : 'No events recorded yet';

        if (msgs.length === 0) {
            body.innerHTML = `
                <div style="text-align:center;padding:48px 24px;color:var(--text-muted);">
                    <div style="font-size:32px;margin-bottom:12px;">📋</div>
                    <div style="font-size:14px;">No events recorded yet.</div>
                    <div style="font-size:12px;margin-top:6px;">Events appear here as the VM changes state.</div>
                </div>
                ${debugHintHtml()}`;
            return;
        }

        const reversed = [...msgs].reverse();
        body.innerHTML = `<div class="vm-msg-timeline">${reversed.map(renderVmMessage).join('')}</div>${debugHintHtml()}`;
    } catch (err) {
        console.error('[VM Messages] Failed to load:', err);
        body.innerHTML = `<div style="padding:24px;color:var(--accent-danger);text-align:center;">Failed to load events.</div>`;
    }
}

// ── VM boot log modal (static modal in index.html) ──────────────────────────

let _vmLogsCleanup = null;
let _vmLogsCurrentVmId = null;
let _vmLogsCurrentContent = '';

export async function showVmLogs(vmId, vmName) {
    const modal = document.getElementById('vm-logs-modal');
    if (!modal) return;

    document.getElementById('vm-logs-title').textContent = `${vmName} — Boot Log`;
    _vmLogsCurrentVmId = vmId;

    modal.classList.add('active');

    if (_vmLogsCleanup) _vmLogsCleanup();
    _vmLogsCleanup = makeModalAccessible(modal, () => {
        modal.classList.remove('active');
        _vmLogsCleanup?.();
        _vmLogsCleanup = null;
        _vmLogsCurrentVmId = null;
        _vmLogsCurrentContent = '';
    });

    // Wire up footer buttons. Use onclick (idempotent reassignment) rather
    // than addEventListener — the modal is static so the buttons persist
    // across opens, but we want each open to bind to the *current* VM.
    document.getElementById('vm-logs-refresh').onclick = () => {
        if (_vmLogsCurrentVmId) loadVmLogs(_vmLogsCurrentVmId);
    };
    document.getElementById('vm-logs-copy').onclick = async () => {
        if (!_vmLogsCurrentContent) return;
        try {
            await navigator.clipboard.writeText(_vmLogsCurrentContent);
            showToast('Copied to clipboard', 'success');
        } catch {
            showToast('Could not copy — please select manually', 'warning');
        }
    };

    await loadVmLogs(vmId);
}

async function loadVmLogs(vmId) {
    const body = document.getElementById('vm-logs-body');
    const sub = document.getElementById('vm-logs-subtitle');
    const refreshBtn = document.getElementById('vm-logs-refresh');
    const copyBtn = document.getElementById('vm-logs-copy');

    body.innerHTML = '<div class="loading-spinner" style="padding:48px;text-align:center;color:var(--text-muted);">Loading log...</div>';
    sub.textContent = '';
    refreshBtn.disabled = true;
    copyBtn.disabled = true;
    _vmLogsCurrentContent = '';

    try {
        const res = await api(`/api/vms/${vmId}/logs?source=Console`);
        const data = await res.json();

        if (!res.ok || !data?.success) {
            // Orchestrator returns ApiResponse.Fail(code, message) for every
            // non-success path (LOG_UNAVAILABLE, NO_NODE, NODE_UNREACHABLE,
            // NODE_TIMEOUT, NODE_BAD_RESPONSE, PROXY_ERROR). Surface the
            // message verbatim — it's already shaped for human reading.
            const errMsg = data?.error?.message
                ?? data?.message
                ?? `Request failed (HTTP ${res.status})`;
            renderEmptyLog(body, errMsg);
            refreshBtn.disabled = false;
            return;
        }

        const result = data.data ?? {};
        const content = result.content ?? '';
        const totalBytes = result.totalBytes ?? content.length;
        const truncated = result.truncated ?? false;

        _vmLogsCurrentContent = content;
        sub.textContent = formatLogMeta(content.length, totalBytes, truncated);

        body.innerHTML = `<pre style="margin:0;padding:16px;font-family:var(--font-mono);font-size:12px;line-height:1.5;color:var(--text-primary);white-space:pre;overflow-x:auto;">${escapeHtml(content)}</pre>`;
        // Scroll to bottom — boot logs are most useful at the end (the
        // failure or the "ready" marker is the last meaningful line).
        body.scrollTop = body.scrollHeight;

        refreshBtn.disabled = false;
        copyBtn.disabled = false;
    } catch (err) {
        console.error('[VM Logs] Failed to load:', err);
        renderEmptyLog(body, err.message || 'Network error');
        refreshBtn.disabled = false;
    }
}

function renderEmptyLog(body, message) {
    body.innerHTML = `
        <div style="text-align:center;padding:48px 24px;color:var(--text-muted);">
            <div style="font-size:32px;margin-bottom:12px;">📄</div>
            <div style="font-size:14px;max-width:480px;margin:0 auto;">${escapeHtml(message)}</div>
        </div>`;
}

function formatLogMeta(captured, total, truncated) {
    const fmt = (b) =>
        b >= 1024 * 1024 ? `${(b / 1024 / 1024).toFixed(1)} MB`
            : b >= 1024 ? `${(b / 1024).toFixed(1)} KB`
                : `${b} B`;
    if (truncated) return `Showing last ${fmt(captured)} of ${fmt(total)} (older lines truncated)`;
    return `${fmt(total)} captured`;
}

function debugHintHtml() {
    const cmds = ['cat /var/log/cloud-init-output.log', 'cat /var/log/setup.log', 'systemctl status cloud-final.service'];
    return `<details style="margin-top:16px; padding:12px; border:1px solid var(--border-color,#333); border-radius:6px; font-size:12px;">
        <summary style="cursor:pointer; color:var(--text-muted,#9ca3af); user-select:none;">Debug: read cloud-init &amp; setup logs on the VM</summary>
        <div style="margin-top:10px; display:flex; flex-direction:column; gap:6px;">
            <p style="margin:0 0 6px; color:var(--text-muted,#9ca3af);">SSH into the VM and run:</p>
            ${cmds.map(cmd => `<code style="display:block; padding:6px 10px; background:var(--bg-primary,#0f0f1a); border-radius:4px; font-family:var(--font-mono); white-space:pre-wrap; word-break:break-all;">${escapeHtml(cmd)}</code>`).join('')}
        </div>
    </details>`;
}

function renderVmMessage(msg) {
    const level = (msg.level ?? 0);
    const dotCls = level === 2 ? 'msg-dot-error' : level === 1 ? 'msg-dot-warn' : 'msg-dot-info';
    const ts = new Date(msg.timestamp);
    const abs = ts.toLocaleString();
    const rel = formatMsgAge(ts);
    const src = escapeHtml(msg.source ?? 'system');
    const text = escapeHtml(msg.text ?? '');

    return `
        <div class="vm-msg-row">
            <div class="vm-msg-left">
                <div class="vm-msg-dot ${dotCls}"></div>
                <div class="vm-msg-line"></div>
            </div>
            <div class="vm-msg-content">
                <div class="vm-msg-meta">
                    <span class="vm-msg-source">${src}</span>
                    <span class="vm-msg-time" title="${escapeHtml(abs)}">${escapeHtml(rel)}</span>
                </div>
                <div class="vm-msg-text">${text}</div>
            </div>
        </div>`;
}

function formatMsgAge(date) {
    const secs = Math.floor((Date.now() - date.getTime()) / 1000);
    if (secs < 60) return `${secs}s ago`;
    if (secs < 3600) return `${Math.floor(secs / 60)}m ago`;
    if (secs < 86400) return `${Math.floor(secs / 3600)}h ago`;
    return `${Math.floor(secs / 86400)}d ago`;
}
