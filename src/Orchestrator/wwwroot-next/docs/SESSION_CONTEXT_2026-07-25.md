# Session Context — DeCloud Frontend Remake (chat of 2026-07-25)

**What this is:** the living context/handoff doc for *this* chat, per the operating discipline
("create a context document for each chat"). It is *not* a replacement for `AGENT_HANDOUT.md`
(the fuller cold-start doc) — it records what *this* session grounded, decided, and did, and
should be read alongside the handout. Newest facts win; **if this disagrees with the repo, the
repo wins.** All patches/files referenced are in `/mnt/user-data/outputs`.

---

## 0. Grounding done this session (so it isn't re-paid)

- **Repo cloned:** `github.com/bekirmfr/DeCloud.Orchestrator` (public), shallow clone at
  `/home/claude/repo`, **HEAD `06dae96`**. `DeCloud.Shared` (e.g. `RelayObligationState`,
  `TemplateVariable`, `JsonOptions`) and the **NodeAgent** are SEPARATE repos, NOT in this
  checkout — their types were grounded from usage or from files the owner uploaded.
- **No build toolchain in the sandbox:** no `dotnet`, no `node_modules`. Nothing here was
  compiled or test-run. Every patch is "grounded + reviewed, not built." **Owner must
  `dotnet build` / `npm run build` + `npm test` after applying.** (This bit once — a TS6133
  unused import slipped and the owner's build caught it; see Workstream C.)
- **Rule #1 honored throughout:** read the real file before writing against any DTO/endpoint/enum.

## 1. State of play (grounded)

Strangler migration: legacy vanilla-JS at `/`, new React+TS at `/app/*`, retiring page-by-page.
**Phase 3 (connect → deploy → operate) is live in production and now materially more complete
this session:** Direct Access (Ports) + Custom Domains built and verified; Deploy Customize at
**4 of 5 parity gaps** done and live-verified. Phases 4 (SSG landing), 5 (marketplace/etc.),
6 (cutover) not started — **Marketplace is the recommended next move** (see Workstream D).

## 2. What this session did (four workstreams, all verified live unless noted)

### A. Direct Access (Ports) + Custom Domains — the original "live regression"
The two features had no UI in either app since the 2026-07-24 legacy retirements. Built as
**URL-driven modal-routes** off `/app/vms/:id` (first child-route pattern in the app).
- **Renamed `/access` → `/ports`** (all 5 refs; component `DirectAccessModal` kept). The journal
  §8 had explicitly warned `/access` collides with the working `VmAccessInfo` "Connection" panel
  and re-creates the naming ambiguity that hid the regression; `/ports` matches the button label.
- Grounded wire traps: **`PortProtocol` serializes NUMERIC** (no converter) → `portProtocol.ts`
  normalizer; **`CustomDomainStatus` serializes STRING** (has converter) → `domainStatus.ts`;
  **direct-access DELETE = 204 raw**, domains DELETE = `ApiResponse<bool>` → one `api()`
  `unwrap()` change to treat 204 as success.
- **Verified live:** `/app/vms/:id/ports` loads on cold nav; add + remove port work; a custom
  domain (`coolify-app-test.stackfi.tech`) resolved to the VM.
- Patches: `direct-access-and-domains.patch` (build), `ports-rename-and-docs.patch` (rename +
  journal §8 → RESOLVED).

### B. Relay port-allocation failure — four layers, each fixed at its own boundary
The new Ports UI was the first client to exercise CGNAT quick-add; it 400'd after ~30s. Chased
down honestly:
1. **Orchestrator fail-fast** (`fail-fast-port-allocation.patch`, SHIPPED + verified): the waiter
   `DirectAccessService.WaitForPortAllocationAsync` polled only for success and turned a 1-second
   relay *rejection* into a 30-second generic *timeout*. Added a bounded `_allocatePortFailures`
   outcome record (DataStore) written on failed ack (NodeService) and consumed by the waiter,
   which now returns `(int Port, string? FailureReason)` and fails fast with the real reason.
2. **Root cause** (relay NodeAgent, separate repo): the relay node's NodeAgent called its **own
   local relay VM's** `add-port-forward` API with **no Bearer token** → relay VM fail-closes
   `401`. Not iptables/timeout — auth.
3. **NodeAgent fix** (`PortForwardingManager.cs`, full file delivered): the node authenticates to
   its local relay VM using the **relay-obligation token it already holds locally** (delivered at
   registration via `NodeRegistrationResponse.ObligationStates` → local SQLite), NOT via
   orchestrator per-command shipping — correct boundary (the node *hosts* that VM). All three
   relay-VM POSTs (add/remove/flush) now attach the Bearer.
4. **Casing fix** (folded into the same file): first read used `"AuthToken"` (PascalCase) but
   `StateJson` is camelCase (`"authToken"`). Fixed by deserializing the shared
   `RelayObligationState` with `DeCloud.Shared.Json.JsonOptions.Wire` (case-insensitive, matches
   the writer). **Verified live: "✓ Relay VM forwarding created."**
5. **JsonOptions unification** (`statejson-shared-jsonoptions.patch`, orchestrator): routed ALL
   `StateJson` (de)serialization — 2 writers + 4 readers (RelayController ×3, the two resolvers,
   WgPublicKeyResolver, NodeService, SystemVmObligationService) — through `JsonOptions.Wire`, so
   the wire format matches on both sides *by construction*, not by hope. Verified (Debian VM
   provisioned cleanly).
- **Security invariant (do not relitigate):** the relay VM's fail-closed `401` is CORRECT and was
  never weakened; every fix *delivered the credential* the boundary demands.

### C. Phase-3 Deploy Customize — 4 of 5 parity gaps (all live-verified); 5th is gated
Base already had cpu/mem/disk/tier/bandwidth/GPU + one-click + Customize + server price.
1. **Description cards** (`deploy-description-cards.patch`): per-selector hint copy
   (`CUSTOMIZE_HINTS`), grounded in real tier semantics (QualityTier inverse, etc.). Verified.
2. **OS image** (`deploy-os-image.patch`): "Operating system" — agnostic templates get a select
   from `GET /api/system/images`; OS-pinned templates render read-only. `resolveImageId(pinned,
   chosen)` (pinned wins). Empty → server applies platform default (`VmService` final fallback),
   so no default id hardcoded. **Verified live (deployed Debian, not the ubuntu default).**
3. **Scheduling constraints** (`deploy-scheduling-constraints.patch` +
   `constraint-vocabulary-operator-types.patch` backend): fully data-driven `ConstraintBuilder`
   from `GET /api/vms/constraint-vocabulary`. **Backend fork (c):** added `OperatorTargetTypes`
   (operator → accepted value-type names) derived from each operator's own `AcceptsTargetType`
   predicate, so the builder filters operators with zero client-side mirror of the rules.
   Template-imposed constraints read-only; user rows below. **Verified live (deployed onto a
   GPU-present node with region-in-`eu` — both boolean-scalar and list value shapes).**
4. **Replication factor** (`deploy-replication-factor.patch`): a "Durability" select over the
   server-accepted set `{0,1,3,5}` (grounded against `VmService`'s clamp; else → 3), labelled
   Ephemeral / N copies. **Verified live + deep**: Mongo showed `Spec.ReplicationFactor: 1` and
   the block-store manifest confirmed (LazysyncStatus, ConfirmedRootCid, 1713 chunks) — actually
   replicating, the machinery `factor==0` skips.
5. **Template Variables — grounded, deliberately NOT built (Marketplace-gated).** The
   platform-vs-user discriminator is **resolver-key membership** in
   `GET /api/marketplace/platform-variables`, NOT `TemplateVariable.kind` (that's
   `VariableKind {Static,Dynamic}` = resolution timing; the frontend comment was wrong and was
   corrected in `deploySubmit.ts`). **Every declared variable in every current template is a
   platform variable** → the form would render empty today. User-declared variables are a
   Marketplace-era concept. Building an empty form now = UI for data that doesn't exist.
- **Recurring latent-bug pattern (fixed each time):** `customSpec` must forward the template's
  recommended fields (image, bandwidth, GPU, **replication**, **constraints**) or customizing
  silently reverts them to spec defaults. Each slice forwards its field.
- **Testing:** pure helpers preferred (`resolveImageId`, `operatorsForTarget`, `valueIsList`,
  `REPLICATION_VALUES` == server set guard, `CUSTOMIZE_HINTS` completeness). Owner: run
  `npm test` for the exact count (per-slice additions recorded in the journal; do not trust a
  single hard number across the session — reconcile by running the suite).
- **Build discipline:** one `TS6133` unused import (`Constraint` in `useDeploy.ts`) slipped
  because nothing was compiled here; the owner's build caught it → `fix-unused-constraint-import.patch`.
  **Run `npm run build` right after applying each FE patch, before eyeballing.**

### D. Marketplace (Phase 5) — plan grounded, not started (recommended next)
Browse + detail over the template catalogue, feeding the existing deploy flow. Earns "first":
retires `template-detail.js` (last stale client-side pricing table) and unhardcodes **three**
`/marketplace/platform-general/deploy` links (`AppShell.tsx:48`, `DashboardPage.tsx:89,148`).
- Endpoints all exist + `AllowAnonymous`: `GET /templates` (`category`/`requiresGpu`/`tags`/
  `search`/`featured`/`sortBy`/`limit` → `VmTemplateSummary[]`), `GET /categories`,
  `GET /templates/{slug}` (full, already wired to deploy), `GET /templates/featured`,
  `GET /reviews/template/{id}`.
- Current: `marketplace` route is commented out in `routes.tsx`; deploy sub-route live; no browse
  or detail page yet.
- Slices, smallest-first: (1) browse `/app/marketplace` (grid + URL filters + featured);
  (2) detail `/app/marketplace/:slug` (**pricing via the existing `usePriceEstimate` /
  `POST /api/system/pricing/calculate` — NEVER a client pricing table**) → Deploy;
  (3) unhardcode the 3 Deploy links → browse.
- **Honest caveat:** browse/detail does NOT by itself create variable-declaring templates.
  Whether it unblocks template Variables (and live-verification of the OS-pinned + locked-
  constraint tiers) depends on **what's actually in the live catalogue**. If it's only seeded
  platform templates, those tails wait on template *authoring* (`POST /templates/create` +
  review flow = My Templates + admin), a larger separate Phase-5 scope. **Check catalogue
  contents before promising Marketplace clears the Phase-3 backlog.**

## 3. Hard constraints to honor while building (do not relitigate)

Still-valid invariants from prior sessions, plus what this session added (★):
- **Never compute pricing/billing client-side.** Call `POST /api/system/pricing/calculate`
  (reuse `usePriceEstimate`). The stale `template-detail.js` tables are exactly this sin.
- **Three enums (`VmStatus`/`VmPowerState`/`VmAction`) serialize as raw numbers** → route through
  `features/vms/vmStatus.ts`; send `VmAction` as an ordinal (`{"action":1}`).
- **`QualityTier` is inverted** (Guaranteed=0 best … Burstable=3); `BandwidthTier` is not. A
  *default* seeds; a *minimum* constrains — never conflate.
- **Server data lives in TanStack Query only**; SignalR patches the cache; new broadcasts need
  `HubProvider.onreconnected` invalidation. **Hooks above early returns** (`enabled:` to wait).
- **Inline token-driven styles** (Meridian), not maybe-missing class names. Read `frontend-design`
  SKILL before UI.
- **Retire, don't deprecate** — but do NOT delete stranded legacy modules until their `/app`
  replacement ships (Phase 6).
- **A write isn't "done" until its button is clicked against the real API.**
- ★ **`StateJson` (de)serialization goes through `DeCloud.Shared.Json.JsonOptions.Wire`** (camelCase,
  case-insensitive) on BOTH sides — never hand-roll per-call options.
- ★ **Relay VM API is fail-closed** (`Bearer RelayObligationState.AuthToken` on every POST). A node
  authenticates to its OWN local system VM with the token it holds locally (registration-delivered),
  NOT orchestrator per-command shipping. **Never weaken the 401.**
- ★ **`customSpec` must forward template-recommended fields** (image / bandwidth / GPU / replication /
  constraints) or customizing silently reverts them to spec defaults.
- ★ **The constraint builder is data-driven** from `GET /api/vms/constraint-vocabulary` (now incl.
  `operatorTargetTypes`). Don't hardcode targets/operators. The one thing the vocab lacks is value
  *arity* (list vs scalar) → small commented `valueIsList` set; server validates the value.
- ★ **Replication valid set is `{0,1,3,5}`** (else clamps to 3); `0` = ephemeral (real data loss).
- ★ **Deploy spec carries `imageId` / `constraints` / `replicationFactor`**; `environmentVariables`
  is separate. Platform-vs-user variable split = resolver-key membership (§2C.5), not `kind`.

## 4. Artifacts (all in /mnt/user-data/outputs)

**Code patches** — orchestrator: `fail-fast-port-allocation.patch`,
`statejson-shared-jsonoptions.patch`, `constraint-vocabulary-operator-types.patch`.
Frontend: `direct-access-and-domains.patch`, `ports-rename-and-docs.patch`,
`deploy-description-cards.patch`, `deploy-os-image.patch`, `deploy-scheduling-constraints.patch`,
`deploy-replication-factor.patch`, `fix-unused-constraint-import.patch`.
NodeAgent (separate repo): **`PortForwardingManager.cs`** (full file — the final version incl. the
JsonOptions.Wire casing fix).
**Journal patches** (`FRONTEND_REMAKE_IMPLEMENTATION.md` §0/§8/§9): `journal-description-cards`,
`journal-os-image`, `journal-scheduling-constraints`, `journal-replication`. The **finalized
journal file** is provided directly (all applied).

## 5. Open items / owner's next steps

- **Build + test after applying FE patches** (`npm run build` + `npm test`) — get the real test
  count; the sandbox couldn't run either.
- **Marketplace next** (Workstream D) — start with the browse page; check live catalogue contents
  to see how much of the Phase-3 tail it unblocks.
- **template Variables:** build only once a variable-declaring template exists (post-Marketplace
  or authoring). The mechanism is grounded (§2C.5).
- **Live-verify the untested tiers:** OS-pinned image (read-only) and locked template constraints —
  both need a template that pins/constrains, i.e. Marketplace.
- **Quick checks:** replication price row scaling at factor 3/5; `flush-port-forwards` auth on a
  relay reconcile (inference-clean — shares the verified `remove` helper).
- **Still unbuilt (Phase-3 tails):** node-agent metrics push (panel correctly gated on data);
  terminal + file-browser in-app routes (still on legacy standalone pages).
