# Phase 2 — the shell + first migrated page (SSH Keys)

Prove the whole pipeline end-to-end on the lowest-stakes page, and retire the
first legacy page. Same test-first shape as Phase 1: the pure decision logic is
implemented + tested; the pieces needing live wiring are typed skeletons.

## Files → design sections

| File | What it is | Status | Spec |
|---|---|---|---|
| `app/resolveShellView.ts` | **pure** DerivedStatus → which surface (+ escrowBlocked) | ✅ done | §2/§4 |
| `app/guards.ts` | **pure** `canAccessAdmin` (UX only; server enforces) | ✅ done | §3/§10 |
| `features/ssh-keys/AddKeyModal.ts` | **pure** `validateSshKey` (+ modal stub) | ✅ done* | §7 |
| `app/StatusGate.tsx` | renders connect / reauth / app from the gate | stub | §4 |
| `app/gates/*` | ConnectGate, ReauthGate, StaleBanner, NetworkBanner | stub | §4 |
| `app/AppShell.tsx` | sidebar + header + `<Outlet/>` | stub | §2/§3 |
| `app/routes.tsx` | React Router v7 tree; admin guard; SSH Keys route | stub | §3 |
| `features/ssh-keys/useSshKeys.ts` | TanStack Query list/create/delete | stub | §6.3 |
| `features/ssh-keys/SshKeysPage.tsx` | the first migrated page | stub | §7 |

`*` the validation rule is done + tested; the Radix modal itself is a stub.

## The gate mapping (resolveShellView)
| DerivedStatus | Surface | Banner | Escrow |
|---|---|---|---|
| READY | app | — | allowed |
| UNCERTAIN | app | stale | allowed (keep last-known) |
| WRONG_NETWORK | app | wrong-network | **blocked** |
| NEEDS_AUTH | reauth (sign in) | — | — |
| ADDRESS_MISMATCH | reauth (as new address, fail closed) | — | — |
| NEEDS_CONNECT | connect | — | — |

## Runnable tests now — `vitest run`
- `app/__tests__/resolveShellView.test.ts` (7) — every status → surface; only WRONG_NETWORK blocks escrow.
- `app/__tests__/guards.test.ts` (3) — admin predicate.
- `features/ssh-keys/__tests__/validateKey.test.ts` (4) — key validation.
Combined with Phase 1: **48 passing | 5 todo**.

## SSH Keys — parity checklist (verify each before deleting the legacy module)
- [ ] list keys: name, fingerprint, created date
- [ ] add key: name + public key; validation (name required, key format) — `validateSshKey`
- [ ] delete key: confirm
- [ ] empty / loading / error states (error.kind → message)

## Retirement mechanism (DESIGN §2 — the whole point of Phase 2)
Atomic, by URL:
1. Build SSH Keys at `/app/settings/ssh-keys` (this route), talking to the real API via `api()`.
2. Verify the parity checklist above.
3. **Swing the entry point**: change the *legacy* sidebar's "SSH Keys" link to point at `/app/settings/ssh-keys`.
4. **Delete** the legacy SSH-keys page div + its module (`loadSSHKeys`/`openAddSSHKeyModal`) + any `window.*` it owned — in the SAME change. No dead code beside the new file.

This first retirement proves the mechanism on something that can't hurt anyone.
Note: the nav-of-record stays the *legacy* app until the dashboard migrates (Phase 3);
here, SSH Keys just becomes a new URL the legacy sidebar links to.

## Add these deps (Phase 2 stack — locked in DESIGN §6.0)
```
npm i react-router-dom @tanstack/react-query @radix-ui/react-dialog
```
**Honest note:** the shell stubs import these three packages, so `tsc` / `npm run
build` will error on the missing imports UNTIL you install them. The pure Vitest
suites run regardless (`vitest run`) — their import graph only touches the pure
modules. So: `vitest run` proves the Phase 2 logic now; install the deps + wire
the stubs to make the shell build and mount.

## Build order
1. `vitest run` → the 14 new pure tests pass (gate + guard + validation locked).
2. Install the three deps above.
3. Wire `useApi()` (expose `api` from AuthProvider once Phase 1 is complete).
4. Fill `StatusGate` → gates → `AppShell` → mount `RouterProvider` in `main.tsx`
   (replacing the Phase 0 `App.tsx` proof).
5. Build `SshKeysPage` + `AddKeyModal` against the real endpoints; pass the parity checklist.
6. Retire the legacy SSH-keys page (steps above). First page migrated. ✅
```

Phase 2 depends on Phase 1's `AuthProvider` being wired (the shell reads `useAuth()`).
Finish the Phase 1 ports first, then this.
