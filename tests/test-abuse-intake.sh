#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Smoke test — Phase 4 slice 1: POST /api/abuse (abuse-report intake).
# Aligns with the repo's bash/curl test convention (tools/test-flow.sh).
#
# Usage:
#   BASE_URL=https://decloud.stackfi.tech ./test-abuse-intake.sh
#   BASE_URL=http://localhost:5050 ./test-abuse-intake.sh
#   # If you've raised the server's rate limit for testing (see PHASE4_SLICE1_TESTS.md):
#   BASE_URL=http://localhost:5050 THROTTLE_SECONDS=0 ./test-abuse-intake.sh
#
# Requires: bash, curl, jq.
# Exit code: 0 if all checks pass, 1 otherwise.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:5050}"
ABUSE_URL="$BASE_URL/api/abuse"

# The endpoint is rate-limited (default 5/min/IP). Space functional requests out so the
# limiter doesn't swallow them mid-suite. Set THROTTLE_SECONDS=0 when the server limit is
# raised/disabled for testing.
THROTTLE_SECONDS="${THROTTLE_SECONDS:-15}"

pass=0; fail=0
GREEN=$'\e[32m'; RED=$'\e[31m'; DIM=$'\e[2m'; RST=$'\e[0m'

ok()  { printf '%s  ✓ %s%s\n' "$GREEN" "$*" "$RST"; pass=$((pass+1)); }
bad() { printf '%s  ✗ %s%s\n' "$RED"   "$*" "$RST"; fail=$((fail+1)); }
throttle() { if [ "$THROTTLE_SECONDS" -gt 0 ]; then sleep "$THROTTLE_SECONDS"; fi; }

for tool in curl jq; do
  command -v "$tool" >/dev/null 2>&1 || { echo "Missing required tool: $tool" >&2; exit 2; }
done

# post_json <json>  →  sets HTTP (status code) and BODY (response text)
post_json() {
  local tmp; tmp="$(mktemp)"
  HTTP="$(curl -sS -o "$tmp" -w '%{http_code}' \
      -X POST "$ABUSE_URL" -H 'Content-Type: application/json' --data "$1" 2>/dev/null || true)"
  BODY="$(cat "$tmp")"; rm -f "$tmp"
}

expect_status() { # <expected> <label>
  if [ "$HTTP" = "$1" ]; then ok "$2 (HTTP $HTTP)"
  else bad "$2 — expected $1, got $HTTP ${DIM}${BODY}${RST}"; fi
}

# tolerant of an ApiResponse envelope ({data:{...}}) or a flat object
field() { local v; v="$(printf '%s' "$BODY" | jq -r "$1 // empty" 2>/dev/null || true)"; printf '%s' "$v"; }
pick()  { local a b; a="$(field ".data.$1")"; b="$(field ".$1")"; [ -n "$a" ] && printf '%s' "$a" || printf '%s' "$b"; }

printf 'Abuse intake smoke test → %s\n' "$ABUSE_URL"
printf '%sthrottle=%ss (endpoint is rate-limited 5/min/IP by default)%s\n\n' "$DIM" "$THROTTLE_SECONDS" "$RST"

# ── 1. Happy path: CSAM (category 0) → P0 / 2h ────────────────────────────────
post_json '{"category":0,"reportedResource":"template:test-template","description":"smoke — csam"}'
expect_status 200 "CSAM report accepted"
REF="$(pick reference)"; PRI="$(pick priority)"; SLA="$(pick sla)"
if printf '%s' "$REF" | grep -Eq '^ABU-[0-9]{4}-[0-9]{5}$'; then ok "reference format ($REF)"; else bad "reference format — got '$REF'"; fi
[ "$PRI" = "P0" ] && ok "priority P0" || bad "priority — expected P0, got '$PRI'"
[ "$SLA" = "2h" ] && ok "sla 2h" || bad "sla — expected 2h, got '$SLA'"
REF1="$REF"
throttle

# ── 2. Happy path: IllegalMarketplace (2) → P1 / 8h (category-specific SLA) ────
post_json '{"category":2,"reportedResource":"vm:abc123","description":"smoke — illegal marketplace","targetWallet":"0x86b8fE9ad3b4596a66b2C586F988A04f03be45F9"}'
expect_status 200 "IllegalMarketplace report accepted"
PRI="$(pick priority)"; SLA="$(pick sla)"; REF2="$(pick reference)"
[ "$PRI" = "P1" ] && ok "priority P1" || bad "priority — expected P1, got '$PRI'"
[ "$SLA" = "8h" ] && ok "sla 8h (category-specific, not priority-derived)" || bad "sla — expected 8h, got '$SLA'"
[ -n "$REF1" ] && [ -n "$REF2" ] && [ "$REF1" != "$REF2" ] && ok "references are unique ($REF1 ≠ $REF2)" || bad "references not unique ($REF1 / $REF2)"
throttle

# ── 3. Validation: unknown category → 400 ─────────────────────────────────────
post_json '{"category":99,"reportedResource":"x","description":"bad category"}'
expect_status 400 "unknown category rejected"
throttle

# ── 4. Validation: empty description → 400 ────────────────────────────────────
post_json '{"category":5,"reportedResource":"x","description":"   "}'
expect_status 400 "blank description rejected"
throttle

# ── 5. Validation: malformed target wallet → 400 ──────────────────────────────
post_json '{"category":5,"reportedResource":"x","description":"bad wallet","targetWallet":"0xNOTAWALLET"}'
expect_status 400 "malformed wallet rejected"
throttle

# ── 6. Body-size cap: >16 KB → 413 (or 400 on some stacks) ────────────────────
BIG="$(head -c 20000 /dev/zero | tr '\0' 'a')"
post_json "{\"category\":5,\"reportedResource\":\"x\",\"description\":\"$BIG\"}"
if [ "$HTTP" = "413" ] || [ "$HTTP" = "400" ]; then ok "oversized body rejected (HTTP $HTTP)"
else bad "oversized body — expected 413/400, got $HTTP"; fi
throttle

# ── 7. Rate limit: rapid burst → 429 (runs LAST; it intentionally trips the limit) ──
printf '\nRate-limit burst (expect a 429 within a few rapid requests)…\n'
saw_429=0
for i in $(seq 1 8); do
  post_json '{"category":5,"reportedResource":"x","description":"rate-limit burst"}'
  printf '%s   burst %d → HTTP %s%s\n' "$DIM" "$i" "$HTTP" "$RST"
  if [ "$HTTP" = "429" ]; then saw_429=1; break; fi
done
[ "$saw_429" = "1" ] && ok "rate limit trips (429)" || bad "no 429 in 8 rapid requests — is the limit raised/disabled, or not wired?"

printf '\n──────────────────────────────────────────\n'
printf 'passed: %d   failed: %d\n' "$pass" "$fail"
[ "$fail" -eq 0 ] || exit 1
