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
| **0** | Scaffold new app beside old | ☐ Not started | — | — | — |
| **1** | Frozen core (session/identity + `api()`) | ☐ Not started | — | — | — |
| **2** | Shell + pipeline-proof page (SSH Keys) | ☐ Not started | — | — | — |
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

**`2026-07-21` · Pre-req · Owner** — **Serving Change 1 implemented in `Program.cs`** (owner is the `Program.cs` owner). Two surgical, production-only edits, aligned to existing patterns: (1) a `/app` static provider serving `wwwroot/dist-app` at `RequestPath="/app"` with the same cache policy, guarded by its own `Directory.Exists(distAppPath)` (independent of the legacy `dist/`); (2) the existing catch-all `MapFallback` amended with a one-line branch — `/app/*` → `dist-app/index.html`, else `dist/index.html` — keeping the `/api|/hub|/swagger|/health` 404 guard first. **No Development-mode change** (dev serves `/app` from the new app's Vite server). Braces balance; regions verified. **Not yet compiled in-repo** — owner to build. **Still required to finish Phase 0 (not `Program.cs`):** the new Vite project (base `/app/`, output `wwwroot/dist-app`) and the parallel csproj `BuildFrontendNext` target. Change 2 (old-app `?page=`) and Change 3 (`/`-flip) remain for Phase 3 and 4/6.

**`2026-07-21` · Design · —** — Resolved the surface undercount by grounding `terminal.html`/`file-browser.html` in the code before deciding. Found they're `window.open('_blank')` **pop-out** pages (multi-window is the deliberate affordance), auth token read from `localStorage` (not a URL ticket), plain xterm over WS with **no SharedArrayBuffer/COOP-COEP** (so no app-wide-isolation blocker). Decision: **fold into in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) **with pop-out preserved** via a chromeless route variant — elegant *and* nothing deliberate dropped. Also caught a security detail now recorded in DESIGN §10: JWT + VM password ride in the WS query string (browser WS can't set `Authorization`); preserved with a "never log" rule and a backend-dependent short-lived-ticket TODO. Bonus: a `/design-tokens.css` already exists (token layer isn't from scratch). Folded to **DESIGN v0.8**.

**`2026-07-21` · Pre-req · —** — **Grounded the serving spec's §5 checklist against the actual code** (`Program.cs`, `Orchestrator.csproj`, `vite.config.js`, `Orchestrator.sln`); `BACKEND_SERVING_SPEC.md` bumped to **v2**. Two material corrections the grounding caught: (1) **an SPA fallback already exists** — a catch-all `app.MapFallback` delegate (prod-only) that 404s `/api|/hub|/swagger|/health` and serves `dist/index.html` for everything else — so Change 1 must **amend** that fallback (a 3-line `/app` branch), not add a new one; the v1 "no fallback" assumption was wrong. (2) **Surface undercount** — there are six standalone HTML entries, not three: also `terminal.html`, `file-browser.html`, `tos.html` (per-VM operate surfaces + the ToS page). Verdicts: §5.1 grounded w/ corrections; §5.3 grounded (no `/app` backend route collision; amend-fallback fits middleware order); §5.4 grounded and simpler (Debug serves no frontend → **no dev-mode backend change**); only §5.2 folder-names and §5.5 timing need owner sign-off. **Change 1 is now a small, well-scoped edit set; only Change 1 gates Phase 0.** New open item logged (§7): reconcile the surface undercount into DESIGN §4.1/§2/§7.

**`2026-07-21` · Pre-req · —** — Drafted **`BACKEND_SERVING_SPEC.md`** (v1).

**`2026-07-21` · Plan · —** — Implementation plan established at v1.0. **Option A (split by URL) ratified.** Phased approach agreed: scaffold → frozen core → shell+SSH-Keys pipeline proof → spine → landing → supporting → cutover. Retirement mechanism defined (atomic per page, by URL). The nav-of-record handoff (at dashboard migration) identified as the one fiddly execution moment and given a mechanism (old-app deep-link via `?page=`). **Next action: confirm the backend serving split (§3, Pre-req #1) with the `Program.cs` owner before starting Phase 0.** No code written yet.
