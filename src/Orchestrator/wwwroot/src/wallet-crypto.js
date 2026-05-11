// ============================================================================
// Wallet-derived password encryption (AES-256-GCM)
// Uses @noble/ciphers so it works on plain HTTP, not just HTTPS / localhost.
// The key is derived from a deterministic signature over a fixed message; the
// same wallet always produces the same key.
// ============================================================================

import { gcm } from '@noble/ciphers/aes';
import { randomBytes } from '@noble/ciphers/webcrypto';
import { sha256 } from '@noble/hashes/sha256';

const ENCRYPTION_MESSAGE = 'DeCloud VM Password Encryption Key v1';

let cachedEncryptionKey = null;

// Resolve the current ethers signer. app.js exposes one via window.ethersSigner().
function getSigner() {
    return window.ethersSigner ? window.ethersSigner() : null;
}

export async function getEncryptionKey() {
    if (cachedEncryptionKey) return cachedEncryptionKey;

    const signer = getSigner();
    if (!signer) throw new Error('Wallet not connected');

    const signature = await signer.signMessage(ENCRYPTION_MESSAGE);
    cachedEncryptionKey = sha256(new TextEncoder().encode(signature));
    return cachedEncryptionKey;
}

export async function encryptPassword(password) {
    const key = await getEncryptionKey();
    const nonce = randomBytes(12);
    const plaintext = new TextEncoder().encode(password);
    const cipher = gcm(key, nonce);
    const ciphertext = cipher.encrypt(plaintext);

    const combined = new Uint8Array(nonce.length + ciphertext.length);
    combined.set(nonce, 0);
    combined.set(ciphertext, nonce.length);
    return btoa(String.fromCharCode(...combined));
}

export async function decryptPassword(encryptedPassword) {
    const key = await getEncryptionKey();
    const combined = Uint8Array.from(atob(encryptedPassword), c => c.charCodeAt(0));
    const nonce = combined.slice(0, 12);
    const ciphertext = combined.slice(12);

    try {
        const cipher = gcm(key, nonce);
        const plaintext = cipher.decrypt(ciphertext);
        return new TextDecoder().decode(plaintext);
    } catch (error) {
        if (error.message?.includes('Invalid')) {
            throw new Error("Decryption failed: invalid key or corrupted data. Make sure you're using the same wallet.");
        }
        throw error;
    }
}

export function clearEncryptionKey() {
    cachedEncryptionKey = null;
}
