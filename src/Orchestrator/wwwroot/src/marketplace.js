// ============================================
// MARKETPLACE MODULE
// Node discovery, search, and detail views
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
}

/**
 * Load the marketplace page (featured nodes by default)
 */
export async function loadMarketplace() {
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
            // Show featured section, hide results
            document.getElementById('mp-featured-section').style.display = '';
            document.getElementById('mp-results-section').style.display = 'none';
        } else {
            container.innerHTML = '<p style="text-align: center; color: #6b7280; padding: 40px; grid-column: 1 / -1;">No featured nodes available</p>';
        }
    } catch (error) {
        console.error('[Marketplace] Failed to load featured nodes:', error);
        container.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 40px; grid-column: 1 / -1;">Failed to load featured nodes. Please try again.</p>';
    }
}

/**
 * Search marketplace nodes with current filter values
 */
export async function searchMarketplace() {
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
        console.error('[Marketplace] Search failed:', error);
        resultsContainer.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 40px; grid-column: 1 / -1;">Search failed. Please try again.</p>';
    }
}

/**
 * Clear all marketplace filters and show featured nodes
 */
export function clearMarketplaceFilters() {
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

        const description = node.description
            ? escapeHtmlFn(node.description)
            : '<span style="color: var(--text-muted); font-style: italic;">No description provided</span>';

        return `
            <div class="mp-node-card" onclick="openNodeDetail('${escapeHtmlFn(node.nodeId)}')">
                <div class="mp-card-header">
                    <div>
                        <div class="mp-node-name">${escapeHtmlFn(node.operatorName || node.nodeId.substring(0, 12))}</div>
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
                        <span class="mp-spec-label">Storage</span>
                        <span class="mp-spec-value">${storageGB} GB</span>
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

                <div class="mp-tags">
                    ${gpuBadge}
                    ${caps.hasNvmeStorage ? '<span class="mp-tag nvme">NVMe</span>' : ''}
                    ${caps.highBandwidth ? '<span class="mp-tag">High BW</span>' : ''}
                    ${tagsHtml}
                </div>

                <div class="mp-card-footer">
                    <div class="mp-price">
                        <span class="mp-price-value">$${(node.basePrice || 0).toFixed(4)}</span>
                        <span class="mp-price-unit">USDC / point / hr</span>
                    </div>
                    <div class="mp-uptime">
                        <span class="mp-uptime-value ${uptimeClass}">${(node.uptimePercentage || 0).toFixed(2)}%</span>
                        <span class="mp-uptime-label">Uptime (${node.totalVmsHosted || 0} VMs hosted)</span>
                    </div>
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
                        <div class="node-detail-row">
                            <span class="node-detail-label">GPU</span>
                            <span class="node-detail-value">${escapeHtmlFn(caps.gpuModel || 'Yes')}${caps.gpuCount > 1 ? ' x' + caps.gpuCount : ''}</span>
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
                        <div class="node-detail-section-title">Pricing & Info</div>
                        <div class="node-detail-row">
                            <span class="node-detail-label">Base Price</span>
                            <span class="node-detail-value" style="color: var(--accent-primary);">$${(node.basePrice || 0).toFixed(4)} / pt / hr</span>
                        </div>
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
        console.error('[Marketplace] Failed to load node detail:', error);
        body.innerHTML = '<p style="text-align: center; color: #ef4444; padding: 20px;">Failed to load node details</p>';
    }
}
