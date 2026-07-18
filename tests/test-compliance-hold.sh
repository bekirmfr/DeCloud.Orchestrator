#!/usr/bin/env bash
# ============================================================================
# test-compliance-hold.sh — the compliance hold is a human admin action, and
# nothing else can apply one.
#
# Replaces test-csam-pass1.sh. That script tested a pass; this tests a
# property, and the property outlives the pass. Pass 2a (Decision 18) removed
# the node-initiated csam-report path, so the two auth probes it opened with
# are gone — check 1 below now asserts the endpoint stays gone.
#
# Why this script matters beyond 2a: the hold + delete-refusal cycle is the
# precondition pass 2b's scan-vm guard rests on ("refuse to scan a VM that is
# not under ComplianceHold"). It should be green before 2b lands, and stay
# green after.
#
# Covers:
#   1. POST /api/compliance/csam-report → 404. Nothing node-initiated applies
#      a hold. A dead endpoint that accepted node-authenticated input and
#      applied holds is attack surface, not optionality (PASS2 §2.1).
#   2. suspend-vm → the hold lands.
#   3. DELETE while held → 400 VM_HELD (evidence preservation, enforced at the
#      service boundary — VmService.DeleteVmAsync refuses every caller).
#   4. Start while held → 400 VM_HELD (owner cannot revive it).
#   5. resume-vm → 200. Human-in-the-loop unsuspend works; no 501.
#   6. THIS RUN's hold/resume pair is in the enforcement audit.
#   7. The abuse queue is reachable.
#
# Usage:
#   ORCH_URL=https://orchestrator.example ADMIN_JWT=eyJ... \
#   [TEST_VM_ID=<existing disposable tenant VM id>] ./test-compliance-hold.sh
#
# Destructive only against TEST_VM_ID (suspend → delete attempt → resume).
# Without TEST_VM_ID, checks 2-6 skip and 1/7 still run.
#
# Note: this leaves TEST_VM_ID STOPPED. resume-vm lifts the hold; it does not
# restart the VM (by design — Decision 10). Start it yourself if you need it.
# ============================================================================
set -u

ORCH_URL="${ORCH_URL:?Set ORCH_URL}"
ADMIN_JWT="${ADMIN_JWT:?Set ADMIN_JWT}"
TEST_VM_ID="${TEST_VM_ID:-}"

# Per-run tag. The enforcement audit is append-only, so grepping it for a
# fixed string like "test-compliance-hold" would match a PREVIOUS run's rows
# and pass green even if this run wrote nothing. The tag makes check 6 assert
# what it claims to assert.
RUN_TAG="hold-test-$(date +%s)-$$"

PASS=0; FAIL=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
hdr()  { echo; echo "── $1"; }

acurl() { curl -s -o /tmp/hold_body -w "%{http_code}" \
          -H "Authorization: Bearer $ADMIN_JWT" -H "Content-Type: application/json" "$@"; }

echo "run tag: $RUN_TAG"

# ── 1. The node-initiated report endpoint is GONE (pass 2a) ─────────────────
hdr "csam-report is deleted, not merely unreachable"

# 404, not 401/403: the route no longer exists, so routing fails before auth.
# An auth error here would mean the controller is still registered.
code=$(acurl -X POST "$ORCH_URL/api/compliance/csam-report" \
  -d '{"vmId":"00000000-0000-0000-0000-000000000000","matchedFileHash":"x","detectedAt":"2026-07-08T00:00:00Z"}')
case "$code" in
  404)     ok "POST /api/compliance/csam-report → 404 (endpoint removed)";;
  401|403) bad "csam-report → $code — the controller is STILL REGISTERED (auth ran, so the route matched)";;
  *)       bad "csam-report → $code (want 404)";;
esac

# ── 2-6. Hold / delete-refusal / resume cycle (needs TEST_VM_ID) ────────────
if [ -n "$TEST_VM_ID" ]; then
  hdr "hold / delete-refusal / resume cycle on $TEST_VM_ID"

  code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/suspend-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"$RUN_TAG hold\"}")
  if [ "$code" = "200" ]; then ok "suspend-vm → 200"; else bad "suspend-vm → $code"; fi

  code=$(acurl -X DELETE "$ORCH_URL/api/vms/$TEST_VM_ID")
  if [ "$code" = "400" ] && grep -q "VM_HELD" /tmp/hold_body; then
    ok "DELETE while held → 400 VM_HELD"
  else
    bad "DELETE while held → $code ($(head -c 120 /tmp/hold_body))"
  fi

  # Owner start must also be refused while held (Phase 2 behaviour — regression
  # check that this pass didn't disturb it). VmActionRequest.Action binds to the
  # VmAction ENUM; the API does not accept the string "Start" (that fails model
  # validation with an RFC-9110 400 before the hold check ever runs). Send the
  # enum's integer value. Assert on VM_HELD, not the bare 400: a plain 400 also
  # comes from model validation or INVALID_ACTION, either of which would pass
  # vacuously and tell us nothing about the hold.
  #   VmAction: Start=0, Stop=1, Restart=2, Pause=3, Resume=4, ForceStop=5
  code=$(acurl -X POST "$ORCH_URL/api/vms/$TEST_VM_ID/action" -d '{"action":0}')
  if [ "$code" = "400" ] && grep -q "VM_HELD" /tmp/hold_body; then
    ok "Start while held → 400 VM_HELD"
  elif [ "$code" = "400" ]; then
    bad "Start while held → 400 but NOT VM_HELD: $(head -c 160 /tmp/hold_body)"
  else
    bad "Start while held → $code ($(head -c 120 /tmp/hold_body))"
  fi

  code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/resume-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"$RUN_TAG cleanup\"}")
  if [ "$code" = "200" ]; then ok "resume-vm → 200 (human-in-the-loop unsuspend works; no 501)"; else bad "resume-vm → $code"; fi

  # The audit must show THIS run's pair — both limbs, not just one.
  code=$(acurl "$ORCH_URL/api/admin/compliance/actions")
  if [ "$code" != "200" ]; then
    bad "audit log fetch → $code"
  elif grep -q "$RUN_TAG hold" /tmp/hold_body && grep -q "$RUN_TAG cleanup" /tmp/hold_body; then
    ok "audit log contains this run's hold/resume pair ($RUN_TAG)"
  else
    bad "audit log has no rows for $RUN_TAG — enforcement actions not recorded"
  fi
else
  hdr "hold / delete-refusal cycle SKIPPED (set TEST_VM_ID to run)"
fi

# ── 7. Abuse queue reachable ────────────────────────────────────────────────
hdr "abuse queue (Phase 4) reachable"
code=$(acurl "$ORCH_URL/api/admin/abuse")
if [ "$code" = "200" ]; then ok "GET /api/admin/abuse → 200"; else bad "GET /api/admin/abuse → $code"; fi

# ── Summary ─────────────────────────────────────────────────────────────────
echo
echo "════════════════════════════════════════════════════════════"
echo "PASS=$PASS FAIL=$FAIL"
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
