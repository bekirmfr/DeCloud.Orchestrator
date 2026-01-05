#!/bin/bash
# WireGuard Peer Cleanup Script
# Removes orphaned peers (no allowed IPs or stale connections)
# Security-first, production-grade approach

set -euo pipefail

# Configuration
INTERFACE="${1:-wg-relay-client}"
DRY_RUN="${2:-false}"
STALE_THRESHOLD_HOURS=48

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    log_error "This script must be run as root"
    exit 1
fi

# Check if WireGuard is installed
if ! command -v wg &> /dev/null; then
    log_error "WireGuard is not installed"
    exit 1
fi

# Check if interface exists
if ! wg show "$INTERFACE" &> /dev/null; then
    log_error "Interface '$INTERFACE' does not exist"
    exit 1
fi

log_info "Analyzing WireGuard interface: $INTERFACE"
echo ""

# Get peer information
PEERS_TO_REMOVE=()
PEERS_TO_KEEP=()

while IFS= read -r line; do
    if [[ $line =~ ^peer:\ (.+)$ ]]; then
        CURRENT_PEER="${BASH_REMATCH[1]}"
        HAS_ALLOWED_IPS=false
        IS_STALE=false
    elif [[ $line =~ allowed\ ips:\ \(none\) ]]; then
        # Peer has no allowed IPs - mark for removal
        HAS_ALLOWED_IPS=false
        PEERS_TO_REMOVE+=("$CURRENT_PEER|no_allowed_ips")
    elif [[ $line =~ allowed\ ips:\ (.+)$ ]]; then
        HAS_ALLOWED_IPS=true
    elif [[ $line =~ latest\ handshake:\ ([0-9]+)\ days ]]; then
        DAYS="${BASH_REMATCH[1]}"
        if [ "$DAYS" -ge 2 ]; then
            IS_STALE=true
            if [ "$HAS_ALLOWED_IPS" = false ]; then
                # Already marked for removal
                :
            else
                PEERS_TO_REMOVE+=("$CURRENT_PEER|stale_${DAYS}days")
            fi
        fi
    fi
done < <(wg show "$INTERFACE")

# Display analysis
log_info "Found ${#PEERS_TO_REMOVE[@]} peers to remove"
echo ""

if [ ${#PEERS_TO_REMOVE[@]} -eq 0 ]; then
    log_info "No cleanup needed. All peers are valid."
    exit 0
fi

# Show peers to be removed
for entry in "${PEERS_TO_REMOVE[@]}"; do
    IFS='|' read -r peer reason <<< "$entry"
    SHORT_PEER="${peer:0:16}...${peer: -8}"
    log_warn "Remove: $SHORT_PEER (Reason: $reason)"
done
echo ""

# Dry run check
if [ "$DRY_RUN" = "true" ]; then
    log_info "DRY RUN MODE - No changes will be made"
    exit 0
fi

# Confirm action
read -p "Proceed with cleanup? (yes/no): " CONFIRM
if [ "$CONFIRM" != "yes" ]; then
    log_info "Cleanup cancelled"
    exit 0
fi

# Perform cleanup
log_info "Starting cleanup..."
echo ""

REMOVED_COUNT=0
FAILED_COUNT=0

for entry in "${PEERS_TO_REMOVE[@]}"; do
    IFS='|' read -r peer reason <<< "$entry"
    SHORT_PEER="${peer:0:16}...${peer: -8}"
    
    if wg set "$INTERFACE" peer "$peer" remove 2>/dev/null; then
        log_info "✓ Removed peer: $SHORT_PEER"
        ((REMOVED_COUNT++))
    else
        log_error "✗ Failed to remove peer: $SHORT_PEER"
        ((FAILED_COUNT++))
    fi
done

# Save configuration
if [ $REMOVED_COUNT -gt 0 ]; then
    log_info "Saving WireGuard configuration..."
    if wg-quick save "$INTERFACE" 2>/dev/null; then
        log_info "✓ Configuration saved successfully"
    else
        log_warn "Failed to save configuration (interface may be managed differently)"
    fi
fi

# Summary
echo ""
log_info "═══════════════════════════════════════"
log_info "Cleanup Summary"
log_info "═══════════════════════════════════════"
echo "  Peers removed:  $REMOVED_COUNT"
echo "  Failed:         $FAILED_COUNT"
log_info "═══════════════════════════════════════"

# Show final status
echo ""
log_info "Current interface status:"
wg show "$INTERFACE"

exit 0
