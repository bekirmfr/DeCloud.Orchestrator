# NodeAgent Patches for Smart Port Allocation - Port Acknowledgment

## Overview

These patches update the DeCloud NodeAgent to send acknowledgments with the actual allocated port number when processing AllocatePort commands. This fixes the issue where the Orchestrator shows `publicPort: 0` instead of the real allocated port (40000-65535 range).

## Repository Information

- **Repository**: https://github.com/bekirmfr/DeCloud.NodeAgent
- **Branch**: master (patches based on commit 5fe125b)
- **Phase 1**: Already implemented (port allocation core functionality)

## Patch Files

### Patch 1: `001-add-data-to-interface.patch`
**File**: `src/DeCloud.NodeAgent.Core/Interfaces/IServices.cs`

**Changes**:
- Adds optional `data` parameter to `IOrchestratorClient.AcknowledgeCommandAsync` interface (line 194)
- Signature: `Task<bool> AcknowledgeCommandAsync(string commandId, bool success, string? errorMessage, string? data = null, CancellationToken ct = default);`

### Patch 2: `002-update-orchestratorclient-impl.patch`
**File**: `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs`

**Changes**:
- Updates `AcknowledgeCommandAsync` implementation to accept `data` parameter (line 811)
- Includes `data` field in the JSON payload sent to Orchestrator (line 838)
- Maintains backward compatibility with optional parameter

### Patch 3: `003-send-port-data-in-ack.patch`
**File**: `src/DeCloud.NodeAgent/Services/CommandProcessorService.cs`

**Changes**:
- Modifies `ExecuteCommandAsync` to return `(bool success, string? data)` tuple instead of just `bool`
- Updates `ProcessCommandAsync` to pass data to `AcknowledgeCommandAsync` (lines 199-204, 217)
- Updates `HandleAllocatePortAsync` to return port data as JSON (lines 506-585):
  ```json
  {
    "VmPort": 22,
    "PublicPort": 45123,
    "Protocol": 1
  }
  ```
- Updates all other command handlers to return `(success, null)` tuple

## How to Apply

### Method 1: Git Apply (Recommended)

```bash
cd /path/to/DeCloud.NodeAgent

# Apply patches in order
git apply /path/to/patches/001-add-data-to-interface.patch
git apply /path/to/patches/002-update-orchestratorclient-impl.patch
git apply /path/to/patches/003-send-port-data-in-ack.patch

# Verify changes
git status
git diff

# Test build
dotnet build

# Commit if successful
git add -A
git commit -m "feat: Add port acknowledgment with allocated port data

- Add Data parameter to AcknowledgeCommandAsync
- Send allocated port info in AllocatePort acknowledgments
- Allows Orchestrator to update publicPort from 0 to actual value

Fixes publicPort: 0 issue in Direct Access UI"
```

### Method 2: Manual Application

If `git apply` fails due to line number differences:

1. **Open each patch file** and review the changes
2. **Manually edit the corresponding files**:
   - `src/DeCloud.NodeAgent.Core/Interfaces/IServices.cs`
   - `src/DeCloud.NodeAgent/Services/OrchestratorClient.cs`
   - `src/DeCloud.NodeAgent/Services/CommandProcessorService.cs`
3. **Apply the changes** as shown in the patches
4. **Test build**: `dotnet build`

## Testing

### 1. Build and Deploy

```bash
cd /path/to/DeCloud.NodeAgent

# Build
dotnet build

# If using install script, run it
sudo bash install.sh

# Or manually copy and restart
sudo systemctl stop decloud-nodeagent
sudo cp -r bin/Release/net8.0/* /opt/decloud/nodeagent/
sudo systemctl start decloud-nodeagent
```

### 2. Test Port Allocation

1. **Open Orchestrator UI** in browser
2. **Create or select a VM**
3. **Click "Direct Access" button**
4. **Click "SSH" quick-add button**
5. **Verify** the public port shows a real number (e.g., 45123) instead of 0

### 3. Verify in Logs

**NodeAgent logs**:
```bash
sudo journalctl -u decloud-nodeagent -f
```

Expected output:
```
[INFO] Allocating port for VM ... (192.168.122.227:22) - TCP
[INFO] ✓ Port allocated: 45123 → 192.168.122.227:22 (VM ...)
[INFO] ✓ Command ... completed: True
```

**Orchestrator logs**:
```bash
sudo journalctl -u decloud-orchestrator -f
```

Expected output:
```
[INFO] Processing acknowledgment for command ...: Success=True
[INFO] Port allocation confirmed for VM ...
[INFO] ✓ Updated port mapping for VM ...: 22 → 45123 (1)
```

### 4. Verify iptables Rules

```bash
sudo iptables -t nat -L -n -v | grep 45123
```

Expected output:
```
DNAT tcp -- * * 0.0.0.0/0 0.0.0.0/0 tcp dpt:45123 to:192.168.122.227:22
```

### 5. Test Connection

```bash
# Replace with your actual DNS and port
ssh user@bu1-ef62.direct.stackfi.tech -p 45123
```

## Architecture Flow

```
┌─────────────────────────────────────────────────────────────────┐
│ 1. User clicks "Add SSH Port" in Orchestrator UI               │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 2. Orchestrator creates DirectAccess mapping with publicPort=0 │
│    Sends AllocatePort command to NodeAgent                      │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 3. NodeAgent receives command via push or pull                 │
│    CommandProcessorService.ProcessCommandAsync()                │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 4. NodeAgent allocates port from pool (e.g., 45123)            │
│    HandleAllocatePortAsync():                                   │
│    - PortPoolManager.AllocatePortAsync()                        │
│    - PortMappingRepository.AddAsync()                           │
│    - PortForwardingManager.CreateForwardingAsync()              │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 5. NodeAgent creates acknowledgment data (NEW)                 │
│    {                                                            │
│      "VmPort": 22,                                              │
│      "PublicPort": 45123,  ← The actual allocated port!        │
│      "Protocol": 1                                              │
│    }                                                            │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 6. NodeAgent sends acknowledgment with data (UPDATED)          │
│    OrchestratorClient.AcknowledgeCommandAsync(                 │
│      commandId, success=true, errorMessage=null, data=JSON)    │
│    POST /api/nodes/{nodeId}/commands/{commandId}/acknowledge   │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 7. Orchestrator receives acknowledgment                        │
│    NodeService.ProcessCommandAcknowledgmentAsync()              │
│    - Parses Data field                                          │
│    - Updates DirectAccess.PortMappings[].PublicPort = 45123    │
└────────────────────┬────────────────────────────────────────────┘
                     │
                     ↓
┌─────────────────────────────────────────────────────────────────┐
│ 8. User sees correct port in UI                                │
│    ssh user@bu1-ef62.direct.stackfi.tech -p 45123 ✅           │
└─────────────────────────────────────────────────────────────────┘
```

## Compatibility

- **NodeAgent**: Requires Phase 1 implementation (already in master branch)
- **Orchestrator**: Requires commit `ac10790` or later
- **.NET Version**: .NET 8.0
- **Dependencies**: No new NuGet packages required

## Troubleshooting

### Issue: Patches don't apply cleanly

**Solution**: Check if the NodeAgent code has diverged from commit 5fe125b. If so:
1. Review the patch files to understand the changes
2. Manually apply the changes to your current codebase
3. Pay attention to line numbers - they may differ

### Issue: Port still shows as 0 after patch

**Possible causes**:
1. NodeAgent not restarted after applying patches
2. Orchestrator not running updated code (commit ac10790+)
3. Acknowledgment failed to send (check NodeAgent logs)
4. Acknowledgment data malformed (check Orchestrator logs)

**Debug**:
```bash
# Check if NodeAgent is sending data
sudo journalctl -u decloud-nodeagent -n 100 | grep -A 5 "Port allocated"

# Check if Orchestrator is receiving it
sudo journalctl -u decloud-orchestrator -n 100 | grep -A 5 "Processing acknowledgment"

# Check for errors
sudo journalctl -u decloud-nodeagent -p err
sudo journalctl -u decloud-orchestrator -p err
```

### Issue: Build errors after applying patches

**Common errors**:
- `CS0103`: Name doesn't exist in context → Check if you added `using System.Text.Json;` if needed
- `CS1061`: Method doesn't contain definition → Check if all three patches were applied
- `CS0029`: Cannot convert type → Check tuple syntax `(bool, string?)` in return types

## Related Changes

### Orchestrator Side (Already Completed)
- **Commit ac10790**: Added `Data` field to `CommandAcknowledgment` model
- **Commit ac10790**: Added `AllocatePortAckData` model
- **Commit ac10790**: Added acknowledgment processing in `NodeService.ProcessCommandAcknowledgmentAsync`
- **Commit b61a729**: Added `Cloudflare` configuration section
- **Commit c01bfbf**: Added `--cloudflare-zone-id` to install script

### NodeAgent Side (These Patches)
- **Patch 1**: Add `data` parameter to interface
- **Patch 2**: Update implementation to send data
- **Patch 3**: Include allocated port in acknowledgment

## Rollback

If you need to rollback the changes:

```bash
cd /path/to/DeCloud.NodeAgent

# If you committed the changes
git revert HEAD

# If you haven't committed
git checkout -- src/DeCloud.NodeAgent.Core/Interfaces/IServices.cs
git checkout -- src/DeCloud.NodeAgent/Services/OrchestratorClient.cs
git checkout -- src/DeCloud.NodeAgent/Services/CommandProcessorService.cs

# Rebuild
dotnet build

# Redeploy
sudo systemctl restart decloud-nodeagent
```

## Support

For issues applying these patches:
1. Check that you're on the correct branch (master)
2. Ensure Phase 1 is already implemented (port allocation core)
3. Review the actual file contents vs patch expectations
4. Check build errors carefully - they often indicate missing steps

## License

These patches are provided as-is for the DeCloud.NodeAgent project.
