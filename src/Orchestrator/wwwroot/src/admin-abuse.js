// ============================================================================
// admin-abuse.js — Admin abuse-report queue (Phase 4)
//
// Drives /api/admin/abuse: list open reports (each with the target wallet's
// enforcement history) and resolve them — dismiss / warn (close without
// withholding service) or takedown (suspend the target wallet through the
// existing enforcement path). Admin-only — every endpoint is enforced server-side
// with [Authorize(Roles="Admin")]; this UI is merely revealed by
// applyAdminVisibility() in app.js and grants no privilege itself.
//
// AdminAbuseController returns the ApiResponse wrapper ({success,data}), so
// responses are read via .data (like admin-compliance.js). Enums arrive as string names
// (numbers still tolerated); resolve category/action via the *_NAMES arrays, not the labels.
// ============================================================================

import { escapeHtml, showToast } from './utils.js';

let _api = null;
let _rendered = false;

const MUTED = 'color:var(--text-muted,#888)';

const CATEGORY_LABELS = ['CSAM', 'Malware / C2', 'Illegal marketplace', 'DMCA', 'TOS violation', 'Spam'];
const PRIORITY_LABELS = ['P0', 'P1', 'P2', 'P3', 'P4'];
const ACTION_LABELS = ['Suspend', 'Unsuspend', 'Block', 'Unblock', 'Terminate VMs', 'Suspend VM', 'Resume VM', 'Scan VM'];
const CATEGORY_NAMES = ['Csam', 'MalwareC2', 'IllegalMarketplace', 'Dmca', 'TosViolation', 'Spam'];
const ACTION_NAMES = ['Suspend', 'Unsuspend', 'Block', 'Unblock', 'TerminateVms', 'SuspendVm', 'ResumeVm', 'ScanVm'];

// P0 most urgent (red), easing to muted by P4.
const PRIORITY_STYLE = [
    'background:rgba(239,68,68,0.15); color:#ef4444;',
    'background:rgba(245,158,11,0.15); color:#f59e0b;',
    'background:rgba(234,179,8,0.15); color:#eab308;',
    'background:rgba(148,163,184,0.15); color:#94a3b8;',
    'background:rgba(148,163,184,0.12); color:#94a3b8;',
];

const idx = (v, names) => (typeof v === 'number' ? v : Math.max(0, names.indexOf(v)));
const categoryLabel = c => CATEGORY_LABELS[idx(c, CATEGORY_NAMES)] ?? String(c);
const priorityLabel = p => PRIORITY_LABELS[idx(p, PRIORITY_LABELS)] ?? String(p);
const priorityStyle = p => PRIORITY_STYLE[idx(p, PRIORITY_LABELS)] ?? PRIORITY_STYLE[4];
const actionLabel = t => ACTION_LABELS[idx(t, ACTION_NAMES)] ?? String(t);

const fmtDate = s => { if (!s) return ''; const d = new Date(s); return isNaN(d) ? String(s) : d.toLocaleString(); };
const shortWallet = w => (w && w.length > 12 ? `${w.slice(0, 6)}…${w.slice(-4)}` : (w ?? ''));

// The scan-vm endpoint needs the VM id. Reports carry it in reportedResource as
// "vm:<id>" (the csam-report path wrote exactly that in pass 1). Pull it from the
// card's data. If a report's resource isn't vm-shaped, the button still POSTs and
// the server refuses with a clear message — the UI does not pre-judge.
function r_vmId(cardEl) {
    const res = cardEl?.querySelector('[data-reported-resource]')?.getAttribute('data-reported-resource') || '';
    const m = res.match(/^vm:(.+)$/);
    return m ? m[1] : '';
}

export function initAdminAbuse(api) {
    _api = api;
    if (!_rendered) { render(); _rendered = true; }
    loadQueue();
}

// ── API helper (ApiResponse wrapper → .data) ─────────────────────────────────

async function call(path, body) {
    const res = await _api(path, body ? { method: 'POST', body: JSON.stringify(body) } : undefined);
    let json = {};
    try { json = await res.json(); } catch { /* no/!json body */ }
    if (!res.ok || json.success === false) {
        throw new Error(json.message || json.error || `Request failed (${res.status})`);
    }
    return json.data;
}

// ── Page skeleton ────────────────────────────────────────────────────────────

function render() {
    const root = document.getElementById('admin-abuse-content');
    if (!root) return;
    root.innerHTML = `
      <div class="form-section" style="display:flex; gap:8px; align-items:center; justify-content:space-between;">
        <div id="aa-summary" style="${MUTED}">Loading…</div>
        <button id="aa-refresh" class="btn btn-secondary">Refresh</button>
      </div>
      <div id="aa-queue"></div>`;
    document.getElementById('aa-refresh').onclick = loadQueue;
    // Delegation: cards are rebuilt on every load, so bind once on the container.
    document.getElementById('aa-queue').addEventListener('click', onQueueClick);
}

// ── Load + render ────────────────────────────────────────────────────────────

async function loadQueue() {
    const summary = document.getElementById('aa-summary');
    const queue = document.getElementById('aa-queue');
    if (summary) summary.textContent = 'Loading…';
    try {
        const items = await call('/api/admin/abuse');
        renderQueue(Array.isArray(items) ? items : []);
    } catch (e) {
        if (summary) summary.textContent = '';
        if (queue) queue.innerHTML = `<div class="form-section">${escapeHtml(e.message)}</div>`;
    }
}

function renderQueue(items) {
    const summary = document.getElementById('aa-summary');
    const queue = document.getElementById('aa-queue');
    if (summary) summary.textContent = items.length === 0
        ? 'No open reports.'
        : `${items.length} open report${items.length === 1 ? '' : 's'}`;
    if (queue) queue.innerHTML = items.map(card).join('');
}

function historyRows(history) {
    if (!Array.isArray(history) || history.length === 0) return `<div style="${MUTED}">No prior enforcement.</div>`;
    return history.map(a => {
        const when = escapeHtml(fmtDate(a.timestamp ?? a.createdAt ?? a.occurredAt ?? a.at));
        const act = escapeHtml(actionLabel(a.type));
        const reason = escapeHtml(a.reason ?? '');
        const ref = a.reference ? ` · <span style="${MUTED}">${escapeHtml(a.reference)}</span>` : '';
        return `<div style="margin:4px 0; font-size:0.85rem;"><strong>${act}</strong> — ${reason} <span style="${MUTED}">(${when})</span>${ref}</div>`;
    }).join('');
}

// CSAM reports (category 0) get a hash-check section: prior results + a button.
// The button is enabled only when the report targets a held VM whose reference
// matches — but the UI cannot see the hold, so it enables on csam+vm reports and
// lets the server refuse (409/400) with a message. NotScanned must NEVER render
// as "clean" or a green tick — it is "we have not checked", not "we checked".
const SCAN_LABELS = {
    NotScanned: ['Not checked', 'color:var(--text-muted,#888)'],
    Clean: ['No known match', 'color:#22c55e'],
    Match: ['KNOWN MATCH', 'color:#ef4444; font-weight:700'],
    Unscannable: ['Could not read', 'color:#f59e0b'],
};

function scanRecordRow(sr) {
    const status = sr.status ?? sr.Status;
    const outcome = sr.outcome ?? sr.Outcome ?? 'NotScanned';
    const when = fmtDate(sr.completedAt ?? sr.CompletedAt ?? sr.orderedAt ?? sr.OrderedAt);
    if (status === 0 || status === 'Ordered')
        return `<div style="${MUTED}; font-size:0.85rem;">⏳ ordered ${escapeHtml(when)} — awaiting node</div>`;
    if (status === 2 || status === 'Failed') {
        const err = escapeHtml(sr.error ?? sr.Error ?? 'scan did not run');
        return `<div style="font-size:0.85rem; color:#f59e0b;">✗ could not check — ${err} <span style="${MUTED}">(${escapeHtml(when)})</span></div>`;
    }
    const [label, style] = SCAN_LABELS[outcome] ?? SCAN_LABELS.NotScanned;
    const matcher = escapeHtml(sr.matcher ?? sr.Matcher ?? '');
    return `<div style="font-size:0.85rem; ${style}">● ${escapeHtml(label)}
            <span style="${MUTED}">— ${matcher} (${escapeHtml(when)})</span></div>`;
}

function scanSection(r) {
    const category = r.category ?? r.Category;
    const isCsam = category === 0 || category === 'Csam';
    if (!isCsam) return '';
    const records = r.scanRecords ?? r.ScanRecords ?? [];
    const ref = escapeHtml(r.reference ?? '');
    const rows = records.length
        ? records.map(scanRecordRow).join('')
        : `<div style="${MUTED}; font-size:0.85rem;">No hash check run yet.</div>`;
    return `
    <div style="margin-top:8px; padding-top:8px; border-top:1px solid var(--border,#333);">
      <div style="display:flex; align-items:center; gap:8px; justify-content:space-between;">
        <strong style="font-size:0.85rem;">CSAM hash check</strong>
        <button class="btn btn-secondary" data-action="scan" data-ref="${ref}"
                title="Order a targeted hash check. The VM must already be held under this report.">
          Run hash check
        </button>
      </div>
      <div style="margin-top:6px;">${rows}</div>
    </div>`;
}

function card(item) {
    const r = item.report ?? item.Report ?? {};
    const history = item.targetHistory ?? item.TargetHistory ?? [];
    const ref = escapeHtml(r.reference ?? '');
    const wallet = r.targetWallet ?? '';
    const contact = r.reporterContact ?? '';
    return `
      <div class="form-section" data-report-ref="${ref}">
        <h3 class="form-section-title" style="margin:0; display:flex; align-items:center; gap:8px;">
          <span class="template-status-badge" style="${priorityStyle(r.priority)}">${escapeHtml(priorityLabel(r.priority))}</span>
          ${escapeHtml(categoryLabel(r.category))}
          <span style="${MUTED}; font-weight:400; font-size:0.85rem;">${ref}</span>
        </h3>
        <div style="${MUTED}; margin:4px 0; font-size:0.85rem;">
          reported: <span style="font-family:monospace;">${escapeHtml(r.reportedResource ?? '—')}</span>
          · SLA ${escapeHtml(r.sla ?? '')}
          · ${escapeHtml(fmtDate(r.createdAt))}
        </div>

        <p style="margin:8px 0; white-space:pre-wrap;">${escapeHtml(r.description ?? '')}</p>

        <div style="${MUTED}; font-size:0.85rem; margin-bottom:8px;">
          ${wallet
            ? `target: <span style="font-family:monospace;" title="${escapeHtml(wallet)}">${escapeHtml(shortWallet(wallet))}</span>`
            : 'no target wallet identified'}
          ${contact ? ` · reporter: ${escapeHtml(contact)}` : ''}
        </div>

        <details style="margin:8px 0;">
          <summary style="cursor:pointer; font-weight:600;">Target enforcement history (${Array.isArray(history) ? history.length : 0})</summary>
          <div style="margin-top:8px;">${historyRows(history)}</div>
        </details>

        <div style="display:flex; gap:8px; align-items:center; margin-top:8px; flex-wrap:wrap;">
          <input class="form-input aa-reason" placeholder="Reason (required)" style="flex:1 1 240px; margin:0;">
          <input class="form-input aa-wallet" placeholder="Target wallet (for takedown)" value="${escapeHtml(wallet)}" style="flex:1 1 240px; margin:0; font-family:monospace;">
          <button class="btn btn-secondary" data-action="dismiss"  data-ref="${ref}" title="Close — not actionable">Dismiss</button>
          <button class="btn btn-secondary" data-action="warn"     data-ref="${ref}" title="Close with a warning note — no service withheld">Warn</button>
          <button class="btn btn-danger"    data-action="takedown" data-ref="${ref}" title="Suspend the target wallet and stop its VMs">Takedown</button>
        </div>
        ${scanSection(r)}
      </div>`;
}

// ── Actions ──────────────────────────────────────────────────────────────────

function onQueueClick(e) {
    const btn = e.target.closest('button[data-action]');
    if (!btn) return;
    resolve(btn.dataset.action, btn);
}

async function resolve(action, btn) {
    const cardEl = btn.closest('[data-report-ref]');
    const ref = btn.dataset.ref;

    // Scan is not a resolution — it orders a hash check and refreshes.
    if (action === 'scan') {
        const reason = (cardEl?.querySelector('.aa-reason')?.value || '').trim()
            || 'targeted CSAM hash check';
        btn.disabled = true;
        try {
            await call('/api/admin/compliance/scan-vm', {
                vmId: (r_vmId(cardEl) || ''), reference: ref, reason
            });
            showToast('Hash check ordered', 'success');
            loadQueue();
        } catch (e) {
            showToast(e.message, 'error');
            btn.disabled = false;
        }
        return;
    }

    const reason = (cardEl?.querySelector('.aa-reason')?.value || '').trim();
    if (!reason) { showToast('A reason is required', 'error'); cardEl?.querySelector('.aa-reason')?.focus(); return; }

    const body = { action, reason };
    if (action === 'takedown') {
        const wallet = (cardEl?.querySelector('.aa-wallet')?.value || '').trim();
        if (!wallet) { showToast('Takedown needs a target wallet', 'error'); cardEl?.querySelector('.aa-wallet')?.focus(); return; }
        body.targetWallet = wallet;
        if (!confirm(`Take down ${shortWallet(wallet)}? This suspends the wallet and stops its VMs.`)) return;
    }

    cardEl?.querySelectorAll('button[data-action]').forEach(b => b.disabled = true);
    try {
        const res = await call(`/api/admin/abuse/${encodeURIComponent(ref)}/resolve`, body);
        const msg = action === 'takedown'
            ? `Taken down${res?.affectedVms ? ` — ${res.affectedVms} VM(s) stopped` : ''}`
            : (action === 'warn' ? 'Warned (closed)' : 'Dismissed');
        showToast(msg, 'success');
        loadQueue();
    } catch (e) {
        showToast(e.message, 'error');
        cardEl?.querySelectorAll('button[data-action]').forEach(b => b.disabled = false);
    }
}