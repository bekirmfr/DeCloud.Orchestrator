// ============================================================================
// My Templates Module
// Create, manage, and publish user-owned VM templates
// ============================================================================

import { ethers } from 'ethers';

let myTemplates = [];

// Escrow ABI for author earnings (nodeWithdraw / nodePendingPayouts)
const ESCROW_AUTHOR_ABI = [
    "function nodePendingPayouts(address) view returns (uint256)",
    "function nodeWithdraw() external",
    "function nodeWithdrawAmount(uint256 amount) external"
];

function api(endpoint, options = {}) {
    if (!window.api) throw new Error('API function not available');
    return window.api(endpoint, options);
}

function escapeHtml(text) {
    if (!text) return '';
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

function showToast(type, message) {
    if (window.showToast) window.showToast(type, message);
    else console.log(`[Toast] ${type}: ${message}`);
}

// ═══════════════════════════════════════════════════════════════════════════
// LOAD & RENDER
// ═══════════════════════════════════════════════════════════════════════════

export async function initMyTemplates() {
    console.log('[My Templates] Initializing...');
    await loadMyTemplates();
    renderMyTemplates();
}

async function loadMyTemplates() {
    try {
        const response = await api('/api/marketplace/templates/my');
        if (!response.ok) {
            if (response.status === 401) {
                myTemplates = [];
                return;
            }
            throw new Error('Failed to load templates');
        }
        const data = await response.json();
        myTemplates = Array.isArray(data) ? data : (data.data || []);
        console.log(`[My Templates] Loaded ${myTemplates.length} templates`);
    } catch (error) {
        console.error('[My Templates] Failed to load:', error);
        myTemplates = [];
    }
}

function renderMyTemplates() {
    const container = document.getElementById('my-templates-grid');
    if (!container) return;

    if (myTemplates.length === 0) {
        container.innerHTML = `
            <div class="empty-state">
                <div class="empty-icon">📦</div>
                <h3>No templates yet</h3>
                <p>Create your first template and share it with the community</p>
                <button class="btn btn-primary" onclick="window.myTemplates.showCreateModal()">
                    Create Template
                </button>
            </div>
        `;
        return;
    }

    container.innerHTML = myTemplates.map(t => createMyTemplateCard(t)).join('');
}

function createMyTemplateCard(template) {
    const statusClass = template.status === 'Published' ? 'status-published' :
                       template.status === 'Draft' ? 'status-draft' : 'status-archived';
    const statusLabel = template.status || 'Draft';

    const visibilityIcon = template.visibility === 'Private' ? '🔒' : '🌐';
    const priceText = template.pricingModel === 'PerDeploy' && template.templatePrice > 0
        ? `${template.templatePrice} USDC`
        : 'Free';

    const rating = template.averageRating || 0;
    const stars = renderStars(rating);
    const reviewCount = template.totalReviews || 0;

    return `
        <div class="my-template-card">
            <div class="my-template-header">
                <span class="template-status-badge ${statusClass}">${statusLabel}</span>
                <span class="template-visibility-badge">${visibilityIcon} ${template.visibility || 'Public'}</span>
            </div>
            <div class="my-template-body">
                <h3 class="my-template-name">${escapeHtml(template.name)}</h3>
                <p class="my-template-desc">${escapeHtml(template.description)}</p>
                <div class="my-template-meta">
                    <span>${priceText}</span>
                    <span>${stars} (${reviewCount})</span>
                    <span>${template.deploymentCount || 0} deploys</span>
                </div>
            </div>
            <div class="my-template-actions">
                ${template.status === 'Draft' ? `
                    <button class="btn btn-sm btn-primary" onclick="window.myTemplates.publishTemplate('${template.id}')">
                        Publish
                    </button>
                ` : ''}
                <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.editTemplate('${template.id}')">
                    Edit
                </button>
                <button class="btn btn-sm btn-danger" onclick="window.myTemplates.deleteTemplate('${template.id}')">
                    Delete
                </button>
            </div>
        </div>
    `;
}

function renderStars(rating) {
    const full = Math.floor(rating);
    const half = rating - full >= 0.5 ? 1 : 0;
    const empty = 5 - full - half;
    return '<span class="stars">' +
        '★'.repeat(full) +
        (half ? '½' : '') +
        '<span class="stars-empty">' + '★'.repeat(empty) + '</span>' +
        '</span>';
}

// ═══════════════════════════════════════════════════════════════════════════
// CREATE TEMPLATE
// ═══════════════════════════════════════════════════════════════════════════

export function showCreateModal() {
    let modal = document.getElementById('create-template-modal');
    if (!modal) {
        createTemplateModal();
        modal = document.getElementById('create-template-modal');
    }
    resetCreateForm();
    modal.classList.add('active');
    document.body.style.overflow = 'hidden';
}

function closeCreateModal() {
    const modal = document.getElementById('create-template-modal');
    if (modal) {
        modal.classList.remove('active');
        document.body.style.overflow = '';
    }
}

function createTemplateModal() {
    const modal = document.createElement('div');
    modal.className = 'modal-overlay';
    modal.id = 'create-template-modal';
    modal.innerHTML = `
        <div class="modal modal-large">
            <div class="modal-header">
                <h2 class="modal-title" id="create-template-modal-title">Create Template</h2>
                <button class="modal-close" onclick="window.myTemplates.closeCreateModal()">
                    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                        <line x1="18" y1="6" x2="6" y2="18" />
                        <line x1="6" y1="6" x2="18" y2="18" />
                    </svg>
                </button>
            </div>
            <div class="modal-body" style="max-height: 70vh; overflow-y: auto;">
                <input type="hidden" id="ct-template-id" value="">

                <!-- Basic Info -->
                <div class="form-section">
                    <h3 class="form-section-title">Basic Info</h3>
                    <div class="form-group">
                        <label class="form-label">Template Name *</label>
                        <input type="text" class="form-input" id="ct-name" placeholder="My Awesome Template">
                    </div>
                    <div class="form-group">
                        <label class="form-label">Slug *</label>
                        <input type="text" class="form-input" id="ct-slug" placeholder="my-awesome-template">
                        <p class="form-help">URL-friendly identifier. Lowercase, hyphens only.</p>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Short Description *</label>
                        <textarea class="form-input" id="ct-description" rows="2" placeholder="Brief description of what this template does"></textarea>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Long Description</label>
                        <textarea class="form-input" id="ct-long-description" rows="4" placeholder="Detailed description (supports markdown)"></textarea>
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Category</label>
                            <select class="form-input" id="ct-category">
                                <option value="dev-tools">Dev Tools</option>
                                <option value="ai-ml">AI/ML</option>
                                <option value="databases">Databases</option>
                                <option value="web-apps">Web Apps</option>
                                <option value="privacy-security">Privacy & Security</option>
                            </select>
                        </div>
                        <div class="form-group">
                            <label class="form-label">Version</label>
                            <input type="text" class="form-input" id="ct-version" value="1.0.0" placeholder="1.0.0">
                        </div>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Tags (comma-separated)</label>
                        <input type="text" class="form-input" id="ct-tags" placeholder="docker, linux, web">
                    </div>
                </div>

                <!-- VM Configuration -->
                <div class="form-section">
                    <h3 class="form-section-title">VM Configuration</h3>
                    <div class="form-group">
                        <label class="form-label">Base Image</label>
                        <select class="form-input" id="ct-image">
                            <option value="ubuntu-22.04">Ubuntu 22.04</option>
                            <option value="ubuntu-24.04">Ubuntu 24.04</option>
                            <option value="debian-12">Debian 12</option>
                        </select>
                    </div>
                    <div class="form-group">
                        <label class="form-label">Cloud-Init Script *</label>
                        <textarea class="form-input" id="ct-cloudinit" rows="10" placeholder="#cloud-config&#10;package_update: true&#10;packages:&#10;  - nginx" style="font-family: var(--font-mono); font-size: 12px;"></textarea>
                        <p class="form-help">Cloud-init configuration to set up the VM. Use {{PASSWORD}}, {{SSH_KEY}} variables.</p>
                    </div>
                </div>

                <!-- Minimum Specs -->
                <div class="form-section">
                    <h3 class="form-section-title">Minimum Specifications</h3>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">CPU Cores</label>
                            <input type="number" class="form-input" id="ct-min-cpu" value="1" min="1" max="32">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Memory (MB)</label>
                            <input type="number" class="form-input" id="ct-min-memory" value="512" min="256" step="256">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Disk (GB)</label>
                            <input type="number" class="form-input" id="ct-min-disk" value="10" min="5">
                        </div>
                    </div>
                    <div class="form-group">
                        <label class="filter-label">
                            <input type="checkbox" id="ct-requires-gpu">
                            <span>Requires GPU</span>
                        </label>
                    </div>
                </div>

                <!-- Exposed Ports -->
                <div class="form-section">
                    <h3 class="form-section-title">Exposed Ports</h3>
                    <div id="ct-ports-list"></div>
                    <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.addPort()" type="button">
                        + Add Port
                    </button>
                </div>

                <!-- Visibility & Pricing -->
                <div class="form-section">
                    <h3 class="form-section-title">Visibility & Pricing</h3>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Visibility</label>
                            <select class="form-input" id="ct-visibility">
                                <option value="Public">Public - visible in marketplace</option>
                                <option value="Private">Private - only you can deploy</option>
                            </select>
                        </div>
                        <div class="form-group">
                            <label class="form-label">Pricing</label>
                            <select class="form-input" id="ct-pricing" onchange="window.myTemplates.onPricingChange()">
                                <option value="Free">Free</option>
                                <option value="PerDeploy">Per Deploy (USDC)</option>
                            </select>
                        </div>
                    </div>
                    <div class="form-group" id="ct-price-group" style="display:none;">
                        <label class="form-label">Price per Deploy (USDC)</label>
                        <input type="number" class="form-input" id="ct-price" value="0" min="0.01" max="1000" step="0.01">
                        <p class="form-help">You receive 85% of the price. 15% platform fee.</p>
                    </div>
                    <div class="form-group" id="ct-wallet-group" style="display:none;">
                        <label class="form-label">Revenue Wallet Address</label>
                        <input type="text" class="form-input" id="ct-revenue-wallet" placeholder="0x...">
                        <p class="form-help">Wallet to receive template earnings. Defaults to your connected wallet.</p>
                    </div>
                </div>

                <!-- Source -->
                <div class="form-section">
                    <h3 class="form-section-title">Links (Optional)</h3>
                    <div class="form-group">
                        <label class="form-label">Source / Documentation URL</label>
                        <input type="text" class="form-input" id="ct-source-url" placeholder="https://github.com/...">
                    </div>
                </div>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" onclick="window.myTemplates.closeCreateModal()">Cancel</button>
                <button class="btn btn-primary" id="ct-submit-btn" onclick="window.myTemplates.submitTemplate()">
                    Create as Draft
                </button>
            </div>
        </div>
    `;
    document.body.appendChild(modal);
}

function resetCreateForm() {
    const fields = ['ct-template-id', 'ct-name', 'ct-slug', 'ct-description',
        'ct-long-description', 'ct-tags', 'ct-cloudinit', 'ct-source-url'];
    fields.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.value = '';
    });
    document.getElementById('ct-version').value = '1.0.0';
    document.getElementById('ct-min-cpu').value = '1';
    document.getElementById('ct-min-memory').value = '512';
    document.getElementById('ct-min-disk').value = '10';
    document.getElementById('ct-requires-gpu').checked = false;
    document.getElementById('ct-visibility').value = 'Public';
    document.getElementById('ct-pricing').value = 'Free';
    document.getElementById('ct-price').value = '0';
    document.getElementById('ct-price-group').style.display = 'none';
    document.getElementById('ct-wallet-group').style.display = 'none';
    document.getElementById('ct-ports-list').innerHTML = '';
    document.getElementById('ct-submit-btn').textContent = 'Create as Draft';
    document.getElementById('create-template-modal-title').textContent = 'Create Template';
}

export function onPricingChange() {
    const pricing = document.getElementById('ct-pricing').value;
    const isPaid = pricing === 'PerDeploy';
    document.getElementById('ct-price-group').style.display = isPaid ? '' : 'none';
    document.getElementById('ct-wallet-group').style.display = isPaid ? '' : 'none';

    // Auto-fill wallet
    if (isPaid && !document.getElementById('ct-revenue-wallet').value) {
        const signer = window.ethersSigner ? window.ethersSigner() : null;
        if (signer) {
            signer.getAddress().then(addr => {
                document.getElementById('ct-revenue-wallet').value = addr;
            });
        }
    }
}

export function addPort() {
    const list = document.getElementById('ct-ports-list');
    const idx = list.children.length;
    const row = document.createElement('div');
    row.className = 'form-row port-row';
    row.style.marginBottom = '8px';
    row.innerHTML = `
        <div class="form-group" style="flex:1">
            <input type="number" class="form-input" placeholder="Port" data-port-idx="${idx}" data-port-field="port" min="1" max="65535">
        </div>
        <div class="form-group" style="flex:1">
            <select class="form-input" data-port-idx="${idx}" data-port-field="protocol">
                <option value="tcp">TCP</option>
                <option value="udp">UDP</option>
            </select>
        </div>
        <div class="form-group" style="flex:2">
            <input type="text" class="form-input" placeholder="Description" data-port-idx="${idx}" data-port-field="description">
        </div>
        <button class="btn btn-sm btn-danger" onclick="this.parentElement.remove()" type="button" style="align-self:center;">X</button>
    `;
    list.appendChild(row);
}

function collectPorts() {
    const rows = document.querySelectorAll('#ct-ports-list .port-row');
    const ports = [];
    rows.forEach(row => {
        const port = row.querySelector('[data-port-field="port"]')?.value;
        const protocol = row.querySelector('[data-port-field="protocol"]')?.value || 'tcp';
        const description = row.querySelector('[data-port-field="description"]')?.value || '';
        if (port) {
            ports.push({ port: parseInt(port), protocol, description });
        }
    });
    return ports;
}

function buildTemplatePayload() {
    const minCpu = parseInt(document.getElementById('ct-min-cpu').value) || 1;
    const minMemMb = parseInt(document.getElementById('ct-min-memory').value) || 512;
    const minDiskGb = parseInt(document.getElementById('ct-min-disk').value) || 10;
    const tags = document.getElementById('ct-tags').value
        .split(',').map(t => t.trim()).filter(t => t);

    return {
        name: document.getElementById('ct-name').value.trim(),
        slug: document.getElementById('ct-slug').value.trim().toLowerCase(),
        description: document.getElementById('ct-description').value.trim(),
        longDescription: document.getElementById('ct-long-description').value.trim() || null,
        category: document.getElementById('ct-category').value,
        version: document.getElementById('ct-version').value.trim() || '1.0.0',
        tags: tags,
        imageId: document.getElementById('ct-image').value,
        cloudInitTemplate: document.getElementById('ct-cloudinit').value,
        requiresGpu: document.getElementById('ct-requires-gpu').checked,
        minimumSpec: {
            virtualCpuCores: minCpu,
            memoryBytes: minMemMb * 1024 * 1024,
            diskBytes: minDiskGb * 1024 * 1024 * 1024
        },
        recommendedSpec: {
            virtualCpuCores: Math.max(minCpu, 2),
            memoryBytes: Math.max(minMemMb, 2048) * 1024 * 1024,
            diskBytes: Math.max(minDiskGb, 20) * 1024 * 1024 * 1024
        },
        exposedPorts: collectPorts(),
        visibility: document.getElementById('ct-visibility').value,
        pricingModel: document.getElementById('ct-pricing').value,
        templatePrice: parseFloat(document.getElementById('ct-price').value) || 0,
        authorRevenueWallet: document.getElementById('ct-revenue-wallet').value.trim() || null,
        sourceUrl: document.getElementById('ct-source-url').value.trim() || null
    };
}

export async function submitTemplate() {
    const btn = document.getElementById('ct-submit-btn');
    const templateId = document.getElementById('ct-template-id').value;
    const isEdit = !!templateId;
    const payload = buildTemplatePayload();

    if (!payload.name || !payload.slug || !payload.description || !payload.cloudInitTemplate) {
        showToast('error', 'Please fill in all required fields (name, slug, description, cloud-init)');
        return;
    }

    btn.disabled = true;
    btn.textContent = isEdit ? 'Saving...' : 'Creating...';

    try {
        let response;
        if (isEdit) {
            response = await api(`/api/marketplace/templates/${templateId}`, {
                method: 'PUT',
                body: JSON.stringify(payload)
            });
        } else {
            response = await api('/api/marketplace/templates/create', {
                method: 'POST',
                body: JSON.stringify(payload)
            });
        }

        if (!response.ok) {
            const err = await response.json();
            throw new Error(err.error || 'Failed to save template');
        }

        showToast('success', isEdit ? 'Template updated!' : 'Template created as draft!');
        closeCreateModal();
        await loadMyTemplates();
        renderMyTemplates();
    } catch (error) {
        showToast('error', error.message);
    } finally {
        btn.disabled = false;
        btn.textContent = isEdit ? 'Save Changes' : 'Create as Draft';
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EDIT / PUBLISH / DELETE
// ═══════════════════════════════════════════════════════════════════════════

export async function editTemplate(templateId) {
    const template = myTemplates.find(t => t.id === templateId);
    if (!template) return;

    showCreateModal();

    document.getElementById('create-template-modal-title').textContent = 'Edit Template';
    document.getElementById('ct-submit-btn').textContent = 'Save Changes';
    document.getElementById('ct-template-id').value = template.id;
    document.getElementById('ct-name').value = template.name || '';
    document.getElementById('ct-slug').value = template.slug || '';
    document.getElementById('ct-description').value = template.description || '';
    document.getElementById('ct-long-description').value = template.longDescription || '';
    document.getElementById('ct-category').value = template.category || 'dev-tools';
    document.getElementById('ct-version').value = template.version || '1.0.0';
    document.getElementById('ct-tags').value = (template.tags || []).join(', ');
    document.getElementById('ct-image').value = template.imageId || 'ubuntu-22.04';
    document.getElementById('ct-cloudinit').value = template.cloudInitTemplate || '';
    document.getElementById('ct-requires-gpu').checked = template.requiresGpu || false;
    document.getElementById('ct-source-url').value = template.sourceUrl || '';

    // Specs
    const minSpec = template.minimumSpec || {};
    document.getElementById('ct-min-cpu').value = minSpec.virtualCpuCores || 1;
    document.getElementById('ct-min-memory').value = Math.round((minSpec.memoryBytes || 536870912) / (1024 * 1024));
    document.getElementById('ct-min-disk').value = Math.round((minSpec.diskBytes || 10737418240) / (1024 * 1024 * 1024));

    // Visibility & pricing
    document.getElementById('ct-visibility').value = template.visibility || 'Public';
    document.getElementById('ct-pricing').value = template.pricingModel || 'Free';
    document.getElementById('ct-price').value = template.templatePrice || 0;
    document.getElementById('ct-revenue-wallet').value = template.authorRevenueWallet || '';
    onPricingChange();

    // Ports
    const portsList = document.getElementById('ct-ports-list');
    portsList.innerHTML = '';
    (template.exposedPorts || []).forEach(p => {
        addPort();
        const rows = portsList.querySelectorAll('.port-row');
        const row = rows[rows.length - 1];
        row.querySelector('[data-port-field="port"]').value = p.port;
        row.querySelector('[data-port-field="protocol"]').value = p.protocol || 'tcp';
        row.querySelector('[data-port-field="description"]').value = p.description || '';
    });
}

export async function publishTemplate(templateId) {
    if (!confirm('Publish this template to the marketplace? It will become visible to all users.')) return;

    try {
        const response = await api(`/api/marketplace/templates/${templateId}/publish`, {
            method: 'PATCH'
        });

        if (!response.ok) {
            const err = await response.json();
            throw new Error(err.error || 'Failed to publish');
        }

        showToast('success', 'Template published!');
        await loadMyTemplates();
        renderMyTemplates();
    } catch (error) {
        showToast('error', error.message);
    }
}

export async function deleteTemplate(templateId) {
    if (!confirm('Delete this template permanently? This cannot be undone.')) return;

    try {
        const response = await api(`/api/marketplace/templates/${templateId}`, {
            method: 'DELETE'
        });

        if (!response.ok) {
            const err = await response.json();
            throw new Error(err.error || 'Failed to delete');
        }

        showToast('success', 'Template deleted');
        await loadMyTemplates();
        renderMyTemplates();
    } catch (error) {
        showToast('error', error.message);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EARNINGS / WITHDRAWAL
// ═══════════════════════════════════════════════════════════════════════════

export async function loadEarnings() {
    const container = document.getElementById('my-templates-earnings');
    if (!container) return;

    try {
        const signer = window.ethersSigner ? window.ethersSigner() : null;
        if (!signer) {
            container.innerHTML = '<p class="text-muted">Connect wallet to view earnings</p>';
            return;
        }

        // Get deposit config for contract address
        const configResponse = await api('/api/payment/deposit-info');
        if (!configResponse.ok) {
            container.innerHTML = '<p class="text-muted">Payment system unavailable</p>';
            return;
        }
        const configResult = await configResponse.json();
        const config = configResult.data || configResult;

        if (!config.escrowContractAddress) {
            container.innerHTML = '<p class="text-muted">Escrow contract not configured</p>';
            return;
        }

        const escrow = new ethers.Contract(
            config.escrowContractAddress,
            ESCROW_AUTHOR_ABI,
            signer
        );

        const address = await signer.getAddress();
        const pendingRaw = await escrow.nodePendingPayouts(address);
        const pending = ethers.formatUnits(pendingRaw, 6); // USDC 6 decimals

        container.innerHTML = `
            <div class="earnings-card">
                <div class="earnings-info">
                    <span class="earnings-label">Pending Earnings</span>
                    <span class="earnings-value">${parseFloat(pending).toFixed(2)} USDC</span>
                </div>
                <button class="btn btn-sm btn-primary"
                    onclick="window.myTemplates.withdrawEarnings()"
                    ${parseFloat(pending) <= 0 ? 'disabled' : ''}>
                    Withdraw
                </button>
            </div>
        `;
    } catch (error) {
        console.error('[My Templates] Failed to load earnings:', error);
        container.innerHTML = '<p class="text-muted">Unable to load earnings</p>';
    }
}

export async function withdrawEarnings() {
    try {
        const signer = window.ethersSigner ? window.ethersSigner() : null;
        if (!signer) {
            showToast('error', 'Wallet not connected');
            return;
        }

        const configResponse = await api('/api/payment/deposit-info');
        const configResult = await configResponse.json();
        const config = configResult.data || configResult;

        const escrow = new ethers.Contract(
            config.escrowContractAddress,
            ESCROW_AUTHOR_ABI,
            signer
        );

        showToast('info', 'Please confirm the withdrawal in your wallet...');
        const tx = await escrow.nodeWithdraw();
        showToast('info', 'Waiting for confirmation...');
        await tx.wait();
        showToast('success', 'Withdrawal successful!');
        await loadEarnings();
    } catch (error) {
        if (error.code === 'ACTION_REJECTED' || error.code === 4001) {
            showToast('warning', 'Transaction rejected');
        } else {
            showToast('error', `Withdrawal failed: ${error.message}`);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// EXPORTS
// ═══════════════════════════════════════════════════════════════════════════

window.myTemplates = {
    showCreateModal,
    closeCreateModal,
    submitTemplate,
    editTemplate,
    publishTemplate,
    deleteTemplate,
    addPort,
    onPricingChange,
    withdrawEarnings,
    loadEarnings
};
