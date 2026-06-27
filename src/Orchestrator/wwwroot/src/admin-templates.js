// ============================================================================
// admin-templates.js — Admin community-template review queue (Phase 3)
//
// Drives /api/marketplace/templates: list pending, approve, reject. Admin-only —
// every endpoint is enforced server-side with [Authorize(Roles="Admin")]; this UI
// is merely revealed by applyAdminVisibility() in app.js and grants no privilege.
//
// MarketplaceController returns RAW objects (not the ApiResponse wrapper that
// admin-compliance.js consumes), so responses are read directly.
// ============================================================================

import { escapeHtml, showToast, sanitizeUrl } from './utils.js';

let _api = null;
let _rendered = false;

const MUTED = 'color:var(--text-muted,#888)';

export function initAdminTemplates(api) {
    _api = api;
    if (!_rendered) { render(); _rendered = true; }
    loadQueue();
}

// ── API helper (raw responses) ───────────────────────────────────────────────

async function apiCall(path, method = 'GET', body) {
    const opts = method === 'GET'
        ? undefined
        : { method, ...(body !== undefined ? { body: JSON.stringify(body) } : {}) };
    const res = await _api(path, opts);
    let json = null;
    try { json = await res.json(); } catch { /* may be empty */ }
    if (!res.ok) {
        throw new Error((json && (json.error || json.message)) || `Request failed (${res.status})`);
    }
    return json;
}

// ── Helpers ──────────────────────────────────────────────────────────────────

const fmtDate = s => {
    if (!s) return '';
    const d = new Date(s);
    return isNaN(d) ? String(s) : d.toLocaleString();
};
const shortWallet = w => (w && w.length > 12 ? `${w.slice(0, 6)}…${w.slice(-4)}` : (w ?? ''));

function portsText(ports) {
    if (!Array.isArray(ports) || ports.length === 0) return '—';
    return ports.map(p => (p && typeof p === 'object') ? JSON.stringify(p) : String(p)).join(', ');
}

function variablesText(vars) {
    if (!Array.isArray(vars) || vars.length === 0) return '—';
    return vars.map(v => {
        if (v && typeof v === 'object') {
            const name = v.name ?? v.Name ?? JSON.stringify(v);
            const def = v.defaultValue ?? v.DefaultValue;
            return def ? `${name}=${def}` : name;
        }
        return String(v);
    }).join(', ');
}

function artifactsRows(arts) {
    if (!Array.isArray(arts) || arts.length === 0) return `<div style="${MUTED}">—</div>`;
    return arts.map(a => {
        const name = escapeHtml(a.name ?? a.Name ?? '');
        const arch = escapeHtml(a.architecture ?? a.Architecture ?? 'any');
        const sha = escapeHtml((a.sha256 ?? a.Sha256 ?? '').slice(0, 16));
        const url = a.url ?? a.Url ?? '';
        const safeUrl = escapeHtml(sanitizeUrl(url));
        return `<div style="margin:4px 0; font-family:monospace; font-size:0.82rem;">
            <strong>${name}</strong> <span style="${MUTED}">(${arch})</span>
            ${sha ? `<span style="${MUTED}"> sha256:${sha}…</span>` : ''}
            ${url ? `<div><a href="${safeUrl}" target="_blank" rel="noopener noreferrer">${escapeHtml(url)}</a></div>` : ''}
        </div>`;
    }).join('');
}

// ── Page skeleton ────────────────────────────────────────────────────────────

function render() {
    const root = document.getElementById('admin-templates-content');
    if (!root) return;
    root.innerHTML = `
      <div class="form-section" style="display:flex; gap:8px; align-items:center; justify-content:space-between;">
        <div id="at-summary" style="${MUTED}">Loading…</div>
        <button id="at-refresh" class="btn btn-secondary">Refresh</button>
      </div>
      <div id="at-queue"></div>`;

    document.getElementById('at-refresh').onclick = loadQueue;
    // Delegation: cards are rebuilt on every load, so bind once on the container.
    document.getElementById('at-queue').addEventListener('click', onQueueClick);
}

// ── Load + render queue ──────────────────────────────────────────────────────

async function loadQueue() {
    const summary = document.getElementById('at-summary');
    const queue = document.getElementById('at-queue');
    if (summary) summary.textContent = 'Loading…';
    try {
        const templates = await apiCall('/api/marketplace/templates/pending');
        renderQueue(Array.isArray(templates) ? templates : []);
    } catch (e) {
        if (summary) summary.textContent = '';
        if (queue) queue.innerHTML = `<div class="form-section">${escapeHtml(e.message)}</div>`;
    }
}

function renderQueue(templates) {
    const summary = document.getElementById('at-summary');
    const queue = document.getElementById('at-queue');
    if (summary) summary.textContent = templates.length === 0
        ? 'No templates awaiting review.'
        : `${templates.length} template${templates.length === 1 ? '' : 's'} awaiting review`;
    if (queue) queue.innerHTML = templates.map(card).join('');
}

function card(t) {
    const id = escapeHtml(t.id ?? '');
    const isPrivate = t.visibility === 'Private' || t.visibility === 1;
    return `
      <div class="form-section" data-template-id="${id}">
        <h3 class="form-section-title" style="margin:0;">${escapeHtml(t.name ?? '(unnamed)')}
          <span style="${MUTED}; font-weight:400;">/${escapeHtml(t.slug ?? '')}</span></h3>
        <div style="${MUTED}; font-size:0.82rem; margin-bottom:8px;">
          by <span style="font-family:monospace;" title="${escapeHtml(t.authorId ?? '')}">${escapeHtml(shortWallet(t.authorId))}</span>
          · ${escapeHtml(t.category ?? 'uncategorized')}
          · submitted ${escapeHtml(fmtDate(t.createdAt))}
          ${isPrivate ? ' · <strong>Private</strong>' : ''}
        </div>

        <p style="margin:8px 0;">${escapeHtml(t.description ?? '')}</p>

        <details style="margin:8px 0;">
          <summary style="cursor:pointer; font-weight:600;">Deployable payload</summary>
          <div style="margin-top:8px;">
            <div class="form-label">Container image</div>
            <div style="font-family:monospace; margin-bottom:8px;">${escapeHtml(t.containerImage || '—')}</div>

            <div class="form-label">Exposed ports</div>
            <div style="margin-bottom:8px;">${escapeHtml(portsText(t.exposedPorts))}</div>

            <div class="form-label">Variables</div>
            <div style="margin-bottom:8px;">${escapeHtml(variablesText(t.variables))}</div>

            <div class="form-label">Artifacts</div>
            <div style="margin-bottom:8px;">${artifactsRows(t.artifacts)}</div>

            <div class="form-label">Cloud-init</div>
            <pre style="max-height:320px; overflow:auto; background:var(--bg-tertiary,#1a1a1a); padding:10px; border-radius:6px; font-size:0.8rem; white-space:pre-wrap;">${escapeHtml(t.cloudInitTemplate || '—')}</pre>
          </div>
        </details>

        <div style="display:flex; gap:8px; align-items:center; margin-top:8px;">
          <input class="form-input at-reason" placeholder="Rejection reason (required to reject)" style="flex:1; margin:0;">
          <button class="btn btn-secondary" data-action="reject" data-id="${id}">Reject</button>
          <button class="btn btn-primary" data-action="approve" data-id="${id}">Approve</button>
        </div>
      </div>`;
}

// ── Actions ──────────────────────────────────────────────────────────────────

function onQueueClick(e) {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    if (btn.dataset.action === 'approve') approve(btn);
    else if (btn.dataset.action === 'reject') reject(btn);
}

async function approve(btn) {
    const id = btn.dataset.id;
    btn.disabled = true;
    try {
        await apiCall(`/api/marketplace/templates/${encodeURIComponent(id)}/approve`, 'POST');
        showToast('Template approved — now live', 'success');
        loadQueue();
    } catch (e) {
        showToast(e.message, 'error');
        btn.disabled = false;
    }
}

async function reject(btn) {
    const id = btn.dataset.id;
    const input = btn.closest('[data-template-id]')?.querySelector('.at-reason');
    const reason = (input?.value || '').trim();
    if (!reason) { showToast('A rejection reason is required', 'error'); input?.focus(); return; }
    btn.disabled = true;
    try {
        await apiCall(`/api/marketplace/templates/${encodeURIComponent(id)}/reject`, 'POST', { reason });
        showToast('Template rejected', 'success');
        loadQueue();
    } catch (e) {
        showToast(e.message, 'error');
        btn.disabled = false;
    }
}