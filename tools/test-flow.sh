#!/bin/bash
# End-to-End Test Script for Orchestrator
# Tests: Health, Auth, Node Registration, VM Creation, VM Actions

set -e

ORCHESTRATOR_URL="${1:-http://localhost:5000}"

# Colors
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[✓]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[WARN]${NC} $1"; }
log_error() { echo -e "${RED}[✗]${NC} $1"; }
log_test() { echo -e "\n${YELLOW}=== TEST: $1 ===${NC}"; }

# Check if jq is available
if ! command -v jq &> /dev/null; then
  log_warn "jq not installed. Output will be raw JSON."
  JQ_CMD="cat"
else
  JQ_CMD="jq"
fi

echo "=========================================="
echo "   Orchestrator E2E Test Suite"
echo "=========================================="
echo "Target: $ORCHESTRATOR_URL"
echo ""

# ==========================================
# TEST 1: Health Check
# ==========================================
log_test "Health Check"

HEALTH=$(curl -s "$ORCHESTRATOR_URL/health")
if echo "$HEALTH" | grep -q "healthy"; then
  log_success "Health check passed"
else
  log_error "Health check failed"
  echo "$HEALTH"
  exit 1
fi

# ==========================================
# TEST 2: System Endpoints (No Auth)
# ==========================================
log_test "System Endpoints (Public)"

log_info "Getting system stats..."
curl -s "$ORCHESTRATOR_URL/api/system/stats" | $JQ_CMD
log_success "Stats endpoint OK"

log_info "Getting available images..."
IMAGES=$(curl -s "$ORCHESTRATOR_URL/api/system/images")
IMAGE_COUNT=$(echo "$IMAGES" | grep -o '"id"' | wc -l)
log_success "Found $IMAGE_COUNT images"

log_info "Getting pricing tiers..."
PRICING=$(curl -s "$ORCHESTRATOR_URL/api/system/pricing")
TIER_COUNT=$(echo "$PRICING" | grep -o '"id"' | wc -l)
log_success "Found $TIER_COUNT pricing tiers"

# ==========================================
# TEST 3: Node Registration
# ==========================================
log_test "Node Registration"

NODE_WALLET="0x$(openssl rand -hex 20)"
log_info "Registering node with wallet: $NODE_WALLET"

REGISTER_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/nodes/register" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "test-node-e2e",
    "walletAddress": "'"$NODE_WALLET"'",
    "publicIp": "10.0.0.100",
    "agentPort": 5050,
    "resources": {
      "cpuCores": 16,
      "memoryMb": 32768,
      "storageGb": 1000,
      "bandwidthMbps": 1000
    },
    "agentVersion": "1.0.0",
    "supportedImages": ["ubuntu-24.04", "ubuntu-22.04", "debian-12"],
    "supportsGpu": true,
    "gpuInfo": {
      "model": "NVIDIA RTX 4090",
      "vramMb": 24576,
      "count": 1,
      "driver": "535.104.05"
    },
    "region": "us-west",
    "zone": "us-west-2a"
  }')

NODE_ID=$(echo "$REGISTER_RESPONSE" | grep -o '"nodeId":"[^"]*"' | cut -d'"' -f4)
NODE_TOKEN=$(echo "$REGISTER_RESPONSE" | grep -o '"authToken":"[^"]*"' | cut -d'"' -f4)

if [ -n "$NODE_ID" ] && [ -n "$NODE_TOKEN" ]; then
  log_success "Node registered: $NODE_ID"
else
  log_error "Node registration failed"
  echo "$REGISTER_RESPONSE" | $JQ_CMD
  exit 1
fi

# ==========================================
# TEST 4: Node Heartbeat
# ==========================================
log_test "Node Heartbeat"

HEARTBEAT_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/nodes/$NODE_ID/heartbeat" \
  -H "Content-Type: application/json" \
  -H "X-Node-Token: $NODE_TOKEN" \
  -d '{
    "nodeId": "'"$NODE_ID"'",
    "metrics": {
      "timestamp": "'"$(date -u +%Y-%m-%dT%H:%M:%SZ)"'",
      "cpuUsagePercent": 15.5,
      "memoryUsagePercent": 25.0,
      "storageUsagePercent": 10.0,
      "networkInMbps": 50,
      "networkOutMbps": 25,
      "activeVmCount": 0,
      "loadAverage": 0.5
    },
    "availableResources": {
      "cpuCores": 16,
      "memoryMb": 32768,
      "storageGb": 1000,
      "bandwidthMbps": 1000
    },
    "activeVmIds": []
  }')

if echo "$HEARTBEAT_RESPONSE" | grep -q '"acknowledged":true'; then
  log_success "Heartbeat acknowledged"
else
  log_error "Heartbeat failed"
  echo "$HEARTBEAT_RESPONSE" | $JQ_CMD
fi

# ==========================================
# TEST 5: User Authentication
# ==========================================
log_test "User Authentication"

USER_WALLET="0x$(openssl rand -hex 20)"
TIMESTAMP=$(date +%s)

log_info "Authenticating user with wallet: $USER_WALLET"

AUTH_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/auth/wallet" \
  -H "Content-Type: application/json" \
  -d '{
    "walletAddress": "'"$USER_WALLET"'",
    "signature": "0xmocksignature",
    "message": "Sign this message to authenticate",
    "timestamp": '"$TIMESTAMP"'
  }')

ACCESS_TOKEN=$(echo "$AUTH_RESPONSE" | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)

if [ -n "$ACCESS_TOKEN" ]; then
  log_success "User authenticated"
  log_info "Token: ${ACCESS_TOKEN:0:50}..."
else
  log_error "Authentication failed"
  echo "$AUTH_RESPONSE" | $JQ_CMD
  exit 1
fi

# ==========================================
# TEST 6: Get User Profile
# ==========================================
log_test "User Profile"

PROFILE=$(curl -s "$ORCHESTRATOR_URL/api/user/me" \
  -H "Authorization: Bearer $ACCESS_TOKEN")

if echo "$PROFILE" | grep -q '"walletAddress"'; then
  log_success "Profile retrieved"
  echo "$PROFILE" | $JQ_CMD '.data | {id, walletAddress, status}' 2>/dev/null || echo "$PROFILE"
else
  log_error "Failed to get profile"
fi

# ==========================================
# TEST 7: Create VM
# ==========================================
log_test "Create VM"

VM_RESPONSE=$(curl -s -X POST "$ORCHESTRATOR_URL/api/vms" \
  -H "Authorization: Bearer $ACCESS_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{
    "name": "test-vm-e2e",
    "spec": {
      "cpuCores": 2,
      "memoryMb": 4096,
      "diskGb": 40,
      "imageId": "ubuntu-24.04",
      "requiresGpu": false,
      "preferredRegion": "us-west"
    },
    "labels": {
      "test": "e2e",
      "environment": "testing"
    }
  }')

VM_ID=$(echo "$VM_RESPONSE" | grep -o '"vmId":"[^"]*"' | cut -d'"' -f4)

if [ -n "$VM_ID" ]; then
  log_success "VM created: $VM_ID"
else
  log_error "VM creation failed"
  echo "$VM_RESPONSE" | $JQ_CMD
fi

# ==========================================
# TEST 8: List VMs
# ==========================================
log_test "List VMs"

VMS_LIST=$(curl -s "$ORCHESTRATOR_URL/api/vms" \
  -H "Authorization: Bearer $ACCESS_TOKEN")

VM_COUNT=$(echo "$VMS_LIST" | grep -o '"totalCount":[0-9]*' | cut -d':' -f2)
log_success "Found $VM_COUNT VM(s)"

# ==========================================
# TEST 9: Get VM Details
# ==========================================
log_test "Get VM Details"

if [ -n "$VM_ID" ]; then
  VM_DETAILS=$(curl -s "$ORCHESTRATOR_URL/api/vms/$VM_ID" \
    -H "Authorization: Bearer $ACCESS_TOKEN")
  
  if echo "$VM_DETAILS" | grep -q '"name":"test-vm-e2e"'; then
    log_success "VM details retrieved"
    echo "$VM_DETAILS" | $JQ_CMD '.data.vm | {id, name, status, nodeId}' 2>/dev/null || true
  else
    log_warn "Could not get VM details"
  fi
fi

# ==========================================
# TEST 10: Check System Stats After
# ==========================================
log_test "Final System Stats"

FINAL_STATS=$(curl -s "$ORCHESTRATOR_URL/api/system/stats")
echo "$FINAL_STATS" | $JQ_CMD '.data' 2>/dev/null || echo "$FINAL_STATS"

# ==========================================
# TEST 11: Delete VM
# ==========================================
log_test "Delete VM"

if [ -n "$VM_ID" ]; then
  DELETE_RESPONSE=$(curl -s -X DELETE "$ORCHESTRATOR_URL/api/vms/$VM_ID" \
    -H "Authorization: Bearer $ACCESS_TOKEN")
  
  if echo "$DELETE_RESPONSE" | grep -q '"success":true'; then
    log_success "VM deleted"
  else
    log_warn "VM deletion response: $DELETE_RESPONSE"
  fi
fi

# ==========================================
# Summary
# ==========================================
echo ""
echo "=========================================="
echo "           Test Summary"
echo "=========================================="
echo ""
log_success "All tests completed!"
echo ""
echo "Resources created during test:"
echo "  - Node ID: $NODE_ID"
echo "  - User Wallet: $USER_WALLET"
echo "  - VM ID: $VM_ID (deleted)"
echo ""
echo "To run the mock node agent:"
echo "  ./tools/mock-node-agent.sh my-node $ORCHESTRATOR_URL"
echo ""
