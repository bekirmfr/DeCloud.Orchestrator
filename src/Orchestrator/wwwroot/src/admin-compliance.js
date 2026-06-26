// ============================================================================
// admin-compliance.js — Admin enforcement / takedown surface (Phase 2)
//
// Drives /api/admin/compliance: suspend/unsuspend, block/unblock, bulk import,
// list denylist, audit log. Admin-only — every endpoint is enforced server-side
// with [Authorize(Roles="Admin")]; this UI is merely revealed to admins by
// applyAdminVisibility() in app.js and grants no privilege itself.
//
// Enum values are sent as NUMBERS (BlockSource: 0..3), which bind regardless of
// whether the API serializes enums as numbers or strings. Responses are read
// defensively (number OR enum-name string).
// ============================================================================

import { escapeHtml, showToast } from './utils.js';

let _api = null;
let _rendered = false;

const SOURCE_OPTIONS = [
    { value: 0, label: 'Sanctions (OFAC/SDN)' },
    { value: 1, label: 'Law Enforcement' },
    { value: 2, label: 'Cross-Platform' },
    { value: 3, label: 'Internal' },
];
const SOURCE_NAMES = ['Sanctions', 'LawEnforcement', 'CrossPlatform', 'Internal'];
const SOURCE_LABELS = ['Sanctions', 'Law Enforcement', 'Cross-Platform', 'Internal'];
const ACTION_NAMES = ['Suspend', 'Unsuspend', 'Block', 'Unblock', 'TerminateVms'];
const ACTION_LABELS = ['Suspend', 'Unsuspend', 'Block', 'Unblock', 'Terminate VMs'];

const sourceToValue = s => (typeof s === 'number' ? s : Math.max(0, SOURCE_NAMES.indexOf(s)));
const sourceLabel = s => SOURCE_LABELS[sourceToValue(s)] ?? String(s);
const actionToValue = t => (typeof t === 'number' ? t : Math.max(0, ACTION_NAMES.indexOf(t)));
const actionLabel = t => ACTION_LABELS[actionToValue(t)] ?? String(t);

export function initAdminCompliance(api) {
    _api = api;
    if (!_rendered) { render(); _rendered = true; }
    refreshTables();
}

// ── API helper ───────────────────────────────────────────────────────────────

async function call(path, body) {
    const res = await _api(path, body ? { method: 'POST', body: JSON.stringify(body) } : undefined);
    let json = {};
    try { json = await res.json(); } catch { /* no/!json body */ }
    if (!res.ok || json.success === false) {
        throw new Error(json.message || json.error || `Request failed (${res.status})`);
    }
    return json.data;
}

const val = id => document.getElementById(id)?.value?.trim() ?? '';
const num = id => Number(document.getElementById(id)?.value ?? 0);

// ── Actions ──────────────────────────────────────────────────────────────────

async function doSuspend(unsuspend) {
    const wallet = val('ac-susp-wallet');
    const reason = val('ac-susp-reason');
    if (!wallet) return showToast('Wallet is required', 'error');
    if (!unsuspend && !reason) return showToast('Reason is required', 'error');
    try {
        const data = await call(`/api/admin/compliance/${unsuspend ? 'unsuspend' : 'suspend'}`, { wallet, reason });
        showToast(unsuspend ? 'Wallet unsuspended' : `Suspended — ${data?.affectedVms ?? 0} VM(s) stopped`, 'success');
        refreshTables();
    } catch (e) { showToast(e.message, 'error'); }
}

async function doBlock(unblock) {
    const wallet = val('ac-block-wallet');
    const source = num('ac-block-source');
    const reason = val('ac-block-reason');
    const reference = val('ac-block-ref');
    if (!wallet) return showToast('Wallet is required', 'error');
    if (!unblock && !reason) return showToast('Reason is required', 'error');
    try {
        await call(`/api/admin/compliance/${unblock ? 'unblock' : 'block'}`,
            unblock ? { wallet, source, reason } : { wallet, source, reason, reference });
        showToast(unblock ? 'Denylist entry removed' : 'Wallet blocked', 'success');
        refreshTables();
    } catch (e) { showToast(e.message, 'error'); }
}

async function doBulk() {
    const wallets = val('ac-bulk-wallets').split(/\s+/).filter(Boolean);
    const source = num('ac-bulk-source');
    const reason = val('ac-bulk-reason');
    if (wallets.length === 0) return showToast('Provide at least one wallet', 'error');
    if (!reason) return showToast('Reason is required', 'error');
    try {
        const count = await call('/api/admin/compliance/block/bulk', { wallets, source, reason });
        showToast(`Imported ${count} entr${count === 1 ? 'y' : 'ies'}`, 'success');
        refreshTables();
    } catch (e) { showToast(e.message, 'error'); }
}

async function unblockRow(wallet, sourceValue) {
    try {
        await call('/api/admin/compliance/unblock',
            { wallet, source: sourceValue, reason: 'Removed from denylist (admin UI)' });
        showToast('Denylist entry removed', 'success');
        refreshTables();
    } catch (e) { showToast(e.message, 'error'); }
}

// ── Tables ───────────────────────────────────────────────────────────────────

async function refreshTables() {
    const filter = val('ac-filter');
    const q = filter ? `?wallet=${encodeURIComponent(filter)}` : '';
    try { renderBlocks(await call(`/api/admin/compliance/blocks${q}`) || []); }
    catch (e) { renderBlocks([], e.message); }
    try { renderActions(await call(`/api/admin/compliance/actions${q}`) || []); }
    catch (e) { renderActions([], e.message); }
}

function renderBlocks(rows, err) {
    const tbody = document.getElementById('ac-blocks-body');
    if (!tbody) return;
    if (err) { tbody.innerHTML = `<tr><td colspan="6">${escapeHtml(err)}</td></tr>`; return; }
    if (rows.length === 0) { tbody.innerHTML = `<tr><td colspan="6">No denylist entries.</td></tr>`; return; }
    tbody.innerHTML = rows.map(b => {
        const sv = sourceToValue(b.source);
        return `<tr>
            <td style="font-family:monospace">${escapeHtml(b.walletAddress)}</td>
            <td>${escapeHtml(sourceLabel(b.source))}</td>
            <td>${escapeHtml(b.reason ?? '')}</td>
            <td>${escapeHtml(b.addedBy ?? '')}</td>
            <td>${escapeHtml(fmtDate(b.addedAt))}</td>
            <td><button class="btn btn-secondary" style="padding:4px 10px;font-size:12px"
                data-wallet="${escapeHtml(b.walletAddress)}" data-source="${sv}">Unblock</button></td>
        </tr>`;
    }).join('');
    tbody.querySelectorAll('button[data-wallet]').forEach(btn => {
        btn.onclick = () => unblockRow(btn.getAttribute('data-wallet'), Number(btn.getAttribute('data-source')));
    });
}

function renderActions(rows, err) {
    const tbody = document.getElementById('ac-actions-body');
    if (!tbody) return;
    if (err) { tbody.innerHTML = `<tr><td colspan="5">${escapeHtml(err)}</td></tr>`; return; }
    if (rows.length === 0) { tbody.innerHTML = `<tr><td colspan="5">No actions recorded.</td></tr>`; return; }
    tbody.innerHTML = rows.map(a => `<tr>
        <td>${escapeHtml(fmtDate(a.timestamp))}</td>
        <td>${escapeHtml(actionLabel(a.type))}</td>
        <td style="font-family:monospace">${escapeHtml(a.walletAddress ?? '')}</td>
        <td style="font-family:monospace">${escapeHtml(a.actorWallet ?? '')}</td>
        <td>${escapeHtml(a.reason ?? '')}</td>
    </tr>`).join('');
}

function fmtDate(s) {
    if (!s) return '';
    const d = new Date(s);
    return isNaN(d) ? String(s) : d.toLocaleString();
}

// ── Page skeleton ────────────────────────────────────────────────────────────

function render() {
    const root = document.getElementById('admin-compliance-content');
    if (!root) return;
    const sel = id => `<select id="${id}" class="form-input">${SOURCE_OPTIONS
        .map(o => `<option value="${o.value}">${o.label}</option>`).join('')}</select>`;

    root.innerHTML = `
      <div class="form-section">
        <h3 class="form-section-title">Suspension (platform-internal)</h3>
        <div class="form-group"><label class="form-label">Wallet</label>
          <input id="ac-susp-wallet" class="form-input" placeholder="0x…"></div>
        <div class="form-group"><label class="form-label">Reason</label>
          <input id="ac-susp-reason" class="form-input" placeholder="Reason for the record"></div>
        <div style="display:flex; gap:8px;">
          <button id="ac-suspend" class="btn btn-primary">Suspend + stop VMs</button>
          <button id="ac-unsuspend" class="btn btn-secondary">Unsuspend</button>
        </div>
      </div>

      <div class="form-section">
        <h3 class="form-section-title">Denylist (provenance-bearing)</h3>
        <div class="form-group"><label class="form-label">Wallet</label>
          <input id="ac-block-wallet" class="form-input" placeholder="0x…"></div>
        <div class="form-group"><label class="form-label">Source</label>${sel('ac-block-source')}</div>
        <div class="form-group"><label class="form-label">Reason</label>
          <input id="ac-block-reason" class="form-input"></div>
        <div class="form-group"><label class="form-label">Reference (optional)</label>
          <input id="ac-block-ref" class="form-input" placeholder="case #, SDN id"></div>
        <div style="display:flex; gap:8px;">
          <button id="ac-block" class="btn btn-primary">Block</button>
          <button id="ac-unblock" class="btn btn-secondary">Unblock (this source)</button>
        </div>
      </div>

      <div class="form-section">
        <h3 class="form-section-title">Bulk import</h3>
        <div class="form-group"><label class="form-label">Wallets (one per line)</label>
          <textarea id="ac-bulk-wallets" class="form-input" rows="4" placeholder="0x…"></textarea></div>
        <div class="form-group"><label class="form-label">Source</label>${sel('ac-bulk-source')}</div>
        <div class="form-group"><label class="form-label">Reason</label>
          <input id="ac-bulk-reason" class="form-input"></div>
        <button id="ac-bulk" class="btn btn-primary">Import</button>
      </div>

      <div class="form-section">
        <div style="display:flex; gap:8px; align-items:flex-end;">
          <div class="form-group" style="flex:1; margin:0;"><label class="form-label">Filter by wallet</label>
            <input id="ac-filter" class="form-input" placeholder="0x… (blank = all)"></div>
          <button id="ac-refresh" class="btn btn-secondary">Refresh</button>
        </div>
      </div>

      <div class="form-section">
        <h3 class="form-section-title">Denylist</h3>
        <div style="overflow-x:auto"><table class="data-table" style="width:100%">
          <thead><tr><th>Wallet</th><th>Source</th><th>Reason</th><th>Added by</th><th>Added</th><th></th></tr></thead>
          <tbody id="ac-blocks-body"><tr><td colspan="6">Loading…</td></tr></tbody>
        </table></div>
      </div>

      <div class="form-section">
        <h3 class="form-section-title">Audit log</h3>
        <div style="overflow-x:auto"><table class="data-table" style="width:100%">
          <thead><tr><th>Time</th><th>Action</th><th>Wallet</th><th>Actor</th><th>Reason</th></tr></thead>
          <tbody id="ac-actions-body"><tr><td colspan="5">Loading…</td></tr></tbody>
        </table></div>
      </div>`;

    document.getElementById('ac-suspend').onclick = () => doSuspend(false);
    document.getElementById('ac-unsuspend').onclick = () => doSuspend(true);
    document.getElementById('ac-block').onclick = () => doBlock(false);
    document.getElementById('ac-unblock').onclick = () => doBlock(true);
    document.getElementById('ac-bulk').onclick = doBulk;
    document.getElementById('ac-refresh').onclick = refreshTables;
}
