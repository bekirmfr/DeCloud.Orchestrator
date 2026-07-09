#!/usr/bin/env bash
# ============================================================================
# test-csam-pass1.sh — Phase 6 pass 1 smoke test (mirrors tests/test-abuse.sh)
#
# Covers the orchestrator-verifiable half of the pass. The node-side half
# (enrollment, scan seam, gate) is observed on a node via logs + lazysync.json;
# the manual checklist for that is printed at the end.
#
# Usage:
#   ORCH_URL=https://orchestrator.example ADMIN_JWT=eyJ... \
#   [TEST_VM_ID=<existing disposable tenant VM id>] ./test-csam-pass1.sh
#
# Notes:
#   - Uses only existing admin endpoints plus the new node-auth csam-report
#     endpoint (which it can only probe for auth behavior — a real report
#     needs a node JWT and is exercised via the node-side forced-match test).
#   - Destructive only against TEST_VM_ID (suspend → delete attempt → resume).
# ============================================================================
set -u

ORCH_URL="${ORCH_URL:?Set ORCH_URL}"
ADMIN_JWT="${ADMIN_JWT:?Set ADMIN_JWT}"
TEST_VM_ID="${TEST_VM_ID:-}"

PASS=0; FAIL=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
hdr()  { echo; echo "── $1"; }

acurl() { curl -s -o /tmp/csam_body -w "%{http_code}" -H "Authorization: Bearer $ADMIN_JWT" -H "Content-Type: application/json" "$@"; }
ncurl() { curl -s -o /tmp/csam_body -w "%{http_code}" -H "Content-Type: application/json" "$@"; }

# ── 1. csam-report endpoint exists and fails CLOSED ─────────────────────────
hdr "csam-report auth posture (fail closed)"

code=$(ncurl -X POST "$ORCH_URL/api/compliance/csam-report" \
  -d '{"vmId":"00000000-0000-0000-0000-000000000000","matchedFileHash":"x","detectedAt":"2026-07-08T00:00:00Z"}')
if [ "$code" = "401" ]; then ok "unauthenticated csam-report → 401"; else bad "unauthenticated csam-report → $code (want 401)"; fi

# An ADMIN token must NOT be able to file node reports (role is "node").
code=$(acurl -X POST "$ORCH_URL/api/compliance/csam-report" \
  -d '{"vmId":"00000000-0000-0000-0000-000000000000","matchedFileHash":"x","detectedAt":"2026-07-08T00:00:00Z"}')
if [ "$code" = "403" ]; then ok "admin-JWT csam-report → 403 (node role required)"; else bad "admin-JWT csam-report → $code (want 403)"; fi

# ── 2. Suspend → delete refused → resume (needs TEST_VM_ID) ─────────────────
if [ -n "$TEST_VM_ID" ]; then
  hdr "hold / delete-refusal / resume cycle on $TEST_VM_ID"

  code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/suspend-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"test-csam-pass1 hold\"}")
  if [ "$code" = "200" ]; then ok "suspend-vm → 200"; else bad "suspend-vm → $code"; fi

  code=$(acurl -X DELETE "$ORCH_URL/api/vms/$TEST_VM_ID")
  if [ "$code" = "400" ] && grep -q "VM_HELD" /tmp/csam_body; then
    ok "DELETE while held → 400 VM_HELD"
  else
    bad "DELETE while held → $code ($(cat /tmp/csam_body | head -c 120))"
  fi

  # Owner start must also be refused while held (existing Phase 2 behavior —
  # regression check that this pass didn't disturb it).
  code=$(acurl -X POST "$ORCH_URL/api/vms/$TEST_VM_ID/actions" -d '{"action":"Start"}')
  if [ "$code" = "400" ]; then ok "Start while held → 400"; else bad "Start while held → $code"; fi

  code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/resume-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"test-csam-pass1 cleanup\"}")
  if [ "$code" = "200" ]; then ok "resume-vm → 200 (human-in-the-loop unsuspend works; no 501)"; else bad "resume-vm → $code"; fi

  # Enforcement audit should show the pair.
  code=$(acurl "$ORCH_URL/api/admin/compliance/actions")
  if [ "$code" = "200" ] && grep -q "test-csam-pass1" /tmp/csam_body; then
    ok "audit log contains the hold/resume pair"
  else
    bad "audit log check → $code"
  fi
else
  hdr "hold / delete-refusal cycle SKIPPED (set TEST_VM_ID to run)"
fi

# ── 3. Abuse queue reachable (csam report lands here as P0) ─────────────────
hdr "abuse queue (Phase 4) reachable"
code=$(acurl "$ORCH_URL/api/admin/abuse")
if [ "$code" = "200" ]; then ok "GET /api/admin/abuse → 200"; else bad "GET /api/admin/abuse → $code"; fi

# ── Summary + node-side manual checklist ────────────────────────────────────
echo
echo "════════════════════════════════════════════════════════════"
echo "PASS=$PASS FAIL=$FAIL"
cat <<'EOF'

Node-side manual checklist (run on one node, report logs + DB back):

 A. Null scanner, RF>0 VM:
    - Cycle completes and replicates as before.
    - {VmStoragePath}/{vmId}/lazysync.json now has "csamScan" with
      outcome NotScanned (NOT Clean) and a lastScanAt timestamp.

 B. Enrollment, RF=0 VM (Decision 15):
    - Logs show snapshot taken + merged back each cycle for the RF=0 VM.
    - lazysync.json exists for it with csamScan.outcome = NotScanned,
      version stays 0, NO manifest registered, NO blocks pushed.
    - System VMs and containers: no enrollment, untouched.

 C. Forced Match (set Csam:Stub:ForceOutcome=Match on ONE node, restart agent):
    - Next cycle: loud NullCsamScanner warning; NO blocks pushed;
      csam-report POSTed; VM gains ComplianceHold and is force-stopped;
      a P0 (csam) item appears in the admin abuse queue with the ABU
      reference, joined to the wallet's enforcement history; the
      EnforcementAction carries Reference = that ABU id and
      actor "node:<nodeId>".
    - Owner cannot start it; DELETE refused (VM_HELD).
    - REMOVE the config, resume-vm, confirm normal cycles resume.

 D. Forced Unscannable (Csam:Stub:ForceOutcome=Unscannable):
    - Cycle proceeds (RF>0 VM still replicates); csamScan.outcome =
      Unscannable. Remove the config afterwards.

 E. Idle-VM merge-back (the flagged pre-existing gap):
    - On a fully idle RF>0 VM, confirm no lazysync-overlay-*.qcow2 files
      accumulate across cycles ("no changed chunks" path now merges back).
EOF
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
