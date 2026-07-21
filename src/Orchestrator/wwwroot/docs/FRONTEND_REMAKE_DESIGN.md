# DeCloud Frontend Remake — Design & Requirements

**Version:** 0.8 — *restructured to lead with purpose; terminal/file-browser folded into in-app routes*

> **v0.8 change:** grounding the serving layer found six standalone HTML entries, not four. Resolved: **terminal & file browser become in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) with pop-out preserved (chromeless variant) — §2, §3, §4.1, §7, §10. `sign.html`, `report.html`, `tos.html` stay standalone. No technical blocker (plain xterm/WS; no COOP/COEP). A `/design-tokens.css` already exists to build the token layer on.
**Status:** Purpose layer settled (critical path, spine, dual-lifecycle, failure taxonomy, state model, parity, success criteria). Construction layer locked and grounded in the real code. Backend contract normalized; balance-emit deferred with a plan.
**Scope:** The user-facing web frontend in `src/Orchestrator/wwwroot`. NodeAgent operator dashboard is a *different product for a different user* — out of scope, deferred (§13).

**Why this version is a restructure, not an append.** Through v0.6 the document answered *how we build* with rigor but under-answered *what it's for*. A step-back stress test found the crown-jewel flow — the reason the front-end exists — was the least-designed thing in it. v0.7 fixes the ordering: it opens with the critical path and the spine (§1–§3), then the models that make the spine correct under real conditions (§4–§5), and only then the construction stack (§6+), which is now explicitly "how," subordinate to "what."

---

## 1. Who this is for, and the one critical path

**Primary user:** a *platform user who creates workloads* — someone turning a wallet into a running, accessible compute workload. This is the backbone the whole front-end serves.

**The critical path:** **connect → ⟨fund⟩ → deploy → operate.** Funding is a **standing precondition**, satisfied once and replenished on depletion — a *guard beside* the path, not a phase inside it. So the spine is really **connect → deploy → operate**, with a fund guard at the threshold and a low-balance warning during operate.

**Supporting paths, deliberately subordinate** (they must not get in the spine's way, and they stay plain — this is the license to *not* gold-plate them): template creation (a workload *becomes* deployable — feeds the spine, isn't on it), the admin path (governs the platform, doesn't use it), and the NodeAgent dashboard (operators *supplying* capacity — a different product, §13).

**Everything downstream is ranked by this.** The spine gets the most design effort, the tightest failure handling, and whatever real-time genuinely earns its place. Supporting paths get correctness and no more.

---

## 2. The deploy-and-operate spine

Grounded in the real deploy logic (`template-detail.js`, `deploy-submit.js`, `constraint-builder.js`, `payment.js`, `vm-modals.js`, `TemplateService.BuildVmRequestFromTemplateAsync`).

**What deploy is today:** template-anchored; a single dense modal collecting name + full spec + GPU + quality/bandwidth tiers + replication + constraints + user variables + a live $/hr estimate; validates name (shared `VmNameService`) and spec vs the template's `minimumSpec`; hides platform-resolved variables; routes through the one deploy path (ToS-gate retry-once); reveals a generated password once; lands on the VM **list**. Three gaps drove the redesign: the deploy form **never checks balance** (the fund guard is absent from the UI, only server-enforced); cost is shown as **$/hr, never runway**; and the handoff drops you on a **list, not the workload you just made**.

**The spine (ratified):**

- **Connect** — SIWE one-click via AppKit (solved). Landing CTA → `/app` → connect gate if no session. No new design.
- **Fund guard** *(beside the spine)* — a **hard gate**: an empty balance intercepts *before* the deploy form, not after a full configuration. Plus a **soft runway indicator** inside deploy: cost shown as "this workload runs ~N days on your balance," with a top-up link when it won't cover a sensible minimum. Deploy is otherwise silent about money.
- **Deploy** — the hot path is **one click: "Deploy with recommended settings"** (the template already carries `RecommendedSpec`, so the default exists in the data). Spec/tiers/GPU/constraints/variables collapse into an opt-in **Customize** area. Complexity is available, not mandatory. Preserved verbatim: name validation, min-spec validation, platform-variable hiding, locked/editable/user constraint rows, ToS-gate retry, cost estimate (now also runway). Deploy is a **route** (`/app/marketplace/:slug/deploy`) — linkable, resumable, and form state survives a mid-flow re-auth (§4).
- **Handoff** — the one-time **password reveal** is a plain overlay that must *not* survive reload (a clean instance of the modal-vs-route rule, §7). Then **land on the new VM's detail page** (`/app/vms/:id`) — you just made it; operate begins; watch it boot.
- **Operate** — the VM detail page is the **cockpit**: live status/health (**where SignalR genuinely earns its subscription** — subscribe per-VM on mount, clean up on unmount), the access panel (SSH keys, direct-access ports, custom domains), the **browser terminal and file/SFTP browser** (in-app routes, see below), metrics, lifecycle actions (stop/restart/destroy), and a **runway indicator** tying draining balance back to the fund guard.
  - *Terminal & file browser (grounded decision):* today these are standalone `terminal.html`/`file-browser.html` pages opened via `window.open('_blank')` — the deliberate reason being **pop-out / multi-window** (keep a terminal open while navigating, several at once, second monitor). They become **in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) — tokened by the session, styled by the design system, xterm bundled + lazy-loaded (off the initial bundle) rather than CDN — **while preserving pop-out**: because a route is a URL, it opens in a new tab via a **chromeless layout variant** (no sidebar). No blocker exists (plain xterm over WS; no SharedArrayBuffer/COOP-COEP). The WS auth mechanism is preserved as-is (see §10).

**What the spine does to the information architecture.** A sidebar of ten equal items is wrong for a spine-shaped product. The **Dashboard becomes the operate + fund home** — running workloads and their runway — with **"Deploy" promoted to a primary, always-available action** instead of buried three levels deep (marketplace → template → modal). Marketplace, My Templates, and Nodes recede to "sources you deploy from." Admin, template authoring, and NodeAgent stay off-spine and plain.

**Where real-time earns its place** (falls out of the spine, not sprayed everywhere): **VM detail** (status/metrics — live) and the **dashboard running-workloads list** (status — live). Balance/runway rides the deferred emit (polls until then, §6.9). Everywhere else, TanStack Query polling is simpler and sufficient.

**Re-tested over-builds (against the critical path):**
- **VM detail page — earned.** It's the deploy-handoff target, the operate cockpit, and the real-time home. The path demands it; not an IA nicety.
- **Node detail page — not earned.** Nodes are supporting (browse; maybe reference as deploy constraints). Downgraded to browse/list; add detail only if a real need appears. (Corrects the v0.6 assertion.)
- **Real-time everywhere — killed.** Scoped to VM detail + dashboard status.
- **All-eight-locales-at-launch — flagged (§6.6):** architecture must support eight; *launching* all eight before there are users in those markets is possible gold-plating. Recommend i18n-ready architecture now, phased locale rollout — no locale dropped, none front-loaded.

---

## 3. Route tree (spine-centered)

The v0.6 tree, with emphasis corrected: Deploy promoted, Dashboard as operate+fund home, Node detail downgraded.

```
PUBLIC (statically pre-rendered — SSG, no runtime server; §6.12)
/                         Landing — SEO/OG, i18n per-locale (hreflang), tokened, tight budget
  /es /zh /ja /ru /fr /hi /tr …   locale-prefixed pre-rendered variants
                          CTAs: "Launch app" → /app · "Run a node" → docs · "Report abuse" → /report.html
/report.html              Public abuse report (standalone entry)
/sign.html                Node authorization signer (standalone entry)

AUTHENTICATED (client-rendered SPA)
/app                      Shell (requires session; no session → SIWE connect gate)
├─ /app                   Dashboard — operate + fund home; primary "Deploy" action
├─ /app/marketplace       Template Marketplace (browse + filter; filters in the URL)
│  └─ /app/marketplace/:slug        Template detail
│     └─ /app/marketplace/:slug/deploy   Deploy (route; one-click recommended + Customize)
├─ /app/my-templates      My Templates → /:id/edit (modal-route)
├─ /app/vms               Virtual Machines (list)
│  └─ /app/vms/:id        VM detail — operate cockpit; per-VM SignalR
│     ├─ /app/vms/:id/terminal      Browser terminal (route; WS; pop-out-able, chromeless variant)
│     ├─ /app/vms/:id/files         File browser / SFTP (route; WS; pop-out-able, chromeless variant)
│     ├─ /app/vms/:id/domains       Custom domains (modal-route)
│     └─ /app/vms/:id/access        Direct access / ports (modal-route)
├─ /app/nodes             Nodes (browse + filter)   ← browse-only; no detail page
├─ /app/settings          Settings (profile; theme + language; ToS/compliance)
│  └─ /app/settings/ssh-keys        SSH Keys (folded here)
├─ /app/report            Abuse report (in-app; reuses the public form component)
└─ /app/admin             Admin — role guard on this layout; non-admin → /app
   ├─ /app/admin/compliance · /app/admin/templates · /app/admin/abuse
```

**Modal-vs-route rule** (write it down so future pages inherit it): *a resource you can link to gets a URL (deep-linkable, back-safe, reload-safe); a transient action is an overlay — a modal-route when the back button should close it and the URL should survive reload, a plain local-state overlay for quick confirms that shouldn't survive reload (e.g. the one-time password reveal).*

**Admin guard consolidation:** the scattered imperative `if (!tokenHasAdminRole) showPage('dashboard')` becomes one guard on the `/app/admin` layout (non-admin → `/app`). Server still enforces; visibility-only.

---

## 4. Wallet ↔ session: two lifecycles, one derived status

The old doc collapsed this into "Context holds token + wallet." The code is already more careful, and the design must model what the code knows. **Two independent machines:**

**Wallet (client — AppKit/ethers):** `Disconnected → Connecting → Connected(address, chainId)`, plus **WrongNetwork** (connected but not Polygon, where USDC/escrow lives) and **account-switched** (address changes underneath you).

**Session (server — SIWE/JWT):** `Anonymous → Authenticating → Authenticated(token,user) → Refreshing → Authenticated | Expired`, governed by the **tri-state refresh**: `true` re-arms, `false` → Expired, `null` *keeps* Authenticated (evidence of death was unverifiable).

**The shell reads one derived status** — the single source of truth that generalizes the scattered handling (`signOutOnDisconnect`, tri-state, stale-balance, mismatch warning) instead of re-deciding per screen:

`READY | NEEDS_CONNECT | NEEDS_AUTH | WRONG_NETWORK | ADDRESS_MISMATCH | UNCERTAIN`

**Ratified transitions:**
- **READY** = Connected + Authenticated + session address + Polygon. The only fully-operational state.
- **Connected + Expired → NEEDS_AUTH:** the wallet is right there — re-sign (SIWE), don't cold-start at connect.
- **refresh `null` → UNCERTAIN:** keep last-known data, mark stale, retry. Never destroy state on unverifiable evidence. (This is the tri-state philosophy *and* the stale-balance handling generalized — §5.)
- **Disconnected + Authenticated → end the session** (`signOutOnDisconnect: true`). Ratified: **identity is the wallet** — now an explicit decision, not an accident of config.
- **Account switch A→B → NEEDS_AUTH as B** (prompt to sign in as the new address). **Fail closed: B never operates A's workloads.**
- **WrongNetwork → blocks only fund/escrow actions** and prompts a switch; view/operate still work. Not a whole-app gate.
- **Mid-flow session expiry → re-auth but preserve in-progress form state** (the configured deploy). Failing closed must not cost the user their work.

---

## 5. Failure taxonomy

**Three kinds** (different UI; naming them stops ad-hoc handling):
1. **Cancel** — rejected signature/tx (`ACTION_REJECTED`/4001). *Not an error*; quietly return to prior state.
2. **Uncertain** — RPC blip, transport failure, refresh `null`. **Keep last-known, mark stale/degraded, retry; never destroy state on unverifiable evidence.** (The generalization of the tri-state refresh and stale-balance handling — the right existing instinct, made the app-wide rule.)
3. **Definitive** — server rejects, tx failed on-chain, refresh `false`, validation. Clear, actionable error.

**Along the spine:**
- **Connect:** signature rejected (cancel); nonce/verify transient (uncertain → retry); no wallet (guide to install/WalletConnect).
- **Fund guard:** wrong network (prompt switch, block action); insufficient balance (the guard — expected, not an error); **top-up tx lifecycle — pending → confirmed → failed → dropped → rejected** each a designed state (a pending-forever/silently-dropped tx is the classic crypto trap); balance-read RPC lag (uncertain).
- **Deploy:** ToS lapsed (gate + retry); name/min-spec (inline, pre-submit); late server reject — insufficient balance at submit / no capacity / template gone (definitive, clear message); session expiry mid-deploy (`api()` refresh-retries once; a definitive rejection re-auths **without losing the configured deploy**).
- **Operate:** VM enters error/stopped (live status shows it — this is where a *provisioning* failure surfaces, not an infinite spinner); **balance hits zero while running → suspended-for-nonpayment, shown as itself**, pre-empted by the runway indicator + low-balance warning (what the deferred emit feeds); SignalR drop (built-in reconnect → fall back to poll → show "reconnecting", never stale-forever); terminal WS drop (reconnect/notify); per-action access failures (inline, as `custom-domains.js` already does).

---

## 6. Construction — how we build it (subordinate to §1–§5)

### 6.0 The locked stack
**React + TypeScript, Vite, static output served by ASP.NET from `wwwroot/dist`; landing pre-rendered via SSG.** React chosen for the roadmap's viz/real-time/on-chain weight and the largest contributor pool; the added machinery is contained by leaning on the data/real-time layers.

| Layer | Decision |
|---|---|
| Language | TypeScript |
| Build/deploy | Vite → static `wwwroot/dist`, multi-surface; landing SSG (§6.12) |
| API types | Generated from `/swagger/v1/swagger.json` (Swashbuckle already serves it) |
| Router | React Router v7 (SPA/data mode); guards per §3 |
| Landing SSG | Build-time React pre-render (e.g. vite-react-ssg) |
| Server data + cache | TanStack Query |
| Real-time | SignalR client → existing `OrchestratorHub`; terminal on raw WS proxy |
| Client/session state | React Context + one tiny store for the session machine (§4) — no Redux backbone |
| i18n | FormatJS / react-intl (ICU + `Intl`) |
| Headless primitives | Radix UI |
| Styling | CSS Modules + design tokens (CSS custom properties) |
| Testing | Vitest + RTL + Playwright |
| Rich text | markdown lib + DOMPurify |
| Wallet | Reown AppKit React adapter + ethers |

### 6.1 Component model (R2, R6)
Declarative rendering, automatic escaping, reusable typed components (cards/modals/tables/badges/stars/toasts once, reused by the landing). Collapses the per-module duplication.

### 6.2 Typed client over a uniform boundary (R5)
Generate from OpenAPI; `api()` unwraps a single `ApiResponse<T>`; enums are string-literal unions. `utils.js`'s dual maps deleted, not ported (backend normalization landed, §12-Q2).

### 6.3 State, split by kind (R3) — proven, not asserted (§ Step-4 enumeration)
**Server data** (templates, VMs+detail+metrics+access, nodes, SSH keys, balance/usage, admin queues, domains, ports) → TanStack Query, keyed by resource, invalidated by SignalR/refetch. **Client state is small and concentrated:** the *session/identity machine* (token, address, chainId, derived status — §4) is the only part with real logic and gets the one tiny store; everything else is ephemeral/UI (route, theme, locale, open modal-route, form drafts incl. the preserved deploy config, toasts). The claim survives enumeration — small and concentrated, not trivial; the session machine is frozen and tested first (§11).

### 6.4 Theme = a design-token layer (R7)
Role-based tokens (color/spacing/type/radii/shadow/motion/z-index); components consume only tokens; **swappable** (brand/white-label = config), **contrast-validated per theme**. Shared by landing and app.

### 6.5 Light/dark (R7)
Token-set swap via CSS custom properties → no re-render, no flicker. Inline pre-paint script; honor `prefers-color-scheme`; persist override; contrast in both modes. CSS Modules over tokens; no theme-via-CSS-in-JS.

### 6.6 i18n — zh, ja, ru, fr, en, es, hi, tr (R8)
FormatJS/react-intl; ICU (Russian plurals; CJK none); `Intl` number/currency/date (USDC, uptime %, Hindi lakh/crore); lazy locale bundles; CJK font strategy; **never translate** addresses/hashes/code/terminal output; **CSS logical properties** now (cheap future-RTL); landing pre-rendered per-locale with `hreflang`. **Phased rollout:** architecture supports all eight; launch set is a product call — don't front-load all eight before there are users for them (no locale dropped).

### 6.7 Responsive / mobile (R9)
Mobile-first + container queries; touch ≥44px; no hover-only; tables collapse to cards; safe-area insets. **Honest degraded surface:** terminal-on-phone is inherently awkward — usable-not-great.

### 6.8 Accessible primitives → Radix (R10)
Radix Dialog/Toast/DropdownMenu/Tabs/Tooltip replace hand-rolled `makeModalAccessible`/`showToast`, styled with tokens; target WCAG 2.1 AA; the current helpers' behavior is the bar.

### 6.9 Real-time → existing SignalR hub (R11)
`OrchestratorHub` already exposes `SubscribeToVm/Node/User` and broadcasts `VmStatusChanged/VmMetricsUpdated/VmAccessInfoUpdated`, JWT-authed. Scope (per §2): VM detail + dashboard status. Map events → Query cache updates; subscribe on mount, clean up on unmount. **Balance push deferred:** poll now; when added, one **contentless invalidation** from `RecordUsageAsync`'s success path to `user:{userId}` → client refetches the authoritative figure (never trusts a pushed amount); deposits stay client-driven; coalesce if metering is frequent. Terminal stays on the raw WS proxy. SSE dropped.

### 6.10 Side-effect discipline (R12)
`AbortController` (Query does most); clean up every subscription in `useEffect` (AppKit `unsubscribers`, SignalR subs → effect cleanups); debounce search; throttle scroll/resize. **Over-memoization is a smell.**

### 6.11 Performance (R13)
Roadmap viz route-lazy-loaded; grids/queues virtualized; budgets (initial JS, Core Web Vitals) in CI; landing has its own tight budget and never pulls the app bundle; locale + viz split first.

### 6.12 Build model: static SPA + SSG landing. No running server.
Release → MSBuild `npm run build`, ASP.NET serves `wwwroot/dist/` + SPA fallback; Debug → Vite :3000, .NET API only. Landing **pre-rendered at build time (SSG)** — indexable static HTML per locale, no runtime Node server (the §9 fence holds). Two-surface serving: `/` + locale paths → landing HTML; `/app/*` → app shell; `/sign.html`,`/report.html` → entries; `/api/*`,`/hub/*` → backend.

---

## 7. Surfaces & parity

**Surfaces (grounded against `vite.config.js` + `.sln`).** The authenticated SPA (`/app/*`, incl. its logged-out connect gate) now **absorbs the terminal and file browser** as in-app routes (was standalone `terminal.html`/`file-browser.html`). Remaining **standalone entries**, kept standalone because each is reached cold/anonymously or needs a stable public URL: `sign.html` (node signer), `report.html` (public abuse report; also in-app at `/app/report`), `tos.html` (Terms — behind the deploy ToS gate and linkable publicly). Plus the SSG landing at `/`. *(A `/design-tokens.css` already exists in the frontend — the token layer builds on it, not from scratch.)*

**Parity inventory (the cutover checklist — verify per migrated page before deleting the old module):**
- **Auth/session:** SIWE nonce→sign→verify; httpOnly refresh cookie untouched by JS; tri-state refresh; `signOutOnDisconnect`; account-switch re-auth; wallet-mismatch warning; admin-visibility (capital-A).
- **Deploy:** shared name validation; min-spec validation; platform-variable hiding; locked/editable/user constraints; ToS-gate retry; one-time password reveal; recommended default + Customize; runway indicator; hard fund gate; land-on-new-VM-detail.
- **Operate:** per-VM SignalR status/metrics; **terminal** (xterm; resize→WS message protocol; token-via-`token`-query on `/api/terminal-proxy`; password-to-connect; reconnect; pop-out) and **file/SFTP browser** (`/api/sftp-proxy`; drag-and-drop upload; context menu; keyboard shortcuts; pop-out); direct-access ports (quick-add + custom); custom domains (add/verify/remove, DNS/CNAME instructions, status labels); stop/restart/destroy; suspended-for-nonpayment shown as itself.
- **Wallet-crypto:** AES-GCM encrypt/decrypt with the wallet-derived key (verbatim).
- **Marketplace/templates:** category/search/GPU/sort filters (URL-driven); template detail; My Templates; repo-deploy → the one deploy path.
- **Admin:** compliance (suspend/block/bulk/VM-hold + tables); abuse (dismiss/warn/takedown + CSAM hash-check; NotScanned never shown as clean); template review (approve/reject).
- **Cross-cutting:** stale-but-keep-showing as the general "uncertain" pattern; toast semantics; Radix modal a11y; `sanitizeUrl` allowlist; the three separate entries.

---

## 8. Success criteria (how we know it worked)

- **Maintainability:** a feature (or a shared-component change) touches *one* place, not three files + the monolith — measured on the first post-migration features.
- **Critical-path speed:** landing TTI; clicks-to-deployed on the spine (recommended-settings deploy = one click past the template).
- **Correctness-by-construction:** XSS is framework-owned (no hand-escaping in feature code); the §4 auth state machine and `api()` unwrap are unit-tested; the three money/lockout flows (connect, deploy, pay/escrow) are E2E-gated in CI.
- **Parity:** the §7 checklist passes at cutover — zero known-behavior regressions.

---

## 9. The fence — what we will NOT add

No running server (SSR/Node) — landing is build-time SSG (§6.12) · No hand-rolled real-time socket — the hub exists (§6.9) · No SSE fallback · No Redux-scale store — client state is small and concentrated (§6.3) · No large CSS/design-system framework — tokens + Radix cover it · No product redesign beyond the ratified spine/IA (§2–§3) · No further backend endpoints — only the deferred balance emit remains · No second wallet/auth path (the `sign.html` signer is a distinct node-authorization flow) · No inline `onclick`/`window._fn` wiring — the migration retires the `window.*` bridge (§11) · No NodeAgent work (§13) · No real-time beyond VM detail + dashboard status (§2) · No node detail page until a real need appears (§2).

---

## 10. Security requirements

Fail closed (the tri-state `null`→UNCERTAIN is the one deliberate exception — keep, don't generalize into laxness). Frontend is never a security boundary (server enforces `[Authorize(Roles="Admin")]`, capital-A; route guards are UX). Refresh token stays httpOnly; preserve `wallet-crypto.js` verbatim. **Account switch fails closed** — B never operates A's workloads (§4). **Never blind-sign** — a clear "what you're signing" screen before every escrow signature; carry `sign.js`'s mismatch + `ACTION_REJECTED`/4001 handling. Automatic escaping default; markdown via DOMPurify; keep `sanitizeUrl`. Balance push (when added) is contentless — client refetches the authoritative figure (§6.9). Landing is public/fail-closed. CSP + supply-chain hygiene. No secrets in the bundle (only the public WalletConnect project ID + public config). **Terminal/SFTP WS auth:** the access token (and VM password) ride in the WS URL query (`?token=…`) because browser WebSockets can't set an `Authorization` header — `Program.cs` `OnMessageReceived` reads `token` for `/api/terminal-proxy` and `/api/sftp-proxy`. This is preserved as-is; keep the existing discipline of **never logging the token/password**, and treat the code's `// TODO: short-lived ticket` as a future hardening that needs backend support (out of scope now).

---

## 11. Migration plan

Incremental strangler.
1. Stand up React+Vite+TS (multi-surface) into the existing build model; add SSG for the landing; refine the two-surface static serving (§6.12).
2. **Port + freeze the core behind tests:** the §4 session/identity machine (derived status, tri-state refresh, `signOutOnDisconnect`, account-switch, WrongNetwork), `api()` single-envelope unwrap, SIWE/AppKit (`siwe-config.js`), session restore, `wallet-crypto.js`, `deploy-submit.js`, `applyAdminVisibility`. Unit-test the state machine + guards.
3. **Build the landing early** — public, static, no auth: a low-risk first deliverable that proves the token/i18n/SSG pipeline.
4. **Build the spine next** — dashboard(operate+fund) → deploy(recommended+Customize) → VM detail(operate). This is the product; it comes before supporting paths.
5. **Retire the `window.*` bridge** (`window.api`, `showPage`, `ethersSigner`, `templateDetail`, `handleDeployTosGate`, `showPasswordModal`, `copyToClipboard`, `_cdVerify/_cdRemove`, `marketplaceTemplates`) — one small PR each.
6. **Migrate remaining pages** (marketplace, my-templates, nodes, settings, admin) behind the router; each deletes its old module + inline handlers; verify §7 parity per page.
7. **Cut over**, remove the page-div monolith; a new file replacing an old one deletes the old one in the same change.

---

## 12. Backend questions — resolved
**Q1 — OpenAPI:** already emitted (Swashbuckle, `/swagger/v1/swagger.json`). Generate types. No change.
**Q2 — Contract normalization: DONE.** `MarketplaceController` now returns `ApiResponse<T>`; global `JsonStringEnumConverter` registered. Uniform envelope + string enums; frontend drops the dual maps and the shim.
**Q3 — Real-time:** SignalR; hub already browser-facing. Adopt + subscribe (scoped per §2). Balance push deferred with a plan (§6.9). SSE dropped.

---

## 13. NodeAgent operator dashboard — deferred
A different product for a different user (operators supplying capacity). Not brought under this stack now. Token/i18n layers need not be portable to a second app yet.

---

## 14. Remaining open items (small)
- Regenerate + spot-check the OpenAPI-derived types now the wrap + converter landed — at the start of the migration.
- Balance-change emit (§6.9) — implement when prioritized; non-blocking.
- Landing content/copy — a product/marketing task; requirements are set (§3, §6).
- Launch locale set (§6.6) — a product call; architecture supports all eight.
- App namespace hosting — `/app/*` same-origin (assumed) vs a future `app.` subdomain; route tree unaffected.
