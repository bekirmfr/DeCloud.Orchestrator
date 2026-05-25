// ============================================================================
// Template Detail Module
// View template details and deploy
// ============================================================================

import { escapeHtml, sanitizeUrl, showToast as sharedShowToast, isPerDeployPricing, renderMarkdown } from './utils.js';

let currentTemplate = null;

// ============================================
// TIER CONFIGURATIONS (must match app.js / backend enums)
// ============================================

// QualityTier enum: 0=Dedicated, 1=Standard, 2=Balanced, 3=Burstable
const DEPLOY_QUALITY_TIERS = {
    0: { name: 'Dedicated (Premium)', pointsPerVCpu: 8, priceMultiplier: 2.5 },
    1: { name: 'Standard', pointsPerVCpu: 4, priceMultiplier: 1.0 },
    2: { name: 'Balanced (Cost-Optimized)', pointsPerVCpu: 2, priceMultiplier: 0.6 },
    3: { name: 'Burstable (Lowest Cost)', pointsPerVCpu: 1, priceMultiplier: 0.4 }
};

// BandwidthTier enum: 0=Basic, 1=Standard, 2=Performance, 3=Unmetered
const DEPLOY_BANDWIDTH_TIERS = {
    0: { name: 'Basic (10 Mbps)', hourlyRate: 0.002 },
    1: { name: 'Standard (50 Mbps)', hourlyRate: 0.008 },
    2: { name: 'Performance (200 Mbps)', hourlyRate: 0.020 },
    3: { name: 'Unmetered', hourlyRate: 0.040 }
};

// Variables that are always resolved by registered platform resolvers.
// Any Static variable NOT in this set and without a resolverKey is user-supplied
// and must be collected from the user before deployment.
const PLATFORM_VARIABLE_NAMES = new Set([
    'HOSTNAME', 'VM_ID', 'VM_NAME', 'NODE_ID', 'ORCHESTRATOR_URL',
    'SSH_AUTHORIZED_KEYS_BLOCK', 'CA_PUBLIC_KEY',
    'PASSWORD_CONFIG_BLOCK', 'SSH_PASSWORD_AUTH', 'ADMIN_PASSWORD',
    'DECLOUD_ROLE', 'HOST_MACHINE_ID', 'PUBLIC_IP',
    'VARIABLE_SCOPES_BLOCK', 'TIMESTAMP',
]);

// VariableKind enum: 0=Static, 1=Dynamic
function getUserVariables(template) {
    if (!Array.isArray(template.variables)) return [];
    return template.variables.filter(v =>
        (v.kind === 0 || v.kind === 'Static') &&     // Static only (int or string enum)
        !PLATFORM_VARIABLE_NAMES.has(v.name)         // not platform-resolved
    );
}

/**
 * Helper function to call the global API
 */
function api(endpoint, options = {}) {
    if (!window.api) {
        throw new Error('API function not available');
    }
    return window.api(endpoint, options);
}

/**
 * Show template detail modal
 */
export async function showTemplateDetail(templateIdOrSlug) {
    console.log('[Template Detail] Loading template:', templateIdOrSlug);

    try {
        // Load template details
        const response = await api(`/api/marketplace/templates/${templateIdOrSlug}`);
        const data = await response.json();

        // API may return direct object or wrapped in data/success
        currentTemplate = data.data || data;

        // Render detail modal
        renderTemplateDetail(currentTemplate);

        // Show modal
        const modal = document.getElementById('template-detail-modal');
        if (modal) {
            if (window.openStaticModal) window.openStaticModal(modal);
            else modal.classList.add('active');
            document.body.style.overflow = 'hidden';
        }
    } catch (error) {
        console.error('[Template Detail] Failed to load template:', error);
        alert('Failed to load template details. Please try again.');
    }
}

/**
 * Close template detail modal
 */
export function closeTemplateDetail() {
    const modal = document.getElementById('template-detail-modal');
    if (modal) {
        if (window.closeStaticModal) window.closeStaticModal(modal); else modal.classList.remove('active');
        document.body.style.overflow = '';
    }
    currentTemplate = null;
}

/**
 * Render template detail modal content
 */
function renderTemplateDetail(template) {
    const container = document.getElementById('template-detail-content');
    if (!container) return;

    // Format specs
    const minCpu = template.minimumSpec?.virtualCpuCores || 0;
    const minMemory = (template.minimumSpec?.memoryBytes || 0) / (1024 * 1024 * 1024);
    const minDisk = (template.minimumSpec?.diskBytes || 0) / (1024 * 1024 * 1024);

    const recCpu = template.recommendedSpec?.virtualCpuCores || minCpu;
    const recMemory = (template.recommendedSpec?.memoryBytes || template.minimumSpec?.memoryBytes || 0) / (1024 * 1024 * 1024);
    const recDisk = (template.recommendedSpec?.diskBytes || template.minimumSpec?.diskBytes || 0) / (1024 * 1024 * 1024);

    // Format cost
    const costPerHour = template.estimatedCostPerHour || 0;
    const costText = costPerHour > 0 ? `$${costPerHour.toFixed(2)}/hr` : 'Free';
    const costPerDay = (costPerHour * 24).toFixed(2);
    const costPerMonth = (costPerHour * 24 * 30).toFixed(2);

    container.innerHTML = `
        <div class="template-detail-header">
            <div class="template-detail-icon">${getCategoryIcon(template.category)}</div>
            <div class="template-detail-title-group">
                <h2 class="template-detail-title">${escapeHtml(template.name)}</h2>
                <div class="template-detail-meta">
                    <span class="template-version">v${escapeHtml(template.version)}</span>
                    <span class="template-author">by ${escapeHtml(template.authorName || 'DeCloud')}</span>
                    ${template.isFeatured ? '<span class="badge-featured">⭐ Featured</span>' : ''}
                    ${template.requiresGpu ? '<span class="badge-gpu">🎮 Requires GPU</span>' : ''}
                </div>
            </div>
        </div>
        
        <div class="template-detail-description">
            ${escapeHtml(template.description)}
        </div>
        
        ${template.longDescription ? `
            <div class="template-detail-long-description">
                ${renderMarkdown(template.longDescription)}
            </div>
        ` : ''}
        
        <div class="template-detail-grid">
            <!-- Specifications -->
            <div class="detail-section">
                <h3 class="detail-section-title">💻 Specifications</h3>
                <div class="spec-comparison">
                    <div class="spec-column">
                        <h4>Minimum</h4>
                        <div class="spec-item">
                            <span class="spec-label">CPU:</span>
                            <span class="spec-value">${minCpu} cores</span>
                        </div>
                        <div class="spec-item">
                            <span class="spec-label">RAM:</span>
                            <span class="spec-value">${minMemory.toFixed(1)} GB</span>
                        </div>
                        <div class="spec-item">
                            <span class="spec-label">Disk:</span>
                            <span class="spec-value">${minDisk} GB</span>
                        </div>
                    </div>
                    <div class="spec-column">
                        <h4>Recommended</h4>
                        <div class="spec-item">
                            <span class="spec-label">CPU:</span>
                            <span class="spec-value">${recCpu} cores</span>
                        </div>
                        <div class="spec-item">
                            <span class="spec-label">RAM:</span>
                            <span class="spec-value">${recMemory.toFixed(1)} GB</span>
                        </div>
                        <div class="spec-item">
                            <span class="spec-label">Disk:</span>
                            <span class="spec-value">${recDisk} GB</span>
                        </div>
                    </div>
                </div>
            </div>
            
            <!-- Pricing -->
            <div class="detail-section">
                <h3 class="detail-section-title">💰 Pricing</h3>
                <div class="pricing-info">
                    <div class="price-item">
                        <span class="price-label">Per Hour:</span>
                        <span class="price-value">${costText}</span>
                    </div>
                    <div class="price-item">
                        <span class="price-label">Per Day:</span>
                        <span class="price-value">~$${costPerDay}</span>
                    </div>
                    <div class="price-item">
                        <span class="price-label">Per Month:</span>
                        <span class="price-value">~$${costPerMonth}</span>
                    </div>
                </div>
                <p class="price-note">Based on recommended specs. Actual cost depends on your configuration.</p>
            </div>
        </div>
        
        <!-- Exposed Ports -->
        ${template.exposedPorts && template.exposedPorts.length > 0 ? `
            <div class="detail-section">
                <h3 class="detail-section-title">🌐 Exposed Ports</h3>
                <div class="ports-list">
                    ${template.exposedPorts.map(port => `
                        <div class="port-item">
                            <span class="port-number">${port.port}</span>
                            <span class="port-protocol">${port.protocol.toUpperCase()}</span>
                            <span class="port-description">${escapeHtml(port.description)}</span>
                        </div>
                    `).join('')}
                </div>
            </div>
        ` : ''}
        
        <!-- Tags -->
        ${template.tags && template.tags.length > 0 ? `
            <div class="detail-section">
                <h3 class="detail-section-title">🏷️ Tags</h3>
                <div class="template-tags-detail">
                    ${template.tags.map(tag =>
        `<span class="tag-detail">${escapeHtml(tag)}</span>`
    ).join('')}
                </div>
            </div>
        ` : ''}
        
        <!-- Stats & Rating -->
        <div class="detail-section">
            <h3 class="detail-section-title">📊 Stats & Rating</h3>
            <div class="template-stats-detail">
                <div class="stat-detail">
                    <span class="stat-detail-label">Deployments:</span>
                    <span class="stat-detail-value">${template.deploymentCount || 0}</span>
                </div>
                <div class="stat-detail">
                    <span class="stat-detail-label">Rating:</span>
                    <span class="stat-detail-value">
                        ${renderStarsDetail(template.averageRating || 0)}
                        <span class="rating-text">${(template.averageRating || 0).toFixed(1)} (${template.totalReviews || 0} reviews)</span>
                    </span>
                </div>
                <div class="stat-detail">
                    <span class="stat-detail-label">Created:</span>
                    <span class="stat-detail-value">${formatDate(template.createdAt)}</span>
                </div>
                ${template.isCommunity ? `
                <div class="stat-detail">
                    <span class="stat-detail-label">Author:</span>
                    <span class="stat-detail-value">${escapeHtml(template.authorName || 'Community')}</span>
                </div>
                ` : ''}
            </div>
        </div>

        <!-- Template Price -->
        ${isPerDeployPricing(template.pricingModel) && template.templatePrice > 0 ? `
        <div class="detail-section">
            <h3 class="detail-section-title">💳 Template Fee</h3>
            <div class="template-fee-info">
                <span class="template-fee-amount">${template.templatePrice} USDC</span>
                <span class="template-fee-note">per deployment (charged from your escrow balance)</span>
            </div>
        </div>
        ` : ''}

        <!-- Reviews Section -->
        <div class="detail-section">
            <h3 class="detail-section-title">💬 Reviews</h3>
            <div id="template-reviews-list">
                <div class="loading-spinner">Loading reviews...</div>
            </div>
            <div id="template-review-form" style="margin-top: 16px; display: none;">
                <h4 style="margin-bottom: 8px;">Write a Review</h4>
                <div class="review-stars-input" id="review-stars-input" role="radiogroup" aria-label="Star rating">
                    ${[1, 2, 3, 4, 5].map(i => `<button type="button" class="review-star" data-value="${i}" role="radio" aria-checked="false" aria-label="${i} star${i === 1 ? '' : 's'}" onclick="window.templateDetail.setReviewRating(${i})">☆</button>`).join('')}
                </div>
                <textarea class="form-input" id="review-comment" rows="3" placeholder="Share your experience with this template..." style="margin-top: 8px;"></textarea>
                <button class="btn btn-sm btn-primary" onclick="window.templateDetail.submitReview()" style="margin-top: 8px;">
                    Submit Review
                </button>
            </div>
        </div>
        
        ${template.sourceUrl ? `
            <div class="detail-section">
                <h3 class="detail-section-title">🔗 Links</h3>
                <div class="template-links">
                    <a href="${escapeHtml(sanitizeUrl(template.sourceUrl))}" target="_blank" rel="noopener noreferrer" class="template-link">
                        📄 Source Code / Documentation
                    </a>
                </div>
            </div>
        ` : ''}
    `;

    // Update deploy button
    const deployButton = document.getElementById('template-deploy-btn');
    if (deployButton) {
        deployButton.onclick = () => openDeployTemplateModal(template);
    }

    // Load reviews async
    loadReviews(template.id);
}

/**
 * Open deployment modal for template
 */
export async function openDeployTemplateModal(template) {
    if (!template) template = currentTemplate;
    if (!template) return;

    console.log('[Template Detail] Opening deployment modal for:', template.name);

    // Close detail modal
    closeTemplateDetail();

    // Store template reference for validation
    currentTemplate = template;

    // Open deploy modal
    const modal = document.getElementById('deploy-template-modal');
    if (!modal) {
        console.error('[Template Detail] Deploy modal not found');
        return;
    }

    // Pre-fill form with template data
    document.getElementById('deploy-template-id').value = template.id;
    document.getElementById('deploy-vm-name').value = `${template.slug}-${Date.now().toString(36)}`;
    document.getElementById('deploy-template-name-display').textContent = template.name;

    // Determine minimum and recommended specs
    const minSpec = template.minimumSpec || {};
    const recSpec = template.recommendedSpec || minSpec;

    // Set min attributes and recommended values for resource inputs
    const cpuInput = document.getElementById('deploy-cpu');
    const memInput = document.getElementById('deploy-memory');
    const diskInput = document.getElementById('deploy-disk');

    const minCpu = minSpec.virtualCpuCores || 1;
    const minMemMb = Math.round((minSpec.memoryBytes || 536870912) / (1024 * 1024));
    const minDiskGb = Math.round((minSpec.diskBytes || 10737418240) / (1024 * 1024 * 1024));

    cpuInput.min = minCpu;
    cpuInput.value = recSpec.virtualCpuCores || minCpu;
    memInput.min = minMemMb;
    memInput.value = Math.round((recSpec.memoryBytes || minSpec.memoryBytes || 2147483648) / (1024 * 1024));
    diskInput.min = minDiskGb;
    diskInput.value = Math.round((recSpec.diskBytes || minSpec.diskBytes || 21474836480) / (1024 * 1024 * 1024));

    // Populate quality tier dropdown — only show tiers >= template minimum
    // QualityTier enum order: 0=Dedicated(best), 1=Standard, 2=Balanced, 3=Burstable(worst)
    // A higher enum value means more overcommit (lower quality).
    // Minimum tier from the template is the worst allowed (highest enum value).
    const minQualityTier = minSpec.qualityTier ?? 1; // default Standard
    const recQualityTier = recSpec.qualityTier ?? minQualityTier;
    const qualitySelect = document.getElementById('deploy-quality-tier');
    qualitySelect.innerHTML = '';
    for (let i = 0; i <= minQualityTier; i++) {
        const tier = DEPLOY_QUALITY_TIERS[i];
        if (!tier) continue;
        const opt = document.createElement('option');
        opt.value = i;
        opt.textContent = tier.name;
        if (i === recQualityTier) opt.selected = true;
        qualitySelect.appendChild(opt);
    }

    // Populate bandwidth tier dropdown — only show tiers >= template minimum
    // BandwidthTier enum order: 0=Basic(lowest), 1=Standard, 2=Performance, 3=Unmetered(highest)
    // Minimum tier from the template is the lowest allowed bandwidth.
    const minBandwidthTier = template.defaultBandwidthTier ?? 3; // default Unmetered
    const bwSelect = document.getElementById('deploy-bandwidth-tier');
    bwSelect.innerHTML = '';
    for (let i = minBandwidthTier; i <= 3; i++) {
        const tier = DEPLOY_BANDWIDTH_TIERS[i];
        if (!tier) continue;
        const opt = document.createElement('option');
        opt.value = i;
        opt.textContent = tier.name;
        if (i === minBandwidthTier) opt.selected = true;
        bwSelect.appendChild(opt);
    }

    // GPU: show section and pre-select mode + VRAM from template spec
    const gpuSection = document.getElementById('deploy-gpu-section');
    const gpuModeSelect = document.getElementById('deploy-gpu-mode');
    const gpuVramRow = document.getElementById('deploy-gpu-vram-row');
    const gpuVramInput = document.getElementById('deploy-gpu-vram');

    if (gpuSection) {
        // defaultGpuMode: 0=None, 1=Passthrough, 2=Proxied
        const defaultGpuMode = template.defaultGpuMode ?? 0;
        const requiresGpu = template.requiresGpu || defaultGpuMode !== 0;

        gpuSection.style.display = requiresGpu ? 'block' : 'none';

        if (gpuModeSelect) gpuModeSelect.value = String(defaultGpuMode);

        if (gpuVramRow)
            gpuVramRow.style.display = defaultGpuMode === 2 ? 'flex' : 'none';

        // Pre-fill VRAM from recommended spec, fall back to minimum, then 4 GB
        const recGpuVram = recSpec.gpuVramBytes || minSpec.gpuVramBytes || 0;
        if (gpuVramInput)
            gpuVramInput.value = recGpuVram > 0
                ? Math.round(recGpuVram / (1024 ** 3))
                : 4;
    }

    // Load available regions
    await loadAvailableRegions();

    // Reset zone selector
    const zoneSelect = document.getElementById('deploy-zone');
    if (zoneSelect) {
        zoneSelect.innerHTML = '<option value="">Any zone (default)</option>';
        zoneSelect.disabled = true;
    }

    // Update cost estimate
    updateDeployCostEstimate();
    // Render user-supplied variable inputs (template configuration section)
    renderDeployVariables(template);


    // Show modal
    if (window.openStaticModal) window.openStaticModal(modal);
    else modal.classList.add('active');
    document.body.style.overflow = 'hidden';
}

/**
 * Close deployment modal
 */
export function closeDeployTemplateModal() {
    const modal = document.getElementById('deploy-template-modal');
    if (modal) {
        if (window.closeStaticModal) window.closeStaticModal(modal); else modal.classList.remove('active');
        document.body.style.overflow = '';
    }
    const replicationSelect = document.getElementById('deploy-replication-factor');
    if (replicationSelect) replicationSelect.value = '3';
}

/**
 * Load available regions from online nodes
 */
async function loadAvailableRegions() {
    try {
        const response = await api('/api/nodes/regions?onlineOnly=true');
        const data = await response.json();

        const regions = data.data || data;
        const regionSelect = document.getElementById('deploy-region');

        if (!regionSelect) return;

        // Clear existing options except the default
        regionSelect.innerHTML = '<option value="">Any region (default)</option>';

        // Add region options
        regions.forEach(region => {
            const opt = document.createElement('option');
            opt.value = region.region;
            opt.textContent = `${region.region} (${region.nodeCount} nodes, ${region.availableComputePoints} compute points)`;
            regionSelect.appendChild(opt);
        });

        console.log('[Template Detail] Loaded {0} available regions', regions.length);
    } catch (error) {
        console.error('[Template Detail] Failed to load regions:', error);
    }
}

/**
 * Handle region change - load zones for selected region
 */
export async function onRegionChange() {
    const regionSelect = document.getElementById('deploy-region');
    const zoneSelect = document.getElementById('deploy-zone');

    if (!regionSelect || !zoneSelect) return;

    const selectedRegion = regionSelect.value;

    // Reset zone selector
    zoneSelect.innerHTML = '<option value="">Any zone (default)</option>';

    if (!selectedRegion) {
        zoneSelect.disabled = true;
        return;
    }

    // Load zones for selected region
    try {
        const response = await api(`/api/nodes/regions/${encodeURIComponent(selectedRegion)}/zones?onlineOnly=true`);
        const data = await response.json();

        const zones = data.data || data;

        if (zones.length > 0) {
            zones.forEach(zone => {
                const opt = document.createElement('option');
                opt.value = zone.zone;
                opt.textContent = `${zone.zone} (${zone.nodeCount} nodes, ${zone.availableComputePoints} compute points)`;
                zoneSelect.appendChild(opt);
            });

            zoneSelect.disabled = false;
            console.log('[Template Detail] Loaded {0} zones for region {1}', zones.length, selectedRegion);
        } else {
            zoneSelect.disabled = true;
            console.log('[Template Detail] No zones available for region {0}', selectedRegion);
        }
    } catch (error) {
        console.error('[Template Detail] Failed to load zones:', error);
        zoneSelect.disabled = true;
    }
}

/**
 * Deploy template
 */
export async function deployFromTemplate() {
    const templateId = document.getElementById('deploy-template-id').value;
    const vmName = document.getElementById('deploy-vm-name').value.trim();
    const cpu = parseInt(document.getElementById('deploy-cpu').value);
    const memoryMb = parseInt(document.getElementById('deploy-memory').value);
    const diskGb = parseInt(document.getElementById('deploy-disk').value);
    const qualityTier = parseInt(document.getElementById('deploy-quality-tier').value);
    const bandwidthTier = parseInt(document.getElementById('deploy-bandwidth-tier').value);
    const replicationFactor = parseInt(document.getElementById('deploy-replication-factor')?.value ?? '3');
    const region = document.getElementById('deploy-region')?.value || null;
    const zone = document.getElementById('deploy-zone')?.value || null;

    // GPU — read from the GPU Access section (hidden when template has no GPU requirement)
    const gpuMode = parseInt(document.getElementById('deploy-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('deploy-gpu-vram')?.value ?? '0');
    const gpuVramBytes = gpuMode === 2 && gpuVramGb > 0
        ? Math.round(gpuVramGb * 1024 ** 3)
        : null;

    // Client-side name validation (mirrors server-side VmNameService)
    const sanitized = window.sanitizeVmName ? window.sanitizeVmName(vmName) : vmName;
    const nameError = window.validateVmName ? window.validateVmName(sanitized) : (!vmName ? 'Please enter a VM name' : null);
    if (nameError) {
        alert(nameError);
        return;
    }

    if (!templateId) {
        alert('Template not selected');
        return;
    }

    // Validate against template minimums
    if (currentTemplate) {
        const minSpec = currentTemplate.minimumSpec || {};
        const minCpu = minSpec.virtualCpuCores || 1;
        const minMemMb = Math.round((minSpec.memoryBytes || 0) / (1024 * 1024));
        const minDiskGb = Math.round((minSpec.diskBytes || 0) / (1024 * 1024 * 1024));

        if (cpu < minCpu) {
            alert(`This template requires at least ${minCpu} CPU core(s).`);
            return;
        }
        if (memoryMb < minMemMb) {
            alert(`This template requires at least ${minMemMb} MB of memory.`);
            return;
        }
        if (diskGb < minDiskGb) {
            alert(`This template requires at least ${minDiskGb} GB of disk.`);
            return;
        }
    }

    const memory = memoryMb * 1024 * 1024; // MB to bytes
    const disk = diskGb * 1024 * 1024 * 1024; // GB to bytes

    const deployButton = document.getElementById('deploy-template-btn-submit');
    if (deployButton) {
        deployButton.disabled = true;
        deployButton.textContent = 'Deploying...';
    }

    try {
        const response = await api(`/api/marketplace/templates/${templateId}/deploy`, {
            method: 'POST',
            body: JSON.stringify({
                vmName: vmName,
                environmentVariables: collectDeployVariables(),
                customSpec: {
                    virtualCpuCores: cpu,
                    memoryBytes: memory,
                    diskBytes: disk,
                    imageId: 'ubuntu-22.04',
                    gpuMode: gpuMode,
                    gpuVramBytes: gpuVramBytes,
                    requiresGpu: gpuMode !== 0,
                    qualityTier: qualityTier,
                    bandwidthTier: bandwidthTier,
                    replicationFactor: replicationFactor,
                    region: region,
                    zone: zone
                }
            })
        });

        const text = await response.text();
        let data;
        try {
            data = text ? JSON.parse(text) : {};
        } catch {
            throw new Error(response.ok ? 'Invalid response from server' : `Server error (${response.status})`);
        }

        if (data.success || response.ok) {
            console.log('[Template Detail] Deployment successful:', data.data || data);

            const vmId = (data.data || data).vmId;
            const password = (data.data || data).password;

            // Close modal
            closeDeployTemplateModal();

            // Show password modal if we got a valid password (human-readable format)
            if (password && !password.includes('_') && password.includes('-')) {
                if (window.showPasswordModal) {
                    await window.showPasswordModal(vmId, vmName, password);
                } else {
                    console.warn('[Template Detail] Password modal not available');
                    showToast('warning', `VM created! Password: ${password} - Please save it!`);
                }
            }

            // Show success message
            showToast('success', `VM "${vmName}" is being deployed!`);

            // Navigate to VMs page
            setTimeout(() => {
                window.showPage('virtual-machines');
            }, 1000);
        } else {
            throw new Error(data.error || 'Deployment failed');
        }
    } catch (error) {
        console.error('[Template Detail] Deployment failed:', error);
        alert(`Deployment failed: ${error.message}`);
    } finally {
        if (deployButton) {
            deployButton.disabled = false;
            deployButton.textContent = 'Deploy Now';
        }
    }
}

/**
 * Render user-supplied variable inputs into #deploy-variables-section.
 * Called each time the deploy modal opens — clears and rebuilds the section.
 */
function renderDeployVariables(template) {
    const section = document.getElementById('deploy-variables-section');
    const list = document.getElementById('deploy-variables-list');
    if (!section || !list) return;

    const userVars = getUserVariables(template);

    if (userVars.length === 0) {
        section.style.display = 'none';
        list.innerHTML = '';
        return;
    }

    section.style.display = '';
    list.innerHTML = userVars.map(v => {
        const inputId = `deploy-var-${escapeHtml(v.name)}`;
        const defaultVal = v.defaultValue ?? '';
        const desc = v.description ? `<p class="form-help">${escapeHtml(v.description)}</p>` : '';
        return `
            <div class="form-group" style="margin-bottom:12px;">
                <label class="form-label" for="${inputId}">${escapeHtml(v.name.replace(/_/g, ' ').toLowerCase().replace(/\b\w/g, c => c.toUpperCase()))}</label>
                <input type="text" class="form-input deploy-template-var"
                    id="${inputId}"
                    data-var-name="${escapeHtml(v.name)}"
                    value="${escapeHtml(defaultVal)}"
                    placeholder="${escapeHtml(defaultVal)}">
                ${desc}
            </div>`;
    }).join('');
}

/**
 * Collect user-supplied variable values from the deploy modal inputs.
 * Returns only entries that differ from the default (or all if no default).
 * The backend's Layer 1 already applies template.DefaultEnvironmentVariables,
 * so sending all values (including defaults) is safe — user values win (Layer 2).
 */
function collectDeployVariables() {
    const result = {};
    document.querySelectorAll('.deploy-template-var').forEach(input => {
        const name = input.dataset.varName;
        const value = input.value.trim();
        if (name && value !== '') result[name] = value;
    });
    return result;
}

/**
 * Show/hide VRAM input when GPU mode changes, then refresh cost.
 */
export function onDeployGpuModeChange() {
    const gpuMode = parseInt(document.getElementById('deploy-gpu-mode')?.value ?? '0');
    const gpuVramRow = document.getElementById('deploy-gpu-vram-row');
    if (gpuVramRow) gpuVramRow.style.display = gpuMode === 2 ? 'flex' : 'none';
    updateDeployCostEstimate();
}

/**
 * Update cost estimate in deploy modal
 */
function updateDeployCostEstimate() {
    const cpu = parseInt(document.getElementById('deploy-cpu')?.value || 2);
    const memory = parseInt(document.getElementById('deploy-memory')?.value || 2048);
    const disk = parseInt(document.getElementById('deploy-disk')?.value || 20);
    const qualityTierId = parseInt(document.getElementById('deploy-quality-tier')?.value ?? 1);
    const bwTierId = parseInt(document.getElementById('deploy-bandwidth-tier')?.value ?? 3);

    const qualityTier = DEPLOY_QUALITY_TIERS[qualityTierId] || DEPLOY_QUALITY_TIERS[1];
    const bwTier = DEPLOY_BANDWIDTH_TIERS[bwTierId] || DEPLOY_BANDWIDTH_TIERS[3];

    // Cost calculation matching app.js / VmService.CalculateHourlyRate
    const baseCpuRate = 0.01;   // $0.01 per vCPU per hour
    const baseMemRate = 0.005;  // $0.005 per GB RAM per hour
    const baseDiskRate = 0.0001; // $0.0001 per GB disk per hour

    const cpuCost = cpu * baseCpuRate;
    const memCost = (memory / 1024) * baseMemRate;
    const diskCost = disk * baseDiskRate;

    const resourceCost = (cpuCost + memCost + diskCost) * qualityTier.priceMultiplier;

    // GPU cost — added outside tierMultiplier, matching VmService.CalculateHourlyRate
    const GPU_PASSTHROUGH_RATE = 0.10;  // DefaultGpuPerHour
    const GPU_VRAM_RATE = 0.006; // DefaultGpuVramPerGbPerHour
    const gpuMode = parseInt(document.getElementById('deploy-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('deploy-gpu-vram')?.value ?? '0');
    let gpuCost = 0;
    if (gpuMode === 1) gpuCost = GPU_PASSTHROUGH_RATE;
    else if (gpuMode === 2 && gpuVramGb) gpuCost = gpuVramGb * GPU_VRAM_RATE;

    const totalHourly = resourceCost + bwTier.hourlyRate + gpuCost;

    const replicationFactor = parseInt(document.getElementById('deploy-replication-factor')?.value ?? '3');

    // Update display
    const costDisplay = document.getElementById('deploy-cost-estimate');
    if (costDisplay) {
        const replicationNote = replicationFactor > 0
            ? ` + storage replication (${replicationFactor}×)`
            : '';
        costDisplay.textContent = `~$${totalHourly.toFixed(4)}/hr (default rates)${replicationNote}`;
    }
}

/**
 * Helper: Get category icon
 */
function getCategoryIcon(categorySlug) {
    const icons = {
        'ai-ml': '🤖',
        'databases': '🗄️',
        'dev-tools': '🛠️',
        'web-apps': '🌐',
        'privacy-security': '🔒'
    };
    return icons[categorySlug] || '📦';
}

/**
 * Helper: Format date
 */
function formatDate(dateString) {
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;
    const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));

    if (diffDays === 0) return 'Today';
    if (diffDays === 1) return 'Yesterday';
    if (diffDays < 7) return `${diffDays} days ago`;
    if (diffDays < 30) return `${Math.floor(diffDays / 7)} weeks ago`;
    if (diffDays < 365) return `${Math.floor(diffDays / 30)} months ago`;
    return date.toLocaleDateString();
}

/**
 * Helper: Render stars for detail view
 */
function renderStarsDetail(rating) {
    const full = Math.floor(rating);
    const half = rating - full >= 0.5 ? 1 : 0;
    const empty = 5 - full - half;
    return '<span class="stars-display stars-detail">' +
        '<span class="stars-filled">' + '★'.repeat(full) + (half ? '★' : '') + '</span>' +
        '<span class="stars-empty">' + '☆'.repeat(empty) + '</span>' +
        '</span>';
}

// ═══════════════════════════════════════════════════════════════════════════
// REVIEWS
// ═══════════════════════════════════════════════════════════════════════════

let currentReviewRating = 0;

async function loadReviews(templateId) {
    const listEl = document.getElementById('template-reviews-list');
    const formEl = document.getElementById('template-review-form');
    if (!listEl) return;

    try {
        const response = await api(`/api/marketplace/reviews/template/${templateId}`);
        const data = await response.json();
        const reviews = Array.isArray(data) ? data : (data.data || []);

        if (reviews.length === 0) {
            listEl.innerHTML = '<p class="text-muted">No reviews yet. Be the first to review!</p>';
        } else {
            listEl.innerHTML = reviews.map(r => `
                <div class="review-item">
                    <div class="review-header">
                        <span class="review-stars">${'★'.repeat(r.rating)}${'☆'.repeat(5 - r.rating)}</span>
                        <span class="review-author">${escapeHtml((r.reviewerId || '').slice(0, 6))}...${escapeHtml((r.reviewerId || '').slice(-4))}</span>
                        <span class="review-date">${r.createdAt ? formatDate(r.createdAt) : ''}</span>
                    </div>
                    ${r.comment ? `<p class="review-comment">${escapeHtml(r.comment)}</p>` : ''}
                </div>
            `).join('');
        }

        // Show review form if authenticated
        if (formEl) {
            const token = localStorage.getItem('authToken');
            formEl.style.display = token ? '' : 'none';
            currentReviewRating = 0;
            updateStarInputDisplay();
        }
    } catch (error) {
        console.error('[Template Detail] Failed to load reviews:', error);
        listEl.innerHTML = '<p class="text-muted">Unable to load reviews</p>';
    }
}

export function setReviewRating(value) {
    currentReviewRating = value;
    updateStarInputDisplay();
}

function updateStarInputDisplay() {
    const container = document.getElementById('review-stars-input');
    if (!container) return;
    container.querySelectorAll('.review-star').forEach(star => {
        const val = parseInt(star.dataset.value);
        star.textContent = val <= currentReviewRating ? '★' : '☆';
        star.classList.toggle('active', val <= currentReviewRating);
    });
}

export async function submitReview() {
    if (!currentTemplate) return;
    if (currentReviewRating < 1 || currentReviewRating > 5) {
        showToast('warning', 'Please select a rating (1-5 stars)');
        return;
    }

    const comment = document.getElementById('review-comment')?.value?.trim() || '';

    try {
        // Find a VM the user deployed from this template as proof
        const vmsResponse = await api('/api/vms');
        const vmsData = await vmsResponse.json();
        const userVms = vmsData.success ? (vmsData.data.items || []) : [];
        const proofVm = userVms.find(vm => vm.templateId === currentTemplate.id);

        if (!proofVm) {
            showToast('error', 'You must deploy this template before reviewing it.');
            return;
        }

        const response = await api('/api/marketplace/reviews', {
            method: 'POST',
            body: JSON.stringify({
                resourceType: 'template',
                resourceId: currentTemplate.id,
                rating: currentReviewRating,
                comment: comment || null,
                proofType: 'deployment',
                proofReferenceId: proofVm.id
            })
        });

        if (!response.ok) {
            const err = await response.json();
            throw new Error(err.error || 'Failed to submit review');
        }

        showToast('success', 'Review submitted!');
        currentReviewRating = 0;
        if (document.getElementById('review-comment')) document.getElementById('review-comment').value = '';
        await loadReviews(currentTemplate.id);
    } catch (error) {
        showToast('error', error.message);
    }
}

// Tolerate legacy reversed-arg call sites that pass (type, message)
function showToast(typeOrMessage, messageOrType) {
    if (['info', 'success', 'error', 'warning'].includes(typeOrMessage)) {
        sharedShowToast(messageOrType, typeOrMessage);
    } else {
        sharedShowToast(typeOrMessage, messageOrType);
    }
}

// Export functions for global access
window.templateDetail = {
    showTemplateDetail,
    closeTemplateDetail,
    openDeployTemplateModal,
    closeDeployTemplateModal,
    deployFromTemplate,
    updateDeployCostEstimate,
    onRegionChange,
    setReviewRating,
    submitReview
};