# NodeAgent Patches for Smart Port Allocation

## Overview

These patches update the DeCloud NodeAgent to send acknowledgments with the actual allocated port number when processing AllocatePort commands. This fixes the issue where the Orchestrator shows `publicPort: 0` instead of the real allocated port.

## Patch Files

### 1. `nodeagent-port-acknowledgment.patch`
**File**: `src/DeCloud.NodeAgent/Services/PortAllocationService.cs`

**Changes**:
- Adds `IOrchestratorClient` dependency injection
- Updates `HandleAllocatePortCommandAsync` to accept `commandId` parameter
- Sends success acknowledgment with JSON data containing:
  - `VmPort`: The VM's internal port
  - `PublicPort`: The actual allocated public port (40000-65535)
  - `Protocol`: TCP (1), UDP (2), or Both (3)
- Updates `HandleRemovePortCommandAsync` to accept `commandId` parameter
- Sends acknowledgments for both success and failure cases
- Adds private `SendAcknowledgmentAsync` helper method

**Example acknowledgment data**:
```json
{
  "VmPort": 22,
  "PublicPort": 45123,
  "Protocol": 1
}
```

### 2. `nodeagent-command-handler.patch`
**File**: `src/DeCloud.NodeAgent/Services/CommandHandlerService.cs`

**Changes**:
- Updates `AllocatePort` and `RemovePort` command dispatch to pass `commandId`
- Updates method signatures for both handlers to accept `commandId` as first parameter

## How to Apply

### Option 1: Manual Application (Recommended)

1. Clone the NodeAgent repository:
   ```bash
   git clone https://github.com/bekirmfr/DeCloud.NodeAgent.git
   cd DeCloud.NodeAgent
   ```

2. Review the patch files to understand the changes

3. Manually apply the changes to:
   - `src/DeCloud.NodeAgent/Services/PortAllocationService.cs`
   - `src/DeCloud.NodeAgent/Services/CommandHandlerService.cs`

4. Ensure the `IOrchestratorClient` interface has a method:
   ```csharp
   Task SendCommandAcknowledgmentAsync(string commandId, object acknowledgment);
   ```

### Option 2: Using Git Apply

If the file structure matches exactly:

```bash
cd /path/to/DeCloud.NodeAgent

# Apply port allocation service patch
git apply nodeagent-port-acknowledgment.patch

# Apply command handler patch
git apply nodeagent-command-handler.patch
```

### Option 3: Using Patch Command

```bash
cd /path/to/DeCloud.NodeAgent

# Apply patches
patch -p1 < nodeagent-port-acknowledgment.patch
patch -p1 < nodeagent-command-handler.patch
```

## Testing

After applying the patches:

1. **Build the NodeAgent**:
   ```bash
   dotnet build
   ```

2. **Deploy to a test node**:
   ```bash
   sudo systemctl stop decloud-nodeagent
   sudo systemctl start decloud-nodeagent
   ```

3. **Test port allocation**:
   - Create a VM via the Orchestrator UI
   - Open Direct Access modal
   - Click "SSH" quick-add button
   - Check the port mapping - should show actual port number (e.g., 45123) instead of 0

4. **Verify in logs**:
   ```bash
   sudo journalctl -u decloud-nodeagent -f
   ```

   You should see:
   ```
   [INFO] Allocated public port 45123 for VM ... port 22
   [INFO] ✓ Port allocation complete: ... port 22 → public port 45123 (TCP)
   [DEBUG] Sent acknowledgment for command ...: Success=True
   ```

5. **Check Orchestrator logs**:
   ```bash
   sudo journalctl -u decloud-orchestrator -f
   ```

   You should see:
   ```
   [INFO] Port allocation confirmed for VM ...
   [INFO] ✓ Updated port mapping for VM ...: 22 → 45123 (1)
   ```

## Orchestrator Changes

The Orchestrator has already been updated with these commits:

- `ac10790` - "feat: Add port acknowledgment handling for Smart Port Allocation"
  - Added `Data` field to `CommandAcknowledgment`
  - Added `AllocatePortAckData` model
  - Added handling in `ProcessCommandAcknowledgmentAsync`

## Compatibility

- **NodeAgent**: Phase 1A and 1B must be implemented (port allocation core)
- **Orchestrator**: Must be running commit `ac10790` or later
- **.NET Version**: .NET 8.0
- **Protocol**: Uses existing command acknowledgment endpoint

## Troubleshooting

### Issue: Acknowledgment not received by Orchestrator

**Check**:
1. NodeAgent can reach Orchestrator API
2. API key is valid
3. Network connectivity between node and orchestrator

**Logs**:
```bash
# NodeAgent
sudo journalctl -u decloud-nodeagent -f | grep -i "acknowledgment"

# Orchestrator
sudo journalctl -u decloud-orchestrator -f | grep -i "acknowledgment"
```

### Issue: Port still shows as 0

**Possible causes**:
1. Acknowledgment not sent by NodeAgent
2. Acknowledgment sent but Data field is empty/malformed
3. VM DirectAccess port mapping not found

**Debug**:
```bash
# Check NodeAgent allocated the port
sudo journalctl -u decloud-nodeagent -n 100 | grep "Allocated public port"

# Check acknowledgment was sent
sudo journalctl -u decloud-nodeagent -n 100 | grep "Sent acknowledgment"

# Check Orchestrator received it
sudo journalctl -u decloud-orchestrator -n 100 | grep "Processing acknowledgment"
```

### Issue: iptables rules not working

This is unrelated to acknowledgments. Check:
```bash
# View iptables NAT rules
sudo iptables -t nat -L -n -v

# Should see DNAT rule
# Example: DNAT tcp -- * * 0.0.0.0/0 0.0.0.0/0 tcp dpt:45123 to:192.168.122.227:22
```

## Architecture

```
┌─────────────────┐
│  User Browser   │
└────────┬────────┘
         │ Clicks "Add SSH Port"
         ↓
┌─────────────────┐
│  Orchestrator   │
│                 │
│  1. Creates     │
│     DirectAccess│
│     with port=0 │
│                 │
│  2. Sends       │
│     AllocatePort│
│     command     │
└────────┬────────┘
         │
         ↓
┌─────────────────┐
│   NodeAgent     │
│                 │
│  3. Allocates   │
│     port 45123  │
│                 │
│  4. Sets up     │
│     iptables    │
│                 │
│  5. Sends ACK   │
│     with Data:  │
│     {           │
│       VmPort:22,│
│       PublicPort│
│       :45123    │
│     }           │
└────────┬────────┘
         │
         ↓
┌─────────────────┐
│  Orchestrator   │
│                 │
│  6. Receives ACK│
│                 │
│  7. Updates     │
│     DirectAccess│
│     port to     │
│     45123       │
│                 │
│  8. User sees   │
│     real port   │
└─────────────────┘
```

## Related Changes

- **Orchestrator**: `src/Orchestrator/Models/Node.cs` - Added `Data` field to `CommandAcknowledgment`
- **Orchestrator**: `src/Orchestrator/Services/NodeService.cs` - Added acknowledgment processing
- **Orchestrator**: `install.sh` - Added `--cloudflare-zone-id` parameter
- **Orchestrator**: `appsettings.json` - Added `Cloudflare` configuration section

## Contact

If you encounter issues applying these patches, check:
1. Your NodeAgent file structure matches the expected layout
2. Phase 1A and 1B are properly implemented
3. IOrchestratorClient interface exists and is properly injected

For questions, refer to the DeCloud.Orchestrator repository commit `ac10790` for the Orchestrator-side implementation.
