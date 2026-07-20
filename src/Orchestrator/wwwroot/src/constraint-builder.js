/**
 * constraint-builder.js
 *
 * Single reusable scheduling constraint builder. Five mount points:
 *   1. Create VM modal         (app.js — replaces attachConstraintBuilder)
 *   2. Template deploy modal   (template-detail.js — replaces region/zone dropdowns)
 *   3. Template detail page    (readonly display of template constraints)
 *   4. Stranded VM editor      (app.js — Error-state constraint update modal)
 *   5. Diagnostics view        (readonly with eligibility annotations)
 *
 * Usage:
 *   import { mount } from './constraint-builder.js';
 *
 *   const handle = await mount(containerEl, {
 *     initial:     existingConstraints,           // Constraint[] — user-editable rows
 *     lockedRows:  templateMinimumConstraints,    // Constraint[] — non-removable (template MinimumSpec)
 *     mode:        'edit',                        // 'edit' | 'readonly'
 *     onChange:    (constraints) => { ... },      // fired on every edit
 *     apiFetch:    api,                           // authenticated fetch fn — enables live preview
 *     apiBase:     '',                            // URL prefix (default: same origin)
 *     qualityTier: 'Standard',                   // forwarded to preview endpoint
 *   });
 *
 *   handle.getConstraints()    → Constraint[]
 *   handle.setConstraints(c)   → void  (replaces non-locked rows)
 *   handle.destroy()           → void
 *
 * The component is a pure view over a Constraint[] array. All state lives in
 * the DOM and is read on demand — no parallel state object to drift.
 *
 * Vocabulary is loaded once (GET /api/vms/constraint-vocabulary, anonymous)
 * and cached at module level for the lifetime of the page. Each mount gets
 * its own preview debounce timer.
 */

// ── Type-to-operator compatibility ────────────────────────────────────────────
// Must stay aligned with ConstraintEvaluator.BuildOperatorRegistry.
// When a new operator is added to the registry, add it here too.
const TYPE_OPERATORS = {
    String: ['eq', 'neq', 'in', 'not_in',
        'starts_with', 'ends_with', 'includes',
        'adjacent_to', 'same_continent_as', 'has_jurisdiction_tag'],
    Numeric: ['eq', 'neq', 'in', 'not_in', 'gte', 'lte', 'gt', 'lt'],
    Boolean: ['eq', 'neq'],
    StringList: ['contains', 'not_contains', 'contains_all', 'contains_any', 'contains_none'],
};

// Friendly display labels for operators shown in the dropdown.
const OP_LABEL = {
    eq: '= equals',
    neq: '≠ not equals',
    in: 'in list',
    not_in: 'not in list',
    gte: '≥ at least',
    lte: '≤ at most',
    gt: '> greater than',
    lt: '< less than',
    contains: 'contains',
    not_contains: 'not contains',
    contains_all: 'contains all',
    contains_any: 'contains any',
    contains_none: 'contains none',
    starts_with: 'starts with',
    ends_with: 'ends with',
    includes: 'includes (substring)',
    adjacent_to: 'adjacent to',
    same_continent_as: 'same continent as',
    has_jurisdiction_tag: 'has jurisdiction tag',
};

// ── Vocabulary cache (module-level) ──────────────────────────────────────────
let _vocab = null;

// ── Preset cache (module-level) ───────────────────────────────────────────────
// Loaded once from /config/constraint-presets.json (a static asset in wwwroot).
// null = not yet fetched; [] = fetched but empty or failed.
let _presets = null;

async function loadVocabulary(apiBase) {
    if (_vocab) return _vocab;
    try {
        const r = await fetch(`${apiBase}/api/vms/constraint-vocabulary`);
        const body = r.ok ? await r.json() : {};
        const d = body?.data ?? body;
        _vocab = {
            targets: Array.isArray(d.targets) ? [...d.targets].sort() : [],
            operators: Array.isArray(d.operators) ? [...d.operators].sort() : [],
            targetTypes: d.targetTypes ?? {},
        };
    } catch (e) {
        console.warn('[ConstraintBuilder] Vocabulary load failed:', e);
        _vocab = { targets: [], operators: [], targetTypes: {} };
    }
    return _vocab;
}

async function loadPresets(apiBase) {
    if (_presets !== null) return _presets;
    try {
        const r = await fetch(`${apiBase}/config/constraint-presets.json`);
        _presets = r.ok ? await r.json() : [];
    } catch (_e) {
        _presets = [];
    }
    return _presets;
}

// ── One-time style injection ──────────────────────────────────────────────────
function injectStyles() {
    if (document.getElementById('_cb-module-styles')) return;
    const s = document.createElement('style');
    s.id = '_cb-module-styles';
    // New classes only — existing cb-* classes are covered by the app stylesheet.
    s.textContent = `
        .cb-row--locked .cb-target,
        .cb-row--locked .cb-operator,
        .cb-row--locked .cb-value {
            background: var(--bg-elevated, rgba(255,255,255,0.04));
            color: var(--text-muted, #718096);
            cursor: not-allowed;
        }
        .cb-remove--locked {
            background: none; border: none; cursor: default;
            font-size: 1rem; padding: 0 0.25rem; opacity: 0.6;
        }
        .cb-preview {
            border-top: 1px solid var(--border, rgba(255,255,255,0.12));
            padding-top: 0.4rem; margin-top: 0.25rem;
            font-size: 0.78rem; line-height: 1.5;
            color: var(--text-muted, #718096);
        }
        .cb-value { flex: 1; min-width: 0; }
        .cb-presets-label {
            display: block; font-size: 0.70rem; font-weight: 600;
            text-transform: uppercase; letter-spacing: 0.06em;
            color: var(--text-muted, #718096);
            margin-bottom: 0.35rem;
        }
        .cb-presets-grid {
            display: flex; flex-wrap: wrap; gap: 0.3rem; margin-bottom: 0.1rem;
        }
        .cb-preset-item {
            display: inline-flex; align-items: center; gap: 0.3rem;
            padding: 0.2rem 0.55rem; border-radius: 999px; cursor: pointer;
            border: 1px solid var(--border, rgba(255,255,255,0.12));
            font-size: 0.76rem;
            background: var(--bg-elevated, rgba(255,255,255,0.05));
            color: var(--text-primary, inherit);
            transition: border-color 0.12s, background 0.12s;
            user-select: none;
        }
        .cb-preset-item:hover { border-color: var(--primary, #4a9eff); }
        .cb-preset-item:has(input:checked) {
            border-color: var(--primary, #4a9eff);
            background: rgba(74,158,255,0.14);
            color: var(--primary, #4a9eff);
        }
        .cb-preset-item input[type="checkbox"] { margin: 0; cursor: pointer; }
        hr.cb-presets-divider {
            border: none; border-top: 1px dashed var(--border, rgba(255,255,255,0.12));
            margin: 0.45rem 0 0.5rem;
        }
    `;
    document.head.appendChild(s);
}

// ── Preset section ────────────────────────────────────────────────────────────
// Renders a pill-checkbox grid of curated presets above the custom builder.
// Toggling a preset adds/removes its constraint set from the combined output.
// Preset constraints are tracked separately from custom rows — they don't
// appear in the row editor and don't conflict with it.
function buildPresetSection(presets, activePresets, onChange) {
    const wrap = document.createElement('div');

    const lbl = document.createElement('span');
    lbl.className = 'cb-presets-label';
    lbl.textContent = 'Common requirements';

    const grid = document.createElement('div');
    grid.className = 'cb-presets-grid';

    presets.forEach(preset => {
        const item = document.createElement('label');
        item.className = 'cb-preset-item';
        if (preset.description) item.title = preset.description;

        const cb = document.createElement('input');
        cb.type = 'checkbox';

        cb.addEventListener('change', () => {
            if (cb.checked) activePresets.add(preset.id);
            else activePresets.delete(preset.id);
            onChange();
        });

        const txt = document.createTextNode(
            (preset.icon ? preset.icon + '\u202f' : '') + preset.label
        );

        item.appendChild(cb);
        item.appendChild(txt);
        grid.appendChild(item);
    });

    wrap.appendChild(lbl);
    wrap.appendChild(grid);
    return wrap;
}

// ── DOM element factory ──────────────────────────────────────────────────────
function mkEl(tag, className, attrs) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (attrs) {
        for (const [k, v] of Object.entries(attrs)) {
            if (k === 'style') node.style.cssText = v;
            else node.setAttribute(k, v);
        }
    }
    return node;
}

function esc(v) {
    return String(v ?? '').replace(/[&<>"']/g,
        c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ── Typed value input factory ─────────────────────────────────────────────────
// Returns the right input element for the given target type + operator,
// pre-filled with currentValue when provided.
const LIST_OPS = new Set([
    'in', 'not_in', 'contains_all', 'contains_any', 'contains_none',
]);

function makeValueInput(type, operator, currentValue) {
    if (type === 'Boolean') {
        const sel = mkEl('select', 'cb-value');
        sel.innerHTML = '<option value="">— select —</option>'
            + '<option value="true">true</option>'
            + '<option value="false">false</option>';
        if (currentValue !== null && currentValue !== undefined)
            sel.value = String(currentValue);
        return sel;
    }

    const inp = mkEl('input', 'cb-value');
    inp.autocomplete = 'off';

    if (type === 'Numeric' && !LIST_OPS.has(operator)) {
        inp.type = 'number';
        inp.step = 'any';
        inp.placeholder = '0';
        if (currentValue !== null && currentValue !== undefined)
            inp.value = currentValue;
        return inp;
    }

    inp.type = 'text';
    inp.placeholder =
        LIST_OPS.has(operator) ? 'comma-separated  (e.g. DE, FR, NL)'
            : operator === 'adjacent_to' ? 'region code  (e.g. eu-central)'
                : operator === 'has_jurisdiction_tag' ? 'country code  (e.g. DE)'
                    : type === 'StringList' ? 'value'
                        : 'value  (e.g. eu-central  or  99.5)';

    if (currentValue !== null && currentValue !== undefined)
        inp.value = Array.isArray(currentValue)
            ? currentValue.join(', ')
            : String(currentValue);

    return inp;
}

// ── Value parser ──────────────────────────────────────────────────────────────
// Mirrors parseConstraintValue in app.js but is type-aware.
function parseValue(raw, type, operator) {
    if (raw === '' || raw == null) return null;
    if (type === 'Boolean') return raw === 'true' || raw === true;

    if (LIST_OPS.has(operator) || (typeof raw === 'string' && raw.includes(','))) {
        return raw.split(',').map(s => {
            const t = s.trim();
            if (!t) return null;
            if (type === 'Numeric') { const n = Number(t); return isNaN(n) ? t : n; }
            return t;
        }).filter(v => v !== null);
    }

    if (type === 'Numeric') {
        const n = Number(raw);
        return isNaN(n) ? raw : n;
    }

    return raw;
}

// ── Row builder ───────────────────────────────────────────────────────────────
function buildRow(vocab, constraint, locked, onRemove, onChange) {
    const row = mkEl('div', locked ? 'cb-row cb-row--locked' : 'cb-row');

    const initType = constraint
        ? (vocab.targetTypes[constraint.target] ?? 'String')
        : 'String';

    // Target dropdown — all registered targets from vocabulary.
    const tgt = mkEl('select', 'cb-target');
    tgt.disabled = locked;
    tgt.innerHTML = '<option value="">— target —</option>'
        + vocab.targets.map(t =>
            `<option value="${esc(t)}"${constraint?.target === t ? ' selected' : ''}>${esc(t)}</option>`
        ).join('');

    // Operator dropdown — filtered by target value type.
    const op = mkEl('select', 'cb-operator');
    op.disabled = locked;

    function refreshOps(type, selectedOp) {
        const compatible = TYPE_OPERATORS[type] ?? [];
        const available = compatible.filter(o => vocab.operators.includes(o));
        op.innerHTML = '<option value="">— op —</option>'
            + available.map(o =>
                `<option value="${esc(o)}"${selectedOp === o ? ' selected' : ''}>${esc(OP_LABEL[o] ?? o)}</option>`
            ).join('');
    }

    refreshOps(initType, constraint?.operator ?? '');

    // Value input — re-rendered when target or operator changes.
    // Queried from DOM in _getConstraint to avoid stale closure.
    let valEl = makeValueInput(initType, constraint?.operator ?? '', constraint?.value ?? null);
    valEl.disabled = locked;

    function refreshVal(type, operator, preserveText) {
        const prev = preserveText ? valEl.value : undefined;
        const nv = makeValueInput(type, operator, prev ?? null);
        nv.disabled = locked;
        nv.addEventListener('change', onChange);
        nv.addEventListener('input', onChange);
        valEl.replaceWith(nv);
        valEl = nv;
    }

    tgt.addEventListener('change', () => {
        const type = vocab.targetTypes[tgt.value] ?? 'String';
        refreshOps(type, '');
        refreshVal(type, '', false);
        onChange();
    });

    op.addEventListener('change', () => {
        const type = vocab.targetTypes[tgt.value] ?? 'String';
        refreshVal(type, op.value, true);
        onChange();
    });

    valEl.addEventListener('change', onChange);
    valEl.addEventListener('input', onChange);

    // Remove / lock button.
    const rmBtn = mkEl('button', locked ? 'cb-remove cb-remove--locked' : 'cb-remove',
        { type: 'button' });
    if (locked) {
        rmBtn.title = 'Required by template — cannot be removed';
        rmBtn.textContent = '🔒';
        rmBtn.disabled = true;
    } else {
        rmBtn.title = 'Remove constraint';
        rmBtn.textContent = '✕';
        rmBtn.addEventListener('click', () => onRemove(row));
    }

    row.appendChild(tgt);
    row.appendChild(op);
    row.appendChild(valEl);
    row.appendChild(rmBtn);

    // Read the row's current state. Querying .cb-value from the DOM avoids
    // stale closure issues when valEl is replaced on target/operator change.
    row._getConstraint = () => {
        const target = tgt.value.trim();
        const operator = op.value.trim();
        const raw = row.querySelector('.cb-value')?.value?.trim() ?? '';
        if (!target || !operator) return null; // incomplete row — skipped
        const type = vocab.targetTypes[target] ?? 'String';
        return { target, operator, value: parseValue(raw, type, operator) };
    };

    return row;
}

// ── Preview line ──────────────────────────────────────────────────────────────
function updatePreview(el, data) {
    if (!data) { el.style.display = 'none'; return; }
    const { eligible, totalOnline, rejectionReasons } = data;
    const colour =
        eligible === 0 ? 'var(--color-error,#e53e3e)'
            : eligible < 3 ? 'var(--color-warning,#d69e2e)'
                : 'var(--color-success,#38a169)';
    let html = `<span style="font-weight:600;color:${colour}">${eligible}</span> of ${totalOnline} nodes match`;
    if (rejectionReasons?.length) {
        const top = rejectionReasons.slice(0, 3)
            .map(r => `${esc(r.reason)} (${r.count})`)
            .join(' · ');
        html += `<br><span style="opacity:0.8">Mismatches: ${top}</span>`;
    }
    el.innerHTML = html;
    el.style.display = 'block';
}

// ── mount (public export) ─────────────────────────────────────────────────────
export async function mount(containerEl, options = {}) {
    const {
        initial = [],
        lockedRows = [],
        mode = 'edit',
        onChange = null,
        apiFetch = null,   // authenticated fetch — enables preview
        apiBase = '',
        qualityTier = 'Standard',
    } = options;

    const readonly = mode === 'readonly';
    injectStyles();
    const [vocab, presets] = await Promise.all([
        loadVocabulary(apiBase),
        loadPresets(apiBase),
    ]);
    // Active preset IDs — constraints from active presets are included in
    // collectConstraints() ahead of custom builder rows.
    const activePresets = new Set();

    // ── Scaffold ──────────────────────────────────────────────────────────────
    containerEl.innerHTML = '';

    const section = mkEl('div', 'cb-section');
    const toggle = mkEl('button', 'cb-toggle', { type: 'button', 'aria-expanded': 'false' });
    const body = mkEl('div', 'cb-body', { style: 'display:none' });
    const hint = mkEl('p', 'cb-hint');
    const rowsEl = mkEl('div', 'cb-rows');
    const footer = mkEl('div', 'cb-footer');
    const addBtn = mkEl('button', 'cb-add-btn', { type: 'button' });
    const preview = mkEl('div', 'cb-preview', { style: 'display:none' });
    const countEl = mkEl('span', 'cb-toggle-count', { style: 'display:none' });
    const chevron = mkEl('span', 'cb-chevron');

    toggle.innerHTML = `<span class="cb-toggle-label"><span class="cb-icon">⚙</span> Scheduling Constraints</span>`;
    toggle.appendChild(countEl);
    toggle.appendChild(chevron);
    chevron.textContent = '›';

    hint.textContent = readonly
        ? 'Constraints declared on this template. Locked entries are mandatory requirements for all deployments.'
        : 'Each constraint is a hard filter — the VM lands only on nodes where all constraints pass. Leave empty to accept any eligible node.';

    addBtn.textContent = '+ Add Constraint';
    if (readonly) addBtn.style.display = 'none';

    footer.appendChild(addBtn);
    body.appendChild(hint);

    // Preset pill-grid — only in edit mode, only when presets are available.
    // fireChange is a hoisted function declaration, so referencing it here
    // (before it appears in source) is safe.
    if (!readonly && presets.length > 0) {
        body.appendChild(buildPresetSection(presets, activePresets, fireChange));
        const divider = document.createElement('hr');
        divider.className = 'cb-presets-divider';
        body.appendChild(divider);
    }

    body.appendChild(rowsEl);
    body.appendChild(footer);
    body.appendChild(preview);
    section.appendChild(toggle);
    section.appendChild(body);
    containerEl.appendChild(section);

    // ── Collapse / expand ─────────────────────────────────────────────────────
    toggle.addEventListener('click', () => {
        const open = body.style.display !== 'none';
        body.style.display = open ? 'none' : 'flex';
        toggle.setAttribute('aria-expanded', String(!open));
        chevron.textContent = open ? '›' : '‹';
    });

    // ── Per-mount preview debounce ────────────────────────────────────────────
    let previewTimer = null;

    // ── Internal helpers ──────────────────────────────────────────────────────
    function syncCount() {
        // Count both active preset selections and custom builder rows so the
        // badge reflects the true total number of active constraints.
        const n = activePresets.size + rowsEl.querySelectorAll('.cb-row').length;
        countEl.textContent = n;
        countEl.style.display = n > 0 ? 'inline' : 'none';
    }

    function collectConstraints() {
        const out = [];
        // Preset constraints first — stable ordering, independent of custom rows.
        for (const id of activePresets) {
            const p = presets.find(x => x.id === id);
            if (p) out.push(...p.constraints);
        }
        // Custom builder rows.
        rowsEl.querySelectorAll('.cb-row').forEach(r => {
            if (typeof r._getConstraint === 'function') {
                const c = r._getConstraint();
                if (c) out.push(c);
            }
        });
        return out;
    }

    function fireChange() {
        syncCount();
        const constraints = collectConstraints();
        onChange?.(constraints);

        // Live preview — debounced, requires authenticated apiFetch.
        clearTimeout(previewTimer);
        if (apiFetch && constraints.length > 0) {
            previewTimer = setTimeout(async () => {
                try {
                    const res = await apiFetch('/api/vms/scheduling/preview', {
                        method: 'POST',
                        body: JSON.stringify({ constraints, qualityTier }),
                    });
                    if (!res.ok) { updatePreview(preview, null); return; }
                    const d = await res.json();
                    updatePreview(preview, d.data ?? d);
                } catch (_e) { /* preview is best-effort; swallow errors */ }
            }, 500);
        } else {
            updatePreview(preview, null);
        }
    }

    function removeRow(row) { row.remove(); fireChange(); }

    function addRow(constraint, locked) {
        const row = buildRow(vocab, constraint, locked, removeRow, fireChange);
        rowsEl.appendChild(row);
    }

    // ── Populate ──────────────────────────────────────────────────────────────
    lockedRows.forEach(c => addRow(c, true));
    initial.forEach(c => addRow(c, false));

    if (lockedRows.length + initial.length > 0) {
        body.style.display = 'flex';
        toggle.setAttribute('aria-expanded', 'true');
        chevron.textContent = '‹';
    }

    syncCount();

    // ── Add-row button ────────────────────────────────────────────────────────
    addBtn.addEventListener('click', () => {
        if (body.style.display === 'none') {
            body.style.display = 'flex';
            toggle.setAttribute('aria-expanded', 'true');
            chevron.textContent = '‹';
        }
        addRow(null, false);
        fireChange();
    });

    // ── Public handle ─────────────────────────────────────────────────────────
    return {
        /** Return the current Constraint[] (locked + user rows, incomplete rows omitted). */
        getConstraints: collectConstraints,

        /** Replace non-locked rows with the given constraints. */
        setConstraints(constraints = []) {
            rowsEl.querySelectorAll('.cb-row:not(.cb-row--locked)').forEach(r => r.remove());
            constraints.forEach(c => addRow(c, false));
            fireChange();
        },

        /** Unmount and clean up. */
        destroy() {
            clearTimeout(previewTimer);
            containerEl.innerHTML = '';
        },
    };
}