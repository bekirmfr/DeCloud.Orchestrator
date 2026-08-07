# Backend Serving Spec — Option A coexistence (v3, Change 2 shipped + nav-of-record live)

**For:** whoever owns `src/Orchestrator/Program.cs` and the frontend build wiring.
**Status:** Change 1 (Phase 0) and **Change 2 (Phase 3) are both implemented and live in production.** Change 3 (the `/`-flip to the SSG landing) remains not started — no landing exists yet (Phase 4 not started). v3 adds: Change 2's shipped shape, and a **new client-side behavior not in the original three-change plan** — a signed-in visitor to `/` is now redirected to `/app` (§2.1). Two material corrections from v1→v2 still stand:
- **An SPA fallback already exists** — a catch-all `app.MapFallback` delegate (production-only). Change 1 must **amend** it, not add a second one. (v1 wrongly said there was none.)
- **There are more standalone HTML entries than v1 listed** — also `terminal.html`, `file-browser.html`, `tos.html`. This affects the design doc's surface inventory (see §6).

---

## 0. Current serving — grounded (✓ confirmed / ⚠ corrected)

- **✓ Prod static:** `UseDefaultFiles` + `UseStaticFiles` from `wwwroot/dist` at `RequestPath = ""`, with the cache policy (hashed `*.js/*.css` + fonts → `immutable`; other/`index.html` → `no-cache`).
- **⚠ CORRECTION — an SPA fallback EXISTS.** Not `MapFallbackToFile`, but a custom **`app.MapFallback` delegate, production-only**:
  ```
  if path StartsWithSegments /api | /hub | /swagger | /health  → 404
  else → serve wwwroot/dist/index.html  (no-cache);  503 "not built" page if dist missing
  ```
  This is a **catch-all that will serve the OLD app for any `/app/*` client route** unless amended. It is the crux of Change 1.
- **✓ Endpoint order:** `UseSwagger` → `UseRequestLogging`/`UseErrorHandling` → `UseWebSockets` → `UseSubdomainProxy` → `UseCors` → `UseAuthentication`/`UseAuthorization` → `UseRateLimiter` → `UseWebSocketProxy` (terminal/sftp WS, reads `token` query, after auth) → `MapControllers` → `MapHub<OrchestratorHub>("/hub/orchestrator")` → `MapHealthChecks("/health")` → **then** the `MapFallback`. Static (prod block) is registered earlier. So: **endpoints before fallback; static before fallback.**
- **✓ Dev:** `IsDevelopment` → `.NET serves NO frontend`; Vite dev server `:3000` proxies `/api` + `/ws` → `:5050`. (Consequence: **dev needs no `Program.cs` change** for Option A — see §5.4.)
- **✓ Build wiring (`Orchestrator.csproj`):** `FrontendDir = wwwroot`; `BuildFrontend = true` (Release) / `false` (Debug); `FrontendSource` globs `src/**/*.js`, `public/**`, `index.html`, `styles.css`; target **`BuildFrontend`** (`BeforeTargets=Build`, Release-only) `Inputs = package.json;vite.config.js;@(FrontendSource)`, `Outputs = dist/index.html`, runs `npm ci` + `npm run build`, errors if `dist/index.html` missing; **`CleanFrontend`** (`AfterTargets=Clean`) removes `dist`. `public/**` is included as Content (its Remove is commented out).
- **✓ Vite:** `base: '/'`; `build.outDir: 'dist'`; multi-page `rollupOptions.input`.
- **⚠ CORRECTION — surface count.** Standalone HTML entries are **more than (app, sign, report)**. From `vite.config.js` input + `Orchestrator.sln`: **`index.html` (app), `sign.html` (signer), `report.html` (abuse, via `public/`), `terminal.html`, `file-browser.html`, `tos.html`.** `terminal.html`/`file-browser.html` are per-VM operate surfaces served alongside the terminal/sftp WS proxy; `tos.html` is the Terms page (the deploy ToS gate). → reconcile in the design doc (§6 here).
- **Must-not-break:** `/api/*`; `/hub/orchestrator` (SignalR, JWT via `access_token` query); the terminal/sftp WS proxy via `UseWebSocketProxy` (JWT via `token` query); `/swagger*`; `/health`; and the six standalone entries above.

---

## 1. Change 1 — serve the new app under `/app/*` (needed for **Phase 0**)

**Requirement (unchanged):** `/app` and `/app/*` serve the new React build (with SPA fallback for its client routes); everything else serves the old app exactly as today.

**Grounded mechanism (corrected — amend the existing fallback):**

*Build:*
- New app = its own Vite project *(recommended: `src/Orchestrator/wwwroot-next/`)*, **`base: '/app/'`**, `build.outDir` → a separate dir *(recommended: `wwwroot/dist-app/`)* so `wwwroot/dist/` (old app) is untouched.
- `Orchestrator.csproj`: add a **parallel** target mirroring the existing one — `BuildFrontendNext` (`BeforeTargets=Build`, Release-only), its own `FrontendSourceNext` inputs, `Outputs = wwwroot/dist-app/index.html`, running `npm ci` + `npm run build` in the new dir; a `Content Include` for `wwwroot/dist-app/**`; `Content Remove` for the new `node_modules`/`src`; and a `CleanFrontendNext`. The existing `BuildFrontend` (keyed on `dist/index.html`) is unaffected.

*Serve (`Program.cs`, prod block):*
1. Keep the existing `UseDefaultFiles` + `UseStaticFiles` (`dist`, `""`).
2. **Add** `UseStaticFiles` with a `PhysicalFileProvider` on `wwwroot/dist-app`, `RequestPath = "/app"`, reusing the same `OnPrepareResponse` cache policy — so `/app/assets/*` resolve as files.
3. **Amend the existing `MapFallback` delegate** (do not add a second — `MapFallback` is terminal):
   ```
   if path StartsWithSegments /api | /hub | /swagger | /health   → 404   (unchanged)
   var isApp = path StartsWithSegments /app
   serve (isApp ? wwwroot/dist-app : wwwroot/dist)/index.html   (no-cache; per-app 503 if missing)
   ```
4. Endpoints (`MapControllers`/`MapHub`/health) already run before the fallback — unchanged.

*Dev:* **no `Program.cs` change** (Debug serves no frontend). Run the new app's Vite on a **second port** *(recommended `:3001`, `base '/app/'`, proxy `/api` + `/hub` + `/ws` → `:5050`)*; old app's Vite `:3000` unchanged.

**Acceptance (Phase 0 exit):** Release — `GET /app` and `/app/<route>` return the new `index.html`; `/app/assets/*` resolve from `dist-app`; `GET /` still returns the old app; `/api/*`, `/hub/orchestrator`, `/swagger`, `/health`, and all six standalone entries unchanged; per-app 503 works when a build is missing. Debug — new app reachable via `:3001` with `/api`+`/hub` proxied; old app unchanged.

---

## 2. Change 2 — old app opens to a page on load — **SHIPPED, live in production**

**Requirement:** after the dashboard migrates, the new shell deep-links into un-migrated pages still in the old monolith.

**Shipped mechanism** (`wwwroot/src/app.js`, inside `DOMContentLoaded`, **after** `restoreSession()` succeeds — not ~5 lines at init as originally estimated; it has to run after session restore because most pages call `api()` immediately and the admin pages gate on `tokenHasAdminRole`):

```js
const requestedPage = new URLSearchParams(location.search).get('page');
if (requestedPage) {
    if (document.getElementById(`page-${requestedPage}`)) {
        showPage(requestedPage);
    } else {
        // Unknown or RETIRED page name. There is no default `.page active` to
        // fall back to any more (page-dashboard carried it and has since been
        // deleted — see §2.2), so silently doing nothing would leave every
        // `.page` hidden and nothing shown. Stale links go home.
        location.replace('/app');
    }
}
```

The `getElementById` guard matters for retirement: as pages get deleted from `index.html`, a stale bookmark to a since-retired `?page=x` degrades to `/app` instead of a blank shell.

No server change — `app.js` is a `FrontendSource` file, so the edit triggers the existing Release rebuild.

**Acceptance (confirmed live):** `GET /?page=nodes` (signed in) opens the old app on Nodes; `?page=<retired-or-unknown>` and no param at all both redirect to `/app` (see §2.1 — the redirect logic and this one now share responsibility for "what does a bare or bad `/` request do").

### 2.1 New: signed-in `/` now redirects to `/app` (not in the original 3-change plan)

Once the dashboard and VM-list pages migrated (Phase 3), the old app's dashboard stopped being *anything* useful for a signed-in user to land on — so **`showDashboard()`** (the single function both `restoreSession()` and a fresh SIWE sign-in call — covers every way a session becomes active) now does:

```js
if (!new URLSearchParams(location.search).get('page')) {
    location.replace('/app');
    return;
}
document.getElementById('login-overlay').classList.remove('active');
// ...
```

`location.replace`, not `location.assign` — the bare `/` shouldn't sit in browser history, or Back from `/app` lands on a URL that immediately redirects forward again.

**This is a client-side (JS) redirect, not a `Program.cs` routing change** — `/` still *serves* the old app's `index.html` and its full bundle to every visitor, signed in or not; the redirect happens after the page has loaded and the session has restored. So:

- **Anonymous visitor to `/`** → the legacy connect/login page, unchanged. This remains the sole public entry point until Change 3 (the landing) ships.
- **Signed-in visitor to `/`** (session restores from the `dc_rt` cookie) → briefly renders the old shell, then redirects to `/app`.
- **Signed-in visitor to `/?page=x`** → stays on the old app's page `x` (the guard above).

**Known cost #2 — the redirect silently breaks any link into the legacy app that targets bare `/` (found 2026-07-25).** The new app carries deliberate, documented "legacy bridge" links for flows not yet ported — most importantly the on-chain deposit flow (`DashboardPage.tsx` and `DeployPage.tsx`, both labelled *"Add funds in the classic app"*, both `href="/"`, both tracked in `wwwroot-next/src/features/deploy/DEPLOY_MIGRATION.md`). Those were written **before** this redirect shipped and were correct at the time. Now a signed-in user who clicks one loads `/`, `restoreSession()` succeeds, `showDashboard()` finds no `?page=` and fires `location.replace('/app')` — **returning them to the page they just left, with no error.** The funding path is dead: on `DeployPage` the link appears in the zero-runway state, i.e. exactly when someone is trying to pay. **Any bridge link must carry `?page=<surviving-page>`** to defeat the redirect; there is no top-up *page* (the balance is a sidebar card opening a modal), so the deposit bridge additionally needs a modal trigger — see the `action=deposit` extension to the `?page=` handler in `FRONTEND_REMAKE_IMPLEMENTATION.md` §8. **This is the second-order cost of an otherwise correct change:** the redirect is right, and it invalidated an assumption held in a different file that nothing linked the two. **Resolves at Change 3** along with the duality above — once `/` is the landing and the old app is gone, there are no bridges left to break.

**Known cost #1 — two independent SIWE/connect flows exist until Change 3 ships.** The old app's login overlay (AppKit + legacy `siwe-config.js`) and the new app's connect gate (AppKit + `src/auth/siwe.ts`) are two separate `createSIWEConfig` instances against the same backend. This is not itself a bug — but debugging a live incident here (a spurious sign-in-again modal on `/app` after visiting a legacy page) initially looked like it must involve *this* duality (refresh-token rotation between the two flows was the leading hypothesis) and turned out to be unrelated: three independent bugs entirely inside the new app's own SIWE wiring (see `FRONTEND_REMAKE_IMPLEMENTATION.md` §6, "SIWE/session bugs" and `AGENT_HANDOUT.md`). **The duality remains real and worth remembering when debugging anything auth-adjacent that reproduces after a legacy round-trip** — it's just not guilty by default.

**Resolves at Change 3** (the `/`-flip): once `/` serves the landing and the old app is deleted, there is only one connect flow.

---

## 3. Change 3 — the `/`-flip to the SSG landing (needed for **Phase 4/6**)

**Requirement:** at cutover, `/` (+ locale prefixes) serve the pre-rendered landing; `/app/*` is the sole app; old app removed.

**Grounded mechanism:**
- Landing = build-time SSG static HTML *(from the new app's build; e.g. `wwwroot/dist-app/landing/` or the new build root)*, served by static middleware at `/` and locale prefixes.
- **Amend the same `MapFallback` again:** add a landing branch for non-`/app`, non-excluded routes; **remove** the old-app `dist` branch when the monolith is deleted.
- Final topology: `/api`,`/hub`,`/swagger`,`/health` → backend (first, unchanged); `/` + locales → landing; `/app/*` → new SPA; `sign.html`/`report.html`/`terminal.html`/`file-browser.html`/`tos.html` → their static entries; old `dist` root → **removed**.

**Acceptance:** `GET /` → landing (not old app); `GET /es` → Spanish landing; `GET /app` → SPA; standalone entries + `/api`+`/hub` unchanged; old app unreachable.

---

## 4. Cross-cutting (grounded)

- **Endpoint precedence invariant holds:** the existing fallback already guards `/api`/`/hub`/`/swagger`/`/health`; keep that guard, add the `/app` branch inside it. Endpoints are mapped before the fallback.
- **Terminal/sftp WS:** handled by `UseWebSocketProxy` **before** the fallback, so it never reaches it — it is *not* in the fallback's exclusion list and doesn't need to be (it's terminated earlier). Don't move the fallback ahead of `UseWebSocketProxy`.
- **Cache policy** reused for `dist-app` (same `OnPrepareResponse`).
- **Two builds, two outputs, two targets** — independent; until the new app exists, nothing changes.

---

## 5. Confirmation checklist — grounded verdicts

1. **Current state (§0): GROUNDED, with corrections.** A catch-all `MapFallback` **does** exist (v1 was wrong); there are **six** standalone entries, not three. Everything else in §0 confirmed against source.
2. **Folder/output layout:** recommendation stands (`wwwroot-next/` → `wwwroot/dist-app/`, served at `/app`), now with a concrete csproj target mirroring `BuildFrontend`. **Owner sign-off wanted only on the folder names** (cosmetic) and where the SSG landing output lands.
3. **Scoped `/app` fallback + no `/app` backend routes: GROUNDED.** No controller/hub/proxy uses an `/app` prefix (controllers are `api/[controller]`; hub `/hub/orchestrator`; health `/health`; swagger `/swagger`; `UseSubdomainProxy` is host-based; `UseWebSocketProxy` is path-specific terminal/sftp). Mechanism corrected to **amend the existing fallback**; fits the middleware order (static + endpoints before fallback).
4. **Two-port dev: GROUNDED, and simpler than v1 stated** — Debug serves no frontend, so **no `Program.cs` change is needed for dev**; just a second Vite server. Acceptable unless the owner prefers a unified-dev setup.
5. **Change 2 / Change 3 timing:** Change 2 **shipped** at Phase 3 as planned (§2). Change 3 remains gated on Phase 4/6 (landing not started).

**Net:** Change 1 is a small, well-scoped set of edits — one new Vite project, one parallel csproj target, one added static provider, and a **three-line branch inside an existing fallback**. No dev-mode backend change. The two remaining owner decisions are cosmetic (folder names) and timing.

---

## 6. Findings that affect the DESIGN doc (surfaced by grounding)

Grounding the serving layer turned up a **surface undercount** — **resolved in DESIGN v0.8 as a decision. ✅ BUILT (2026-07-27).**

> **Change 3 gate now SATISFIED.** The in-app **terminal** (`/app/vms/:id/terminal`, xterm) and **file browser** (`/app/vms/:id/files`, SFTP) shipped 2026-07-27 as full-page new-tab routes, verified live on composed VMs (see `FRONTEND_REMAKE_IMPLEMENTATION.md` 2026-07-27 journal). So the earlier gate — "keep serving `terminal.html` and `file-browser.html` until the in-app routes exist" — **is now met**: those two standalone HTML pages can be dropped from the Change 3 keep-list once their legacy files are deleted (deletion pending the legacy-app grep). **One caveat carried forward:** SSH/SFTP reaches only **composed-template** VMs; **inline-template** VMs ship no SSH provisioning, so the routes can't connect to them — that's a template-migration gap, not a serving gap, and it doesn't change the topology below.

- **`terminal.html` + `file-browser.html` → in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) with pop-out preserved — **BUILT 2026-07-27**. They were `window.open('_blank')` pop-out pages; plain xterm/WS with no COOP/COEP, so no serving-isolation concern. **Serving consequence, now realized: two fewer standalone entries once the legacy HTML is deleted.**
- **`tos.html`, `sign.html`, `report.html` stay standalone.**
- **Updated Change 3 topology — condition now MET (2026-07-27):** the in-app terminal + file-browser routes exist, so the standalone-entries list to keep serving after the `/`-flip is **`sign.html`, `report.html`, `tos.html`**. The `terminal.html`/`file-browser.html` entries drop out on legacy-file deletion (pending the grep). Everything else in §3 unchanged.
