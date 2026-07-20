# DeCloud Frontend Remake — Design & Requirements

**Version:** 0.6
**Status:** React ratified; five stack decisions locked (§4); backend questions resolved (§13); contract normalization landed; balance-emit deferred with a plan. **This version adds the page/route structure and a public landing page** (§5.13), with the build model refined to cover it (§5.12).
**Scope:** The user-facing web frontend in `src/Orchestrator/wwwroot`. NodeAgent dashboard out of scope and deferred (§11).

**Changes since v0.5:**
- **Public landing page added**, and with it a **two-surface split**: a small *public, statically pre-rendered* surface (landing + the existing `report.html`) vs the *login-walled, client-rendered* SPA.
- **App namespaced under `/app/*`; the landing owns `/`** (the most valuable URL, and the marketing home).
- **New §5.13 — Page & route structure:** the full route tree, the reusable *modal-vs-route rule*, and the admin-guard consolidation.
- **§5.12 refined:** the landing is **SSG (build-time pre-render)** — static output, still **no running server**. This is a refinement of "no SSR," not a reversal of the fence.
- **Four IA decisions ratified** (§5.13): detail-as-route (yes); VM/Node detail pages (yes); SSH Keys folded under Settings; an in-app `/app/report` reusing the public form.

---

## 0. How to read this document

This guides *all* future frontend work, so it states what **not** to build as much as what to build. Every new layer earns its place: name the property it adds, then show the current system doesn't already provide it.

The backend read (v0.4) and the normalization landing (v0.5) proved "don't build what the foundation already provides": OpenAPI was already emitted, the real-time hub already existed and was browser-facing, and the only contract fixes were tiny. The landing page (v0.6) is the mirror-image lesson — a genuinely new need that must be *earned without smuggling back the weakness the fence removed*: it needs SEO, so it's pre-rendered **at build time** (SSG), which keeps the "no running server" fence intact (§5.12).

The backend is not the problem and is largely not in scope. This is a rendering, state, routing, and real-time-client remake on seams that already work.

---

## 1. Why we're doing this — confirmed against the source

1. **Manual DOM + HTML strings; escaping is a hand-discipline.** Template literals + `innerHTML`, per-interpolation `escapeHtml`/`sanitizeUrl`/`escapeJs`. `custom-domains.js` re-exposes `window._cdVerify`/`window._cdRemove` for inline `onclick=`.
2. **State in scattered module globals.** `marketplace-templates.js`'s `currentCategory`/`currentSearch`/`allTemplates`/`allCategories`; re-fetch-and-rebuild is the only sync.
3. **No real router.** `showPage()` toggles `.active`; no URLs; filter state lives in the DOM; admin guard is imperative and scattered.
4. **`app.js` monolith** mixing AppKit, SIWE, `api()`, tri-state refresh, routing, balance, `applyAdminVisibility()`.
5. **Boundary tax — removed at the source.** The old raw-vs-wrapper and numeric-vs-string-enum inconsistency has been normalized server-side (§13-Q2). The rewrite deletes `utils.js`'s dual maps rather than porting them.
6. **No component reuse.** Cards/modals/tables/badges/stars/toasts re-authored per module.

---

## 2. What is NOT broken (preserve exactly)

- **SIWE One-Click Auth (`siwe-config.js`).** Server nonce → wallet signs EIP-4361 → `POST /api/auth/wallet` verifies and recovers the address **server-side**. `credentials:'include'` throughout.
- **httpOnly refresh cookie (`dc_rt`).** Set by the server; **never seen by JS**. Access token in memory + localStorage mirror. Don't move the refresh token into JS-reachable storage.
- **Tri-state token refresh (`app.js`).** `true`/`false`/`null` (rotated / rejected → logout / uncertain → keep). Port faithfully; test it.
- **Admin = visibility only.** Endpoints are `[Authorize(Roles="Admin")]` (capital-A; lowercase fails closed — keep exact casing). The route guard is UX, not security (§5.13).
- **The `api()` contract.** Bearer + `credentials:'include'` + on-401 refresh-and-retry-once. Unwraps a single `ApiResponse<T>` (boundary now uniform).
- **Central error envelope.** `ErrorHandlingMiddleware` → `ApiResponse<object>.Fail(code,message)`, camelCase. One typed error shape.
- **The ONE deploy path (`deploy-submit.js`).** ToS-gate retry-once, error parsing, password sniff, centralized. Port as a typed hook.
- **Wallet-derived password crypto (`wallet-crypto.js`).** AES-256-GCM via `@noble/ciphers`, key = `sha256(signature)`, session-cached. Port verbatim behind a hook.
- **`makeModalAccessible` / `showToast` behavior (`utils.js`).** The behavior is the acceptance bar for the Radix replacements (§5.8).
- **Build/serve split.** Release → MSBuild `npm run build`, ASP.NET serves `wwwroot/dist/` + SPA fallback; Debug → Vite dev server :3000, .NET serves only the API. Refined (not replaced) for the landing in §5.12.
- **Reown AppKit + ethers**, the **terminal WebSocket** (raw WS proxy `/api/terminal-proxy`, JWT via `token` query param), **USDC/escrow on Polygon**, the **CSS-variable tokens**.

---

## 3. Goals and non-goals

**Goals:** component model with automatic escaping; one source of truth for state; **URL routing with a real route tree and a public landing page**; a typed client generated from the OpenAPI spec; real-time via the existing SignalR hub; 100% parity across all surfaces; preserve every §2 seam; a foundation that makes roadmap work cheap.

**Non-goals:** product/IA redesign beyond the ratified route structure (§5.13); further backend changes (only the deferred balance emit remains); **SSR as a running server** — but the **public landing is statically pre-rendered for SEO** (§5.12), and the authenticated app stays client-rendered with no SEO; delivering roadmap viz/collaboration/trust features; NodeAgent (§11). All **Later** items (§7) stay later.

---

## 4. The locked stack

**React + TypeScript, Vite, static output served by ASP.NET from `wwwroot/dist`.**

| Layer | **Decision** | Grounding |
|---|---|---|
| Language | **TypeScript** | Uniform boundary (§13-Q2 done); clean generated types. |
| Build/deploy | **Vite → static `wwwroot/dist`**, multi-surface; **landing pre-rendered via SSG** | Matches the Release-build / Debug-dev-server split; no running server (§5.12). |
| API types | **Generated from `/swagger/v1/swagger.json`** | Swashbuckle already serves it. |
| Router | **React Router v7 (SPA/data mode)** | Route tree + guards in §5.13. TanStack Router = switch-to for typed search params. |
| Landing SSG | **A build-time React pre-render (e.g. vite-react-ssg or equivalent)** | Reuses tokens/i18n/components; emits static HTML per locale (§5.13). |
| Server data + cache | **TanStack Query** | Dedup/cancellation/stale-while-revalidate/retry (R12). |
| **Real-time** | **SignalR client → existing `OrchestratorHub`; terminal on the raw WS proxy** | Hub already browser-facing, JWT-authed (§13-Q3). |
| Client/session state | **React Context + tiny store (Zustand only if it grows)** | Small by design (§5.3). |
| i18n | **FormatJS / react-intl** (ICU + `Intl`) | Greenfield; ICU/locale-format core (§5.6). |
| Headless primitives | **Radix UI** | Matches what `utils.js` hand-rolls (§5.8). |
| Styling | **CSS Modules + design tokens (CSS custom properties)** | Runtime light/dark; shared by landing + app (§5.5). |
| Testing | **Vitest + RTL + Playwright** | Auth/`api` unit; money/lockout E2E (R14). |
| Rich text | **markdown lib + DOMPurify** | Replaces `renderMarkdown` regex over UGC (R17). |
| Wallet | **Reown AppKit React adapter + ethers** | Same flow; React adapter. |

### 4.1 Surface inventory (R1 parity)
Four surfaces, two public + two authenticated:
1. **Public landing** — statically pre-rendered (SSG), owns `/` (+ locale path prefixes). SEO surface (§5.13, R21).
2. **Authenticated app** — client-rendered SPA under `/app/*`, including its own logged-out SIWE connect gate.
3. **Node authorization signer** — `sign.html`: standalone entry, own AppKit init, URL params → signature. Separate from app auth.
4. **Public abuse report** — `report.html`: standalone public entry (rate-limited server-side), also surfaced in-app at `/app/report` reusing the same form component.

Vite is configured for all four; the landing and app share the token/i18n/component layers.

---

## 5. Architecture & the sharpened product requirements

### 5.1 Component framework (R2, R6)
Declarative rendering, automatic escaping, reusable components, reactive updates. Shared typed components collapse the per-module duplication and are reused by the landing.

### 5.2 Typed client over a uniform boundary (R5, R5a)
Generate the client/types from `/swagger/v1/swagger.json`. The typed `api()` unwraps a single `ApiResponse<T>`; enums are string-literal unions. `utils.js`'s enum tables are deleted, not ported. Regenerate + spot-check now that the wrap + converter landed (§14).

### 5.3 State, split by kind (R3)
Server data → TanStack Query. Small client/session state (token, wallet, route, UI toggles) → Context/tiny store. **Rejected: a Redux-scale backbone** — the server-cache split already removes the sync problem.

### 5.4 Theme = a design-token layer (R7)
One source of truth for color/spacing/type/radii/shadow/motion/z-index; components consume **only tokens**. Role-based, **swappable** (brand/white-label = config), **contrast-validated per theme** (§10). Shared by landing and app so both look like one product.

### 5.5 Light/dark (R7) + styling mechanism
Two token sets swapped via CSS custom properties → no re-render, no flicker. **Must:** inline pre-paint script; honor `prefers-color-scheme`; persist override; contrast in both modes. **CSS Modules over tokens.** No theme-via-CSS-in-JS. Tailwind only if wanted *and* it reads the tokens.

### 5.6 i18n — zh, ja, ru, fr, en, es, hi, tr (R8)
**FormatJS / react-intl.** ICU (Russian plurals; CJK none); `Intl` number/currency/date; lazy locale bundles; CJK font strategy; **never translate** addresses/hashes/code/terminal output; **CSS logical properties** now (cheap future-RTL); extraction CLI for the migration. The landing is pre-rendered **per locale** with `hreflang` (§5.13) — a further reason SSG beats a client-only route for that surface.

### 5.7 Responsive / all devices / reactive (R9)
Mobile-first + **container queries**; touch targets ≥44px; no hover-only; tables collapse to cards; safe-area insets. Landing is mobile-first too (first impression on phones). **Honest degraded surface:** terminal-on-phone is inherently awkward — usable-not-great.

### 5.8 Accessible headless primitives → Radix (R10) — Must
Radix Dialog/Toast/DropdownMenu/Tabs/Tooltip replace hand-rolled `makeModalAccessible`/`showToast` with correct a11y, styled with tokens. Target **WCAG 2.1 AA**; current helpers' behavior is the bar. React Aria stays the pick if locale-aware input widgets later become central.

### 5.9 Real-time → the existing SignalR hub (R11) — Must
`OrchestratorHub` (`/hub/orchestrator`) already exposes `SubscribeToVm`/`SubscribeToNode`/`SubscribeToUser` groups and broadcasts `VmStatusChanged`/`VmMetricsUpdated`/`VmAccessInfoUpdated`, JWT-authed. Frontend work: connect, subscribe per screen, map events → TanStack Query cache updates. The **VM/Node detail routes (§5.13) are the natural home for per-resource subscriptions** — subscribe on mount, clean up on unmount.

- **Ready now:** VM lifecycle + metrics + access-info push.
- **Balance push — deferred, plan on record.** Poll for now. When added: one **contentless invalidation** to `user:{userId}` from the **`RecordUsageAsync` success path**; client (subscribed per this section) refetches the authoritative figure — never trusts a pushed amount. **Deposits stay client-driven** (client initiates → already knows to refetch). *Implementation note:* if `RecordUsageAsync` fires at metering frequency, **coalesce/debounce** the invalidation server-side (or throttle client refetch) to avoid a refetch storm.
- **Terminal** stays on the raw WS proxy (`/api/terminal-proxy`).
- **SSE fallback: dropped.**

### 5.10 React side-effect discipline (R12)
`AbortController` (TanStack Query does most); clean up every subscription in `useEffect` (AppKit `unsubscribers` and SignalR subscriptions become effect cleanups); debounce search; throttle scroll/resize. **Over-memoization is a smell.**

### 5.11 Performance budgets, code-splitting, virtualization (R13) — Must-ish
Roadmap viz route-lazy-loaded; grids/queues virtualized; budgets (initial JS, Core Web Vitals) in CI. **The landing has its own tight budget and must not pull in the app bundle** (§5.13). Locale + viz bundles split first.

### 5.12 Build model: static SPA + SSG landing. No running server. (Refined)
The existing model stands: Release → MSBuild `npm run build`, ASP.NET serves `wwwroot/dist/` with SPA fallback; Debug → Vite :3000, .NET API only. **Refinement for the landing:** the landing is **pre-rendered to static HTML at build time (SSG)** — real indexable markup per locale, emitted into `wwwroot/dist` like every other asset. This is *not* SSR: there is **no Node server at runtime**; the §8 fence (no running server process) is fully intact. A build-time pre-render is just another static output.

**Two-surface serving.** ASP.NET's static serving/fallback is refined (not replaced) to route: `/` and locale-prefixed landing paths → the pre-rendered landing HTML; `/app/*` → the app shell `index.html` (client-side routing takes over); `/sign.html`, `/report.html` → their entries; `/api/*`, `/hub/*` → backend. This aligns to the existing static-file seam rather than inventing a parallel one.

### 5.13 Page & route structure

**Two surfaces, one design system.**
- **Public** (unauthenticated, SEO-relevant, statically pre-rendered): the **landing** and the existing `report.html`.
- **Authenticated** (login-walled, client-rendered, no SEO): the SPA under `/app/*`, including its own logged-out SIWE connect gate (where the AppKit/SIWE machinery already lives).

**The modal-vs-route rule** (write it down so future pages inherit it instead of re-deciding):
> A **resource you can link to** (a specific template, VM, node) gets a **URL** — deep-linkable, shareable, back-button-correct, reload-safe. A **transient action on a resource** (deploy wizard, add-key, confirm-password, pick-constraints, add-domain) is an **overlay**. Render an overlay as a **modal-route** (its own path) when the back button should close it and the URL should survive reload; keep it a **plain local-state overlay** for quick confirms that *shouldn't* survive reload (e.g. the one-time password reveal).

**Route tree** (ratified):

```
PUBLIC (statically pre-rendered — SSG)
/                         Landing — SEO/OG, i18n per-locale (hreflang), tokened, tight budget
  /es /zh /ja /ru /fr /hi /tr …   locale-prefixed pre-rendered variants
                          CTAs: "Launch app" → /app · "Run a node" → docs · "Report abuse" → /report.html
/report.html              Public abuse report (standalone entry)
/sign.html                Node authorization signer (standalone entry)

AUTHENTICATED (client-rendered SPA)
/app                      App shell (requires session; no session → SIWE connect gate)
├─ /app                   Dashboard
├─ /app/marketplace       Template Marketplace (browse + filter; filters in the URL)
│  └─ /app/marketplace/:slug        Template detail        (was a modal → shareable route)
│     └─ /app/marketplace/:slug/deploy   Deploy wizard     (modal-route over detail)
├─ /app/my-templates      My Templates
│  └─ /app/my-templates/:id/edit    Edit / revise          (modal-route)
├─ /app/vms               Virtual Machines (list)
│  └─ /app/vms/:id        VM detail  (new — hosts per-VM SignalR subscription)
│     ├─ /app/vms/:id/terminal      Browser terminal       (route; raw WS)
│     ├─ /app/vms/:id/domains       Custom domains         (modal-route)
│     └─ /app/vms/:id/access        Direct access / ports  (modal-route)
├─ /app/nodes             Nodes (browse the network + filter)
│  └─ /app/nodes/:id      Node detail (new)
├─ /app/settings          Settings (profile; preferences: theme + language; ToS/compliance)
│  └─ /app/settings/ssh-keys        SSH Keys               (folded here as a tab)
├─ /app/report            Abuse report (in-app, reuses the public form component)
└─ /app/admin             Admin section — role guard on this layout; non-admin → /app
   ├─ /app/admin/compliance         Enforcement / takedown
   ├─ /app/admin/templates          Template review queue
   └─ /app/admin/abuse              Abuse report queue
```

**Ratified IA decisions** baked into the tree:
1. **Detail-as-route: yes.** Templates/VMs/nodes are addressable resources → URLs (delivers R4 deep-linking). Template detail was already a modal, so this is a low-risk upgrade.
2. **VM & Node detail pages: yes.** Today VMs/Nodes are flat tables with modal actions; the new detail pages are the scalable home for the growing per-resource surface (terminal, domains, access, metrics, future) and the natural mount point for per-resource SignalR subscriptions (§5.9). This is a deliberate IA change, not a straight port — recorded as such.
3. **SSH Keys folded under Settings** as `/app/settings/ssh-keys`. Minor consolidation; trivially reversible to a top-level nav item if usage shows friction.
4. **In-app `/app/report`** reuses the public abuse-report form component; `report.html` remains the public standalone entry.

**Admin guard consolidation.** The scattered imperative `if (!tokenHasAdminRole) showPage('dashboard')` becomes a **single guard on the `/app/admin` layout route** (non-admin → `/app`), same fail-closed behavior in one place. Server enforcement unchanged; still visibility-only.

**Locale routing.** Public landing uses **path-prefixed locales** (`/`, `/es/`, `/zh/`…) so crawlers get localized HTML with `hreflang`. The app uses a **stored preference** (no SEO need), defaulting from `Accept-Language`/`prefers` on first load.

**R21 — the landing's own requirements:** statically pre-rendered (SSG, no runtime server); semantic HTML + meta description + Open Graph/Twitter card + JSON-LD + `sitemap.xml` + `robots.txt` + canonical/`hreflang`; localized in all eight locales; built on the shared token/theme system with light/dark and mobile-first responsive; a tight performance budget that excludes the app bundle (minimal JS, lazy media); **public/fail-closed** — nothing privileged renders; the CTA leads *into* the SIWE gate. Optional progressive enhancement: a small client-side fetch for live network stats (nodes online, regions) after the static shell paints, plus a pricing section anchored on the existing tier tables (Guaranteed/Standard/Balanced/Burstable; Basic→Unmetered) — static-first, enhanced with a real number.

---

## 6. (reserved — see §5.13 for page/route structure)

---

## 7. Additional requirement categories (prioritized)

**Must:** R11 real-time via the existing hub (§5.9) · R10 Radix / WCAG 2.1 AA (§5.8) · R13 perf budgets + splitting + virtualization (§5.11) · R15 Web3/tx UX — wrong-network detect+switch, tx pending/confirming/failed, **"exactly what you're signing"** before any escrow signature (**never blind-sign**), graceful disconnect/account-switch · **R21 public landing (SSG/SEO/i18n/tokened/tight-budget/fail-closed)** (§5.13).

**Should:** R16 error resilience (boundary; skeleton/empty/error-retry; reflect tri-state "network unknown"; central error envelope gives one typed shape) · R17 security beyond auth (CSP vs AppKit/WalletConnect; lockfile+audit; **markdown+DOMPurify** over UGC; keep `sanitizeUrl` allowlist; clickjacking headers) · R14 testing pyramid (unit: tri-state refresh, `api()` unwrap; component: shared primitives; **E2E: connect-wallet, deploy-VM incl. ToS-gate+password modal, pay/escrow**) · R18 privacy-respecting observability · R19 multi-tab consistency (extend `signOutOnDisconnect` cross-tab) · R20 feature flags.

**Later (deferred):** PWA/installability; Storybook.

---

## 8. Requirement checklist

R0 Preserve §2 seams · R1 Parity across all four surfaces (§4.1) · R2 Declarative rendering/escaping · R3 State split · R4 Routing + route tree + admin guards (§5.13) · R5 Typed boundary · R5a Generated types over the uniform envelope/enums · R6 Component reuse · R7 Token theme, customizable/contrast-validated, light/dark · R8 i18n (8 locales, ICU, Intl, lazy, CJK, logical props) · R9 Responsive/mobile, container queries, terminal degraded · R10 Radix → WCAG 2.1 AA *(Must)* · R11 SignalR real-time via existing hub *(Must)* · R12 Side-effect discipline · R13 Perf/splitting/virtualization *(Must-ish)* · R14 Testing pyramid *(Should)* · R15 Web3/tx UX, never blind-sign *(Must)* · R16 Error resilience *(Should)* · R17 Security beyond auth *(Should)* · R18 Observability *(Should)* · R19 Multi-tab *(Should)* · R20 Feature flags *(Should)* · **R21 Public landing — SSG/SEO/i18n/tokened/fail-closed *(Must)*** (§5.13).

---

## 9. What we will explicitly NOT add (the fence)

No **running server** (no SSR/Node runtime) — the landing is **build-time SSG**, which honors this (§5.12) · No hand-rolled real-time socket — the hub exists (§5.9) · No SSE fallback · No Redux-scale store unless a concrete need appears (§5.3) · No large CSS/design-system framework — tokens + Radix cover it · No product redesign beyond the ratified route structure (§5.13) · **No further backend endpoints** — only the deferred balance emit remains · No second wallet/auth path (the `sign.html` signer is a distinct node-authorization flow — keep separate) · No inline `onclick`/`window._fn` wiring — the migration removes the `window.*` bridge (§12) · No NodeAgent work (§11).

---

## 10. Security requirements

Fail closed (tri-state refresh is the one deliberate exception). Frontend is never a security boundary (server enforces `[Authorize(Roles="Admin")]`, capital-A; route guards are UX). Refresh token stays httpOnly; preserve `wallet-crypto.js`. **Never blind-sign** (R15). Automatic escaping default; markdown via DOMPurify; keep `sanitizeUrl`. Balance push (when added) is **contentless** — client refetches the authoritative figure (§5.9). The **landing is public/fail-closed** — nothing privileged renders; CTA leads into the SIWE gate (§5.13). CSP + supply-chain hygiene. No secrets in the bundle (only the public WalletConnect project ID + public config).

---

## 11. Two tensions to hold

- **Feature breadth vs bundle size (esp. mobile).** i18n (8 locales + CJK), viz, Radix push weight up. **Lazy-loading reconciles them** — why R13 is a Must. The landing's separate, tight budget keeps the first impression fast.
- **Customizable theme vs accessibility.** Define tokens **by role**, validate contrast **per theme** (R7).

---

## 12. NodeAgent operator dashboard — deferred

Separate vanilla-JS operator app (ports/routes/firewall), different audience. **Ratified: not brought under this stack now.** Token/i18n layers need not be portable to a second app yet.

---

## 13. Migration plan

**Incremental strangler.**
1. Stand up React+Vite+TS (multi-surface) into the existing build model; add the **SSG pre-render** for the landing and refine the ASP.NET static serving/fallback for the two-surface split (§5.12).
2. **Port + freeze the core behind tests:** `api()` (single-envelope unwrap), tri-state refresh, SIWE/AppKit (`siwe-config.js`), session restore, `wallet-crypto.js`, `deploy-submit.js`, `applyAdminVisibility`.
3. **Build the landing early.** It's public, static, and touches no auth — a low-risk first deliverable that proves the token/i18n/SSG pipeline before the app migration.
4. **Retire the `window.*` bridge** (`window.api`, `showPage`, `ethersSigner`, `templateDetail`, `handleDeployTosGate`, `showPasswordModal`, `copyToClipboard`, `_cdVerify/_cdRemove`, `marketplaceTemplates`) — one small PR each.
5. **Migrate page-by-page** behind the router per §5.13, simplest first; each deletes its old module + inline handlers; wire SignalR subscriptions on the VM/Node detail routes as they land.
6. **Cut over**, remove the page-div monolith; new file replacing an old one → delete the old one in the same change.

---

## 14. Backend questions — resolved

**Q1 — OpenAPI: already emitted.** Swashbuckle serves `/swagger/v1/swagger.json` in all environments. Generate the TS client/types from it. No backend change.

**Q2 — Contract normalization: DONE.** `MarketplaceController` now returns `ApiResponse<T>`; a global `JsonStringEnumConverter` is registered. Uniform envelope + string enums. The frontend drops `utils.js`'s dual maps and the `api()` shim.

**Q3 — Real-time: SignalR; hub already browser-facing.** Adoption is connect + subscribe + map to cache. **Balance push deferred** with an agreed plan (§5.9). SSE dropped.

---

## 15. Remaining open items (small)
- **Regenerate + spot-check the OpenAPI-derived types** now that the wrap + enum converter landed — do it at the start of the migration.
- **Balance-change emit** (§5.9) — implement when prioritized; non-blocking. Plan on record.
- **Landing content + copy** — the requirements are set (R21); the actual marketing copy, hero, and pricing framing are a content task for whoever owns product/marketing, not a design-doc decision.
- **App namespace hosting** — `/app/*` under the same origin (assumed here) vs a future `app.` subdomain is a DNS/hosting call, deferrable; the route tree is unaffected either way.
