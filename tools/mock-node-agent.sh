#!/bin/bash
# Mock Node Agent - Simulates a node registering and sending heartbeats
# Usage: ./mock-node-agent.sh [node-name] [orchestrator-url]

set -e

NODE_NAME="${1:-test-node-1}"
ORCHESTRATOR_URL="${2:-http://localhost:5000}"
HEARTBEAT_INTERVAL=30

# Generate a mock wallet address
WALLET="0x$(openssl rand -hex 20)"

# Colors for output
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[SUCCESS]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[ERROR]${NC} $1"; }

echo "=========================================="
echo "       Mock Node Agent v1.0.0"
echo "=========================================="
echo ""
log_info "Node Name: $NODE_NAME"
log_info "Wallet: $WALLET"
log_info "Orchestrator: $ORCHESTRATOR_URL"
echo ""

# Step 1: Register the node
log_info "Registering node with orchestrator..."

REGISTER_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/nodes/register" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "'"$NODE_NAME"'",
    "walletAddress": "'"$WALLET"'",
    "publicIp": "192.168.1.100",
    "agentPort": 5050,
    "resources": {
      "cpuCores": 8,
      "memoryMb": 16384,
      "storageGb": 500,
      "bandwidthMbps": 1000
    },
    "agentVersion": "1.0.0",
    "supportedImages": ["ubuntu-24.04", "ubuntu-22.04", "debian-12", "fedora-40", "alpine-3.19"],
    "supportsGpu": false,
    "gpuInfo": null,
    "region": "us-east",
    "zone": "us-east-1a"
  }')

# Parse response
NODE_ID=$(echo "$REGISTER_RESPONSE" | grep -o '"nodeId":"[^"]*"' | cut -d'"' -f4)
NODE_TOKEN=$(echo "$REGISTER_RESPONSE" | grep -o '"authToken":"[^"]*"' | cut -d'"' -f4)

if [ -z "$NODE_ID" ] || [ -z "$NODE_TOKEN" ]; then
  log_error "Failed to register node!"
  echo "Response: $REGISTER_RESPONSE"
  exit 1
fi

log_success "Node registered!"
echo ""
echo "  Node ID: $NODE_ID"
echo "  Token: ${NODE_TOKEN:0:20}..."
echo ""

# Save credentials to file for other scripts
echo "NODE_ID=$NODE_ID" > /tmp/node-agent-$NODE_NAME.env
echo "NODE_TOKEN=$NODE_TOKEN" >> /tmp/node-agent-$NODE_NAME.env
log_info "Credentials saved to /tmp/node-agent-$NODE_NAME.env"
echo ""

# Step 2: Heartbeat loop
log_info "Starting heartbeat loop (every ${HEARTBEAT_INTERVAL}s)..."
log_info "Press Ctrl+C to stop"
echo ""

# Track simulated resources
AVAILABLE_CPU=8
AVAILABLE_MEM=16384
AVAILABLE_STORAGE=500
ACTIVE_VMS=()

send_heartbeat() {
  local cpu_usage=$(( RANDOM % 50 + 10 ))
  local mem_usage=$(( RANDOM % 40 + 20 ))
  local storage_usage=$(( RANDOM % 30 + 10 ))
  local load=$(echo "scale=2; $(( RANDOM % 300 )) / 100" | bc)
  
  # Build active VM IDs JSON array
  local vm_ids_json="[]"
  if [ ${#ACTIVE_VMS[@]} -gt 0 ]; then
    vm_ids_json=$(printf '%s\n' "${ACTIVE_VMS[@]}" | jq -R . | jq -s .)
  fi

  HEARTBEAT_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/nodes/$NODE_ID/heartbeat" \
    -H "Content-Type: application/json" \
    -H "X-Node-Token: $NODE_TOKEN" \
    -d '{
      "nodeId": "'"$NODE_ID"'",
      "metrics": {
        "timestamp": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'",
        "cpuUsagePercent": '"$cpu_usage"',
        "memoryUsagePercent": '"$mem_usage"',
        "storageUsagePercent": '"$storage_usage"',
        "networkInMbps": '"$(( RANDOM % 500 ))"',
        "networkOutMbps": '"$(( RANDOM % 200 ))"',
        "activeVmCount": '"${#ACTIVE_VMS[@]}"',
        "loadAverage": '"$load"'
      },
      "availableResources": {
        "cpuCores": '"$AVAILABLE_CPU"',
        "memoryMb": '"$AVAILABLE_MEM"',
        "storageGb": '"$AVAILABLE_STORAGE"',
        "bandwidthMbps": 1000
      },
      "activeVmIds": '"$vm_ids_json"'
    }')

  echo -n "$(date +%H:%M:%S) - Heartbeat sent: CPU ${cpu_usage}%, MEM ${mem_usage}%, Load ${load}"
  
  # Check for pending commands
  PENDING=$(echo "$HEARTBEAT_RESPONSE" | grep -o '"pendingCommands":\[[^]]*\]' || true)
  
  if [ -n "$PENDING" ] && [ "$PENDING" != '"pendingCommands":null' ]; then
    echo ""
    log_warn "Received pending commands!"
    
    # Parse commands (simplified)
    COMMANDS=$(echo "$HEARTBEAT_RESPONSE" | grep -o '"type":"[^"]*"' | cut -d'"' -f4)
    
    for cmd in $COMMANDS; do
      case $cmd in
        "CreateVm")
          VM_ID=$(echo "$HEARTBEAT_RESPONSE" | grep -o '"vmId":"[^"]*"' | head -1 | cut -d'"' -f4)
          log_info "Command: Create VM $VM_ID"
          ACTIVE_VMS+=("$VM_ID")
          AVAILABLE_CPU=$((AVAILABLE_CPU - 2))
          AVAILABLE_MEM=$((AVAILABLE_MEM - 4096))
          log_success "VM $VM_ID 'created' (simulated)"
          ;;
        "StopVm"|"DeleteVm")
          log_info "Command: $cmd"
          # In real agent, would stop/delete the VM
          ;;
        *)
          log_info "Command: $cmd (not handled in mock)"
          ;;
      esac
    done
  else
    echo " - No commands"
  fi
}

# Trap Ctrl+C
trap 'echo ""; log_info "Shutting down..."; exit 0' INT

# Initial heartbeat
send_heartbeat

# Heartbeat loop
while true; do
  sleep $HEARTBEAT_INTERVAL
  send_heartbeat
done
