// ============================================================================
// Custom domains (central ingress) module
// Manages custom-domain CNAME mappings to a VM. Static modal lives in
// index.html (#custom-domains-modal); this module fills it in and handles
// add/verify/remove.
// ============================================================================

import { escapeHtml, sanitizeUrl, showToast, makeModalAccessible } from './utils.js';

function api(endpoint, options = {}) {
    if (!window.api) throw new Error('API function not available');
    return window.api(endpoint, options);
}

function copyToClipboard(text) {
    if (window.copyToClipboard) return window.copyToClipboard(text);
    return navigator.clipboard?.writeText(text);
}

const DOMAIN_STATUS_LABELS = {
    PendingDns: { text: 'DNS Pending', class: 'status-pending' },
    Active: { text: 'Active', class: 'status-running' },
    Paused: { text: 'Paused', class: 'status-stopped' },
    Error: { text: 'Error', class: 'status-error' }
};

let _a11yCleanup = null;
let _listClickHandler = null;

export async function openCustomDomainsModal(vmId, vmName) {
    const modal = document.getElementById('custom-domains-modal');
    const title = document.getElementById('custom-domains-modal-title');
    const listContainer = document.getElementById('custom-domains-list');
    const addContainer = document.getElementById('custom-domains-add');
    const loadingEl = document.getElementById('custom-domains-loading');

    if (!modal) return;

    modal.dataset.vmId = vmId;
    modal.dataset.vmName = vmName;
    title.textContent = `Custom Domains - ${vmName}`;

    modal.style.display = 'flex';
    loadingEl.style.display = 'block';
    listContainer.style.display = 'none';
    addContainer.style.display = 'none';

    // Set up a11y once per open
    if (_a11yCleanup) _a11yCleanup();
    _a11yCleanup = makeModalAccessible(modal, closeCustomDomainsModal);

    try {
        const response = await api(`/api/central-ingress/vm/${vmId}/domains`);
        const result = await response.json();

        if (!result.success) throw new Error(result.error || 'Failed to load domains');

        const domains = result.data || [];
        renderCustomDomainsList(domains, vmId);
        renderCustomDomainsAddForm(vmId);

        loadingEl.style.display = 'none';
        modal.classList.add('active');
        document.body.style.overflow = 'hidden';
    } catch (error) {
        loadingEl.style.display = 'none';
        listContainer.innerHTML = `<p class="text-muted" style="text-align: center; padding: 20px;">Failed to load domains: ${escapeHtml(error.message)}</p>`;
        listContainer.style.display = 'block';
        renderCustomDomainsAddForm(vmId);
        modal.classList.add('active');
        document.body.style.overflow = 'hidden';
    }
}

export function closeCustomDomainsModal() {
    const modal = document.getElementById('custom-domains-modal');
    if (!modal) return;
    modal.classList.remove('active');
    modal.style.display = '';
    document.body.style.overflow = '';
    _a11yCleanup?.();
    _a11yCleanup = null;
}

function renderCustomDomainsList(domains, vmId) {
    const container = document.getElementById('custom-domains-list');

    if (!domains || domains.length === 0) {
        container.innerHTML = `
            <div class="direct-access-empty">
                <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <circle cx="12" cy="12" r="10"/>
                    <line x1="2" y1="12" x2="22" y2="12"/>
                    <path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>
                </svg>
                <h3>No Custom Domains</h3>
                <p>Add a custom domain below to route your own domain to this VM.</p>
            </div>
        `;
        container.style.display = 'block';
        return;
    }

    const html = `
        <table class="ports-table">
            <thead>
                <tr><th>Domain</th><th>Port</th><th>Status</th><th>Actions</th></tr>
            </thead>
            <tbody>
                ${domains.map(d => {
        const statusInfo = DOMAIN_STATUS_LABELS[d.status] || DOMAIN_STATUS_LABELS.Error;
        const canVerify = d.status === 'PendingDns' || d.status === 'Error';
        return `
                    <tr data-domain-id="${escapeHtml(d.id)}" data-domain-name="${escapeHtml(d.domain)}" data-dns-target="${escapeHtml(d.dnsTarget || '')}">
                        <td>
                            ${d.status === 'Active' && d.publicUrl
                ? `<a href="${escapeHtml(sanitizeUrl(d.publicUrl))}" target="_blank" rel="noopener noreferrer" class="domain-link">${escapeHtml(d.domain)}</a>`
                : `<code>${escapeHtml(d.domain)}</code>`}
                        </td>
                        <td><code>${escapeHtml(d.targetPort)}</code></td>
                        <td><span class="status-badge ${statusInfo.class}">${escapeHtml(statusInfo.text)}</span></td>
                        <td>
                            <div class="table-actions">
                                ${canVerify ? `
                                    <button class="btn btn-sm btn-primary" data-action="verify" title="Verify DNS">
                                        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                            <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/>
                                            <polyline points="22 4 12 14.01 9 11.01"/>
                                        </svg>
                                        Verify
                                    </button>` : ''}
                                <button class="btn btn-sm btn-danger" data-action="remove" title="Remove">
                                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                        <line x1="18" y1="6" x2="6" y2="18"/>
                                        <line x1="6" y1="6" x2="18" y2="18"/>
                                    </svg>
                                </button>
                            </div>
                        </td>
                    </tr>
                    ${canVerify ? `
                    <tr>
                        <td colspan="4">
                            <div class="dns-instructions">
                                <span class="dns-instructions-label">DNS Setup:</span>
                                <code>CNAME ${escapeHtml(d.domain)} &rarr; ${escapeHtml(d.dnsTarget || '')}</code>
                                <button class="btn-icon" data-action="copy-dns" data-target="${escapeHtml(d.dnsTarget || '')}" title="Copy DNS target">
                                    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                        <rect x="9" y="9" width="13" height="13" rx="2" ry="2"/>
                                        <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>
                                    </svg>
                                </button>
                            </div>
                        </td>
                    </tr>` : ''}
                `;
    }).join('')}
            </tbody>
        </table>
    `;

    container.innerHTML = html;
    container.style.display = 'block';

    if (_listClickHandler) container.removeEventListener('click', _listClickHandler);
    _listClickHandler = (e) => {
        const btn = e.target.closest('[data-action]');
        if (!btn) return;
        const row = btn.closest('[data-domain-id]');
        if (btn.dataset.action === 'copy-dns') {
            copyToClipboard(btn.dataset.target || '');
            return;
        }
        if (!row) return;
        const domainId = row.dataset.domainId;
        const domainName = row.dataset.domainName;
        if (btn.dataset.action === 'verify') verifyCustomDomain(vmId, domainId);
        else if (btn.dataset.action === 'remove') removeCustomDomain(vmId, domainId, domainName);
    };
    container.addEventListener('click', _listClickHandler);
}

function renderCustomDomainsAddForm(vmId) {
    const container = document.getElementById('custom-domains-add');

    container.innerHTML = `
        <div class="custom-port-section">
            <h4>Add Custom Domain</h4>
            <div class="custom-port-form">
                <input type="text" id="custom-domain-input" placeholder="my.awesome.app" style="flex: 2;" />
                <input type="number" id="custom-domain-port" placeholder="Port (80)" min="1" max="65535" value="80" style="flex: 0.5;" />
                <button class="btn btn-primary" data-action="add">Add Domain</button>
            </div>
        </div>
    `;
    container.style.display = 'block';
    container.querySelector('[data-action="add"]').addEventListener('click', () => addCustomDomain(vmId));
}

export async function addCustomDomain(vmId) {
    const domainInput = document.getElementById('custom-domain-input');
    const portInput = document.getElementById('custom-domain-port');

    const domain = domainInput.value.trim();
    const targetPort = parseInt(portInput.value) || 80;

    if (!domain) {
        showToast('Please enter a domain name', 'error');
        return;
    }

    try {
        showToast('Adding custom domain...', 'info');
        const response = await api(`/api/central-ingress/vm/${vmId}/domains`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ domain, targetPort })
        });
        const result = await response.json();
        if (!result.success) throw new Error(result.error || 'Failed to add domain');

        showToast(`Domain ${domain} added! Configure DNS and click Verify.`, 'success');
        domainInput.value = '';
        await refreshCurrentModal(vmId);
    } catch (error) {
        showToast('Failed to add domain: ' + error.message, 'error');
    }
}

export async function verifyCustomDomain(vmId, domainId) {
    try {
        showToast('Verifying DNS...', 'info');
        const response = await api(`/api/central-ingress/vm/${vmId}/domains/${domainId}/verify`, { method: 'POST' });
        const result = await response.json();
        if (!result.success) throw new Error(result.error || 'Verification failed');

        const domain = result.data;
        if (domain.status === 'Active') {
            showToast(`Domain ${domain.domain} verified and active!`, 'success');
        } else {
            showToast(`DNS verification failed for ${domain.domain}. Check your CNAME record.`, 'error');
        }
        await refreshCurrentModal(vmId);
    } catch (error) {
        showToast('DNS verification failed: ' + error.message, 'error');
    }
}

export async function removeCustomDomain(vmId, domainId, domainName) {
    if (!confirm(`Remove custom domain ${domainName}?`)) return;

    try {
        showToast('Removing domain...', 'info');
        const response = await api(`/api/central-ingress/vm/${vmId}/domains/${domainId}`, { method: 'DELETE' });
        const result = await response.json();
        if (!result.success) throw new Error(result.error || 'Failed to remove');

        showToast(`Domain ${domainName} removed`, 'success');
        await refreshCurrentModal(vmId);
    } catch (error) {
        showToast('Failed to remove domain: ' + error.message, 'error');
    }
}

async function refreshCurrentModal(vmId) {
    const modal = document.getElementById('custom-domains-modal');
    const vmName = modal?.dataset?.vmName || '';
    await openCustomDomainsModal(vmId, vmName);
}
