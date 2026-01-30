# Targeted Node Deployment - VM Creation Fix

## Issue Summary

After introducing targeted node deployment with `request.NodeId`, VMs were failing to create with a `NullReferenceException` on the node agent side.

## Root Cause

The issue occurred in `LibvirtVmManager.GenerateLibvirtXml()` at line 1468-1469 where it attempts to access:

```csharp
var pointsPerVCpu = (config.Tiers[spec.QualityTier].MinimumBenchmark/config.BaselineBenchmark) *
                   (config.BaselineOvercommitRatio / config.Tiers[spec.QualityTier].CpuOvercommitRatio);
```

### The Problem

1. **Node Initialization**: When a node starts up, `NodeMetadataService.InitializeAsync()` creates a default `SchedulingConfig` with:
   - `BaselineBenchmark = 1000`
   - `BaselineOvercommitRatio = 4.0`
   - **Empty `Tiers` dictionary** ❌

2. **Timing Issue**: With targeted node deployment, the orchestrator can send a VM creation command immediately to a specific node, potentially before the node has received the full `SchedulingConfig` from the heartbeat response.

3. **Crash**: When `GenerateLibvirtXml()` tries to access `config.Tiers[spec.QualityTier]`, it throws a `KeyNotFoundException` because the Tiers dictionary is empty.

## Errors in Logs

```
fail: DeCloud.NodeAgent.Infrastructure.Libvirt.LibvirtVmManager[0]
      VM c2bf9a16-abec-4ed1-9be0-dcb432aa76df: Unexpected error during creation
      System.NullReferenceException: Object reference not set to an instance of an object.
         at DeCloud.NodeAgent.Infrastructure.Libvirt.LibvirtVmManager.GenerateLibvirtXml(...)
```

## Fixes Applied

### 1. Node Initialization State Management ✅ (Primary Fix)

**File**: `DeCloud.NodeAgent/src/DeCloud.NodeAgent.Infrastructure/Services/Metadata/NodeMetadataService.cs`

Added explicit initialization tracking:

```csharp
/// <summary>
/// True when node has received SchedulingConfig from orchestrator (version > 0)
/// </summary>
public bool IsFullyInitialized => SchedulingConfig?.Version > 0;
```

The node now tracks when it has received a valid `SchedulingConfig` from the orchestrator and is ready to accept VM creation commands.

### 2. Refuse Commands Until Initialized ✅ (Primary Fix)

**File**: `DeCloud.NodeAgent/src/DeCloud.NodeAgent/Services/CommandProcessorService.cs`

Added initialization check in `HandleCreateVmAsync()`:

```csharp
// Refuse VM creation commands until node has received SchedulingConfig from orchestrator
if (!_nodeMetadata.IsFullyInitialized)
{
    _logger.LogWarning(
        "❌ Refusing CreateVM command: Node not fully initialized. " +
        "Waiting for SchedulingConfig from orchestrator (current version: {Version}). " +
        "This command will be retried.",
        _nodeMetadata.GetSchedulingConfigVersion());
    return false; // Command will be retried by orchestrator
}
```

Commands are now **explicitly refused** until the node is ready, preventing crashes and providing clear error messages.

### 3. Initialize Default Tiers (Defensive Fallback) ✅

**File**: `DeCloud.NodeAgent/src/DeCloud.NodeAgent.Infrastructure/Services/Metadata/NodeMetadataService.cs`

Added proper tier configurations to the default `SchedulingConfig`:

```csharp
Tiers = new Dictionary<QualityTier, TierConfiguration>
{
    [QualityTier.Guaranteed] = new TierConfiguration { ... },
    [QualityTier.Standard] = new TierConfiguration { ... },
    [QualityTier.Balanced] = new TierConfiguration { ... },
    [QualityTier.Burstable] = new TierConfiguration { ... }
}
```

This provides a defensive fallback, though commands should be refused until proper config is received.

### 4. Add Defensive Tier Lookup (Additional Safety) ✅

**File**: `DeCloud.NodeAgent/src/DeCloud.NodeAgent.Infrastructure/Libvirt/LibvirtVmManager.cs`

Added defensive code in both `GenerateLibvirtXml()` and `GenerateLibvirtXmlMultiArch()`:

```csharp
// Defensive: Ensure tier exists in config, fallback to Standard if not
if (!config.Tiers.TryGetValue(spec.QualityTier, out var tierConfig))
{
    _logger.LogWarning(
        "VM {VmId}: Tier {Tier} not found in scheduling config, falling back to Standard tier",
        spec.Id, spec.QualityTier);
    tierConfig = config.Tiers[QualityTier.Standard];
}
```

This provides additional safety if a tier is missing from the configuration.

### 5. Fix Misleading Log Warning ✅

**File**: `DeCloud.Orchestrator/src/Orchestrator/Services/VmService.cs`

Fixed a bug where the warning "VM references non-existent node" was logged **every time** a VM had a NodeId, even when the node existed:

```csharp
// Before: Warning always logged
if (node != null)
{
    // populate connection details
}
_logger.LogWarning("VM references non-existent node"); // ❌ Always runs!

// After: Warning only when node is missing
if (node != null)
{
    // populate connection details
}
else
{
    _logger.LogWarning("VM references non-existent node"); // ✅ Only when needed
}
```

## Why This Wasn't an Issue Before

Previously, VMs were only scheduled to nodes that had:
1. Successfully registered with the orchestrator
2. Sent at least one heartbeat
3. Received the full `SchedulingConfig` in the heartbeat response

With targeted node deployment (`request.NodeId`), the orchestrator can bypass this natural synchronization and send commands immediately, exposing the timing race condition.

## Solution Architecture: Layered Defense

This fix implements a **defense-in-depth** strategy with multiple layers of protection:

### Layer 1: Explicit Command Refusal (Primary Protection)
- Node tracks initialization state via `IsFullyInitialized`
- Commands are **refused** until `SchedulingConfig` is received (version > 0)
- Clear error messages indicate the node is waiting for initialization
- Commands will be retried by orchestrator once node is ready

### Layer 2: Default Configuration (Fallback)
- Node initializes with sensible default tier configurations
- Ensures code doesn't crash even if Layer 1 check is bypassed
- Allows graceful degradation

### Layer 3: Defensive Lookups (Safety Net)
- `TryGetValue` checks before accessing tier configurations
- Falls back to Standard tier if requested tier is missing
- Logs warnings for debugging

### Expected Behavior Now

1. **Node starts up** → Initializes with default config (version 0)
2. **Command arrives early** → Refused with clear message: "Node not fully initialized"
3. **First heartbeat** → Receives SchedulingConfig v1+ from orchestrator
4. **Node logs**: "✅ Node fully initialized: Ready to accept VM commands"
5. **Subsequent commands** → Processed normally with correct configuration

## Testing Plan

1. **Create VM with Targeted Node**: Create a VM targeting a specific node immediately after node startup
2. **Verify Command Refusal**: Command should be refused with clear "not fully initialized" message
3. **Wait for Initialization**: Check logs for "✅ Node fully initialized" message
4. **Retry Command**: VM should be created successfully after initialization
5. **Check Logs**: No warnings about non-existent nodes (when nodes actually exist)
6. **Test All Tiers**: Create VMs with each quality tier (Guaranteed, Standard, Balanced, Burstable)

## Related Changes

The targeted node deployment feature allows users to select specific nodes from the marketplace:

```csharp
// In VmsController.Create():
var response = await _vmService.CreateVmAsync(userId, request, request.NodeId);
```

This is used for relay VM deployments where a specific node must be used for CGNAT relay functionality.

## Status

✅ **Fixed** - All changes applied and ready for deployment
