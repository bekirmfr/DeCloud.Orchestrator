// ============================================
// MARKETPLACE MODULE
// Node discovery, search, and detail views
// Uses /api/marketplace endpoints for rich node data
// ============================================

let orchestratorUrl = '';
let escapeHtmlFn = (text) => text;

/**
 * Initialize the marketplace module
 * @param {string} baseUrl - Orchestrator base URL
 * @param {function} escapeHtml - HTML escaping function from app.js
 */
export function initializeMarketplace(baseUrl, escapeHtml) {
    orchestratorUrl = baseUrl;
    escapeHtmlFn = escapeHtml;
    setupNodeCardDelegation();
}

/**
 * Set up delegated click handlers for node cards.
 * Listens on the grid containers so we don't need inline onclick handlers.
 */
function setupNodeCardDelegation() {
    document.addEventListener('click', (e) => {
        // Don't open detail if clicking buttons
        if (e.target.closest('button')) {
            return;
        }

        const card = e.target.closest('.mp-node-card[data-node-id]');
        if (card) {
            openNodeDetail(card.dataset.nodeId);
        }
    });

    // Expose functions globally for inline onclick handlers
    window.nodeMarketplace = {
        openNodeDetail,
        deployToNode,
        clearNodeSelection
    };
}

/**
 * Load the Nodes page - fetches featured nodes by default
 */
export async function loadNodes() {
    await loadFeaturedNodes();
}

/**
 * Load featured nodes from the marketplace API
 */
async function loadFeaturedNodes() {
    const container = document.getElementById('mp-featured-nodes');
    if (!container) return;

    container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">Loading featured nodes...</p>';

    try {
        const response = await fetch(`${orchestratorUrl}/api/marketplace/nodes/featured`);
        const data = await response.json();

        if (data.success && data.data) {
            renderNodeCards(container, data.data);
            document.getElementById('mp-featured-section').style.display = '';
            document.getElementById('mp-results-section').style.display = 'none';
        } else {
            container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">No featured nodes available</p>';
        }
    } catch (error) {
        console.error('[Nodes] Failed to load featured nodes:', error);
        container.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 40px; grid-column: 1 / -1;">Failed to load featured nodes. Please try again.</p>';
    }
}

/**
 * Search nodes with current filter values
 */
export async function searchNodes() {
    const resultsContainer = document.getElementById('mp-search-results');
    const resultsSection = document.getElementById('mp-results-section');
    const resultsCount = document.getElementById('mp-results-count');

    if (!resultsContainer) return;

    // Build query params from filters
    const params = new URLSearchParams();

    const tags = document.getElementById('mp-filter-tags').value.trim();
    if (tags) params.set('tags', tags);

    const region = document.getElementById('mp-filter-region').value.trim();
    if (region) params.set('region', region);

    const sortBy = document.getElementById('mp-filter-sort').value;
    if (sortBy) params.set('sortBy', sortBy);

    const maxPrice = document.getElementById('mp-filter-price').value;
    if (maxPrice) params.set('maxPricePerPoint', maxPrice);

    const minUptime = document.getElementById('mp-filter-uptime').value;
    if (minUptime) params.set('minUptimePercent', minUptime);

    const minCapacity = document.getElementById('mp-filter-capacity').value;
    if (minCapacity) params.set('minAvailableComputePoints', minCapacity);

    const requiresGpu = document.getElementById('mp-filter-gpu').checked;
    if (requiresGpu) params.set('requiresGpu', 'true');

    const onlineOnly = document.getElementById('mp-filter-online').checked;
    params.set('onlineOnly', onlineOnly.toString());

    params.set('sortDescending', 'true');

    // Show results section
    resultsSection.style.display = '';
    resultsContainer.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">Searching...</p>';

    try {
        const response = await fetch(`${orchestratorUrl}/api/marketplace/nodes?${params.toString()}`);
        const data = await response.json();

        if (data.success && data.data) {
            const nodes = data.data;
            resultsCount.textContent = `${nodes.length} node${nodes.length !== 1 ? 's' : ''} found`;
            renderNodeCards(resultsContainer, nodes);

            if (nodes.length === 0) {
                resultsContainer.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">No nodes match your criteria. Try adjusting your filters.</p>';
            }

            // Hide featured when showing search results
            document.getElementById('mp-featured-section').style.display = 'none';
        } else {
            resultsContainer.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 40px; grid-column: 1 / -1;">Search failed. Please try again.</p>';
        }
    } catch (error) {
        console.error('[Nodes] Search failed:', error);
        resultsContainer.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 40px; grid-column: 1 / -1;">Search failed. Please try again.</p>';
    }
}

/**
 * Clear all node filters and show featured nodes
 */
export function clearNodeFilters() {
    document.getElementById('mp-filter-tags').value = '';
    document.getElementById('mp-filter-region').value = '';
    document.getElementById('mp-filter-sort').value = 'uptime';
    document.getElementById('mp-filter-price').value = '';
    document.getElementById('mp-filter-uptime').value = '';
    document.getElementById('mp-filter-capacity').value = '';
    document.getElementById('mp-filter-gpu').checked = false;
    document.getElementById('mp-filter-online').checked = true;

    // Hide search results, show featured
    document.getElementById('mp-results-section').style.display = 'none';
    document.getElementById('mp-featured-section').style.display = '';
    loadFeaturedNodes();
}

/**
 * Render an array of NodeAdvertisement objects as cards
 */
function renderNodeCards(container, nodes) {
    if (!nodes || nodes.length === 0) {
        container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">No nodes found</p>';
        return;
    }

    container.innerHTML = nodes.map(node => {
        const caps = node.capabilities || {};
        const memoryGB = ((caps.totalMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(1);
        const storageGB = ((caps.totalStorageBytes || 0) / (1024 * 1024 * 1024)).toFixed(0);
        const availMemGB = ((node.availableMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(1);
        const availStorageGB = ((node.availableStorageBytes || 0) / (1024 * 1024 * 1024)).toFixed(0);

        const uptimeClass = node.uptimePercentage >= 99 ? 'excellent' :
            node.uptimePercentage >= 95 ? 'good' : 'poor';

        // Build tags HTML
        const tagsHtml = (node.tags || []).map(tag => {
            let tagClass = '';
            if (tag.toLowerCase().includes('gpu')) tagClass = 'gpu';
            else if (tag.toLowerCase().includes('nvme')) tagClass = 'nvme';
            return `<span class="mp-tag ${tagClass}">${escapeHtmlFn(tag)}</span>`;
        }).join('');

        // GPU badge
        const gpuBadge = caps.hasGpu
            ? `<span class="mp-tag gpu">${escapeHtmlFn(caps.gpuModel || 'GPU')}${caps.gpuCount > 1 ? ' x' + caps.gpuCount : ''}</span>`
            : '';

        // GPU status icon for card header
        const gpuIcon = caps.hasGpu
            ? `<span class="mp-gpu-indicator" title="${escapeHtmlFn(caps.gpuModel || 'GPU Available')}">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                    <rect x="2" y="7" width="20" height="14" rx="2" />
                    <path d="M2 11h20M7 7V4M12 7V4M17 7V4" />
                </svg>
               </span>`
            : '';

        const description = node.description
            ? escapeHtmlFn(node.description)
            : '<span style="color: var(--text-muted); font-style: italic;">No description provided</span>';

        return `
            <div class="mp-node-card${node.isOnline ? '' : ' offline'}" data-node-id="${escapeHtmlFn(node.nodeId)}">
                <div class="mp-card-header">
                    <div>
                        <div class="mp-node-name">
                            ${escapeHtmlFn(node.operatorName || node.nodeId.substring(0, 12))}
                            ${gpuIcon}
                        </div>
                        <div class="mp-node-region">${escapeHtmlFn(node.region || 'Unknown')}${node.zone ? ' / ' + escapeHtmlFn(node.zone) : ''}</div>
                    </div>
                    <span class="mp-node-status ${node.isOnline ? 'online' : 'offline'}">
                        <span class="mp-status-dot"></span>
                        ${node.isOnline ? 'Online' : 'Offline'}
                    </span>
                </div>

                <div class="mp-node-desc">${description}</div>

                <div class="mp-specs-row">
                    <div class="mp-spec">
                        <span class="mp-spec-label">CPU</span>
                        <span class="mp-spec-value">${caps.cpuCores || 0} cores</span>
                    </div>
                    <div class="mp-spec">
                        <span class="mp-spec-label">Memory</span>
                        <span class="mp-spec-value">${memoryGB} GB</span>
                    </div>
                    <div class="mp-spec">
                        <span class="mp-spec-label">${caps.hasGpu ? 'GPU' : 'Storage'}</span>
                        <span class="mp-spec-value">${caps.hasGpu ? escapeHtmlFn(caps.gpuModel?.split(' ').slice(-1)[0] || 'Yes') : storageGB + ' GB'}</span>
                    </div>
                </div>

                <div class="mp-specs-row">
                    <div class="mp-spec">
                        <span class="mp-spec-label">Available Points</span>
                        <span class="mp-spec-value">${node.availableComputePoints || 0}</span>
                    </div>
                    <div class="mp-spec">
                        <span class="mp-spec-label">Free Memory</span>
                        <span class="mp-spec-value">${availMemGB} GB</span>
                    </div>
                    <div class="mp-spec">
                        <span class="mp-spec-label">Free Storage</span>
                        <span class="mp-spec-value">${availStorageGB} GB</span>
                    </div>
                </div>

                ${caps.hasGpu ? `
                <div class="mp-gpu-highlight">
                    <div class="mp-gpu-icon">
                        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">
                            <rect x="2" y="7" width="20" height="14" rx="2" />
                            <path d="M2 11h20M7 7V4M12 7V4M17 7V4" />
                        </svg>
                    </div>
                    <div class="mp-gpu-info">
                        <div class="mp-gpu-model">${escapeHtmlFn(caps.gpuModel || 'GPU Available')}${caps.gpuCount > 1 ? ' x' + caps.gpuCount : ''}</div>
                        ${caps.gpuMemoryBytes ? `<div class="mp-gpu-memory">${((caps.gpuMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(0)} GB VRAM</div>` : ''}
                    </div>
                </div>
                ` : ''}

                <div class="mp-tags">
                    ${caps.hasNvmeStorage ? '<span class="mp-tag nvme">NVMe</span>' : ''}
                    ${caps.highBandwidth ? '<span class="mp-tag">High BW</span>' : ''}
                    ${tagsHtml}
                </div>

                <div class="mp-card-footer">
                    <div class="mp-price">
                        ${node.pricing && node.pricing.hasCustomPricing
                ? `<span class="mp-price-value">$${node.pricing.cpuPerHour.toFixed(3)}</span>
                               <span class="mp-price-unit">CPU/hr</span>`
                : `<span class="mp-price-value">Default</span>
                               <span class="mp-price-unit">Platform rates</span>`
            }
                    </div>
                    <div class="mp-uptime">
                        <span class="mp-uptime-value ${uptimeClass}">${(node.uptimePercentage || 0).toFixed(2)}%</span>
                        <span class="mp-uptime-label">Uptime (${node.totalVmsHosted || 0} VMs hosted)</span>
                    </div>
                </div>

                <div class="mp-card-actions">
                    <button class="mp-btn-primary" onclick="window.nodeMarketplace.deployToNode('${escapeHtmlFn(node.nodeId)}', '${escapeHtmlFn(node.operatorName || node.nodeId.substring(0, 12))}')" ${!node.isOnline ? 'disabled' : ''}>
                        🚀 Deploy VM
                    </button>
                    <button class="mp-btn-secondary" onclick="window.nodeMarketplace.openNodeDetail('${escapeHtmlFn(node.nodeId)}')">
                        📊 Details
                    </button>
                </div>
            </div>
        `;
    }).join('');
}

/**
 * Open node detail modal
 */
export async function openNodeDetail(nodeId) {
    const modal = document.getElementById('node-detail-modal');
    const body = document.getElementById('node-detail-body');
    const title = document.getElementById('node-detail-title');

    modal.classList.add('active');
    body.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 20px;">Loading node details...</p>';

    try {
        const response = await fetch(`${orchestratorUrl}/api/marketplace/nodes/${encodeURIComponent(nodeId)}`);
        const data = await response.json();

        if (data.success && data.data) {
            const node = data.data;
            const caps = node.capabilities || {};
            const memoryGB = ((caps.totalMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(1);
            const storageGB = ((caps.totalStorageBytes || 0) / (1024 * 1024 * 1024)).toFixed(0);
            const availMemGB = ((node.availableMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(1);
            const availStorageGB = ((node.availableStorageBytes || 0) / (1024 * 1024 * 1024)).toFixed(0);
            const uptimeClass = node.uptimePercentage >= 99 ? 'excellent' :
                node.uptimePercentage >= 95 ? 'good' : 'poor';

            title.textContent = escapeHtmlFn(node.operatorName || 'Node Details');

            const tagsHtml = (node.tags || []).map(tag => {
                let tagClass = '';
                if (tag.toLowerCase().includes('gpu')) tagClass = 'gpu';
                else if (tag.toLowerCase().includes('nvme')) tagClass = 'nvme';
                return `<span class="mp-tag ${tagClass}">${escapeHtmlFn(tag)}</span>`;
            }).join('');

            body.innerHTML = `
                ${node.description ? `<div class="node-detail-section full-width"><div class="node-detail-desc">${escapeHtmlFn(node.description)}</div></div>` : ''}

                <div class="node-detail-grid">
                    <div class="node-detail-section">
                        <div class="node-detail-section-title">Hardware</div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">CPU</span>
                            <span class="node-detail-value">${escapeHtmlFn(caps.cpuModel || 'Unknown')} (${caps.cpuCores || 0} cores)</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Memory</span>
                            <span class="node-detail-value">${memoryGB} GB</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Storage</span>
                            <span class="node-detail-value">${storageGB} GB${caps.hasNvmeStorage ? ' (NVMe)' : ''}</span>
                        </div>
                        ${caps.hasGpu ? `
                        <div class="node-detail-row" style="background: rgba(102, 126, 234, 0.08); margin: 8px -12px; padding: 8px 12px; border-radius: 6px;">
                            <span class="node-detail-label" style="color: var(--accent-primary); font-weight: 600;">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" style="vertical-align: middle; margin-right: 4px;">
                                    <rect x="2" y="7" width="20" height="14" rx="2" />
                                    <path d="M2 11h20M7 7V4M12 7V4M17 7V4" />
                                </svg>
                                GPU
                            </span>
                            <span class="node-detail-value" style="font-weight: 600;">
                                ${escapeHtmlFn(caps.gpuModel || 'Available')}${caps.gpuCount > 1 ? ' x' + caps.gpuCount : ''}
                                ${caps.gpuMemoryBytes ? `<span style="color: var(--text-muted); font-weight: 400; font-size: 13px;"> • ${((caps.gpuMemoryBytes || 0) / (1024 * 1024 * 1024)).toFixed(0)} GB VRAM</span>` : ''}
                            </span>
                        </div>` : ''}
                        <div class="node-detail-row">
                            <span class="node-detail-label">Bandwidth</span>
                            <span class="node-detail-value">${caps.highBandwidth ? '> 1 Gbps' : 'Standard'}</span>
                        </div>
                    </div>

                    <div class="node-detail-section">
                        <div class="node-detail-section-title">Availability</div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Status</span>
                            <span class="node-detail-value" style="color: ${node.isOnline ? 'var(--accent-primary)' : 'var(--text-muted)'}">
                                ${node.isOnline ? 'Online' : 'Offline'}
                            </span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Compute Points</span>
                            <span class="node-detail-value">${node.availableComputePoints || 0} available</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Free Memory</span>
                            <span class="node-detail-value">${availMemGB} GB</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Free Storage</span>
                            <span class="node-detail-value">${availStorageGB} GB</span>
                        </div>
                    </div>

                    <div class="node-detail-section">
                        <div class="node-detail-section-title">Reputation</div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Uptime</span>
                            <span class="node-detail-value ${uptimeClass}">${(node.uptimePercentage || 0).toFixed(2)}%</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">VMs Hosted</span>
                            <span class="node-detail-value">${node.totalVmsHosted || 0}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Successful Completions</span>
                            <span class="node-detail-value">${node.successfulVmCompletions || 0}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Registered</span>
                            <span class="node-detail-value">${node.registeredAt ? new Date(node.registeredAt).toLocaleDateString() : 'Unknown'}</span>
                        </div>
                    </div>

                    <div class="node-detail-section">
                        <div class="node-detail-section-title">Pricing (USDC/hr)</div>
                        ${node.pricing && node.pricing.hasCustomPricing ? `
                        <div class="node-detail-row">
                            <span class="node-detail-label">CPU (per core)</span>
                            <span class="node-detail-value" style="color: var(--accent-primary);">$${node.pricing.cpuPerHour.toFixed(4)}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Memory (per GB)</span>
                            <span class="node-detail-value" style="color: var(--accent-primary);">$${node.pricing.memoryPerGbPerHour.toFixed(4)}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Storage (per GB)</span>
                            <span class="node-detail-value" style="color: var(--accent-primary);">$${node.pricing.storagePerGbPerHour.toFixed(5)}</span>
                        </div>
                        ${node.pricing.gpuPerHour > 0 ? `
                        <div class="node-detail-row">
                            <span class="node-detail-label">GPU (per unit)</span>
                            <span class="node-detail-value" style="color: var(--accent-primary);">$${node.pricing.gpuPerHour.toFixed(4)}</span>
                        </div>` : ''}
                        ` : `
                        <div class="node-detail-row">
                            <span class="node-detail-label">Rates</span>
                            <span class="node-detail-value" style="color: var(--text-muted);">Platform defaults</span>
                        </div>
                        `}
                        <div class="node-detail-row">
                            <span class="node-detail-label">Region</span>
                            <span class="node-detail-value">${escapeHtmlFn(node.region || 'Unknown')}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Zone</span>
                            <span class="node-detail-value">${escapeHtmlFn(node.zone || 'N/A')}</span>
                        </div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Node ID</span>
                            <span class="node-detail-value" style="font-size: 11px;">${escapeHtmlFn(node.nodeId)}</span>
                        </div>
                    </div>
                </div>

                ${(node.tags && node.tags.length > 0) ? `
                <div style="margin-top: 16px;">
                    <div class="node-detail-section-title">Tags</div>
                    <div class="mp-tags">${tagsHtml}</div>
                </div>` : ''}
            `;
        } else {
            body.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 20px;">Node not found</p>';
        }
    } catch (error) {
        console.error('[Nodes] Failed to load node detail:', error);
        body.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 20px;">Failed to load node details</p>';
    }
}

/**
 * Deploy VM to specific node
 * Pre-populates VM creation form with selected node
 */
export function deployToNode(nodeId, nodeName) {
    console.log('[Marketplace] Deploying to node:', nodeId, nodeName);

    // Pre-populate node selection
    const nodeIdInput = document.getElementById('vm-target-node-id');
    if (nodeIdInput) {
        nodeIdInput.value = nodeId;
    }

    // Open VM creation modal
    if (window.openCreateVMModal) {
        window.openCreateVMModal();

        // Small delay to ensure modal is open before showing banner
        setTimeout(() => {
            // Show node info banner
            showSelectedNodeBanner(nodeId, nodeName);

            // Focus on VM name input
            const vmNameInput = document.getElementById('vm-name');
            if (vmNameInput) {
                vmNameInput.focus();
            }
        }, 100);
    }
}

/**
 * Show banner indicating selected node
 */
function showSelectedNodeBanner(nodeId, nodeName) {
    // Banner is already in the modal HTML, just update it
    const banner = document.getElementById('selected-node-banner');
    if (!banner) return;

    banner.innerHTML = `
        <div>
            <strong>🎯 Deploying to:</strong> ${escapeHtmlFn(nodeName)}
            <span style="opacity: 0.8; font-size: 12px; margin-left: 8px;">(${escapeHtmlFn(nodeId.substring(0, 12))}...)</span>
        </div>
        <button onclick="window.nodeMarketplace.clearNodeSelection()" style="
            background: rgba(255,255,255,0.2);
            border: none;
            color: white;
            padding: 4px 12px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 13px;
        ">
            Choose Different Node
        </button>
    `;

    banner.style.cssText = `
        background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
        color: white;
        padding: 12px 16px;
        border-radius: 8px;
        margin-bottom: 16px;
        display: flex;
        justify-content: space-between;
        align-items: center;
        box-shadow: 0 2px 8px rgba(102, 126, 234, 0.3);
    `;
}

/**
 * Clear node selection
 */
export function clearNodeSelection() {
    if (window.app && window.app.state) {
        window.app.state.selectedNodeId = null;
        window.app.state.selectedNodeName = null;
    }

    const nodeIdInput = document.getElementById('vm-target-node-id');
    if (nodeIdInput) {
        nodeIdInput.value = '';
    }

    const banner = document.getElementById('selected-node-banner');
    if (banner) {
        banner.style.display = 'none';
    }
}
