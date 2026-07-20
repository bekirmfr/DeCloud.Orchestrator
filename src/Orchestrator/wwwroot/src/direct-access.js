/**
 * Direct Access (Smart Port Allocation) Module
 * Manages TCP/UDP port forwarding for direct VM access
 */

import { escapeHtml, showToast as sharedShowToast } from './utils.js';

// Wrapper that delegates to window.api when present so 401-refresh logic and
// the configurable orchestrator URL are respected.
async function authFetch(path, options = {}) {
    if (window.api) return window.api(path, options);
    const token = localStorage.getItem('authToken');
    const headers = { ...(options.headers || {}) };
    if (token) headers['Authorization'] = `Bearer ${token}`;
    return fetch(path, { ...options, headers });
}

// Common service templates
const COMMON_SERVICES = {
    ssh: { port: 22, protocol: 'TCP', label: 'SSH', icon: '🔐' },
    rdp: { port: 3389, protocol: 'TCP', label: 'RDP', icon: '🖥️' },
    mysql: { port: 3306, protocol: 'TCP', label: 'MySQL', icon: '🗄️' },
    postgresql: { port: 5432, protocol: 'TCP', label: 'PostgreSQL', icon: '🐘' },
    mongodb: { port: 27017, protocol: 'TCP', label: 'MongoDB', icon: '🍃' },
    redis: { port: 6379, protocol: 'TCP', label: 'Redis', icon: '⚡' },
    minecraft: { port: 25565, protocol: 'Both', label: 'Minecraft', icon: '⛏️' },
    shadowsocks: { port: 8388, protocol: 'Both', label: 'Shadowsocks', icon: '🔒' },
    wireguard: { port: 51820, protocol: 'UDP', label: 'WireGuard', icon: '🔐' },
    http: { port: 80, protocol: 'TCP', label: 'HTTP', icon: '🌐' },
    https: { port: 443, protocol: 'TCP', label: 'HTTPS', icon: '🔒' },
};

/**
 * Get direct access info for a VM
 */
export async function getDirectAccessInfo(vmId) {
    try {
        const response = await authFetch(`/api/vms/${vmId}/direct-access`);

        if (!response.ok) {
            if (response.status === 404) {
                return null; // No direct access configured yet
            }
            throw new Error(`HTTP ${response.status}`);
        }

        const body = await response.json();
        return body?.data ?? body;
    } catch (error) {
        console.error('Error fetching direct access info:', error);
        throw error;
    }
}

/**
 * Allocate a port for direct access
 */
export async function allocatePort(vmId, vmPort, protocol = 1, label = null) {
    try {
        const response = await authFetch(`/api/vms/${vmId}/direct-access/ports`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ vmPort, protocol, label })
        });

        const data = await response.json();

        if (!response.ok) {
            throw new Error((data.error?.message ?? data.error) || `HTTP ${response.status}`);
        }

        return data?.data ?? data;
    } catch (error) {
        console.error('Error allocating port:', error);
        throw error;
    }
}

/**
 * Remove a port mapping
 */
export async function removePort(vmId, vmPort) {
    try {
        const response = await authFetch(`/api/vms/${vmId}/direct-access/ports/${vmPort}`, {
            method: 'DELETE'
        });

        if (!response.ok && response.status !== 204) {
            throw new Error(`HTTP ${response.status}`);
        }

        return true;
    } catch (error) {
        console.error('Error removing port:', error);
        throw error;
    }
}

/**
 * Quick-add a common service
 */
export async function quickAddService(vmId, serviceName) {
    try {
        const response = await authFetch(`/api/vms/${vmId}/direct-access/quick-add`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ serviceName })
        });

        const data = await response.json();

        if (!response.ok) {
            throw new Error((data.error?.message ?? data.error) || `HTTP ${response.status}`);
        }

        return data?.data ?? data;
    } catch (error) {
        console.error('Error quick-adding service:', error);
        throw error;
    }
}

/**
 * Get list of available services
 */
export async function getAvailableServices(vmId) {
    try {
        const response = await authFetch(`/api/vms/${vmId}/direct-access/services`);

        if (!response.ok) {
            throw new Error(`HTTP ${response.status}`);
        }

        const body = await response.json();
        return body?.data ?? body;
    } catch (error) {
        console.error('Error fetching services:', error);
        return { services: [] };
    }
}

/**
 * Convert protocol number to string
 */
function protocolToString(protocol) {
    switch (protocol) {
        case 1: return 'TCP';
        case 2: return 'UDP';
        case 3: return 'Both';
        default: return 'Unknown';
    }
}

/**
 * Open Direct Access modal for a VM
 */
export async function openDirectAccessModal(vmId, vmName) {
    const modal = document.getElementById('direct-access-modal');
    const title = document.getElementById('direct-access-modal-title');
    const dnsInfo = document.getElementById('direct-access-dns-info');
    const portsContainer = document.getElementById('direct-access-ports');
    const quickAddContainer = document.getElementById('direct-access-quick-add');
    const loadingEl = document.getElementById('direct-access-loading');

    if (!modal) return;

    // Store current VM ID and name (so we don't recover vmName from title text)
    modal.dataset.vmId = vmId;
    modal.dataset.vmName = vmName;

    title.textContent = `Direct Access - ${vmName}`;

    // Show modal
    modal.style.display = 'flex';

    // Show loading
    loadingEl.style.display = 'block';
    dnsInfo.style.display = 'none';
    portsContainer.style.display = 'none';
    quickAddContainer.style.display = 'none';

    try {
        // Fetch direct access info
        const info = await getDirectAccessInfo(vmId);

        if (!info || !info.isDnsConfigured) {
            // No direct access configured yet
            dnsInfo.innerHTML = `
                <div class="direct-access-empty">
                    <svg width="48" height="48" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <circle cx="12" cy="12" r="10"/>
                        <line x1="12" y1="8" x2="12" y2="12"/>
                        <line x1="12" y1="16" x2="12.01" y2="16"/>
                    </svg>
                    <h3>Direct Access Not Configured</h3>
                    <p>Add your first port mapping to enable direct TCP/UDP access to this VM.</p>
                </div>
            `;
            dnsInfo.style.display = 'block';
            portsContainer.innerHTML = '<p class="text-muted">No port mappings configured.</p>';
            portsContainer.style.display = 'block';
        } else {
            // Render DNS info
            renderDnsInfo(info.dnsName);

            // Render port mappings
            renderPortMappings(info.portMappings);
        }

        // Render quick-add buttons
        renderQuickAddButtons(vmId);

        loadingEl.style.display = 'none';
        if (window.openStaticModal) window.openStaticModal(modal);
        else modal.classList.add('active');
        document.body.style.overflow = 'hidden';
    } catch (error) {
        loadingEl.style.display = 'none';
        showToast('Failed to load direct access info: ' + error.message, 'error');
    }
}

/**
 * Close Direct Access modal
 */
export function closeDirectAccessModal() {
    const modal = document.getElementById('direct-access-modal');
    if (modal) {
        if (window.closeStaticModal) window.closeStaticModal(modal); else modal.classList.remove('active');
        document.body.style.overflow = '';
    }
}

/**
 * Render DNS information
 */
function renderDnsInfo(dnsName) {
    const dnsInfo = document.getElementById('direct-access-dns-info');
    dnsInfo.innerHTML = `
        <div class="direct-access-dns">
            <div class="dns-label">Public DNS:</div>
            <div class="dns-value">
                <code id="dns-name-code"></code>
                <button class="btn-icon" data-action="copy-dns" title="Copy DNS">
                    <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <rect x="9" y="9" width="13" height="13" rx="2" ry="2"/>
                        <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>
                    </svg>
                </button>
            </div>
        </div>
    `;
    dnsInfo.querySelector('#dns-name-code').textContent = dnsName;
    dnsInfo.querySelector('[data-action="copy-dns"]').addEventListener('click', () => copyToClipboardSafe(dnsName));
    dnsInfo.style.display = 'block';
}

function copyToClipboardSafe(text) {
    if (window.copyToClipboard) window.copyToClipboard(text);
    else navigator.clipboard?.writeText(text);
}

/**
 * Render port mappings table
 */
function renderPortMappings(mappings) {
    const container = document.getElementById('direct-access-ports');

    if (!mappings || mappings.length === 0) {
        container.innerHTML = '<p class="text-muted">No port mappings configured.</p>';
        container.style.display = 'block';
        return;
    }

    const html = `
        <table class="ports-table">
            <thead>
                <tr>
                    <th>Service</th>
                    <th>VM Port</th>
                    <th>Public Port</th>
                    <th>Protocol</th>
                    <th>Connection</th>
                    <th>Actions</th>
                </tr>
            </thead>
            <tbody>
                ${mappings.map(mapping => `
                    <tr data-vm-port="${escapeHtml(mapping.vmPort)}" data-connection="${escapeHtml(mapping.connectionExample || '')}">
                        <td>
                            <strong>${escapeHtml(mapping.label || 'Custom')}</strong>
                        </td>
                        <td><code>${escapeHtml(mapping.vmPort)}</code></td>
                        <td><code>${escapeHtml(mapping.publicPort)}</code></td>
                        <td><span class="protocol-badge">${escapeHtml(protocolToString(mapping.protocol))}</span></td>
                        <td>
                            <code class="connection-string">${escapeHtml(mapping.connectionExample)}</code>
                            <button class="btn-icon" data-action="copy-connection" title="Copy">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                    <rect x="9" y="9" width="13" height="13" rx="2" ry="2"/>
                                    <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/>
                                </svg>
                            </button>
                        </td>
                        <td>
                            <button class="btn btn-sm btn-danger" data-action="remove-port" title="Remove">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                                    <line x1="18" y1="6" x2="6" y2="18"/>
                                    <line x1="6" y1="6" x2="18" y2="18"/>
                                </svg>
                            </button>
                        </td>
                    </tr>
                `).join('')}
            </tbody>
        </table>
    `;

    container.innerHTML = html;
    container.addEventListener('click', portsTableClickHandler);
    container.style.display = 'block';
}

function portsTableClickHandler(e) {
    const btn = e.target.closest('[data-action]');
    if (!btn) return;
    const row = btn.closest('[data-vm-port]');
    if (!row) return;
    if (btn.dataset.action === 'copy-connection') {
        copyToClipboardSafe(row.dataset.connection || '');
    } else if (btn.dataset.action === 'remove-port') {
        window.removeDirectAccessPort(row.dataset.vmPort);
    }
}

/**
 * Render quick-add service buttons
 */
function renderQuickAddButtons(vmId) {
    const container = document.getElementById('direct-access-quick-add');

    const popularServices = ['ssh', 'mysql', 'postgresql', 'mongodb', 'redis', 'minecraft', 'shadowsocks', 'wireguard'];

    const html = `
        <div class="quick-add-section">
            <h4>Quick Add Service</h4>
            <div class="quick-add-buttons">
                ${popularServices.map(service => {
        const config = COMMON_SERVICES[service];
        return `
                        <button class="quick-add-btn" data-action="quick-add" data-service="${escapeHtml(service)}" title="${escapeHtml(config.label)} - Port ${config.port}">
                            <span class="service-icon">${escapeHtml(config.icon)}</span>
                            <span class="service-name">${escapeHtml(config.label)}</span>
                            <span class="service-port">${config.port}</span>
                        </button>
                    `;
    }).join('')}
            </div>
        </div>

        <div class="custom-port-section">
            <h4>Custom Port</h4>
            <div class="custom-port-form">
                <input type="number" id="custom-vm-port" placeholder="VM Port (e.g., 8080)" min="1" max="65535" />
                <select id="custom-protocol">
                    <option value="1">TCP</option>
                    <option value="2">UDP</option>
                    <option value="3">Both</option>
                </select>
                <input type="text" id="custom-label" placeholder="Label (optional)" />
                <button class="btn btn-primary" data-action="add-custom">Add Port</button>
            </div>
        </div>
    `;

    container.innerHTML = html;
    container.addEventListener('click', (e) => {
        const btn = e.target.closest('[data-action]');
        if (!btn) return;
        if (btn.dataset.action === 'quick-add') {
            window.quickAddDirectAccessService(vmId, btn.dataset.service);
        } else if (btn.dataset.action === 'add-custom') {
            window.addCustomDirectAccessPort(vmId);
        }
    });
    container.style.display = 'block';
}

/**
 * Quick-add a service
 */
window.quickAddDirectAccessService = async function (vmId, serviceName) {
    try {
        showToast(`Adding ${COMMON_SERVICES[serviceName]?.label || serviceName}...`, 'info');

        await quickAddService(vmId, serviceName);

        showToast(`${COMMON_SERVICES[serviceName]?.label || serviceName} added successfully!`, 'success');

        // Refresh modal
        const modal = document.getElementById('direct-access-modal');
        const vmName = modal?.dataset?.vmName || '';
        await openDirectAccessModal(vmId, vmName);
    } catch (error) {
        showToast('Failed to add service: ' + error.message, 'error');
    }
};

/**
 * Add custom port
 */
window.addCustomDirectAccessPort = async function (vmId) {
    const vmPortInput = document.getElementById('custom-vm-port');
    const protocolSelect = document.getElementById('custom-protocol');
    const labelInput = document.getElementById('custom-label');

    const vmPort = parseInt(vmPortInput.value);
    const protocol = parseInt(protocolSelect.value);
    const label = labelInput.value.trim() || null;

    if (!vmPort || vmPort < 1 || vmPort > 65535) {
        showToast('Please enter a valid port number (1-65535)', 'error');
        return;
    }

    try {
        showToast('Adding port mapping...', 'info');

        await allocatePort(vmId, vmPort, protocol, label);

        showToast('Port mapping added successfully!', 'success');

        // Clear inputs
        vmPortInput.value = '';
        labelInput.value = '';

        // Refresh modal
        const modal = document.getElementById('direct-access-modal');
        const vmName = modal?.dataset?.vmName || '';
        await openDirectAccessModal(vmId, vmName);
    } catch (error) {
        showToast('Failed to add port: ' + error.message, 'error');
    }
};

/**
 * Remove a port mapping
 */
window.removeDirectAccessPort = async function (vmPort) {
    const modal = document.getElementById('direct-access-modal');
    const vmId = modal.dataset.vmId;

    if (!confirm(`Remove port mapping for port ${vmPort}?`)) {
        return;
    }

    try {
        showToast('Removing port mapping...', 'info');

        await removePort(vmId, vmPort);

        showToast('Port mapping removed successfully!', 'success');

        // Refresh modal
        const vmName = document.getElementById('direct-access-modal-title').textContent.replace('Direct Access - ', '');
        await openDirectAccessModal(vmId, vmName);
    } catch (error) {
        showToast('Failed to remove port: ' + error.message, 'error');
    }
};

/**
 * Copy to clipboard utility
 */
window.copyToClipboard = function (text) {
    navigator.clipboard.writeText(text).then(() => {
        showToast('Copied to clipboard!', 'success');
    }).catch(err => {
        console.error('Failed to copy:', err);
        showToast('Failed to copy to clipboard', 'error');
    });
};

const showToast = sharedShowToast;

// Export functions to window for onclick handlers
window.openDirectAccessModal = openDirectAccessModal;
window.closeDirectAccessModal = closeDirectAccessModal;