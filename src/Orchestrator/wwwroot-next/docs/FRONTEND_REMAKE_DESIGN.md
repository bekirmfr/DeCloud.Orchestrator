# DeCloud Frontend Remake — Design & Requirements

**Version:** 0.10 — *the spine is built; this version folds in what stayed true, what refined, and what the build taught us*

> **v0.10 change (2026-07-24):** Phase 3 (the spine) is substantially built and live: connect → fund-gated deploy (one-click + Customize, server-computed pricing) → operate (live cockpit) → **Dashboard as the operate+fund home**, all working end-to-end in production. The design held up well — every ratified shape in §2 is what actually shipped — but the *build* surfaced real refinements, folded in below (§2, §6.9, §11, §12, §14) and catalogued in full, with root causes, in `FRONTEND_REMAKE_IMPLEMENTATION.md` §6 and `AGENT_HANDOUT.md`. **Read those before touching anything this version references** — most of what they contain is "the mechanism already exists somewhere, find it before building a new one."
>
> **v0.9 change:** visual identity chosen — **Meridian** (cool architectural light, iris accent, Space Grotesk, centerless-mesh signature). Token seeds recorded in §6.4; dark mode to be derived from the same roles. Anchored on real hero + dashboard mockups.

**Version (history):** 0.8 — *restructured to lead with purpose; terminal/file-browser folded into in-app routes*

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
  **[Built.]** One-click and Customize (cpu/mem/disk, quality tier, bandwidth, GPU+VRAM) both ship, with a live per-component price breakdown from `/api/system/pricing/calculate`. Constraint rows and template Variables (user-facing env vars) are **not yet built** — the platform-variable-hiding rule is grounded (`GET /api/marketplace/platform-variables` distinguishes static/platform from user-facing) but the form fields aren't wired. Replication factor and OS image selection are also not yet exposed in Customize (the platform default `ubuntu-22.04` applies server-side when unset — see §6.9's sibling note in the implementation doc). **One deliberate, tracked exception to "preserved verbatim":** the ToS-gate retry and the on-chain deposit (fund top-up) flow are **not** ported to React yet — v1 bridges to the legacy app's `window.handleDeployTosGate()` and a link to the classic deposit UI, documented as open debt in `DEPLOY_MIGRATION.md` with an explicit "legacy app cannot be fully retired until these are native" warning. Both are real, working, complex flows (wallet-signature acceptance; an ethers escrow transaction) that were judged not worth reimplementing before Phase 5.
- **Handoff** — the one-time **password reveal** is a plain overlay that must *not* survive reload (a clean instance of the modal-vs-route rule, §7). Then **land on the new VM's detail page** (`/app/vms/:id`) — you just made it; operate begins; watch it boot.
- **Operate** — the VM detail page is the **cockpit**: live status/health (**where SignalR genuinely earns its subscription** — subscribe per-VM on mount, clean up on unmount), the access panel (SSH keys, direct-access ports, custom domains), the **browser terminal and file/SFTP browser** (in-app routes, see below), metrics, lifecycle actions (stop/restart/destroy), and a **runway indicator** tying draining balance back to the fund guard. **[Built, with one honest gap.]** Status, the access panel's SSH details, per-service readiness, and lifecycle actions are all live via SignalR — each required a backend fix, because the broadcast *mechanism* existed but nothing called it for owner-initiated changes (four separate instances of this; see the implementation doc §6.2/§6.9 and the handout). The metrics panel is built and wired (REST-seeded, SignalR-updated) but **stays hidden** — the node agent computes VM resource usage but never pushes it up via `ReportVmMetrics`, so there is genuinely nothing to show yet. Rather than display a permanent "waiting for metrics" placeholder for a feed that may never arrive, the panel renders only once real data exists. The node-side push is tracked, unbuilt work.
  - *Terminal & file browser (grounded decision):* today these are standalone `terminal.html`/`file-browser.html` pages opened via `window.open('_blank')` — the deliberate reason being **pop-out / multi-window** (keep a terminal open while navigating, several at once, second monitor). They become **in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) — tokened by the session, styled by the design system, xterm bundled + lazy-loaded (off the initial bundle) rather than CDN — **while preserving pop-out**: because a route is a URL, it opens in a new tab via a **chromeless layout variant** (no sidebar). No blocker exists (plain xterm over WS; no SharedArrayBuffer/COOP-COEP). The WS auth mechanism is preserved as-is (see §10). **[Not yet built.]** The cockpit itself (status/spec/access/services/lifecycle actions, all live via SignalR) is built and live; terminal and file-browser routes remain on the legacy standalone pages, unstarted.

**What the spine does to the information architecture.** A sidebar of ten equal items is wrong for a spine-shaped product. The **Dashboard becomes the operate + fund home** — running workloads and their runway — with **"Deploy" promoted to a primary, always-available action** instead of buried three levels deep (marketplace → template → modal). Marketplace, My Templates, and Nodes recede to "sources you deploy from." Admin, template authoring, and NodeAgent stay off-spine and plain.

**Status (2026-07-24): built and live, matching this shape exactly.** The Dashboard is the `/` route inside `/app` — balance, live hourly burn rate, runway ("About N days" / "Not currently billed" when workloads run but nothing is accruing / "No active workloads" when the list is empty — three states, not a single ambiguous null), a live running-workloads list (status dot + name + resources, subscribed via the hub's `user:{walletAddress}` group), and Deploy promoted as the header's primary action. Reaching it *is* now the nav-of-record: a signed-in visitor to the legacy `/` is redirected here (see `BACKEND_SERVING_SPEC.md` §2.1) — the handoff described at the end of this section has already happened, ahead of Marketplace/Nodes/Settings migrating (Phase 5 hasn't started; those remain deep-links into the legacy app for now, per the modal-vs-route... no — per the retirement mechanism in the implementation doc §2).

One refinement the build forced that's worth ratifying formally: **runway must be computed from the server's own billing formula, not a client estimate or a template-declared field.** `estimatedCostPerHour` on a template is frequently unset; the dashboard and the deploy page both call `POST /api/system/pricing/calculate` (the same `HourlyRateCalculator` that stamps `VmBillingInfo.HourlyRateCrypto` at scheduling time) rather than trust a possibly-stale or possibly-absent template field. This is the general principle in §6.9/§12 applied to money specifically: the client asks the server for the number it will actually be charged, every time, rather than deriving or caching a copy that can drift.

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
├─ /app/nodes             Nodes — my-nodes + fleet search (tabs); region/gpu/online filters
│  └─ /app/nodes/:id      Node detail — owner-aware (owner: full + earnings; non-owner: trimmed availability)
├─ /app/wallet            Wallet — balance/runway/deposits/usage; native on-chain deposit + earnings-withdraw
├─ /app/profile           Profile — identity + quotas (opened from the header avatar menu)
├─ /app/settings          Settings (theme + language; ToS/compliance)
│  └─ /app/settings/ssh-keys        SSH Keys (folded here)
├─ /app/report            Abuse report (in-app; reuses the public form component)
└─ /app/admin             Admin — role guard on this layout; non-admin → /app
   ├─ /app/admin/compliance · /app/admin/abuse
   ├─ /app/admin/templates              Template review queue (pending)
   │  └─ /app/admin/templates/:id       Template inspect (read-only: composed cloud-init, variables, artifacts)
   └─ /app/admin/nodes                  Node manager (fleet + remove)
      └─ /app/admin/nodes/:id           Node inspect (full + remove)
```

**Shell & nav — reworked to a top bar (2026-08-07).** The routes above are unchanged, but the shell is **no longer a sidebar**: `AppShell` is the Meridian reference's horizontal **top bar** (wordmark + glowing dot + nav on the left; balance chip + ProfileMenu + Deploy on the right; content in a centred column). Nav IA regrouped — top-level nav is **Overview · Marketplace · Nodes · Virtual Machines · Admin ▾**. Relocated *off* the nav: **Wallet** (reached via the balance chip → `BalanceModal` → "View full wallet"); **SSH Keys** + **Settings** (under the ProfileMenu dropdown); **My Templates** (now the **Marketplace "My Templates" tab**, `?tab=mine`, mirroring the Nodes tabs — `/my-templates` list redirects there). Admin pages sit under the **Admin ▾** dropdown. **Responsive:** the app is inline-styled (no `@media`), so under 880 px the top bar collapses to a hamburger → slide-in drawer via a `useMediaQuery` hook — the *shell* is responsive; per-page responsiveness is still owed (§14).

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

**Chosen visual direction: "Meridian"** (ratified). Cool architectural light; drama from typography + restraint; one accent; signature = the *centerless hairline mesh* (a node graph with no center, literalizing the network). These are the **token seeds** to encode into `design-tokens.css` (reconciling/replacing the one that already exists), not final values:
- **Color (light):** canvas `#EDEFEE`, panel `#F6F7F6`, ink `#14181A`, muted `#5C666B`, faint `#8A9298`, hairline `#D6DBD9` / `#E6E9E8`. Accent (iris) `#332ED6` (soft `#ECEBFB`) — used sparingly (primary action, live accents, links). Status: running/live pine `#1C7A56`, provisioning/warn `#B26B12`. (Iris is deliberately *not* the terracotta/acid-green AI-design tells.)
- **Type:** display **Space Grotesk** (tight, ~-0.03em), body **Inter**, mono **JetBrains Mono** (tabular figures for money, wallet addresses, metrics). All OFL/open — self-host to avoid a CDN-font dependency (matches the earlier move off CDNs).
- **Radii:** 8px controls, 12–14px cards. **Signature:** centerless mesh + off-axis hero; disciplined whitespace + hairlines carry the rest.
- **Dark mode (must derive, R7):** Meridian is light-*first*, not light-*only*. Derive a dark counterpart from the same roles — dark canvas/elevated panels, iris lifted for contrast, same Space Grotesk/JetBrains Mono, same mesh — and contrast-validate both modes. This is derivation, not a re-pick.
- **Fonts:** self-hosted (no CDN), latin + latin-ext now; Cyrillic/CJK subsets added in the i18n phase (R8), lazy-loaded per locale. `fonts.css` (`@font-face`) or Fontsource npm packages; import fonts before tokens.
- **Reconciliation with the existing `wwwroot/design-tokens.css` (decided):** the old file is a *different system* — dark-only, teal `#00d4aa`, Outfit, glow/gradient utilities, and different token *names* (`--bg-deep`, `--accent-primary`), with same-named tokens (`--text-primary`) holding opposite values. It is **not** replaced in place (that would break `index.html`, `sign.html`, `terminal.html`, `file-browser.html`). Instead: Meridian tokens are **net-new for the new app** (`dist-app`); the old file **stays for legacy** during migration; the two **never share a document** (serving split — old at `/`, new at `/app`), so no collision. Each page adopts Meridian **when it migrates** (a restyle, not a variable alias — e.g. `terminal`/`file-browser` fold into `/app/vms/:id` and target Meridian **dark**, which suits a terminal). The old `design-tokens.css` is **retired at cutover** (Phase 6) once the monolith and the last standalone page are on Meridian.

### 6.5 Light/dark (R7)
Token-set swap via CSS custom properties → no re-render, no flicker. Inline pre-paint script; honor `prefers-color-scheme`; persist override; contrast in both modes. CSS Modules over tokens; no theme-via-CSS-in-JS.

**Built (2026-08-07):** the `useTheme` control (Light / Dark / System) on `/app/settings` writes the pre-paint contract (`localStorage["dc-theme"]`; System = absent key, resolved via `prefers-color-scheme`). **Base-layer gotcha, found + fixed:** native form controls (checkboxes, number steppers, `<select>` chrome, and the default background of any un-styled input/select) follow the CSS `color-scheme` property — which was never pinned, so with the app on light and the OS on dark they rendered dark. `color-scheme` is now pinned to `data-theme` in the token layer, and `<select>`/bare inputs are themed in the base stylesheet. **Rule: the base layer, not each component, owns form-control styling** — an un-styled control follows the OS, not `data-theme`.

### 6.6 i18n — zh, ja, ru, fr, en, es, hi, tr (R8)
FormatJS/react-intl; ICU (Russian plurals; CJK none); `Intl` number/currency/date (USDC, uptime %, Hindi lakh/crore); lazy locale bundles; CJK font strategy; **never translate** addresses/hashes/code/terminal output; **CSS logical properties** now (cheap future-RTL); landing pre-rendered per-locale with `hreflang`. **Phased rollout:** architecture supports all eight; launch set is a product call — don't front-load all eight before there are users for them (no locale dropped).

### 6.7 Responsive / mobile (R9)
Mobile-first + container queries; touch ≥44px; no hover-only; tables collapse to cards; safe-area insets. **Honest degraded surface:** terminal-on-phone is inherently awkward — usable-not-great.

### 6.8 Accessible primitives → Radix (R10)
Radix Dialog/Toast/DropdownMenu/Tabs/Tooltip replace hand-rolled `makeModalAccessible`/`showToast`, styled with tokens; target WCAG 2.1 AA; the current helpers' behavior is the bar.

### 6.9 Real-time → existing SignalR hub (R11) — **built and live**
`OrchestratorHub` exposes `SubscribeToVm/Node/User` and broadcasts `VmStatusChanged/VmMetricsUpdated/VmAccessInfoUpdated/VmServicesUpdated`, JWT-authed (the token rides the `access_token` query param — browsers can't set a WS `Authorization` header). Scope (per §2): VM detail + dashboard status, exactly as ratified. Events map into the TanStack Query cache (`qc.setQueryData`) rather than parallel component state; subscribe on mount, clean up on unmount.

**What building this actually took, because it matters for the next real-time feature added to this app:** the hub methods and event names were all real and correctly named — but for three of the four events, **nothing on the server ever called the broadcast for owner-initiated changes.** `ReportVmStatus`/`ReportVmMetrics`/`ReportVmAccessInfo` exist as methods a *node* would invoke, and the node only ever exercises the metrics one partially (see below); the actual state changes — a user clicking Stop, a heartbeat updating service readiness or access info — went through entirely different code paths (`VmLifecycleManager.TransitionAsync`, `VmService.PerformVmActionAsync`, `NodeService`'s heartbeat handler) that updated the database and told nobody. Four separate instances of exactly this shape were found and fixed by introducing **one shared seam**, `IVmNotificationService`, with a method per event type, called from every path that actually changes that piece of state. This is the general lesson: **when a feature works in the legacy polling UI but not the new push-based one, the data path exists — find where the legacy client re-polls to and add a broadcast there, don't invent a new one.**

**A second, subtler lesson from the same work: change-gated broadcasts and automatic reconnection interact badly, and both are individually correct.** Every broadcast added above is gated on "did this value actually change" (heartbeats fire continuously; broadcasting unchanged state on every tick would be a firehose). SignalR's `withAutomaticReconnect()` recovers a dropped connection and re-subscribes — but does **not** replay events missed during the gap, and from the server's side there is nothing *to* replay, because nothing changed *since the last broadcast it sent*. So a status/service/access transition that happens entirely within a disconnect window is silently lost, not delayed — the client would sit on stale data indefinitely. The fix: on `onreconnected`, invalidate the relevant Query cache entries and let REST re-sync, rather than trusting the socket to have caught everything.

**User-scoped broadcasts (the dashboard's live list) were the fourth instance of the same missing-connection pattern.** `SubscribeToUser(userId)` existed on the hub since it was written, with zero code anywhere publishing to a `user:{id}` group — a subscription with no publisher. Fixed by having the VM-status broadcast (already flowing through the shared seam above) publish to **both** `vm:{vmId}` and `user:{ownerId}` in one call.

**Balance push remains deferred, as planned:** the dashboard and deploy-page runway figures poll (`GET /api/payment/balance`, ~20–30s `staleTime`/`refetchInterval`). No `BalanceUpdated` emit exists. When it's prioritized, the plan in this section is unchanged: one **contentless invalidation** from the billing success path to `user:{userId}` → client refetches the authoritative figure (never trusts a pushed amount); deposits stay client-driven; coalesce if metering is frequent. Terminal stays on the raw WS proxy (unbuilt — see §2's operate note). SSE dropped, as planned.

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
- **✅ Billing/payments — DESIGNED + largely BUILT (2026-07-27 … 2026-08-07), superseding the 2026-07-25 "NOT YET DESIGNED" note.** A dedicated **`/app/wallet`** page (balance / confirmed / pending deposits / unpaid usage / runway / recent usage / deposit details) plus a **sidebar balance card → balance modal** (opened from the shell) give the read + write surface; a **header avatar menu** carries Profile / Log out. Writes are now **native in `/app`**, ported faithfully from `payment.js` into `features/billing/paymentClient.ts` (ethers v6; the wallet signer is exposed via a new `getSigner()` on the auth context — it already existed internally for SIWE): on-chain **deposit** (`escrow.deposit`; approve-then-deposit; `frozen()`/`replacementContract` guard; min-deposit; Polygon EIP-1559 gas floors) and **earnings withdrawal** (`escrow.nodeWithdraw(0)`). **Verified live on Polygon Amoy** — a deposit and a withdrawal round-tripped on-chain. Config (escrow/USDC addresses, chain, min, confirmations) comes from `GET /api/payment/deposit-info`; **nothing is hardcoded**, and the wallet is the final signing gate. **One deliberate deviation from the legacy flow:** wrong-network **fails closed** (asks the user to switch) rather than auto-adding a chain with guessed RPC params. **Still legacy-only — the residual reasons the monolith can't yet be deleted:** admin **platform-fee withdrawal** (`withdrawPlatformFees`, escrow `onlyOwner`); the **frozen-contract migration** path (the native flow fails closed and points to the classic app); and **unused-deposit withdrawal** (`escrow.withdrawBalance` — ABI present, UI not wired; distinct from earnings). **Constraint, now relaxed but not gone:** user deposit + earnings withdrawal no longer require the legacy app; it remains the only home of the three residual money-moves, so it can't be *fully* deleted until those are re-homed (or accepted as admin-only/rare) at Phase 6.
- **Nodes (Phase 5, built 2026-08-07):** my-nodes (address-filtered — no owner-scoped endpoint exists, so filtered client-side against `GET /api/nodes`) + fleet search (`GET /api/nodes/search`, region/gpu/online) as tabs; **node detail** (`/app/nodes/:id`) is **owner-aware** — owner sees full sections + earnings, non-owner sees a trimmed availability/uptime view (the endpoint returns a fail-closed `NodeView` DTO — node secrets are never serialized to anyone, and only the owner/admin get the operational tier; 2026-08-07); **deploy-to-node** ("Deploy here" → `/deploy?node=` source chooser, threaded to `DeployTemplateRequest.NodeId`). Admin: **node manager** (`/app/admin/nodes`, `DELETE /api/nodes/{id}` — the only admin node action; deregister is node-self, no suspend endpoint) + **node inspect** (`/app/admin/nodes/:id`, full + remove). Legacy `marketplace.js` (all node code despite the name) + its `index.html`/`app.js` touch-points were retired.
- **Profile (built 2026-08-07):** `/app/profile` from `GET /api/user/me` — identity (wallet, display name/email, status, joined/last-login, VM + key counts) and **quotas** as Current-vs-Max usage bars (VMs / vCPU / memory / storage). **Roles are read from the session**, not `/me` (the response omits them). A deterministic avatar (address-hashed gradient, no dependency) appears in the header menu and on the page.
- **Marketplace/templates:** category/search/GPU/sort filters (URL-driven); template detail; repo-deploy → the one deploy path. **My Templates (built 2026-08-07)** now carries the full **lifecycle** (`publish` → PendingReview if community else Published; `cancel-review`; `revise` community-Published → new Draft revision) and **per-template earnings** ("Earned N USDC · K paid deploys" — net author cut, read from the settlement ledger, *not* a stored counter). **Edit resolves by template `id`, never slug** — the public slug lookup is `Published`-only and collides across authors, so drafts 404 by slug.
- **Admin:** compliance (suspend/block/bulk/VM-hold + tables); abuse (dismiss/warn/takedown + CSAM hash-check; NotScanned never shown as clean); **template review (built 2026-08-07):** a **pending queue** (`GET /templates/pending`, `Authorize(Roles=Admin)`) with approve/reject (reason required), and a dedicated **read-only inspect page** (`/app/admin/templates/:id`: tabbed role/composed cloud-init, variables table, artifacts with full SHA + base64 decode) — inspect is *not* the author edit form; **node manager (built 2026-08-07):** fleet list + remove (`DELETE /api/nodes/{id}`) and a full node inspect page.
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
   **Progress (2026-07-24):** the dashboard/VM-list bridge functions (`loadDashboardStats`, `loadVirtualMachines`, `renderVMsTable`, `attachVmsTableDelegation`, `renderDashboardVMs`, and their `window.*` exports) and the create-VM-modal bridge functions (`openCreateVMModal`, `createVM`, `updateTierInfo`, `updateBandwidthInfo`, `updateReplicationInfo`, `onGpuModeChange`, `updateEstimatedCost`, plus the client-side `QUALITY_TIERS`/`BANDWIDTH_TIERS`/`REPLICATION_TIERS` pricing tables they used — a **third, stale copy** of numbers `HourlyRateCalculator`/`SchedulingConfig` already own, see §12) are all deleted, ~1,013 lines removed across `app.js`/`index.html` in two retirements. `refreshData` was **kept but hollowed** to `loadUserBalance()` only — its name and `window.*` export survive because other legacy modules still call it after VM actions; renaming it would have broken them for no gain. `sanitizeVmName`/`validateVmName`/`previewVmName` are **deliberately still exported** — `repo-deploy.js` and `template-detail.js` (the still-live legacy marketplace deploy path) consume them. **Retirement hazard worth repeating for whoever does the next one:** deleting the create-VM modal's markup without also deleting five *module-scope* `document.getElementById('vm-cpu').addEventListener(...)`-style lines (no optional chaining) would have thrown at bundle load and taken down every remaining legacy page, not just the one being retired — always grep for top-level (non-function-scoped) references to an element before deleting that element.
6. **Migrate remaining pages** (marketplace, my-templates, nodes, settings, admin) behind the router; each deletes its old module + inline handlers; verify §7 parity per page. **Not started (Phase 5).** Marketplace is the highest-value pick to go first: it retires the last stale copy of the pricing tables (in `template-detail.js`) and lets the shell's Deploy button stop hard-coding a single default template slug.
7. **Cut over**, remove the page-div monolith; a new file replacing an old one deletes the old one in the same change. **Not started (Phase 6).** One piece of Phase 6 has already happened early and out of order: a signed-in visitor to `/` now redirects to `/app` (`BACKEND_SERVING_SPEC.md` §2.1) — the *nav-of-record* has flipped even though `/` itself still serves the old app's bundle to anonymous visitors and the old app is far from deleted. Worth knowing this when reading "Phase 6 not started" literally.

---

## 12. Backend questions — resolved
**Q1 — OpenAPI:** already emitted (Swashbuckle, `/swagger/v1/swagger.json`). Generate types. No change.
**Q2 — Contract normalization: DONE, with a real asterisk found during the build.** `MarketplaceController` returns `ApiResponse<T>`; a **global** `JsonStringEnumConverter` is registered — but three enums (`VmStatus`, `VmPowerState`, `VmAction`) predate it and lack the *per-enum* `[JsonConverter]` attribute that `VmRole`/`VmCategory`/`SubdomainTier`/`ServiceStatus` all carry, so **those three still serialize as raw numeric ordinals**, global converter notwithstanding. The frontend tolerates both forms at the one boundary (`normalizeStatus`/`normalizePowerState`/`vmActionOrdinal` in `src/features/vms/vmStatus.ts`) rather than trusting the doc's claim of uniformity. The correct backend fix — adding the attribute to all three — is still open; it's deferred because the legacy NodeAgent operator dashboard keys a lookup table on the integer ordinals and would need updating in the same change, and nobody has scoped that yet. **Lesson generalized: trust an observed wire response over what a design doc or a "global converter is registered" fact implies it should be.**
**Q3 — Real-time:** SignalR; hub already browser-facing. Adopted + subscribed, scoped exactly per §2 (VM detail + dashboard status) — see §6.9 for what the build found and fixed. Balance push remains deferred with the plan in §6.9 unchanged. SSE dropped.
**Q4 — Pricing formula: resolved to a single source, found by the build, not anticipated by this doc.** The legacy UI computed deploy cost estimates **client-side**, hardcoding the same tier-multiplier table the backend's `HourlyRateCalculator` uses for actual billing — a second copy that, separately, had drifted from a *third* copy inside `SchedulingConfig` (which feeds the node capability model's advertised pricing). All deploy-cost math now flows through one public endpoint, `POST /api/system/pricing/calculate`, which calls the exact `HourlyRateCalculator` that stamps `VmBillingInfo.HourlyRateCrypto` at scheduling time — and that calculator itself was changed to read its tier multiplier from `SchedulingConfig` rather than a second hardcoded copy, with a version-gated migration (`SchedulingConfigService`, config version 1→2) so a live, already-deployed config document converges automatically. **New client-side rule this establishes generally:** never reimplement a billing/pricing formula in the frontend, even as an "estimate" — always call the endpoint that shares the calculation code path with what actually gets charged.

---

## 13. NodeAgent operator dashboard — deferred
A different product for a different user (operators supplying capacity). Not brought under this stack now. Token/i18n layers need not be portable to a second app yet.

---

## 14. Remaining open items

**Deploy parity gaps** (§2, §7) — all groundable, none blocking further work: replication factor field (server clamps to 0/1/3/5; UI doesn't expose it), OS image selection (`GET /api/system/images` exists and is unused by the new deploy form), template Variables / user-facing env vars (`GET /api/marketplace/platform-variables` grounds the platform-vs-user discriminator; the form fields aren't built), the description/info cards the legacy Customize modal shows under each selector. Scheduling constraints (the locked/editable/user constraint-row builder) is the one genuine sub-effort among these — it's a small standalone module in the legacy app (`constraint-builder.js`), not just a missing field.

**Backend items surfaced by building the spine, not anticipated by this doc:**
- Three enums serialize as numeric ordinals despite a global string-enum converter (§12-Q2) — `VmStatus`/`VmPowerState`/`VmAction`. Client-tolerant now; the real fix has a blast radius into the legacy NodeAgent dashboard.
- Node-agent metrics push doesn't exist — the orchestrator/hub side is fully wired (`ReportVmMetrics`, `VmMetricsUpdated` broadcast, the REST snapshot endpoint, the client panel) but no code on the node agent ever calls `ReportVmMetrics`. The cockpit's metrics panel is correctly hidden until this exists (§2).
- `VmTemplate.MinimumSpec`/`RecommendedSpec` are non-nullable (`= new()`), so a template author who declares neither still ships a full spec of C# field defaults — and on the wire, "I require Standard tier" and "I said nothing" are byte-identical. This bit twice during the build (a tier dropdown that only offered two options; a bandwidth dropdown that collapsed to one) before the *client* stopped trusting `?? no-constraint` fallbacks that were structurally unreachable. The real fix is making `MinimumSpec` nullable; not done, tracked.
- Relatedly, at least one existing template (a private-browser template referred to as "Neko" during the build) recommends a quality tier that its own declared `MinimumSpec` forbids (`RecommendedSpec.QualityTier` fails its own floor check) — a one-click deploy of it would succeed, but opening Customize and keeping the recommendation would fail with `TIER_TOO_LOW`. Not audited for other templates with the same shape.
- A billing flag (`VmBillingInfo.IsPaused`) could be set to true on insufficient balance but was never cleared when the balance was later topped up — only an explicit (and apparently rarely-fired) `BalanceAdded` event resumed it, so a VM could sit "paused" indefinitely with a fully-funded owner. Fixed with a self-healing check (probe the actual balance on every billing cycle when the pause reason was specifically insufficient-balance) rather than waiting on the event. **This was invisible until the new dashboard became the first consumer of that flag** — see the "second client is a truth serum" pattern below.

**Frontend items:**
- The build's own error-handling gap: two React runtime crashes reached production as a raw, unstyled router developer page before an `errorElement`/`RouteError` boundary was added (now fixed — a Meridian-styled boundary sits on `/app`'s root route). `eslint-plugin-react-hooks` — the one static tool that would have caught the *cause* of one of those crashes (a hook called after a conditional early return) before it shipped — has a scoped config written but is **not yet run against the codebase or wired into the build**; doing both is the natural next step.
- `EXPECTED_CHAIN_ID` (the network the SIWE session and `WRONG_NETWORK` derivation compare against) is a Vite build-time constant that silently defaulted to `137` (Polygon **mainnet**) in every server build, because its real value lived only in a gitignored `.env.local`. A committed `.env.production` now fixes it, but the underlying shape — a deployment-critical fact duplicated into a client build constant, with a plausible-looking wrong fallback instead of a hard failure — is worth removing at the root by having the client fetch the chain ID from the backend (which already knows it) rather than bake it in at build time. Not done, tracked.
- The old app and the new app currently run **two independent SIWE/connect configurations** against the same backend (`BACKEND_SERVING_SPEC.md` §2.1). This resolves naturally at the `/`-flip (Change 3 / Phase 6) and isn't otherwise harmful, but any auth-adjacent bug that reproduces specifically after visiting a legacy page should have this duality checked early — even though, so far, every such bug found has turned out to be self-contained inside one app or the other rather than an interaction between them.
- Regenerate + spot-check the OpenAPI-derived types now the wrap + converter landed — still not done; do at the start of any renewed Phase 5 push.
- Balance-change emit (§6.9) — implement when prioritized; non-blocking, both the dashboard and deploy page poll correctly today.
- Landing content/copy — a product/marketing task; requirements are set (§3, §6). Phase 4 (landing) has not started.
- Launch locale set (§6.6) — a product call; architecture supports all eight.
- App namespace hosting — `/app/*` same-origin (assumed) vs a future `app.` subdomain; route tree unaffected.

**Items surfaced building nodes / wallet / profile (2026-08-07) — none blocking, all tracked:**
- **Atlas link latency is the highest-impact open item.** A Mongo crash-loop was triggered by a `usageRecords` scan taking ~11.7 s to transfer 2,508 tiny records over a pathologically slow Atlas connection; a compound index + a 10s→60s socket timeout stopped the crash, but *why the link itself is that slow* is unknown and is the most operationally important thing to chase. A `{NodeId:1,PeriodStart:1}` index on `usageRecords` would also speed the template-earnings scan if that collection keeps growing.
- **`HardwareInventory` shape unknown** (it lives in `DeCloud.Shared`, not in this checkout) — node *search* cards show CPU/GPU (via `NodeAdvertisement.capabilities`) but node *detail* can't, because `Node` carries no hardware model. Closing the search-vs-detail gap needs that shape.
- **✅ RESOLVED (2026-08-07) — node endpoints over-exposed secrets, fixed with a fail-closed `NodeView` DTO.** The endpoints serialized the internal `Node` model, leaking earnings, the API-key hash, **and** system-VM/relay **private keys** to any authenticated caller (the last found in production testing of an interim field-scrub, which was fail-open). Now projected to `NodeView` (+ `RelayView`/`DhtView`/`BlockStoreView`): a field ships only if explicitly listed, secrets in no tier (not even owner/admin — keys live on the node), owner/admin get the operational tier, non-owners the marketplace tier. Verified in production. Root lesson: don't return the internal persistence model from an API — project to a DTO.
- **`withdrawBalance` (unused-deposit withdrawal) UI not wired** — ABI present in `paymentClient`, distinct from earnings withdrawal; a small addition when wanted.
- **Repo/GitHub deploy doesn't consume `?node=`** — the source chooser forwards the node param, but the repo-deploy page doesn't pin to it yet.
- **`templateForm` unit tests owed** — the authoring form's compose/validate helpers should get the pure-helper test treatment the deploy slices got.

**Where the full, dated bug-by-bug account of all of the above lives:** `FRONTEND_REMAKE_IMPLEMENTATION.md` §6 (patterns) and §9 (journal), and `AGENT_HANDOUT.md` (the consolidated version written for picking up this project cold).
