// ============================================================
// MongoDB Recovery Script — fix false-positive Deleting cascade
// ============================================================
//
// Run in mongosh connected to the orchestrator database:
//   mongosh "mongodb://localhost:27017/orchestrator" recover-false-positive-deleting.js
//
// What happened:
//   The "0 connected peers" self-healing check false-positived on running
//   DHT VMs, transitioning them to Deleting (status 6). This stripped
//   their ingress domains and created orphaned duplicate VM records.
//
// What this script does:
//   1. Recovers valid system VMs: Deleting (6) + PowerState Running (1) → Running (3)
//   2. Fixes node obligations to point to the recovered VMs
//   3. Marks orphaned duplicate system VMs as Deleted (9)
//
// After running this script, restart the orchestrator so the lifecycle
// manager re-registers ingress routes for recovered VMs. Alternatively,
// deploy the updated code which includes auto-recovery — it will handle
// ingress re-registration automatically through the Deleting → Running
// transition in VmLifecycleManager.
// ============================================================

// Enum mappings:
//   VmType:         Relay=5, Dht=6
//   VmStatus:       Running=3, Deleting=6, Deleted=9
//   VmPowerState:   Running=1
//   SystemVmRole:   Dht=0, Relay=1
//   SystemVmStatus: Pending=0, Active=2

// ── Step 1: Find all system VMs incorrectly in Deleting state ──────────

const affectedVms = db.vms.find({
    "Spec.VmType": { $in: [5, 6] },  // Relay or Dht
    Status: 6,                         // Deleting
    PowerState: 1                      // Still Running on node
}).toArray();

print(`\nFound ${affectedVms.length} system VM(s) in false-positive Deleting state:\n`);
affectedVms.forEach(vm => {
    print(`  VM: ${vm._id}  Name: ${vm.Name}  Node: ${vm.NodeId}  Type: ${vm.Spec.VmType === 6 ? 'Dht' : 'Relay'}`);
});

if (affectedVms.length === 0) {
    print("\nNo VMs to recover. Exiting.");
    quit();
}

// ── Step 2: Recover affected VMs → Running ─────────────────────────────

const recoverResult = db.vms.updateMany(
    {
        "Spec.VmType": { $in: [5, 6] },
        Status: 6,
        PowerState: 1
    },
    {
        $set: {
            Status: 3,  // Running
            StatusMessage: "Recovered from false-positive Deleting state",
            UpdatedAt: new Date()
        }
    }
);
print(`\nRecovered ${recoverResult.modifiedCount} VM(s) to Running status.`);

// ── Step 3: Fix node obligations ───────────────────────────────────────

affectedVms.forEach(vm => {
    const roleInt = vm.Spec.VmType === 6 ? 0 : 1;  // Dht=0, Relay=1

    const nodeResult = db.nodes.updateOne(
        {
            _id: vm.NodeId,
            "SystemVmObligations.Role": roleInt
        },
        {
            $set: {
                "SystemVmObligations.$.VmId": vm._id,
                "SystemVmObligations.$.Status": 2,  // Active
                "SystemVmObligations.$.ActiveAt": new Date(),
                "SystemVmObligations.$.FailureCount": 0,
                "SystemVmObligations.$.LastError": null
            }
        }
    );

    if (nodeResult.modifiedCount > 0) {
        print(`  Fixed obligation on node ${vm.NodeId} for ${vm.Spec.VmType === 6 ? 'Dht' : 'Relay'} → VM ${vm._id}`);
    } else {
        print(`  WARNING: Could not fix obligation on node ${vm.NodeId} — check manually`);
    }
});

// ── Step 4: Clean up orphaned duplicate system VMs ─────────────────────
//
// For each affected node, find other DHT/Relay VMs that are NOT the
// recovered one and are in a non-terminal state. These are ghost records
// from failed redeployment attempts.

let totalOrphans = 0;
affectedVms.forEach(vm => {
    const orphans = db.vms.find({
        NodeId: vm.NodeId,
        "Spec.VmType": vm.Spec.VmType,
        _id: { $ne: vm._id },
        Status: { $nin: [3, 9] }  // Not Running, not already Deleted
    }).toArray();

    if (orphans.length > 0) {
        orphans.forEach(orphan => {
            print(`  Orphan: ${orphan._id}  Name: ${orphan.Name}  Status: ${orphan.Status}`);
        });

        const deleteResult = db.vms.updateMany(
            {
                NodeId: vm.NodeId,
                "Spec.VmType": vm.Spec.VmType,
                _id: { $ne: vm._id },
                Status: { $nin: [3, 9] }
            },
            {
                $set: {
                    Status: 9,  // Deleted
                    PowerState: 0,
                    StatusMessage: "Cleaned up: orphaned system VM from failed redeployment loop",
                    StoppedAt: new Date(),
                    UpdatedAt: new Date()
                }
            }
        );
        totalOrphans += deleteResult.modifiedCount;
    }
});

print(`\nMarked ${totalOrphans} orphaned duplicate VM(s) as Deleted.`);
print("\n✓ Recovery complete. Restart the orchestrator or deploy updated code to re-register ingress routes.\n");
