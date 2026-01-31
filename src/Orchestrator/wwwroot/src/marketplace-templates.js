// ============================================================================
// Marketplace Templates Module
// Browse and deploy VM templates from the marketplace
// ============================================================================

// Use global api function (from app.js)
const api = window.api;

let currentCategory = null;
let currentSearch = '';
let allTemplates = [];
let allCategories = [];

/**
 * Initialize marketplace templates page
 */
export async function initMarketplaceTemplates() {
    console.log('[Marketplace Templates] Initializing...');
    
    // Load data
    await Promise.all([
        loadCategories(),
        loadTemplates()
    ]);
    
    // Setup UI
    renderCategories();
    renderTemplates();
    setupEventListeners();
    
    console.log('[Marketplace Templates] Ready');
}

/**
 * Load all categories from API
 */
async function loadCategories() {
    try {
        const response = await api('/api/marketplace/categories');
        allCategories = response.data || [];
        console.log(`[Marketplace Templates] Loaded ${allCategories.length} categories`);
    } catch (error) {
        console.error('[Marketplace Templates] Failed to load categories:', error);
        allCategories = [];
    }
}

/**
 * Load all templates from API
 */
async function loadTemplates(options = {}) {
    try {
        const params = new URLSearchParams();
        
        if (options.category) params.append('category', options.category);
        if (options.search) params.append('search', options.search);
        if (options.requiresGpu !== undefined) params.append('requiresGpu', options.requiresGpu);
        if (options.featured) params.append('featured', 'true');
        
        params.append('sortBy', options.sortBy || 'popular');
        
        const url = `/api/marketplace/templates?${params.toString()}`;
        const response = await api(url);
        
        allTemplates = response.data || [];
        console.log(`[Marketplace Templates] Loaded ${allTemplates.length} templates`);
        
        return allTemplates;
    } catch (error) {
        console.error('[Marketplace Templates] Failed to load templates:', error);
        allTemplates = [];
        return [];
    }
}

/**
 * Render category tabs
 */
function renderCategories() {
    const container = document.getElementById('marketplace-categories');
    if (!container) return;
    
    const categories = [
        { slug: null, name: 'All Templates', iconEmoji: '📦' },
        ...allCategories
    ];
    
    container.innerHTML = categories.map(cat => `
        <button 
            class="category-tab ${currentCategory === cat.slug ? 'active' : ''}"
            data-category="${cat.slug || ''}"
            onclick="window.marketplaceTemplates.selectCategory('${cat.slug || ''}')"
        >
            <span class="category-icon">${cat.iconEmoji}</span>
            <span class="category-name">${cat.name}</span>
        </button>
    `).join('');
}

/**
 * Render templates grid
 */
function renderTemplates() {
    const container = document.getElementById('templates-grid');
    if (!container) return;
    
    if (allTemplates.length === 0) {
        container.innerHTML = `
            <div class="empty-state">
                <div class="empty-icon">📦</div>
                <h3>No templates found</h3>
                <p>Try adjusting your filters or search terms</p>
            </div>
        `;
        return;
    }
    
    container.innerHTML = allTemplates.map(template => createTemplateCard(template)).join('');
}

/**
 * Create HTML for a template card
 */
function createTemplateCard(template) {
    const category = allCategories.find(c => c.slug === template.category);
    const categoryName = category?.name || template.category;
    const categoryIcon = category?.iconEmoji || '📦';
    
    // Format cost
    const costPerHour = template.estimatedCostPerHour || 0;
    const costText = costPerHour > 0 ? `$${costPerHour.toFixed(2)}/hr` : 'Free';
    
    // Deployment count
    const deployments = template.deploymentCount || 0;
    const deploymentsText = deployments === 1 ? '1 deployment' : `${deployments} deployments`;
    
    return `
        <div class="template-card" data-template-id="${template.id}">
            <div class="template-card-header">
                ${template.isFeatured ? '<span class="featured-badge">⭐ Featured</span>' : ''}
                ${template.requiresGpu ? '<span class="gpu-badge">🎮 GPU</span>' : ''}
            </div>
            
            <div class="template-card-body">
                <div class="template-icon">${categoryIcon}</div>
                <h3 class="template-name">${escapeHtml(template.name)}</h3>
                <div class="template-category">${categoryIcon} ${categoryName}</div>
                <p class="template-description">${escapeHtml(template.description)}</p>
                
                <div class="template-tags">
                    ${template.tags.slice(0, 3).map(tag => 
                        `<span class="tag">${escapeHtml(tag)}</span>`
                    ).join('')}
                    ${template.tags.length > 3 ? `<span class="tag">+${template.tags.length - 3}</span>` : ''}
                </div>
            </div>
            
            <div class="template-card-footer">
                <div class="template-stats">
                    <span class="stat">
                        <span class="stat-icon">💰</span>
                        <span class="stat-value">${costText}</span>
                    </span>
                    <span class="stat">
                        <span class="stat-icon">🚀</span>
                        <span class="stat-value">${deploymentsText}</span>
                    </span>
                </div>
                <div class="template-actions">
                    <button 
                        class="btn-secondary btn-sm"
                        onclick="window.marketplaceTemplates.viewTemplate('${template.id}')"
                    >
                        Details
                    </button>
                    <button 
                        class="btn-primary btn-sm"
                        onclick="window.marketplaceTemplates.deployTemplate('${template.id}')"
                    >
                        Deploy
                    </button>
                </div>
            </div>
        </div>
    `;
}

/**
 * Setup event listeners
 */
function setupEventListeners() {
    // Search input
    const searchInput = document.getElementById('template-search');
    if (searchInput) {
        let searchTimeout;
        searchInput.addEventListener('input', (e) => {
            clearTimeout(searchTimeout);
            searchTimeout = setTimeout(() => {
                currentSearch = e.target.value;
                filterTemplates();
            }, 300);
        });
    }
    
    // Sort dropdown
    const sortSelect = document.getElementById('template-sort');
    if (sortSelect) {
        sortSelect.addEventListener('change', (e) => {
            filterTemplates({ sortBy: e.target.value });
        });
    }
    
    // GPU filter
    const gpuFilter = document.getElementById('filter-gpu');
    if (gpuFilter) {
        gpuFilter.addEventListener('change', (e) => {
            const requiresGpu = e.target.checked ? true : undefined;
            filterTemplates({ requiresGpu });
        });
    }
}

/**
 * Select a category
 */
export async function selectCategory(categorySlug) {
    currentCategory = categorySlug || null;
    
    // Update UI
    renderCategories();
    
    // Show loading state
    const container = document.getElementById('templates-grid');
    if (container) {
        container.innerHTML = '<div class="loading-spinner">Loading templates...</div>';
    }
    
    // Load filtered templates
    await filterTemplates();
}

/**
 * Filter templates based on current filters
 */
async function filterTemplates(options = {}) {
    const sortSelect = document.getElementById('template-sort');
    const gpuFilter = document.getElementById('filter-gpu');
    
    const filterOptions = {
        category: currentCategory,
        search: currentSearch || undefined,
        sortBy: options.sortBy || sortSelect?.value || 'popular',
        requiresGpu: options.requiresGpu !== undefined ? options.requiresGpu : 
                     (gpuFilter?.checked ? true : undefined)
    };
    
    await loadTemplates(filterOptions);
    renderTemplates();
}

/**
 * View template details
 */
export function viewTemplate(templateId) {
    console.log('[Marketplace Templates] Viewing template:', templateId);
    
    // Open template detail modal
    if (window.templateDetail && window.templateDetail.showTemplateDetail) {
        window.templateDetail.showTemplateDetail(templateId);
    } else {
        console.error('[Marketplace Templates] Template detail module not loaded');
    }
}

/**
 * Deploy template
 */
export function deployTemplate(templateId) {
    console.log('[Marketplace Templates] Deploying template:', templateId);
    const template = allTemplates.find(t => t.id === templateId);
    
    if (!template) {
        alert('Template not found');
        return;
    }
    
    // Open deployment modal directly
    if (window.templateDetail && window.templateDetail.showTemplateDetail) {
        window.templateDetail.showTemplateDetail(templateId);
        // The detail modal has a "Deploy" button
    } else {
        console.error('[Marketplace Templates] Template detail module not loaded');
    }
}

/**
 * Escape HTML to prevent XSS
 */
function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

// Export functions for global access
window.marketplaceTemplates = {
    selectCategory,
    viewTemplate,
    deployTemplate
};
