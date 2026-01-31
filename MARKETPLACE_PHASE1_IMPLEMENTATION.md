# DeCloud Marketplace - Phase 1 Implementation Plan
**Created:** 2026-01-30  
**Target Completion:** Week 1 (3-4 days)  
**Status:** Ready to Start

---

## Executive Summary

**Goal:** Launch VM Template Marketplace with 10-15 curated templates and basic browsing/deployment capabilities.

**Decisions Made:**
- ✅ **Scope:** Phase 1 only (VM Templates, defer solutions)
- ✅ **Strategy:** Hybrid (curated launch → community Phase 2)
- ✅ **Immutability:** Hybrid (versioned when published, editable drafts)
- ✅ **Monetization:** Optional (platform free, creators can charge)

**Success Criteria:**
- [ ] 10-15 high-quality curated templates live
- [ ] One-click deployment working end-to-end
- [ ] Template browsing with categories & filters
- [ ] <5 minute average deployment time
- [ ] 95%+ successful deployment rate

---

## Table of Contents
1. [Architecture Overview](#architecture-overview)
2. [Task Breakdown](#task-breakdown)
3. [Data Models (Simplified for Phase 1)](#data-models)
4. [Backend Implementation](#backend-implementation)
5. [Frontend Implementation](#frontend-implementation)
6. [Curated Template Catalog](#curated-template-catalog)
7. [Testing Strategy](#testing-strategy)
8. [Launch Checklist](#launch-checklist)

---

## Architecture Overview

### System Flow

```
┌─────────────────────────────────────────────────────────────┐
│                    USER EXPERIENCE                          │
│  Browse Templates → Select Template → Configure → Deploy    │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                  FRONTEND (React/Vite)                      │
│  • Marketplace home (/marketplace)                          │
│  • Template browsing (/marketplace/templates)               │
│  • Template detail (/marketplace/templates/:slug)           │
│  • Deploy modal (reuse existing VM creation)                │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│              MARKETPLACE API (New Controller)               │
│  GET  /api/marketplace/templates                            │
│  GET  /api/marketplace/templates/:id                        │
│  POST /api/marketplace/templates/:id/deploy                 │
│  GET  /api/marketplace/categories                           │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│                   TEMPLATE SERVICE (New)                    │
│  • Load templates from MongoDB                              │
│  • Apply filters & search                                   │
│  • Merge template with user spec                            │
│  • Generate cloud-init with variables                       │
└─────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────┐
│              EXISTING VM SERVICE (Enhanced)                 │
│  • CreateVmAsync() accepts templateId parameter             │
│  • Applies template defaults                                │
│  • Processes cloud-init                                     │
│  • Deploys to node                                          │
└─────────────────────────────────────────────────────────────┘
```

### Key Integration Points

**Existing Systems We'll Use:**
- ✅ VmService.CreateVmAsync() - Just add templateId parameter
- ✅ Node scheduling - Works as-is
- ✅ Cloud-init system - Just add variable substitution
- ✅ VM creation modal - Enhance to show template info
- ✅ Authentication - Wallet-based, works as-is

**New Components We'll Build:**
- 🆕 VmTemplate model & MongoDB collection
- 🆕 TemplateCategory model & seed data
- 🆕 MarketplaceController (API endpoints)
- 🆕 TemplateService (business logic)
- 🆕 Marketplace UI pages (3 pages)
- 🆕 10-15 curated templates

---

## Task Breakdown

### Backend Tasks (Est: 6-8 hours)

#### Task 1.1: Data Models & MongoDB Setup (1 hour)

**Files to Create:**
- `Models/VmTemplate.cs`
- `Models/TemplateCategory.cs`
- `Models/TemplateStatus.cs` (enum)

**Files to Modify:**
- `Models/VirtualMachine.cs` - Add `TemplateId` field
- `Persistence/DataStore.cs` - Add template collections

**Acceptance Criteria:**
- [ ] VmTemplate model with all Phase 1 fields
- [ ] TemplateCategory model
- [ ] MongoDB collections created
- [ ] Indexes created (slug, category, status)

**Estimated Time:** 1 hour

---

#### Task 1.2: Template Service (2 hours)

**File to Create:**
- `Services/TemplateService.cs`
- `Services/ITemplateService.cs`

**Methods to Implement:**

```csharp
public interface ITemplateService
{
    // Read operations
    Task<List<VmTemplate>> GetTemplatesAsync(TemplateQuery query);
    Task<VmTemplate?> GetTemplateByIdAsync(string templateId);
    Task<VmTemplate?> GetTemplateBySlugAsync(string slug);
    Task<List<TemplateCategory>> GetCategoriesAsync();
    
    // Admin operations (for curated templates)
    Task<VmTemplate> CreateTemplateAsync(VmTemplate template);
    Task UpdateTemplateAsync(VmTemplate template);
    
    // Deployment helpers
    Task<CreateVmRequest> BuildVmRequestFromTemplateAsync(
        string templateId, 
        VmSpec? customSpec = null,
        Dictionary<string, string>? envVars = null
    );
    
    // Cloud-init processing
    string SubstituteVariables(string cloudInit, Dictionary<string, string> variables);
    
    // Stats
    Task IncrementDeploymentCountAsync(string templateId);
}
```

**Acceptance Criteria:**
- [ ] All interface methods implemented
- [ ] Template queries with filters (category, GPU, tags)
- [ ] Cloud-init variable substitution working
- [ ] Template validation logic
- [ ] Deployment count tracking

**Estimated Time:** 2 hours

---

#### Task 1.3: Marketplace Controller (1.5 hours)

**File to Create:**
- `Controllers/MarketplaceController.cs`

**Endpoints to Implement:**

```csharp
[ApiController]
[Route("api/marketplace")]
public class MarketplaceController : ControllerBase
{
    // Browse templates
    [HttpGet("templates")]
    public async Task<ActionResult<ApiResponse<List<VmTemplate>>>> GetTemplates(
        [FromQuery] string? category = null,
        [FromQuery] bool? requiresGpu = null,
        [FromQuery] string? tags = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = "popular" // popular, newest, name
    );
    
    // Get template details
    [HttpGet("templates/{slugOrId}")]
    public async Task<ActionResult<ApiResponse<VmTemplate>>> GetTemplate(string slugOrId);
    
    // Deploy from template
    [HttpPost("templates/{templateId}/deploy")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<CreateVmResponse>>> DeployTemplate(
        string templateId,
        [FromBody] DeployTemplateRequest request
    );
    
    // Get categories
    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<List<TemplateCategory>>>> GetCategories();
}

public class DeployTemplateRequest
{
    public string? NodeId { get; set; }              // Optional target node
    public VmSpec? CustomSpec { get; set; }          // Override template defaults
    public Dictionary<string, string>? EnvironmentVariables { get; set; }
}
```

**Acceptance Criteria:**
- [ ] All endpoints working
- [ ] Proper authorization on deploy endpoint
- [ ] Error handling
- [ ] Request validation
- [ ] Integration with TemplateService

**Estimated Time:** 1.5 hours

---

#### Task 1.4: VmService Integration (1 hour)

**File to Modify:**
- `Services/VmService.cs`

**Changes:**

```csharp
public async Task<CreateVmResponse> CreateVmAsync(
    string userId, 
    CreateVmRequest request,
    string? targetNodeId = null,
    string? templateId = null  // NEW parameter
)
{
    // If templateId provided, load template and merge
    if (!string.IsNullOrEmpty(templateId))
    {
        var template = await _templateService.GetTemplateByIdAsync(templateId);
        if (template == null)
            throw new ArgumentException("Template not found");
        
        // Merge template spec with user overrides
        request = MergeTemplateWithRequest(template, request);
        
        // Process cloud-init with variables
        request.CloudInit = _templateService.SubstituteVariables(
            template.CloudInitTemplate,
            GetTemplateVariables(vm)
        );
    }
    
    // Rest of existing logic...
    
    // After successful creation, track deployment
    if (!string.IsNullOrEmpty(templateId))
    {
        await _templateService.IncrementDeploymentCountAsync(templateId);
        vm.TemplateId = templateId;
    }
}
```

**Acceptance Criteria:**
- [ ] VmService accepts templateId
- [ ] Template merging logic works
- [ ] Cloud-init variable substitution
- [ ] Deployment tracking
- [ ] Backward compatible (works without templateId)

**Estimated Time:** 1 hour

---

#### Task 1.5: Seed Curated Templates (1.5 hours)

**File to Create:**
- `Data/CuratedTemplates.json`
- `Services/TemplateSeeder.cs`

**Implementation:**

```csharp
public class TemplateSeeder
{
    public async Task SeedCuratedTemplatesAsync()
    {
        // Load from JSON or define in code
        var templates = GetCuratedTemplates();
        
        foreach (var template in templates)
        {
            // Check if already exists (by slug)
            var existing = await _dataStore.GetTemplateBySlugAsync(template.Slug);
            if (existing == null)
            {
                await _dataStore.SaveTemplateAsync(template);
                _logger.LogInformation($"Seeded template: {template.Name}");
            }
        }
    }
    
    private List<VmTemplate> GetCuratedTemplates()
    {
        // Return list of 10-15 templates (see catalog below)
    }
}
```

**Run seeder on startup:**

```csharp
// Program.cs
if (args.Contains("--seed-templates"))
{
    var seeder = app.Services.GetRequiredService<TemplateSeeder>();
    await seeder.SeedCuratedTemplatesAsync();
}
```

**Acceptance Criteria:**
- [ ] All curated templates defined
- [ ] Cloud-init scripts tested
- [ ] Seeder runs successfully
- [ ] Templates visible in database
- [ ] Can reseed without duplicates

**Estimated Time:** 1.5 hours

---

### Frontend Tasks (Est: 8-10 hours)

#### Task 2.1: Marketplace Home Page (2 hours)

**File to Create:**
- `wwwroot/marketplace.html`
- `wwwroot/src/marketplace-home.js`
- `wwwroot/styles/marketplace.css` (enhance existing)

**Layout:**

```html
<!-- Marketplace Home -->
<div class="page" id="marketplace-page">
    <!-- Hero Section -->
    <div class="marketplace-hero">
        <h1>🎨 Template Marketplace</h1>
        <p>Deploy production-ready VMs in minutes</p>
        
        <!-- Search Bar -->
        <div class="marketplace-search">
            <input type="text" placeholder="Search templates...">
            <button>🔍 Search</button>
        </div>
    </div>
    
    <!-- Categories -->
    <div class="marketplace-categories">
        <button class="category-pill" data-category="ai-ml">
            🤖 AI & ML (8)
        </button>
        <button class="category-pill" data-category="web-apps">
            🌐 Web Apps (3)
        </button>
        <button class="category-pill" data-category="dev-tools">
            🛠️ Dev Tools (4)
        </button>
    </div>
    
    <!-- Featured Templates -->
    <div class="marketplace-section">
        <h2>🌟 Featured Templates</h2>
        <div class="template-grid" id="featured-templates">
            <!-- Template cards dynamically loaded -->
        </div>
    </div>
    
    <!-- All Templates -->
    <div class="marketplace-section">
        <h2>📦 All Templates</h2>
        <div class="marketplace-filters">
            <select id="sort-select">
                <option value="popular">Most Popular</option>
                <option value="newest">Newest</option>
                <option value="name">Name A-Z</option>
            </select>
            <label>
                <input type="checkbox" id="gpu-only"> GPU Only
            </label>
        </div>
        <div class="template-grid" id="all-templates">
            <!-- Template cards -->
        </div>
    </div>
</div>
```

**Template Card Component:**

```javascript
function renderTemplateCard(template) {
    return `
        <div class="template-card" data-template-id="${template.id}">
            <div class="template-icon">
                ${template.iconUrl 
                    ? `<img src="${template.iconUrl}">`
                    : getDefaultIcon(template.category)
                }
            </div>
            
            <div class="template-header">
                <h3 class="template-name">${template.name}</h3>
                ${template.isVerified ? '<span class="badge-verified">✓</span>' : ''}
                ${template.isFeatured ? '<span class="badge-featured">🌟</span>' : ''}
            </div>
            
            <p class="template-description">${template.description}</p>
            
            <div class="template-tags">
                ${template.tags.map(tag => 
                    `<span class="tag">${tag}</span>`
                ).join('')}
            </div>
            
            <div class="template-stats">
                <span>🚀 ${template.deploymentCount} deploys</span>
                ${template.requiresGpu ? '<span class="gpu-badge">GPU</span>' : ''}
            </div>
            
            <div class="template-footer">
                <span class="template-cost">~$${template.estimatedCostPerHour}/hr</span>
                <button class="btn-deploy" onclick="deployTemplate('${template.id}')">
                    🚀 Deploy
                </button>
            </div>
        </div>
    `;
}
```

**Acceptance Criteria:**
- [ ] Marketplace home page loads
- [ ] Categories display correctly
- [ ] Featured templates section
- [ ] All templates grid
- [ ] Basic filtering (category, GPU, sort)
- [ ] Template cards look good
- [ ] Click card → goes to detail page

**Estimated Time:** 2 hours

---

#### Task 2.2: Template Detail Page (2.5 hours)

**File to Create:**
- `wwwroot/src/template-detail.js`

**Layout:**

```html
<div class="template-detail-page">
    <!-- Header -->
    <div class="detail-header">
        <button onclick="goBack()">← Back</button>
        
        <div class="detail-hero">
            <div class="detail-icon">[Icon]</div>
            <div class="detail-info">
                <h1 id="template-name"></h1>
                <p id="template-author">by DeCloud</p>
                <div class="detail-badges">
                    <!-- Verified, Featured badges -->
                </div>
            </div>
        </div>
        
        <div class="detail-stats">
            <span>🚀 <span id="deploy-count">0</span> deployments</span>
        </div>
    </div>
    
    <!-- Description -->
    <div class="detail-section">
        <h2>📝 Description</h2>
        <div id="template-description-full" class="markdown-content">
            <!-- Rendered markdown -->
        </div>
    </div>
    
    <!-- Requirements -->
    <div class="detail-section">
        <h2>💻 Requirements</h2>
        <div class="requirements-grid">
            <div class="req-card">
                <h3>Minimum</h3>
                <ul id="min-spec"></ul>
                <span class="req-cost" id="min-cost"></span>
            </div>
            <div class="req-card recommended">
                <h3>Recommended ⭐</h3>
                <ul id="rec-spec"></ul>
                <span class="req-cost" id="rec-cost"></span>
            </div>
        </div>
    </div>
    
    <!-- Access Info -->
    <div class="detail-section">
        <h2>🔗 Access</h2>
        <div id="access-info">
            <!-- Ports, URLs, authentication -->
        </div>
    </div>
    
    <!-- Tags -->
    <div class="detail-section">
        <h2>🏷️ Tags</h2>
        <div id="template-tags-detail"></div>
    </div>
    
    <!-- Deploy Section -->
    <div class="detail-deploy-sticky">
        <div class="deploy-config">
            <label>
                <input type="radio" name="spec-tier" value="minimum"> 
                Minimum (~$0.25/hr)
            </label>
            <label>
                <input type="radio" name="spec-tier" value="recommended" checked> 
                Recommended (~$0.85/hr) ⭐
            </label>
            <label>
                <input type="radio" name="spec-tier" value="custom"> 
                Custom
            </label>
        </div>
        <button class="btn-deploy-large" onclick="deployTemplateNow()">
            🚀 Deploy Now
        </button>
    </div>
</div>
```

**JavaScript Functions:**

```javascript
async function loadTemplateDetail(slugOrId) {
    const template = await fetchTemplate(slugOrId);
    
    // Populate all fields
    document.getElementById('template-name').textContent = template.name;
    // ... etc
    
    // Render markdown description
    renderMarkdown(template.longDescription);
    
    // Show requirements
    renderRequirements(template.minimumSpec, template.recommendedSpec);
    
    // Show ports/access
    renderAccessInfo(template.exposedPorts);
}

async function deployTemplateNow() {
    const templateId = getCurrentTemplateId();
    const specTier = getSelectedSpecTier();
    
    // Show deploy modal with template pre-filled
    showDeployModal(templateId, specTier);
}
```

**Acceptance Criteria:**
- [ ] Detail page loads from slug or ID
- [ ] All template info displayed correctly
- [ ] Markdown description renders
- [ ] Requirements shown (min/recommended)
- [ ] Spec tier selection works
- [ ] Deploy button opens modal
- [ ] Cost estimates accurate

**Estimated Time:** 2.5 hours

---

#### Task 2.3: Deploy from Template Flow (2 hours)

**File to Modify:**
- `wwwroot/src/app.js` - Enhance createVM()
- `wwwroot/index.html` - Add template info to modal

**Enhanced VM Creation Modal:**

```html
<!-- Add to create-vm-modal -->
<div id="template-deploy-banner" style="display: none;">
    <div class="deploy-from-template-banner">
        <div>
            <strong>📦 Deploying from template:</strong>
            <span id="template-name-banner"></span>
        </div>
        <button onclick="clearTemplate()">Use Blank VM</button>
    </div>
</div>

<!-- Show template-filled specs (read-only or adjustable) -->
<div id="template-spec-preview" style="display: none;">
    <div class="spec-preview">
        <h4>Template Configuration</h4>
        <ul>
            <li>CPU: <span id="template-cpu"></span> cores</li>
            <li>Memory: <span id="template-memory"></span> GB</li>
            <li>Storage: <span id="template-storage"></span> GB</li>
        </ul>
        <label>
            <input type="checkbox" id="allow-spec-override">
            Customize specifications
        </label>
    </div>
</div>
```

**JavaScript:**

```javascript
// Global state for template deployment
window.templateDeployState = {
    templateId: null,
    templateName: null,
    spec: null
};

function deployTemplate(templateId) {
    // Load template
    fetch(`${orchestratorUrl}/api/marketplace/templates/${templateId}`)
        .then(r => r.json())
        .then(data => {
            const template = data.data;
            
            // Set state
            window.templateDeployState = {
                templateId: template.id,
                templateName: template.name,
                spec: template.recommendedSpec
            };
            
            // Pre-fill VM creation form
            prefillFromTemplate(template);
            
            // Show modal
            openCreateVMModal();
            
            // Show template banner
            showTemplateBanner(template.name);
        });
}

function prefillFromTemplate(template) {
    document.getElementById('vm-name').value = 
        `${template.slug}-${Date.now()}`;
    
    document.getElementById('vm-cpu').value = 
        template.recommendedSpec.virtualCpuCores;
    
    document.getElementById('vm-memory').value = 
        Math.floor(template.recommendedSpec.memoryBytes / (1024 * 1024));
    
    document.getElementById('vm-disk').value = 
        Math.floor(template.recommendedSpec.diskBytes / (1024 * 1024 * 1024));
    
    // Lock fields (or mark as template-provided)
    markFieldsAsTemplateProvided();
}

// Modify createVM() to include templateId
async function createVM() {
    const templateId = window.templateDeployState.templateId;
    
    const requestBody = {
        name: name,
        spec: { ... },
        templateId: templateId  // NEW: Include template ID
    };
    
    // ... rest of existing logic
}
```

**Acceptance Criteria:**
- [ ] Deploy button on template card works
- [ ] Deploy button on detail page works
- [ ] VM creation modal shows template banner
- [ ] Specs pre-filled from template
- [ ] User can override specs (optional)
- [ ] Template ID sent to backend
- [ ] After deployment, shows template-specific info

**Estimated Time:** 2 hours

---

#### Task 2.4: Navigation & Integration (1 hour)

**Files to Modify:**
- `wwwroot/index.html` - Add marketplace nav item
- `wwwroot/src/app.js` - Add route handling

**Changes:**

```html
<!-- Navigation -->
<nav class="nav">
    <a href="#vms" class="nav-link" onclick="showPage('vms')">
        <span class="nav-icon">💻</span> VMs
    </a>
    <a href="#nodes" class="nav-link" onclick="showPage('nodes')">
        <span class="nav-icon">🖥️</span> Nodes
    </a>
    <!-- NEW -->
    <a href="#marketplace" class="nav-link" onclick="showPage('marketplace')">
        <span class="nav-icon">🎨</span> Marketplace
    </a>
    <a href="#keys" class="nav-link" onclick="showPage('keys')">
        <span class="nav-icon">🔑</span> SSH Keys
    </a>
    <a href="#settings" class="nav-link" onclick="showPage('settings')">
        <span class="nav-icon">⚙️</span> Settings
    </a>
</nav>
```

```javascript
// app.js - Add marketplace to showPage()
function showPage(pageName) {
    // ... existing code ...
    
    if (pageName === 'marketplace') {
        loadMarketplacePage();
    }
}

async function loadMarketplacePage() {
    // Load categories
    const categories = await fetchCategories();
    renderCategories(categories);
    
    // Load featured templates
    const featured = await fetchTemplates({ featured: true });
    renderTemplateGrid('featured-templates', featured);
    
    // Load all templates
    const all = await fetchTemplates();
    renderTemplateGrid('all-templates', all);
}
```

**Acceptance Criteria:**
- [ ] Marketplace link in nav
- [ ] Clicking nav loads marketplace
- [ ] URL hash updates (#marketplace)
- [ ] Back button works
- [ ] Deep linking works (share URL)

**Estimated Time:** 1 hour

---

#### Task 2.5: Styling & Polish (1.5 hours)

**File to Modify:**
- `wwwroot/styles.css`

**Add Marketplace Styles:**

```css
/* Marketplace Home */
.marketplace-hero {
    text-align: center;
    padding: 60px 20px;
    background: linear-gradient(135deg, var(--accent-primary), var(--accent-secondary));
    border-radius: var(--radius-lg);
    margin-bottom: 40px;
}

.marketplace-search {
    max-width: 600px;
    margin: 20px auto 0;
    display: flex;
    gap: 12px;
}

.marketplace-categories {
    display: flex;
    gap: 12px;
    justify-content: center;
    margin-bottom: 40px;
    flex-wrap: wrap;
}

.category-pill {
    padding: 12px 24px;
    border: 2px solid var(--border-default);
    border-radius: 999px;
    background: var(--bg-elevated);
    cursor: pointer;
    transition: all var(--transition-fast);
}

.category-pill:hover,
.category-pill.active {
    border-color: var(--accent-primary);
    background: var(--accent-primary);
    color: var(--bg-deep);
}

/* Template Cards */
.template-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(300px, 1fr));
    gap: 24px;
    margin-top: 24px;
}

.template-card {
    background: var(--bg-elevated);
    border: 1px solid var(--border-default);
    border-radius: var(--radius-lg);
    padding: 20px;
    cursor: pointer;
    transition: all var(--transition-normal);
}

.template-card:hover {
    border-color: var(--accent-primary);
    transform: translateY(-4px);
    box-shadow: 0 8px 24px var(--glow-primary);
}

.template-icon {
    width: 64px;
    height: 64px;
    border-radius: var(--radius-md);
    background: var(--bg-secondary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 32px;
    margin-bottom: 16px;
}

.template-header {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-bottom: 12px;
}

.template-name {
    font-size: 18px;
    font-weight: 600;
    margin: 0;
}

.badge-verified,
.badge-featured {
    font-size: 14px;
}

.template-description {
    color: var(--text-secondary);
    font-size: 14px;
    margin-bottom: 16px;
    line-height: 1.5;
}

.template-tags {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 16px;
}

.template-tags .tag {
    padding: 4px 12px;
    background: var(--bg-secondary);
    border-radius: 999px;
    font-size: 12px;
    color: var(--text-muted);
}

.template-stats {
    display: flex;
    gap: 12px;
    font-size: 13px;
    color: var(--text-muted);
    margin-bottom: 16px;
}

.gpu-badge {
    background: linear-gradient(135deg, #667eea, #764ba2);
    color: white;
    padding: 2px 8px;
    border-radius: 4px;
    font-weight: 600;
}

.template-footer {
    display: flex;
    justify-content: space-between;
    align-items: center;
    padding-top: 16px;
    border-top: 1px solid var(--border-subtle);
}

.template-cost {
    font-size: 16px;
    font-weight: 600;
    color: var(--accent-primary);
    font-family: var(--font-mono);
}

.btn-deploy {
    padding: 8px 20px;
    background: linear-gradient(135deg, var(--accent-primary), var(--accent-secondary));
    border: none;
    border-radius: var(--radius-sm);
    color: var(--bg-deep);
    font-weight: 600;
    cursor: pointer;
    transition: all var(--transition-fast);
}

.btn-deploy:hover {
    transform: scale(1.05);
    box-shadow: 0 4px 12px var(--glow-primary);
}

/* Template Detail Page */
.template-detail-page {
    max-width: 900px;
    margin: 0 auto;
}

.detail-hero {
    display: flex;
    gap: 24px;
    align-items: center;
    margin-bottom: 40px;
}

.detail-icon {
    width: 120px;
    height: 120px;
    border-radius: var(--radius-lg);
    background: var(--bg-secondary);
    display: flex;
    align-items: center;
    justify-content: center;
    font-size: 64px;
}

.detail-section {
    background: var(--bg-elevated);
    border-radius: var(--radius-lg);
    padding: 24px;
    margin-bottom: 24px;
}

.requirements-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 20px;
}

.req-card {
    padding: 20px;
    background: var(--bg-secondary);
    border-radius: var(--radius-md);
    border: 2px solid var(--border-subtle);
}

.req-card.recommended {
    border-color: var(--accent-primary);
    box-shadow: 0 0 20px var(--glow-primary);
}

.detail-deploy-sticky {
    position: sticky;
    bottom: 0;
    background: var(--bg-elevated);
    border-top: 2px solid var(--border-default);
    padding: 20px;
    display: flex;
    justify-content: space-between;
    align-items: center;
}

.btn-deploy-large {
    padding: 16px 48px;
    font-size: 18px;
    background: linear-gradient(135deg, var(--accent-primary), var(--accent-secondary));
    border: none;
    border-radius: var(--radius-md);
    color: var(--bg-deep);
    font-weight: 700;
    cursor: pointer;
    transition: all var(--transition-normal);
}

.btn-deploy-large:hover {
    transform: scale(1.05);
    box-shadow: 0 8px 24px var(--glow-primary);
}
```

**Acceptance Criteria:**
- [ ] All marketplace components styled consistently
- [ ] Cards have hover effects
- [ ] Colors match existing theme
- [ ] Responsive (works on mobile)
- [ ] Loading states look good
- [ ] Empty states handled

**Estimated Time:** 1.5 hours

---

## Data Models (Simplified for Phase 1)

### VmTemplate Model

```csharp
public class VmTemplate
{
    // Identity
    public string Id { get; set; }                    // ULID
    public string Name { get; set; }                  // "Stable Diffusion WebUI"
    public string Slug { get; set; }                  // "stable-diffusion-webui"
    public string Version { get; set; } = "1.0.0";
    
    // Metadata
    public string Description { get; set; }           // Short (280 chars)
    public string? LongDescription { get; set; }      // Markdown
    public string Category { get; set; }              // "ai-ml"
    public List<string> Tags { get; set; } = new();
    public string? IconUrl { get; set; }
    public List<string> Screenshots { get; set; } = new();
    
    // Author (Phase 1: Only platform)
    public string AuthorId { get; set; } = "platform";
    public string AuthorName { get; set; } = "DeCloud";
    
    // Specifications
    public VmSpec MinimumSpec { get; set; }
    public VmSpec RecommendedSpec { get; set; }
    
    // Requirements
    public bool RequiresGpu { get; set; }
    public string? GpuRequirement { get; set; }       // "RTX 3060+"
    public List<string> RequiredCapabilities { get; set; } = new();
    
    // Cloud-Init
    public string CloudInitTemplate { get; set; }     // Full YAML
    public Dictionary<string, string> DefaultEnvironmentVariables { get; set; } = new();
    
    // Ports
    public List<PortMapping> ExposedPorts { get; set; } = new();
    public string? DefaultAccessUrl { get; set; }     // "https://{vm-id}.decloud.app:7860"
    
    // Stats
    public int DeploymentCount { get; set; }
    public DateTime? LastDeployedAt { get; set; }
    
    // Status
    public TemplateStatus Status { get; set; } = TemplateStatus.Published;
    public bool IsFeatured { get; set; }
    public bool IsVerified { get; set; } = true;      // All Phase 1 templates verified
    
    // Timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    
    // Pricing (Phase 1: Just estimates)
    public decimal EstimatedCostPerHour { get; set; }
    
    // Phase 2+ fields (add later)
    // public double AverageRating { get; set; }
    // public int RatingCount { get; set; }
    // public int ForkCount { get; set; }
    // public bool IsCommunityPick { get; set; }
}

public enum TemplateStatus
{
    Draft,          // Not ready
    Published,      // Live
    Archived        // Deprecated
}

public class PortMapping
{
    public int Port { get; set; }
    public string Protocol { get; set; }        // "http", "https"
    public string Description { get; set; }
    public bool IsPublic { get; set; }
}
```

### TemplateCategory Model

```csharp
public class TemplateCategory
{
    public string Id { get; set; }
    public string Name { get; set; }            // "AI & Machine Learning"
    public string Slug { get; set; }            // "ai-ml"
    public string Description { get; set; }
    public string IconEmoji { get; set; }       // "🤖"
    public int DisplayOrder { get; set; }
    public int TemplateCount { get; set; }      // Cached
}
```

### VirtualMachine Enhancement

```csharp
// Add to existing VirtualMachine model
public class VirtualMachine
{
    // ... existing fields ...
    
    // NEW: Template tracking
    public string? TemplateId { get; set; }     // If deployed from template
    public string? TemplateName { get; set; }   // Cached for display
    public string? TemplateVersion { get; set; } // Track version used
}
```

---

## Curated Template Catalog

### Phase 1 Launch Templates (15 templates)

#### Category: AI & Machine Learning (8 templates)

**1. Stable Diffusion WebUI**
```yaml
Name: Stable Diffusion WebUI
Slug: stable-diffusion-webui
Category: ai-ml
Tags: [gpu, stable-diffusion, ai, image-generation]
RequiresGpu: true
GpuRequirement: "RTX 3060 or better"

MinimumSpec:
  CPU: 4 cores
  Memory: 16 GB
  Storage: 50 GB
  
RecommendedSpec:
  CPU: 8 cores
  Memory: 24 GB
  Storage: 100 GB
  
ExposedPorts:
  - Port: 7860
    Protocol: http
    Description: Web UI
    IsPublic: true

EstimatedCost: $0.85/hour

CloudInit: |
  #cloud-config
  packages:
    - python3.11
    - python3-pip
    - git
    - wget
    - nvidia-cuda-toolkit
  
  runcmd:
    # Install AUTOMATIC1111 WebUI
    - cd /opt && git clone https://github.com/AUTOMATIC1111/stable-diffusion-webui
    - cd stable-diffusion-webui && ./webui.sh --skip-torch-cuda-test --exit
    
    # Create systemd service
    - |
      cat > /etc/systemd/system/sdwebui.service <<EOF
      [Unit]
      Description=Stable Diffusion WebUI
      After=network.target
      
      [Service]
      Type=simple
      User=root
      WorkingDirectory=/opt/stable-diffusion-webui
      ExecStart=/opt/stable-diffusion-webui/webui.sh --listen --port 7860
      Restart=always
      
      [Install]
      WantedBy=multi-user.target
      EOF
    
    - systemctl daemon-reload
    - systemctl enable sdwebui
    - systemctl start sdwebui
```

**2. LLaMA Chat**
```yaml
Name: LLaMA Chat Interface
Slug: llama-chat
Category: ai-ml
Tags: [gpu, llm, chat, llama]
RequiresGpu: true
RecommendedSpec: 8 CPU, 32 GB, 200 GB
ExposedPorts: [8000]
EstimatedCost: $1.20/hour
```

**3. ComfyUI**
```yaml
Name: ComfyUI - Node-based Stable Diffusion
Slug: comfyui
Category: ai-ml
Tags: [gpu, stable-diffusion, workflow]
RequiresGpu: true
RecommendedSpec: 8 CPU, 24 GB, 80 GB
ExposedPorts: [8188]
EstimatedCost: $0.85/hour
```

**4. Whisper Transcription API**
```yaml
Name: Whisper Speech-to-Text API
Slug: whisper-api
Category: ai-ml
Tags: [gpu, transcription, api]
RequiresGpu: true
RecommendedSpec: 4 CPU, 16 GB, 50 GB
ExposedPorts: [8000]
EstimatedCost: $0.60/hour
```

**5. Jupyter Data Science**
```yaml
Name: Jupyter Lab for Data Science
Slug: jupyter-datascience
Category: ai-ml
Tags: [jupyter, python, data-science]
RequiresGpu: false
RecommendedSpec: 4 CPU, 8 GB, 50 GB
ExposedPorts: [8888]
EstimatedCost: $0.25/hour
```

**6. PyTorch Research Environment**
```yaml
Name: PyTorch Research Environment
Slug: pytorch-research
Category: ai-ml
Tags: [gpu, pytorch, cuda, research]
RequiresGpu: true
RecommendedSpec: 8 CPU, 32 GB, 100 GB
ExposedPorts: [8888]
EstimatedCost: $1.10/hour
```

**7. TensorFlow Training Server**
```yaml
Name: TensorFlow GPU Training Server
Slug: tensorflow-training
Category: ai-ml
Tags: [gpu, tensorflow, training]
RequiresGpu: true
RecommendedSpec: 8 CPU, 32 GB, 150 GB
ExposedPorts: [6006]  # TensorBoard
EstimatedCost: $1.10/hour
```

**8. Ollama Local LLM**
```yaml
Name: Ollama - Run LLMs Locally
Slug: ollama-llm
Category: ai-ml
Tags: [llm, ollama, api]
RequiresGpu: true
RecommendedSpec: 8 CPU, 24 GB, 100 GB
ExposedPorts: [11434]
EstimatedCost: $0.85/hour
```

#### Category: Web Applications (3 templates)

**9. WordPress + MySQL**
```yaml
Name: WordPress with MySQL
Slug: wordpress-mysql
Category: web-apps
Tags: [wordpress, php, mysql, cms]
RequiresGpu: false
RecommendedSpec: 2 CPU, 4 GB, 30 GB
ExposedPorts: [80]
EstimatedCost: $0.12/hour
```

**10. Node.js + Redis**
```yaml
Name: Node.js Application Server
Slug: nodejs-redis
Category: web-apps
Tags: [nodejs, redis, api]
RequiresGpu: false
RecommendedSpec: 2 CPU, 4 GB, 20 GB
ExposedPorts: [3000]
EstimatedCost: $0.12/hour
```

**11. Nginx Static Site**
```yaml
Name: Nginx Static Website Host
Slug: nginx-static
Category: web-apps
Tags: [nginx, static, hosting]
RequiresGpu: false
MinimumSpec: 1 CPU, 1 GB, 10 GB
ExposedPorts: [80]
EstimatedCost: $0.05/hour
```

#### Category: Development Tools (4 templates)

**12. VS Code Server**
```yaml
Name: VS Code Server (code-server)
Slug: vscode-server
Category: dev-tools
Tags: [vscode, ide, development]
RequiresGpu: false
RecommendedSpec: 2 CPU, 4 GB, 30 GB
ExposedPorts: [8080]
EstimatedCost: $0.12/hour

CloudInit: |
  #cloud-config
  packages:
    - curl
    - git
  
  runcmd:
    - curl -fsSL https://code-server.dev/install.sh | sh
    - systemctl enable --now code-server@root
    - sed -i 's/127.0.0.1:8080/0.0.0.0:8080/' ~/.config/code-server/config.yaml
    - systemctl restart code-server@root
```

**13. Docker Host**
```yaml
Name: Docker Host with Portainer
Slug: docker-host
Category: dev-tools
Tags: [docker, containers, portainer]
RequiresGpu: false
RecommendedSpec: 4 CPU, 8 GB, 100 GB
ExposedPorts: [9000, 9443]
EstimatedCost: $0.20/hour
```

**14. GitLab Runner**
```yaml
Name: GitLab CI/CD Runner
Slug: gitlab-runner
Category: dev-tools
Tags: [gitlab, ci-cd, runner]
RequiresGpu: false
RecommendedSpec: 2 CPU, 4 GB, 50 GB
EstimatedCost: $0.12/hour
```

**15. MongoDB Database**
```yaml
Name: MongoDB Database Server
Slug: mongodb-server
Category: dev-tools
Tags: [mongodb, database, nosql]
RequiresGpu: false
RecommendedSpec: 2 CPU, 4 GB, 50 GB
ExposedPorts: [27017]
EstimatedCost: $0.12/hour
```

---

## Testing Strategy

### Manual Testing Checklist

**Template Browsing:**
- [ ] Marketplace home page loads
- [ ] Categories display with correct counts
- [ ] Featured templates show (if any marked featured)
- [ ] All templates grid displays
- [ ] Category filter works
- [ ] GPU filter works
- [ ] Sort dropdown changes order
- [ ] Search bar filters templates

**Template Detail:**
- [ ] Click template card opens detail page
- [ ] All template info displays correctly
- [ ] Markdown description renders
- [ ] Requirements show min/recommended
- [ ] Ports/access info displayed
- [ ] Tags render correctly
- [ ] Cost estimates accurate

**Deployment:**
- [ ] Deploy button opens VM creation modal
- [ ] Template banner shows in modal
- [ ] Specs pre-filled from template
- [ ] Can override specs
- [ ] VM creates successfully
- [ ] Cloud-init runs correctly
- [ ] Services start automatically
- [ ] Can access via ingress URL
- [ ] Deployment count increments

**Integration:**
- [ ] Marketplace nav link works
- [ ] Can deploy from marketplace and node marketplace
- [ ] VM list shows template name
- [ ] Template ID tracked in database

### Test Each Curated Template

For each of the 15 templates:
- [ ] Deploy successfully
- [ ] Service starts within 5 minutes
- [ ] Can access via browser (if web UI)
- [ ] Basic functionality works
- [ ] Cost estimate within 10% of actual

---

## Launch Checklist

### Pre-Launch

**Backend:**
- [ ] All API endpoints tested
- [ ] Template seeder runs successfully
- [ ] 15 curated templates in database
- [ ] Cloud-init variable substitution working
- [ ] Error handling covers edge cases
- [ ] Logging in place

**Frontend:**
- [ ] All pages styled consistently
- [ ] Responsive on mobile/tablet
- [ ] Loading states implemented
- [ ] Error states handled gracefully
- [ ] Empty states look good
- [ ] Images/icons optimized

**Infrastructure:**
- [ ] MongoDB indexes created
- [ ] Backup strategy in place
- [ ] Monitoring configured
- [ ] SSL certificates working

### Launch Day

- [ ] Seed curated templates to production
- [ ] Verify all templates deploy successfully
- [ ] Test from fresh user account
- [ ] Monitor deployment success rate
- [ ] Watch for errors in logs
- [ ] Collect initial user feedback

### Post-Launch (Week 1)

- [ ] Track metrics:
  - Templates deployed per day
  - Deployment success rate
  - Average deployment time
  - Most popular templates
  - User feedback
- [ ] Fix critical bugs immediately
- [ ] Document common issues
- [ ] Plan Phase 2 (community features)

---

## Success Metrics (Week 1 Targets)

| Metric | Target | Measurement |
|--------|--------|-------------|
| Templates Deployed | 20+ | MongoDB query |
| Successful Deployments | 95%+ | Success vs failure rate |
| Avg Deployment Time | <5 min | From API call to running |
| Template Views | 100+ | Page view tracking |
| Most Popular Category | AI/ML expected | Category breakdown |
| User Feedback | Positive | Qualitative assessment |

---

## Phase 2 Preview

After Phase 1 is stable (1 week), we'll add:

**Community Features:**
- Template creation UI (wizard)
- User-submitted templates (approval queue)
- Rating & review system
- Fork/clone functionality
- User profiles & portfolios

**Enhanced Discovery:**
- Advanced search
- Template collections
- Trending/rising stars
- Community picks

**Analytics:**
- Creator dashboards
- Deployment analytics
- Cost tracking
- Performance metrics

---

## Timeline

### Day 1: Backend Foundation
- Morning: Data models & MongoDB (Task 1.1)
- Afternoon: Template Service (Task 1.2)
- Evening: Marketplace Controller (Task 1.3)

### Day 2: Backend Integration
- Morning: VmService integration (Task 1.4)
- Afternoon: Curated templates (Task 1.5)
- Evening: Testing & fixes

### Day 3: Frontend Core
- Morning: Marketplace home page (Task 2.1)
- Afternoon: Template detail page (Task 2.2)
- Evening: Deploy flow (Task 2.3)

### Day 4: Polish & Launch
- Morning: Navigation & styling (Task 2.4, 2.5)
- Afternoon: End-to-end testing
- Evening: Launch! 🚀

---

## Questions & Decisions

### Resolved:
✅ MVP scope: Phase 1 only  
✅ Template strategy: Hybrid (curated → community)  
✅ Immutability: Hybrid (versioned published, editable drafts)  
✅ Monetization: Optional from start  

### Open Questions:
- Should we track deployment duration for each template?
- Do we want template "quick start" guides (separate from description)?
- Should users be able to favorite templates in Phase 1 or Phase 2?
- What's the approval process for community templates (Phase 2)?

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Cloud-init fails | High | Test each template thoroughly; add rollback |
| Templates don't work on all nodes | Medium | Mark templates with node requirements |
| Deployment takes >10 minutes | Medium | Optimize cloud-init; show progress |
| Users want to customize templates | Low | Phase 1: Allow spec override; Phase 2: Fork |
| Template quality issues | Low | Phase 1: Curated only; Phase 2: Verification |

---

## Next Steps

1. **Review this plan** - Any adjustments needed?
2. **Set up project board** - Create GitHub issues for each task
3. **Start Day 1** - Begin with backend foundation
4. **Daily standups** - Quick sync on progress
5. **Ship Phase 1** - Launch in 3-4 days!

---

**Ready to start implementing? Let's build! 🚀**
