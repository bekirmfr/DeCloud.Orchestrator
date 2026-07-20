#!/usr/bin/env bash
# ============================================================================
# test-scan-failed-path.sh — Phase 6 pass 2b, the Failed branch.
#
# Proves the finding that justified the ack-routing in NodeService (§12): a scan
# that CANNOT run lands on the report as status=Failed with the node's reason —
# never dropped into silence. A held VM is stopped, so the ordinary path always
# succeeds; to see a refusal we must scan a RUNNING VM. The node's independent
# not-running check (§4.5 step 3) then refuses, the ack carries success=false,
# and the record must move Ordered → Failed.
#
# The trick: hold the VM under a reference (so scan-vm's guard passes), then
# START it while still held (owner-start is refused, but an admin start via the
# action endpoint is... also refused by ComplianceHold — see NOTE). So the only
# way to get a running+held VM is to hold a VM that is ALREADY running and rely
# on the hold's force-stop NOT having completed, which is racy and unreliable.
#
# The RELIABLE method, and the one this script uses: order the scan through the
# normal chain, but point it at a VM you keep running by NOT letting the hold
# stop it — which the system doesn't allow. So instead we prove the branch at
# the SEAM the node actually checks: we cannot easily force "running" from the
# orchestrator, so this script DOCUMENTS the manual procedure and verifies the
# outcome, rather than forcing the race.
#
# Usage (manual, two steps — read the NOTE):
#   ORCH_URL=... ADMIN_JWT=... REF=ABU-YYYY-NNNNN VM_ID=... COMMAND_ID=...
#   ./test-scan-failed-path.sh
#
# where you have ALREADY, by hand:
#   1. confirmed VM_ID is RUNNING and held under REF (see NOTE for how), and
#   2. ordered a scan (POST scan-vm) whose commandId is COMMAND_ID.
#
# This script then polls the report and asserts the record for COMMAND_ID
# resolved to Failed with a running-VM error.
# ============================================================================
set -u

ORCH_URL="${ORCH_URL:?Set ORCH_URL}"
ADMIN_JWT="${ADMIN_JWT:?Set ADMIN_JWT}"
REF="${REF:?Set REF (the report the scan was ordered under)}"
COMMAND_ID="${COMMAND_ID:?Set COMMAND_ID (from the scan-vm you ordered)}"

PASS=0; FAIL=0
ok()  { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad() { echo "  ✗ $1"; FAIL=$((FAIL+1)); }

acurl() { : > /tmp/sfp_body; curl -s -o /tmp/sfp_body -w "%{http_code}" \
          -H "Authorization: Bearer $ADMIN_JWT" -H "Content-Type: application/json" "$@"; }

echo "Polling report $REF for scan record $COMMAND_ID (up to 60s)…"
deadline=$(( $(date +%s) + 60 ))
rec=""
while [ "$(date +%s)" -lt "$deadline" ]; do
  code=$(acurl "$ORCH_URL/api/admin/abuse")
  [ "$code" = "200" ] || { sleep 5; continue; }
  rec=$(jq -c --arg r "$REF" --arg c "$COMMAND_ID" \
    '.data[]?.report | select(.reference==$r) | .scanRecords[]? | select(.commandId==$c)' \
    /tmp/sfp_body 2>/dev/null)
  st=$(echo "$rec" | jq -r '.status' 2>/dev/null)
  # 0=Ordered 1=Completed 2=Failed. Stop once it leaves Ordered.
  [ "$st" = "1" ] || [ "$st" = "2" ] && break
  sleep 5
done

if [ -z "$rec" ]; then
  bad "no scan record for commandId $COMMAND_ID under $REF — was the scan ordered?"
  echo "PASS=$PASS FAIL=$FAIL"; exit 1
fi

echo "  record: $rec"
st=$(echo "$rec" | jq -r '.status')
err=$(echo "$rec" | jq -r '.error // ""')

case "$st" in
  2)
    ok "record resolved to Failed (status 2) — NOT dropped into silence"
    if echo "$err" | grep -qi "running"; then
      ok "error names the cause: \"$err\""
    else
      bad "Failed, but error doesn't mention running: \"$err\""
    fi
    ;;
  1)
    bad "record is Completed (status 1) — the VM was NOT running when scanned, so this did not exercise the Failed branch. Re-run against a genuinely running VM (see NOTE)."
    ;;
  0)
    bad "record still Ordered after 60s — the ack never landed. If the VM was running and the node refused, the failure was DROPPED: investigate NodeService ack-routing (§12)."
    ;;
  *)
    bad "unexpected status: $st"
    ;;
esac

echo
echo "════════════════════════════════════════════════════════════"
echo "PASS=$PASS FAIL=$FAIL"
cat <<'EOF'

NOTE — how to get a running, held VM to scan (the setup this script assumes):

The system deliberately makes this hard: a ComplianceHold force-stops the VM,
and owner/admin start is refused while held. That is correct — it is the whole
point of the hold. So you cannot cleanly produce "running AND held" through the
normal API.

Two honest ways to exercise the node's running-VM refusal:

  A. Race the hold (best-effort, may need repeats):
     - Ensure VM is Running and NOT held.
     - In one shell, POST suspend-vm {vmId, reference}.
     - IMMEDIATELY in another, POST scan-vm {vmId, reference, reason}.
     If the scan command reaches the node before the force-stop completes, the
     node sees Running and refuses → Failed. Timing-dependent; retry if it lands
     Completed.

  B. Node-level forced check (deterministic, node-side):
     - Confirm the node's own not-running guard directly by ordering a scan and,
       on the node, watching for "ScanVm: refusing VM {id} — it is Running".
       This requires the VM to be Running at scan time on that node.

If neither is practical in your environment, record the Failed branch as
"verified by construction, not observed" — the ack-routing that carries a
success=false result is the SAME code path as the Completed routing (NodeService
§12, the else-branch), and Completed is observed. The branch is one if/else away
from proven; note it honestly rather than forcing a flaky race.
EOF
exit $([ "$FAIL" -eq 0 ] && echo 0 || echo 1)
