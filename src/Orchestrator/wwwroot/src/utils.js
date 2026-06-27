// ============================================================================
// Shared utilities
// HTML escaping, attribute escaping, URL sanitization, toast, enum mappings,
// tier tables, star rendering. Single source of truth — import from here.
// ============================================================================

// ── HTML escaping ───────────────────────────────────────────────────────────

const HTML_ESCAPE_MAP = {
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;',
    '`': '&#96;'
};

export function escapeHtml(text) {
    if (text === null || text === undefined) return '';
    return String(text).replace(/[&<>"'`]/g, ch => HTML_ESCAPE_MAP[ch]);
}

// JS string literal escape (for embedding inside an attribute like onclick="fn('...')").
// Prefer event delegation + data attributes instead — but when inline is unavoidable,
// use this on every interpolated value.
export function escapeJs(text) {
    if (text === null || text === undefined) return '';
    return String(text)
        .replace(/\\/g, '\\\\')
        .replace(/'/g, "\\'")
        .replace(/"/g, '\\"')
        .replace(/\n/g, '\\n')
        .replace(/\r/g, '\\r')
        .replace(/</g, '\\u003c')
        .replace(/>/g, '\\u003e')
        .replace(/&/g, '\\u0026');
}

// Sanitize a URL for use in href/src attributes. Returns '#' for disallowed schemes
// (javascript:, data:, vbscript:, file:). Allows http(s), mailto, and relative URLs.
export function sanitizeUrl(url) {
    if (!url || typeof url !== 'string') return '#';
    const trimmed = url.trim();
    if (!trimmed) return '#';
    // Relative URL
    if (/^[/.?#]/.test(trimmed)) return trimmed;
    // eslint-disable-next-line no-control-regex
    const cleaned = trimmed.replace(/[\u0000-\u0020]/g, '');
    if (/^(https?:|mailto:)/i.test(cleaned)) return cleaned;
    return '#';
}

// ── Toast ───────────────────────────────────────────────────────────────────
// Canonical signature: showToast(message, type). Other call shapes are tolerated.

export function showToast(message, type = 'info') {
    // Tolerate legacy reversed-arg callers: showToast('error', 'msg')
    if (typeof message === 'string' && ['info', 'success', 'error', 'warning'].includes(message)
        && typeof type === 'string' && !['info', 'success', 'error', 'warning'].includes(type)) {
        [message, type] = [type, message];
    }

    const container = document.getElementById('toast-container');
    if (!container) {
        console.log(`[Toast ${type}] ${message}`);
        return;
    }

    const toast = document.createElement('div');
    toast.className = `toast toast-${type}`;
    toast.setAttribute('role', type === 'error' ? 'alert' : 'status');
    toast.setAttribute('aria-live', type === 'error' ? 'assertive' : 'polite');
    toast.textContent = message;

    container.appendChild(toast);
    setTimeout(() => toast.classList.add('show'), 10);
    setTimeout(() => {
        toast.classList.remove('show');
        setTimeout(() => toast.remove(), 300);
    }, 3000);
}

// ── Enum mappings (C# enums serialize as integers or strings depending on settings) ──

export const VISIBILITY_TO_INT = { Public: 0, Private: 1 };
export const VISIBILITY_TO_STR = { 0: 'Public', 1: 'Private', Public: 'Public', Private: 'Private' };

export const PRICING_TO_INT = { Free: 0, PerDeploy: 1 };
export const PRICING_TO_STR = { 0: 'Free', 1: 'PerDeploy', Free: 'Free', PerDeploy: 'PerDeploy' };

export const STATUS_TO_STR = {
    0: 'Draft', 1: 'Published', 2: 'Archived', 3: 'Pending Review', 4: 'Rejected',
    Draft: 'Draft', Published: 'Published', Archived: 'Archived',
    PendingReview: 'Pending Review', Rejected: 'Rejected'
};

export const QUALITY_TIER_TO_INT = { Guaranteed: 0, Standard: 1, Balanced: 2, Burstable: 3 };
export const QUALITY_TIER_TO_STR = {
    0: 'Guaranteed', 1: 'Standard', 2: 'Balanced', 3: 'Burstable',
    Guaranteed: 'Guaranteed', Standard: 'Standard', Balanced: 'Balanced', Burstable: 'Burstable'
};

export const BANDWIDTH_TIER_TO_INT = { Basic: 0, Standard: 1, Performance: 2, Unmetered: 3 };
export const BANDWIDTH_TIER_TO_STR = {
    0: 'Basic', 1: 'Standard', 2: 'Performance', 3: 'Unmetered',
    Basic: 'Basic', Standard: 'Standard', Performance: 'Performance', Unmetered: 'Unmetered'
};

// True if a template pricing value (int or string) represents PerDeploy.
export function isPerDeployPricing(pricingModel) {
    if (pricingModel === null || pricingModel === undefined) return false;
    return pricingModel === 1 || pricingModel === 'PerDeploy' || PRICING_TO_INT[pricingModel] === 1;
}

// ── Tier tables ─────────────────────────────────────────────────────────────

export const QUALITY_TIERS = {
    0: { name: 'Guaranteed', pointsPerVCpu: 8, priceMultiplier: 2.5, description: 'Dedicated resources, guaranteed 1:1 CPU performance' },
    1: { name: 'Standard', pointsPerVCpu: 4, priceMultiplier: 1.0, description: 'Balanced performance and cost, 2:1 CPU overcommit' },
    2: { name: 'Balanced', pointsPerVCpu: 2, priceMultiplier: 0.6, description: 'Cost-optimized for consistent workloads' },
    3: { name: 'Burstable', pointsPerVCpu: 1, priceMultiplier: 0.4, description: 'Aggressive overcommit, lowest cost, variable performance' }
};

export const BANDWIDTH_TIERS = {
    0: { name: 'Basic', speed: '10 Mbps', burst: '20 Mbps', hourlyRate: 0.002, description: 'Light browsing and text-based workloads' },
    1: { name: 'Standard', speed: '50 Mbps', burst: '100 Mbps', hourlyRate: 0.008, description: 'General browsing, video calls, moderate streaming' },
    2: { name: 'Performance', speed: '200 Mbps', burst: '400 Mbps', hourlyRate: 0.020, description: 'HD video streaming, large downloads, data-intensive tasks' },
    3: { name: 'Unmetered', speed: 'No cap', burst: 'No cap', hourlyRate: 0.040, description: 'No artificial bandwidth cap. Limited only by host NIC.' }
};

// ── Markdown rendering ──────────────────────────────────────────────────────
// Regex-based subset: headings, bold, code blocks, inline code, line breaks.

export function renderMarkdown(markdown) {
    if (!markdown) return '';
    let html = escapeHtml(markdown);
    html = html.replace(/^### (.+)$/gm, '<h3>$1</h3>');
    html = html.replace(/^## (.+)$/gm, '<h2>$1</h2>');
    html = html.replace(/^# (.+)$/gm, '<h1>$1</h1>');
    html = html.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    html = html.replace(/```([\s\S]+?)```/g, '<pre><code>$1</code></pre>');
    html = html.replace(/`(.+?)`/g, '<code>$1</code>');
    html = html.replace(/\n/g, '<br>');
    return html;
}

// ── Star rendering ──────────────────────────────────────────────────────────

export function renderStars(rating, max = 5) {
    const r = Math.max(0, Math.min(max, Number(rating) || 0));
    const full = Math.round(r);
    return '★'.repeat(full) + '☆'.repeat(max - full);
}

// ── IPv4 validation ─────────────────────────────────────────────────────────

export function isValidIp(ip) {
    if (!ip || typeof ip !== 'string') return false;
    const ipv4Regex = /^(\d{1,3}\.){3}\d{1,3}$/;
    if (!ipv4Regex.test(ip)) return false;
    return ip.split('.').every(octet => {
        const num = parseInt(octet, 10);
        return num >= 0 && num <= 255;
    });
}

// ── Modal accessibility ─────────────────────────────────────────────────────
// Makes an overlay modal accessible: aria attributes, Escape to close,
// click-on-backdrop to close, focus trap, and focus restore on close.
// Returns a cleanup function. Pass closeFn that performs the actual close.

const FOCUSABLE_SELECTOR = [
    'a[href]', 'button:not([disabled])', 'textarea:not([disabled])',
    'input:not([disabled]):not([type="hidden"])', 'select:not([disabled])',
    '[tabindex]:not([tabindex="-1"])'
].join(',');

export function makeModalAccessible(modal, closeFn, { labelledBy } = {}) {
    if (!modal) return () => {};
    modal.setAttribute('role', 'dialog');
    modal.setAttribute('aria-modal', 'true');
    if (labelledBy) modal.setAttribute('aria-labelledby', labelledBy);

    const previouslyFocused = document.activeElement;
    const focusables = () => Array.from(modal.querySelectorAll(FOCUSABLE_SELECTOR))
        .filter(el => el.offsetParent !== null);

    // Initial focus
    setTimeout(() => {
        const first = focusables()[0];
        if (first) first.focus();
        else modal.setAttribute('tabindex', '-1'), modal.focus();
    }, 0);

    const onKeyDown = (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            closeFn();
            return;
        }
        if (e.key !== 'Tab') return;
        const items = focusables();
        if (items.length === 0) {
            e.preventDefault();
            return;
        }
        const first = items[0];
        const last = items[items.length - 1];
        if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
        }
    };

    const onBackdropClick = (e) => {
        if (e.target === modal) closeFn();
    };

    document.addEventListener('keydown', onKeyDown);
    modal.addEventListener('click', onBackdropClick);

    return function cleanup() {
        document.removeEventListener('keydown', onKeyDown);
        modal.removeEventListener('click', onBackdropClick);
        if (previouslyFocused && typeof previouslyFocused.focus === 'function') {
            try { previouslyFocused.focus(); } catch { /* element gone */ }
        }
    };
}
