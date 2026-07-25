# Session Context — DeCloud Frontend Remake (chat of 2026-07-25)

**What this is:** the living context/handoff doc for *this* chat, per the operating discipline
("create a context document for each chat"). It is *not* a replacement for `AGENT_HANDOUT.md`
(the fuller cold-start doc) — it records what *this* session grounded, decided, and did, and
should be read alongside the handout, not instead of it. Newest facts win; if this disagrees
with what you observe in the repo, the repo wins.

---

## 0. Grounding done this session (so it isn't re-paid)

- **The real repo is reachable and was cloned.** `github.com/bekirmfr/DeCloud.Orchestrator`
  is public; shallow-cloned to `/home/claude/repo`. **HEAD = `06dae96`, 2026-07-25 15:12 +0300.**
  This is the same tree the four planning docs describe ("as of 2026-07-25"), so the docs are
  current, not stale, for this checkout. Verify HEAD hasn't moved before trusting that again.
- **Confirmed against real code (not just the docs):**
  - `wwwroot-next/src/features/` = `billing dashboard deploy ssh-keys vms` — no `terminal`,
    no `files`, no `direct-access`, no `domains`. Matches the docs exactly.
  - Grep of the whole new app (`wwwroot-next/src/`) for
    `direct-access|directAccess|DirectAccess|central-ingress|customDomain|CustomDomain|portMapping`
    returns **zero hits**. The "Direct Access / Custom Domains are unbuilt in the new app"
    claim is true on disk, not just asserted.
- **The actual repository, not the docs, is the source of truth for code shape.** Before writing
  any client code against a DTO/endpoint/enum, read the file in `/home/claude/repo`, per the
  project's #1 rule. The docs' §4 cheat-sheet is a map, not the territory.

## 1. State of play (grounded summary)

Incremental strangler migration: legacy vanilla-JS app at `/`, new React+TS app at `/app/*`,
retiring the old page-by-page. Phase 3 (the "connect → deploy → operate" spine) is functionally
live in production. Phases 4 (SSG landing), 5 (marketplace/my-templates/nodes/settings/admin),
6 (cutover) not started.

## 2. Candidate next-work items (from DESIGN §14 / IMPL §8 / HANDOUT §7), grounded

Ordered by my read on value+urgency, for discussion — not yet chosen:

1. **🔴 LIVE REGRESSION — Direct Access (Smart Port Allocation) + Custom Domains have no UI in
   either app since 2026-07-24.** Backends live (`/api/vms/{id}/direct-access`,
   `/api/central-ingress/vm/{id}/domains`); legacy modals stranded (openers were deleted with
   the VM table); new app has no replacement. Designed as `/app/vms/:id/access` and
   `/app/vms/:id/domains` (modal-routes), unbuilt. **Directly serves the Minecraft/game-server
   vision** — `DirectAccessService.GenerateConnectionString` has explicit cases for `minecraft`,
   `mysql`, `postgresql`, `mongodb`, `redis`, `rdp`; that whole surface is currently dark
   (e.g. no way to open port 25565). This is a Phase-3 *build* gap, not housekeeping.
2. **Deploy Customize parity gaps** — replication factor, OS image selection
   (`GET /api/system/images`), template Variables / user env vars
   (`GET /api/marketplace/platform-variables` grounds the platform-vs-user split), description
   cards, and scheduling constraints (the constraint-row builder — a real sub-effort, was
   `constraint-builder.js`). Needed to call Phase 3 "done".
3. **Marketplace migration (Phase 5, recommended first of Phase 5)** — retires the last stale
   pricing-table copy (`template-detail.js`) and frees the shell's Deploy button from
   hard-coding `platform-general`.
4. **Terminal + file browser in-app routes** (`/app/vms/:id/terminal`, `/app/vms/:id/files`) —
   also stranded/unreachable since 2026-07-24; designed, zero code. Keep legacy pages serving
   until these ship (serving-spec §6 gate).

## 3. Hard constraints to honor while building (do not relitigate)

- **Never compute pricing/billing client-side.** Call `POST /api/system/pricing/calculate`.
- **Three enums (`VmStatus`/`VmPowerState`/`VmAction`) serialize as raw numbers.** Route all
  status/power/action handling through `features/vms/vmStatus.ts`
  (`normalizeStatus`/`normalizePowerState`/`vmActionOrdinal`). `VmAction` must be *sent* as an
  ordinal in request bodies (`{"action":1}`, not `{"action":"Stop"}`).
- **`QualityTier` is inverted** (Guaranteed=0 best … Burstable=3 worst); `BandwidthTier` is not.
  A *default* seeds a selection; a *minimum* constrains choices — never conflate them.
- **Server data lives in TanStack Query only**, keyed by resource; SignalR events patch the
  cache (`qc.setQueryData`), never parallel `useState`. New broadcast types need
  reconnect-invalidation in `HubProvider.onreconnected`.
- **Hooks above early returns**, always (`enabled:` to make a query wait).
- **Ship components with inline token-driven styles** (Meridian tokens), not class names that
  may not exist. Read `frontend-design` SKILL before building UI.
- **Retire, don't deprecate:** a migrated page deletes its legacy div + module + inline handlers
  + only-its `window.*` exports in the *same* change. But **do NOT delete the stranded
  direct-access/terminal/domains legacy modules** — they are the only working implementation;
  they die at Phase 6, after their `/app` replacements ship.
- **Before writing against any file, read it in `/home/claude/repo`.** Before any bulk delete,
  grep the range for callers *outside* it.
- **A write action isn't "done" until its button is clicked against the real API** (the VmAction
  bug lived invisibly because reads were exercised and writes never were).

## 4. Decisions / actions taken this session

**Built the two stranded features as `/app` modal-routes (the live regression's fix).**
All grounded against real backend source in the clone, verified with the real toolchain
(`tsc -b` clean, `eslint .` 0, `vitest` 122 passing (110→122), `npm run build` succeeds).
11 files, +890 lines. Patch: `direct-access-and-domains.patch`.

**Grounded wire facts (the traps, confirmed against source + the working legacy client):**
- **`PortProtocol` (TCP=1/UDP=2/Both=3) has NO string converter → serializes NUMERIC**, both
  read and write. The working `direct-access.js` sends `protocol` as an int and reads it back
  as one. So we send/tolerate ordinals via a new `portProtocol.ts` normalizer (mirrors
  `vmStatus.ts`). **There is no ordinal 0** — mapped explicitly, not via a 0-based array.
- **`CustomDomainStatus` (PendingDns/Active/Paused/Error) DOES carry the converter (Ingress.cs
  line 243) → serializes as a STRING.** Opposite of PortProtocol, same API. `domainStatus.ts`
  handles names, stays defensive about ordinals.
- **direct-access DELETE returns `204 No Content`** (raw, no envelope); **domains DELETE returns
  `ApiResponse<bool>`** (JSON). The shared `api()` `unwrap()` could not handle 204 (it called
  `res.json()` on an empty body and threw "Malformed response" because 204 *is* `res.ok`).

**Files:**
- `api/client.ts` — **one boundary change:** `unwrap()` now treats `204` as success →
  `undefined`. Minimal, general, needed by the direct-access DELETE. The 8 client tests still pass.
- `features/direct-access/` — `portProtocol.ts` (+test), `useDirectAccess.ts` (5 hooks),
  `DirectAccessModal.tsx` (DNS name, port table w/ connection example + copy, quick-add from
  `GET .../services`, custom-port form).
- `features/domains/` — `domainStatus.ts` (+test), `useDomains.ts` (4 hooks),
  `DomainsModal.tsx` (list w/ status badges, CNAME instructions + copy, add/verify/remove).
- `app/routes.tsx` — `vms/:id` is now a parent with `access` + `domains` modal-route children.
- `features/vms/VmDetailPage.tsx` — renders `<Outlet/>`; two entry links added; **the SSH/VNC
  "Access" card relabelled "Connection"** (the docs' recommended de-ambiguation, done because
  this was the moment the panel was edited).

**Design decisions honored / made:**
- Used the design's ratified **modal-route** pattern (URL-driven Radix Dialog, Back closes,
  reload survives) — established here for the first time (was no child-route pattern before).
- Reused everything the foundation provides: `api()`, TanStack Query, Radix, Meridian tokens,
  reusable global classes; quick-add list comes from the backend's own `GET .../services`,
  not a hardcoded copy. No new dependency, no new endpoint, no client-side pricing/derivation.

**NOT done / owner's step:**
- **Click-through against a live backend.** No running Orchestrator here. Per the project's own
  rule, a *write* action isn't proven until its button is clicked against the real API and the
  Network tab read (this is exactly how the VmAction ordinal bug was found). The wire shapes are
  grounded from source, but the owner must exercise each button once on a real VM.
- **Do NOT delete the legacy `direct-access.js` / `custom-domains.js` modules yet.** They ship
  and prove first; deletion is Phase 6 with the rest of the old app (per DESIGN + IMPL §8). They
  are currently unreachable anyway (openers deleted 2026-07-24).
- Custom Domains's `EnableVmIngress`/`disable`/port (the platform-subdomain default ingress) is a
  *separate* surface from custom domains and was left out of scope — the regression was
  specifically custom domains + direct-access ports.

---

## Addendum — Relay AllocatePort fail-fast (orchestrator, backend; out of original frontend scope)

**How it surfaced:** the new Direct Access UI was the first client to exercise quick-add against a CGNAT VM. Clicking SSH returned a 400 after ~30s: "Relay port allocation failed or timed out." Frontend proved correct end-to-end (right endpoint, `{"serviceName":"ssh"}` body, error surfaced cleanly). The failure is backend/infra.

**Root cause (relay side, NOT in this repo):** orchestrator log shows the relay node `e9277b2c` acknowledged the `AllocatePort` command in ~1s with `Success=False` and no error message (logged as "Unknown error" because the orchestrator does `ack.ErrorMessage ?? "Unknown error"`). The relay's own port allocation failed; the real reason lives only in the relay NodeAgent's logs (separate codebase). Still needs the relay-side log for that command (`IsRelayForwarding=true`) to name the true cause.

**Second defect (orchestrator side, fixed here):** `DirectAccessService.WaitForPortAllocationAsync` polled only for success (`PortMapping.PublicPort > 0`), 60×500ms = 30s. It had no eyes on the failed acknowledgement the orchestrator already received and logged at second 1 (`CommandRegistration` holds no result; `TryCompleteCommand` removes the entry). So a definitive 1s rejection became a 30s generic "timeout" that also discarded the relay's real status.

**Fix (patch: `fail-fast-port-allocation.patch`, 3 files, +104/-8):**
- `DataStore`: new `_allocatePortFailures` map + `RecordAllocatePortFailure` / `TryConsumeAllocatePortFailure`. Records the *missing* command-outcome signal, scoped to AllocatePort only (the sole command with a waiter, which consumes on read → bounded; 2-min TTL prune for orphaned late acks).
- `NodeService.ProcessCommandAcknowledgmentAsync`: on a failed ack for an AllocatePort command, record the failure (right where it already logs it).
- `DirectAccessService.WaitForPortAllocationAsync`: returns `(int Port, string? FailureReason)` (out-params illegal in async). Poll loop checks success first, then the failure record → fails fast (~1s) with the reason. Relay call site now distinguishes "Relay rejected port allocation: {reason}" from "timed out (no response within 30s)". Non-relay hop kept its existing optimistic contract (destructures, discards reason) — deliberately not touched.
- Race-free: success path never records a failure, so `TryConsume` can't misfire on success.

**NOT done:** not compiled (no dotnet in the build sandbox) — owner must `dotnet build`. Relay-side root cause still open (needs relay NodeAgent log). The non-relay hop's optimistic "Success: in progress on timeout" is untouched and could later surface the reason too.
