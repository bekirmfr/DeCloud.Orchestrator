# Deploy — migration notes & tracked debt

The React deploy flow (`/app/marketplace/:slug/deploy`) reuses two legacy flows via
the `window.*` bridge instead of reimplementing them in React. This is a **deliberate
v1 shortcut**, recorded here as an **OPEN ACTION** — not a silent dependency.

## Bridged legacy dependencies (v1)

| Concern | Legacy source | Global used | Why deferred |
|---|---|---|---|
| **ToS acceptance gate** | `app.js` → `handleDeployTosGate` | `window.handleDeployTosGate()` | Wallet-signature acceptance flow; complex, working. Porting to React is its own scope (signer + version check + modal). |
| **Fund deposit** | `payment.js` → deposit modal | `window.showDepositModal()` (or legacy deposit page link) | On-chain `escrow.deposit` via ethers; a full wallet/tx flow. Rebuilding in React is separate work. |

## The contract we depend on

- `window.handleDeployTosGate()` → `Promise<boolean>`. Returns `true` **only** when ToS
  was the deploy blocker and has now been accepted (so the caller retries **once**).
  Returns `false`/absent otherwise. Matches `deploy-submit.js`'s retry-once guard.
- Deposit is fire-and-forget UX: open the legacy top-up, user returns and retries deploy.

## Hidden coupling — READ BEFORE RETIRING THE LEGACY APP

The React deploy page **silently depends on the legacy bundle being loaded** for the two
flows above. If the legacy app (`app.js` / `payment.js`) is retired or those globals are
removed **before** native React replacements exist, deploy will break.

Mitigation in code: calls are `window.fn?.()` with an explicit **absence fallback** — if
the bridge is missing, the user sees a clear message ("ToS acceptance is unavailable —
please accept in the legacy app") rather than a silent failure. So breakage is loud, not
mysterious. But the real fix is replacement.

## OPEN ACTIONS (must close before legacy retirement)

- [ ] **Native React ToS gate** — port the acceptance + wallet-signature flow; drop
      `window.handleDeployTosGate`.
- [ ] **Native React deposit flow** — port the escrow deposit (ethers) into `/app`; drop
      the `window.showDepositModal` bridge.
- [ ] Once both are native: remove this bridge, remove the legacy ToS/deposit code from
      `app.js`/`payment.js`, and delete this section.

Until these are closed, **the legacy app cannot be fully retired** — the deploy page
holds a live dependency on it.
