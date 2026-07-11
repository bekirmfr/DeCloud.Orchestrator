// ============================================================================
// Template Detail Module
// View template details and deploy
// ============================================================================

import { escapeHtml, sanitizeUrl, showToast as sharedShowToast, isPerDeployPricing, renderMarkdown } from './utils.js';
import { submitTemplateDeploy, afterDeploySuccess } from './deploy-submit.js';

// ============================================
// MODULE STATE
// ============================================
let currentTemplate = null;

// Handle from constraint-builder.js mounted in the deploy modal.
// Destroyed and re-created each time the modal opens.
let _cbDeployHandle = null;

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

// Platform-resolved variable names — fetched from the orchestrator's resolver
// registry via GET /api/marketplace/platform-variables. Cached for the page
// lifetime; full reload re-fetches. Single source of truth lives in C# (the
// IVariableResolver DI registrations); see UNIFIED_CLOUDINIT_PIPELINE.md §2.4.
//
// Any declared Static variable whose (resolverKey ?? name) appears in this set
// is platform-resolved and hidden from the deploy form. Variables not in the
// set surface in the Template Configuration section as user-supplied input.
let _platformStaticKeys = null;

async function getPlatformStaticKeys() {
    if (_platformStaticKeys !== null) return _platformStaticKeys;
    try {
        const response = await api('/api/marketplace/platform-variables');
        const data = await response.json();
        // Response shape: { static: [...], dynamic: [...] }
        // PascalCase from C# is camelCased by ConfigureHttpJsonOptions in Program.cs.
        const list = Array.isArray(data?.static) ? data.static : [];
        _platformStaticKeys = new Set(list);
    } catch (error) {
        console.error('[Template Detail] Failed to load platform variables. ' +
            'Deploy form will surface every declared Static — user ' +
            'may see fields the platform actually fills. Hard-refresh ' +
            'or check orchestrator availability.', error);
        // Empty set: when the fetch fails, fall open (show everything) rather
        // than fall closed (hide everything). A surfaced field is recoverable
        // by the user; a hidden required field would block deployment silently.
        _platformStaticKeys = new Set();
    }
    return _platformStaticKeys;
}

// VariableKind enum: 0=Static, 1=Dynamic
async function getUserVariables(template) {
    if (!Array.isArray(template.variables)) return [];
    const platformKeys = await getPlatformStaticKeys();
    return template.variables.filter(v => {
        const isStatic = v.kind === 0 || v.kind === 'Static';
        if (!isStatic) return false;
        // Mirror CloudInitRenderer's Pass 1 lookup key: ResolverKey ?? Name.
        // A variable declared as { name: 'EGRESS_IP', resolverKey: 'PUBLIC_IP' }
        // is platform-bound via PUBLIC_IP's resolver, not via EGRESS_IP.
        const lookupKey = v.resolverKey || v.name;
        return !platformKeys.has(lookupKey);
    });
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

        // Show VRAM input for any GPU mode (Passthrough or Proxied), hide for None.
        if (gpuVramRow)
            gpuVramRow.style.display = defaultGpuMode !== 0 ? 'flex' : 'none';

        // Label differs by mode: Passthrough uses minimum VRAM for scheduling +
        // billing estimate; Proxied uses it as the exact allocation quota.
        const gpuVramLabel = document.getElementById('deploy-gpu-vram-label');
        if (gpuVramLabel) {
            gpuVramLabel.textContent = defaultGpuMode === 1
                ? 'Min. VRAM (GB)  ·  estimate only'
                : 'VRAM (GB)';
        }

        // Pre-fill VRAM from recommended spec, fall back to minimum, then 4 GB.
        // For Passthrough this is the minimum scheduling requirement and
        // the billing estimate floor — actual cost reflects the assigned GPU's
        // full VRAM, which may be higher.
        const recGpuVram = recSpec.gpuVramBytes || minSpec.gpuVramBytes || 0;
        if (gpuVramInput)
            gpuVramInput.value = recGpuVram > 0
                ? Math.round(recGpuVram / (1024 ** 3))
                : 4;
    }

    // Mount the constraint builder.
    // - lockedRows: template.minimumSpec.constraints — mandatory, non-removable.
    // - initial:    template.recommendedSpec.constraints — defaults user can override.
    // Destroyed in closeDeployTemplateModal so each open starts fresh.
    const cbContainer = document.getElementById('deploy-constraint-builder');
    if (cbContainer) {
        _cbDeployHandle?.destroy();
        const { mount } = await import('./constraint-builder.js');
        _cbDeployHandle = await mount(cbContainer, {
            lockedRows: template.minimumSpec?.constraints ?? [],
            initial: template.recommendedSpec?.constraints ?? [],
            apiFetch: api,
            qualityTier: String(document.getElementById('deploy-quality-tier')?.value ?? 'Standard'),
        });
    }

    // Update cost estimate
    updateDeployCostEstimate();
    // Render user-supplied variable inputs (template configuration section).
    // Awaited so the modal opens with the correct visibility — without await,
    // the section would briefly flash visible before the async filter resolves.
    await renderDeployVariables(template);


    // Show modal
    if (window.openStaticModal) window.openStaticModal(modal);
    else modal.classList.add('active');
    document.body.style.overflow = 'hidden';
}

/**
 * Close deployment modal
 */
export function closeDeployTemplateModal() {
    _cbDeployHandle?.destroy();
    _cbDeployHandle = null;

    const modal = document.getElementById('deploy-template-modal');
    if (modal) {
        if (window.closeStaticModal) window.closeStaticModal(modal); else modal.classList.remove('active');
        document.body.style.overflow = '';
    }
    const replicationSelect = document.getElementById('deploy-replication-factor');
    if (replicationSelect) replicationSelect.value = '3';
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

    // GPU — read from the GPU Access section (hidden when template has no GPU requirement)
    const gpuMode = parseInt(document.getElementById('deploy-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('deploy-gpu-vram')?.value ?? '0');
    const gpuVramBytes = gpuMode !== 0 && gpuVramGb > 0
        ? Math.round(gpuVramGb * 1024 ** 3) : null;

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
        // Collect scheduling constraints from the builder.
        // Includes the user's custom rows plus any non-locked template
        // RecommendedSpec constraints the user didn't override.
        // Template MinimumSpec constraints are merged server-side
        // (BuildVmRequestFromTemplateAsync) regardless of what is sent here.
        const deployConstraints = _cbDeployHandle
            ? _cbDeployHandle.getConstraints()
            : [];

        // Submission (fetch, ToS-gate retry, error shapes) and the
        // post-success ritual (password modal, toast, navigation) live in
        // deploy-submit.js — the ONE deploy path, shared with the Deploy
        // from Repo form. This function only reads its own DOM.
        const result = await submitTemplateDeploy(templateId, {
            vmName,
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
                constraints: deployConstraints.length > 0 ? deployConstraints : null
            }
        });

        console.log('[Template Detail] Deployment successful:', result);
        closeDeployTemplateModal();
        await afterDeploySuccess(result, vmName);
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
 * Async because the platform-variables fetch may need to run on first call.
 */
async function renderDeployVariables(template) {
    const section = document.getElementById('deploy-variables-section');
    const list = document.getElementById('deploy-variables-list');
    if (!section || !list) return;

    const userVars = await getUserVariables(template);

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
    const gpuVramLabel = document.getElementById('deploy-gpu-vram-label');
    if (gpuVramRow) gpuVramRow.style.display = gpuMode !== 0 ? 'flex' : 'none';
    if (gpuVramLabel) {
        gpuVramLabel.textContent = gpuMode === 1
            ? 'Min. VRAM (GB)  ·  estimate only'
            : 'VRAM (GB)';
    }
    updateDeployCostEstimate();
}

/**
 * Update cost estimate in deploy modal.
 * Delegates to POST /api/system/pricing/calculate (HourlyRateCalculator)
 * so replication, bandwidth, and GPU are all factored into the total.
 */
async function updateDeployCostEstimate() {
    const cpu = parseInt(document.getElementById('deploy-cpu')?.value || 2);
    const memoryMb = parseInt(document.getElementById('deploy-memory')?.value || 2048);
    const diskGb = parseInt(document.getElementById('deploy-disk')?.value || 20);
    const qualityTier = parseInt(document.getElementById('deploy-quality-tier')?.value ?? 1);
    const bandwidthTier = parseInt(document.getElementById('deploy-bandwidth-tier')?.value ?? 3);
    const replicationFactor = parseInt(document.getElementById('deploy-replication-factor')?.value ?? '3');
    const gpuMode = parseInt(document.getElementById('deploy-gpu-mode')?.value ?? '0');
    const gpuVramGb = parseFloat(document.getElementById('deploy-gpu-vram')?.value ?? '0');
    // For Proxied: GpuVramBytes is the exact VRAM quota allocated from the pool.
    // For Passthrough: GpuVramBytes is the minimum VRAM the workload needs —
    // actual billing post-scheduling uses the assigned GPU's full VRAM, which
    // may be higher. This value drives both the scheduler (minimum requirement)
    // and the pre-deployment cost estimate (floor).
    const gpuVramBytes = gpuMode !== 0 && gpuVramGb > 0
        ? Math.round(gpuVramGb * 1024 ** 3) : null;

    const costDisplay = document.getElementById('deploy-cost-estimate');
    if (!costDisplay) return;
    costDisplay.textContent = 'Calculating…';

    try {
        const res = await api('/api/system/pricing/calculate', {
            method: 'POST',
            body: JSON.stringify({
                virtualCpuCores: cpu,
                memoryBytes: memoryMb * 1024 * 1024,
                diskBytes: diskGb * 1024 * 1024 * 1024,
                qualityTier,
                bandwidthTier,
                replicationFactor,
                gpuMode,
                gpuVramBytes,
            }),
        });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const d = await res.json();
        const calc = d.data ?? d;
        const daily = Number(calc.dailyTotal);
        const weekly = daily * 7;
        const monthly = Number(calc.monthlyTotal);
        const isPassthrough = gpuMode === 1;
        const passthroughNote = isPassthrough
            ? `<br><span style="font-size:0.75em;opacity:0.5">` +
            `GPU estimate based on minimum VRAM — actual cost reflects assigned GPU</span>`
            : '';
        costDisplay.innerHTML =
            `~$${Number(calc.hourlyTotal).toFixed(4)}/hr (default rates)` +
            `<br><span style="font-size:0.8em;opacity:0.6">` +
            `~$${daily.toFixed(2)}/day&nbsp;&nbsp;·&nbsp;&nbsp;` +
            `~$${weekly.toFixed(2)}/wk&nbsp;&nbsp;·&nbsp;&nbsp;` +
            `~$${monthly.toFixed(2)}/mo` +
            `</span>` + passthroughNote;
    } catch (_e) {
        costDisplay.textContent = 'Pricing unavailable';
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
    onDeployGpuModeChange,
    setReviewRating,
    submitReview
};