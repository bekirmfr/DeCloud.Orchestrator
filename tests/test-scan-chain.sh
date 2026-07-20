#!/usr/bin/env bash
# ============================================================================
# test-scan-chain.sh — Phase 6 pass 2b, orchestrator-verifiable half.
#
# The guard is the feature: a scan happens only on cause, and "on cause" is
# "the VM is held under a reference that matches the one you cite." So most of
# this script is a refusal matrix — proving scan-vm fails closed — and one
# happy path that lands a stub result on a real report.
#
# What it CANNOT verify from the orchestrator: that the node ran the scanner,
# refused a running VM, and the ack landed. That's the node-side half —
# test-scan-node.sh, run on a node. This script's happy path leaves an Ordered
# (or, once the node acks, Completed/NotScanned) record on a report; it checks
# the record appears, and notes that the outcome fills in asynchronously.
#
# Usage:
#   ORCH_URL=https://orchestrator.example ADMIN_JWT=eyJ... \
#   TEST_VM_ID=<disposable tenant VM id, owned or admin-visible> \
#   ./test-scan-chain.sh
#
# Destructive against TEST_VM_ID: files a csam report naming it, holds it,
# scans it, then resumes. Leaves it STOPPED (resume lifts the hold, does not
# restart — Decision 10) and leaves a resolved-as-dismissed csam report behind
# (there is no report-delete path; that's expected).
# ============================================================================
set -u

ORCH_URL="${ORCH_URL:?Set ORCH_URL}"
ADMIN_JWT="${ADMIN_JWT:?Set ADMIN_JWT}"
TEST_VM_ID="${TEST_VM_ID:?Set TEST_VM_ID (a disposable tenant VM id)}"

RUN_TAG="scan-test-$(date +%s)-$$"
PASS=0; FAIL=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
hdr()  { echo; echo "── $1"; }

acurl() { : > /tmp/scan_body; curl -s -o /tmp/scan_body -w "%{http_code}" \
          -H "Authorization: Bearer $ADMIN_JWT" -H "Content-Type: application/json" "$@"; }
pubcurl() { : > /tmp/scan_body; curl -s -o /tmp/scan_body -w "%{http_code}" -H "Content-Type: application/json" "$@"; }
body() { cat /tmp/scan_body; }
jqget() { body | jq -r "$1" 2>/dev/null; }

echo "run tag: $RUN_TAG   vm: $TEST_VM_ID"

# ── 0. Cleanup helper: always try to lift the hold on exit ──────────────────
cleanup() {
  acurl -X POST "$ORCH_URL/api/admin/compliance/resume-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"$RUN_TAG cleanup\"}" >/dev/null
}
trap cleanup EXIT

# ── 1. File a csam report naming the VM (public intake) ─────────────────────
# scan-vm needs a real reference to cite. Category 0 = Csam. reportedResource
# "vm:<id>" is the shape pass 1 used and the UI's r_vmId() expects.
hdr "file a csam report to scan against"
code=$(pubcurl -X POST "$ORCH_URL/api/abuse" \
  -d "{\"category\":0,\"reportedResource\":\"vm:$TEST_VM_ID\",\"description\":\"$RUN_TAG synthetic report for scan-chain test\"}")
if [ "$code" = "200" ]; then
  REF=$(jqget '.data.reference')
  [ -n "$REF" ] && [ "$REF" != "null" ] && ok "report filed → $REF" || { bad "no reference in receipt: $(body|head -c160)"; exit 1; }
else
  bad "POST /api/abuse → $code ($(body|head -c160))"; exit 1
fi

# A second report, NOT about this VM — used for the reference-mismatch test.
code=$(pubcurl -X POST "$ORCH_URL/api/abuse" \
  -d "{\"category\":0,\"reportedResource\":\"vm:00000000-0000-0000-0000-000000000000\",\"description\":\"$RUN_TAG decoy\"}")
REF_OTHER=$(jqget '.data.reference')

# ── 2. REFUSALS (the guard fails closed) ────────────────────────────────────
hdr "scan-vm refusal matrix"

# 2a. Empty reference → refused. NOTE: ScanVmRequest.Reference is non-nullable, so
#     OMITTING the key fails at MODEL BINDING (RFC-9110 400) before the controller's
#     REFERENCE_REQUIRED check runs. Sending an empty STRING reaches the guard and
#     yields REFERENCE_REQUIRED. Both are correct fail-closed refusals; accept either
#     — what matters is that no-reference never scans.
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"\",\"reason\":\"$RUN_TAG\"}")
if [ "$code" = "400" ] && grep -q "REFERENCE_REQUIRED" /tmp/scan_body; then
  ok "empty reference → 400 REFERENCE_REQUIRED (controller guard)"
elif [ "$code" = "400" ]; then
  ok "empty reference → 400 (model binding refused; still fails closed)"
else
  bad "empty reference → $code ($(body|head -c120))"
fi

# 2a-bis. Omitted reference key → also refused (model binding, non-nullable field).
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reason\":\"$RUN_TAG\"}")
if [ "$code" = "400" ]; then ok "omitted reference → 400 (fails closed)"; else bad "omitted reference → $code (want 400)"; fi

# 2b. Reference that doesn't resolve → REPORT_NOT_FOUND
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"ABU-1999-00001\",\"reason\":\"$RUN_TAG\"}")
if grep -q "REPORT_NOT_FOUND\|NOT_HELD" /tmp/scan_body; then ok "bogus reference → refused ($(jqget '.error'))"; else bad "bogus reference → $code ($(body|head -c120))"; fi

# 2c. Real reference, but VM NOT held yet → 400 NOT_HELD
#     (this is the on-cause gate: you cannot scan a VM you have not held)
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"$REF\",\"reason\":\"$RUN_TAG\"}")
if [ "$code" = "400" ] && grep -q "NOT_HELD" /tmp/scan_body; then ok "not held → 400 NOT_HELD"; else bad "not held → $code ($(body|head -c120))"; fi

# ── 3. Hold the VM UNDER this report's reference ────────────────────────────
hdr "hold under $REF, then scan"
# suspend-vm carries an optional Reference (pass 2b §16): holding with it stamps
# ComplianceHoldReference, which is exactly what scan-vm's guard matches against.
# A hold WITHOUT a reference records null and cannot be scanned — by design.
# This test holds WITH the reference, so the happy path (§4) is reachable.
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/suspend-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"$REF\",\"reason\":\"$RUN_TAG hold\"}")
if [ "$code" = "200" ]; then
  ok "suspend-vm → 200 (held under $REF)"
elif [ "$code" = "000" ]; then
  bad "suspend-vm → 000 (NO RESPONSE — orchestrator unreachable/restarting, not a logic failure). Aborting; nothing downstream can be trusted."
  echo; echo "PASS=$PASS FAIL=$FAIL"; exit 1
else
  bad "suspend-vm → $code ($(body|head -c120))"
fi

# 2d. Held, but citing the OTHER report → REFERENCE_MISMATCH
if [ -n "$REF_OTHER" ] && [ "$REF_OTHER" != "null" ]; then
  code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
    -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"$REF_OTHER\",\"reason\":\"$RUN_TAG\"}")
  if grep -q "REFERENCE_MISMATCH" /tmp/scan_body; then ok "held but wrong reference → REFERENCE_MISMATCH"; else bad "wrong reference → $code ($(body|head -c120))"; fi
fi

# ── 4. HAPPY PATH: held under $REF, scanning $REF → 200, record ordered ─────
hdr "happy path: scan-vm under the matching reference"
code=$(acurl -X POST "$ORCH_URL/api/admin/compliance/scan-vm" \
  -d "{\"vmId\":\"$TEST_VM_ID\",\"reference\":\"$REF\",\"reason\":\"$RUN_TAG scan\"}")
if [ "$code" = "200" ]; then
  ok "scan-vm → 200 (command issued)"
else
  # Post-§16, a held VM cited under its own reference should never mismatch. If it
  # does, either the hold didn't store the reference (suspend-vm patch not applied
  # / not rebuilt) or the guard read it wrong — investigate, don't wave through.
  bad "scan-vm → $code ($(body|head -c160))"
fi

# ── 5. The Ordered record is on the report ──────────────────────────────────
hdr "scan record landed on $REF"
code=$(acurl "$ORCH_URL/api/admin/abuse")
if [ "$code" = "200" ]; then
  # Find our report in the queue, check it has a scanRecords entry.
  n=$(body | jq --arg r "$REF" '[.data[]?.report | select(.reference==$r) | .scanRecords[]?] | length' 2>/dev/null)
  if [ "${n:-0}" -ge 1 ]; then
    st=$(body | jq -r --arg r "$REF" '[.data[]?.report | select(.reference==$r) | .scanRecords[]?][-1].status' 2>/dev/null)
    ok "report $REF carries a scan record (status: $st)"
    echo "    NotScanned outcome fills in when the node acks — check the node with test-scan-node.sh,"
    echo "    then re-GET /api/admin/abuse and confirm status=Completed, outcome=NotScanned."
  else
    bad "report $REF has no scanRecords — AppendScanOrderedAsync didn't land"
  fi
else
  bad "GET /api/admin/abuse → $code"
fi

# ── 6. Audit row for the scan order ─────────────────────────────────────────
hdr "enforcement audit shows the ScanVm order"
code=$(acurl "$ORCH_URL/api/admin/compliance/actions")
if [ "$code" = "200" ] && body | grep -q "$RUN_TAG scan"; then
  ok "audit contains this run's ScanVm action"
else
  bad "no ScanVm audit row for $RUN_TAG (→ $code)"
fi

echo
echo "════════════════════════════════════════════════════════════"
echo "PASS=$PASS FAIL=$FAIL"
echo "Reference filed: $REF   (cleanup lifts the hold; report remains, by design)"
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
