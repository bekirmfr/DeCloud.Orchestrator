#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Smoke test — Phase 4 abuse reporting: slice 1 (intake) + slice 2 (admin queue).
# Replaces the separate test-abuse-intake.sh and test-abuse-admin.sh.
#
# Slice 1 (public POST /api/abuse) ALWAYS runs — no auth needed.
# Slice 2 (GET/POST /api/admin/abuse) runs ONLY if TOKEN (an admin JWT) is set.
#
# Getting an admin token (simple + safe — no private keys in scripts):
#   Log in through the app with the ADMIN wallet (the one in Admin:WalletAddress
#   config). Open the browser Network tab on any /api call, copy the Authorization
#   header's "Bearer <token>" value, and pass just the token as TOKEN.
#
# Usage:
#   BASE_URL=https://decloud.stackfi.tech ./test-abuse.sh                  # slice 1 only
#   BASE_URL=… TOKEN='eyJ…admin…' ./test-abuse.sh                          # slice 1 + 2
#   … TOKEN=… USER_TOKEN='eyJ…user…' ./test-abuse.sh                       # + role-enforcement (403) check
#   … TOKEN=… RUN_TAKEDOWN=1 TAKEDOWN_WALLET='0x…' ./test-abuse.sh         # + destructive takedown (see note)
#   … THROTTLE_SECONDS=0 …                                                 # if the server rate limit is raised
#
# Takedown template limb (2026-07-16) — optional fixtures, only used when
# RUN_TAKEDOWN=1. A takedown withholds service on all three surfaces the wallet
# reaches others through: its VMs, its nodes, and its published templates.
#   TAKEDOWN_TEMPLATE_ID='<id|slug>'   a PUBLISHED + PUBLIC + COMMUNITY template
#                                      authored by TAKEDOWN_WALLET. Checks it is
#                                      archived + delisted by the takedown, and
#                                      NOT resurrected by the unsuspend.
#                                      ⚠ NOT restored by this script: republish
#                                        it by hand afterwards (it re-enters review).
#   PENDING_TEMPLATE_ID='<id|slug>'    a PENDINGREVIEW template authored by
#                                      TAKEDOWN_WALLET. Checks an admin cannot
#                                      approve it while the author is suspended.
#                                      Non-destructive (the approve must fail).
#
# Requires: bash, curl, jq.  Exit code: 0 if all checks pass, 1 otherwise.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5050}"
ABUSE_URL="$BASE_URL/api/abuse"
ADMIN="$BASE_URL/api/admin/abuse"
COMPLIANCE="$BASE_URL/api/admin/compliance"
MARKET="$BASE_URL/api/marketplace"
THROTTLE_SECONDS="${THROTTLE_SECONDS:-15}"

pass=0; fail=0
GREEN=$'\e[32m'; RED=$'\e[31m'; DIM=$'\e[2m'; RST=$'\e[0m'
ok()  { printf '%s  ✓ %s%s\n' "$GREEN" "$*" "$RST"; pass=$((pass+1)); }
bad() { printf '%s  ✗ %s%s\n' "$RED"   "$*" "$RST"; fail=$((fail+1)); }
skip(){ printf '%s  – %s%s\n' "$DIM"   "$*" "$RST"; }
throttle() { if [ "$THROTTLE_SECONDS" -gt 0 ]; then sleep "$THROTTLE_SECONDS"; fi; }
for t in curl jq; do command -v "$t" >/dev/null 2>&1 || { echo "Missing required tool: $t" >&2; exit 2; }; done

# req METHOD URL [TOKEN] [JSON]  →  sets HTTP + BODY
req() {
  local method="$1" url="$2" tok="${3:-}" data="${4:-}" tmp; tmp="$(mktemp)"
  local args=(-sS -o "$tmp" -w '%{http_code}' -X "$method" "$url")
  [ -n "$tok" ]  && args+=(-H "Authorization: Bearer $tok")
  [ -n "$data" ] && args+=(-H 'Content-Type: application/json' --data "$data")
  HTTP="$(curl "${args[@]}" 2>/dev/null || true)"; BODY="$(cat "$tmp")"; rm -f "$tmp"
}
post_json() { req POST "$ABUSE_URL" "" "$1"; }   # slice-1 intake calls (behavior unchanged)

expect_status() { if [ "$HTTP" = "$1" ]; then ok "$2 (HTTP $HTTP)"; else bad "$2 — expected $1, got $HTTP ${DIM}${BODY}${RST}"; fi; }
field() { local v; v="$(printf '%s' "$BODY" | jq -r "$1 // empty" 2>/dev/null || true)"; printf '%s' "$v"; }
pick()  { local a b; a="$(field ".data.$1")"; b="$(field ".$1")"; [ -n "$a" ] && printf '%s' "$a" || printf '%s' "$b"; }

# ── template helpers (MarketplaceController returns raw objects/arrays, not the
#    ApiResponse wrapper — tolerate both, and accept enum-as-name or enum-as-int:
#    TemplateStatus { Draft=0, Published=1, Archived=2, PendingReview=3, Rejected=4 }
#    — PendingReview/Rejected were APPENDED, so 0/1/2 are unchanged.)
tpl_status()  { printf '%s' "$BODY" | jq -r '(.status // .data.status) | tostring' 2>/dev/null || true; }
is_published(){ [ "$1" = "Published" ] || [ "$1" = "1" ]; }
is_archived() { [ "$1" = "Archived" ]  || [ "$1" = "2" ]; }
# true when the current BODY (a template listing) contains this id or slug
list_has() {
  printf '%s' "$BODY" | jq -e --arg i "$1" \
    '[ (if type=="array" then . else (.data // .templates // []) end)[]? | (.id, .slug) ] | index($i) != null' \
    >/dev/null 2>&1
}

# ═══════════════════════════════════════════════════════════════════════════════
# SLICE 1 — public intake  (always runs)
# ═══════════════════════════════════════════════════════════════════════════════
printf 'Abuse intake (slice 1) → %s\n' "$ABUSE_URL"
printf '%sthrottle=%ss (public endpoint is rate-limited 5/min/IP by default)%s\n\n' "$DIM" "$THROTTLE_SECONDS" "$RST"

# 1. CSAM (0) → P0 / 2h
post_json '{"category":0,"reportedResource":"template:test-template","description":"smoke — csam"}'
expect_status 200 "CSAM report accepted"
REF1="$(pick reference)"; PRI="$(pick priority)"; SLA="$(pick sla)"
if printf '%s' "$REF1" | grep -Eq '^ABU-[0-9]{4}-[0-9]{5}$'; then ok "reference format ($REF1)"; else bad "reference format — got '$REF1'"; fi
[ "$PRI" = "P0" ] && ok "priority P0" || bad "priority — expected P0, got '$PRI'"
[ "$SLA" = "2h" ] && ok "sla 2h" || bad "sla — expected 2h, got '$SLA'"
throttle

# 2. IllegalMarketplace (2) → P1 / 8h (category-specific SLA)
post_json '{"category":2,"reportedResource":"vm:abc123","description":"smoke — illegal marketplace","targetWallet":"0x86b8fE9ad3b4596a66b2C586F988A04f03be45F9"}'
expect_status 200 "IllegalMarketplace report accepted"
PRI="$(pick priority)"; SLA="$(pick sla)"; REF2="$(pick reference)"
[ "$PRI" = "P1" ] && ok "priority P1" || bad "priority — expected P1, got '$PRI'"
[ "$SLA" = "8h" ] && ok "sla 8h (category-specific, not priority-derived)" || bad "sla — expected 8h, got '$SLA'"
[ -n "$REF1" ] && [ -n "$REF2" ] && [ "$REF1" != "$REF2" ] && ok "references are unique ($REF1 ≠ $REF2)" || bad "references not unique ($REF1 / $REF2)"
throttle

# 3-5. validation
post_json '{"category":99,"reportedResource":"x","description":"bad category"}';                              expect_status 400 "unknown category rejected"; throttle
post_json '{"category":5,"reportedResource":"x","description":"   "}';                                        expect_status 400 "blank description rejected"; throttle
post_json '{"category":5,"reportedResource":"x","description":"bad wallet","targetWallet":"0xNOTAWALLET"}';    expect_status 400 "malformed wallet rejected"; throttle

# 6. body-size cap: >16 KB → 413 (or 400 on some stacks)
BIG="$(head -c 20000 /dev/zero | tr '\0' 'a')"
post_json "{\"category\":5,\"reportedResource\":\"x\",\"description\":\"$BIG\"}"
if [ "$HTTP" = "413" ] || [ "$HTTP" = "400" ]; then ok "oversized body rejected (HTTP $HTTP)"; else bad "oversized body — expected 413/400, got $HTTP"; fi
throttle

# 7. rate-limit burst (LAST — intentionally trips the limit for the rest of the window)
printf '\nRate-limit burst (expect a 429 within a few rapid requests)…\n'
saw_429=0
for i in $(seq 1 8); do
  post_json '{"category":5,"reportedResource":"x","description":"rate-limit burst"}'
  printf '%s   burst %d → HTTP %s%s\n' "$DIM" "$i" "$HTTP" "$RST"
  if [ "$HTTP" = "429" ]; then saw_429=1; break; fi
done
[ "$saw_429" = "1" ] && ok "rate limit trips (429)" || bad "no 429 in 8 rapid requests — is the limit raised/disabled, or not wired?"

# ═══════════════════════════════════════════════════════════════════════════════
# SLICE 2 — admin queue + resolve  (runs only if TOKEN is set)
# Reuses the open reports created above — no new public POSTs (those are rate-limited).
# ═══════════════════════════════════════════════════════════════════════════════
if [ -z "${TOKEN:-}" ]; then
  printf '\n%s— slice 2 skipped: set TOKEN=<admin JWT> to run the admin queue + resolve checks.%s\n' "$DIM" "$RST"
else
  printf '\nAdmin queue + resolve (slice 2) → %s\n' "$ADMIN"

  # auth is enforced
  req GET "$ADMIN" ""
  [ "$HTTP" = "401" ] && ok "no token → 401" || bad "no token → expected 401, got $HTTP"
  if [ -n "${USER_TOKEN:-}" ]; then
    req GET "$ADMIN" "$USER_TOKEN"
    [ "$HTTP" = "403" ] && ok "non-admin token → 403" || bad "non-admin token → expected 403, got $HTTP"
  else
    printf '%s  – non-admin check skipped (set USER_TOKEN to run it)%s\n' "$DIM" "$RST"
  fi

  # admin reads the queue
  req GET "$ADMIN" "$TOKEN"
  [ "$HTTP" = "200" ] && ok "admin queue → 200" || bad "admin queue → expected 200, got $HTTP"
  if [ -n "$REF1" ] && printf '%s' "$BODY" | jq -e --arg r "$REF1" 'any(.data[]?; .report.reference == $r)' >/dev/null 2>&1; then
    ok "report from slice 1 present in queue"
  else
    bad "report ($REF1) not found in queue"
  fi
  if printf '%s' "$BODY" | jq -e '[.data[]?.report.priority] as $p | $p == ($p | sort)' >/dev/null 2>&1; then
    ok "queue ordered by priority (P0→P4)"
  else
    bad "queue not priority-ordered"
  fi

  # resolve error cases + dismiss (acts on REF1)
  if [ -n "$REF1" ]; then
    req POST "$ADMIN/ABU-9999-99999/resolve" "$TOKEN" '{"action":"dismiss","reason":"x"}'; [ "$HTTP" = "404" ] && ok "resolve unknown ref → 404" || bad "unknown ref → expected 404, got $HTTP"
    req POST "$ADMIN/$REF1/resolve" "$TOKEN" '{"action":"bogus","reason":"x"}';            [ "$HTTP" = "400" ] && ok "invalid action → 400" || bad "invalid action → expected 400, got $HTTP"
    req POST "$ADMIN/$REF1/resolve" "$TOKEN" '{"action":"dismiss","reason":"  "}';          [ "$HTTP" = "400" ] && ok "missing reason → 400" || bad "missing reason → expected 400, got $HTTP"

    req POST "$ADMIN/$REF1/resolve" "$TOKEN" '{"action":"dismiss","reason":"smoke test dismiss"}'
    [ "$HTTP" = "200" ] && ok "dismiss → 200" || bad "dismiss → expected 200, got $HTTP ${DIM}${BODY}${RST}"
    [ "$(field '.data.status')" = "Dismissed" ] && ok "status Dismissed" || bad "status → expected Dismissed, got '$(field '.data.status')'"

    req POST "$ADMIN/$REF1/resolve" "$TOKEN" '{"action":"dismiss","reason":"again"}'
    [ "$HTTP" = "409" ] && ok "re-resolve resolved report → 409" || bad "re-resolve → expected 409, got $HTTP"
  else
    bad "slice-2 resolve checks skipped — slice 1 did not yield a report reference"
  fi

  # takedown (DESTRUCTIVE, opt-in) — acts on REF2 with an overridden target wallet
  if [ "${RUN_TAKEDOWN:-0}" = "1" ] && [ -n "${TAKEDOWN_WALLET:-}" ] && [ -n "$REF2" ]; then
    printf '%s⚠ takedown will SUSPEND %s, stop its VMs, suspend its nodes, and ARCHIVE its\n' "$RED" "$TAKEDOWN_WALLET"
    printf '  published community templates (then unsuspend; VMs stay stopped and templates\n'
    printf '  stay archived — republish them by hand, they re-enter review)%s\n' "$RST"

    # ── Fixture baseline: the template limb only proves something if the template
    #    is live BEFORE the takedown. Verify, or the post-checks pass vacuously.
    TPL_BASELINE=0
    if [ -n "${TAKEDOWN_TEMPLATE_ID:-}" ]; then
      req GET "$MARKET/templates/$TAKEDOWN_TEMPLATE_ID" "$TOKEN"
      TPL_ST="$(tpl_status)"
      if [ "$HTTP" = "200" ] && is_published "$TPL_ST"; then
        req GET "$MARKET/templates" ""
        if list_has "$TAKEDOWN_TEMPLATE_ID"; then
          TPL_BASELINE=1
          ok "fixture: $TAKEDOWN_TEMPLATE_ID is Published and publicly listed before takedown"
        else
          bad "fixture: $TAKEDOWN_TEMPLATE_ID is Published but NOT publicly listed — is it Private? (delist check would pass vacuously)"
        fi
      else
        bad "fixture: $TAKEDOWN_TEMPLATE_ID is not Published (HTTP $HTTP, status '$TPL_ST') — template limb not exercised"
      fi
    else
      skip "template limb skipped (set TAKEDOWN_TEMPLATE_ID=<published community template of $TAKEDOWN_WALLET>)"
    fi

    req POST "$ADMIN/$REF2/resolve" "$TOKEN" "{\"action\":\"takedown\",\"reason\":\"smoke takedown\",\"targetWallet\":\"$TAKEDOWN_WALLET\"}"
    if [ "$HTTP" = "200" ]; then
      [ "$(field '.data.status')" = "Actioned" ] && ok "takedown → Actioned" || bad "takedown status → expected Actioned, got '$(field '.data.status')'"
      req GET "$COMPLIANCE/actions?wallet=$TAKEDOWN_WALLET" "$TOKEN"
      if printf '%s' "$BODY" | jq -e --arg r "$REF2" 'any(.data[]?; .reference == $r)' >/dev/null 2>&1; then
        ok "enforcement action linked to $REF2"
      else
        bad "no enforcement action carries reference $REF2"
      fi

      # ── Template limb: archived, delisted, and counted on the audit row ──────
      if [ "$TPL_BASELINE" = "1" ]; then
        req GET "$MARKET/templates/$TAKEDOWN_TEMPLATE_ID" "$TOKEN"
        TPL_ST="$(tpl_status)"
        if [ "$HTTP" = "200" ] && is_archived "$TPL_ST"; then
          ok "takedown archived the author's published template"
        elif [ "$HTTP" = "404" ]; then
          skip "template fetch → 404 after takedown (endpoint may hide non-published templates); relying on the delist check below"
        else
          bad "template not Archived after takedown (HTTP $HTTP, status '$TPL_ST')"
        fi

        req GET "$MARKET/templates" ""
        if [ "$HTTP" = "200" ] && ! list_has "$TAKEDOWN_TEMPLATE_ID"; then
          ok "archived template is delisted from the public marketplace"
        else
          bad "archived template still listed publicly (HTTP $HTTP) — amplification surface still open"
        fi

        # The count rides the existing enforcement action's metadata.
        req GET "$COMPLIANCE/actions?wallet=$TAKEDOWN_WALLET" "$TOKEN"
        if printf '%s' "$BODY" | jq -e --arg r "$REF2" \
             'any(.data[]?; .reference == $r and ((.metadata.templatesArchived // "0") | tonumber) >= 1)' >/dev/null 2>&1; then
          ok "audit row records templatesArchived ≥ 1"
        else
          bad "enforcement action for $REF2 has no templatesArchived ≥ 1 in metadata"
        fi
      fi

      # ── Approve-path gate: a PendingReview submission from a now-suspended
      #    author must not be approvable (InvalidOperationException → 409).
      if [ -n "${PENDING_TEMPLATE_ID:-}" ]; then
        req POST "$MARKET/templates/$PENDING_TEMPLATE_ID/approve" "$TOKEN"
        if [ "$HTTP" = "409" ]; then
          ok "approve refused for suspended author's pending template → 409"
        elif [ "$HTTP" = "200" ]; then
          bad "approve SUCCEEDED for a suspended author's template — takedown undone by the review queue"
          printf '%s    ⚠ that template is now Published; archive it manually.%s\n' "$RED" "$RST"
        else
          bad "approve → expected 409, got $HTTP ${DIM}${BODY}${RST}"
        fi
      else
        skip "approve-gate check skipped (set PENDING_TEMPLATE_ID=<pendingreview template of $TAKEDOWN_WALLET>)"
      fi
    else
      bad "takedown → expected 200, got $HTTP ${DIM}${BODY}${RST}"
    fi

    req POST "$COMPLIANCE/unsuspend" "$TOKEN" "{\"wallet\":\"$TAKEDOWN_WALLET\",\"reason\":\"smoke cleanup\"}"
    [ "$HTTP" = "200" ] && ok "cleanup: unsuspended $TAKEDOWN_WALLET (restart its VMs manually)" || bad "cleanup unsuspend → got $HTTP — UNSUSPEND $TAKEDOWN_WALLET MANUALLY"

    # Unsuspend restores nodes; it must NOT silently republish an archived template.
    # (Deliberate asymmetry: a node is infrastructure, a published template is
    #  distribution — it returns only by the author republishing → re-review.)
    if [ "$TPL_BASELINE" = "1" ]; then
      req GET "$MARKET/templates/$TAKEDOWN_TEMPLATE_ID" "$TOKEN"
      TPL_ST="$(tpl_status)"
      if [ "$HTTP" = "404" ]; then
        skip "post-unsuspend template fetch → 404; confirm manually that it is still Archived"
      elif is_archived "$TPL_ST"; then
        ok "unsuspend leaves the template archived (no silent republish)"
      else
        bad "template status is '$TPL_ST' after unsuspend — expected still Archived"
      fi
      printf '%s  ⚠ %s stays ARCHIVED by design — republish it manually to restore the fixture.%s\n' \
        "$DIM" "$TAKEDOWN_TEMPLATE_ID" "$RST"
    fi
  else
    printf '%s  – takedown skipped (destructive). Set RUN_TAKEDOWN=1 TAKEDOWN_WALLET=0x… to run it.%s\n' "$DIM" "$RST"
  fi
fi

printf '\n──────────────────────────────────────────\n'
printf 'passed: %d   failed: %d\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1