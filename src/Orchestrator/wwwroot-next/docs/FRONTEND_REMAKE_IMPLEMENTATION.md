# DeCloud Frontend Remake — Implementation Plan & Journal

**Companion to:** `FRONTEND_REMAKE_DESIGN.md` (the *what/why*). This document is the *how/when/status*, and doubles as the **running implementation journal** (§ Journal, bottom).
**Approach:** Option A — **split by URL**. Old vanilla app and new React app run side-by-side, separated by path at the server; pages are retired one at a time as they're re-imported into the new design.
**Version:** 1.0 (plan established; no code written yet).

> **How to use this doc.** The plan (§1–§4) is stable scaffolding. The **Status dashboard** (§0) and the **Journal** (§8) are living — update them as work happens. Design rationale is *not* duplicated here; it lives in the design doc and is cited by section (e.g. "parity per DESIGN §7").

---

## 0. Status dashboard (living)

| Phase | Goal | Status | Owner | Started | Done |
|---|---|---|---|---|---|
| **Pre** | Backend serving split confirmed (§3) | ◑ Change 1 `Program.cs` delivered for sign-off; csproj target + new Vite project still needed (Phase 0) | Owner | 2026-07-21 | — |
| **0** | Scaffold new app beside old | ☑ Done — dev (`/app` via Vite) + Release/Production serving (`/app` from `dist-app`, `/` legacy) both proven | Owner | 2026-07-21 | 2026-07-21 |
| **0.5** | Visual direction → tokenize (Meridian) | ☑ Done — tokens (light+dark, AA-validated), fonts.css, reconciliation decided | — | 2026-07-21 | 2026-07-21 |
| **1** | Frozen core (session/identity + `api()`) | ☑ Done — full auth flow live (connect→SIWE→authenticated→shell), reload-persists; 53 tests | Owner | 2026-07-21 | 2026-07-22 |
| **2** | Shell + pipeline-proof page (SSH Keys) | ◑ Pure shell logic implemented + tested (14 pass); wire stubs + build SSH Keys + retire legacy remain | — | 2026-07-22 | — |
| **3** | The spine (dashboard → deploy → VM detail) | ☐ Not started | — | — | — |
| **4** | Landing (SSG) built & proven | ☐ Not started | — | — | — |
| **5** | Supporting paths (marketplace, my-templates, nodes, settings, admin) | ☐ Not started | — | — | — |
| **6** | Cutover: `/`-flip, delete monolith + `window.*` bridge | ☐ Not started | — | — | — |

Legend: ☐ Not started · ◐ In progress · ☑ Done · ⚠ Blocked

---

## 1. The approach, and the constraint that forced it

**Option A — split by URL.** The old app keeps serving its paths; the new React app owns `/app/*`; ASP.NET routes between them.

**Why not "run both in one shell" (Option B islands).** Two routers can't co-own the URL. The old app isn't really routed (`showPage()` toggles `.active` on divs in one `index.html`), while React Router *does* own the URL — so co-mounting means they fight over paths and the back button. Worse, the old modules are entangled with the global document: `custom-domains.js` registers a document-level click listener at module load, inter-module calls go through `window.api`/`window.showPage`/`window.templateDetail`, and `index.html` has inline `onclick="showPage(...)"`. Making those safely mount/unmount as islands is fiddly adapter work that gets thrown away at cutover. **Option A sidesteps all of it**: the old app stays untouched, the new app is built cleanly beside it, and the migration end-state *is* the target-state (app under `/app/*`, landing at `/`) — not a temporary scaffold.

**Coexistence rule (who owns which path):**
- Old monolith: `/` and its existing assets — **primary entry point during migration**.
- New app: `/app/*` — grows page by page.
- Untouched standalone entries: `/sign.html`, `/report.html` (ported later, or left as-is until then).
- The landing (`/`) flip is **deliberately late** (§ Phase 4/6): moving the old app off `/` mid-migration is disruptive, so `/` stays the old app until the spine exists and it makes sense to send new users to a landing.

---

## 2. The retirement mechanism ("retire each page as we import it")

A page is migrated **atomically** — at any instant it's served by the old monolith *or* by React, never half-and-half, decided by URL. The steps per page:

1. **Build** the page as a React route under `/app/...`, talking to the real API through the new typed `api()`.
2. **Verify** against the design-doc parity inventory (DESIGN §7) for that page — the specific behaviors that must survive.
3. **Flip the entry point:** swing the link that used to open the old page so it now points at the new `/app/...` URL. (Early phases: the *old* sidebar link. After the dashboard migrates: the *new* shell's sidebar link — see the nav-of-record handoff, §4.)
4. **Delete** the old page's `<div id="page-...">` from `index.html`, its module (e.g. `ssh-keys` logic), its inline handlers, and any `window.*` exports only it used.

**Definition of "retired":** new page passes parity; old entry point points at the new URL; old div + module + inline handlers deleted in the *same* change (no dead code left beside the new file — DESIGN principle: tidy workspace).

**The `window.*` bridge to dismantle across phases** (each retired page removes the ones it owned; the rest go at cutover): `window.api`, `showPage`, `ethersSigner`, `templateDetail`, `handleDeployTosGate`, `showPasswordModal`, `copyToClipboard`, `_cdVerify`/`_cdRemove`, `marketplaceTemplates`, `sanitizeVmName`/`validateVmName`.

---

## 3. Precondition — the one non-frontend dependency (confirm before Phase 0)

Option A needs ASP.NET to serve **two front-end builds by path**. This is a small, real change to the serving layer (`Program.cs` / static-file + SPA-fallback rules), and it's the only piece that isn't pure frontend. Confirm with whoever owns the backend serving before Phase 0. Three distinct asks, needed at different times:

1. **Phase 0:** serve the new build under `/app/*` (with SPA fallback to the new `index.html` for client routing), while `/` and existing assets continue to serve the old app. Debug: Vite dev server for `/app`, .NET for API, as today.
2. **Phase 3:** teach the *old* monolith to **open to a given page on load** (read `?page=x` or `#x` → call `showPage(x)`). A few lines in the old app's init. Needed so the new shell can deep-link back into un-migrated pages after the nav-of-record handoff.
3. **Phase 4/6:** the `/`-flip — route `/` (+ locale prefixes) to the SSG landing, move/retire the old app's entry, keep `/app/*` as the SPA. (DESIGN §6.12 two-surface serving.)

---

## 4. Phases

Each phase lists **goal · tasks · exit criteria · what's retired · risk**. Tasks cite real files/modules so the work is concrete, not generic.

### Phase 0 — Scaffold beside the old app
- **Goal:** new React+Vite+TS build coexisting with the vanilla app; nothing user-facing changes.
- **Tasks:** new Vite+React+TS project emitting to a dist path the old build doesn't touch, served under `/app`; toolchain (tsconfig, ESLint/Prettier, Vitest, Playwright skeleton, CI perf-budget stub); design-token file skeleton (CSS custom properties — DESIGN §6.4) so components have tokens from day one; typed API client generated from `/swagger/v1/swagger.json` as a CI step (DESIGN §6.2); a hello-world `/app` page to prove the serving split (Pre-req #1).
- **Exit criteria:** `/app` renders a React page served by ASP.NET in Release and via Vite in Debug; old app at `/` unchanged; type-gen + tests run in CI.
- **Retires:** nothing.
- **Risk:** low. Main risk is the serving split (Pre-req) — that's why it's confirmed first.

### Phase 0.5 — Visual direction → tokens
- **Goal:** turn the chosen visual identity into the real token layer everything else consumes, before components get built against placeholder styles.
- **Status:** direction **chosen — "Meridian"** (cool architectural light; iris `#332ED6` accent; Space Grotesk / Inter / JetBrains Mono; centerless-mesh signature). Token seeds in DESIGN §6.4.
- **Tasks:** encode the Meridian seeds into `design-tokens.css` as role-based tokens (reconcile/replace the `design-tokens.css` that already exists in the frontend); **derive the dark mode** from the same roles (R7) and contrast-validate both; self-host the three OFL fonts (no CDN dependency); tighten the mockup into a small component reference (buttons, cards, rows, status dots, the mesh) that Phase 2's shell reuses.
- **Exit criteria:** a tokenized light+dark theme that passes contrast in both modes; the hero + dashboard mockup rebuilt from tokens (not hardcoded hex); fonts self-hosted.
- **Retires:** nothing (foundation).
- **Risk:** low. Gates token finalization so components aren't built twice.
- **Sequencing:** overlaps Phase 1 (the frozen core is logic, not looks) — can run in parallel. Must land before Phase 2's shell.

### Phase 1 — The frozen core
- **Goal:** the DESIGN §4 session/identity machine + `api()` proven correct as a *tested library*, before any page depends on it. This is the riskiest logic in the project (subtle, silently broken in rewrites), so it's built and frozen first.
- **Tasks:**
  - `api()`: bearer + `credentials:'include'` + on-401 refresh-retry-once; **single `ApiResponse<T>` unwrap** (boundary now uniform, DESIGN §12-Q2); typed.
  - Session/identity machine (DESIGN §4): derived `READY | NEEDS_CONNECT | NEEDS_AUTH | WRONG_NETWORK | ADDRESS_MISMATCH | UNCERTAIN` from (wallet, session, network); tri-state refresh (`true`/`false`/`null`); `signOutOnDisconnect`; account-switch A→B → NEEDS_AUTH (fail closed); wrong-network gates fund/escrow only.
  - SIWE/AppKit: port `siwe-config.js` hooks (nonce/verify/getSession/signOut) to the AppKit React adapter; `onAuthenticated` → session store.
  - `wallet-crypto.js`: port **verbatim** behind a hook (AES-GCM, wallet-derived key, session cache).
- **Exit criteria:** state machine + `api()` unit-tested green; can connect / sign-in (SIWE) / restore / refresh / sign-out / handle disconnect / account-switch / wrong-network — driven by a **test harness, no page built**. Assert: refresh token never read by JS; access token in memory + localStorage mirror; the three failure kinds (cancel/uncertain/definitive, DESIGN §5) behave.
- **Retires:** nothing (parallel to old app).
- **Risk:** medium — this is the crown-jewel logic. Mitigation: freeze + tests before anything builds on it.

### Phase 2 — Shell + pipeline-proof page (SSH Keys)
- **Goal:** prove the *entire* pipeline end-to-end on the lowest-stakes page before touching the spine.
- **Tasks:** `/app` shell (sidebar, header with balance placeholder, the derived-status gate → connect / re-auth / stale-banner; Radix + tokens; React Router v7 with the `/app` tree skeleton + admin-layout guard stub, DESIGN §3). **SSH Keys** page (table + add/delete via new `api()`, Radix modal for add-key) — chosen because it has no wallet-signing and no real-time, so it can't hurt anyone. Retire old SSH Keys by the §2 mechanism (swing the old sidebar link to `/app/settings/ssh-keys`; delete its div + `loadSSHKeys`/`openAddSSHKeyModal`).
- **Exit criteria:** SSH Keys fully works in React under `/app`, retired from the old app via link-swing; parity for that page passes; back button correct.
- **Retires:** **SSH Keys** (first cross-boundary retirement — validates the whole mechanism).
- **Risk:** low stakes by design; high learning value.

### Phase 3 — The spine (the product)
- **Goal:** build `connect → deploy → operate` — the reason the front-end exists (DESIGN §2). Retire old dashboard/deploy/VM pages as each lands.
- **Pre-task (grounding):** **close-read the live VM-status / metrics / lifecycle code** before building the operate cockpit, so it's drawn from the real state machine (flagged in DESIGN §2 / v0.7 note — the operate phase currently leans on the SignalR event names and modal surfaces, not a full read of the status paths).
- **Tasks:**
  - **Dashboard** = operate + fund home; **"Deploy" promoted** to a primary action (DESIGN §2). *Migrating the dashboard triggers the nav-of-record handoff — see below.*
  - **Deploy** at `/app/marketplace/:slug/deploy`: one-click "recommended settings" + opt-in Customize; preserve name validation (shared `VmNameService`), min-spec validation, platform-variable hiding, locked/editable/user constraints, ToS-gate retry, cost→runway, hard fund gate; land on the new VM detail page. Reuse the ONE deploy path logic from `deploy-submit.js`.
  - **VM detail** (`/app/vms/:id`) = operate cockpit: live status/metrics via **per-VM SignalR** (subscribe on mount / clean up on unmount, DESIGN §6.9); access panel (SSH/ports/domains/terminal as tabs/modal-routes); lifecycle stop/restart/destroy; runway indicator.
  - Implement Pre-req #2 (old app opens to a page via param).
- **Nav-of-record handoff (the fiddly moment):** while migrating, the old monolith is the entry at `/`. **The instant the dashboard is migrated**, the new shell becomes the natural home (it *is* the post-login landing), so: post-login now lands on `/app`; the new shell's sidebar links to *migrated* pages as client routes and to *un-migrated* pages by deep-linking into the old monolith (`/?page=x`, via Pre-req #2). Each subsequent migration swings one link from the old deep-link to the `/app/*` route.
- **Exit criteria:** a user can connect → (fund guard) → deploy with recommended settings in one click → land on the live VM detail and operate it, entirely in React; old dashboard/deploy/VM modules retired; spine parity (DESIGN §7) passes; the three money/lockout E2E flows green (DESIGN §8).
- **Retires:** dashboard, template deploy modal, VM list + VM modals (terminal/domains/access) as each lands.
- **Risk:** highest value + highest risk. Mitigations: core already frozen (Phase 1); operate close-read first; E2E gates on the money flows.

### Phase 4 — Landing (SSG) built & proven
- **Goal:** prove the **SSG pipeline** (distinct from the SPA pipeline proven in Phase 2) and have the landing ready.
- **Tasks:** build the SSG landing (build-time prerender, DESIGN §6.12) served at a **preview path** first (not `/` yet); tokened, light/dark, mobile-first, SEO/OG/`hreflang` scaffold, tight budget (must not pull the app bundle); one launch locale to start (i18n-ready arch, phased rollout — DESIGN §6.6); CTA → `/app`.
- **Exit criteria:** landing renders as static HTML at the preview path, passes its perf budget, excludes the app bundle; SSG build wired into CI.
- **Retires:** nothing yet (the `/`-flip is Phase 6).
- **Risk:** low. Decoupling "build landing" from "flip `/`" keeps it low-risk.

### Phase 5 — Supporting paths
- **Goal:** migrate the off-spine pages. **Parallelizable** — each is independent behind its own URL, so different people can take them concurrently once the spine is up.
- **Tasks (each via the §2 retirement mechanism):** Marketplace (`marketplace-templates.js` → URL-driven filters); My Templates (`my-templates.js`); Nodes (browse-only — **no detail page**, DESIGN §2); Settings (incl. SSH Keys already there from Phase 2); Admin (compliance/abuse/templates — `admin-*.js`, behind the layout role-guard); in-app `/app/report` reusing the report form; port `sign.html` if desired (or leave standalone).
- **Exit criteria:** every old page has a migrated `/app` equivalent; each old module + div + inline handlers deleted; parity per page passes.
- **Retires:** all remaining feature/admin modules.
- **Risk:** low-moderate, mechanical. The one care point: admin parity (CSAM NotScanned never shown as clean, etc. — DESIGN §7).

### Phase 6 — Cutover
- **Goal:** the old app is gone; the target topology is live.
- **Tasks:** the `/`-flip (Pre-req #3): `/` + locale prefixes → SSG landing; `/app/*` is the sole app. Delete the `index.html` page-div monolith, all remaining `window.*` bridge globals (§2), and inline handlers. Final full parity + E2E pass.
- **Exit criteria:** no vanilla monolith remains; DESIGN §8 success criteria met (feature-touches-one-place; critical-path speed; correctness-by-construction; zero known-behavior regressions).
- **Retires:** the old app, entirely.
- **Risk:** low if Phases 2–5 were disciplined (nothing left but deletion + the flip).

---

## 5. Sequencing notes & known wrinkles (honest)

- **Dual-shell transition period (Phases 2–3).** Before the dashboard migrates, clicking a migrated item from the *old* sidebar loads the new `/app` shell (its own sidebar) — so users briefly see the new shell for migrated pages and the old shell for the rest. This is the honest cost of incrementalism; it ends at the nav-of-record handoff (dashboard migration), after which the new shell is home and old pages are deep-linked.
- **Phases 4 and 5 are not strictly sequential.** The landing (4) and supporting paths (5) can proceed in parallel after the spine (3). Numbered for readability, not hard ordering.
- **`sign.html` / `report.html`** stay as standalone entries until explicitly migrated; they don't block anything and share only the wallet/token layers.
- **Locale launch set** is a product call (DESIGN §6.6, §14); the architecture carries all eight regardless of which launch first.

---

## 6. Definition of done (project-level)

From DESIGN §8, restated as the finish line: a feature or shared-component change touches **one** place; recommended-settings deploy is **one click** past the template; XSS is framework-owned (no hand-escaping in feature code); the §4 auth machine + `api()` are unit-tested and the three money/lockout flows are E2E-gated in CI; the §7 parity checklist passed at each retirement, so cutover carries **zero known-behavior regressions**.

---

## 7. Open items feeding the plan (from DESIGN §14)
- **Surface undercount — RESOLVED (2026-07-21).** Six standalone entries found, not four. Decision: **`terminal.html` + `file-browser.html` → in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) with **pop-out preserved** (chromeless variant), tokened by session, xterm bundled+lazy (not CDN). No blocker (plain xterm/WS; no COOP/COEP). `sign.html`, `report.html`, `tos.html` stay standalone. Folded into DESIGN v0.8 (§2/§3/§4.1/§7/§10). Consequence: **two fewer standalone entries to serve** — the `/`-flip (serving Change 3) list shortens to sign/report/tos. WS auth (token-in-query on `/api/terminal-proxy` + `/api/sftp-proxy`) preserved; "never log token/password" is a parity item.
- Regenerate + spot-check OpenAPI-derived types (do in Phase 0).
- Balance-change emit — deferred; balance polls until then (does not block the spine).
- Landing content/copy — product task, needed for Phase 4.
- Launch locale set — product call, needed for Phase 4.
- App namespace hosting (`/app/*` same-origin vs future `app.` subdomain) — route tree unaffected; decide before Phase 6 if a subdomain is wanted.

---

## 8. Journal (living — newest first)

> Entry template:
> **`YYYY-MM-DD` · Phase N · <author>** — what changed / decided / learned; any status-dashboard update; any new blocker.

**`2026-07-22` · Phase 2 · —** — **Shell skeleton drafted; combined pure suite green in-repo (48 passed | 5 todo).** Delivered `wwwroot-next/src/app` + `src/features/ssh-keys`. Pure/tested: `resolveShellView` (DerivedStatus → surface; all 6 statuses; only WRONG_NETWORK blocks escrow), `canAccessAdmin` guard, `validateSshKey` — 14 new tests. Skeletons (typed, TODO): `StatusGate`, the four gates (Connect/Reauth/Stale/Network), `AppShell`, React Router v7 `routes.tsx` (admin guard + SSH Keys at `/app/settings/ssh-keys`), `useSshKeys` (TanStack Query), `SshKeysPage`, `AddKeyModal` (Radix). Gate mapping encodes §4: WRONG_NETWORK shows app + escrowBlocked; UNCERTAIN shows app + stale banner; ADDRESS_MISMATCH → reauth as new address (fail closed). Retirement mechanism for SSH Keys spelled out (build → parity → swing legacy link → delete old module, same change). Spec in `src/app/PHASE2_SHELL.md`. **Honest note:** shell stubs import react-router-dom / @tanstack/react-query / @radix-ui/react-dialog → `tsc`/build errors until those are `npm i`-ed; Vitest runs regardless (pure import graph). Phase 2 depends on Phase 1's AuthProvider being wired. Both phases now specified test-first; remaining work is in-repo.

**`2026-07-22` · Phase 1 · Owner** — **walletCrypto cross-compat fixture closed — 54 passed, 0 todo.** Captured a real `{signature, plaintext, ciphertext}` triple from the running legacy app (`wallet-crypto.js`, throwaway wallet) and added it as a fixture test: the noble-v2 port decrypts genuine legacy-produced ciphertext back to plaintext, proven both in a sandbox and in-repo. Cross-compatibility with legacy wallet-encrypted data is now VERIFIED, not assumed — the migration-safety guarantee for VM passwords. Last real Phase 1 item done; `EXPECTED_CHAIN_ID` env-wired earlier and picking the correct chain live; `App.tsx` (Phase 0 proof) deleted; PHASE1_AUTH_CORE.md refreshed to COMPLETE. Remaining are deferrable hardening only (`as never` AppKit-boundary types, `gen:api`→schema.d.ts, clean Release build to exercise BuildFrontendNext, `npm audit` review, favicon). Legacy `app.js` `window.__cap` capture shim to be removed.

**`2026-07-22` · Phase 1 · Owner** — **Phase 1 COMPLETE — full auth flow proven live.** Wallet connect → SIWE nonce → sign → `/api/auth/wallet` verify → `onAuthenticated` → `AUTH_SUCCESS` → `deriveStatus` READY → Meridian shell renders (address chip shows). Reload PERSISTS the session: `dc_rt` httpOnly cookie survives the Vite proxy, AppKit `getSession` restores, and the AuthProvider restore-via-refresh path (mirrored token → callRefresh → AUTH_SUCCESS) re-establishes it; `/api/auth/session` returns `{success:true,data:{address}}`. The AppKit slice — `siwe.ts` (createDecloudSiweConfig), `walletState.ts` (createWalletAdapter), `AuthProvider.tsx` (wiring + restore), `main.tsx` (QueryClient>Auth>Router) — all wired and working against the real orchestrator. Grounding wins this stretch: pulled the real wallet-crypto.js, siwe-config.js, AuthController.cs, app.js, and the actual @noble/@reown installed types before/while writing each port (after early guessing cost a detour). Deps pinned: @noble/ciphers+hashes 2.2.0, @reown/appkit* 1.8.23, ethers 6.17. Deferred/known: `as never` casts at the AppKit boundary (tighten to real types as a hardening pass); `EXPECTED_CHAIN_ID` 137 vs backend Amoy 80002 (set per target chain); walletCrypto cross-compat fixture still todo; App.tsx (Phase 0 proof) now orphaned — delete. Phase 1 → ☑.

**`2026-07-21` · Phase 1 · —** — **Auth-core skeleton drafted and its pure tests pass in-repo (34 passed | 5 todo).** Delivered `wwwroot-next/src/auth` + `src/api`: the pure, fully-tested core is implemented — `deriveStatus` (the §4 truth table, 16 cases), `sessionMachine` reducer + tri-state `performRefresh` (10), `api()` envelope-unwrap + 401 refresh-retry-once + the three failure kinds (8) — plus typed stubs for the pieces needing live AppKit/ethers/backend (`siwe`, `walletCrypto`, `walletState`, `AuthProvider`), each citing the legacy file it ports. `vitest run` green on Windows (Vitest 4.1). One judgment call flagged in code + test: `WRONG_NETWORK` precedes `UNCERTAIN` when both hold. `walletCrypto` + `buildSiweMessage` marked port-verbatim (byte-compat boundaries). `api()` assumes an `ApiResponse<T>` shape — reconcile against generated `schema.d.ts`. Build order + deps in `src/auth/PHASE1_AUTH_CORE.md`. Phase 1 is now scaffolded test-first; remaining Phase 1 work (port the two verbatim modules, wire AppKit + AuthProvider) is in-repo. **Note:** `npm install` reported 4 audit vulns (build-time devDeps likely) — review with `npm audit`, don't reflexively `--force`.

**`2026-07-21` · Pre-req · Owner** — **Serving Change 1 implemented in `Program.cs`** (owner is the `Program.cs` owner). Two surgical, production-only edits, aligned to existing patterns: (1) a `/app` static provider serving `wwwroot/dist-app` at `RequestPath="/app"` with the same cache policy, guarded by its own `Directory.Exists(distAppPath)` (independent of the legacy `dist/`); (2) the existing catch-all `MapFallback` amended with a one-line branch — `/app/*` → `dist-app/index.html`, else `dist/index.html` — keeping the `/api|/hub|/swagger|/health` 404 guard first. **No Development-mode change** (dev serves `/app` from the new app's Vite server). Braces balance; regions verified. **Not yet compiled in-repo** — owner to build. **Still required to finish Phase 0 (not `Program.cs`):** the new Vite project (base `/app/`, output `wwwroot/dist-app`) and the parallel csproj `BuildFrontendNext` target. Change 2 (old-app `?page=`) and Change 3 (`/`-flip) remain for Phase 3 and 4/6.

**`2026-07-21` · Phase 0 · Owner** — **Phase 0 COMPLETE — Release/Production serving verified.** Startup banner in Production confirms all three: `✓ Static files configured: Serving from wwwroot/dist/` (legacy at `/`), `✓ New app configured: Serving /app from wwwroot/dist-app/` (the new `/app` provider activating), `Hosting environment: Production`. Two environment gotchas resolved along the way, both about **build config vs runtime environment being independent knobs**: `-c Release` changes the build, not the environment; and `dotnet run` applies `launchSettings.json` which pinned `ASPNETCORE_ENVIRONMENT=Development` and overrode the shell env var — fixed with `--no-launch-profile` (or running the built DLL). The production-only `/app` static provider + amended `MapFallback` only run when `IsDevelopment()` is false, which is why Production was required to exercise them. Serving Change 1 (Program.cs) proven end to end. Phase 0 → ☑ (dev + production both proven).

**`2026-07-21` · Phase 0 · Owner** — **Scaffold runs — dev pipeline proven.** The Meridian proof page renders at `http://localhost:3001/app/` (Vite): React+TS compiling, `/app` base, tokens + self-hosted fonts loading, light/dark toggle working, `/api`+`/hub` proxying to `:5050`. Setup issues resolved along the way, all pre-existing or environmental, none from the serving/csproj changes: (1) **DI captive dependency** — `IUserService` was needlessly `Scoped` while three singletons consumed it; fixed by registering it `Singleton` (all its deps are singletons, stateless) — a latent bug caught by Dev-mode scope validation. (2) `npm ci` needs a committed `package-lock.json` (`npm install` once → commit). (3) `tsconfig.node.json` can't set `noEmit` under project-references → `emitDeclarationOnly`. (4) Local env: MongoDB not running (started via Docker) + user-secret for the connection string. Non-blocking dev-box noise: `cosign` missing (system-VM seeder) and Caddy `:2019` down (central ingress) — unrelated to frontend. **Still open for Phase 0:** verify the **Release** serving path (`dotnet run -c Release` → `/app` served by ASP.NET from `dist-app`, `/` still legacy) — the production-only `/app` provider + `MapFallback` aren't exercised in dev.

**`2026-07-21` · Phase 0 · —** — **Drafted the Phase 0 scaffold** (`wwwroot-next/`, zipped): Vite + React + TS with `base:'/app/'` → `../wwwroot/dist-app`, dev on `:3001` proxying `/api`+`/hub`(ws)+`/ws` to `:5050`; `index.html` with an inline pre-paint theme script (no flash); `main.tsx` importing fonts (Fontsource) → tokens → global in the right order; `App.tsx` a token-driven Phase 0 proof page with a working light/dark toggle; `design-tokens.css`/`fonts.css`/`global.css` under `src/styles`; `csproj-additions.xml` = a `BuildFrontendNext` target mirroring the existing `BuildFrontend`; README + `.gitignore`; `gen:api` script for OpenAPI types. **Caught + fixed a real bug:** `fonts.css`'s absolute `/fonts/...` urls break under the `/app` base → switched primary fonts to **Fontsource** (base-aware), kept static `fonts.css` as a documented fallback. Not yet `npm install`ed/built in-repo — it's a starter set to drop into `src/Orchestrator/wwwroot-next/`. Phase 0 remains ☐ until it's in the repo, building, and `/app` renders under ASP.NET.

**`2026-07-21` · Phase 0.5 · —** — **Phase 0.5 complete.** Closed the two loose ends. Fonts: `fonts.css` self-hosts the three OFL faces (Space Grotesk / Inter / JetBrains Mono) as woff2 with `font-display:swap` and latin+latin-ext ranges (Cyrillic/CJK deferred to the i18n phase, lazy per-locale); Fontsource npm path documented as the Vite-idiomatic alternative; CDN link dropped for production (kept only in the preview reference). Reconciliation: grounded a read of the existing `wwwroot/design-tokens.css` — it's a *different* system (dark-only, teal, Outfit, glow/gradient, clashing token names/values), so **not replaced in place**. Decision recorded (DESIGN §6.4): Meridian is net-new for the new app; old file stays for legacy during migration; never coexist on a document (serving split); each page adopts Meridian on migration as a restyle (terminal/file-browser → Meridian **dark**); old file retired at cutover. Phase 0.5 → ☑.

**`2026-07-21` · Phase 0.5 · —** — **Built the Meridian token layer** (`design-tokens.css`) — role-based light + derived dark as CSS custom properties. **Contrast computed, not eyeballed:** a WCAG script flagged three real failures the mockup hid (faint text on light 2.7:1, warning text on light 3.6:1, dark tertiary 4.0:1) — all corrected, and it surfaced a genuine tension (three small-text grays can't all clear 4.5:1 on a light canvas), resolved by scoping `--text-tertiary` to large/non-essential text with `--text-secondary` for anything small and informational. Delivered `meridian-reference.html`: components + hero + dashboard rebuilt entirely from tokens (no hardcoded hex) with a live light/dark toggle — the Phase 0.5 exit criteria, visible. Remaining for Phase 0.5: self-host the three OFL fonts as woff2 (drop the CDN link), and reconcile/replace the pre-existing `wwwroot/design-tokens.css` in-repo.

**`2026-07-21` · Phase 0.5 · —** — **Visual direction chosen: "Meridian."** Explored three fresh identities as real standalone HTML mockups (hero + dashboard each), deliberately steered off the three AI-design tells (cream+terracotta, black+acid-green, broadsheet), each re-derived from DeCloud's own world (centerless mesh / beacon / escrow ledger). Picked **A — Meridian**: cool architectural light, iris `#332ED6` accent, Space Grotesk + Inter + JetBrains Mono, centerless-hairline-mesh signature. Added **Phase 0.5 (Visual direction → tokens)** to the plan; token seeds recorded in DESIGN §6.4 (v0.9). Flagged the real consequence: Meridian is light-*first*, not light-*only* — dark mode must be **derived from the same roles** (R7), not skipped. Next: tokenize into `design-tokens.css` (reconciling the one that already exists) + derive dark mode.

**`2026-07-21` · Design · —** — Resolved the surface undercount by grounding `terminal.html`/`file-browser.html` in the code before deciding. Found they're `window.open('_blank')` **pop-out** pages (multi-window is the deliberate affordance), auth token read from `localStorage` (not a URL ticket), plain xterm over WS with **no SharedArrayBuffer/COOP-COEP** (so no app-wide-isolation blocker). Decision: **fold into in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) **with pop-out preserved** via a chromeless route variant — elegant *and* nothing deliberate dropped. Also caught a security detail now recorded in DESIGN §10: JWT + VM password ride in the WS query string (browser WS can't set `Authorization`); preserved with a "never log" rule and a backend-dependent short-lived-ticket TODO. Bonus: a `/design-tokens.css` already exists (token layer isn't from scratch). Folded to **DESIGN v0.8**.

**`2026-07-21` · Pre-req · —** — **Grounded the serving spec's §5 checklist against the actual code** (`Program.cs`, `Orchestrator.csproj`, `vite.config.js`, `Orchestrator.sln`); `BACKEND_SERVING_SPEC.md` bumped to **v2**. Two material corrections the grounding caught: (1) **an SPA fallback already exists** — a catch-all `app.MapFallback` delegate (prod-only) that 404s `/api|/hub|/swagger|/health` and serves `dist/index.html` for everything else — so Change 1 must **amend** that fallback (a 3-line `/app` branch), not add a new one; the v1 "no fallback" assumption was wrong. (2) **Surface undercount** — there are six standalone HTML entries, not three: also `terminal.html`, `file-browser.html`, `tos.html` (per-VM operate surfaces + the ToS page). Verdicts: §5.1 grounded w/ corrections; §5.3 grounded (no `/app` backend route collision; amend-fallback fits middleware order); §5.4 grounded and simpler (Debug serves no frontend → **no dev-mode backend change**); only §5.2 folder-names and §5.5 timing need owner sign-off. **Change 1 is now a small, well-scoped edit set; only Change 1 gates Phase 0.** New open item logged (§7): reconcile the surface undercount into DESIGN §4.1/§2/§7.

**`2026-07-21` · Pre-req · —** — Drafted **`BACKEND_SERVING_SPEC.md`** (v1).

**`2026-07-21` · Plan · —** — Implementation plan established at v1.0. **Option A (split by URL) ratified.** Phased approach agreed: scaffold → frozen core → shell+SSH-Keys pipeline proof → spine → landing → supporting → cutover. Retirement mechanism defined (atomic per page, by URL). The nav-of-record handoff (at dashboard migration) identified as the one fiddly execution moment and given a mechanism (old-app deep-link via `?page=`). **Next action: confirm the backend serving split (§3, Pre-req #1) with the `Program.cs` owner before starting Phase 0.** No code written yet.
