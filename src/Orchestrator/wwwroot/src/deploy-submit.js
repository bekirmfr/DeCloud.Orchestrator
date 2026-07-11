/**
 * deploy-submit.js — the ONE deploy submission path.
 *
 * Custom presentation, shared submission: any deploy form (the generic
 * template modal, the Deploy from Repo form, whatever comes next) builds
 * its own payload however it likes, then hands it here. Everything that
 * must be correct about deploying — the ToS-gate retry, the password
 * modal, error shapes, navigation — lives in this module exactly once.
 *
 * If you find yourself copying the fetch out of this file into a new
 * form, stop: that is the fork this module exists to prevent.
 *
 * Migration note: template-detail.js's deployFromTemplate() should be
 * reduced to (a) reading its own DOM and (b) calling submitTemplateDeploy.
 * See INTEGRATION.md.
 */

/**
 * Resolve a template by slug or id. The deploy endpoint takes ids only
 * (MarketplaceController.DeployTemplate → GetTemplateByIdAsync), while
 * GET /templates/{slugOrId} accepts both — so slugs get one extra GET.
 */
export async function resolveTemplate(slugOrId) {
    const response = await window.api(`/api/marketplace/templates/${encodeURIComponent(slugOrId)}`);
    if (!response.ok) {
        throw new Error(`Template '${slugOrId}' not found (HTTP ${response.status})`);
    }
    const data = await response.json();
    return data.data || data;
}

/**
 * Submit a template deploy. Returns { vmId, password, ... } on success,
 * throws with a user-facing message on failure.
 *
 * @param {string} templateId   The template's _id (not slug — resolve first).
 * @param {object} payload      { vmName, environmentVariables, customSpec }
 * @param {object} [opts]       { retried } internal ToS-gate recursion guard.
 */
export async function submitTemplateDeploy(templateId, payload, opts = {}) {
    const response = await window.api(`/api/marketplace/templates/${templateId}/deploy`, {
        method: 'POST',
        body: JSON.stringify({
            vmName: payload.vmName,
            environmentVariables: payload.environmentVariables || {},
            customSpec: payload.customSpec,
        }),
    });

    const text = await response.text();
    let data;
    try {
        data = text ? JSON.parse(text) : {};
    } catch {
        throw new Error(response.ok ? 'Invalid response from server' : `Server error (${response.status})`);
    }

    if (data.success || response.ok) {
        return data.data || data;
    }

    // ToS may be the blocker (acceptance lapses on a version bump).
    // Surface the gate and retry exactly once — after acceptance the
    // status check returns false, so this can't loop.
    if (!opts.retried && await window.handleDeployTosGate?.()) {
        return submitTemplateDeploy(templateId, payload, { retried: true });
    }

    throw new Error(data?.error?.message ?? data?.error ?? data?.message
        ?? `Failed to deploy (HTTP ${response.status})`);
}

/**
 * The shared post-success ritual: password modal (only for the
 * human-readable generated format — same sniff the generic form uses),
 * toast, navigate to the VM list.
 */
export async function afterDeploySuccess(result, vmName, { toastMessage } = {}) {
    const password = result.password;
    if (password && !password.includes('_') && password.includes('-')) {
        if (window.showPasswordModal) {
            await window.showPasswordModal(result.vmId, vmName, password);
        } else {
            window.showToast?.('warning', `VM created! Password: ${password} — please save it!`);
        }
    }

    window.showToast?.('success', toastMessage || `VM "${vmName}" is being deployed!`);
    setTimeout(() => window.showPage?.('virtual-machines'), 1000);
}
