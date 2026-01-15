#!/bin/bash
# WireGuard Peer Cleanup Script (FIXED)
# Removes orphaned peers (no allowed IPs or stale connections)
# Security-first, production-grade approach
# 
# KEY FIX: Directly modifies config file instead of using unreliable wg-quick save

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
CONFIG_FILE="/etc/wireguard/${INTERFACE}.conf"

# Check if config file exists
if [ -f "$CONFIG_FILE" ]; then
    log_info "Using configuration file method (reliable persistence)"
    
    # Create backup
    BACKUP_FILE="${CONFIG_FILE}.backup.$(date +%Y%m%d-%H%M%S)"
    cp "$CONFIG_FILE" "$BACKUP_FILE"
    log_info "Backup created: $BACKUP_FILE"
    
    # Create temp file for new config
    TEMP_FILE=$(mktemp)
    
    # Read current config and filter out peers to remove
    IN_PEER_SECTION=false
    SKIP_CURRENT_PEER=false
    CURRENT_PEER_PUBKEY=""
    
    while IFS= read -r line; do
        # Detect [Peer] section start
        if [[ "$line" =~ ^\[Peer\] ]]; then
            IN_PEER_SECTION=true
            SKIP_CURRENT_PEER=false
            CURRENT_PEER_PUBKEY=""
            # Hold the [Peer] line - we'll decide whether to write it after seeing PublicKey
            PEER_SECTION_START="$line"
            continue
        fi
        
        # Detect [Interface] or other sections
        if [[ "$line" =~ ^\[.*\] ]] && [[ ! "$line" =~ ^\[Peer\] ]]; then
            IN_PEER_SECTION=false
            SKIP_CURRENT_PEER=false
            echo "$line" >> "$TEMP_FILE"
            continue
        fi
        
        # Handle lines within [Peer] section
        if [ "$IN_PEER_SECTION" = true ]; then
            # Check if this line contains PublicKey
            if [[ "$line" =~ ^PublicKey[[:space:]]*=[[:space:]]*(.*) ]]; then
                CURRENT_PEER_PUBKEY="${BASH_REMATCH[1]}"
                CURRENT_PEER_PUBKEY=$(echo "$CURRENT_PEER_PUBKEY" | xargs) # trim whitespace
                
                # Check if this peer should be removed
                for entry in "${PEERS_TO_REMOVE[@]}"; do
                    IFS='|' read -r peer_to_remove reason <<< "$entry"
                    if [ "$CURRENT_PEER_PUBKEY" = "$peer_to_remove" ]; then
                        SKIP_CURRENT_PEER=true
                        SHORT_PEER="${peer_to_remove:0:16}...${peer_to_remove: -8}"
                        log_info "✓ Removing peer from config: $SHORT_PEER (Reason: $reason)"
                        ((REMOVED_COUNT++))
                        break
                    fi
                done
                
                # Now decide whether to write the [Peer] section and this line
                if [ "$SKIP_CURRENT_PEER" = false ]; then
                    echo "$PEER_SECTION_START" >> "$TEMP_FILE"
                    echo "$line" >> "$TEMP_FILE"
                fi
                continue
            fi
            
            # Other lines in peer section - only write if not skipping
            if [ "$SKIP_CURRENT_PEER" = false ]; then
                echo "$line" >> "$TEMP_FILE"
            fi
        else
            # Lines outside peer sections - always write
            echo "$line" >> "$TEMP_FILE"
        fi
    done < "$CONFIG_FILE"
    
    # Replace config file
    mv "$TEMP_FILE" "$CONFIG_FILE"
    chmod 600 "$CONFIG_FILE"
    
    # Reload configuration using syncconf (atomic, no disruption to existing connections)
    log_info "Reloading WireGuard configuration..."
    
    # Try syncconf first (best method - no downtime)
    if wg syncconf "$INTERFACE" <(wg-quick strip "$INTERFACE") 2>/dev/null; then
        log_info "✓ Configuration reloaded successfully (syncconf)"
    else
        # Fallback: restart interface via systemd
        log_warn "syncconf failed, attempting interface restart..."
        
        if systemctl is-active --quiet "wg-quick@${INTERFACE}"; then
            systemctl restart "wg-quick@${INTERFACE}" 2>/dev/null
            if [ $? -eq 0 ]; then
                log_info "✓ Interface restarted successfully (systemctl)"
            else
                log_error "Failed to restart via systemctl"
                
                # Last resort: manual wg-quick
                log_warn "Attempting manual wg-quick restart..."
                wg-quick down "$INTERFACE" 2>/dev/null
                wg-quick up "$INTERFACE" 2>/dev/null
                
                if [ $? -eq 0 ]; then
                    log_info "✓ Interface restarted successfully (wg-quick)"
                else
                    log_error "Failed to reload configuration"
                    log_error "Manual intervention required:"
                    log_error "  1. Check interface status: systemctl status wg-quick@${INTERFACE}"
                    log_error "  2. Check config file: cat ${CONFIG_FILE}"
                    log_error "  3. Restore backup if needed: cp ${BACKUP_FILE} ${CONFIG_FILE}"
                    exit 1
                fi
            fi
        else
            # Interface not managed by systemd
            wg-quick down "$INTERFACE" 2>/dev/null
            wg-quick up "$INTERFACE" 2>/dev/null
            
            if [ $? -eq 0 ]; then
                log_info "✓ Interface restarted successfully (wg-quick)"
            else
                log_error "Failed to restart interface"
                exit 1
            fi
        fi
    fi
    
    # Clean up old backups (keep last 5)
    BACKUP_DIR=$(dirname "$CONFIG_FILE")
    ls -t "${BACKUP_DIR}/${INTERFACE}.conf.backup."* 2>/dev/null | tail -n +6 | xargs -r rm
    
else
    # Fallback: Runtime-only removal (NOT RECOMMENDED - changes not persistent)
    log_warn "Config file not found at $CONFIG_FILE"
    log_warn "Using runtime removal method - CHANGES WILL NOT PERSIST!"
    log_warn "Peers will return after interface restart"
    echo ""
    
    for entry in "${PEERS_TO_REMOVE[@]}"; do
        IFS='|' read -r peer reason <<< "$entry"
        SHORT_PEER="${peer:0:16}...${peer: -8}"
        
        if wg set "$INTERFACE" peer "$peer" remove 2>/dev/null; then
            log_info "✓ Removed peer from runtime: $SHORT_PEER"
            ((REMOVED_COUNT++))
        else
            log_error "✗ Failed to remove peer: $SHORT_PEER"
            ((FAILED_COUNT++))
        fi
    done
    
    echo ""
    log_warn "⚠️  IMPORTANT: Changes are NOT persistent"
    log_warn "To make changes permanent:"
    log_warn "  1. Create config file: $CONFIG_FILE"
    log_warn "  2. Run this script again"
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