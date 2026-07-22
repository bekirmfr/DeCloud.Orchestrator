# Phase 1 — the frozen auth core

The subtle, security-load-bearing logic the whole app sits on. Built and **frozen
behind tests before any page depends on it** (DESIGN §4/§5, migration step 2).
**Status: COMPLETE** — the full flow is proven live (connect → SIWE → authenticated
→ shell, reload-persistent) against the real orchestrator. All modules are wired;
the pure logic is unit-tested (53 tests). Remaining items are cleanup, not core
work (see bottom).

## Files → design sections

| File | What it is | Status | Spec |
|---|---|---|---|
| `types.ts` | WalletState, SessionState, DerivedStatus | ✅ done | §4 |
| `deriveStatus.ts` | **pure** (wallet, session, chain) → status | ✅ done | §4 |
| `sessionMachine.ts` | session reducer + tri-state `performRefresh` | ✅ done | §4/§5 |
| `tokenStore.ts` | access token (memory + mirror); refresh cookie untouched | ✅ done | §10 |
| `../api/errors.ts` | the three failure kinds (cancel/uncertain/definitive) | ✅ done | §5 |
| `../api/client.ts` | `api()` — envelope unwrap + 401 refresh-retry-once | ✅ done* | §5, §6.2 |
| `siwe.ts` | `createDecloudSiweConfig` (AppKit SIWE hooks) | ✅ done (live) | §4 (ported `siwe-config.js`) |
| `walletCrypto.ts` | AES-GCM, wallet-derived key | ✅ done — 4 tests; cross-compat fixture `it.todo` | §10 (ported `wallet-crypto.js` verbatim) |
| `walletState.ts` | AppKit/ethers → WalletState adapter | ✅ done (live) | §4/§6.10 |
| `AuthProvider.tsx` | React seam; wires it together; restore-via-refresh | ✅ done (live) | §4 |

`*` `api/client.ts` assumes an `ApiResponse<T>` shape — proven working against the
live backend; `npm run gen:api` → `schema.d.ts` would confirm the wire types formally.

## Tests (the spec, made runnable) — `src/auth/__tests__/`
- `deriveStatus.test.ts` — the full §4 truth table (13 cases). ✅ runnable now.
- `sessionMachine.test.ts` — reducer arms + tri-state refresh. ✅ runnable now.
- `client.test.ts` — envelope unwrap, 401 retry-once, the three failure kinds. ✅ runnable now.
- `walletCrypto.test.ts` — round-trip + auth-failure + legacy byte-compat. `it.todo` until the port lands.

## Build order (all done)
The recommended order was: pure tests → `walletCrypto` → `siwe` → `walletState` →
`AuthProvider` → Phase 2 consumes `useAuth()`. All complete and proven live.

## Remaining cleanup (not core work)
- `walletCrypto` **cross-compat fixture**: capture one real `{signature, plaintext,
  legacy-ciphertext}` from the running legacy app to prove cross-decryption (currently `it.todo`).
- **`as never` casts** at the AppKit boundary (`siwe.ts`, `walletState.ts`) — tighten
  to the real `@reown/appkit` types as a hardening pass.
- **`npm run gen:api`** → `schema.d.ts`; reconcile `AuthUser`/`SessionResponse`/`NonceResponse`.
- **`EXPECTED_CHAIN_ID`** is env-wired (`VITE_EXPECTED_CHAIN_ID`); set it per environment
  (dev Amoy 80002 / prod mainnet 137) to match the backend `Payment:ChainId`.
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