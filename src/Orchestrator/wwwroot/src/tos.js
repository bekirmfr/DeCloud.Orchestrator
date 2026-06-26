// ============================================================================
// Terms of Service acceptance gate
// ============================================================================
//
// Backend contract:
//   GET  /api/tos          → { version, hash, text }   (public)
//   GET  /api/tos/status   → { version, hash, accepted } (auth)
//   POST /api/tos/accept   → { version, hash, timestamp, signature } (auth)
//
// The signed message MUST match TosService.BuildAcceptanceMessage byte-for-byte:
//   "DeCloud Terms of Service Acceptance\nWallet: {wallet}\nVersion: {v}\nHash: {h}\nTimestamp: {ts}"
// The server reconstructs this message using the CHECKSUM-normalized wallet, so we
// sign with signer.getAddress() (EIP-55) — its casing matches the server's
// Nethereum normalization. Using a differently-cased address would make the
// server-side signature reconstruction fail with INVALID_SIGNATURE.

import { escapeHtml, showToast } from './utils.js';

const MODAL_ID = 'tos-gate-modal';

/**
 * Ensure the connected wallet has accepted the current ToS.
 * Blocking: resolves true once accepted (now or already), false if the user
 * declines. Throws if the status/document calls fail (caller decides what to do).
 *
 * @param {{ api: Function, getSigner: Function }} ctx
 * @returns {Promise<boolean>}
 */
export async function ensureTosAccepted({ api, getSigner }) {
    // 1. Already accepted the current version?
    const statusRes = await api('/api/tos/status');
    const status = await statusRes.json();
    if (status?.data?.accepted) return true;

    // 2. Load the full document for display.
    const docRes = await api('/api/tos');
    const doc = (await docRes.json())?.data; // { version, hash, text }
    if (!doc?.version || !doc?.hash) {
        throw new Error('Terms of Service document is unavailable.');
    }

    // 3. Present the blocking gate and await the user's decision.
    return showTosGate(doc, getSigner, api);
}

function showTosGate(doc, getSigner, api) {
    return new Promise((resolve) => {
        document.getElementById(MODAL_ID)?.remove(); // rebuild with current text
        const modal = buildModal(doc);
        document.body.appendChild(modal);

        const acceptBtn = modal.querySelector('#tos-accept-btn');
        const declineBtn = modal.querySelector('#tos-decline-btn');
        const errEl = modal.querySelector('#tos-error');

        const close = () => {
            modal.classList.remove('active');
            modal.remove();
        };

        declineBtn.onclick = () => { close(); resolve(false); };

        acceptBtn.onclick = async () => {
            errEl.style.display = 'none';
            acceptBtn.disabled = true;
            declineBtn.disabled = true;
            const original = acceptBtn.textContent;
            acceptBtn.textContent = 'Sign in wallet…';
            try {
                const signer = getSigner();
                if (!signer) throw new Error('Wallet not connected');

                // EIP-55 checksum address — must match the server's normalized wallet.
                const wallet = await signer.getAddress();
                const timestamp = Math.floor(Date.now() / 1000);
                const message =
                    'DeCloud Terms of Service Acceptance\n' +
                    `Wallet: ${wallet}\n` +
                    `Version: ${doc.version}\n` +
                    `Hash: ${doc.hash}\n` +
                    `Timestamp: ${timestamp}`;

                const signature = await signer.signMessage(message);

                const res = await api('/api/tos/accept', {
                    method: 'POST',
                    body: JSON.stringify({
                        version: doc.version,
                        hash: doc.hash,
                        timestamp,
                        signature,
                    }),
                });
                const data = await res.json();

                if (res.ok && data?.success) {
                    showToast('Terms of Service accepted', 'success');
                    close();
                    resolve(true);
                    return;
                }
                throw new Error(data?.message || `Acceptance failed (${res.status})`);
            } catch (e) {
                console.error('[ToS] acceptance failed', e);
                const msg = (e.code === 'ACTION_REJECTED' || e.code === 4001)
                    ? 'Signature request rejected.'
                    : (e.message || 'Acceptance failed. Please try again.');
                errEl.textContent = msg;
                errEl.style.display = 'block';
                acceptBtn.disabled = false;
                declineBtn.disabled = false;
                acceptBtn.textContent = original;
            }
        };

        // Show directly (not via openStaticModal): this is a gate, so it must NOT
        // be dismissible by Esc or overlay click — those would hide the modal
        // without resolving the promise and strand the auth flow.
        modal.classList.add('active');
        acceptBtn.focus();
    });
}

function buildModal(doc) {
    const modal = document.createElement('div');
    modal.className = 'modal-overlay';
    modal.id = MODAL_ID;
    modal.innerHTML = `
        <div class="modal modal-large" role="dialog" aria-modal="true" aria-labelledby="tos-gate-title">
            <div class="modal-header">
                <h2 class="modal-title" id="tos-gate-title">Terms of Service</h2>
            </div>
            <div class="modal-body" style="max-height:60vh; overflow-y:auto;">
                <p class="form-help" style="margin-bottom:12px;">
                    Version ${escapeHtml(doc.version)} — please review and accept to continue.
                    Your acceptance is recorded as a signature from your wallet.
                </p>
                <div style="white-space:pre-wrap; font-size:13px; line-height:1.5;">${escapeHtml(doc.text || '')}</div>
                <p id="tos-error" style="display:none; color:var(--color-error,#e53e3e); font-size:0.85rem; margin-top:12px;"></p>
            </div>
            <div class="modal-footer">
                <button class="btn btn-secondary" id="tos-decline-btn">Decline</button>
                <button class="btn btn-primary" id="tos-accept-btn">Accept &amp; Sign</button>
            </div>
        </div>
    `;
    return modal;
}
