// ============================================================================
// VM and service status helpers
// Pure mapping + rendering helpers for VM/service state.
// ============================================================================

import { escapeHtml } from './utils.js';

const VM_STATUS_CLASS = {
    0: 'pending', 1: 'scheduling', 2: 'provisioning',
    3: 'running', 4: 'stopping', 5: 'stopped',
    6: 'deleting', 7: 'migrating', 8: 'error', 9: 'deleted'
};

const VM_STATUS_TEXT = {
    0: 'Pending', 1: 'Scheduling', 2: 'Provisioning',
    3: 'Running', 4: 'Stopping', 5: 'Stopped',
    6: 'Deleting', 7: 'Migrating', 8: 'Error', 9: 'Deleted'
};

export function getStatusClass(status) {
    return VM_STATUS_CLASS[status] || 'unknown';
}

export function getStatusText(status) {
    return VM_STATUS_TEXT[status] || 'Unknown';
}

const SERVICE_STATUS_INT = { 0: 'pending', 1: 'checking', 2: 'ready', 3: 'timedout', 4: 'failed' };

export function normalizeServiceStatus(status) {
    if (status == null) return 'pending';
    if (typeof status === 'number') return SERVICE_STATUS_INT[status] || 'pending';
    return String(status).toLowerCase();
}

export function getServiceStatusClass(status) {
    switch (normalizeServiceStatus(status)) {
        case 'ready': return 'svc-ready';
        case 'checking': return 'svc-checking';
        case 'pending': return 'svc-pending';
        case 'timedout': return 'svc-timedout';
        case 'failed': return 'svc-failed';
        default: return 'svc-pending';
    }
}

export function getServiceStatusIcon(status) {
    switch (normalizeServiceStatus(status)) {
        case 'ready': return '<span class="svc-icon svc-ready">&#x2713;</span>';
        case 'checking': return '<span class="svc-icon svc-checking">&#x25CF;</span>';
        case 'pending': return '<span class="svc-icon svc-pending">&#x25CB;</span>';
        case 'timedout': return '<span class="svc-icon svc-timedout">&#x26A0;</span>';
        case 'failed': return '<span class="svc-icon svc-failed">&#x2717;</span>';
        default: return '<span class="svc-icon svc-pending">&#x25CB;</span>';
    }
}

export function renderServiceReadiness(services, vmStatus) {
    if (!services || services.length === 0 || vmStatus !== 3) return '';

    const readyCount = services.filter(s => normalizeServiceStatus(s.status) === 'ready').length;
    const total = services.length;
    const allReady = readyCount === total;

    const summaryClass = allReady ? 'svc-summary-ready' : 'svc-summary-partial';
    const summaryText = allReady ? 'All services ready' : `${readyCount}/${total} ready`;

    const serviceItems = services.map(s => {
        const name = s.name || 'Unknown';
        const port = s.port ? `:${s.port}` : '';
        const label = name === 'System' ? 'System' : `${name}${port}`;
        const statusClass = getServiceStatusClass(s.status);
        const icon = getServiceStatusIcon(s.status);
        const statusText = normalizeServiceStatus(s.status);
        const tooltip = s.statusMessage
            ? `${label}: ${statusText} — ${s.statusMessage}`
            : `${label}: ${statusText}`;

        return `<div class="svc-item ${statusClass}" title="${escapeHtml(tooltip)}">
            ${icon}
            <span class="svc-label">${escapeHtml(label)}</span>
        </div>`;
    }).join('');

    return `<div class="svc-readiness">
        <div class="svc-summary ${summaryClass}">${escapeHtml(summaryText)}</div>
        <div class="svc-list">${serviceItems}</div>
    </div>`;
}

export function renderServiceBadge(services, vmStatus) {
    if (!services || services.length === 0 || vmStatus !== 3) return '';

    const readyCount = services.filter(s => normalizeServiceStatus(s.status) === 'ready').length;
    const total = services.length;
    const allReady = readyCount === total;
    const hasFailure = services.some(s => {
        const st = normalizeServiceStatus(s.status);
        return st === 'failed' || st === 'timedout';
    });

    const badgeClass = allReady ? 'svc-badge-ready' : hasFailure ? 'svc-badge-warn' : 'svc-badge-progress';

    return `<span class="svc-badge ${badgeClass}" title="${escapeHtml(readyCount + '/' + total + ' services ready')}">${readyCount}/${total}</span>`;
}
