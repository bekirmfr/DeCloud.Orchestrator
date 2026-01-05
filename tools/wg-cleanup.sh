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

# Get peer information using wg show dump for reliable parsing
PEERS_TO_REMOVE=()

# Parse wg show dump output (tab-separated format)
# Format: public_key preshared_key endpoint allowed_ips latest_handshake rx tx persistent_keepalive
while IFS=$'\t' read -r pubkey psk endpoint allowed_ips handshake rx tx keepalive; do
    # Skip the interface line (first line)
    if [[ "$pubkey" == "$INTERFACE" ]] || [[ -z "$pubkey" ]]; then
        continue
    fi
    
    # Check if peer has no allowed IPs
    if [[ -z "$allowed_ips" ]] || [[ "$allowed_ips" == "(none)" ]]; then
        PEERS_TO_REMOVE+=("$pubkey|no_allowed_ips")
        continue
    fi
    
    # Check if peer is stale (handshake timestamp is in seconds since epoch)
    if [[ -n "$handshake" ]] && [[ "$handshake" != "0" ]]; then
        CURRENT_TIME=$(date +%s)
        TIME_DIFF=$((CURRENT_TIME - handshake))
        HOURS_DIFF=$((TIME_DIFF / 3600))
        
        if [ "$HOURS_DIFF" -ge "$STALE_THRESHOLD_HOURS" ]; then
            DAYS=$((HOURS_DIFF / 24))
            PEERS_TO_REMOVE+=("$pubkey|stale_${DAYS}days")
        fi
    fi
done < <(wg show "$INTERFACE" dump 2>/dev/null | tail -n +2)

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