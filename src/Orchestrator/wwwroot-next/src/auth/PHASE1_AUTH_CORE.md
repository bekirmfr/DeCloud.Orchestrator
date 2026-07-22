# Phase 1 — the frozen auth core

The subtle, security-load-bearing logic the whole app sits on. Built and **frozen
behind tests before any page depends on it** (DESIGN §4/§5, migration step 2).
This is a *skeleton*: the pure, fully-testable pieces are implemented; the pieces
that need live AppKit/ethers/backend are typed stubs with precise TODOs.

## Files → design sections

| File | What it is | Status | Spec |
|---|---|---|---|
| `types.ts` | WalletState, SessionState, DerivedStatus | ✅ done | §4 |
| `deriveStatus.ts` | **pure** (wallet, session, chain) → status | ✅ done | §4 |
| `sessionMachine.ts` | session reducer + tri-state `performRefresh` | ✅ done | §4/§5 |
| `tokenStore.ts` | access token (memory + mirror); refresh cookie untouched | ✅ done | §10 |
| `../api/errors.ts` | the three failure kinds (cancel/uncertain/definitive) | ✅ done | §5 |
| `../api/client.ts` | `api()` — envelope unwrap + 401 refresh-retry-once | ✅ done* | §5, §6.2 |
| `siwe.ts` | nonce→sign→verify; sign-out | �stub | §4 (port `siwe-config.js`) |
| `walletCrypto.ts` | AES-GCM, wallet-derived key | �stub | §10 (port `wallet-crypto.js` **verbatim**) |
| `walletState.ts` | AppKit/ethers → WalletState adapter | �stub | §4/§6.10 |
| `AuthProvider.tsx` | React seam; wires it together; owns side effects | �stub | §4 |

`*` `api/client.ts` is complete but assumes an `ApiResponse<T>` shape — confirm it
against the generated `src/api/schema.d.ts` (`npm run gen:api`) and the real envelope.

## Tests (the spec, made runnable) — `src/auth/__tests__/`
- `deriveStatus.test.ts` — the full §4 truth table (13 cases). ✅ runnable now.
- `sessionMachine.test.ts` — reducer arms + tri-state refresh. ✅ runnable now.
- `client.test.ts` — envelope unwrap, 401 retry-once, the three failure kinds. ✅ runnable now.
- `walletCrypto.test.ts` — round-trip + auth-failure + legacy byte-compat. `it.todo` until the port lands.

## Build order (recommended)
1. **Run the pure tests first** — they pass against what's here and lock the contract:
   `npm run test` → deriveStatus + sessionMachine + client green.
2. **Port `walletCrypto.ts` verbatim**, fill its test (round-trip + tamper + legacy compat).
3. **Port `siwe.ts`** against the real auth endpoints; confirm `buildSiweMessage` matches
   the backend byte-for-byte (else every verify fails).
4. **Implement `walletState.ts`** over AppKit; clean up every subscription (§6.10).
5. **Wire `AuthProvider.tsx`** per its header steps; add effect tests for the side
   effects (disconnect→signOut, account-switch→re-auth, restore-on-mount).
6. Only then does Phase 2 (the shell + first migrated page) consume `useAuth()`.

## Add these dev deps (test-first)
```
npm i -D vitest jsdom @testing-library/react @testing-library/jest-dom
```
Add to `package.json` scripts:
```
"test": "vitest run",
"test:watch": "vitest"
```
(`vitest.config.ts` is already in the scaffold root — jsdom env.)

## The invariants these tests exist to protect
- **Tri-state refresh**: `true` re-arms, `false` expires, **`null` keeps last-known** (never destroy state on unverifiable evidence).
- **Fail closed on identity**: wallet address ≠ session address → `ADDRESS_MISMATCH`, re-auth as the new address; B never operates A.
- **Cancel ≠ error**: a rejected signature (4001 / ACTION_REJECTED) is a `cancel`, handled quietly.
- **Refresh token is httpOnly**: JS never reads/writes it; it rides via `credentials:'include'`.
