// ============================================================================
// Marketplace Templates Module
// Browse and deploy VM templates from the marketplace
// ============================================================================

import { escapeHtml, isPerDeployPricing, showToast } from './utils.js';

let currentCategory = null;
let currentSearch = '';
let allTemplates = [];
let allCategories = [];

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
        const data = await response.json();
        console.log('[Marketplace Templates] Categories data:', data);
        
        // API returns array directly or wrapped in success object
        allCategories = Array.isArray(data) ? data : (data.data || []);
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
        const data = await response.json();
        console.log('[Marketplace Templates] Templates data:', data);
        
        // API returns array directly or wrapped in success object
        allTemplates = Array.isArray(data) ? data : (data.data || []);
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
    
    container.innerHTML = categories.map(cat => {
        const slug = cat.slug || '';
        return `
        <button
            class="category-tab ${currentCategory === cat.slug ? 'active' : ''}"
            data-category="${escapeHtml(slug)}"
            data-action="select-category"
        >
            <span class="category-icon">${escapeHtml(cat.iconEmoji || '')}</span>
            <span class="category-name">${escapeHtml(cat.name || '')}</span>
        </button>`;
    }).join('');

    if (!container.dataset.delegated) {
        container.dataset.delegated = '1';
        container.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-action="select-category"]');
            if (!btn) return;
            selectCategory(btn.dataset.category || null);
        });
    }
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

    // Template price badge
    const isPaid = isPerDeployPricing(template.pricingModel) && template.templatePrice > 0;
    const priceText = isPaid ? `${template.templatePrice} USDC` : 'Free';

    // Deployment count
    const deployments = template.deploymentCount || 0;
    const deploymentsText = deployments === 1 ? '1 deploy' : `${deployments} deploys`;

    // Rating
    const rating = template.averageRating || 0;
    const totalReviews = template.totalReviews || 0;
    const starsHtml = renderStarsHtml(rating);

    // Community badge
    const communityBadge = template.isCommunity ? '<span class="community-badge">Community</span>' : '';

    return `
        <div class="template-card" data-template-id="${escapeHtml(template.id)}">
            <div class="template-card-header">
                ${template.isFeatured ? '<span class="featured-badge">⭐ Featured</span>' : ''}
                ${template.requiresGpu ? '<span class="gpu-badge">🎮 GPU</span>' : ''}
                ${communityBadge}
                <span class="price-badge ${isPaid ? 'price-paid' : 'price-free'}">${escapeHtml(priceText)}</span>
            </div>

            <div class="template-card-body">
                <div class="template-icon">${escapeHtml(categoryIcon)}</div>
                <h3 class="template-name">${escapeHtml(template.name)}</h3>
                <div class="template-category">${escapeHtml(categoryIcon)} ${escapeHtml(categoryName)}</div>
                <p class="template-description">${escapeHtml(template.description)}</p>

                <div class="template-rating">
                    ${starsHtml}
                    <span class="rating-count">(${totalReviews})</span>
                </div>

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
                        <span class="stat-icon">🚀</span>
                        <span class="stat-value">${escapeHtml(deploymentsText)}</span>
                    </span>
                    <span class="stat">
                        <span class="stat-icon">👤</span>
                        <span class="stat-value">${escapeHtml(template.authorName || 'DeCloud')}</span>
                    </span>
                </div>
                <div class="template-actions">
                    <button class="btn-secondary btn-sm" data-action="view-template">Details</button>
                    <button class="btn-primary btn-sm" data-action="deploy-template">Deploy</button>
                </div>
            </div>
        </div>
    `;
}

function renderStarsHtml(rating) {
    const full = Math.floor(rating);
    const half = rating - full >= 0.5 ? 1 : 0;
    const empty = 5 - full - half;
    return '<span class="stars-display">' +
        '<span class="stars-filled">' + '★'.repeat(full) + (half ? '★' : '') + '</span>' +
        '<span class="stars-empty">' + '☆'.repeat(empty + (half ? 0 : 0)) + '</span>' +
        '</span>';
}

/**
 * Setup event listeners (guarded against re-entry)
 */
let _eventListenersWired = false;
function setupEventListeners() {
    if (_eventListenersWired) return;
    _eventListenersWired = true;

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

    const sortSelect = document.getElementById('template-sort');
    if (sortSelect) {
        sortSelect.addEventListener('change', (e) => {
            filterTemplates({ sortBy: e.target.value });
        });
    }

    const gpuFilter = document.getElementById('filter-gpu');
    if (gpuFilter) {
        gpuFilter.addEventListener('change', (e) => {
            const requiresGpu = e.target.checked ? true : undefined;
            filterTemplates({ requiresGpu });
        });
    }

    // Delegated handlers for template cards (rebuilt on every render).
    const grid = document.getElementById('templates-grid');
    if (grid) {
        grid.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-action]');
            if (!btn) return;
            const card = btn.closest('[data-template-id]');
            if (!card) return;
            const templateId = card.dataset.templateId;
            if (btn.dataset.action === 'view-template') viewTemplate(templateId);
            else if (btn.dataset.action === 'deploy-template') deployTemplate(templateId);
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

// Export functions for global access
window.marketplaceTemplates = {
    selectCategory,
    viewTemplate,
    deployTemplate
};
