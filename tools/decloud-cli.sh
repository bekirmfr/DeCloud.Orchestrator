#!/bin/bash
#
# DeCloud CLI - Simple command-line tool for managing VMs
# Usage: decloud <command> [options]
#

set -e

# Configuration
ORCHESTRATOR_URL="${DECLOUD_ORCHESTRATOR_URL:-http://localhost:5050}"
WALLET="${DECLOUD_WALLET:-0x1234567890abcdef1234567890abcdef12345678}"
CONFIG_FILE="$HOME/.decloud/config"
TOKEN_FILE="$HOME/.decloud/token"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

# Load config if exists
if [ -f "$CONFIG_FILE" ]; then
    source "$CONFIG_FILE"
fi

# Ensure config directory exists
mkdir -p "$HOME/.decloud"

# ============================================================
# Helper Functions
# ============================================================

get_token() {
    # Check if we have a cached token
    if [ -f "$TOKEN_FILE" ]; then
        local token_age=$(($(date +%s) - $(stat -c %Y "$TOKEN_FILE" 2>/dev/null || echo 0)))
        if [ $token_age -lt 82800 ]; then  # 23 hours
            cat "$TOKEN_FILE"
            return
        fi
    fi
    
    # Get new token
    local response=$(curl -s -X POST "$ORCHESTRATOR_URL/api/auth/wallet" \
        -H "Content-Type: application/json" \
        -d "{\"walletAddress\":\"$WALLET\",\"signature\":\"0xmocksig\",\"message\":\"Sign\",\"timestamp\":$(date +%s)}")
    
    local token=$(echo "$response" | grep -o '"accessToken":"[^"]*"' | cut -d'"' -f4)
    
    if [ -n "$token" ]; then
        echo "$token" > "$TOKEN_FILE"
        echo "$token"
    else
        echo -e "${RED}Error: Failed to authenticate${NC}" >&2
        echo "$response" >&2
        exit 1
    fi
}

api_get() {
    local endpoint="$1"
    local token=$(get_token)
    curl -s -H "Authorization: Bearer $token" "$ORCHESTRATOR_URL$endpoint"
}

api_post() {
    local endpoint="$1"
    local data="$2"
    local token=$(get_token)
    curl -s -X POST -H "Authorization: Bearer $token" -H "Content-Type: application/json" \
        -d "$data" "$ORCHESTRATOR_URL$endpoint"
}

api_delete() {
    local endpoint="$1"
    local token=$(get_token)
    curl -s -X DELETE -H "Authorization: Bearer $token" "$ORCHESTRATOR_URL$endpoint"
}

print_header() {
    echo -e "${CYAN}$1${NC}"
    echo "────────────────────────────────────────"
}

# ============================================================
# Commands
# ============================================================

cmd_configure() {
    echo -e "${BLUE}DeCloud CLI Configuration${NC}"
    echo ""
    
    read -p "Orchestrator URL [$ORCHESTRATOR_URL]: " input_url
    ORCHESTRATOR_URL="${input_url:-$ORCHESTRATOR_URL}"
    
    read -p "Wallet Address [$WALLET]: " input_wallet
    WALLET="${input_wallet:-$WALLET}"
    
    # Save config
    cat > "$CONFIG_FILE" << EOF
ORCHESTRATOR_URL="$ORCHESTRATOR_URL"
WALLET="$WALLET"
EOF
    
    # Clear cached token
    rm -f "$TOKEN_FILE"
    
    echo ""
    echo -e "${GREEN}Configuration saved to $CONFIG_FILE${NC}"
}

cmd_status() {
    print_header "System Status"
    
    local stats=$(curl -s "$ORCHESTRATOR_URL/api/system/stats")
    
    if echo "$stats" | grep -q '"success":true'; then
        local data=$(echo "$stats" | grep -o '"data":{[^}]*}' | sed 's/"data"://')
        
        echo -e "Nodes:     $(echo "$stats" | grep -o '"onlineNodes":[0-9]*' | cut -d: -f2) online / $(echo "$stats" | grep -o '"totalNodes":[0-9]*' | cut -d: -f2) total"
        echo -e "VMs:       $(echo "$stats" | grep -o '"runningVms":[0-9]*' | cut -d: -f2) running / $(echo "$stats" | grep -o '"totalVms":[0-9]*' | cut -d: -f2) total"
        echo -e "CPU:       $(echo "$stats" | grep -o '"availableCpuCores":[0-9]*' | cut -d: -f2) / $(echo "$stats" | grep -o '"totalCpuCores":[0-9]*' | cut -d: -f2) cores available"
        echo -e "Memory:    $(echo "$stats" | grep -o '"availableMemoryMb":[0-9]*' | cut -d: -f2) / $(echo "$stats" | grep -o '"totalMemoryMb":[0-9]*' | cut -d: -f2) MB available"
    else
        echo -e "${RED}Failed to get system status${NC}"
        echo "$stats"
    fi
}

cmd_nodes() {
    print_header "Nodes"
    
    local nodes=$(api_get "/api/nodes")
    
    if echo "$nodes" | grep -q '"success":true'; then
        echo "$nodes" | jq -r '.data[] | "\(.id[0:8])...  \(.name)\t\(.status)\t\(.availableResources.cpuCores) CPU\t\(.availableResources.memoryMb)MB"' 2>/dev/null || \
        echo "$nodes" | grep -o '"name":"[^"]*"' | cut -d'"' -f4
    else
        echo -e "${YELLOW}No nodes registered${NC}"
    fi
}

cmd_vms() {
    print_header "Virtual Machines"
    
    local vms=$(api_get "/api/vms")
    
    if echo "$vms" | grep -q '"totalCount":0'; then
        echo -e "${YELLOW}No VMs found${NC}"
        return
    fi
    
    # Parse and display VMs
    echo "$vms" | jq -r '.data.items[] | "\(.id[0:8])...\t\(.name)\t\(.status)\t\(.spec.cpuCores) CPU\t\(.spec.memoryMb)MB"' 2>/dev/null || \
    echo "$vms" | grep -o '"name":"[^"]*"' | while read line; do
        echo "  - $(echo $line | cut -d'"' -f4)"
    done
}

cmd_create() {
    local name="$1"
    local image="${2:-ubuntu-22.04}"
    local cpu="${3:-1}"
    local memory="${4:-1024}"
    local disk="${5:-10}"
    
    if [ -z "$name" ]; then
        echo "Usage: decloud create <name> [image] [cpu] [memory_mb] [disk_gb]"
        echo ""
        echo "Examples:"
        echo "  decloud create my-vm"
        echo "  decloud create my-vm ubuntu-24.04"
        echo "  decloud create my-vm ubuntu-22.04 2 4096 40"
        echo ""
        echo "Available images: ubuntu-24.04, ubuntu-22.04, debian-12, fedora-40, alpine-3.19"
        return 1
    fi
    
    print_header "Creating VM: $name"
    echo "  Image:   $image"
    echo "  CPU:     $cpu cores"
    echo "  Memory:  $memory MB"
    echo "  Disk:    $disk GB"
    echo ""
    
    local response=$(api_post "/api/vms" "{
        \"name\": \"$name\",
        \"spec\": {
            \"cpuCores\": $cpu,
            \"memoryMb\": $memory,
            \"diskGb\": $disk,
            \"imageId\": \"$image\",
            \"requiresGpu\": false
        }
    }")
    
    if echo "$response" | grep -q '"success":true'; then
        local vmId=$(echo "$response" | grep -o '"vmId":"[^"]*"' | cut -d'"' -f4)
        echo -e "${GREEN}✓ VM created successfully${NC}"
        echo "  VM ID: $vmId"
        echo ""
        echo "The VM is being provisioned. Check status with:"
        echo "  decloud vm $vmId"
    else
        echo -e "${RED}✗ Failed to create VM${NC}"
        echo "$response" | jq . 2>/dev/null || echo "$response"
    fi
}

cmd_vm() {
    local vmId="$1"
    
    if [ -z "$vmId" ]; then
        echo "Usage: decloud vm <vm-id>"
        return 1
    fi
    
    local vm=$(api_get "/api/vms/$vmId")
    
    if echo "$vm" | grep -q '"success":true'; then
        echo "$vm" | jq '.data.vm | {id, name, status, powerState, nodeId, spec: {cpu: .spec.cpuCores, memory: .spec.memoryMb, disk: .spec.diskGb, image: .spec.imageId}, network: .networkConfig}' 2>/dev/null || echo "$vm"
    else
        echo -e "${RED}VM not found${NC}"
    fi
}

cmd_start() {
    local vmId="$1"
    if [ -z "$vmId" ]; then echo "Usage: decloud start <vm-id>"; return 1; fi
    
    local response=$(api_post "/api/vms/$vmId/action" '{"action":"Start"}')
    if echo "$response" | grep -q '"success":true'; then
        echo -e "${GREEN}✓ VM start command sent${NC}"
    else
        echo -e "${RED}✗ Failed to start VM${NC}"
        echo "$response"
    fi
}

cmd_stop() {
    local vmId="$1"
    if [ -z "$vmId" ]; then echo "Usage: decloud stop <vm-id>"; return 1; fi
    
    local response=$(api_post "/api/vms/$vmId/action" '{"action":"Stop"}')
    if echo "$response" | grep -q '"success":true'; then
        echo -e "${GREEN}✓ VM stop command sent${NC}"
    else
        echo -e "${RED}✗ Failed to stop VM${NC}"
        echo "$response"
    fi
}

cmd_delete() {
    local vmId="$1"
    if [ -z "$vmId" ]; then echo "Usage: decloud delete <vm-id>"; return 1; fi
    
    read -p "Are you sure you want to delete VM $vmId? [y/N] " confirm
    if [ "$confirm" != "y" ] && [ "$confirm" != "Y" ]; then
        echo "Cancelled"
        return
    fi
    
    local response=$(api_delete "/api/vms/$vmId")
    if echo "$response" | grep -q '"success":true'; then
        echo -e "${GREEN}✓ VM deleted${NC}"
    else
        echo -e "${RED}✗ Failed to delete VM${NC}"
        echo "$response"
    fi
}

cmd_ssh_key() {
    local action="$1"
    local name="$2"
    local key="$3"
    
    case "$action" in
        list)
            print_header "SSH Keys"
            local keys=$(api_get "/api/user/me/ssh-keys")
            echo "$keys" | jq -r '.data[] | "  \(.name)\t\(.fingerprint[0:16])...\t\(.createdAt[0:10])"' 2>/dev/null || echo "$keys"
            ;;
        add)
            if [ -z "$name" ]; then
                echo "Usage: decloud ssh-key add <name> [key-file]"
                echo ""
                echo "Examples:"
                echo "  decloud ssh-key add my-key ~/.ssh/id_rsa.pub"
                echo "  cat ~/.ssh/id_rsa.pub | decloud ssh-key add my-key -"
                return 1
            fi
            
            local pubkey
            if [ "$key" = "-" ]; then
                pubkey=$(cat)
            elif [ -n "$key" ] && [ -f "$key" ]; then
                pubkey=$(cat "$key")
            elif [ -f "$HOME/.ssh/id_rsa.pub" ]; then
                pubkey=$(cat "$HOME/.ssh/id_rsa.pub")
            else
                echo -e "${RED}No SSH key provided and ~/.ssh/id_rsa.pub not found${NC}"
                return 1
            fi
            
            local response=$(api_post "/api/user/me/ssh-keys" "{\"name\":\"$name\",\"publicKey\":\"$pubkey\"}")
            if echo "$response" | grep -q '"success":true'; then
                echo -e "${GREEN}✓ SSH key added${NC}"
            else
                echo -e "${RED}✗ Failed to add SSH key${NC}"
                echo "$response"
            fi
            ;;
        *)
            echo "Usage: decloud ssh-key <list|add> [options]"
            ;;
    esac
}

cmd_images() {
    print_header "Available Images"
    
    local images=$(curl -s "$ORCHESTRATOR_URL/api/system/images")
    echo "$images" | jq -r '.data[] | "  \(.id)\t\(.name)"' 2>/dev/null || echo "$images"
}

cmd_help() {
    echo "DeCloud CLI - Decentralized Cloud Management"
    echo ""
    echo -e "${CYAN}Usage:${NC} decloud <command> [options]"
    echo ""
    echo -e "${CYAN}Commands:${NC}"
    echo "  configure          Configure CLI (orchestrator URL, wallet)"
    echo "  status             Show system status"
    echo "  nodes              List all nodes"
    echo "  images             List available VM images"
    echo ""
    echo "  vms                List your VMs"
    echo "  create <name>      Create a new VM"
    echo "  vm <id>            Show VM details"
    echo "  start <id>         Start a VM"
    echo "  stop <id>          Stop a VM"
    echo "  delete <id>        Delete a VM"
    echo ""
    echo "  ssh-key list       List your SSH keys"
    echo "  ssh-key add <name> Add an SSH key"
    echo ""
    echo -e "${CYAN}Environment Variables:${NC}"
    echo "  DECLOUD_ORCHESTRATOR_URL   Orchestrator URL"
    echo "  DECLOUD_WALLET             Your wallet address"
    echo ""
    echo -e "${CYAN}Examples:${NC}"
    echo "  decloud configure"
    echo "  decloud ssh-key add my-laptop ~/.ssh/id_rsa.pub"
    echo "  decloud create my-server ubuntu-24.04 2 4096 40"
    echo "  decloud vms"
    echo ""
}

# ============================================================
# Main
# ============================================================

case "${1:-help}" in
    configure)  cmd_configure ;;
    status)     cmd_status ;;
    nodes)      cmd_nodes ;;
    images)     cmd_images ;;
    vms)        cmd_vms ;;
    create)     cmd_create "$2" "$3" "$4" "$5" "$6" ;;
    vm)         cmd_vm "$2" ;;
    start)      cmd_start "$2" ;;
    stop)       cmd_stop "$2" ;;
    delete)     cmd_delete "$2" ;;
    ssh-key)    cmd_ssh_key "$2" "$3" "$4" ;;
    help|--help|-h)  cmd_help ;;
    *)
        echo -e "${RED}Unknown command: $1${NC}"
        echo "Run 'decloud help' for usage"
        exit 1
        ;;
esac
