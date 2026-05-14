// ============================================================================
// My Templates Module
// Create, manage, and publish user-owned VM templates
// ============================================================================

import { escapeHtml as sharedEscapeHtml, escapeJs, showToast as sharedShowToast, renderMarkdown } from './utils.js';

let myTemplates = [];

// Artifacts staged in-memory during a create/edit session. In create mode they
// are submitted as part of the POST /create payload. In edit mode they are
// posted one-by-one to POST /{id}/artifacts when the user clicks "Add".
let pendingArtifacts = [];

// Active mode for the new-artifact form: 'upload' | 'paste' | 'url'.
let artifactMode = 'upload';

// What the artifact form is editing right now:
//   null                                     — adding a brand-new artifact
//   { kind: 'pending', index }               — editing a client-staged entry
//   { kind: 'existing', templateId, id }     — editing one persisted on the server
let editingArtifactTarget = null;

// ── Enum mappings (C# enums serialize as integers) ──────────────────────
const VISIBILITY_TO_INT = { 'Public': 0, 'Private': 1 };
const VISIBILITY_TO_STR = { 0: 'Public', 1: 'Private', 'Public': 'Public', 'Private': 'Private' };

const PRICING_TO_INT = { 'Free': 0, 'PerDeploy': 1 };
const PRICING_TO_STR = { 0: 'Free', 1: 'PerDeploy', 'Free': 'Free', 'PerDeploy': 'PerDeploy' };

const STATUS_TO_STR = {
    0: 'Draft', 1: 'Published', 2: 'Archived',
    'Draft': 'Draft', 'Published': 'Published', 'Archived': 'Archived'
};

const QUALITY_TIER_TO_INT = { 'Guaranteed': 0, 'Standard': 1, 'Balanced': 2, 'Burstable': 3 };
const QUALITY_TIER_TO_STR = {
    0: 'Guaranteed', 1: 'Standard', 2: 'Balanced', 3: 'Burstable',
    'Guaranteed': 'Guaranteed', 'Standard': 'Standard', 'Balanced': 'Balanced', 'Burstable': 'Burstable'
};

const BANDWIDTH_TIER_TO_INT = { 'Basic': 0, 'Standard': 1, 'Performance': 2, 'Unmetered': 3 };
const BANDWIDTH_TIER_TO_STR = {
    0: 'Basic', 1: 'Standard', 2: 'Performance', 3: 'Unmetered',
    'Basic': 'Basic', 'Standard': 'Standard', 'Performance': 'Performance', 'Unmetered': 'Unmetered'
};

// Platform variables substituted by the orchestrator before cloud-init runs.
// Static variables use __DOUBLE_UNDERSCORE__ form; dynamic boot-time vars use ${DOLLAR_BRACE} form.
const PLATFORM_STATIC_VARS = new Set([
    '__VM_ID__', '__HOSTNAME__', '__CA_PUBLIC_KEY__',
    '__SSH_AUTHORIZED_KEYS_BLOCK__', '__ORCHESTRATOR_URL__'
]);
const PLATFORM_DYNAMIC_VARS = new Set([
    'DECLOUD_PASSWORD', 'DECLOUD_DOMAIN'
]);

// runcmd entries run under /bin/sh (dash on Ubuntu/Debian), which lacks pipefail.
// Write the script as a file so it can be executed with bash.
const CLOUD_INIT_STARTER = `#cloud-config
package_update: true
packages: []

write_files:
  - path: /usr/local/bin/setup.sh
    permissions: '0755'
    content: |
      #!/bin/bash
      set -euo pipefail
      exec >> /var/log/setup.log 2>&1
      echo "=== Setup started $(date) ==="

      # --- your setup commands here ---

      echo "=== Setup complete $(date) ==="

runcmd:
  - /usr/local/bin/setup.sh
`;

function api(endpoint, options = {}) {
    if (!window.api) throw new Error('API function not available');
    return window.api(endpoint, options);
}

const escapeHtml = sharedEscapeHtml;

// Legacy callers in this file pass (type, message); the util tolerates either order.
function showToast(type, message) {
    sharedShowToast(message, type);
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
    const status = STATUS_TO_STR[template.status] ?? 'Draft';
    const statusClass = status === 'Published' ? 'status-published' :
                       status === 'Draft' ? 'status-draft' : 'status-archived';
    const statusLabel = status;

    const visibility = VISIBILITY_TO_STR[template.visibility] ?? 'Public';
    const visibilityIcon = visibility === 'Private' ? '🔒' : '🌐';
    const pricing = PRICING_TO_STR[template.pricingModel] ?? 'Free';
    const priceText = pricing === 'PerDeploy' && template.templatePrice > 0
        ? `${template.templatePrice} USDC`
        : 'Free';

    const rating = template.averageRating || 0;
    const stars = renderStars(rating);
    const reviewCount = template.totalReviews || 0;

    return `
        <div class="my-template-card">
            <div class="my-template-header">
                <span class="template-status-badge ${statusClass}">${statusLabel}</span>
                <span class="template-visibility-badge">${visibilityIcon} ${visibility}</span>
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
                ${status === 'Draft' ? `
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
    if (window.openStaticModal) window.openStaticModal(modal);
    else modal.classList.add('active');
    document.body.style.overflow = 'hidden';
}

function closeCreateModal() {
    const modal = document.getElementById('create-template-modal');
    if (modal) {
        if (window.closeStaticModal) window.closeStaticModal(modal); else modal.classList.remove('active');
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
                        <div style="display:flex; align-items:center; gap:8px; margin-bottom:4px;">
                            <label class="form-label" style="margin-bottom:0;">Long Description</label>
                            <div style="display:flex; gap:2px; margin-left:auto;">
                                <button type="button" id="ct-ld-write-btn"
                                    onclick="window.myTemplates.setLdTab('write')"
                                    style="font-size:11px; padding:2px 8px; border:1px solid var(--border-color,#333); border-radius:4px 0 0 4px; background:var(--accent-primary,#7c3aed); color:#fff; cursor:pointer;">Write</button>
                                <button type="button" id="ct-ld-preview-btn"
                                    onclick="window.myTemplates.setLdTab('preview')"
                                    style="font-size:11px; padding:2px 8px; border:1px solid var(--border-color,#333); border-left:none; border-radius:0 4px 4px 0; background:transparent; color:var(--text-muted,#9ca3af); cursor:pointer;">Preview</button>
                            </div>
                        </div>
                        <textarea class="form-input" id="ct-long-description" rows="4" placeholder="Detailed description (supports markdown)"></textarea>
                        <div id="ct-ld-preview" style="display:none; min-height:80px; padding:10px 12px; border:1px solid var(--border-color,#333); border-radius:6px; font-size:13px; line-height:1.6; background:var(--bg-secondary,#1a1a2e); color:var(--text-primary,#e2e8f0);"></div>
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Category</label>
                            <select class="form-input" id="ct-category">
                                <option value="gaming">Games</option>
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
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Icon URL</label>
                            <input type="text" class="form-input" id="ct-icon-url" placeholder="https://example.com/icon.png">
                            <p class="form-help">PNG or SVG shown in the marketplace. 128×128 recommended.</p>
                        </div>
                        <div class="form-group">
                            <label class="form-label">License</label>
                            <input type="text" class="form-input" id="ct-license" placeholder="MIT, Apache-2.0, GPL-3.0, …">
                        </div>
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
                        <div style="display:flex; align-items:center; gap:8px; margin-bottom:4px;">
                            <label class="form-label" style="margin-bottom:0;">Cloud-Init Script *</label>
                            <button type="button" onclick="window.myTemplates.insertCloudInitStarter()"
                                style="font-size:11px; padding:2px 8px; border:1px solid var(--border-color,#333); border-radius:4px; background:transparent; color:var(--text-muted,#9ca3af); cursor:pointer; margin-left:auto;">↓ Starter</button>
                        </div>
                        <textarea class="form-input" id="ct-cloudinit" rows="10"
                            placeholder="#cloud-config&#10;package_update: true&#10;packages:&#10;  - nginx"
                            style="font-family: var(--font-mono); font-size: 12px;"
                            oninput="window.myTemplates.updateSubstitutionPreview()"></textarea>
                        <div id="ct-substitution-preview" style="margin-top:6px; min-height:24px; display:flex; flex-wrap:wrap; gap:4px; align-items:center;"></div>
                        <p class="form-help">
                            Cloud-init YAML. Render-time statics (substituted by orchestrator before deploy):
                            <code>__VM_ID__</code> <code>__HOSTNAME__</code> <code>__CA_PUBLIC_KEY__</code>
                            <code>__SSH_AUTHORIZED_KEYS_BLOCK__</code> <code>__ORCHESTRATOR_URL__</code><br>
                            Boot-time env vars (from Default Environment Variables below):
                            <code>$\{DECLOUD_PASSWORD}</code> <code>$\{DECLOUD_DOMAIN}</code><br>
                            Artifact references: <code>$\{ARTIFACT_URL:name}</code> <code>$\{ARTIFACT_SHA256:name}</code>
                        </p>
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
                            <input type="number" class="form-input" id="ct-min-memory" value="512" min="256" step="128">
                        </div>
                        <div class="form-group">
                            <label class="form-label">Disk (GB)</label>
                            <input type="number" class="form-input" id="ct-min-disk" value="10" min="1">
                        </div>
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Minimum Quality Tier</label>
                            <select class="form-input" id="ct-min-quality-tier">
                                <option value="3">Burstable (Lowest Cost)</option>
                                <option value="2">Balanced</option>
                                <option value="1" selected>Standard</option>
                                <option value="0">Guaranteed (Premium)</option>
                            </select>
                            <p class="form-help">Worst allowed tier — higher tiers are always permitted at deploy time.</p>
                        </div>
                        <div class="form-group">
                            <label class="form-label">Default Bandwidth Tier</label>
                            <select class="form-input" id="ct-default-bandwidth-tier">
                                <option value="0">Basic (10 Mbps) — $0.002/hr</option>
                                <option value="1">Standard (50 Mbps) — $0.008/hr</option>
                                <option value="2">Performance (200 Mbps) — $0.020/hr</option>
                                <option value="3" selected>Unmetered — $0.040/hr</option>
                            </select>
                            <p class="form-help">Pre-selected bandwidth tier when deploying this template.</p>
                        </div>
                    </div>
                    <div class="form-group">
                        <label class="filter-label">
                            <input type="checkbox" id="ct-requires-gpu" onchange="window.myTemplates.onGpuToggle()">
                            <span>Requires GPU</span>
                        </label>
                    </div>
                    <div class="form-group" id="ct-gpu-mode-group" style="display:none;">
                        <label class="form-label">GPU Mode</label>
                        <select class="form-input" id="ct-gpu-mode">
                            <option value="1">Dedicated (Passthrough) &mdash; exclusive GPU, best performance, IOMMU required</option>
                            <option value="2">Shared (Proxied) &mdash; shared GPU via proxy, cost-effective, no IOMMU</option>
                        </select>
                        <p class="form-help">Dedicated gives full GPU access to a single VM. Shared allows multiple VMs to share one GPU at lower cost.</p>
                    </div>
                    <div class="form-group">
                        <label class="form-label">GPU Requirement Description</label>
                        <input type="text" class="form-input" id="ct-gpu-requirement" placeholder="e.g. NVIDIA RTX 3060+ with 8GB VRAM">
                        <p class="form-help">Human-readable GPU requirement shown in the marketplace.</p>
                    </div>
                </div>

                <!-- Exposed Ports -->
                <div class="form-section">
                    <h3 class="form-section-title">Exposed Ports</h3>
                    <p class="form-help" style="margin-bottom: 8px;">Define services your template exposes. Use <strong>http</strong> protocol for web services routed through ingress.</p>
                    <div id="ct-ports-list"></div>
                    <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.addPort()" type="button">
                        + Add Port
                    </button>
                </div>

                <!-- Access & Credentials -->
                <div class="form-section">
                    <h3 class="form-section-title">Access & Credentials</h3>
                    <div class="form-group">
                        <label class="form-label">Default Access URL</label>
                        <input type="text" class="form-input" id="ct-access-url" placeholder="https://$\{DECLOUD_DOMAIN}">
                        <p class="form-help">URL shown to users after deploy. Use <code>$\{DECLOUD_DOMAIN}</code> for the VM's domain.</p>
                    </div>
                    <div class="form-row">
                        <div class="form-group">
                            <label class="form-label">Default Username</label>
                            <input type="text" class="form-input" id="ct-default-username" placeholder="e.g. user, admin">
                        </div>
                        <div class="form-group" style="display:flex; align-items:center; padding-top:24px;">
                            <label class="filter-label">
                                <input type="checkbox" id="ct-use-generated-password" checked>
                                <span>Use Generated Password</span>
                            </label>
                        </div>
                    </div>
                </div>

                <!-- Environment Variables -->
                <div class="form-section">
                    <h3 class="form-section-title">Environment Variables</h3>
                    <p class="form-help" style="margin-bottom: 8px;">Default env vars passed to cloud-init. Deployers can override these.</p>
                    <div id="ct-env-vars-list"></div>
                    <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.addEnvVar()" type="button">
                        + Add Variable
                    </button>
                </div>

                <!-- Variables -->
                <div class="form-section" id="ct-variables-section">
                    <div style="display:flex; align-items:center; justify-content:space-between; cursor:pointer;"
                         onclick="window.myTemplates.toggleVariablesSection()">
                        <h3 class="form-section-title" style="margin-bottom:0;">
                            <span id="ct-variables-toggle-icon">▶</span> Variables
                            <span id="ct-variables-count" style="font-weight:normal; color:var(--text-muted,#6b7280); font-size:12px; margin-left:4px;"></span>
                        </h3>
                    </div>
                    <div id="ct-variables-body" style="display:none; margin-top:12px;">
                        <p class="form-help" style="margin-bottom: 8px;">
                            Declared variables drive the renderer's full substitution pipeline.
                            Most templates don't need this — leave empty unless your cloud-init
                            references <code>__VARNAME__</code> placeholders that should be
                            resolved by static defaults or dynamic resolvers.
                        </p>
                        <div id="ct-variables-list"></div>
                        <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.addVariable()" type="button" style="margin-top:8px;">
                            + Add Variable
                        </button>
                    </div>
                </div>

                <!-- Artifacts -->
                <div class="form-section" id="ct-artifacts-section">
                    <h3 class="form-section-title">Artifacts</h3>
                    <p class="form-help" style="margin-bottom: 8px;">
                        Files your template fetches at deploy time (binaries, scripts, dashboards).
                        Reference them in cloud-init as <code>$\{ARTIFACT_URL:name}</code> and
                        <code>$\{ARTIFACT_SHA256:name}</code>.
                        Upload or paste for inline (≤5 MB per artifact, ≤10 MB total) — the platform computes SHA256 for you.
                        Use External URL for larger files or Binary type.
                    </p>
                    <div id="ct-artifacts-list"></div>

                    <div id="ct-artifact-new-row" style="display:none; margin-top:12px; padding:12px; border:1px solid var(--border-color, #333); border-radius:6px;">
                        <!-- Mode tabs -->
                        <div style="display:flex; gap:4px; margin-bottom:12px; border-bottom:1px solid var(--border-color,#333);">
                            <button type="button" class="btn btn-sm artifact-mode-tab" data-art-mode="upload"
                                onclick="window.myTemplates.switchArtifactMode('upload')">Upload File</button>
                            <button type="button" class="btn btn-sm artifact-mode-tab" data-art-mode="paste"
                                onclick="window.myTemplates.switchArtifactMode('paste')">Paste Script</button>
                            <button type="button" class="btn btn-sm artifact-mode-tab" data-art-mode="url"
                                onclick="window.myTemplates.switchArtifactMode('url')">External URL</button>
                        </div>

                        <!-- Upload mode -->
                        <div class="artifact-mode-panel" id="ct-art-mode-upload">
                            <div class="form-group">
                                <label class="form-label" style="font-size:11px;">File</label>
                                <div id="ct-art-drop-zone"
                                    style="border:2px dashed var(--border-color,#333); border-radius:6px; padding:24px; text-align:center; cursor:pointer; background:var(--bg-secondary, #1a1a1a);">
                                    <input type="file" id="ct-art-file" style="display:none;"
                                        onchange="window.myTemplates.onArtifactFileSelected(event)">
                                    <p id="ct-art-drop-msg" style="margin:0; color:var(--text-muted,#6b7280); font-size:13px;">
                                        Click to choose a file, or drag-and-drop here
                                    </p>
                                    <p id="ct-art-file-meta" style="margin:4px 0 0; font-family:var(--font-mono); font-size:12px; color:var(--text-primary,#fff); display:none;"></p>
                                </div>
                                <p class="form-help">≤5 MB. SHA256 is computed server-side from the decoded bytes.</p>
                            </div>
                        </div>

                        <!-- Paste mode -->
                        <div class="artifact-mode-panel" id="ct-art-mode-paste" style="display:none;">
                            <div class="form-row">
                                <div class="form-group" style="flex:1">
                                    <label class="form-label" style="font-size:11px;">Content Type</label>
                                    <select class="form-input" id="ct-art-content-type">
                                        <option value="text/x-shellscript">Shell script (text/x-shellscript)</option>
                                        <option value="text/x-python">Python (text/x-python)</option>
                                        <option value="application/yaml">YAML (application/yaml)</option>
                                        <option value="application/json">JSON (application/json)</option>
                                        <option value="application/javascript">JavaScript (application/javascript)</option>
                                        <option value="text/x-systemd-unit">systemd unit (text/x-systemd-unit)</option>
                                        <option value="text/plain">Plain text (text/plain)</option>
                                    </select>
                                </div>
                            </div>
                            <div class="form-group">
                                <label class="form-label" style="font-size:11px;">Content</label>
                                <textarea class="form-input" id="ct-art-content" rows="10"
                                    style="font-family: var(--font-mono); font-size: 12px;"
                                    placeholder="#!/usr/bin/env bash&#10;# Paste your script content here..."></textarea>
                                <p class="form-help">≤5 MB. SHA256 is computed server-side from the bytes.</p>
                            </div>
                        </div>

                        <!-- External URL mode -->
                        <div class="artifact-mode-panel" id="ct-art-mode-url" style="display:none;">
                            <div class="form-group">
                                <label class="form-label" style="font-size:11px;">Source URL *</label>
                                <input type="text" class="form-input" id="ct-art-url"
                                    placeholder="https://github.com/.../releases/download/v1.0/binary-amd64">
                                <p class="form-help">HTTPS URL — the platform fetches and verifies SHA256 on registration.</p>
                            </div>
                            <div class="form-group">
                                <label class="form-label" style="font-size:11px;">SHA256 *</label>
                                <input type="text" class="form-input" id="ct-art-sha256"
                                    placeholder="64-character lowercase hex"
                                    style="font-family:var(--font-mono); font-size:12px;">
                                <p class="form-help">Required for external URLs. Run <code>sha256sum file</code> to compute.</p>
                            </div>
                        </div>

                        <!-- Common fields -->
                        <div class="form-row" style="margin-top:12px;">
                            <div class="form-group" style="flex:2">
                                <label class="form-label" style="font-size:11px;">Name *</label>
                                <input type="text" class="form-input" id="ct-art-name" placeholder="e.g. install-script">
                                <p class="form-help">Referenced as <code>$\{ARTIFACT_URL:name}</code> in cloud-init.</p>
                            </div>
                            <div class="form-group" style="flex:1">
                                <label class="form-label" style="font-size:11px;">Type</label>
                                <select class="form-input" id="ct-art-type">
                                    <option value="0">Binary</option>
                                    <option value="1" selected>Script</option>
                                    <option value="2">Web Asset</option>
                                    <option value="3">Config</option>
                                    <option value="4">Archive</option>
                                    <option value="5">Image</option>
                                </select>
                            </div>
                            <div class="form-group" style="flex:1">
                                <label class="form-label" style="font-size:11px;">Architecture</label>
                                <select class="form-input" id="ct-art-arch">
                                    <option value="">Any</option>
                                    <option value="amd64">amd64</option>
                                    <option value="arm64">arm64</option>
                                </select>
                            </div>
                        </div>
                        <div class="form-group">
                            <label class="form-label" style="font-size:11px;">Description</label>
                            <input type="text" class="form-input" id="ct-art-description" placeholder="Optional">
                        </div>

                        <div style="display:flex; gap:8px; margin-top:4px;">
                            <button class="btn btn-sm btn-primary" onclick="window.myTemplates.saveArtifact()" type="button">Add Artifact</button>
                            <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.cancelArtifact()" type="button">Cancel</button>
                        </div>
                    </div>

                    <button class="btn btn-sm btn-secondary" id="ct-add-artifact-btn"
                        onclick="window.myTemplates.showArtifactRow()" type="button" style="margin-top:8px;">
                        + Add Artifact
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

// ── F4: Long Description Write/Preview tabs ─────────────────────────────────

export function setLdTab(tab) {
    const textarea = document.getElementById('ct-long-description');
    const preview  = document.getElementById('ct-ld-preview');
    const writeBtn = document.getElementById('ct-ld-write-btn');
    const prevBtn  = document.getElementById('ct-ld-preview-btn');
    if (!textarea || !preview) return;

    if (tab === 'preview') {
        textarea.style.display = 'none';
        preview.style.display  = '';
        preview.innerHTML = renderMarkdown(textarea.value) || '<span style="color:var(--text-muted,#9ca3af); font-style:italic;">Nothing to preview yet.</span>';
        if (writeBtn) { writeBtn.style.background = 'transparent'; writeBtn.style.color = 'var(--text-muted,#9ca3af)'; }
        if (prevBtn)  { prevBtn.style.background  = 'var(--accent-primary,#7c3aed)'; prevBtn.style.color = '#fff'; }
    } else {
        textarea.style.display = '';
        preview.style.display  = 'none';
        if (writeBtn) { writeBtn.style.background = 'var(--accent-primary,#7c3aed)'; writeBtn.style.color = '#fff'; }
        if (prevBtn)  { prevBtn.style.background  = 'transparent'; prevBtn.style.color = 'var(--text-muted,#9ca3af)'; }
    }
}

// ── F6: Cloud-init starter snippet ─────────────────────────────────────────

export function insertCloudInitStarter() {
    const ta = document.getElementById('ct-cloudinit');
    if (!ta) return;
    if (ta.value.trim() && !confirm('Replace current cloud-init content with the starter template?')) return;
    ta.value = CLOUD_INIT_STARTER;
    updateSubstitutionPreview();
}

// ── F1: Variable substitution preview ──────────────────────────────────────

export function updateSubstitutionPreview() {
    const container = document.getElementById('ct-substitution-preview');
    if (!container) return;
    const text = document.getElementById('ct-cloudinit')?.value || '';

    // Collect user-defined env var keys so they show as 'dynamic' rather than 'unknown'
    const userEnvKeys = new Set(PLATFORM_DYNAMIC_VARS);
    document.querySelectorAll('#ct-env-vars-list [data-env-field="key"]').forEach(el => {
        if (el.value.trim()) userEnvKeys.add(el.value.trim());
    });

    // Collect all ${VAR} and __VAR__ references
    const found = new Map(); // name -> kind: 'static' | 'dynamic' | 'artifact' | 'unknown'

    // __STATIC__ vars
    for (const m of text.matchAll(/__([A-Z0-9_]+)__/g)) {
        const raw = `__${m[1]}__`;
        if (!found.has(raw)) {
            found.set(raw, PLATFORM_STATIC_VARS.has(raw) ? 'static' : 'unknown');
        }
    }

    // ${DYNAMIC} and ${ARTIFACT_URL:name} vars
    for (const m of text.matchAll(/\$\{([^}]+)\}/g)) {
        const inner = m[1];
        const raw = `\${${inner}}`;
        if (found.has(raw)) continue;
        if (inner.startsWith('ARTIFACT_URL:') || inner.startsWith('ARTIFACT_SHA256:')) {
            found.set(raw, 'artifact');
        } else if (userEnvKeys.has(inner)) {
            found.set(raw, 'dynamic');
        } else {
            found.set(raw, 'unknown');
        }
    }

    if (found.size === 0) {
        container.innerHTML = '';
        return;
    }

    const COLOR = {
        static:   { bg: '#14532d', border: '#16a34a', text: '#86efac', label: 'orchestrator' },
        dynamic:  { bg: '#1e3a5f', border: '#3b82f6', text: '#93c5fd', label: 'env var' },
        artifact: { bg: '#3b1f5e', border: '#8b5cf6', text: '#c4b5fd', label: 'artifact' },
        unknown:  { bg: '#292524', border: '#78716c', text: '#a8a29e', label: 'shell/literal' }
    };

    container.innerHTML = [...found.entries()].map(([name, kind]) => {
        const c = COLOR[kind] || COLOR.unknown;
        return `<span title="${escapeHtml(c.label)}" style="font-family:var(--font-mono); font-size:10px; padding:2px 6px; border-radius:4px; border:1px solid ${c.border}; background:${c.bg}; color:${c.text}; white-space:nowrap;">${escapeHtml(name)}</span>`;
    }).join('');
}

function resetCreateForm() {
    const fields = ['ct-template-id', 'ct-name', 'ct-slug', 'ct-description',
        'ct-long-description', 'ct-tags', 'ct-cloudinit', 'ct-source-url',
        'ct-access-url', 'ct-default-username', 'ct-revenue-wallet',
        'ct-icon-url', 'ct-license'];
    fields.forEach(id => {
        const el = document.getElementById(id);
        if (el) el.value = '';
    });
    document.getElementById('ct-version').value = '1.0.0';
    document.getElementById('ct-min-cpu').value = '1';
    document.getElementById('ct-min-memory').value = '512';
    document.getElementById('ct-min-disk').value = '10';
    document.getElementById('ct-requires-gpu').checked = false;
    document.getElementById('ct-gpu-mode-group').style.display = 'none';
    document.getElementById('ct-gpu-mode').value = '1';
    document.getElementById('ct-gpu-requirement').value = '';
    document.getElementById('ct-use-generated-password').checked = true;
    document.getElementById('ct-min-quality-tier').value = '1';
    document.getElementById('ct-default-bandwidth-tier').value = '3';
    document.getElementById('ct-visibility').value = 'Public';
    document.getElementById('ct-pricing').value = 'Free';
    document.getElementById('ct-price').value = '0';
    document.getElementById('ct-price-group').style.display = 'none';
    document.getElementById('ct-wallet-group').style.display = 'none';
    document.getElementById('ct-ports-list').innerHTML = '';
    document.getElementById('ct-env-vars-list').innerHTML = '';
    document.getElementById('ct-submit-btn').textContent = 'Create as Draft';
    document.getElementById('create-template-modal-title').textContent = 'Create Template';
    document.getElementById('ct-artifacts-list').innerHTML = '';
    document.getElementById('ct-artifact-new-row').style.display = 'none';
    const saveFirst = document.getElementById('ct-artifacts-save-first');
    if (saveFirst) saveFirst.style.display = '';
    document.getElementById('ct-add-artifact-btn').style.display = 'none';
    // Reset Long Description to Write tab (also resets button active styles)
    setLdTab('write');
    // Reset substitution preview
    const subPreview = document.getElementById('ct-substitution-preview');
    if (subPreview) subPreview.innerHTML = '';
    document.getElementById('ct-add-artifact-btn').style.display = '';

    // Reset pending artifacts and re-render empty list.
    pendingArtifacts = [];
    renderCombinedArtifactsList([], null);

    // Reset variables section.
    document.getElementById('ct-variables-list').innerHTML = '';
    document.getElementById('ct-variables-body').style.display = 'none';
    document.getElementById('ct-variables-toggle-icon').textContent = '▶';
    updateVariablesCount();

    artifactMode = 'upload';
    switchArtifactMode('upload');
}

export function onGpuToggle() {
    const checked = document.getElementById('ct-requires-gpu').checked;
    document.getElementById('ct-gpu-mode-group').style.display = checked ? '' : 'none';
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
    row.className = 'port-row';
    row.style.marginBottom = '12px';
    row.style.padding = '10px';
    row.style.border = '1px solid var(--border-color, #333)';
    row.style.borderRadius = '6px';
    row.innerHTML = `
        <div class="form-row" style="margin-bottom:6px;">
            <div class="form-group" style="flex:1">
                <label class="form-label" style="font-size:11px;">Port *</label>
                <input type="number" class="form-input" placeholder="8080" data-port-idx="${idx}" data-port-field="port" min="1" max="65535">
            </div>
            <div class="form-group" style="flex:1">
                <label class="form-label" style="font-size:11px;">Protocol</label>
                <select class="form-input" data-port-idx="${idx}" data-port-field="protocol" onchange="window.myTemplates.onPortProtocolChange(this)">
                    <option value="http">HTTP (web/ingress)</option>
                    <option value="https">HTTPS</option>
                    <option value="tcp">TCP</option>
                    <option value="udp">UDP</option>
                    <option value="ws">WebSocket</option>
                    <option value="wss">WebSocket Secure</option>
                </select>
            </div>
            <div class="form-group" style="flex:2">
                <label class="form-label" style="font-size:11px;">Description</label>
                <input type="text" class="form-input" placeholder="e.g. Open WebUI Chat Interface" data-port-idx="${idx}" data-port-field="description">
            </div>
            <button class="btn btn-sm btn-danger" onclick="this.closest('.port-row').remove()" type="button" style="align-self:flex-end; margin-bottom:4px;">X</button>
        </div>
        <div class="form-row" style="margin-bottom:0;">
            <div class="form-group" style="flex:0 0 auto; display:flex; align-items:center;">
                <label class="filter-label">
                    <input type="checkbox" data-port-idx="${idx}" data-port-field="isPublic" checked>
                    <span style="font-size:12px;">Public</span>
                </label>
            </div>
            <div class="form-group port-check-group" style="flex:1">
                <label class="form-label" style="font-size:11px;">Health Check Path</label>
                <input type="text" class="form-input" placeholder="/health" data-port-idx="${idx}" data-port-field="healthPath" style="font-size:12px;">
            </div>
            <div class="form-group" style="flex:0 0 100px;">
                <label class="form-label" style="font-size:11px;">Timeout (s)</label>
                <input type="number" class="form-input" placeholder="300" data-port-idx="${idx}" data-port-field="timeout" min="30" max="1800" style="font-size:12px;">
            </div>
        </div>
    `;
    list.appendChild(row);
}

export function onPortProtocolChange(select) {
    const row = select.closest('.port-row');
    const checkGroup = row.querySelector('.port-check-group');
    const isHttp = ['http', 'https', 'ws', 'wss'].includes(select.value);
    if (checkGroup) {
        checkGroup.style.display = isHttp ? '' : 'none';
    }
}

export function addEnvVar() {
    const list = document.getElementById('ct-env-vars-list');
    const row = document.createElement('div');
    row.className = 'form-row env-var-row';
    row.style.marginBottom = '6px';
    row.innerHTML = `
        <div class="form-group" style="flex:1">
            <input type="text" class="form-input" placeholder="KEY" data-env-field="key" style="font-family:var(--font-mono);font-size:12px;">
        </div>
        <div class="form-group" style="flex:2">
            <input type="text" class="form-input" placeholder="value" data-env-field="value" style="font-family:var(--font-mono);font-size:12px;">
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
        const protocol = row.querySelector('[data-port-field="protocol"]')?.value || 'http';
        const description = row.querySelector('[data-port-field="description"]')?.value || '';
        const isPublic = row.querySelector('[data-port-field="isPublic"]')?.checked ?? true;
        const healthPath = row.querySelector('[data-port-field="healthPath"]')?.value || '';
        const timeout = parseInt(row.querySelector('[data-port-field="timeout"]')?.value) || 0;
        if (port) {
            const entry = { port: parseInt(port), protocol, description, isPublic };
            // Add readiness check if health path or timeout is set
            const isHttp = ['http', 'https', 'ws', 'wss'].includes(protocol);
            if (healthPath || timeout) {
                entry.readinessCheck = {
                    strategy: isHttp ? 1 : 0, // 1=HttpGet, 0=TcpPort
                    httpPath: healthPath || (isHttp ? '/' : null),
                    timeoutSeconds: timeout || 300
                };
            }
            ports.push(entry);
        }
    });
    return ports;
}

function collectEnvVars() {
    const rows = document.querySelectorAll('#ct-env-vars-list .env-var-row');
    const envVars = {};
    rows.forEach(row => {
        const key = row.querySelector('[data-env-field="key"]')?.value?.trim();
        const value = row.querySelector('[data-env-field="value"]')?.value || '';
        if (key) envVars[key] = value;
    });
    return envVars;
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
        cloudInitTemplate: document.getElementById('ct-cloudinit').value,
        requiresGpu: document.getElementById('ct-requires-gpu').checked,
        defaultGpuMode: document.getElementById('ct-requires-gpu').checked
            ? parseInt(document.getElementById('ct-gpu-mode').value)
            : 0,
        gpuRequirement: document.getElementById('ct-gpu-requirement').value.trim() || null,
        minimumSpec: {
            virtualCpuCores: minCpu,
            memoryBytes: minMemMb * 1024 * 1024,
            diskBytes: minDiskGb * 1024 * 1024 * 1024,
            qualityTier: parseInt(document.getElementById('ct-min-quality-tier').value) ?? 1,
            bandwidthTier: parseInt(document.getElementById('ct-default-bandwidth-tier').value) ?? 3
        },
        recommendedSpec: {
            virtualCpuCores: Math.max(minCpu, 2),
            memoryBytes: Math.max(minMemMb, 2048) * 1024 * 1024,
            diskBytes: Math.max(minDiskGb, 20) * 1024 * 1024 * 1024,
            qualityTier: parseInt(document.getElementById('ct-min-quality-tier').value) ?? 1,
            bandwidthTier: parseInt(document.getElementById('ct-default-bandwidth-tier').value) ?? 3
        },
        exposedPorts: collectPorts(),
        defaultAccessUrl: document.getElementById('ct-access-url').value.trim() || null,
        defaultUsername: document.getElementById('ct-default-username').value.trim() || null,
        useGeneratedPassword: document.getElementById('ct-use-generated-password').checked,
        defaultEnvironmentVariables: collectEnvVars(),
        defaultBandwidthTier: parseInt(document.getElementById('ct-default-bandwidth-tier').value) || 3,
        visibility: VISIBILITY_TO_INT[document.getElementById('ct-visibility').value] ?? 0,
        pricingModel: PRICING_TO_INT[document.getElementById('ct-pricing').value] ?? 0,
        templatePrice: parseFloat(document.getElementById('ct-price').value) || 0,
        authorRevenueWallet: document.getElementById('ct-revenue-wallet').value.trim() || null,
        sourceUrl: document.getElementById('ct-source-url').value.trim() || null,
        iconUrl: document.getElementById('ct-icon-url').value.trim() || null,
        license: document.getElementById('ct-license').value.trim() || null,
        containerImage: document.getElementById('ct-image').value || null,
        artifacts: pendingArtifacts,
        variables: collectVariables()
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

    // F3: client-side cloud-init lint
    const ci = payload.cloudInitTemplate || '';
    if (!ci.trimStart().startsWith('#cloud-config')) {
        showToast('warning', 'Cloud-init does not start with #cloud-config — deploy may fail');
    }
    if (ci.includes('runcmd') && !ci.includes('set -e')) {
        showToast('warning', 'runcmd detected but no "set -e" — errors in runcmd will be silently ignored');
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

        const result = await response.json().catch(() => null);
        // F3: surface server-side warnings
        const warnings = result?.warnings ?? [];
        warnings.forEach(w => showToast('warning', w));

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
    document.getElementById('ct-image').value = template.containerImage || 'ubuntu-22.04';
    document.getElementById('ct-cloudinit').value = template.cloudInitTemplate || '';
    updateSubstitutionPreview();
    document.getElementById('ct-requires-gpu').checked = template.requiresGpu || false;
    document.getElementById('ct-gpu-mode').value = template.defaultGpuMode || 1;
    document.getElementById('ct-gpu-mode-group').style.display = template.requiresGpu ? '' : 'none';
    document.getElementById('ct-gpu-requirement').value = template.gpuRequirement || '';
    document.getElementById('ct-source-url').value = template.sourceUrl || '';
    document.getElementById('ct-icon-url').value = template.iconUrl || '';
    document.getElementById('ct-license').value = template.license || '';

    // Access & credentials
    document.getElementById('ct-access-url').value = template.defaultAccessUrl || '';
    document.getElementById('ct-default-username').value = template.defaultUsername || '';
    document.getElementById('ct-use-generated-password').checked = template.useGeneratedPassword ?? true;

    // Specs
    const minSpec = template.minimumSpec || {};
    document.getElementById('ct-min-cpu').value = minSpec.virtualCpuCores || 1;
    document.getElementById('ct-min-memory').value = Math.round((minSpec.memoryBytes || 536870912) / (1024 * 1024));
    document.getElementById('ct-min-disk').value = Math.round((minSpec.diskBytes || 10737418240) / (1024 * 1024 * 1024));

    // Quality & bandwidth tiers
    document.getElementById('ct-min-quality-tier').value = minSpec.qualityTier ?? 1;
    document.getElementById('ct-default-bandwidth-tier').value = template.defaultBandwidthTier ?? 3;

    // Visibility & pricing
    document.getElementById('ct-visibility').value = VISIBILITY_TO_STR[template.visibility] ?? 'Public';
    document.getElementById('ct-pricing').value = PRICING_TO_STR[template.pricingModel] ?? 'Free';
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
        row.querySelector('[data-port-field="protocol"]').value = p.protocol || 'http';
        row.querySelector('[data-port-field="description"]').value = p.description || '';
        row.querySelector('[data-port-field="isPublic"]').checked = p.isPublic ?? true;
        if (p.readinessCheck) {
            row.querySelector('[data-port-field="healthPath"]').value = p.readinessCheck.httpPath || '';
            row.querySelector('[data-port-field="timeout"]').value = p.readinessCheck.timeoutSeconds || '';
        }
        // Trigger protocol change to show/hide health path
        const protocolSelect = row.querySelector('[data-port-field="protocol"]');
        onPortProtocolChange(protocolSelect);
    });

    // Environment variables
    const envList = document.getElementById('ct-env-vars-list');
    envList.innerHTML = '';
    const envVars = template.defaultEnvironmentVariables || {};
    Object.entries(envVars).forEach(([key, value]) => {
        addEnvVar();
        const rows = envList.querySelectorAll('.env-var-row');
        const row = rows[rows.length - 1];
        row.querySelector('[data-env-field="key"]').value = key;
        row.querySelector('[data-env-field="value"]').value = value;
    });

    // Variables — populate rows from the loaded template (view mode by default)
    const varsList = document.getElementById('ct-variables-list');
    varsList.innerHTML = '';
    const templateVars = template.variables || [];
    templateVars.forEach(v => {
        const row = addVariable(false);
        row.querySelector('[data-var-field="name"]').value = v.name || '';
        row.querySelector('[data-var-field="kind"]').value = (typeof v.kind === 'number')
            ? String(v.kind)
            : (v.kind === 'Dynamic' ? '1' : '0');
        row.querySelector('[data-var-field="defaultValue"]').value = v.defaultValue || '';
        row.querySelector('[data-var-field="required"]').checked = !!v.required;
        row.querySelector('[data-var-field="resolverKey"]').value = v.resolverKey || '';
        onVariableKindChange(row.querySelector('[data-var-field="kind"]'));
        setVariableRowMode(row, 'view');
    });
    if (templateVars.length > 0) {
        document.getElementById('ct-variables-body').style.display = '';
        document.getElementById('ct-variables-toggle-icon').textContent = '▼';
    }
    updateVariablesCount();

    // Artifacts — existing template artifacts plus any pending (new) ones added during this edit session.
    pendingArtifacts = [];
    renderCombinedArtifactsList(template.artifacts || [], template.id);
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
// ARTIFACT MANAGEMENT
// ═══════════════════════════════════════════════════════════════════════════

const ARTIFACT_TYPE_LABELS = ['Binary', 'Script', 'Web Asset', 'Config', 'Archive', 'Image'];

// Render existing template artifacts (from server) plus pending ones (staged
// client-side). Pending items show "(pending)" and remove client-side; existing
// items use the DELETE endpoint immediately.
function renderCombinedArtifactsList(existingArtifacts, templateId) {
    const list = document.getElementById('ct-artifacts-list');
    if (!list) return;

    const rows = [];

    existingArtifacts.forEach(a => {
        rows.push(`
            <div class="artifact-row" style="display:flex; align-items:center; gap:8px; padding:8px 0; border-bottom:1px solid var(--border-color, #333);">
                <div style="flex:1; min-width:0; overflow:hidden;">
                    <span style="font-weight:600; font-family:var(--font-mono); font-size:13px;">${escapeHtml(a.name)}</span>
                    <span style="color:var(--text-muted,#6b7280); font-size:11px; margin-left:6px;">${escapeHtml(ARTIFACT_TYPE_LABELS[a.type] ?? String(a.type))}${a.architecture ? ' · ' + escapeHtml(a.architecture) : ''}</span>
                    ${a.description ? `<span style="color:var(--text-muted,#6b7280); font-size:11px; margin-left:6px;">— ${escapeHtml(a.description)}</span>` : ''}
                </div>
                <span style="font-family:var(--font-mono); font-size:10px; color:var(--text-muted,#6b7280); flex-shrink:0;">${escapeHtml((a.sha256 || '').slice(0, 12))}…</span>
                ${templateId ? `
                    <button class="btn btn-sm btn-secondary"
                        onclick="window.myTemplates.editArtifact('${escapeJs(templateId)}', '${escapeJs(a.id)}')"
                        type="button" style="flex-shrink:0; padding:4px 10px;">Edit</button>
                    <button class="btn btn-sm btn-danger"
                        onclick="window.myTemplates.removeArtifact('${escapeJs(templateId)}', '${escapeJs(a.id)}')"
                        type="button" style="flex-shrink:0; padding:4px 8px;">✕</button>
                ` : ''}
            </div>
        `);
    });

    pendingArtifacts.forEach((a, idx) => {
        rows.push(`
            <div class="artifact-row" style="display:flex; align-items:center; gap:8px; padding:8px 0; border-bottom:1px solid var(--border-color, #333);">
                <div style="flex:1; min-width:0; overflow:hidden;">
                    <span style="font-weight:600; font-family:var(--font-mono); font-size:13px;">${escapeHtml(a.name)}</span>
                    <span style="color:var(--text-muted,#6b7280); font-size:11px; margin-left:6px;">${escapeHtml(ARTIFACT_TYPE_LABELS[a.type] ?? String(a.type))}${a.architecture ? ' · ' + escapeHtml(a.architecture) : ''}</span>
                    <span style="color:var(--accent, #00d9ff); font-size:11px; margin-left:6px;">(pending)</span>
                </div>
                <button class="btn btn-sm btn-secondary"
                    onclick="window.myTemplates.editPendingArtifact(${idx})"
                    type="button" style="flex-shrink:0; padding:4px 10px;">Edit</button>
                <button class="btn btn-sm btn-danger"
                    onclick="window.myTemplates.removePendingArtifact(${idx})"
                    type="button" style="flex-shrink:0; padding:4px 8px;">✕</button>
            </div>
        `);
    });

    if (rows.length === 0) {
        list.innerHTML = '<p style="color: var(--text-muted, #6b7280); font-size:13px; margin-bottom:4px;">No artifacts attached.</p>';
        return;
    }

    list.innerHTML = rows.join('');
}

// Decode a `data:` URI into its media type and UTF-8 text payload. Used when
// switching an existing inline artifact into the form's paste-mode panel.
function decodeDataUri(dataUri) {
    const commaIdx = dataUri.indexOf(',');
    if (commaIdx < 0) return { mediaType: 'text/plain', text: '' };
    const header = dataUri.slice(5, commaIdx); // strip 'data:'
    const mediaType = header.split(';')[0] || 'text/plain';
    const payload = dataUri.slice(commaIdx + 1);
    if (header.includes(';base64')) {
        try {
            const binary = atob(payload);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
            return { mediaType, text: new TextDecoder().decode(bytes) };
        } catch {
            return { mediaType, text: '' };
        }
    }
    try {
        return { mediaType, text: decodeURIComponent(payload) };
    } catch {
        return { mediaType, text: '' };
    }
}

// Open the artifact form pre-populated with the given artifact's values.
// `art` may be a pending entry (with `content`/`contentType` for paste-mode
// drafts) or an existing server artifact (only `sourceUrl` is set).
function populateArtifactForm(art) {
    showArtifactRow();   // resets + reveals the form

    document.getElementById('ct-art-name').value = art.name || '';
    document.getElementById('ct-art-description').value = art.description || '';
    document.getElementById('ct-art-type').value = String(art.type ?? 1);
    document.getElementById('ct-art-arch').value = art.architecture || '';

    if (typeof art.content === 'string' && art.content.length > 0) {
        // Pending paste-mode entry — restore content directly.
        document.getElementById('ct-art-content').value = art.content;
        if (art.contentType) {
            document.getElementById('ct-art-content-type').value = art.contentType;
        }
        switchArtifactMode('paste');
    } else if (typeof art.sourceUrl === 'string' && art.sourceUrl.startsWith('data:')) {
        const { mediaType, text } = decodeDataUri(art.sourceUrl);
        document.getElementById('ct-art-content').value = text;
        const ctSelect = document.getElementById('ct-art-content-type');
        if ([...ctSelect.options].some(o => o.value === mediaType)) {
            ctSelect.value = mediaType;
        }
        switchArtifactMode('paste');
    } else if (typeof art.sourceUrl === 'string' && art.sourceUrl.length > 0) {
        document.getElementById('ct-art-url').value = art.sourceUrl;
        document.getElementById('ct-art-sha256').value = art.sha256 || '';
        switchArtifactMode('url');
    } else {
        switchArtifactMode('upload');
    }
}

export function editPendingArtifact(index) {
    const art = pendingArtifacts[index];
    if (!art) return;
    // Order matters: populateArtifactForm calls showArtifactRow which resets
    // editingArtifactTarget. Set it AFTER populating so saveArtifact sees it.
    populateArtifactForm(art);
    editingArtifactTarget = { kind: 'pending', index };
    setArtifactSaveButtonLabel('Save Changes');
}

export function editArtifact(templateId, artifactId) {
    const template = myTemplates.find(t => t.id === templateId);
    if (!template) return;
    const art = (template.artifacts || []).find(a => a.id === artifactId);
    if (!art) return;
    populateArtifactForm(art);
    editingArtifactTarget = { kind: 'existing', templateId, id: artifactId };
    setArtifactSaveButtonLabel('Save Changes');
}

function setArtifactSaveButtonLabel(label) {
    const btn = document.querySelector(
        '#ct-artifact-new-row button.btn-primary[onclick*="saveArtifact"]');
    if (btn) btn.textContent = label;
}

export function showArtifactRow() {
    document.getElementById('ct-artifact-new-row').style.display = '';
    document.getElementById('ct-add-artifact-btn').style.display = 'none';
    // Clear all inputs
    ['ct-art-name', 'ct-art-url', 'ct-art-sha256', 'ct-art-description', 'ct-art-content'].forEach(id => {
        const el = document.getElementById(id);
        if (el) el.value = '';
    });
    document.getElementById('ct-art-file').value = '';
    document.getElementById('ct-art-file-meta').textContent = '';
    document.getElementById('ct-art-file-meta').style.display = 'none';
    document.getElementById('ct-art-drop-msg').textContent = 'Click to choose a file, or drag-and-drop here';
    document.getElementById('ct-art-type').value = '1';   // Script default
    document.getElementById('ct-art-arch').value = '';
    document.getElementById('ct-art-content-type').value = 'text/x-shellscript';
    const newRow = document.getElementById('ct-artifact-new-row');
    delete newRow.dataset.dataUri;
    delete newRow.dataset.fileName;

    editingArtifactTarget = null;
    setArtifactSaveButtonLabel('Add Artifact');

    artifactMode = 'upload';
    switchArtifactMode('upload');
    bindArtifactDropZone();
}

export function cancelArtifact() {
    document.getElementById('ct-artifact-new-row').style.display = 'none';
    document.getElementById('ct-add-artifact-btn').style.display = '';
    editingArtifactTarget = null;
    setArtifactSaveButtonLabel('Add Artifact');
}

export function switchArtifactMode(mode) {
    artifactMode = mode;
    ['upload', 'paste', 'url'].forEach(m => {
        const panel = document.getElementById(`ct-art-mode-${m}`);
        if (panel) panel.style.display = (m === mode) ? '' : 'none';
    });
    document.querySelectorAll('.artifact-mode-tab').forEach(btn => {
        const active = btn.dataset.artMode === mode;
        btn.classList.toggle('btn-primary', active);
        btn.classList.toggle('btn-secondary', !active);
    });
}

function bindArtifactDropZone() {
    const zone = document.getElementById('ct-art-drop-zone');
    const input = document.getElementById('ct-art-file');
    if (!zone || !input || zone.dataset.bound === '1') return;

    zone.addEventListener('click', () => input.click());
    zone.addEventListener('dragover', (e) => {
        e.preventDefault();
        zone.style.background = 'var(--bg-tertiary, #222)';
    });
    zone.addEventListener('dragleave', () => {
        zone.style.background = 'var(--bg-secondary, #1a1a1a)';
    });
    zone.addEventListener('drop', (e) => {
        e.preventDefault();
        zone.style.background = 'var(--bg-secondary, #1a1a1a)';
        if (e.dataTransfer?.files?.length) {
            readArtifactFile(e.dataTransfer.files[0]);
        }
    });

    zone.dataset.bound = '1';
}

export function onArtifactFileSelected(event) {
    const file = event.target.files?.[0];
    if (file) readArtifactFile(file);
}

function readArtifactFile(file) {
    if (file.size > 5 * 1024 * 1024) {
        showToast('error', `File is ${(file.size / 1024 / 1024).toFixed(1)} MB — inline limit is 5 MB. Use External URL for larger files.`);
        return;
    }

    const reader = new FileReader();
    reader.onload = () => {
        const dataUri = reader.result;
        const newRow = document.getElementById('ct-artifact-new-row');
        newRow.dataset.dataUri = dataUri;
        newRow.dataset.fileName = file.name;

        // Pre-fill name from filename (sans extension) if name is empty.
        const nameInput = document.getElementById('ct-art-name');
        if (!nameInput.value.trim()) {
            nameInput.value = file.name.replace(/\.[^.]+$/, '').toLowerCase()
                .replace(/[^a-z0-9-]+/g, '-').replace(/(^-+|-+$)/g, '');
        }

        const meta = document.getElementById('ct-art-file-meta');
        meta.textContent = `${file.name} — ${(file.size / 1024).toFixed(1)} KB`;
        meta.style.display = '';
        document.getElementById('ct-art-drop-msg').textContent = 'File ready. Click to replace.';
    };
    reader.onerror = () => showToast('error', 'Failed to read file');
    reader.readAsDataURL(file);
}

export async function saveArtifact() {
    const templateId = document.getElementById('ct-template-id').value;
    const isTemplateEdit = !!templateId;

    const name = document.getElementById('ct-art-name').value.trim();
    if (!name) { showToast('error', 'Artifact name is required'); return; }

    const description = document.getElementById('ct-art-description').value.trim() || null;
    const type = parseInt(document.getElementById('ct-art-type').value, 10);
    const architecture = document.getElementById('ct-art-arch').value || null;

    const newRow = document.getElementById('ct-artifact-new-row');
    let payload = { name, description, type, architecture };

    if (artifactMode === 'upload') {
        if (!newRow.dataset.dataUri) {
            showToast('error', 'Choose a file to upload');
            return;
        }
        payload.sourceUrl = newRow.dataset.dataUri;
    } else if (artifactMode === 'paste') {
        const content = document.getElementById('ct-art-content').value;
        if (!content.trim()) {
            showToast('error', 'Paste some content first');
            return;
        }
        payload.content = content;
        payload.contentType = document.getElementById('ct-art-content-type').value;
    } else if (artifactMode === 'url') {
        const url = document.getElementById('ct-art-url').value.trim();
        const sha256 = document.getElementById('ct-art-sha256').value.trim().toLowerCase();
        if (!url) { showToast('error', 'Source URL is required'); return; }
        if (!sha256) { showToast('error', 'SHA256 is required for external URLs'); return; }
        if (sha256.length !== 64 || !/^[0-9a-f]+$/.test(sha256)) {
            showToast('error', 'SHA256 must be a 64-character lowercase hex string');
            return;
        }
        payload.sourceUrl = url;
        payload.sha256 = sha256;
    }

    // ── Editing an existing server-side artifact ───────────────────────────
    if (editingArtifactTarget?.kind === 'existing') {
        const { templateId: tid, id: aid } = editingArtifactTarget;
        try {
            const response = await api(`/api/marketplace/templates/${tid}/artifacts/${aid}`, {
                method: 'PUT',
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const err = await response.json().catch(() => ({}));
                throw new Error(err.error || 'Failed to update artifact');
            }

            showToast('success', `Artifact '${name}' updated`);
            cancelArtifact();

            await loadMyTemplates();
            const updated = myTemplates.find(t => t.id === tid);
            if (updated) renderCombinedArtifactsList(updated.artifacts || [], tid);
        } catch (error) {
            showToast('error', error.message);
        }
        return;
    }

    // ── Editing a client-staged (pending) artifact ─────────────────────────
    if (editingArtifactTarget?.kind === 'pending') {
        pendingArtifacts[editingArtifactTarget.index] = payload;
        showToast('success', `Artifact '${name}' updated`);
        cancelArtifact();
        const tid = document.getElementById('ct-template-id').value;
        const existing = tid
            ? (myTemplates.find(t => t.id === tid)?.artifacts || [])
            : [];
        renderCombinedArtifactsList(existing, tid || null);
        return;
    }

    // ── Adding new during a template edit: POST immediately ────────────────
    if (isTemplateEdit) {
        try {
            const response = await api(`/api/marketplace/templates/${templateId}/artifacts`, {
                method: 'POST',
                body: JSON.stringify(payload)
            });

            if (!response.ok) {
                const err = await response.json().catch(() => ({}));
                throw new Error(err.error || 'Failed to add artifact');
            }

            showToast('success', `Artifact '${name}' added`);
            cancelArtifact();

            await loadMyTemplates();
            const updated = myTemplates.find(t => t.id === templateId);
            if (updated) renderCombinedArtifactsList(updated.artifacts || [], templateId);
        } catch (error) {
            showToast('error', error.message);
        }
        return;
    }

    // ── Adding new during template create: stage locally ───────────────────
    pendingArtifacts.push(payload);
    showToast('success', `Artifact '${name}' staged (submitted on Create)`);
    cancelArtifact();
    renderCombinedArtifactsList([], null);
}

export function removePendingArtifact(index) {
    pendingArtifacts.splice(index, 1);
    const templateId = document.getElementById('ct-template-id').value;
    if (templateId) {
        const t = myTemplates.find(x => x.id === templateId);
        renderCombinedArtifactsList(t?.artifacts || [], templateId);
    } else {
        renderCombinedArtifactsList([], null);
    }
}

export async function removeArtifact(templateId, artifactId) {
    if (!confirm('Remove this artifact from the template?')) return;

    try {
        const response = await api(
            `/api/marketplace/templates/${templateId}/artifacts/${artifactId}`,
            { method: 'DELETE' }
        );

        // 204 No Content is success; any non-ok status is an error
        if (!response.ok && response.status !== 204) {
            const err = await response.json().catch(() => ({}));
            throw new Error(err.error || 'Failed to remove artifact');
        }

        showToast('success', 'Artifact removed');

        await loadMyTemplates();
        const updated = myTemplates.find(t => t.id === templateId);
        if (updated) renderCombinedArtifactsList(updated.artifacts || [], templateId);
    } catch (error) {
        showToast('error', error.message);
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// VARIABLES MANAGEMENT
// ═══════════════════════════════════════════════════════════════════════════

export function toggleVariablesSection() {
    const body = document.getElementById('ct-variables-body');
    const icon = document.getElementById('ct-variables-toggle-icon');
    const isHidden = body.style.display === 'none';
    body.style.display = isHidden ? '' : 'none';
    icon.textContent = isHidden ? '▼' : '▶';
}

// Add a variable row. When `startInView` is true (used when populating from a
// loaded template), the row renders read-only with Edit/Delete buttons. When
// false (used by the "+ Add Variable" button), the row opens directly into
// edit mode so the user can fill in values.
export function addVariable(startInView = false) {
    const list = document.getElementById('ct-variables-list');
    const row = document.createElement('div');
    row.className = 'variable-row';
    row.style.marginBottom = '10px';
    row.style.padding = '10px';
    row.style.border = '1px solid var(--border-color, #333)';
    row.style.borderRadius = '6px';
    row.innerHTML = `
        <div class="variable-view" style="display:none; align-items:center; gap:8px;">
            <div style="flex:1; min-width:0;">
                <div style="display:flex; align-items:center; gap:8px;">
                    <span class="variable-view-name" style="font-weight:600; font-family:var(--font-mono); font-size:13px;"></span>
                    <span class="variable-view-kind" style="font-size:11px; padding:2px 6px; border-radius:3px; background:var(--bg-tertiary,#222);"></span>
                    <span class="variable-view-required" style="font-size:11px; color:var(--accent,#00d9ff); display:none;">required</span>
                </div>
                <div class="variable-view-meta" style="font-size:11px; color:var(--text-muted,#6b7280); margin-top:2px; font-family:var(--font-mono);"></div>
            </div>
            <button class="btn btn-sm btn-secondary" onclick="window.myTemplates.editVariableRow(this)"
                type="button" style="flex-shrink:0; padding:4px 10px;">Edit</button>
            <button class="btn btn-sm btn-danger" onclick="window.myTemplates.removeVariableRow(this)"
                type="button" style="flex-shrink:0; padding:4px 8px;">✕</button>
        </div>
        <div class="variable-edit">
            <div class="form-row" style="margin-bottom:6px;">
                <div class="form-group" style="flex:2">
                    <label class="form-label" style="font-size:11px;">Name *</label>
                    <input type="text" class="form-input" data-var-field="name"
                        placeholder="DECLOUD_PASSWORD" style="font-family:var(--font-mono); font-size:12px;">
                </div>
                <div class="form-group" style="flex:1">
                    <label class="form-label" style="font-size:11px;">Kind</label>
                    <select class="form-input" data-var-field="kind"
                        onchange="window.myTemplates.onVariableKindChange(this)">
                        <option value="0">Static</option>
                        <option value="1">Dynamic</option>
                    </select>
                </div>
            </div>
            <div class="form-row variable-static-fields" style="margin-bottom:6px;">
                <div class="form-group" style="flex:2">
                    <label class="form-label" style="font-size:11px;">Default Value</label>
                    <input type="text" class="form-input" data-var-field="defaultValue"
                        placeholder="Optional default" style="font-family:var(--font-mono); font-size:12px;">
                </div>
                <div class="form-group" style="flex:0 0 auto; display:flex; align-items:center; padding-top:20px;">
                    <label class="filter-label">
                        <input type="checkbox" data-var-field="required">
                        <span style="font-size:12px;">Required</span>
                    </label>
                </div>
                <div class="form-group" style="flex:2">
                    <label class="form-label" style="font-size:11px;">Resolver Key (advanced)</label>
                    <input type="text" class="form-input" data-var-field="resolverKey"
                        placeholder="Defaults to Name" style="font-family:var(--font-mono); font-size:12px;">
                </div>
            </div>
            <div style="display:flex; gap:6px;">
                <button class="btn btn-sm btn-primary" onclick="window.myTemplates.doneVariableRow(this)"
                    type="button">Done</button>
                <button class="btn btn-sm btn-danger" onclick="window.myTemplates.removeVariableRow(this)"
                    type="button">✕</button>
            </div>
        </div>
    `;
    list.appendChild(row);
    if (startInView) setVariableRowMode(row, 'view');
    updateVariablesCount();
    return row;
}

function setVariableRowMode(row, mode) {
    const viewEl = row.querySelector('.variable-view');
    const editEl = row.querySelector('.variable-edit');
    if (mode === 'view') {
        refreshVariableViewText(row);
        viewEl.style.display = 'flex';
        editEl.style.display = 'none';
    } else {
        viewEl.style.display = 'none';
        editEl.style.display = '';
    }
}

function refreshVariableViewText(row) {
    const name = row.querySelector('[data-var-field="name"]').value.trim();
    const kind = row.querySelector('[data-var-field="kind"]').value;
    const defaultValue = row.querySelector('[data-var-field="defaultValue"]').value;
    const required = row.querySelector('[data-var-field="required"]').checked;
    const resolverKey = row.querySelector('[data-var-field="resolverKey"]').value.trim();

    row.querySelector('.variable-view-name').textContent = name || '(unnamed)';
    row.querySelector('.variable-view-kind').textContent = kind === '1' ? 'Dynamic' : 'Static';
    row.querySelector('.variable-view-required').style.display =
        (kind === '0' && required) ? '' : 'none';

    const parts = [];
    if (kind === '0' && defaultValue) parts.push(`default = ${defaultValue}`);
    if (resolverKey) parts.push(`resolver = ${resolverKey}`);
    row.querySelector('.variable-view-meta').textContent = parts.join('  ·  ');
}

export function editVariableRow(btn) {
    const row = btn.closest('.variable-row');
    setVariableRowMode(row, 'edit');
}

export function doneVariableRow(btn) {
    const row = btn.closest('.variable-row');
    const name = row.querySelector('[data-var-field="name"]').value.trim();
    if (!name) {
        showToast('error', 'Variable name is required');
        return;
    }
    setVariableRowMode(row, 'view');
}

export function removeVariableRow(btn) {
    btn.closest('.variable-row')?.remove();
    updateVariablesCount();
}

export function onVariableKindChange(select) {
    const row = select.closest('.variable-row');
    const staticFields = row.querySelector('.variable-static-fields');
    // Dynamic kind: hide Default Value and Required (those are Static-only).
    const isStatic = select.value === '0';
    if (staticFields) staticFields.style.display = isStatic ? '' : 'none';
}

function updateVariablesCount() {
    const list = document.getElementById('ct-variables-list');
    const count = list ? list.querySelectorAll('.variable-row').length : 0;
    const el = document.getElementById('ct-variables-count');
    if (el) el.textContent = count > 0 ? `(${count})` : '';
}

function collectVariables() {
    const rows = document.querySelectorAll('#ct-variables-list .variable-row');
    const vars = [];
    rows.forEach(row => {
        const name = row.querySelector('[data-var-field="name"]')?.value?.trim();
        if (!name) return;
        const kind = parseInt(row.querySelector('[data-var-field="kind"]')?.value || '0', 10);
        const defaultValue = row.querySelector('[data-var-field="defaultValue"]')?.value || '';
        const required = row.querySelector('[data-var-field="required"]')?.checked || false;
        const resolverKey = row.querySelector('[data-var-field="resolverKey"]')?.value?.trim() || null;
        const entry = { name, kind };
        if (kind === 0) {
            entry.defaultValue = defaultValue || null;
            entry.required = required;
        }
        if (resolverKey) entry.resolverKey = resolverKey;
        vars.push(entry);
    });
    return vars;
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
    addEnvVar,
    onPricingChange,
    onGpuToggle,
    onPortProtocolChange,
    showArtifactRow,
    cancelArtifact,
    saveArtifact,
    removeArtifact,
    setLdTab,
    insertCloudInitStarter,
    updateSubstitutionPreview,
    switchArtifactMode,
    onArtifactFileSelected,
    removePendingArtifact,
    editPendingArtifact,
    editArtifact,
    addVariable,
    removeVariableRow,
    editVariableRow,
    doneVariableRow,
    onVariableKindChange,
    toggleVariablesSection
};
