# Backend Serving Spec — Option A coexistence (v2, grounded against the code)

**For:** whoever owns `src/Orchestrator/Program.cs` and the frontend build wiring.
**Status:** every item in the confirmation checklist (§5) has now been **grounded against the actual code** — `Program.cs`, `Orchestrator.csproj`, `vite.config.js`, `Orchestrator.sln`. v2 corrects v1 where the code disagreed. Two material corrections up front:
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

## 2. Change 2 — old app opens to a page on load (needed for **Phase 3**)

**Requirement:** after the dashboard migrates, the new shell deep-links into un-migrated pages still in the old monolith.

**Grounded mechanism:** ~5 lines in the old `wwwroot/src/app.js` init — read `?page=<name>` (or `#<name>`) and call `showPage(name)` after auth/init. `app.js` is a `FrontendSource` file, so the edit triggers the Release rebuild; no server change. New shell then links to e.g. `/?page=nodes`.

**Acceptance:** `GET /?page=nodes` opens the old app on Nodes; unknown/absent → default (dashboard), no error.

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
5. **Change 2 / Change 3 timing:** unchanged — **only Change 1 gates Phase 0**; Changes 2 and 3 land at Phases 3 and 4/6. Owner to agree.

**Net:** Change 1 is a small, well-scoped set of edits — one new Vite project, one parallel csproj target, one added static provider, and a **three-line branch inside an existing fallback**. No dev-mode backend change. The two remaining owner decisions are cosmetic (folder names) and timing.

---

## 6. Findings that affect the DESIGN doc (surfaced by grounding)

Grounding the serving layer turned up a **surface undercount** — now **RESOLVED** in DESIGN v0.8:
- **`terminal.html` + `file-browser.html` → in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) with pop-out preserved. They were `window.open('_blank')` pop-out pages; plain xterm/WS with no COOP/COEP, so no serving-isolation concern. **Serving consequence: two fewer standalone entries.**
- **`tos.html`, `sign.html`, `report.html` stay standalone.**
- **Updated Change 3 topology:** the standalone-entries list to keep serving after the `/`-flip is **`sign.html`, `report.html`, `tos.html`** (not the six originally implied). Everything else in §3 unchanged.
