#!/usr/bin/env bash
# ============================================================================
# test-lazysync-regression.sh — pass 2a: lazysync is replication and nothing else.
#
# Replaces test-csam-node.sh, whose premise — observe the per-cycle CSAM scan —
# no longer exists after Decision 18. This verifies by REGRESSION: "lazysync
# behaves exactly as it did before pass 1."
#
# Runs ON THE NODE (root). Non-destructive: no config changes, no restarts,
# no VM touched. Runtime ~8 min.
#
# Checks:
#   A. RF>0 tenant VM is still enrolled and replicating.
#   B. RF=0 tenant VM is NOT enrolled — no snapshot, no cycle work at all.
#      This is 2a's headline: pass 1 enrolled it (Decision 15); 2a does not.
#   E. Merge-back invariant still holds — no lazysync-overlay-*.qcow2 survives
#      a cycle. Kept VERBATIM from test-csam-node.sh. This is the one pass-1
#      change 2a retains: a real pre-existing bug, unrelated to CSAM.
#   F. An existing lazysync.json carrying pass-1's "csamScan" still loads —
#      no "corrupt lazysync state" warning. This is the no-shim claim
#      (JsonOptions.Wire leaves UnmappedMemberHandling unset → Skip).
#
# ⚠ B is GATED ON A. Absence of a log line proves nothing unless a cycle
#   demonstrably ran. Ungated, B would pass green on a stopped agent — the
#   exact "green while the door stands open" shape this project keeps hitting.
#
# Usage (on the node, as root):
#   RF1_VM_ID=<RF>0 tenant VM id> RF0_VM_ID=<RF=0 tenant VM id> \
#   ./test-lazysync-regression.sh
#
# Optional env:
#   NODE_SERVICE  systemd unit          (default: decloud-node-agent)
#   VM_STORAGE    VM storage root       (default: /var/lib/decloud/vms)
#   CYCLE_WAIT    max wait for a cycle  (default: 420 = 7 min)
#
# BEFORE UPGRADING to the 2a agent, if you want check F to mean anything:
#   jq '.csamScan' $VM_STORAGE/<RF1_VM_ID>/lazysync.json
# If that prints null, the file never carried the field and F is vacuous —
# F only proves the migration path on a node that actually ran pass 1.
#
# What this does NOT prove, and why:
#   - "No BlockStore VM ⇒ no cycle at all" (2a DoD item 4). Proving it means
#     stopping the local BlockStore VM, which SystemVmReconciler redeploys
#     underneath the test. Do it by hand once on a node you can disturb and
#     record it in the build log. Do not fight the reconciler from a script.
#   - Whether the pre-fix changedChunks==0 overlay leak was ever live. E
#     proves the FIXED agent's invariant, not the old agent's bug. Run
#     `ls $VM_STORAGE/*/lazysync-overlay-*.qcow2` on a not-yet-updated node
#     to settle that, and record either answer.
#   - Container / system-VM exclusion. Always held; nothing in 2a touches it.
# ============================================================================
set -u

RF1_VM_ID="${RF1_VM_ID:?Set RF1_VM_ID (an RF>0 tenant VM on this node)}"
RF0_VM_ID="${RF0_VM_ID:?Set RF0_VM_ID (an RF=0 tenant VM on this node)}"
NODE_SERVICE="${NODE_SERVICE:-decloud-node-agent}"
VM_STORAGE="${VM_STORAGE:-/var/lib/decloud/vms}"
CYCLE_WAIT="${CYCLE_WAIT:-420}"

PASS=0; FAIL=0; SKIP=0
ok()   { echo "  ✓ $1"; PASS=$((PASS+1)); }
bad()  { echo "  ✗ $1"; FAIL=$((FAIL+1)); }
skip() { echo "  – $1 (skipped)"; SKIP=$((SKIP+1)); }
say()  { echo; echo "── $1"; }

# ── Preflight ────────────────────────────────────────────────────────────────
[ "$(id -u)" = "0" ] || { echo "Run as root (journalctl, VM dirs)."; exit 1; }
for dep in jq find systemctl journalctl; do
  command -v "$dep" >/dev/null || { echo "Missing dependency: $dep"; exit 1; }
done
systemctl is-active --quiet "$NODE_SERVICE" \
  || { echo "Service $NODE_SERVICE is not active. Set NODE_SERVICE=<unit>."; exit 1; }
for id in "$RF1_VM_ID" "$RF0_VM_ID"; do
  [ -d "$VM_STORAGE/$id" ] \
    || { echo "No $VM_STORAGE/$id — wrong VM_STORAGE or VM not on this node."; exit 1; }
done

# ── Helpers ──────────────────────────────────────────────────────────────────

# wait_log <since> <timeout_s> <fixed-string>  → 0 when the string appears
wait_log() {
  local since="$1" timeout="$2" s="$3"
  local deadline=$(( $(date +%s) + timeout ))
  while [ "$(date +%s)" -lt "$deadline" ]; do
    journalctl -u "$NODE_SERVICE" --since "$since" --no-pager -q 2>/dev/null \
      | grep -qF "$s" && return 0
    sleep 15
  done
  return 1
}

log_has() { journalctl -u "$NODE_SERVICE" --since "$1" --no-pager -q 2>/dev/null | grep -qF "$2"; }

# ════════════════════════════════════════════════════════════════════════════
say "A — RF>0 VM $RF1_VM_ID is still enrolled and replicating"
# ════════════════════════════════════════════════════════════════════════════
T0=$(date '+%Y-%m-%d %H:%M:%S')
echo "  waiting up to ${CYCLE_WAIT}s for a lazysync cycle…"

CYCLE_RAN=0
if wait_log "$T0" "$CYCLE_WAIT" "VM $RF1_VM_ID: snapshot taken"; then
  CYCLE_RAN=1
  if log_has "$T0" "VM $RF1_VM_ID: lazysync v"; then
    ok "A: $RF1_VM_ID snapshotted and replicated (manifest advanced)"
  else
    # An idle VM legitimately pushes nothing — 0 changed chunks. The snapshot
    # line alone still proves enrolment, which is what A is for.
    ok "A: $RF1_VM_ID snapshotted; no push this cycle (idle — 0 changed chunks)"
  fi
else
  bad "A: no snapshot line for RF>0 VM $RF1_VM_ID within ${CYCLE_WAIT}s — is the VM Running and IsFullyReady? is there a local BlockStore VM?"
fi

# ════════════════════════════════════════════════════════════════════════════
say "B — RF=0 VM $RF0_VM_ID is NOT enrolled (the 2a revert)"
# ════════════════════════════════════════════════════════════════════════════
if [ "$CYCLE_RAN" = 0 ]; then
  # Refusing to assert on absence when nothing ran. This is the whole point of
  # the gate: a silent journal is not evidence of a working filter.
  skip "B: no cycle observed (A failed) — absence of a snapshot line proves nothing"
else
  if log_has "$T0" "VM $RF0_VM_ID: snapshot taken"; then
    bad "B: RF=0 VM $RF0_VM_ID WAS snapshotted — the RF>0 enrolment filter did not revert"
  else
    ok "B: RF=0 VM $RF0_VM_ID not enrolled (no snapshot, in a cycle that demonstrably ran)"
  fi
  if log_has "$T0" "VM $RF0_VM_ID: lazysync v"; then
    bad "B: RF=0 VM $RF0_VM_ID pushed a manifest — it is replicating"
  fi
fi

# A stale lazysync.json from pass-1 cycles may still sit in the RF=0 VM's
# directory. That is expected and harmless — nothing reads it once the VM is
# unenrolled. B deliberately asserts on the journal, not on the file, so it
# does not fail on any node that ran pass 1.
if [ -f "$VM_STORAGE/$RF0_VM_ID/lazysync.json" ]; then
  echo "    (note: stale lazysync.json present for $RF0_VM_ID — expected, not read)"
fi

# ════════════════════════════════════════════════════════════════════════════
say "E — merge-back invariant (the pass-1 fix 2a keeps)"
# ════════════════════════════════════════════════════════════════════════════
# Grace period covers a cycle currently in flight, where an overlay is
# legitimately live mid-cycle.
sleep 90
leftover=$(find "$VM_STORAGE/$RF1_VM_ID" "$VM_STORAGE/$RF0_VM_ID" \
           -maxdepth 1 -name 'lazysync-overlay-*.qcow2' 2>/dev/null)
if [ -z "$leftover" ]; then
  ok "E: no lazysync-overlay-*.qcow2 persists after the cycle (single-exit merge holds)"
else
  bad "E: orphan overlay(s) found — merge-back invariant broken: $leftover"
fi

# ════════════════════════════════════════════════════════════════════════════
say "F — old lazysync.json with pass-1's csamScan still loads (no shim needed)"
# ════════════════════════════════════════════════════════════════════════════
AGENT_START_RAW=$(systemctl show -p ActiveEnterTimestamp --value "$NODE_SERVICE" 2>/dev/null)
AGENT_START=$(date -d "$AGENT_START_RAW" '+%Y-%m-%d %H:%M:%S' 2>/dev/null || echo "$T0")

if log_has "$AGENT_START" "corrupt lazysync state"; then
  bad "F: agent logged 'corrupt lazysync state' since startup — deserialization IS failing; a shim is needed after all"
else
  ok "F: no 'corrupt lazysync state' warning since agent start ($AGENT_START)"
fi

# After the first save, the field is dropped from the file. Its absence now is
# consistent with a clean round-trip — but is equally consistent with a file
# that never had it. Reported, not asserted; see the header's pre-upgrade note.
if [ -f "$VM_STORAGE/$RF1_VM_ID/lazysync.json" ]; then
  if jq -e '(.csamScan // .CsamScan) != null' "$VM_STORAGE/$RF1_VM_ID/lazysync.json" >/dev/null 2>&1; then
    echo "    (note: csamScan STILL in $RF1_VM_ID/lazysync.json — no save has happened since the upgrade; harmless, it is ignored on load)"
  else
    echo "    (note: csamScan absent from $RF1_VM_ID/lazysync.json — consistent with a clean round-trip; vacuous if the file never had it)"
  fi
fi

# ── Summary ──────────────────────────────────────────────────────────────────
echo
echo "════════════════════════════════════════════════════════════"
echo "PASS=$PASS FAIL=$FAIL SKIP=$SKIP"
echo
echo "Still owed to the 2a build log, by hand:"
echo "  · No BlockStore VM ⇒ no cycle at all, and no VM frozen (DoD 4)."
echo "  · ls $VM_STORAGE/*/lazysync-overlay-*.qcow2 on a NOT-yet-updated node —"
echo "    accumulated overlays there settle whether the pre-fix leak was live."
exit $([ "$FAIL" = 0 ] && echo 0 || echo 1)
