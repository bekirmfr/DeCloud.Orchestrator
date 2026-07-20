# DeCloud Frontend Remake — Design & Requirements

**Version:** 0.4
**Status:** React ratified; the five stack decisions locked (§4); **the three §13 backend questions are now answered from the backend source** (§13). Grounded in a full read of the frontend *and* the relevant backend: `Program.cs`, `Orchestrator.csproj`, `ApiResponse.cs`, `OrchestratorHub.cs`, `MarketplaceController.cs`, `AdminComplianceController.cs`, `AbuseController.cs`, `Middleware.cs`.
**Scope:** The user-facing web frontend in `src/Orchestrator/wwwroot`. The NodeAgent operator dashboard is out of scope and deferred (§11).

**Changes since v0.3 — all from reading the backend:**
- **OpenAPI already exists.** Swashbuckle is wired and serves `/swagger/v1/swagger.json` in all environments → R5a types are *generated from the live spec*, not hand-maintained. No backend work needed for this.
- **The wrapper inconsistency is one file.** Every controller returns `ApiResponse<T>` except `MarketplaceController`. "Normalize the contract" = wrap one controller.
- **The enum inconsistency has a single cause.** No global `JsonStringEnumConverter` (enums → numbers) while some endpoints hand-call `.ToString()` (→ strings). One converter fixes it everywhere.
- **The real-time hub already exists and is browser-facing.** `OrchestratorHub` already has `SubscribeToVm/Node/User` groups and already broadcasts `VmStatusChanged`/`VmMetricsUpdated`/`VmAccessInfoUpdated`, JWT-authed. SignalR adoption is "connect and subscribe," not "build a hub." **SSE fallback dropped.**

---

## 0. How to read this document

This guides *all* future frontend work, so it states what **not** to build as much as what to build. Every new layer earns its place: name the property it adds, then show the current system doesn't already provide it.

The v0.4 backend read is the principle "don't build what the foundation already provides" paying off concretely: the two items previously flagged as possible backend work turned out to be **already built** (OpenAPI spec, the real-time hub) or a **one-file change** (the response wrapper) plus a **one-line converter** (enums). The data layer (TanStack Query) and the existing SignalR hub together dissolve most of the "race conditions / throttling / memory leaks / real-time" work into the foundation.

The backend is not the problem and is largely not in scope. This is a rendering, state, routing, and real-time-client remake sitting on seams that already work.

---

## 1. Why we're doing this — confirmed against the source

The thesis held up under a full read of both tiers. Concrete evidence per defect:

1. **Manual DOM + HTML strings; escaping is a hand-discipline.** Every module builds markup with template literals + `innerHTML`, calling `escapeHtml`/`sanitizeUrl`/`escapeJs` per interpolation. `custom-domains.js` re-exposes `window._cdVerify`/`window._cdRemove` for inline `onclick=` — the exact hazard `escapeJs` exists to paper over.
2. **State in scattered module globals.** `marketplace-templates.js` holds `currentCategory`/`currentSearch`/`allTemplates`/`allCategories` as module `let`s; re-fetch-and-rebuild is the only sync.
3. **No real router.** `showPage()` toggles `.active` and dispatches to per-page `init`. No URLs; filter state lives in the DOM.
4. **`app.js` monolith** mixing AppKit, SIWE, `api()`, tri-state refresh, routing, balance, `applyAdminVisibility()`.
5. **Untyped, inconsistent boundary.** `MarketplaceController` returns raw objects; everyone else returns `ApiResponse<T>`. Enums arrive as numbers (default) *or* strings (hand-stringified). Hence `utils.js`'s dual maps and the admin `?? Name ?? name` reads. **Root cause now located precisely — see §13.**
6. **No component reuse.** Cards/modals/tables/badges/stars/toasts re-authored per module (two `renderStars` variants already exist).

---

## 2. What is NOT broken (preserve exactly — highest-priority invariants)

Confirmed correct and subtle in the source; port faithfully:

- **SIWE One-Click Auth (`siwe-config.js`).** Server nonce → wallet signs EIP-4361 → `POST /api/auth/wallet` verifies and recovers the address **server-side**. `credentials:'include'` throughout.
- **httpOnly refresh cookie (`dc_rt`).** Set by the server inside `verifyMessage`; **never seen by JS**. Access token in memory + localStorage mirror. Don't move the refresh token into JS-reachable storage.
- **Tri-state token refresh (`app.js`).** `true`/`false`/`null` (rotated / definitively rejected → logout / transport-uncertain → keep session). Port faithfully; test it.
- **Admin = visibility only.** `tokenHasAdminRole()` reveals nav; every admin endpoint is `[Authorize(Roles="Admin")]` server-side. Note the backend comment: role string is **capital-A "Admin"**, and a lowercase mismatch fails closed (Decision 8, fixed 2026-07-10/16) — keep the exact casing.
- **The `api()` contract.** One wrapper: bearer + `credentials:'include'` + on-401 refresh-and-retry-once.
- **Central error envelope.** `ErrorHandlingMiddleware` maps exceptions → `ApiResponse<object>.Fail(code,message)` with camelCase. The typed client can rely on this shape for *all* errors.
- **The ONE deploy path (`deploy-submit.js`).** ToS-gate retry-once, error-shape parsing, password-format sniff, centralized. Port as a typed hook; don't re-scatter.
- **Wallet-derived password crypto (`wallet-crypto.js`).** AES-256-GCM via `@noble/ciphers`, key = `sha256(signature)`, session-cached. Port verbatim behind a hook.
- **`makeModalAccessible` / `showToast` behavior (`utils.js`).** The *behavior* is the acceptance bar for the Radix replacements (§5.8).
- **Build → static → served by ASP.NET.** In Release, MSBuild runs `npm run build` and serves `wwwroot/dist/` with a SPA fallback to `index.html`; in Debug, the Vite dev server (port 3000) serves the frontend and .NET serves only the API. Keep this exact split (§5.12).
- **Reown AppKit + ethers**, the **terminal WebSocket** (raw WS proxy at `/api/terminal-proxy`, JWT via `token` query param), **USDC/escrow on Polygon**, the **CSS-variable tokens**.

---

## 3. Goals and non-goals

**Goals:** component model with automatic escaping; one source of truth for state; URL routing; a **typed client generated from the existing OpenAPI spec**; real-time via the **existing SignalR hub**; 100% parity across all entry points; preserve every §2 seam; a foundation that makes roadmap work cheap.

**Non-goals:** product/IA redesign; backend changes *except* the three small, now-specified items in §13; SSR/SEO; delivering roadmap viz/collaboration/trust features; NodeAgent (§11). All **Later** items (§6) stay later.

---

## 4. The locked stack

**React + TypeScript, Vite, static output served by ASP.NET from `wwwroot/dist`.**

| Layer | **Decision** | Grounding |
|---|---|---|
| Language | **TypeScript** | Kills the untyped-boundary tax (§1.5, §13). |
| Build/deploy | **Vite → static `wwwroot/dist`**, multi-page | Matches the existing MSBuild Release build + Debug dev-server split (§5.12). Three HTML entries (§4.1). |
| API types | **Generated from `/swagger/v1/swagger.json`** (openapi-typescript / orval) | Swashbuckle already serves it (§13-Q1). Not hand-maintained. |
| Router | **React Router v7 (SPA/data mode)** | Ubiquity + hiring; admin guards → loaders. TanStack Router is the switch-to for typed search params. |
| Server data + cache | **TanStack Query** | Dedup/cancellation/stale-while-revalidate/retry — dissolves most side-effect work (R12). |
| **Real-time** | **SignalR client → existing `OrchestratorHub` at `/hub/orchestrator`; terminal stays on the raw WS proxy** | Hub already has browser groups + VM events, JWT-authed (§13-Q3). |
| Client/session state | **React Context + a tiny store (Zustand only if it grows)** | Small by design (§5.3). No Redux backbone. |
| i18n | **FormatJS / react-intl** (ICU + `Intl`) | Greenfield; ICU/locale-format is its core (§5.6). |
| Headless primitives | **Radix UI** | Matches what `utils.js` hand-rolls (§5.8). |
| Styling | **CSS Modules + design tokens (CSS custom properties)** | Already themed via CSS vars; runtime light/dark, no re-render. No Tailwind by default (§5.5). |
| Testing | **Vitest + React Testing Library + Playwright** | Auth/`api` unit; money/lockout flows E2E (R14). |
| Rich text | **markdown lib + DOMPurify** | Replaces `renderMarkdown` regex over UGC (R17). |
| Wallet | **Reown AppKit React adapter + ethers** | Same flow; React adapter. |

### 4.1 Entry-point inventory (R1 parity)
1. **Main app** — `index.html` + `app.js` + feature/admin modules (the SPA).
2. **Node authorization signer** — `sign.js` + HTML: standalone, own AppKit init, parses `message`/`nodeId`/`wallet`/`hardware` from URL, produces a signature. Separate lifecycle and separate from app auth — keep it its own Vite entry.
3. **Abuse report** — `report.html` (public intake, rate-limited server-side).

Vite multi-page for all three.

---

## 5. Architecture & the sharpened product requirements

### 5.1 Component framework (R2, R6)
Declarative rendering, automatic escaping, reusable components, reactive updates — the four properties §1 defects 1/2/6 lack. Shared typed components collapse the per-module duplication.

### 5.2 Typed client + normalized boundary (R5, R5a)
**Generate** the client/types from the existing OpenAPI spec (§13-Q1). The typed `api()` then unwraps a **single** shape once the two §13 normalizations land:
- Backend wraps `MarketplaceController` in `ApiResponse<T>` (§13-Q2a) → one envelope everywhere.
- Backend registers `JsonStringEnumConverter` (§13-Q2b) → enums are stable string unions in the generated types; `utils.js`'s dual maps and the `?? Name ?? name` reads disappear.
Until/unless those land, the typed `api()` normalizes both shapes as a shim — but the backend fixes are small and recommended (§13), so the shim should be temporary.

### 5.3 State, split by kind (R3)
Server data → TanStack Query (replaces the module-global `let allTemplates`). Small client/session state (token, wallet, route, UI toggles) → Context/tiny store. **Rejected: a Redux-scale backbone** — the server-cache split already removes the sync problem it would solve; add later only for a concrete need.

### 5.4 Theme = a design-token layer (R7)
One source of truth for color/spacing/type/radii/shadow/motion/z-index; components consume **only tokens**. Formalize the existing CSS vars (`--text-muted`, `--border`, `--accent`, `--primary`…) into a role-based, **swappable** set (brand/white-label = config), **contrast-validated per theme** (§10).

### 5.5 Light/dark (R7) + styling mechanism
Two token sets swapped via CSS custom properties → **no re-render, no flicker**. **Must:** inline pre-paint script sets theme before first render; honor `prefers-color-scheme`; persist override; contrast passes in both modes. **CSS Modules over tokens** (a home for the inline-`style=` sprawl). No theme-via-CSS-in-JS. Tailwind only if the team wants it *and* it reads the tokens.

### 5.6 i18n — zh, ja, ru, fr, en, es, hi, tr (R8)
Greenfield. **FormatJS / react-intl.** ICU MessageFormat (Russian plural classes; **CJK have no plurals**); `Intl` number/currency/date (USDC, uptime %, Hindi lakh/crore); lazy-loaded locale bundles; deliberate CJK font strategy; **never translate** addresses/hashes/code/terminal output; build with **CSS logical properties** now (cheap future-RTL insurance); use FormatJS's extraction CLI for the migration.

### 5.7 Responsive / all devices / reactive (R9)
Mobile-first + **container queries**; touch targets ≥44px; no hover-only; tables collapse to cards (admin denylist/audit, custom-domains); safe-area insets. **Honest degraded surface:** terminal-on-phone is inherently awkward — usable-not-great.

### 5.8 Accessible headless primitives → Radix (R10) — Must
Radix Dialog/Toast/DropdownMenu/Tabs/Tooltip replace the hand-rolled `makeModalAccessible`/`showToast` with correct a11y and less assembly, styled with tokens. Target **WCAG 2.1 AA**; the current helpers' behavior is the acceptance bar. React Aria stays the pick if locale-aware input widgets later become central.

### 5.9 Real-time → the existing SignalR hub (R11) — Must
`OrchestratorHub` at `/hub/orchestrator` **already** exposes browser-facing `SubscribeToVm` / `SubscribeToNode` / `SubscribeToUser` (groups `vm:{id}`/`node:{id}`/`user:{id}`) and **already** broadcasts `VmStatusChanged`, `VmMetricsUpdated`, `VmAccessInfoUpdated`. JWT auth via `access_token` query param is wired in `Program.cs`. So the frontend work is: connect the SignalR client, subscribe on the relevant screens, and **map events → TanStack Query cache invalidations/patches**. SignalR's transport negotiation, reconnection, and groups come for free.
- **Ready today:** VM lifecycle + metrics + access-info push.
- **Small backend addition (§13-Q3):** user-scoped events like **balance changes** need a server-side emit to `user:{userId}` (the group exists; the broadcast on balance-change does not yet). Until added, keep balance on a light poll/refetch.
- **Terminal stays on the raw WS proxy** (`/api/terminal-proxy`) — a pty byte stream, not a hub message. (The hub also carries terminal-proxy methods; we don't need them for the browser terminal.)
- **SSE fallback: dropped** — the hub exists and is authed; there's no reason to consider it.

### 5.10 React side-effect discipline (R12)
`AbortController` cancels in-flight fetches (TanStack Query does most); clean up every subscription in `useEffect` (the existing AppKit `unsubscribers` arrays and the SignalR subscriptions become effect cleanups); debounce search (the marketplace's hand-rolled 300ms becomes a hook); throttle scroll/resize. **Over-memoization is a smell** — memoize on profiled cost, not reflexively.

### 5.11 Performance budgets, code-splitting, virtualization (R13) — Must-ish
Roadmap viz route-lazy-loaded; marketplace grids and admin queues virtualized; explicit budgets (initial JS, Core Web Vitals) in CI. Locale + viz bundles are the first split points.

### 5.12 Keep the static-build/dev-server split. No SSR. (Deliberate "don't build it")
The build model already exists and works: Release → MSBuild runs `npm run build`, ASP.NET serves `wwwroot/dist/` with SPA fallback; Debug → Vite dev server on :3000, .NET serves only the API. SSR would add a server to run/secure for SEO a login-walled app doesn't need. Keep it exactly.

---

## 6. Additional requirement categories (prioritized)

**Must**
- **R11 Real-time** via the existing hub — §5.9.
- **R10 Radix / WCAG 2.1 AA** — §5.8.
- **R13 Perf budgets + splitting + virtualization** — §5.11.
- **R15 Web3 / tx UX.** Wrong-network detect + switch (Polygon), tx pending/confirming/failed, a clear **"exactly what you're signing"** screen before any escrow signature (**never blind-sign**), graceful disconnect/account-switch. Carry `sign.js`'s mismatch + `ACTION_REJECTED`/4001 handling into React.

**Should**
- **R16 Error resilience.** Error boundary; skeletons/empty/error-retry states (standardize the ad-hoc `empty-state` blocks); reflect the tri-state "network unknown"; basic offline. The central error envelope (§2) gives one typed error shape to render.
- **R17 Frontend security beyond auth.** CSP (test vs AppKit/WalletConnect); lockfile + audit; **markdown lib + DOMPurify** over UGC (template descriptions, node profiles, reviews); keep `sanitizeUrl`'s allowlist; clickjacking headers.
- **R14 Testing pyramid.** Unit (tri-state refresh, `api()` unwrap); component (shared primitives); **Playwright E2E for connect-wallet, deploy-VM (incl. ToS-gate + password modal), pay/escrow**. CI-gated.
- **R18 Observability.** Privacy-respecting/self-hostable error + perf monitoring (no Google Analytics).
- **R19 Multi-tab consistency.** `signOutOnDisconnect` exists in SIWE config; extend cross-tab via storage events / BroadcastChannel.
- **R20 Feature flags.** Ship roadmap features dark.

**Later (deferred):** PWA/installability; Storybook.

---

## 7. Requirement checklist

R0 Preserve §2 seams · R1 Parity across all three entries (§4.1) · R2 Declarative rendering/escaping · R3 State split · R4 Routing + admin guards · R5 Typed boundary · **R5a Generated types + normalized envelope/enums (§13)** · R6 Component reuse · R7 Token theme, customizable/contrast-validated, light/dark · R8 i18n (8 locales, ICU, Intl, lazy, CJK, logical props) · R9 Responsive/mobile, container queries, terminal degraded · R10 Radix → WCAG 2.1 AA *(Must)* · R11 SignalR real-time via existing hub *(Must)* · R12 Side-effect discipline · R13 Perf/splitting/virtualization *(Must-ish)* · R14 Testing pyramid *(Should)* · R15 Web3/tx UX, never blind-sign *(Must)* · R16 Error resilience *(Should)* · R17 Security beyond auth *(Should)* · R18 Observability *(Should)* · R19 Multi-tab *(Should)* · R20 Feature flags *(Should)*

---

## 8. What we will explicitly NOT add (the fence)

- **No SSR / Node server layer** (§5.12).
- **No hand-rolled real-time socket** — the SignalR hub exists (§5.9).
- **No SSE fallback** — moot (§13-Q3).
- **No Redux-scale store** unless a concrete need appears (§5.3).
- **No large CSS/design-system framework** — tokens + Radix cover it.
- **No product redesign.**
- **No new backend endpoints to serve the frontend.** The only sanctioned backend changes are the three small items in §13.
- **No second wallet/auth path.** The `sign.js` signer is a distinct node-authorization flow — keep it separate, don't merge.
- **No inline `onclick`/`window._fn` wiring** — the migration removes the `window.*` bridge (§12).
- **No NodeAgent work** (§11).

---

## 9. Security requirements

Fail closed (tri-state refresh is the one deliberate exception; preserve exactly). Frontend is never a security boundary (server enforces `[Authorize(Roles="Admin")]`, capital-A). Refresh token stays httpOnly; preserve `wallet-crypto.js`. **Never blind-sign** (R15). Automatic escaping default; markdown via DOMPurify; keep `sanitizeUrl`. CSP + supply-chain hygiene. No secrets in the bundle (only the public WalletConnect project ID + public config).

---

## 10. Two tensions to hold

- **Feature breadth vs bundle size (esp. mobile).** i18n (8 locales + CJK), viz, Radix push weight up; mobile-ready pushes down. **Lazy-loading (locales, routes, viz) reconciles them** — why R13 is a Must.
- **Customizable theme vs accessibility.** A swappable palette can fail contrast. Define tokens **by role**, validate contrast **per theme** (R7).

---

## 11. NodeAgent operator dashboard — deferred

Separate vanilla-JS operator app (ports/routes/firewall), different audience. **Ratified: not brought under this stack now.** Token/i18n layers need not be portable to a second app yet.

---

## 12. Migration plan

**Incremental strangler.** 1) Stand up React+Vite+TS (multi-page) into the same build model. 2) **Port + freeze the core behind tests:** `api()` + generated-types unwrap, tri-state refresh, SIWE/AppKit (`siwe-config.js`), session restore, `wallet-crypto.js`, `deploy-submit.js`, `applyAdminVisibility`. 3) **Retire the `window.*` bridge** (`window.api`, `showPage`, `ethersSigner`, `templateDetail`, `handleDeployTosGate`, `showPasswordModal`, `copyToClipboard`, `_cdVerify/_cdRemove`, `marketplaceTemplates`) — one small PR each. 4) **Migrate page-by-page** behind the router, simplest first; each deletes its old module + inline handlers; wire SignalR subscriptions per screen as pages land. 5) **Cut over**, remove the page-div monolith. New file replacing an old one → delete the old one in the same change.

---

## 13. Backend questions — ANSWERED from source

### Q1 — OpenAPI generation → **already emitted; consume it.**
Swashbuckle.AspNetCore (6.5.0) is referenced; `Program.cs` calls `AddEndpointsApiExplorer()` + `AddSwaggerGen()` and `UseSwagger()`/`UseSwaggerUI()`, serving `/swagger/v1/swagger.json` in **all** environments. **Action:** generate the TS client/types from that spec (openapi-typescript or orval) — R5a types are generated, not hand-written. **No backend change required.** (The spec gets more accurate once Q2 lands.)

### Q2 — Contract normalization → **two small, precisely located fixes.**
**(a) Response envelope — one controller.** Every controller returns `ApiResponse<T>` (Terminal, Nodes, AdminCompliance, AdminAbuse, Abuse) and the error middleware emits `ApiResponse<object>.Fail(...)` — **except `MarketplaceController`**, which returns raw objects (`Ok(templates)`) and `new { error = ... }`. **Action:** wrap `MarketplaceController` in `ApiResponse<T>` to match. One file. Sequence it with the frontend rewrite (wrap server-side → regenerate types → new client reads the envelope); the typed `api()` shim covers the interim.
**(b) Enum serialization — one converter.** The Orchestrator's `AddJsonOptions` sets camelCase but registers **no `JsonStringEnumConverter`**, so enums serialize as **numbers** by default — while some endpoints hand-call `.ToString()` (e.g. `AbuseController` emits `Priority` as a string). That split is the sole reason for the frontend's dual int/string maps. **Action:** register `JsonStringEnumConverter` once — ideally in the shared `DeCloud.Shared/Json/JsonOptions.Wire` (the NodeAgent already uses it; converge the Orchestrator onto it instead of its inline options) — and drop the manual `.ToString()`. Enums then serialize as stable names everywhere; Swagger reflects it; generated TS becomes string-literal unions. It's a wire-format change, so coordinate — and the frontend rewrite is exactly when to do it.

### Q3 — SignalR vs SSE → **SignalR; the hub already exists and is browser-facing. SSE dropped.**
`OrchestratorHub` (`/hub/orchestrator`) already exposes `SubscribeToVm`/`SubscribeToNode`/`SubscribeToUser` (groups `vm:{id}`/`node:{id}`/`user:{id}`) and already broadcasts `VmStatusChanged`/`VmMetricsUpdated`/`VmAccessInfoUpdated`. JWT via `access_token` query param is wired in `Program.cs`. **Action:** connect the SignalR client, subscribe per screen, map events → TanStack Query cache updates.
- **Ready now:** VM lifecycle/metrics/access-info push.
- **Small backend addition:** emit to `user:{userId}` on **balance change** (group exists; broadcast doesn't yet). Until then, balance stays on a light poll.
- **Terminal** remains on the raw WS proxy.

**Net:** of the three, one needs **no** backend change (Q1), one is **one file + one converter** (Q2), and one is **already built** with a single small emit to add later (Q3). Scope shrank.

---

## 14. Remaining open items (small, for the API owner)
- Confirm ownership/sequencing of the two Q2 changes (MarketplaceController wrap; global enum converter) — recommend doing both at the start of the migration so generated types are correct from day one.
- Decide when to add the `user:{userId}` balance-change emit (Q3) — not blocking; balance can poll until then.
- Verify the generated OpenAPI schema is accurate for the currently-raw MarketplaceController endpoints after wrapping (regenerate + spot-check types).
