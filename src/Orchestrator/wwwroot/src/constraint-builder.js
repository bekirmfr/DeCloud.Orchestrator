/**
 * constraint-builder.js
 * Scheduling constraint builder for the orchestrator dashboard.
 *
 * Public API (three calls from app.js):
 *
 *   await ConstraintBuilder.init()
 *     – fetch vocabulary once, call from DOMContentLoaded
 *
 *   ConstraintBuilder.attach(containerEl)
 *     – inject the builder UI into containerEl, call when deploy modal opens
 *
 *   ConstraintBuilder.read()
 *     – return current constraint list, call just before POST /api/vms
 *
 *   ConstraintBuilder.attachUpdateModal(vmId, currentConstraints)
 *     – open the update-constraints modal for an Error-state VM
 *
 * Value parsing (smart, no type schema needed client-side):
 *   "DE,FR,NL"   → ["DE","FR","NL"]   (comma list → string array)
 *   "[1,2,3]"    → [1,2,3]            (JSON array)
 *   "99.5"       → 99.5               (numeric)
 *   "true/false" → true/false         (boolean)
 *   "EU"         → "EU"               (scalar string)
 */

'use strict';

export const ConstraintBuilder = (() => {

  // ── State ──────────────────────────────────────────────────────────────────

  let _targets   = [];
  let _operators = [];
  let _ready     = false;

  // ── Vocabulary fetch ───────────────────────────────────────────────────────

  async function init() {
    try {
      const res = await fetch('/api/vms/constraint-vocabulary', {
        headers: { 'Authorization': `Bearer ${getToken()}` }
      });
      if (!res.ok) throw new Error(`HTTP ${res.status}`);
      const data = await res.json();
      _targets   = data.targets   ?? [];
      _operators = data.operators ?? [];
      _ready     = true;
    } catch (err) {
      console.warn('[ConstraintBuilder] vocabulary fetch failed:', err);
    }
  }

  // ── Builder attachment ─────────────────────────────────────────────────────

  /**
   * Inject the constraint builder UI into containerEl.
   * Call each time the deploy modal opens (idempotent — clears previous rows).
   * @param {HTMLElement} containerEl
   * @param {Array}       [initialConstraints] – pre-populate (marketplace deploy)
   */
  function attach(containerEl, initialConstraints = []) {
    containerEl.innerHTML = '';
    containerEl.appendChild(_buildSection(initialConstraints));
  }

  function _buildSection(initial) {
    const section = document.createElement('div');
    section.className = 'cb-section';
    section.innerHTML = `
      <button type="button" class="cb-toggle" aria-expanded="false">
        <span class="cb-toggle-label">
          <span class="cb-icon">⚙</span>
          Scheduling Constraints
        </span>
        <span class="cb-toggle-count" hidden></span>
        <span class="cb-chevron">›</span>
      </button>
      <div class="cb-body" hidden>
        <p class="cb-hint">
          Each constraint is a hard filter. The VM will only be placed on nodes
          where ALL constraints pass. Leave empty to accept any eligible node.
        </p>
        <div class="cb-rows"></div>
        <div class="cb-footer">
          <button type="button" class="cb-add-btn">+ Add Constraint</button>
          <span class="cb-error" hidden></span>
        </div>
      </div>
    `;

    const toggle  = section.querySelector('.cb-toggle');
    const body    = section.querySelector('.cb-body');
    const chevron = section.querySelector('.cb-chevron');
    const rows    = section.querySelector('.cb-rows');
    const addBtn  = section.querySelector('.cb-add-btn');
    const errEl   = section.querySelector('.cb-error');
    const countEl = section.querySelector('.cb-toggle-count');

    toggle.addEventListener('click', () => {
      const open = body.hidden;
      body.hidden = !open;
      toggle.setAttribute('aria-expanded', String(open));
      chevron.textContent = open ? '‹' : '›';
    });

    addBtn.addEventListener('click', () => {
      rows.appendChild(_buildRow(rows, countEl));
      _updateCount(rows, countEl);
    });

    // Pre-populate
    initial.forEach(c => {
      const row = _buildRow(rows, countEl);
      rows.appendChild(row);
      _fillRow(row, c);
      _updateCount(rows, countEl);
    });
    if (initial.length > 0) {
      body.hidden = false;
      toggle.setAttribute('aria-expanded', 'true');
      chevron.textContent = '‹';
    }

    return section;
  }

  function _buildRow(rowContainer, countEl) {
    const row = document.createElement('div');
    row.className = 'cb-row';
    row.innerHTML = `
      <select class="cb-target">
        <option value="">— target —</option>
        ${_targets.map(t => `<option value="${esc(t)}">${esc(t)}</option>`).join('')}
      </select>
      <select class="cb-operator">
        <option value="">— op —</option>
        ${_operators.map(o => `<option value="${esc(o)}">${esc(o)}</option>`).join('')}
      </select>
      <input
        type="text"
        class="cb-value"
        placeholder="value  (e.g. DE  or  DE,FR,NL  or  99.5)"
        autocomplete="off"
      />
      <button type="button" class="cb-remove" aria-label="Remove constraint">✕</button>
    `;
    row.querySelector('.cb-remove').addEventListener('click', () => {
      row.remove();
      _updateCount(rowContainer, countEl);
    });
    return row;
  }

  function _fillRow(row, constraint) {
    const tgt = row.querySelector('.cb-target');
    const op  = row.querySelector('.cb-operator');
    const val = row.querySelector('.cb-value');

    if (_targets.includes(constraint.target))   tgt.value = constraint.target;
    if (_operators.includes(constraint.operator)) op.value = constraint.operator;

    const v = constraint.value;
    if (Array.isArray(v))       val.value = v.join(', ');
    else if (v !== null && v !== undefined) val.value = String(v);
  }

  function _updateCount(rowContainer, countEl) {
    const n = rowContainer.querySelectorAll('.cb-row').length;
    if (n > 0) {
      countEl.textContent = `${n}`;
      countEl.hidden = false;
    } else {
      countEl.hidden = true;
    }
  }

  // ── Read (serialize) ───────────────────────────────────────────────────────

  /**
   * Read all constraint rows from whichever builder is currently in the DOM.
   * Returns null if any row is incomplete (target or operator missing).
   * Shows inline error on invalid rows.
   * @returns {Array|null}
   */
  function read() {
    const rows = document.querySelectorAll('.cb-row');
    if (rows.length === 0) return [];

    const result = [];
    let valid = true;

    rows.forEach((row, i) => {
      const target   = row.querySelector('.cb-target').value.trim();
      const operator = row.querySelector('.cb-operator').value.trim();
      const rawVal   = row.querySelector('.cb-value').value.trim();

      _clearRowError(row);

      if (!target || !operator) {
        _markRowError(row, 'Select a target and operator');
        valid = false;
        return;
      }

      const value = _parseValue(rawVal);
      result.push({ target, operator, value });
    });

    return valid ? result : null;
  }

  function _parseValue(raw) {
    if (raw === '')      return null;
    if (raw === 'true')  return true;
    if (raw === 'false') return false;

    // JSON array e.g. ["DE","FR"]
    if (raw.startsWith('[')) {
      try { return JSON.parse(raw); } catch { /* fall through */ }
    }

    // Comma-separated list → string array
    if (raw.includes(',')) {
      return raw.split(',').map(s => {
        const t = s.trim();
        const n = Number(t);
        return isNaN(n) ? t : n;
      });
    }

    // Numeric
    const n = Number(raw);
    if (!isNaN(n) && raw !== '') return n;

    // Scalar string
    return raw;
  }

  function _markRowError(row, msg) {
    row.classList.add('cb-row--error');
    let tip = row.querySelector('.cb-row-tip');
    if (!tip) {
      tip = document.createElement('span');
      tip.className = 'cb-row-tip';
      row.appendChild(tip);
    }
    tip.textContent = msg;
  }

  function _clearRowError(row) {
    row.classList.remove('cb-row--error');
    row.querySelector('.cb-row-tip')?.remove();
  }

  // ── Update-constraints modal (Error-state VMs) ─────────────────────────────

  /**
   * Open the scheduling-constraints update modal for a stranded VM.
   * @param {string} vmId
   * @param {Array}  currentConstraints  – from vm.spec.constraints
   */
  function attachUpdateModal(vmId, currentConstraints = []) {
    let modal = document.getElementById('cb-update-modal');
    if (!modal) {
      modal = _createUpdateModal();
      document.body.appendChild(modal);
    }

    const title   = modal.querySelector('.cb-update-title');
    const builder = modal.querySelector('.cb-update-builder');
    const saveBtn = modal.querySelector('.cb-update-save');
    const errEl   = modal.querySelector('.cb-update-error');

    title.textContent = `Update Scheduling Constraints`;
    errEl.hidden = true;
    attach(builder, currentConstraints);

    // Open
    modal.hidden = false;
    requestAnimationFrame(() => modal.classList.add('cb-modal--open'));

    // Close handler
    const close = () => {
      modal.classList.remove('cb-modal--open');
      setTimeout(() => { modal.hidden = true; }, 200);
    };
    modal.querySelector('.cb-update-close').onclick = close;
    modal.onclick = e => { if (e.target === modal) close(); };

    // Save handler
    saveBtn.onclick = async () => {
      errEl.hidden = true;
      const constraints = read();
      if (constraints === null) return; // row-level errors already shown

      saveBtn.disabled = true;
      saveBtn.textContent = 'Saving…';

      try {
        const res = await fetch(`/api/vms/${vmId}/scheduling`, {
          method: 'PATCH',
          headers: {
            'Content-Type':  'application/json',
            'Authorization': `Bearer ${getToken()}`
          },
          body: JSON.stringify({ constraints })
        });

        if (res.ok) {
          close();
          // Trigger a VM list refresh if the parent page exposes one
          if (typeof window.refreshVms === 'function') window.refreshVms();
        } else {
          const body = await res.json().catch(() => ({}));
          errEl.textContent = body?.message ?? `Error ${res.status}`;
          errEl.hidden = false;
        }
      } catch (err) {
        errEl.textContent = `Network error: ${err.message}`;
        errEl.hidden = false;
      } finally {
        saveBtn.disabled = false;
        saveBtn.textContent = 'Save Constraints';
      }
    };
  }

  function _createUpdateModal() {
    const modal = document.createElement('div');
    modal.id = 'cb-update-modal';
    modal.className = 'cb-modal-overlay';
    modal.hidden = true;
    modal.innerHTML = `
      <div class="cb-modal-box" role="dialog" aria-modal="true">
        <div class="cb-modal-head">
          <span class="cb-update-title"></span>
          <button class="cb-update-close" type="button" aria-label="Close">✕</button>
        </div>
        <div class="cb-modal-body">
          <p class="cb-hint" style="margin-bottom:1rem">
            Replaces all scheduling constraints on this stranded VM.
            The migration scheduler picks up changes within 10 seconds.
            Clear all rows to accept any eligible node.
          </p>
          <div class="cb-update-builder"></div>
          <span class="cb-update-error" hidden style="display:block;margin-top:.5rem;color:var(--danger);font-size:.8rem"></span>
        </div>
        <div class="cb-modal-foot">
          <button class="cb-update-save btn btn-primary" type="button">Save Constraints</button>
        </div>
      </div>
    `;
    return modal;
  }

  // ── Constraint display (VM list cell) ─────────────────────────────────────

  /**
   * Render a compact inline summary of a constraint list.
   * Returns an HTML string.
   * @param {Array} constraints
   */
  function renderSummary(constraints) {
    if (!constraints?.length) return '';
    const count = constraints.length;
    const preview = constraints.slice(0, 2)
      .map(c => `<span class="cb-chip">${esc(c.target)} ${esc(c.operator)} ${esc(_previewValue(c.value))}</span>`)
      .join('');
    const more = count > 2 ? `<span class="cb-chip cb-chip--more">+${count - 2}</span>` : '';
    return `<div class="cb-summary">${preview}${more}</div>`;
  }

  function _previewValue(v) {
    if (Array.isArray(v)) return v.length > 2 ? `[${v.slice(0, 2).join(', ')}, …]` : `[${v.join(', ')}]`;
    return String(v ?? '');
  }

  // ── Helpers ────────────────────────────────────────────────────────────────

  function esc(s) {
    return String(s ?? '')
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;');
  }

  function getToken() {
    // Adjust to however app.js retrieves the JWT / session token
    return window._authToken ?? localStorage.getItem('token') ?? '';
  }

  return { init, attach, read, attachUpdateModal, renderSummary };

})();
