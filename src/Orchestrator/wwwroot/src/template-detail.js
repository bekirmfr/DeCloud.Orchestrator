// ============================================================================
// Template Detail Module
// View template details and deploy
// ============================================================================

let currentTemplate = null;

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
            modal.classList.add('active');
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
        modal.classList.remove('active');
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
                    <span class="template-version">v${template.version}</span>
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
        
        <!-- Stats -->
        <div class="detail-section">
            <h3 class="detail-section-title">📊 Stats</h3>
            <div class="template-stats-detail">
                <div class="stat-detail">
                    <span class="stat-detail-label">Deployments:</span>
                    <span class="stat-detail-value">${template.deploymentCount || 0}</span>
                </div>
                <div class="stat-detail">
                    <span class="stat-detail-label">Last Deployed:</span>
                    <span class="stat-detail-value">
                        ${template.lastDeployedAt ? formatDate(template.lastDeployedAt) : 'Never'}
                    </span>
                </div>
                <div class="stat-detail">
                    <span class="stat-detail-label">Created:</span>
                    <span class="stat-detail-value">${formatDate(template.createdAt)}</span>
                </div>
            </div>
        </div>
        
        ${template.sourceUrl ? `
            <div class="detail-section">
                <h3 class="detail-section-title">🔗 Links</h3>
                <div class="template-links">
                    <a href="${template.sourceUrl}" target="_blank" class="template-link">
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
}

/**
 * Open deployment modal for template
 */
export function openDeployTemplateModal(template) {
    if (!template) template = currentTemplate;
    if (!template) return;
    
    console.log('[Template Detail] Opening deployment modal for:', template.name);
    
    // Close detail modal
    closeTemplateDetail();
    
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
    
    // Set recommended specs
    const recSpec = template.recommendedSpec || template.minimumSpec;
    if (recSpec) {
        document.getElementById('deploy-cpu').value = recSpec.virtualCpuCores || 2;
        document.getElementById('deploy-memory').value = (recSpec.memoryBytes || 2147483648) / (1024 * 1024) || 2048;
        document.getElementById('deploy-disk').value = (recSpec.diskBytes || 21474836480) / (1024 * 1024 * 1024) || 20;
    }
    
    // Update cost estimate
    updateDeployCostEstimate();
    
    // Show modal
    modal.classList.add('active');
    document.body.style.overflow = 'hidden';
}

/**
 * Close deployment modal
 */
export function closeDeployTemplateModal() {
    const modal = document.getElementById('deploy-template-modal');
    if (modal) {
        modal.classList.remove('active');
        document.body.style.overflow = '';
    }
}

/**
 * Deploy template
 */
export async function deployFromTemplate() {
    const templateId = document.getElementById('deploy-template-id').value;
    const vmName = document.getElementById('deploy-vm-name').value.trim();
    const cpu = parseInt(document.getElementById('deploy-cpu').value);
    const memory = parseInt(document.getElementById('deploy-memory').value) * 1024 * 1024; // MB to bytes
    const disk = parseInt(document.getElementById('deploy-disk').value) * 1024 * 1024 * 1024; // GB to bytes
    
    if (!vmName) {
        alert('Please enter a VM name');
        return;
    }
    
    if (!templateId) {
        alert('Template not selected');
        return;
    }
    
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
                customSpec: {
                    virtualCpuCores: cpu,
                    memoryBytes: memory,
                    diskBytes: disk,
                    imageId: 'ubuntu-22.04',
                    requiresGpu: currentTemplate?.requiresGpu || false
                }
            })
        });
        
        const data = await response.json();
        
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
 * Update cost estimate in deploy modal
 */
function updateDeployCostEstimate() {
    const cpu = parseInt(document.getElementById('deploy-cpu')?.value || 2);
    const memory = parseInt(document.getElementById('deploy-memory')?.value || 2048);
    const disk = parseInt(document.getElementById('deploy-disk')?.value || 20);
    
    // Simple cost calculation (can be enhanced)
    const baseCost = 0.01; // $0.01 per vCPU per hour
    const memoryCost = (memory / 1024) * 0.005; // $0.005 per GB RAM per hour
    const diskCost = (disk / 100) * 0.01; // $0.01 per 100GB disk per hour
    
    const totalHourly = (cpu * baseCost) + memoryCost + diskCost;
    
    // Update display
    const costDisplay = document.getElementById('deploy-cost-estimate');
    if (costDisplay) {
        costDisplay.textContent = `~$${totalHourly.toFixed(3)}/hr`;
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
        'web-apps': '🌐'
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
 * Helper: Render markdown (simplified)
 */
function renderMarkdown(markdown) {
    if (!markdown) return '';
    
    // Simple markdown rendering (for full support, use marked.js)
    let html = escapeHtml(markdown);
    
    // Headers
    html = html.replace(/^### (.+)$/gm, '<h3>$1</h3>');
    html = html.replace(/^## (.+)$/gm, '<h2>$1</h2>');
    html = html.replace(/^# (.+)$/gm, '<h1>$1</h1>');
    
    // Bold
    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    
    // Code blocks
    html = html.replace(/```([\s\S]+?)```/g, '<pre><code>$1</code></pre>');
    
    // Inline code
    html = html.replace(/`(.+?)`/g, '<code>$1</code>');
    
    // Line breaks
    html = html.replace(/\n/g, '<br>');
    
    return html;
}

/**
 * Helper: Escape HTML
 */
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

/**
 * Helper: Show toast notification
 */
function showToast(type, message) {
    if (window.showToast) {
        window.showToast(type, message);
    } else {
        console.log(`[Toast] ${type}: ${message}`);
    }
}

// Export functions for global access
window.templateDetail = {
    showTemplateDetail,
    closeTemplateDetail,
    openDeployTemplateModal,
    closeDeployTemplateModal,
    deployFromTemplate,
    updateDeployCostEstimate
};
