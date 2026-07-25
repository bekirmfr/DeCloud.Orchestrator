# DeCloud Frontend Remake — Agent Handout

**Purpose of this document:** you are picking up this project with no memory of how it got here. This document is written to make that safe. Read it fully before touching code — it is denser than it needs to be for a quick skim on purpose, because the cost of re-discovering any one fact in here (via a production incident) has already been paid once, sometimes twice.

**Read in this order:**
1. This document, fully.
2. `FRONTEND_REMAKE_DESIGN.md` — the *what/why*. Ratified product decisions, requirements, the fence (what NOT to build).
3. `FRONTEND_REMAKE_IMPLEMENTATION.md` — the *how/when/status*. Phase plan, status dashboard, §6 "working agreements & hard-won gotchas" (overlaps this document somewhat — that section is the condensed, pattern-level version; this document is the fuller, fact-level version with file names and endpoint shapes), and §9 the journal (dated, chronological).
4. `BACKEND_SERVING_SPEC.md` — the serving-layer contract between the old and new app.
5. Then, and only then, the actual repository.

**This document will go stale.** It reflects the state as of **2026-07-24**. If the phase status table in `FRONTEND_REMAKE_IMPLEMENTATION.md` §0 says something different from what's summarized here, trust that doc — it's the living one. This handout is a snapshot, not a source of truth for status; it's a source of truth for *how things got the way they are and why*, which doesn't go stale the same way.

---

## 0. The operating discipline that produced everything below

This isn't optional flavor text — every one of the ~15 real bugs catalogued in §5 was found *because* this discipline was followed, and at least three were shipped in the first place because a shortcut was taken around it. Internalize these five rules before writing anything:

**1. Ground before acting.** Pull the real file before writing anything that depends on its shape — endpoint paths, DTO field names, enum members, constructor signatures. Never assume from memory, from a design doc, or from what "should" be true. A design doc said a global `JsonStringEnumConverter` made all enums serialize as strings; three enums didn't have the per-enum attribute it actually requires, and arrived as raw numbers instead. The doc was wrong. The wire is never wrong. When in doubt, `grep` the actual backend source, or curl the actual endpoint, before writing a line of client code against it.

**2. Understand why before changing.** A design that looks wrong in isolation may be holding up an invariant you can't see yet. Several times in this project, something that looked like an obvious bug (a template with no declared `ImageId`; a client that never computes pricing itself) turned out to be a deliberate, correct design once traced fully — and the actual bug was one specific missing connection nearby, not the design itself. Find the *specific* defect; don't redesign around a symptom.

**3. Build the least the architecture needs.** Before adding a mechanism, ask what already provides the property you want. Real examples from this project where the answer was "it already exists, use it": pricing (call `POST /api/system/pricing/calculate`, don't compute it client-side — this is the *exact* mistake the legacy app made, which is how its pricing tables drifted from what actually gets billed); image resolution (the platform already has a default-image mechanism, don't invent a client-side fallback); template variable visibility (`GET /api/marketplace/platform-variables` already tells you which fields are platform-owned vs. user-facing). Client-side pre-checks are fine and expected for UX; the client should never be the second copy of a server decision.

**4. Say it plainly, own mistakes openly.** Commit messages and any explanation to the project owner should be in plain language, and should correct earlier wrong claims explicitly rather than quietly overwriting them. This project has at least two documented instances of a wrong claim being corrected in the open in a *later* commit message rather than silently fixed — that's the right way to do it, not a failure.

**5. Keep the workspace tidy.** A page is "retired," not "deprecated" — the old code is deleted in the *same change* that ships its replacement, never left beside it "for reference." Before creating or editing a file, check whether a version already exists and is being edited elsewhere.

**One additional rule earned specifically by this project, worth stating as its own law: when adding code to an existing file, re-verify what you're inserting it next to — do not assume a block that looks contiguous actually is.** Three separate incidents (documented in §6, `FRONTEND_REMAKE_IMPLEMENTATION.md`) came from cutting or editing a range of code that *looked* like one cohesive unit but had something load-bearing sitting inside it that needed to survive. The check that would have caught all three in advance: before deleting or replacing a range, grep the range itself for anything with a caller *outside* the range.

---

## 1. What DeCloud is

A decentralized compute platform. Users connect a crypto wallet, fund an escrow balance in USDC, and deploy virtual machines onto a network of provider nodes; billing is metered hourly against the escrow balance and settled on-chain. Backend: ASP.NET Core "Orchestrator" + MongoDB (Atlas, remote) + Polygon USDC escrow (currently **Amoy testnet**, chain ID `80002` — not mainnet, a fact that mattered a great deal, see §5.11). A separate "NodeAgent" codebase runs on provider machines and is a different product for a different user (operators supplying capacity) — explicitly out of scope for this frontend work, per `FRONTEND_REMAKE_DESIGN.md` §13.

**Repository:** `github.com/bekirmfr/DeCloud.Orchestrator`. **Owner** develops on Windows + WSL. **Production:** Ubuntu 20.04 LTS, host `srv020184` (`142.234.200.108`), public domain `decloud.stackfi.tech`.

**The migration this whole engagement is about:** the frontend is being rewritten from a vanilla-JS monolith (`src/Orchestrator/wwwroot/`, one `index.html` with `showPage()` toggling `.active` on divs, everything wired through `window.*` globals and inline `onclick`) to React + TypeScript (`src/Orchestrator/wwwroot-next/`), using an **incremental strangler pattern** — the old app keeps serving, the new app is built page-by-page under `/app/*`, and each page is *retired* (old code deleted) the moment its replacement is proven live. This is "Option A — split by URL" in `FRONTEND_REMAKE_IMPLEMENTATION.md` §1; read that section for why the alternative (mounting both apps in one shell) was rejected.

---

## 2. Architecture snapshot

| Layer | Choice |
|---|---|
| New frontend | React + TypeScript, Vite, static build → `wwwroot/dist-app`, served at `/app/*` |
| Legacy frontend | Vanilla JS, served at `/`, being deleted page by page |
| Router | React Router v7, `basename="/app"` (so all `NavLink`/route paths inside the app are **relative to `/app`**, not absolute) |
| Server data / cache | TanStack Query — every server-backed value lives here, keyed by resource, never in component state |
| Client state | React Context + one small reducer for the auth/session machine (`sessionMachine.ts`) — deliberately *not* Redux-scale; almost everything else is either TanStack Query or ephemeral UI state |
| Real-time | SignalR client → `OrchestratorHub` (`/hub/orchestrator`) — one shared connection app-wide via `HubProvider`, JWT auth via the `access_token` **query parameter** (browsers cannot set a WebSocket `Authorization` header — this is confirmed server-side in `Program.cs`'s `OnMessageReceived`, not a client guess) |
| Wallet / auth | Reown AppKit (`@reown/appkit`) + ethers v6, SIWE (Sign-In With Ethereum) via `@reown/appkit-siwe` |
| Styling | CSS Modules + a token layer ("Meridian" — light-first, iris `#332ED6` accent, Space Grotesk/Inter/JetBrains Mono); new components lean on **inline token-driven styles** rather than external class names that might not exist yet — this was a direct lesson from an early "invisible modal" bug where a component referenced CSS classes that were never written |
| UI primitives | Radix UI (Dialog, etc.) |
| Backend | ASP.NET Core 8, MongoDB via the official driver, `ApiResponse<T>` envelope on (almost) everything |
| Tests | Vitest, currently **110 passing, 0 failing**, pure-logic-only (no component rendering tests exist — see §6 on what that does and doesn't catch) |

**Auth model, precisely.** Two independent state machines that must never be conflated:
- **`WalletState`** (client-only, AppKit): `disconnected | connecting | connected(address, chainId)`.
- **`SessionState`** (server-backed, JWT): `anonymous | authenticating | authenticated(token, user) | uncertain(token, user) | expired`. `"uncertain"` means a token refresh is in flight, **not** that identity is in doubt — treat it the same as `authenticated` for read purposes.
- A pure function, `deriveStatus`, combines both plus the expected chain into one of: `READY | NEEDS_CONNECT | NEEDS_AUTH | WRONG_NETWORK | ADDRESS_MISMATCH | UNCERTAIN`. This is the single source every gate/guard reads — never re-derive this logic ad hoc in a component.
- **The JWT's `sub` claim is the wallet address**, and it is the *same value* as `VirtualMachine.OwnerId` on every VM record — confirmed directly on a live record, not assumed. This is why `SubscribeToUser` and the dashboard's ownership filtering use the wallet address as the key.
- **Refresh:** `dc_rt` is an httpOnly cookie, never read by JS. `POST /api/auth/refresh` (empty body, cookie rides via `credentials: 'include'`) returns a fresh access token; the client mirrors the access token into `localStorage` under key `dc-access-token` (via `tokenStore`) purely as a "was this user signed in" hint for restore-on-mount, never as the source of truth.
- **`EXPECTED_CHAIN_ID`** is `Number(import.meta.env.VITE_EXPECTED_CHAIN_ID) || 137`. **This defaults to Polygon mainnet if the env var is missing** — and until 2026-07-24, the build-carrying env var lived *only* in a gitignored `.env.local`, so every server build silently ran on the wrong chain default. Fixed with a committed `.env.production`. See §5.11 for the full incident; the durable open item is that this should be served by the backend, not duplicated into a client build constant at all.

**Serving split, precisely** (full detail in `BACKEND_SERVING_SPEC.md`):
- Anonymous visitor to `/` → legacy app, unchanged, still the sole public entry point (no landing page exists yet — Phase 4, not started).
- **Signed-in visitor to `/` → client-side redirect to `/app`** (`showDashboard()` in the legacy `app.js` calls `location.replace('/app')` unless a `?page=` param is present). This is new as of 2026-07-24 and is **not** the planned Phase 6 "`/`-flip" — it's a JS-level redirect layered on top of the still-fully-present legacy app, done early because Phase 3's dashboard migration made the old dashboard genuinely useless to land on.
- `/?page=<name>` opens the legacy app directly on page `<name>` (added specifically so the new app's sidebar can deep-link to not-yet-migrated legacy pages: Nodes, Marketplace, My Templates, Settings, Admin). Unknown/retired page names redirect to `/app` rather than showing a blank shell.
- **Consequence: two independent SIWE/connect configurations run simultaneously** against the same backend — the legacy app's own AppKit instance at `/`, and the new app's at `/app`. This is a real, acknowledged cost with no fix until the old app is fully deleted (Phase 6). It is *not* automatically the cause of every auth-adjacent bug that happens to reproduce after visiting a legacy page — three real bugs were found in this exact symptom shape during this project, and all three turned out to be self-contained inside the new app's own code, unrelated to this duality (§5.11). Don't skip straight to blaming the duality; capture first.

---

## 3. Current state (as of 2026-07-24 — verify against `FRONTEND_REMAKE_IMPLEMENTATION.md` §0 for anything newer)

**Live in production, proven:**
- Full auth flow: connect → SIWE → session persists across reload → sign out.
- SSH Keys page (first page ever migrated + retired — the "proof the whole pipeline works" milestone).
- VM list (`/app/vms`, paged) and VM detail cockpit (`/app/vms/:id`) — status, spec, access panel (SSH command line, ready to copy), per-service readiness, and **state-aware lifecycle action buttons that actually work** (Stop/Start/Restart/Pause/Resume/ForceStop/Delete) — all four of status/services/access-info/lifecycle-actions required a distinct backend fix to become genuinely live (§5.1–§5.4).
- Live metrics panel on the cockpit — built and wired, but **deliberately hidden** until the node agent actually pushes metrics (it never has; §7 open items).
- Deploy (`/app/marketplace/:slug/deploy`) — one-click "Deploy with recommended settings," and an opt-in Customize panel (CPU/memory/disk, quality tier, bandwidth, GPU+VRAM) with a **live, server-computed price breakdown** (never a client-side pricing formula). Fund gate before the form; runway shown, not raw $/hr.
- Dashboard (`/app`, the index route) — available balance, live hourly burn rate, runway ("About N days" / "Not currently billed" / "No active workloads" — three distinct states, not one ambiguous null), a live-updating list of running workloads, Deploy promoted as the primary header action.
- Nav-of-record handoff — the new app's sidebar links migrated pages as real routes and un-migrated pages as `/?page=x` deep-links into the legacy app; a signed-in user lands on `/app` by default.
- Two full legacy-page retirements executed: the legacy dashboard + VM-list pages (~490 lines), and the legacy create-VM modal (~523 lines, including the deletion of a third stale copy of the tier-pricing tables).
- An `errorElement` (`RouteError.tsx`) on the `/app` root route — styled 404 / error page instead of React Router's raw developer fallback.

**Tooling, verified 2026-07-25 (this block previously said ESLint was unrun and unwired — it was wrong on both counts):**
- `eslint.config.js` — a deliberately narrow, hooks-only ESLint flat config. **Installed, wired, and green.** `"build": "eslint . && tsc -b && vite build"`, and `BuildFrontendNext` runs `npm run build`, so a `rules-of-hooks` violation fails `dotnet build -c Release` and therefore the deploy. `npm run lint` reports **0 errors, 0 warnings across 45 files** (file count checked with `npx eslint . -f json` — a lint matching zero files prints the same clean output as one matching all of them). Caveat: `exhaustive-deps` is `warn` and ESLint exits 0 on warnings, so a green *build* says nothing about that rule; run the lint separately.
- `npm run typecheck` — **was broken since Phase 1 and had never run.** It was `tsc -b --noEmit`, which fails `TS6310` before reading a line of source. Now `tsc -b`; first real verdict was 0 errors on `tsc -b --force`. It re-checks in full every run (with `noEmit`, build mode never sees its outputs as up to date) — harmless, and it can never falsely skip.
- `npm run gen:api` — writes `src/api/schema.d.ts`, which **nothing imports**. The hand-written interfaces are the contract; the generated file is an on-demand way to inspect the live backend, is gitignored, and adoption is deliberately deferred to Phase 5. See `FRONTEND_REMAKE_IMPLEMENTATION.md` §8.

**Not started:** Phase 4 (SSG landing page), Phase 5 (migrating Marketplace/My Templates/Nodes/Settings/Admin), Phase 6 (final cutover / delete the legacy app entirely / the real `/`-flip). Terminal and file-browser in-app routes (planned, ratified in the design doc, zero code written).

**Test suite:** 110 passing, all pure-logic (`vitest`). No component-rendering tests exist anywhere in this codebase — every UI bug in §5 that a test *could* have caught was actually caught by `tsc --noEmit` (type/import errors) or not caught by any automated tool at all (runtime/wire-shape/timing bugs). See §6 for what this means practically.

---

## 4. Grounded domain facts — the cheat sheet

Everything in this section was pulled from real code or a real HTTP response, not inferred. Treat it as more reliable than any prose description elsewhere, including in this document, if the two ever disagree — then go re-ground, because something changed.

### 4.1 The wire-numeric-enum trap — the single most expensive class of bug in this project

**These three enums have NO per-enum `[JsonConverter(typeof(JsonStringEnumConverter))]` attribute, despite a global converter being registered, and therefore serialize as raw integers, not strings:**

```
VmStatus (12 values):
  Pending=0, Scheduling=1, Provisioning=2, Running=3, Paused=4, Suspended=5,
  Stopping=6, Stopped=7, Deleting=8, Deleted=9, Migrating=10, Error=11

VmPowerState (3 values):
  Off=0, Running=1, Paused=2

VmAction (6 values) — THIS ONE MUST BE SENT AS A NUMBER IN REQUEST BODIES, not just tolerated on read:
  Start=0, Stop=1, Restart=2, Pause=3, Resume=4, ForceStop=5
```

`POST /api/vms/{id}/action` with body `{"action":"Stop"}` returns a `400` — `"The JSON value could not be converted to VmActionRequest"`. It must be `{"action":1}`. **This was broken in production, invisibly, for the entire time the VM detail cockpit existed** — every lifecycle button posted the string form and silently 400'd, and nobody noticed because every previously-verified status transition in this project had been triggered from the *legacy* app, never by clicking the new cockpit's own buttons. Found only when someone manually curled the endpoint for an unrelated reason.

**The client's single point of truth for all three:** `src/Orchestrator/wwwroot-next/src/features/vms/vmStatus.ts` — `normalizeStatus(raw: VmStatus | number | string): VmStatus`, `normalizePowerState(...)` (same shape), `vmActionOrdinal(action: VmAction): number`. **Any new code touching status, power state, or actions must go through these**, not a fresh `switch` or a raw string comparison.

Contrast: `VmRole`, `VmCategory`, `SubdomainTier`, `ServiceStatus`, `EnforcementActionType` **do** carry the per-enum attribute and correctly serialize as strings. There is no way to know which category a given enum falls into except checking the actual C# declaration or the actual wire response — do not assume based on what "should" be consistent.

**Backend fix status:** not done, but **the blast radius was re-measured on 2026-07-25 and is smaller than this section used to claim.** Two things shrink it. (a) `VmNotificationService` hand-builds its SignalR payloads with `status.ToString()`, so the **push path already sends names** and is unaffected by adding the attribute — only REST changes. (b) A suspected second integer-keyed consumer, `wwwroot/src/status-helpers.js`, turned out to be **dead code** (orphaned by the dashboard/VM-list retirements; zero call sites across all `.js` and `.html`) and has been deleted. So the only consumer needing a same-commit update is the legacy NodeAgent operator dashboard's `vmStateName()` lookup, keyed on the integer ordinals — which is what this section originally said, and is now true by construction rather than by luck. Still nobody's scoped it.

### 4.2 The inverted-enum trap

`QualityTier` is **inverted** — the best tier has the lowest number:

```
Guaranteed=0 (best, dedicated 1:1 CPU)
Standard=1
Balanced=2
Burstable=3 (worst, best-effort, 4:1 overcommit)
```

"Meets a floor" is therefore `tier <= floor`, **not** `tier >= floor`. The backend's own `QualityTierComparison.MeetsFloor` carries an explicit code comment warning against a raw `</>` comparison for exactly this reason. The client mirrors this in `allowedQualityTiers(minimumTier)` in `src/features/deploy/useDeploy.ts`.

`BandwidthTier` is **not** inverted — `Basic=0` (worst) through `Unmetered=3` (best), and a floor comparison is the ordinary `tier >= floor`. **Do not assume both tier-shaped enums invert the same way** — this exact confusion (using `defaultBandwidthTier` as if it were a floor, which conflates "the value that seeds a default selection" with "the value that constrains which selections are allowed") caused a real UI bug (§5.7).

### 4.3 Key endpoints (Orchestrator, not NodeAgent — two `VmsController`s exist in this codebase, in different projects; the frontend only ever talks to the Orchestrator one)

```
Auth
  POST /api/auth/nonce
  POST /api/auth/wallet              — SIWE verify → { accessToken, user }
  GET  /api/auth/session              — { address } only (no token) — restore needs a refresh too
  POST /api/auth/refresh              — cookie-driven, returns { accessToken, expiresAt, user }
  POST /api/auth/logout

SSH keys (UserController, base /api/user)
  GET/POST /api/user/me/ssh-keys
  DELETE   /api/user/me/ssh-keys/{keyId}

VMs (Orchestrator VmsController)
  GET    /api/vms?page=&pageSize=&status=&search=&sortBy=&sortDesc=
           → ApiResponse<PagedResult<VmSummaryDto>>
  GET    /api/vms/{id}                → ApiResponse<VmDetailResponse{ vm: VirtualMachine, hostNode: Node? }>
  GET    /api/vms/{id}/metrics        → ApiResponse<VmMetrics>  (404 "NO_METRICS" if the node has never reported)
  POST   /api/vms/{id}/action         body { action: <ORDINAL, see 4.1> } → ApiResponse<bool>
  DELETE /api/vms/{id}                → ApiResponse<bool>

Deploy / Marketplace
  POST /api/marketplace/templates/{templateId}/deploy
       body { vmName, environmentVariables?, customSpec? }
       → ApiResponse<CreateVmResponse{ vmId, status, message, error, password? }>
       — customSpec is OMITTED (null) for one-click deploy; the server then applies
         template.RecommendedSpec AND its own DefaultBandwidthTier/DefaultGpuMode.
         SENDING ANY customSpec SKIPS THOSE TWO SERVER DEFAULTS — so the Customize
         path must explicitly re-include bandwidth tier and GPU mode in its payload,
         or a "customized" deploy silently downgrades bandwidth. See DeployPage.tsx.
  GET  /api/marketplace/templates?sortBy=&search=&limit=&category=&requiresGpu=&tags=
       → ApiResponse<List<VmTemplateSummary>>   (NOT VmTemplate — see §5.5)
  GET  /api/marketplace/templates/{slugOrId}
       → ApiResponse<VmTemplate>   (full object, unprojected — this is the one endpoint
         allowed to return the whole thing, because a single-template fetch is small)
  GET  /api/marketplace/platform-variables
       → { static: [...], dynamic: [...] }  — distinguishes platform-resolved template
         variables (must stay hidden from the deploy form) from user-facing ones

Pricing / images / balance
  POST /api/system/pricing/calculate   [AllowAnonymous]  body = VmSpec
       → ApiResponse<PriceCalculation{ cpuCost, memoryCost, storageCost, gpuCost,
           bandwidthCost, replicationCost, hourlyTotal, dailyTotal, monthlyTotal, currency }>
       — THE SAME HourlyRateCalculator THAT ACTUALLY BILLS. Never reimplement this
         formula client-side. Rates are PLATFORM DEFAULTS (nodePricing: null passed
         server-side) — an individual node with operator-set rates may charge more,
         never less.
  GET  /api/system/images              → public VmImage list
  GET  /api/payment/balance
       → ApiResponse<BalanceResponse{ balance, confirmedBalance, pendingDeposits,
           unpaidUsage, totalBalance, hourlyBurnRate, tokenSymbol,
           pendingDepositsList?, recentUsage? }>
       — hourlyBurnRate = sum of HourlyRateCrypto across the user's Running,
         non-paused VMs, computed SERVER-SIDE (a client re-derivation from specs
         would silently disagree with actual billing). Poll this; there is no push.
```

### 4.4 SignalR hub — `/hub/orchestrator`

Auth: JWT via the `access_token` **query string parameter** (`accessTokenFactory` in the client config), because browsers cannot set a WebSocket `Authorization` header. Confirmed server-side in `Program.cs`'s `OnMessageReceived`.

```
Client → Hub:
  SubscribeToVm(vmId) / UnsubscribeFromVm(vmId)     — per-VM group "vm:{id}"
  SubscribeToUser(userId)                            — group "user:{id}" (userId = WALLET ADDRESS)
  SubscribeToNode(nodeId)
  (terminal proxy methods — unused; no terminal route exists yet)

Hub → Client (all broadcast via ONE shared seam, IVmNotificationService — see §5.1–§5.4):
  VmStatusChanged     { VmId, Status, Message, Timestamp }  — sent to BOTH vm:{id} AND user:{ownerId}
  VmMetricsUpdated     { VmId, Metrics }                     — vm:{id} only; server never actually
                                                                sends this (node doesn't push metrics)
  VmAccessInfoUpdated  { VmId, AccessInfo, Timestamp }       — vm:{id} only; AccessInfo is a
                                                                PROJECTION, VncPassword deliberately excluded
  VmServicesUpdated    { VmId, Services, Timestamp }         — vm:{id} only; gated on an actual-change
                                                                flag, not sent on every heartbeat
```

**Every one of these broadcasts is change-gated** — the emit only fires when the value actually differs from before, because heartbeats arrive continuously and an unconditional broadcast would flood every connected client. **This interacts badly with SignalR's automatic reconnect**, which does not replay missed events: a transition that happens entirely inside a disconnect window is lost, not delayed, because the server has nothing left to resend once reconnected. `HubProvider`'s `onreconnected` handler invalidates the relevant TanStack Query keys to force a REST re-sync as the fix — remember this if you add a fifth broadcast type; it needs the same reconnect-invalidation treatment, or it will have the same silent-gap bug.

**Client architecture:** one `HubConnection` for the whole app, owned by `HubProvider` (`src/realtime/HubProvider.tsx`), started once authenticated. `useVmRealtime(vmId)` (per-VM detail page) and `useUserRealtime(walletAddress)` (dashboard + VM list) are the two consuming hooks — both subscribe on mount, unsubscribe on unmount, and **patch the TanStack Query cache directly** (`qc.setQueryData` / `qc.setQueriesData`) rather than maintaining any parallel component state. If you're tempted to add `useState` for a value that's already in a Query cache, don't — read from the cache instead.

### 4.5 Pricing — one formula, one place, as of 2026-07-24

`HourlyRateCalculator.Calculate(spec, nodePricing, pricingConfig, schedulingConfig, ...)` is the **only** place tier-multiplier-based pricing is computed. It reads the tier multiplier from `SchedulingConfig` (stored, versioned, admin-editable document — currently config version 2), not a hardcoded switch. Both the deploy-time billing stamp (`VmService.CreateVmAsync`, via `VmLifecycleManager`) and the public live-estimate endpoint (`SystemController.CalculatePrice`) call this exact function with the exact same config — so a deploy-page quote and the actual bill can never disagree again. **If you ever see a second place computing a $/hr number from a spec and a tier, that is a regression of exactly the bug fixed in §5.9 — delete it and route through the shared calculator instead.**

Current multipliers (config v2): `Guaranteed=2.5, Standard=1.0, Balanced=0.6, Burstable=0.4`.

### 4.6 Templates — defaults vs. minimums, and why the distinction is load-bearing

`VmTemplate.MinimumSpec` and `.RecommendedSpec` are **non-nullable** `VmSpec` fields (`= new()`). A template author who declares neither still ships a full spec of plain C# field defaults — and on the wire, "I require Standard tier" and "I never said anything" are **byte-identical**. This has caused two real UI bugs (a tier dropdown offering only 2 of 4 options; a bandwidth dropdown collapsing to 1 of 4) and at least one known template-data inconsistency (a private-browser template, referred to during debugging as "Neko," whose `RecommendedSpec.QualityTier` fails its own `MinimumSpec` floor check).

**Rule: a *default* seeds which option starts selected. A *minimum* constrains which options are legal to pick. Never read one as the other.** `platform-general` (the template the shell's Deploy button targets by default) now has both fields explicitly declared as of 2026-07-24, specifically to give this rule something correct to point at. The structural fix — making `MinimumSpec`/`RecommendedSpec` nullable so "unconstrained" is expressible at the type level — is not done.

`ImageId` deliberately has **no** minimum, ever, on any template — there is no meaningful ordering ("at least ubuntu-22.04" isn't a coherent floor), so putting it in `MinimumSpec` would silently turn "the OS is a user's free choice" into an OS mandate. The platform instead applies a default (`ubuntu-22.04`, chosen because it's the one registry image with a pinned SHA256 — the content-verified path) server-side, at the single VM-creation funnel (`VmService.CreateVmAsync`), **scoped to tenant VMs only** — a system VM (Relay/DHT/BlockStore) is debian-12-based and must never silently inherit a tenant default.

---

## 5. The bug catalog

Every entry: symptom → root cause → fix location → the generalizable lesson. Read this before assuming any given piece of behavior is "obviously" correct — several of these were shipped, believed correct, and stayed that way for a meaningful stretch before being found.

### 5.1–5.4 The dominant pattern: broadcast mechanism exists, nothing calls it (four instances)

The single most common bug shape in this project. Full detail and the shared fix seam in `FRONTEND_REMAKE_IMPLEMENTATION.md` §6.2 — summarized here:

- **5.1 Status.** `VmLifecycleManager.TransitionAsync` (the *only* method allowed to change `VirtualMachine.Status`, per the codebase's own doc comment) updated the database but never broadcast. The hub's only status broadcast lived inside `ReportVmStatus`, a method only a *node* invokes. Owner-initiated stops/starts (via `VmService.PerformVmActionAsync`, which writes an *optimistic* in-flight status directly, deliberately bypassing `TransitionAsync`'s heavier side-effects) were doubly silent.
- **5.2 Services.** The heartbeat handler (`NodeService`) updated `vm.Services` readiness and told nobody.
- **5.3 Access info.** Same heartbeat handler populated `vm.AccessInfo` (SSH host, VNC details) and told nobody — the one method that *does* broadcast on access-info change (`OrchestratorHub.ReportVmAccessInfo`) is, like `ReportVmStatus`, only ever invoked by a node, and no node calls it.
- **5.4 User-scoped (dashboard live list).** `SubscribeToUser(userId)` has existed on the hub since it was written; nothing anywhere published to a `user:{id}` group. A subscription with zero publishers.

**Fix, all four:** one shared seam, `IVmNotificationService` (`Services/VmNotificationService.cs`), one method per event type, called from every code path that actually changes that piece of state — not duplicated inline at each call site. The status broadcast (5.1) now also solves 5.4 by publishing to both `vm:{id}` and `user:{ownerId}` in the same call.

**Diagnostic habit this teaches:** when a feature works via legacy polling but not via the new push channel, `grep -rn "EventName"` across the whole backend before assuming a broadcast fires anywhere — don't reason about whether it *should*.

### 5.5 Marketplace listing: ~1MB payload, two swallowed exceptions, in-memory filtering

`GET /api/marketplace/templates` intermittently returned `200 {"success":true,"data":[]}` or a truncated JSON body (`JSON.parse: unterminated string`). Three independent causes, all needed to be fixed together:

1. **Payload.** The listing endpoint returned full `VmTemplate` objects, including `CloudInitTemplate` — a multi-KB YAML body per template that no listing UI renders. Real measured response size: 986,589 bytes. Large enough that the Mongo driver's socket read timed out mid-stream against the remote Atlas cluster, non-deterministically. **Fix:** a new projection DTO, `VmTemplateSummary` (`Models/VmTemplateSummary.cs`) — 14 fields the marketplace grid actually reads, built via a **Mongo-side `.Project()`**, not a C# object built after fetching the full document (the projection *is* the query, so `CloudInitTemplate` never leaves the database). Payload dropped to ~12,400 bytes — a ~79× reduction. This mirrors a decision already made for VMs (`GET /api/vms` → `VmSummaryDto`, never the raw `VirtualMachine`).
2. **Swallowed exceptions, at TWO layers.** `DataStore.GetTemplatesAsync` caught the (now much rarer, but not impossible) exception and returned `new List<VmTemplateSummary>()`. One layer up, `TemplateService.GetTemplatesAsync` did the exact same thing independently. `MarketplaceController` already correctly turned a thrown exception into a `500 INTERNAL_ERROR` — it simply never got the chance, because the exception was swallowed twice before reaching it. **Fixing only one layer would have produced a build error at the other, unfixed one** — this is exactly what happened on the first attempt. **Fix:** `throw;` at both layers, matching an existing convention already present elsewhere in `DataStore.cs`.
3. **In-memory filtering.** `SearchTerm` and `Limit` were applied *after* fetching every published template into memory, so `?limit=10` still transferred the entire collection before discarding most of it. **Fix:** pushed into the Mongo query — `Regex.Escape`'d search (not optional; this is an `[AllowAnonymous]` endpoint taking the search term straight off the query string, so an unescaped regex is both a correctness bug and a ReDoS surface), server-side `.Limit()`.
4. **A latent, unrelated bug found as a side effect of grounding this fix.** `TenantVmTemplateSeeder.SeedTemplatesAsync` checked whether a template slug already existed by scanning the **public** marketplace listing (filtered to `Published`+`Public`), so a `Draft` or `Private` template with the same slug was invisible to it — a re-seed would have created a duplicate. Fixed with a direct `GetTemplateBySlugAsync` lookup, matching the pattern two *other* call sites in the same file already used correctly.

**Generalizable rule:** `catch (Exception ex) { log; return <empty-but-valid-shaped-default>; }` on a public listing endpoint makes "the query failed" and "there is genuinely nothing to return" indistinguishable to every caller, forever. Prefer `throw;` unless there's a specific, articulated reason the caller must never see the failure.

### 5.6 Tier price multiplier disagreement (money-facing, backend-only)

`HourlyRateCalculator` (bills) and `SchedulingConfig` (advertises via the node capability model) disagreed on quality-tier multipliers: `2.5/1.0/0.6/0.4` vs `1.8/1.0/0.7/0.5`. **An earlier claim during this project's own history — that the legacy UI's pricing table was "stale" — was wrong and was corrected in the open**: the legacy UI matches the actual billing formula exactly; the real disagreement was entirely internal to the backend, between two things that should have been one thing. Fixed by making `HourlyRateCalculator.Calculate` take `SchedulingConfig` as a **required**, not optional-with-a-fallback, parameter (an optional param with a default would have silently recreated the same class of drift) and read the multiplier from it. `SchedulingConfigService` gained a version-gated migration (stored config schema v1→v2) so the live, already-persisted document self-corrects on next load, no manual DB edit needed. **Values aligned to what was actually being billed, deliberately** — no user's bill changed; the wrong side (the capability advertisement) is what moved.

### 5.7 Deploy Customize dropdowns offered too few options

Two related but distinct bugs, both from §4.6's default-vs-minimum confusion:
- Quality tier dropdown offered only `Guaranteed`/`Standard` because the template's absent `MinimumSpec` fell back to `new VmSpec()`, whose `QualityTier` field defaults to `Standard` — an unintended floor nobody chose.
- Bandwidth dropdown collapsed to a single option because the client (copying a bug the *legacy* modal has always had) read `template.defaultBandwidthTier` as if it were a floor — a template that *defaults* to the best tier became impossible to *downgrade*.

**Fix:** `allowedQualityTiers`/`allowedBandwidthTiers` (`src/features/deploy/useDeploy.ts`) now read floors from `minimumSpec` only, defaulting to "no constraint" when absent, with a passing regression test asserting the specific old wrong behavior ("undefined floor → only the top tier available") no longer happens. `platform-general` got explicit `MinimumSpec`/`RecommendedSpec` (§4.6) as the other half of the fix.

### 5.8 Billing `IsPaused` flag never cleared

A VM's billing could be marked `IsPaused=true` on insufficient balance and then **never resume**, even after the owner topped up — the resume path only fired on an explicit `BalanceAdded` event that apparently doesn't reliably arrive in practice, and the one other exit from the paused branch (a `VmStop` fall-through) bills final usage but leaves the flag set. **Fix:** self-heal — on every billing cycle, if the current pause reason is specifically insufficient-balance, probe whether the owner now has funds and clear the flag if so, rather than waiting on an event that may not come. **This flag had zero readers anywhere in the app until the dashboard's burn-rate calculation started excluding paused VMs from the sum** — at which point a stale flag silently understated a real user's runway by roughly 5×, discovered by comparing the displayed number against manual arithmetic.

### 5.9 SIWE `getSession` never actually authenticated

The hook AppKit calls to decide whether to prompt for a wallet signature (`getSession` in `src/auth/siwe.ts`) built its request through a local helper that set `Content-Type` and `credentials` but **never an `Authorization` header**, against an endpoint (`GET /api/auth/session`) that requires a bearer token. It had **always** 401'd — for the entire time this code existed — and AppKit correctly read "401" as "no session," opening the sign-in modal on every full page load. Invisible during ordinary SPA navigation (which never reloads the JS bundle); only surfaced reliably once something forced a real page reload (visiting the legacy app and coming back did, every time). **Fix:** `getSession` now delegates to a `getCurrentSession` callback supplied by `AuthProvider`, answering from the app's *own* already-restored session state instead of independently querying the server — removing a second, racing source of truth entirely, rather than patching the first one to also succeed.

### 5.10 Production build silently ran on the wrong blockchain network

Found immediately after fixing 5.9, when the modal still appeared: `getSession`'s answer carried the correct wallet address but chain ID `137` (Polygon **mainnet**) instead of `80002` (Amoy — what the platform actually runs on). `EXPECTED_CHAIN_ID` falls back to `137` when `VITE_EXPECTED_CHAIN_ID` is unset, and that variable lived **only** in a gitignored `.env.local` — so every server build had silently defaulted to mainnet. AppKit correctly treated a session on the "wrong" chain as inapplicable. **This almost certainly means `WRONG_NETWORK` derived-status has been comparing against the wrong chain in production since Phase 1**, invisibly, because nothing had exercised that comparison path visibly until AppKit's session-applicability check used the same constant. **Fix:** committed `.env.production` with `VITE_EXPECTED_CHAIN_ID=80002`. **Open, not fixed:** the client should not own this fact at all; the backend already knows its own chain ID and should serve it, removing the class of bug (a build constant with a plausible-looking wrong default) rather than just this instance of it.

### 5.11 Stale `useRef` read due to render timing

Found after 5.9 and 5.10 both landed and the modal *still* occasionally appeared: `getCurrentSession` awaited a restore promise and then read a `useRef` mirror of the session state — but `dispatch()` only **schedules** a re-render; the ref itself is only written *during* render. So the ref could still read `"anonymous"` at the exact moment the callback ran, even though the underlying restore had already genuinely succeeded moments earlier. **Fix:** the restore promise now resolves **with** the recovered wallet address directly, so the callback has a value to use that doesn't depend on a render having already happened.

**5.9–5.11 together, the meta-lesson:** three wrong-but-plausible hypotheses (refresh-token rotation; a call-ordering race) were floated and discarded before landing on the real chain, all disproven by an actual browser capture (Network tab, `console.log` instrumentation), never by re-reading the source more carefully. **When a fix "should" work and the symptom persists, the next step is always to capture what's actually happening, not to reason to a second guess.**

### 5.12 Every VM lifecycle button was broken from the day the cockpit shipped

Covered in §4.1 — repeated here because of *how* it was found, which is the more important fact: not by a user report, not by a test, but by a manual `curl` of the action endpoint done to trigger a status change *for an unrelated testing purpose* (proving the dashboard's live list worked). **No automated gate in this codebase would have caught it.** `tsc` sees two strings on both sides of a JSON body construction; `vitest` never makes a real HTTP call. The only thing that would have caught it earlier is exactly what did catch it — someone actually clicking the button, or hitting the endpoint by hand, and reading the real response.

### 5.13 React hooks placed after an early return (`DeployPage`, React error #310)

A `usePriceEstimate`/`useDebounced` pair was added to `DeployPage` positioned *after* the component's `if (isLoading) return ...` guards. On a cold page load, the loading render calls fewer hooks than the loaded render (the early return skips the new ones on the first render only) — React's hook-count invariant breaks, and it throws `#310` on a **second-or-later** render, which is why it only reproduced intermittently ("sometimes I get this error") depending on whether the template was already warm in the TanStack Query cache. **Fix:** moved every hook above all early returns; `template` typed as optional through the derivation block and narrowed only after the guards, with `enabled: !!specJson` as the correct way to make a query "wait" for data rather than conditionally calling the hook at all. **This class of bug is exactly what `eslint-plugin-react-hooks`'s `rules-of-hooks` rule exists to catch statically — see §3, "written but not yet run."**

### 5.14 `install.sh --env-file` silently dropped side-effect flags

Not a frontend bug, but directly relevant to anyone deploying this project: an env-file mechanism was added to keep secrets off the install command line (`sudo bash install.sh --env-file /etc/decloud/install.env`). Putting `INGRESS_DOMAIN` in that file (instead of passing `--ingress-domain` on the command line) silently left `ENABLE_INGRESS` false, because `--ingress-domain` sets **three** variables as a side effect (`INGRESS_DOMAIN`, `INSTALL_CADDY`, `ENABLE_INGRESS`) and only one of them was captured by putting the value in the env file — the result was new VMs getting no subdomain, with only a Debug-level log line as evidence. Setting the other two directly in the env file *by hand* to compensate produced an **unstartable systemd unit** (`INSTALL_CADDY=true` adds `Requires=caddy.service` to the generated unit, and something about that path didn't complete correctly when set this way). **Fix:** the installer now actively **rejects** an env file that sets any known side-effect variable, rather than merely documenting the rule (`install.env.example` explains why). **Rule that generalizes:** an env file should hold secrets *only*; any flag with a side effect beyond setting one variable belongs on the command line, always.

---

## 6. File manifest

```
src/Orchestrator/
├─ Program.cs                          serving split (/, /app, fallback routing)
├─ Controllers/
│  ├─ MarketplaceController.cs         GetTemplates/GetFeaturedTemplates → VmTemplateSummary (§5.5)
│  │                                    GetMyTemplates/GetPendingTemplates → still full VmTemplate
│  │                                    deploy action (POST .../deploy)
│  ├─ PaymentController.cs             GetBalance + HourlyBurnRate (§5.8 dashboard support)
│  ├─ SystemController.cs              CalculatePrice (§4.5), images, health
│  ├─ VmsController.cs                 THE ORCHESTRATOR ONE — list/detail/action/delete/metrics
│  └─ UserController.cs                ssh-keys (base /api/user)
├─ Services/
│  ├─ VmService.cs                     CreateVmAsync (ImageId default, §4.6);
│  │                                    PerformVmActionAsync (optimistic status write, §5.1)
│  ├─ VmLifecycleManager.cs            TransitionAsync — the ONE status-transition authority (§5.1)
│  ├─ VmNotificationService.cs /
│  │  IVmNotificationService.cs        THE shared broadcast seam (§5.1–5.4):
│  │                                    BroadcastStatusAsync(vmId, ownerId, status, msg)
│  │                                    BroadcastServicesAsync(vmId, services)
│  │                                    BroadcastAccessInfoAsync(vmId, accessInfo)
│  ├─ NodeService.cs                   heartbeat handling — ApplyReportedAccess (§5.3),
│  │                                    UpdateServiceReadiness (§5.2), both change-gated
│  ├─ TemplateService.cs /
│  │  ITemplateService.cs              GetTemplatesAsync/GetFeaturedTemplatesAsync → VmTemplateSummary
│  ├─ BillingService.cs                self-heals IsPaused (§5.8)
│  ├─ SchedulingConfigService.cs       tier multiplier source of truth, v1→v2 migration (§5.6)
│  ├─ HourlyRateCalculator.cs          static; Calculate(...) now REQUIRES SchedulingConfig (§4.5)
│  └─ Tenant/TenantVmTemplateSeeder.cs BuildGeneralTemplateAsync (explicit Min/RecommendedSpec, §4.6)
│                                       SeedTemplatesAsync (fixed slug lookup, §5.5-4)
├─ Hubs/OrchestratorHub.cs             SubscribeToVm/User/Node; node-facing Report* methods
├─ Models/
│  ├─ VmTemplateSummary.cs             NEW — the listing DTO (§5.5)
│  ├─ VirtualMachine.cs / VmSpec.cs / VmAccessInfo.cs / VmNetworkConfig.cs /
│  │  VmBillingInfo.cs / VmServiceModel.cs
│  └─ SchedulingConfig.cs
└─ Persistence/DataStore.cs            GetTemplatesAsync — projection + throw + server-side filter (§5.5)

src/Orchestrator/wwwroot/               ── LEGACY vanilla-JS app — being deleted page by page ──
├─ index.html                          nav items + page divs — dashboard/vm-list/create-vm-modal
│                                        sections DELETED (§ retirements)
├─ src/app.js                          retired: loadDashboardStats/loadVirtualMachines/
│                                        renderVMsTable/openCreateVMModal/createVM/updateTierInfo/
│                                        etc. — all gone. refreshData KEPT but hollowed to
│                                        loadUserBalance() only (still has 7+ callers + a
│                                        window.refreshData export). showDashboard() now redirects
│                                        signed-in / to /app. ?page= deep-link handling added.
│                                        sanitizeVmName/validateVmName/previewVmName DELIBERATELY
│                                        KEPT — repo-deploy.js and template-detail.js still use them.
├─ src/template-detail.js              STILL LIVE — the legacy marketplace deploy path.
│                                        Still contains DEPLOY_QUALITY_TIERS, the LAST surviving
│                                        stale copy of the pricing tables. Dies when Marketplace
│                                        migrates (Phase 5) — see §7.
├─ src/marketplace-templates.js        still live, calls the now-fixed GET /api/marketplace/templates
├─ src/direct-access.js,
│  src/custom-domains.js               modal openers now ORPHANED by the create-VM-modal
│                                        retirement (no caller in markup) but the modules load
│                                        independently and haven't been audited for other callers
│                                        — NOT deleted, tracked open item (§7)
└─ install.sh, install.env.example     --env-file now REJECTS side-effect vars (§5.14)

src/Orchestrator/wwwroot-next/          ── NEW React app ──
├─ eslint.config.js                    NEW — hooks-only flat config, NOT YET RUN (§3)
├─ .env.production                     NEW — VITE_EXPECTED_CHAIN_ID=80002 (§5.10)
└─ src/
   ├─ main.tsx                         QueryClientProvider > AuthProvider > HubProvider > Router
   ├─ auth/
   │  ├─ types.ts                      SessionState, WalletState, AuthUser
   │  ├─ AuthProvider.tsx               getCurrentSession added (§5.9); restoreRef resolves
   │  │                                  WITH { address } now, not void (§5.11)
   │  ├─ siwe.ts                        getSession delegates to getCurrentSession (§5.9)
   │  ├─ deriveStatus.ts, sessionMachine.ts, walletState.ts, walletCrypto.ts, tokenStore.ts
   ├─ api/client.ts, errors.ts          api() — the single ApiResponse<T> unwrap point
   ├─ app/
   │  ├─ routes.tsx                     index→DashboardPage; /vms; /vms/:id;
   │  │                                  /marketplace/:slug/deploy; /settings/ssh-keys;
   │  │                                  /admin/* (role-guarded); errorElement on root
   │  ├─ AppShell.tsx                   sidebar: migrated NavLinks + legacy
   │  │                                  <a href="/?page=x"> + admin section (canAccessAdmin)
   │  ├─ RouteError.tsx                 NEW — the error boundary (§3)
   │  ├─ guards.ts                      canAccessAdmin(user)
   │  └─ StatusGate.tsx, resolveShellView.ts
   ├─ realtime/
   │  ├─ HubProvider.tsx                ONE shared HubConnection; onreconnected now
   │  │                                  invalidates queries (§4.4 reconnect note)
   │  ├─ useVmRealtime.ts               per-VM: Status/Metrics/AccessInfo/Services handlers
   │  └─ useUserRealtime.ts             NEW — SubscribeToUser; patches the vms-list cache
   │                                     + invalidates balance; used by BOTH DashboardPage
   │                                     and VmsPage (the latter added specifically so the
   │                                     VM list page has its own live updates, §5's retirement entry)
   └─ features/
      ├─ ssh-keys/                     useSshKeys, SshKeysPage, AddKeyModal (first migrated page)
      ├─ vms/
      │  ├─ vmStatus.ts                normalizeStatus/normalizePowerState/vmActionOrdinal (§4.1),
      │  │                              vmStatusBadge, allowedActions — THE enum-tolerance module
      │  ├─ useVms.ts                  useVms/useVm/useVmAction/useDeleteVm/useVmMetrics + types
      │  ├─ VmsPage.tsx                 list; now subscribes useUserRealtime
      │  └─ VmDetailPage.tsx            the cockpit — status/spec/access/services/lifecycle +
      │                                  MetricsPanel (gated on data existing, §7)
      └─ deploy/
         ├─ deploySubmit.ts             resolveTemplate, submitTemplateDeploy (ToS-retry bridge
         │                              to the LEGACY window.handleDeployTosGate — tracked debt,
         │                              see DEPLOY_MIGRATION.md), shouldRevealPassword
         ├─ useDeploy.ts                useTemplate, useDeploy mutation, specFloorErrors,
         │                              allowedQualityTiers/allowedBandwidthTiers (§4.2, §5.7),
         │                              re-exports useBalance/runwayDays from ../billing
         ├─ DeployPage.tsx              one-click + Customize; live pricing; reset-on-toggle;
         │                              every hook above the early returns (§5.13)
         └─ DEPLOY_MIGRATION.md         tracked debt doc — the legacy ToS-gate + deposit bridge
      ├─ billing/useBalance.ts          moved out of deploy/ once dashboard needed it too —
      │                                  BalanceResponse incl. hourlyBurnRate; runwayDays; formatRunway
      └─ dashboard/DashboardPage.tsx    balance/burn/runway, live workload list, promoted Deploy
```

---

## 7. Open items, prioritized

**Would prevent calling Phase 3 fully done:**
1. Deploy Customize parity gaps: replication factor field, OS image selection (`GET /api/system/images` exists, unused), template Variables (user-facing env vars — `GET /api/marketplace/platform-variables` grounds the discriminator, form not built), description cards, scheduling constraints (a real sub-effort — a locked/editable/user constraint-row builder, `constraint-builder.js` in the legacy app, not just a missing field).
2. Node-agent metrics push doesn't exist. The entire client/hub/REST-snapshot chain is built and correctly gated to hide until data exists — the missing piece is on the **NodeAgent** side (a different codebase area), a periodic task that would call `GetVmUsageAsync` per running VM and push it via `ReportVmMetrics`.
3. **~~VM-modal cross-module audit~~ — DONE 2026-07-25, and it found a live regression, not dead code.** The framing was wrong: this item asked *"is it safe to delete?"*, so a grep showing zero callers read as reassuring. It is the opposite. `openDirectAccessModal` and `openCustomDomainsModal` have **no external caller anywhere** — their `onclick` handlers went with the retired VM table — the modal markup still sits in `index.html` wired to close buttons nothing can reach, and **the new app has no replacement** (`wwwroot-next/src/features/` is `billing dashboard deploy ssh-keys vms`; `VmDetailPage.tsx` mentions neither). Same for `terminal.html`/`file-browser.html`, opened only from `vm-modals.js:133,140`. **So Direct Access, Custom Domains, the browser terminal and the file browser have all been unreachable to users since 2026-07-24, with their backends still live.** All four are already designed (`FRONTEND_REMAKE_DESIGN.md` route tree: `/app/vms/:id/{terminal,files,domains,access}`) — they are unbuilt, not unplanned. **Do not delete the legacy modules; they are the only working implementation.** Tracked in `FRONTEND_REMAKE_IMPLEMENTATION.md` §8. **Generalizable lesson: an audit inherits the question its tracking item asks — "safe to delete?" and "can a user still reach this?" are answered by the same grep and mean opposite things. Ask "was this replaced, or just removed?" first.**

**Correctness/robustness, no user-visible symptom currently, but real:**
4. Three enums serialize numeric despite the global string converter (§4.1) — client-tolerant, backend fix has a blast radius into the legacy NodeAgent dashboard.
5. `MinimumSpec`/`RecommendedSpec` can't express "unconstrained" (§4.6) — at least one template ("Neko") has a live contradiction as a direct consequence.
6. Chain ID is a client build constant with a silently-plausible-wrong fallback (§5.10) — should be served by the backend.
7. ~~`eslint.config.js` written, not run or wired into the build (§3)~~ — **CLOSED 2026-07-25.** It is wired (`npm run build` runs `eslint .` first), green (0/0 across 45 files), and now gates the Release build. See §3.
8. Two independent SIWE configs run simultaneously until the `/`-flip (§2) — not itself broken, but the first thing to rule out (via capture, not assumption) in any future auth bug that reproduces after a legacy round-trip.
9. Mixed line endings in `VmService.cs`/`NodeService.cs` — a `.gitattributes` normalizing to LF removes a recurring patch-matching annoyance.
10. Error boundary (`RouteError.tsx`) has no reporting-service hook — deliberate (none exists yet), `console.error` only.

**Not started, no urgency yet:**
- Phase 4 (SSG landing), Phase 5 (Marketplace/My Templates/Nodes/Settings/Admin migration — **Marketplace recommended first**, since it kills the last stale pricing-table copy and lets the shell's Deploy button stop hard-coding `platform-general`), Phase 6 (real cutover / delete the legacy app).
- Terminal and file-browser in-app routes — fully specified in the design doc, zero code written.
- Regenerate/spot-check OpenAPI-derived TypeScript types — the schema has drifted since it was last checked.
- Balance-change SignalR push (currently polls, correctly, everywhere) — non-blocking.

---

## 8. Commands

```bash
# In src/Orchestrator/wwwroot-next/
npx vitest run           # full suite — expect 110 passed, 0 failed
npx tsc --noEmit         # type check — expect silent/clean
npm run build             # eslint . && tsc -b && vite build — the real production build (lint errors fail it)
npm run lint               # NOT YET RUN AGAINST THIS CODEBASE — expect findings; see §3

# Deploy (production server, srv020184)
sudo bash install.sh --env-file /etc/decloud/install.env --ingress-domain vms.stackfi.tech --enable-wireguard
# NEVER put --ingress-domain's value (or any side-effect flag's value) directly in the
# env file — see §5.14. The installer will now reject an env file that tries.
sudo systemctl restart decloud-orchestrator
sudo journalctl -u decloud-orchestrator -f
curl -s http://localhost:5050/health
```

**Frontend dependencies are tracked by the build, not by you (since 2026-07-25).** This section previously asked you to manually confirm `npm ci` had run after any dependency change, because `BuildFrontendNext` only ran it when `node_modules` was *absent* — so a stale-but-present tree silently skipped a new dependency and failed later with an unrelated-looking `TS2307`. That gate used "directory exists" as a proxy for "tree matches lockfile". It has been replaced by a `RestoreFrontendNext` target (and a legacy twin) keyed on `Inputs=package-lock.json` / `Outputs=node_modules/.package-lock.json`, so a pull that changes the lockfile reinstalls and one that doesn't, skips. Verified on the production server: touch the lockfile → next build installs (96s) → the one after skips (6s). **No ritual required.** One edge deliberately unguarded: a *partially* deleted `node_modules` still has `.package-lock.json` and reads as current — the symptom is loud (`TS2307`) and the recovery is `rm -rf node_modules`.

---

## 9. Where to start

If nothing more urgent has come up since 2026-07-24: run `npm run lint` for the first time and see what `eslint-plugin-react-hooks` actually finds (item 7 above) — it's cheap, and it's the one open item most likely to prevent the *next* version of the bug in §5.13. After that, the Deploy Customize parity gaps (item 1) are the most valuable next feature work, and Marketplace is the most valuable next page to migrate in Phase 5.

Whatever you pick: ground first. Every fact in this document was pulled from real code or a real response, and the moment any of it stops matching what you observe, the observation wins — go find out why, don't patch around the discrepancy.
