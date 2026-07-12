/**
 * repo-deploy.js — the "Deploy from Repo" form.
 *
 * A fixed-target entry point (same pattern as the Create VM button →
 * platform-general): this form always deploys the platform-repo-deploy
 * template, through the SAME endpoint and submit ritual as the generic
 * template modal (deploy-submit.js). Custom presentation, shared
 * submission — nothing in this file talks to the deploy API directly.
 *
 * Injection boundary: everything the user types is shell-quoted and/or
 * base64-encoded HERE, client-side, and travels as three opaque
 * variables (DEPLOY_CONF_B64, APP_ENV_B64, DEPLOY_KEY_B64). No raw user
 * string is ever substituted into cloud-init YAML.
 */

import { resolveTemplate, submitTemplateDeploy, afterDeploySuccess } from './deploy-submit.js';
import { showToast } from './utils.js';

const TEMPLATE_SLUG = 'platform-repo-deploy';

let _template = null;          // resolved template doc (cached per page load)
let _platformKeys = null;      // Set of reserved variable names (lowercased)

// Names the platform or this template owns. Used as the fallback when the
// platform-variables fetch fails — for a *reserved-name* check we fall
// CLOSED to this core list (unlike the generic form's display filtering,
// which falls open): silently letting HOSTNAME through would be silently
// overwritten by Layer 3 later, and silent is the one thing a form must
// never be.
const CORE_RESERVED = [
    'vm_id', 'vm_name', 'hostname', 'orchestrator_url', 'ca_public_key',
    'ssh_authorized_keys_block', 'password_config_block', 'admin_password',
    'ssh_password_auth', 'decloud_domain', 'decloud_password',
    'ssh_public_keys', 'deploy_conf_b64', 'app_env_b64', 'deploy_key_b64',
];

// ─── helpers ────────────────────────────────────────────────────────────

/** Unicode-safe base64 (btoa alone chokes on non-latin1). */
function b64(str) {
    const bytes = new TextEncoder().encode(str);
    let bin = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
        bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    }
    return btoa(bin);
}

/** Shell single-quote: ' → '\'' — the value can never escape its quotes. */
function shq(v) {
    return "'" + String(v).replace(/'/g, "'\\''") + "'";
}

function esc(s) {
    return String(s).replace(/[&<>"']/g,
        c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

const $ = id => document.getElementById(id);

function setFieldError(id, msg) {
    let el = $(id + '-error');
    if (!el) {
        // Create on demand — the form must not depend on error <p> elements
        // existing in the markup (a missing element must not mean a
        // silently missing message).
        const field = $(id);
        if (!field) return;
        el = document.createElement('p');
        el.id = id + '-error';
        el.className = 'form-help';
        el.style.color = '#ef4444';
        el.style.display = 'none';
        field.insertAdjacentElement('afterend', el);
    }
    el.textContent = msg || '';
    el.style.display = msg ? '' : 'none';
}

/**
 * One quiet line under the action buttons naming still-empty required
 * fields. Empty-required is not an error while the user is still filling
 * the form — red "required" text on a field they haven't reached yet is
 * noise. Invalid input (a malformed URL, a bad port) is an error and gets
 * red text on the field itself.
 */
function showMissingHint(missing) {
    let el = $('rd-missing-hint');
    if (!el) {
        const btn = $('rd-deploy-btn');
        if (!btn || !btn.parentElement) return;
        el = document.createElement('p');
        el.id = 'rd-missing-hint';
        el.className = 'form-help';
        el.style.cssText = 'text-align:right; margin-top:6px;';
        btn.parentElement.insertAdjacentElement('afterend', el);
    }
    el.textContent = missing.length ? `To deploy, fill in: ${missing.join(', ')}.` : '';
    el.style.display = missing.length ? '' : 'none';
}

// ─── validation ─────────────────────────────────────────────────────────

/** https://host/path or git@host:path. Everything else is rejected. */
function validRepoUrl(url) {
    if (/^https:\/\/[^\s/]+\/\S+$/.test(url)) return true;
    if (/^git@[^\s:]+:\S+$/.test(url)) return true;
    return false;
}

function validateForm() {
    let ok = true;
    const missing = [];

    if (!$('rd-vm-name').value.trim()) missing.push('VM name');

    const url = $('rd-source-url').value.trim();
    if (!url) {
        missing.push('repository URL');
        setFieldError('rd-source-url', '');
    } else if (!validRepoUrl(url)) {
        setFieldError('rd-source-url', 'Use https://host/owner/repo or git@host:owner/repo.');
        ok = false;
    } else {
        setFieldError('rd-source-url', '');
    }

    const port = parseInt($('rd-app-port').value, 10);
    if (!Number.isInteger(port) || port < 1 || port > 65535) {
        setFieldError('rd-app-port', 'Port must be between 1 and 65535.');
        ok = false;
    } else {
        setFieldError('rd-app-port', '');
    }

    if ($('rd-private-toggle').checked) {
        const key = $('rd-deploy-key').value.trim();
        if (!key) {
            missing.push('deploy key (or turn off "Private repository")');
            setFieldError('rd-deploy-key', '');
        } else if (!key.includes('BEGIN')) {
            setFieldError('rd-deploy-key', 'This does not look like a private key (missing BEGIN header).');
            ok = false;
        } else {
            setFieldError('rd-deploy-key', '');
        }
    } else {
        setFieldError('rd-deploy-key', '');
    }

    const envOk = validateEnvRows();

    showMissingHint(missing);
    const valid = ok && envOk && missing.length === 0;
    const btn = $('rd-deploy-btn');
    if (btn) btn.disabled = !valid;
    return valid;
}

// ─── reserved names ─────────────────────────────────────────────────────

async function getReservedNames() {
    if (_platformKeys) return _platformKeys;
    const keys = new Set(CORE_RESERVED);
    try {
        const r = await window.api('/api/marketplace/platform-variables');
        const data = await r.json();
        const body = data.data || data;
        for (const list of [body.static, body.dynamic]) {
            if (Array.isArray(list)) list.forEach(n => keys.add(String(n).toLowerCase()));
        }
    } catch (e) {
        console.warn('[Repo Deploy] platform-variables fetch failed; using core reserved list', e);
    }
    _platformKeys = keys;
    return keys;
}

// ─── env var rows ────────────────────────────────────────────────────────

function addEnvRow(key = '', value = '') {
    const list = $('rd-env-list');
    const row = document.createElement('div');
    row.className = 'form-row rd-env-row';
    row.style.cssText = 'gap:8px; margin-bottom:6px; align-items:flex-start;';
    row.innerHTML = `
        <div class="form-group" style="flex:1; margin-bottom:0;">
            <input type="text" class="form-input rd-env-key" placeholder="KEY"
                   value="${esc(key)}" spellcheck="false" autocomplete="off">
            <p class="form-help rd-env-key-error" style="display:none; color:#ef4444;"></p>
        </div>
        <div class="form-group" style="flex:2; margin-bottom:0;">
            <input type="text" class="form-input rd-env-value" placeholder="value"
                   value="${esc(value)}" spellcheck="false" autocomplete="off">
        </div>
        <button class="btn btn-sm btn-secondary" type="button" title="Remove"
                style="flex:0 0 auto;">&times;</button>`;
    row.querySelector('button').onclick = () => { row.remove(); validateForm(); };
    row.querySelectorAll('input').forEach(i => i.addEventListener('input', validateForm));
    list.appendChild(row);
}

function validateEnvRows() {
    const reserved = _platformKeys || new Set(CORE_RESERVED);
    let ok = true;
    const seen = new Set();
    document.querySelectorAll('.rd-env-row').forEach(row => {
        const keyInput = row.querySelector('.rd-env-key');
        const errEl = row.querySelector('.rd-env-key-error');
        const key = keyInput.value.trim();
        let msg = '';
        if (key) {
            if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(key)) {
                msg = 'Letters, digits and underscores only; cannot start with a digit.';
            } else if (reserved.has(key.toLowerCase())) {
                msg = `${key} is set by the platform and would be silently overwritten — pick another name.`;
            } else if (key.toUpperCase() === 'PORT') {
                msg = 'PORT is set automatically to your app port.';
            } else if (seen.has(key.toLowerCase())) {
                msg = 'Duplicate key.';
            }
            seen.add(key.toLowerCase());
        }
        errEl.textContent = msg;
        errEl.style.display = msg ? '' : 'none';
        if (msg) ok = false;
    });
    return ok;
}

/**
 * Parse pasted .env content into rows. Handles comments, blanks,
 * `export ` prefixes, and single/double surrounding quotes. This one
 * button is most of the migration UX: `heroku config -s`, paste, done.
 */
function parseDotEnv(text) {
    const entries = [];
    for (let line of text.split(/\r?\n/)) {
        line = line.trim();
        if (!line || line.startsWith('#')) continue;
        if (line.startsWith('export ')) line = line.slice(7).trim();
        const eq = line.indexOf('=');
        if (eq <= 0) continue;
        const key = line.slice(0, eq).trim();
        let value = line.slice(eq + 1).trim();
        if ((value.startsWith('"') && value.endsWith('"')) ||
            (value.startsWith("'") && value.endsWith("'"))) {
            value = value.slice(1, -1);
        }
        entries.push([key, value]);
    }
    return entries;
}

function pasteEnvClicked() {
    const area = $('rd-env-paste-area');
    const wrap = $('rd-env-paste-wrap');
    if (wrap.style.display === 'none') { wrap.style.display = ''; area.focus(); return; }
    const entries = parseDotEnv(area.value);
    // Merge into existing rows: same key updates in place.
    const existing = {};
    document.querySelectorAll('.rd-env-row').forEach(row => {
        const k = row.querySelector('.rd-env-key').value.trim();
        if (k) existing[k] = row;
    });
    for (const [k, v] of entries) {
        if (existing[k]) existing[k].querySelector('.rd-env-value').value = v;
        else addEnvRow(k, v);
    }
    area.value = '';
    wrap.style.display = 'none';
    validateForm();
    showToast(`${entries.length} variable${entries.length === 1 ? '' : 's'} added`, 'success');
}

function collectEnvVars() {
    const out = {};
    document.querySelectorAll('.rd-env-row').forEach(row => {
        const k = row.querySelector('.rd-env-key').value.trim();
        const v = row.querySelector('.rd-env-value').value;
        if (k) out[k] = v;
    });
    return out;
}

// ─── open / close ────────────────────────────────────────────────────────

export async function openRepoDeployModal() {
    const modal = $('repo-deploy-modal');
    if (!modal) {
        console.error('[Repo Deploy] #repo-deploy-modal not found in the page — is the modal markup in index.html?');
        return;
    }
    // Wire (or re-verify wiring) at open: the modal must work regardless
    // of module load order or whether any bootstrap code called
    // initRepoDeploy(). Idempotent — safe on every open.
    initRepoDeploy();

    // Reset to a clean slate every open.
    ['rd-vm-name', 'rd-source-url', 'rd-source-ref', 'rd-deploy-key'].forEach(id => { if ($(id)) $(id).value = ''; });
    $('rd-app-port').value = '8080';
    $('rd-database').value = 'none';
    $('rd-private-toggle').checked = false;
    $('rd-private-section').style.display = 'none';
    $('rd-env-list').innerHTML = '';
    $('rd-env-paste-wrap').style.display = 'none';
    $('rd-advanced-body').style.display = 'none';
    setFieldError('rd-source-url', '');
    setFieldError('rd-app-port', '');
    setFieldError('rd-deploy-key', '');

    if (window.openStaticModal) window.openStaticModal(modal);
    else modal.classList.add('active');
    document.body.style.overflow = 'hidden';

    // Warm caches; prefill specs from the template's RecommendedSpec so
    // the advanced section starts honest (the server merges MinimumSpec
    // regardless — the form cannot under-provision).
    getReservedNames();
    try {
        _template = _template || await resolveTemplate(TEMPLATE_SLUG);
        const rec = _template.recommendedSpec || _template.RecommendedSpec;
        if (rec) {
            const cpu = rec.virtualCpuCores ?? rec.VirtualCpuCores;
            const mem = rec.memoryBytes ?? rec.MemoryBytes;
            const disk = rec.diskBytes ?? rec.DiskBytes;
            if (cpu) $('rd-cpu').value = cpu;
            if (mem) $('rd-memory').value = Math.round(Number(mem) / (1024 * 1024));
            if (disk) $('rd-disk').value = Math.round(Number(disk) / (1024 * 1024 * 1024));
        }
    } catch (e) {
        console.error('[Repo Deploy] Could not resolve template', e);
        showToast('The Deploy from Repository template is not available. ' +
            'Check that the orchestrator seeded platform-repo-deploy.', 'error');
        closeRepoDeployModal();
        return;
    }

    validateForm();
    $('rd-source-url').focus();
}

export function closeRepoDeployModal() {
    const modal = $('repo-deploy-modal');
    if (!modal) return;
    if (window.closeStaticModal) window.closeStaticModal(modal);
    else modal.classList.remove('active');
    document.body.style.overflow = '';
}

function togglePrivate() {
    $('rd-private-section').style.display = $('rd-private-toggle').checked ? '' : 'none';
    validateForm();
}

function toggleAdvanced() {
    const body = $('rd-advanced-body');
    const icon = $('rd-advanced-icon');
    const open = body.style.display === 'none';
    body.style.display = open ? '' : 'none';
    if (icon) icon.textContent = open ? '▼' : '▶';
}

// ─── submit ──────────────────────────────────────────────────────────────

async function deployClicked() {
    await getReservedNames();          // ensure the real list before final check
    if (!validateForm()) return;

    const vmName = $('rd-vm-name').value.trim();
    const sourceUrl = $('rd-source-url').value.trim();
    const sourceRef = $('rd-source-ref').value.trim() || 'HEAD';
    const appPort = parseInt($('rd-app-port').value, 10);
    const database = $('rd-database').value;
    const deployKey = $('rd-private-toggle').checked ? $('rd-deploy-key').value.trim() : '';

    // The injection boundary: shell-quoted, then base64. After this
    // point no user byte is parsed by anything but provision.sh's
    // dot-source of a root-only file on the user's own VM.
    const conf = [
        `SOURCE_URL=${shq(sourceUrl)}`,
        `SOURCE_REF=${shq(sourceRef)}`,
        `APP_PORT=${shq(appPort)}`,
        `DATABASE=${shq(database)}`,
        '',
    ].join('\n');

    const envVars = collectEnvVars();
    const envFile = Object.entries(envVars)
        // docker --env-file is line-based: strip newlines rather than
        // letting one value smuggle a second variable.
        .map(([k, v]) => `${k}=${String(v).replace(/\r?\n/g, ' ')}`)
        .join('\n') + '\n';

    const environmentVariables = {
        DEPLOY_CONF_B64: b64(conf),
        APP_ENV_B64: b64(envFile),
    };
    if (deployKey) {
        environmentVariables.DEPLOY_KEY_B64 = b64(deployKey.endsWith('\n') ? deployKey : deployKey + '\n');
    }

    const customSpec = {
        virtualCpuCores: parseInt($('rd-cpu').value, 10) || 2,
        memoryBytes: (parseInt($('rd-memory').value, 10) || 4096) * 1024 * 1024,
        diskBytes: (parseInt($('rd-disk').value, 10) || 25) * 1024 * 1024 * 1024,
        imageId: 'ubuntu-22.04',
        gpuMode: 0,
        requiresGpu: false,
        qualityTier: parseInt($('rd-quality-tier')?.value ?? '1', 10),
        bandwidthTier: parseInt($('rd-bandwidth-tier')?.value ?? '3', 10),
        replicationFactor: parseInt($('rd-replication-factor')?.value ?? '0', 10),
    };

    const btn = $('rd-deploy-btn');
    btn.disabled = true;
    btn.textContent = 'Deploying…';
    try {
        const templateId = _template._id || _template.id;
        const result = await submitTemplateDeploy(templateId, {
            vmName, environmentVariables, customSpec,
        });
        closeRepoDeployModal();
        await afterDeploySuccess(result, vmName, {
            toastMessage: `"${vmName}" is deploying — open your VM's URL to watch the build.`,
        });
    } catch (error) {
        console.error('[Repo Deploy] Deployment failed:', error);
        showToast(error.message || 'Deployment failed', 'error');
    } finally {
        btn.disabled = false;
        btn.textContent = 'Deploy';
    }
}

// ─── wiring ──────────────────────────────────────────────────────────────

let _wired = false;
export function initRepoDeploy() {
    if (_wired) return;
    if (!$('repo-deploy-modal')) return;   // markup not parsed yet — openRepoDeployModal retries
    if (document.querySelectorAll('#repo-deploy-modal').length > 1) {
        console.warn('[Repo Deploy] Duplicate #repo-deploy-modal in the page — remove one. getElementById only ever sees the first, so the visible copy may be inert.');
    }

    // Fail loudly, not optionally: a missing element here is an
    // integration bug, and silent ?. chaining is how the last one hid.
    const wire = (id, event, handler) => {
        const el = $(id);
        if (!el) { console.error(`[Repo Deploy] #${id} missing — modal markup out of date?`); return; }
        el.addEventListener(event, handler);
    };
    wire('rd-private-toggle', 'change', togglePrivate);
    wire('rd-advanced-toggle', 'click', toggleAdvanced);
    wire('rd-env-add', 'click', () => addEnvRow());
    wire('rd-env-paste-btn', 'click', pasteEnvClicked);
    wire('rd-deploy-btn', 'click', deployClicked);
    ['rd-vm-name', 'rd-source-url', 'rd-source-ref', 'rd-app-port', 'rd-deploy-key']
        .forEach(id => wire(id, 'input', validateForm));

    _wired = true;
}

window.repoDeploy = { openRepoDeployModal, closeRepoDeployModal, initRepoDeploy };

// Self-initialize on load — index.html includes this file as a module,
// so no app.js changes are needed.
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', initRepoDeploy);
} else {
    initRepoDeploy();
}