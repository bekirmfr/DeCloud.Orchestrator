# Session Context — DeCloud Frontend Remake (chat of 2026-08-07)

**What this is:** the living context/handoff doc for *this* chat, per the operating discipline
("create a context document for each chat"). It records what *this* session grounded, decided,
and did; read it alongside `FRONTEND_REMAKE_DESIGN.md` / `FRONTEND_REMAKE_IMPLEMENTATION.md`
(the authoritative plan + journal). The dated `SESSION_CONTEXT_2026-07-25.md` is the previous
chat's snapshot and is kept as history — this file does not replace it. Newest facts win;
**if this disagrees with the repo, the repo wins.** All patches/files referenced are in
`/mnt/user-data/outputs`.

---

## 0. Grounding done this session (so it isn't re-paid)

- **Repo:** shallow clone of `github.com/bekirmfr/DeCloud.Orchestrator` at `/home/claude/repo`.
  Frontend `src/Orchestrator/wwwroot-next/` (React+TS+Vite, basename `/app`); backend
  `src/Orchestrator/`; legacy SPA `src/Orchestrator/wwwroot/` (readable, the reference for
  on-chain flows). **`DeCloud.Shared` and the NodeAgent are SEPARATE repos, NOT in this
  checkout** — their types (`HardwareInventory`, `JsonOptions`, …) were grounded from usage or
  from files the owner uploaded.
- **No build toolchain here:** no `dotnet`, no `node_modules`. Nothing was compiled or test-run.
  Deliverables are `git`-apply patches or full-file drop-ins; **the owner applies + builds
  (`dotnet build` / `npm run build`) — those are the gates.** This bit twice this session (the
  ethers-v6 typing errors below; a `templateForm` test import) — see §3.
- **Rule #1 honored:** read the real file/DTO/endpoint before writing against it. Every contract
  cited below was checked in source, not recalled.

## 1. State of play (grounded)

Strangler migration: legacy vanilla-JS at `/`, new React+TS at `/app/*`, retiring page-by-page.
**Phase 3 (connect → deploy → operate) is live.** After this session **Phase 5 (supporting
paths) is largely done** — Marketplace, template authoring + lifecycle + admin review/inspect,
template earnings, **Nodes** (my-nodes/search/detail + admin manager/inspect), **Wallet**
(read-only + native deposit/earnings-withdraw), and **Profile** all shipped; **Settings** is the
last un-migrated supporting page. Two **Phase-6** pieces landed early: signed-in `/`→`/app`
redirect, and **native on-chain money-moves in `/app`** (see §2E). Phase 4 (SSG landing) and the
full Phase-6 cutover are not started.

## 2. What this session did (workstreams — verified live unless noted)

### A. Template authoring, lifecycle, admin review + inspect
My Templates gained the authoring **parity** fields and the full **lifecycle**:
`PATCH templates/{id}/publish` (Draft/Rejected → PendingReview if `IsCommunity` else Published),
`cancel-review`, `revise` (community-Published → new Draft with `ParentTemplateId`);
`IsCommunity = !isAdmin`. **Admin:** pending queue (`GET /templates/pending`,
`Authorize(Roles=Admin)`) with approve/reject (reason required), and a **read-only inspect page**
(`/app/admin/templates/:id`: tabbed role/composed cloud-init, variables table, artifacts with
full SHA + base64 decode) — inspect is *not* the author edit form.
- **Grounded correctness fix:** edit/inspect address by **`id`, never slug** —
  `GetTemplateBySlugAsync` filters `Slug==slug && Status==Published`, so drafts are unresolvable
  by slug and slugs collide across authors. `t.slug || t.id` links 404'd on drafts → changed to
  `t.id` (`template-edit-by-id-fix.patch`).
- Patches: `template-authoring-slices-1-2`, `template-authoring-form`,
  `template-parity-all-six`, `template-form-ux-category-resources-gpu`,
  `template-form-help-and-starter`, `template-validation-and-help-fix`,
  `template-lifecycle-slice3`, `template-admin-review-slice4`, `admin-template-inspect`,
  `my-templates`; full files `MyTemplatesPage.tsx`, `CreateTemplatePage.tsx`,
  `AdminTemplateInspectPage.tsx`, `templateForm.ts` + `templateForm.test.ts`.

### B. Mongo crash-loop incident (ops)
Orchestrator crash-looping on startup. Root cause: a `usageRecords` query (2,508 tiny records)
took **11,753 ms** to transfer over a pathologically slow Atlas link, past the 10 s socket
timeout → unhandled → crash. Fixes: compound index `idx_settled_created
{SettledOnChain:1, CreatedAt:-1}` auto-created in `DataStore` (`usage-index-autocreate.patch`),
and Mongo **socket timeout 10s→60s** (`mongo-socket-timeout.patch`). **Unresolved and the top
operational open item:** *why* the link is that slow — the timeout bump is a guard, not a cure.

### C. Nodes migration (slices 1–3) + legacy delete + deploy-to-node
`/app/nodes` = my-nodes + fleet search tabs. **my-nodes filters client-side** by wallet against
`GET /api/nodes` because **no owner-scoped endpoint exists**. **Node detail** (`/app/nodes/:id`)
is **owner-aware**: owner → full + earnings; non-owner → trimmed availability/uptime — because
`GET /api/nodes/{id}` returns earnings to *any* caller, so the UI gates what the endpoint doesn't
(flagged, not fixed — backend over-exposure). **Admin:** `/app/admin/nodes` + `DELETE
/api/nodes/{id}` (the **only** admin node action — `deregister` is node-self via a `node_id` JWT
claim; there is **no suspend** endpoint) + a full inspect page. **Deploy-to-node:** "Deploy here"
→ `/deploy?node=` chooser → `DeployTemplateRequest.NodeId`. **Legacy `marketplace.js` retired**
(all node code, despite the name).
- Patches: `nodes-page-slice1`, `nodes-detail-slice2`, `nodes-admin-slice3`,
  `node-detail-views-owner-admin`, `legacy-nodes-remove`, `deploy-to-node`,
  `deploy-to-node-source-chooser`.
- **Open:** `HardwareInventory` shape (in `DeCloud.Shared`, not in checkout) blocks CPU/GPU on
  the detail page; repo-deploy doesn't yet consume `?node=`.

### D. Template earnings — from the settlement ledger, not a counter
`GET /api/marketplace/templates/my/earnings` → `{templateId:{net,gross,deploys}}`; My Templates
shows "Earned N.NN USDC · K paid deploys" (net author cut). **`DeploymentCount × TemplatePrice`
is wrong** (self-deploys skipped when `OwnerId==AuthorId`, failed settlements, price changes).
Truth is summed from `usageRecords`: template fees are written by `SettleTemplateFeeAsync` with a
**zero-length period** (`PeriodStart==PeriodEnd`) crediting the author's revenue wallet → the
query finds those, maps `VmId → VirtualMachine.TemplateId` (VMs soft-deleted/retained, so history
resolves), sums `NodeShare` (net) + `TotalCost` (gross). Reading the ledger can't drift from
on-chain reality. Patches: `template-earnings-backend` (5 files, incl. `TemplateEarnings.cs`),
`template-earnings-frontend`; full `useTemplates.ts`.

### E. Wallet (slices 1 & 2) + sidebar balance + profile + modal polish
- **Slice 1 (read-only `/app/wallet`):** balance hero + runway/burn + breakdown + pending
  deposits + recent usage + deposit-details, all from `useBalance` + new `useDepositInfo`
  (`GET /api/payment/deposit-info`). `wallet-page-slice1.patch`; usage costs at 4 decimals
  (`wallet-usage-precision.patch`).
- **Slice 2 (native on-chain deposit + earnings withdrawal) — FUND-MOVING:**
  `features/billing/paymentClient.ts` (ethers v6) is a faithful port of `payment.js` with every
  guard: **deposit** = network → **`frozen()`/`replacementContract` guard** → min-deposit → USDC
  balance → allowance/`approve` → `escrow.deposit`, Polygon gas floors (×1.2, 30-gwei min, 50/30
  fallback); **earnings withdrawal** = `escrow.nodeWithdraw(0)`. Exposed the ethers **signer**
  (`getSigner()`, already internal for SIWE) on the wallet adapter + auth context. Wrong network
  **fails closed** (deliberate deviation — no auto-add-chain with guessed RPC). **Verified live
  on Polygon Amoy: deposit + withdrawal round-tripped.** `wallet-deposit-withdraw-slice2.patch`
  + full `paymentClient.ts` (the build-fixed drop-in).
- **Sidebar balance card + balance modal + header profile menu + `/app/profile`**
  (`sidebar-balance-profile.patch`, 6 new files): `SidebarBalance`, `BalanceModal`, `ProfileMenu`,
  deterministic address-hashed `avatar.ts`, `useProfile` (`GET /api/user/me`), `ProfilePage`
  (identity + quotas as Current-vs-Max bars; **roles from the session** — `/me` omits them).
- **Modal polish/consolidation** (`wallet-modals-polish.patch`): one shared `DepositModal`
  (legacy-style info card + prominent action + success state) reused by the Wallet page and the
  balance modal — one deposit experience, no duplicated on-chain logic. Driven by owner feedback
  that "all the legacy UI felt much better."

### F. Node earnings scoping (security) + Settings page + light-theme fix
Three follow-on pieces after the docs were squared:
- **Node earnings/credentials scoping (server-side security).** `GET /api/nodes/{id}` **and**
  `GET /api/nodes` returned the full `Node` to any authenticated caller — leaking every operator's
  earnings (`PendingPayout`/`TotalEarned`) *and* credentials (`ApiKeyHash`/`CurrentJti`/key
  timestamps). `Node.WithoutOwnerPrivateData()` = cache-safe **shallow copy** (never mutate the
  shared `ActiveNodes` reference the hot path returns) with those cleared; controller scopes both
  endpoints to owner (wallet claim vs `Node.WalletAddress`, case-insensitive) / admin. UI gate is
  now defense-in-depth. **Fail-open on future fields** is the stated residual (a DTO would fail
  closed but needs a coordinated FE change). `node-earnings-owner-scoping.patch` (+41/−2).
- **Settings (`/app/settings`)** — the last supporting page. `useTheme` writes `index.html`'s
  pre-paint contract (`dc-theme`; Light/Dark/System). Language deferred (no i18n layer → no fake
  selector). New `features/settings/{SettingsPage.tsx,useTheme.ts}` + `settings-page-wiring.patch`
  (cut against the owner's current `routes.tsx`/`AppShell.tsx` — drifted from my clone).
- **Light-theme regression fixed — VERIFIED.** Not hardcoded dark styles: (1) `color-scheme` was
  never pinned, so native controls (checkboxes, steppers, select chrome, un-styled input/select
  backgrounds) followed the OS not `data-theme`; (2) only `.field input/textarea` was themed, so
  `<select>` + repeatable-row inputs fell to raw native rendering. Fixed in the base layer:
  `color-scheme` pinned to `data-theme`, and `select`/bare inputs themed globally.
  `theme-native-controls-fix.patch` (+42/−0). **Lesson: an un-styled native control follows the
  OS `color-scheme`, not `data-theme` — pin it.**

## 3. Hard constraints to honor while building (do not relitigate)

Still-valid invariants from prior sessions (see `SESSION_CONTEXT_2026-07-25.md` §3), plus what
this session added (★):
- **Never compute pricing/billing client-side** (`POST /api/system/pricing/calculate`).
- **Three enums serialize numeric** (`VmStatus`/`VmPowerState`/`VmAction`) → route through
  `vmStatus.ts`; `VmAction` as ordinal.
- **Server data lives in TanStack Query only**; SignalR patches the cache; hooks above early
  returns.
- **Retire, don't deprecate** — don't delete stranded legacy modules until the `/app` replacement
  ships.
- **A write isn't "done" until its button is clicked against the real API.**
- ★ **On-chain money code is faithfully ported from `payment.js` and MUST be testnet-verified.**
  Keep the `frozen()`/`replacement` guard, min-deposit, allowance/approve, and gas floors; wrong
  network **fails closed**. Config from `GET /api/payment/deposit-info` — nothing hardcoded; the
  wallet is the final signing gate.
- ★ **ethers v6 types `Contract` methods as possibly-`undefined`** (dynamic ABI) → strict TS
  errors on every call. Define explicit `UsdcContract`/`EscrowContract` interfaces and cast
  `new Contract(...) as unknown as X`. (First Slice-2 build failed on 14 such errors.)
- ★ **The ethers signer is on the auth context** (`getSigner()`) — reuse it; don't re-reach into
  AppKit. `walletState.ts` remains the only module talking to AppKit/ethers.
- ★ **Earnings come from the settlement ledger, never a stored counter** (§2D).
- ★ **Slug is Published-only, public addressing** → author/admin edit & inspect must use `t.id`.
- ★ **No owner-scoped node endpoint** → my-nodes filters client-side; `GET /nodes/{id}` returns
  earnings to any caller → gate them in the UI. `DELETE /nodes/{id}` is the only admin node action.
- ★ **`noUncheckedIndexedAccess` is on** — guard index/`Record` access with `?? fallback`; bind
  `earnings?.[id]` to a `const` before member access; `wallet.address` needs an inline
  `wallet.kind==="connected" ? wallet.address : null` (a separate boolean doesn't narrow).
- ★ **Patch base discipline (§6.13 of the impl doc):** hot shared files (`routes.tsx`,
  `AppShell.tsx`, the auth pair, `WalletPage.tsx`, `useTemplates.ts`, `MyTemplatesPage.tsx`,
  `DataStore.cs`) drift from my clone's `HEAD` → **owner uploads current file, diff against exact
  bytes**; when basing off a file I edited earlier this turn, **reverse this turn's edits** to
  build the base (don't use `HEAD`, or the patch re-bundles applied changes).

## 4. Artifacts (this session's, all in /mnt/user-data/outputs)

- **Template authoring/lifecycle/admin:** `template-authoring-slices-1-2.patch`,
  `template-authoring-form.patch`, `template-parity-all-six.patch`,
  `template-form-ux-category-resources-gpu.patch`, `template-form-help-and-starter.patch`,
  `template-validation-and-help-fix.patch`, `template-lifecycle-slice3.patch`,
  `template-admin-review-slice4.patch`, `admin-template-inspect.patch`,
  `template-edit-by-id-fix.patch`, `my-templates.patch`; full files `MyTemplatesPage.tsx`,
  `CreateTemplatePage.tsx`, `AdminTemplateInspectPage.tsx`, `templateForm.ts`,
  `templateForm.test.ts`; build fixes `build-fix-templateform-test.patch`,
  `fix-marketplace-test-vitest-import.patch`.
- **Mongo incident:** `usage-index-autocreate.patch`, `mongo-socket-timeout.patch`.
- **Nodes:** `nodes-page-slice1.patch`, `nodes-detail-slice2.patch`, `nodes-admin-slice3.patch`,
  `node-detail-views-owner-admin.patch`, `legacy-nodes-remove.patch`, `deploy-to-node.patch`,
  `deploy-to-node-source-chooser.patch`.
- **Template earnings:** `template-earnings-backend.patch`, `template-earnings-frontend.patch`,
  full `useTemplates.ts`.
- **Wallet / shell identity:** `wallet-page-slice1.patch`, `wallet-usage-precision.patch`,
  `wallet-deposit-withdraw-slice2.patch`, full `paymentClient.ts`, `sidebar-balance-profile.patch`,
  `wallet-modals-polish.patch`.
- **Build fixes:** `build-fix-tsc-eslint.patch`, `build-fix-help-record.patch`.
- **Security / Settings / theme (workstream F):** `node-earnings-owner-scoping.patch`;
  `SettingsPage.tsx` + `useTheme.ts` (new) + `settings-page-wiring.patch`;
  `theme-native-controls-fix.patch`.
- **Docs (this task):** updated `FRONTEND_REMAKE_IMPLEMENTATION.md`, `FRONTEND_REMAKE_DESIGN.md`,
  `BACKEND_SERVING_SPEC.md`, and this file.
- *(The outputs dir also carries prior-session artifacts — marketplace/deploy/terminal/relay
  patches — and some authored-template-composition + relay decision-log groundwork; the journal
  and decision-logs are authoritative on those.)*

## 5. Open items / owner's next steps

- **Build + testnet-verify after applying** — especially `paymentClient.ts` (fund-moving; the
  sandbox can't build). `npm run build` + `npm test`; get the real test count.
- **Atlas link latency — chase the root cause** (highest-impact). The index + timeout stopped the
  crash; the slow link itself is unexplained. A `{NodeId:1,PeriodStart:1}` index on `usageRecords`
  would also speed the earnings scan.
- **Settings** shipped (theme toggle live; language deferred to i18n) — pending owner build-verify.
- **✅ Node earnings/credentials over-exposure — RESOLVED** server-side (workstream F); residual is
  fail-open on future owner-private fields.
- **Wire when wanted:** `withdrawBalance` (unused-deposit withdrawal, ABI present); repo-deploy
  `?node=` pinning; `templateForm` unit tests owed.
- **HardwareInventory shape** — get it (from `DeCloud.Shared`) to show CPU/GPU on node detail.
- **Residual legacy-only money-moves** (admin `withdrawPlatformFees`, frozen-contract migration,
  `withdrawBalance`) still gate full monolith deletion at Phase 6.
