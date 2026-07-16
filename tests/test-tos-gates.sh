#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# test-tos-gates.sh — ToS acceptance gates + node declaration binding.
#
# Covers the 2026-07-16 pass:
#   1. ToS document sanity            (public, no auth)
#   2. Auth posture                   (no token → 401)
#   3. Gates refuse an unaccepted wallet  (needs FRESH_JWT)
#   4. Gates allow an accepted wallet     (needs USER_JWT — positive control)
#   5. Declaration binding, by replaying a REAL signature with mutations
#      (needs root on a node with a fresh /etc/decloud/pending-auth)
#   6. Manual checklist for the CLI ceremony (printed)
#
# Getting the tokens (same method as test-abuse.sh — no private keys in scripts):
#   Log in through the app with the wallet, open the browser Network tab on any
#   /api call, copy the Authorization header's "Bearer <token>" value.
#
#   FRESH_JWT — a wallet that has NEVER accepted the current ToS. Connect it and
#               DECLINE the terms modal, then grab the token. (A ToS version bump
#               also makes every existing wallet "fresh" — see §6.)
#   USER_JWT  — a NON-ADMIN wallet that HAS accepted. Admin tokens are useless
#               here: an admin's templates are platform templates (IsCommunity
#               = !isAdmin), which are exempt from the ToS gate by design — so an
#               admin create succeeding would prove nothing.
#
# Usage:
#   BASE_URL=https://decloud.stackfi.tech ./test-tos-gates.sh            # §1,§2 only
#   BASE_URL=… FRESH_JWT='eyJ…' ./test-tos-gates.sh                      # + §3
#   BASE_URL=… FRESH_JWT='eyJ…' USER_JWT='eyJ…' ./test-tos-gates.sh      # + §4
#   sudo BASE_URL=… RUN_BINDING=1 ./test-tos-gates.sh                    # + §5 (on a node)
#
# Optional:
#   DRAFT_TEMPLATE_ID  a Draft template owned by FRESH_JWT's wallet, created
#                      before the gate shipped (grandfathered). Checks publish
#                      is refused too, not just create.
#
# §5 is READ-ONLY and side-effect free BY CONSTRUCTION: every mutation it sends
# is refused whether or not the fix is applied — it distinguishes the two by
# WHICH refusal comes back. It never sends an unmutated declaration, so it can
# never register anything.
#
# Requires: bash, curl, jq.  Exit code: 0 if all checks pass, 1 otherwise.
# ─────────────────────────────────────────────────────────────────────────────
set -uo pipefail

# Accept either convention (test-abuse.sh uses BASE_URL/TOKEN; test-csam-pass1.sh
# uses ORCH_URL/ADMIN_JWT). Muscle memory shouldn't cost a re-run.
BASE_URL="${BASE_URL:-${ORCH_URL:-http://localhost:5050}}"
PENDING_AUTH="${PENDING_AUTH:-/etc/decloud/pending-auth}"

pass=0; fail=0
GREEN=$'\e[32m'; RED=$'\e[31m'; YEL=$'\e[33m'; DIM=$'\e[2m'; RST=$'\e[0m'
ok()   { printf '%s  ✓ %s%s\n' "$GREEN" "$*" "$RST"; pass=$((pass+1)); }
bad()  { printf '%s  ✗ %s%s\n' "$RED"   "$*" "$RST"; fail=$((fail+1)); }
skip() { printf '%s  – %s%s\n' "$DIM"   "$*" "$RST"; }
warn() { printf '%s  ! %s%s\n' "$YEL"   "$*" "$RST"; }
hdr()  { printf '\n%s── %s%s\n' "$RST" "$*" "$RST"; }

for t in curl jq; do command -v "$t" >/dev/null 2>&1 || { echo "Missing required tool: $t" >&2; exit 2; }; done

# req METHOD URL [TOKEN] [JSON] → sets HTTP + BODY
req() {
  local method="$1" url="$2" tok="${3:-}" data="${4:-}" tmp; tmp="$(mktemp)"
  local args=(-sS -o "$tmp" -w '%{http_code}' -X "$method" "$url")
  [ -n "$tok" ]  && args+=(-H "Authorization: Bearer $tok")
  [ -n "$data" ] && args+=(-H 'Content-Type: application/json' --data "$data")
  HTTP="$(curl "${args[@]}" 2>/dev/null || true)"; BODY="$(cat "$tmp")"; rm -f "$tmp"
}
field() { printf '%s' "$BODY" | jq -r "$1 // empty" 2>/dev/null || true; }
# Case-insensitive substring over the whole body (error text lives in different
# shapes across controllers: {error}, {message}, ApiResponse.message).
body_has() { printf '%s' "$BODY" | grep -qiF "$1"; }

# ═════════════════════════════════════════════════════════════════════════════
hdr "1. ToS document (public) → $BASE_URL/api/tos"
# ═════════════════════════════════════════════════════════════════════════════
req GET "$BASE_URL/api/tos" ""
if [ "$HTTP" = "200" ]; then ok "GET /api/tos → 200 (no auth needed — the CLI fetches this)"
else bad "GET /api/tos → $HTTP (want 200)"; fi

TOS_VERSION="$(field '.data.version')"; [ -n "$TOS_VERSION" ] || TOS_VERSION="$(field '.version')"
TOS_HASH="$(field '.data.hash')";       [ -n "$TOS_HASH" ]    || TOS_HASH="$(field '.hash')"
TOS_TEXT="$(field '.data.text')";       [ -n "$TOS_TEXT" ]    || TOS_TEXT="$(field '.text')"

[ -n "$TOS_VERSION" ] && ok "version present ($TOS_VERSION)" || bad "no version in /api/tos"

if printf '%s' "$TOS_HASH" | grep -Eq '^[0-9a-fA-F]{64}$'; then
  ok "hash is a full SHA-256 (${TOS_HASH:0:12}…)"
else
  bad "hash is not 64 hex chars ('$TOS_HASH') — the declaration signs this verbatim"
fi

# The empty-document case is the fail-closed precondition: TosService logs loudly
# and blocks every tenant VM creation. If the text is empty, nothing below means
# anything — the gates would "pass" by refusing everyone.
if [ "$(printf '%s' "$TOS_TEXT" | wc -c)" -gt 200 ]; then
  ok "document text is present ($(printf '%s' "$TOS_TEXT" | wc -c) bytes)"
else
  bad "ToS text is empty/tiny — fail-closed is active: EVERY tenant deploy is blocked"
fi

# ═════════════════════════════════════════════════════════════════════════════
hdr "2. Auth posture"
# ═════════════════════════════════════════════════════════════════════════════
req GET "$BASE_URL/api/tos/status" ""
[ "$HTTP" = "401" ] && ok "GET /api/tos/status without token → 401" \
                    || bad "GET /api/tos/status without token → $HTTP (want 401)"

# ═════════════════════════════════════════════════════════════════════════════
hdr "3. Gates REFUSE a wallet that has not accepted"
# ═════════════════════════════════════════════════════════════════════════════
if [ -z "${FRESH_JWT:-}" ]; then
  skip "§3 skipped — set FRESH_JWT to a wallet that has NOT accepted the current ToS"
else
  # Baseline first. Without this the refusals below could be caused by anything
  # (a blocked wallet, a bad token) and we'd score a false pass.
  req GET "$BASE_URL/api/tos/status" "$FRESH_JWT"
  FRESH_ACCEPTED="$(field '.data.accepted')"
  if [ "$HTTP" = "200" ] && [ "$FRESH_ACCEPTED" = "false" ]; then
    ok "fixture: FRESH_JWT's wallet has NOT accepted (accepted=false)"
    FRESH_OK=1
  else
    bad "fixture: FRESH_JWT shows accepted='$FRESH_ACCEPTED' (HTTP $HTTP) — need an UNACCEPTED wallet; §3 proves nothing"
    FRESH_OK=0
  fi

  if [ "${FRESH_OK:-0}" = "1" ]; then
    # 3a. Template create — the gap this pass was reported for.
    req POST "$BASE_URL/api/marketplace/templates/create" "$FRESH_JWT" \
      '{"name":"tos gate check","slug":"tos-gate-check-'"$RANDOM"'","version":"1.0.0","category":"other","description":"smoke: must be refused"}'
    if [ "$HTTP" = "200" ] || [ "$HTTP" = "201" ]; then
      bad "template create SUCCEEDED without ToS acceptance — gate missing (delete the template)"
    elif body_has "Terms of Service"; then
      ok "template create refused, naming the ToS (HTTP $HTTP)"
    else
      bad "template create → $HTTP but the error does not mention the ToS: ${DIM}$(printf '%s' "$BODY" | head -c 120)${RST}"
    fi

    # 3b. VM create — the pre-existing gate; regression check.
    req POST "$BASE_URL/api/vms" "$FRESH_JWT" \
      '{"name":"tos-gate-check","spec":{"virtualCpuCores":1,"memoryBytes":1073741824,"diskBytes":10737418240}}'
    if [ "$HTTP" = "200" ] && [ -n "$(field '.data.vmId')" ]; then
      bad "VM create SUCCEEDED without ToS acceptance — the original gate regressed (DELETE THAT VM)"
    elif body_has "TOS_NOT_ACCEPTED" || body_has "Terms of Service"; then
      ok "VM create refused with the ToS gate (HTTP $HTTP)"
    else
      ok "VM create refused (HTTP $HTTP) ${DIM}— reason not ToS-specific; quota/spec may have refused first${RST}"
    fi

    # 3c. Publish — needs a pre-existing Draft (create is now blocked, so this
    # only exists for wallets grandfathered in before the gate).
    if [ -n "${DRAFT_TEMPLATE_ID:-}" ]; then
      req POST "$BASE_URL/api/marketplace/templates/$DRAFT_TEMPLATE_ID/publish" "$FRESH_JWT"
      if [ "$HTTP" = "200" ]; then
        bad "publish SUCCEEDED without ToS acceptance — the amplification door is open"
      elif body_has "Terms of Service"; then
        ok "publish refused, naming the ToS (HTTP $HTTP)"
      else
        bad "publish → $HTTP, error does not mention the ToS: ${DIM}$(printf '%s' "$BODY" | head -c 120)${RST}"
      fi
    else
      skip "publish check skipped (set DRAFT_TEMPLATE_ID to a Draft owned by FRESH_JWT's wallet)"
    fi
  fi
fi

# ═════════════════════════════════════════════════════════════════════════════
hdr "4. Gates ALLOW an accepted wallet (positive control)"
# ═════════════════════════════════════════════════════════════════════════════
if [ -z "${USER_JWT:-}" ]; then
  skip "§4 skipped — set USER_JWT to a NON-ADMIN wallet that HAS accepted"
  warn "without this, §3 could be passing because the gate refuses EVERYONE"
else
  req GET "$BASE_URL/api/tos/status" "$USER_JWT"
  if [ "$(field '.data.accepted')" != "true" ]; then
    bad "fixture: USER_JWT's wallet has not accepted — it cannot serve as the positive control"
  else
    ok "fixture: USER_JWT's wallet HAS accepted"
    SLUG="tos-positive-$RANDOM"
    req POST "$BASE_URL/api/marketplace/templates/create" "$USER_JWT" \
      '{"name":"tos positive control","slug":"'"$SLUG"'","version":"1.0.0","category":"other","description":"smoke: must succeed"}'
    if [ "$HTTP" = "200" ] || [ "$HTTP" = "201" ]; then
      NEW_ID="$(field '.id')"; [ -n "$NEW_ID" ] || NEW_ID="$(field '.data.id')"
      ok "accepted wallet CAN create a template (the gate is a gate, not a wall)"
      if [ -n "$NEW_ID" ]; then
        req DELETE "$BASE_URL/api/marketplace/templates/$NEW_ID" "$USER_JWT"
        [ "$HTTP" = "200" ] || [ "$HTTP" = "204" ] \
          && ok "cleanup: positive-control template deleted" \
          || warn "cleanup: could not delete $NEW_ID (HTTP $HTTP) — remove it by hand"
      else
        warn "created, but no id returned — find and delete slug '$SLUG' by hand"
      fi
    else
      bad "accepted wallet was REFUSED a template create (HTTP $HTTP): ${DIM}$(printf '%s' "$BODY" | head -c 160)${RST}"
    fi
  fi
fi

# ═════════════════════════════════════════════════════════════════════════════
hdr "5. Node declaration binding (replay a real signature, mutated)"
# ═════════════════════════════════════════════════════════════════════════════
# Every request here is refused whether or not the fix is applied — the mutations
# use INVALID values that STEP 3 rejects on its own. What changes is WHICH refusal
# comes back, and that is the whole measurement:
#   "locality settings changed"  → STEP 1.9 fired  → the declaration is bound  ✓
#   "Invalid country code"       → STEP 3 fired    → 1.9 never ran → NOT bound ✗
# So the test can never register anything, in either state of the code.
if [ "${RUN_BINDING:-0}" != "1" ]; then
  skip "§5 skipped — run on a node as root with RUN_BINDING=1 (needs $PENDING_AUTH)"
elif [ ! -r "$PENDING_AUTH" ]; then
  bad "$PENDING_AUTH not readable — run as root, right after 'sudo decloud register'"
else
  WALLET="$(jq -r '.walletAddress // empty' "$PENDING_AUTH")"
  SIG="$(jq -r '.signature // empty' "$PENDING_AUTH")"
  MSG="$(jq -r '.message // empty' "$PENDING_AUTH")"

  if [ -z "$WALLET" ] || [ -z "$SIG" ] || [ -z "$MSG" ]; then
    bad "pending-auth is missing walletAddress/signature/message"
  else
    ok "fixture: pending-auth loaded for $WALLET"

    # Does the declaration even carry the terms? (CLI side of the pass.)
    if printf '%s' "$MSG" | grep -q "Terms of Service version: $TOS_VERSION" \
       && printf '%s' "$MSG" | grep -qi "Terms of Service hash: $TOS_HASH"; then
      ok "declaration names the CURRENT ToS version + full hash"
    else
      bad "declaration does not carry the current ToS version/hash — old CLI, or the terms were bumped after signing"
      printf '%s      (expected version %s / hash %s…)%s\n' "$DIM" "$TOS_VERSION" "${TOS_HASH:0:12}" "$RST"
    fi

    DECL_COUNTRY="$(printf '%s' "$MSG" | sed -n 's/^Country:[[:space:]]*//p' | head -1)"
    DECL_REGION="$(printf '%s' "$MSG"  | sed -n 's/^Region:[[:space:]]*//p'  | head -1)"
    DECL_MACHINE="$(printf '%s' "$MSG" | sed -n 's/^Machine ID:[[:space:]]*//p' | head -1)"
    printf '%s      signed: country=%s region=%s machine=%s%s\n' \
      "$DIM" "${DECL_COUNTRY:-?}" "${DECL_REGION:-?}" "${DECL_MACHINE:0:12}" "$RST"

    reg_json() {  # reg_json <country> <region> <machineId>
      jq -n --arg mid "$3" --arg w "$WALLET" --arg sig "$SIG" --arg msg "$MSG" \
            --arg c "$1" --arg r "$2" \
        '{machineId:$mid, name:"tos-binding-test", walletAddress:$w,
          signature:$sig, message:$msg, publicIp:"127.0.0.1", agentPort:5100,
          hardwareInventory:{}, agentVersion:"binding-test", supportedImages:[],
          region:$r, zone:"default", country:$c}'
    }

    # judge <label> — reads HTTP/BODY, decides bound vs not vs inconclusive
    judge() {
      if [ "$HTTP" = "200" ]; then
        bad "$1: registration SUCCEEDED — the declaration is NOT bound, and a node was created. DELETE IT."
      elif body_has "expired" || body_has "future"; then
        skip "$1: inconclusive — the signature aged out (>5 min). Re-run 'decloud register' and retry immediately."
        warn "     (this does prove the freshness fix is live — the verdict is no longer discarded)"
      elif body_has "locality settings changed" || body_has "different machine" \
        || body_has "does not state a country"; then
        ok "$1: refused by the declaration binding (STEP 1.9)"
      elif body_has "Invalid country code" || body_has "Invalid region"; then
        bad "$1: refused by STEP 3 validation, NOT by the binding — STEP 1.9 is missing or runs too late"
      elif body_has "Terms of Service"; then
        skip "$1: inconclusive — refused at the ToS gate (STEP 1.8) before reaching the binding"
      elif [ "$HTTP" = "400" ]; then
        skip "$1: inconclusive — HTTP 400, likely request-model binding: ${DIM}$(printf '%s' "$BODY" | head -c 100)${RST}"
      else
        bad "$1: unexpected refusal $HTTP: ${DIM}$(printf '%s' "$BODY" | head -c 140)${RST}"
      fi
    }

    # 5a. Country forged. 'XX' is not a valid ISO code, so STEP 3 refuses it too —
    #     that is what makes this safe to run against production.
    req POST "$BASE_URL/api/nodes/register" "" "$(reg_json 'XX' "$DECL_REGION" "$DECL_MACHINE")"
    judge "country forged (signed $DECL_COUNTRY, sent XX)"

    # 5b. Region forged. Same trick — an invalid region code.
    req POST "$BASE_URL/api/nodes/register" "" "$(reg_json "$DECL_COUNTRY" 'xx-not-a-region' "$DECL_MACHINE")"
    judge "region forged (signed $DECL_REGION, sent xx-not-a-region)"

    # 5c. Machine ID forged — paired with an invalid country so that a MISSING
    #     binding still hits STEP 3 and creates nothing. It cannot isolate the
    #     machine check from the country check; see the checklist below.
    req POST "$BASE_URL/api/nodes/register" "" "$(reg_json 'XX' "$DECL_REGION" 'ffffffffffffffffffffffffffffffff')"
    judge "machine forged (+invalid country, so a missing binding is still refused)"
  fi
fi

# ═════════════════════════════════════════════════════════════════════════════
printf '\n──────────────────────────────────────────\n'
printf 'passed: %d   failed: %d\n' "$pass" "$fail"
cat <<'EOF'

Manual checklist — the CLI ceremony and the paths no script can reach:

 A. decloud register, happy path (on a node with NO 'region' in settings.json —
    the case the old 'unknown' vs 'default' default mismatch would break):
      sudo decloud register --force
    - The FULL terms print, then version + SHA-256.
    - The message shows BOTH ToS lines and "Region:     default".
    - Sign → agent registers.

 B. Decline:
    - Type anything other than 'accept' → aborts.
    - /etc/decloud/pending-auth is UNCHANGED (nothing written).

 C. Offline fail-closed:
    - Point the CLI at an unreachable orchestrator → it must refuse to build a
      declaration at all. An operator must never register by being offline.

 D. Stale ToS (the fail-closed path nobody hits by accident):
    - Keep a pending-auth signed under the current version.
    - Bump Tos:Version on the orchestrator, restart it, restart the agent.
    - Registration refused: "does not accept the current Terms of Service".

 E. Unified acceptance (the payoff — one wallet, one agreement):
    - After a successful registration, the OPERATOR's wallet should pass the
      tenant gates with no web acceptance at all:
        curl -H "Authorization: Bearer $OPERATOR_JWT" "$BASE_URL/api/tos/status"
      → accepted: true
    - If false, RecordAcceptanceAsync is not wired into STEP 1.8.

 F. Machine binding in isolation (§5c cannot separate it from the country check):
    - On a STAGING orchestrator only: send a forged machineId with a VALID
      country. Expect "signed for a different machine". If it registers, an
      attacker with a leaked signature can put their own hardware in the fleet
      under someone else's wallet — delete the node and fix before shipping.
EOF
[ "$fail" -eq 0 ] || exit 1
