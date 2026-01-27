#!/bin/bash
# WireGuard Peer Cleanup Script (RELAY-SAFE VERSION - NO EXTERNAL DEPENDENCIES)
# Removes orphaned peers while PROTECTING active relay peers
# Identifies relay peers by their AllowedIPs pattern (10.20.X.0/24 subnets)
#
# USAGE:
#   sudo ./wg-cleanup.sh [interface]
#
#   interface: WireGuard interface name (default: wg-relay-client)
#
# BEHAVIOR:
#   1. Analyzes all peers
#   2. Shows what would be removed
#   3. Prompts for confirmation (type 'REMOVE' to proceed)
#   4. Creates backup and removes peers only if confirmed
#
# EXAMPLE:
#   sudo ./wg-cleanup.sh wg-relay-client
#
# KEY FEATURES:
# - No MongoDB dependency - uses WireGuard data only
# - Protects relay peers identified by subnet pattern
# - Conservative cleanup - requires BOTH no allowed IPs AND stale handshake
# - Requires typing "REMOVE" to confirm destructive action
# - Always creates backup before making changes

set -euo pipefail

# Configuration
INTERFACE="${1:-wg-relay-client}"
STALE_THRESHOLD_HOURS=48  # 2 days for non-relay peers
RELAY_STALE_THRESHOLD_HOURS=168  # 7 days for relay peers (more tolerant)

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
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

log_protected() {
    echo -e "${BLUE}[PROTECTED]${NC} $1"
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

log_info "═══════════════════════════════════════════════════════════"
log_info "WireGuard Peer Cleanup (Relay-Safe Mode)"
log_info "═══════════════════════════════════════════════════════════"
log_info "Interface: $INTERFACE"
log_info "Stale threshold (non-relay): $STALE_THRESHOLD_HOURS hours"
log_info "Stale threshold (relay): $RELAY_STALE_THRESHOLD_HOURS hours"
echo ""

# ==================== Analyze peers ====================
log_info "Analyzing WireGuard peers..."
echo ""

PEERS_TO_REMOVE=()
PROTECTED_COUNT=0
TOTAL_PEERS=0

while IFS=$'\t' read -r pubkey psk endpoint allowed_ips handshake rx tx keepalive; do
    # Skip the interface line
    if [[ "$pubkey" == "$INTERFACE" ]] || [[ -z "$pubkey" ]]; then
        continue
    fi
    
    ((TOTAL_PEERS++))
    SHORT_PEER="${pubkey:0:16}...${pubkey: -8}"
    
    # ========================================
    # PROTECTION 1: Identify relay peers by AllowedIPs pattern
    # ========================================
    # Relay peers have subnets: 10.20.X.0/24
    # CGNAT node peers have single IPs: 10.20.X.Y/32
    IS_RELAY_PEER=false
    
    if [[ "$allowed_ips" =~ 10\.20\.[0-9]+\.0/24 ]]; then
        IS_RELAY_PEER=true
        log_protected "Relay subnet detected: $SHORT_PEER (AllowedIPs: $allowed_ips)"
        ((PROTECTED_COUNT++))
        
        # Still check handshake age for monitoring purposes
        if [[ -n "$handshake" ]] && [[ "$handshake" != "0" ]]; then
            CURRENT_TIME=$(date +%s)
            TIME_DIFF=$((CURRENT_TIME - handshake))
            HOURS_DIFF=$((TIME_DIFF / 3600))
            DAYS=$((HOURS_DIFF / 24))
            
            if [ "$HOURS_DIFF" -ge "$RELAY_STALE_THRESHOLD_HOURS" ]; then
                log_warn "  Relay peer has old handshake ($DAYS days) but PROTECTED from removal"
                log_warn "  Manual investigation recommended - may indicate network issue"
            else
                log_protected "  Handshake age: $DAYS days (healthy)"
            fi
        elif [[ "$handshake" == "0" ]] || [[ -z "$handshake" ]]; then
            log_warn "  Relay peer has no handshake yet - may be initializing (PROTECTED)"
        fi
        
        continue
    fi
    
    # ========================================
    # PROTECTION 2: Be conservative with single-IP peers
    # ========================================
    # Single IP peers could be CGNAT nodes or other legitimate peers
    # Only remove if BOTH conditions are met:
    # 1. No allowed IPs OR empty allowed IPs
    # 2. Stale/no handshake
    
    HAS_NO_ALLOWED_IPS=false
    if [[ -z "$allowed_ips" ]] || [[ "$allowed_ips" == "(none)" ]]; then
        HAS_NO_ALLOWED_IPS=true
    fi
    
    IS_STALE=false
    if [[ -n "$handshake" ]] && [[ "$handshake" != "0" ]]; then
        CURRENT_TIME=$(date +%s)
        TIME_DIFF=$((CURRENT_TIME - handshake))
        HOURS_DIFF=$((TIME_DIFF / 3600))
        
        if [ "$HOURS_DIFF" -ge "$STALE_THRESHOLD_HOURS" ]; then
            IS_STALE=true
            DAYS=$((HOURS_DIFF / 24))
        fi
    elif [[ "$handshake" == "0" ]] || [[ -z "$handshake" ]]; then
        # Never had a handshake - consider stale after 1 hour
        IS_STALE=true
        DAYS="never"
    fi
    
    # ========================================
    # Decision: Remove only if meets removal criteria
    # ========================================
    
    if [ "$HAS_NO_ALLOWED_IPS" = true ]; then
        # Peer has no allowed IPs - definitely should be removed
        PEERS_TO_REMOVE+=("$pubkey|no_allowed_ips")
        log_warn "Orphan peer (no allowed IPs): $SHORT_PEER"
        continue
    fi
    
    if [ "$IS_STALE" = true ]; then
        # Peer has allowed IPs but stale handshake
        # This is likely a legitimate peer that's offline
        if [[ "$DAYS" == "never" ]]; then
            PEERS_TO_REMOVE+=("$pubkey|no_handshake")
            log_warn "Peer with no handshake: $SHORT_PEER (AllowedIPs: $allowed_ips)"
        else
            PEERS_TO_REMOVE+=("$pubkey|stale_${DAYS}days")
            log_warn "Stale peer ($DAYS days): $SHORT_PEER (AllowedIPs: $allowed_ips)"
        fi
        continue
    fi
    
    # Peer is active and has allowed IPs - keep it
    if [[ -n "$handshake" ]] && [[ "$handshake" != "0" ]]; then
        CURRENT_TIME=$(date +%s)
        TIME_DIFF=$((CURRENT_TIME - handshake))
        HOURS_DIFF=$((TIME_DIFF / 3600))
        MINUTES=$((TIME_DIFF / 60))
        
        if [ "$MINUTES" -lt 60 ]; then
            log_info "Active peer (<1 hour): $SHORT_PEER (AllowedIPs: $allowed_ips)"
        fi
    fi
    
done < <(wg show "$INTERFACE" dump 2>/dev/null | tail -n +2)

# ==================== Display results ====================
echo ""
log_info "═══════════════════════════════════════════════════════════"
log_info "Analysis Results"
log_info "═══════════════════════════════════════════════════════════"
echo "  Total peers:            $TOTAL_PEERS"
echo "  Protected relay peers:  $PROTECTED_COUNT"
echo "  Peers to remove:        ${#PEERS_TO_REMOVE[@]}"
log_info "═══════════════════════════════════════════════════════════"
echo ""

if [ ${#PEERS_TO_REMOVE[@]} -eq 0 ]; then
    log_info "✓ No cleanup needed. All peers are valid."
    exit 0
fi

# Show peers to be removed
log_info "Peers marked for removal:"
for entry in "${PEERS_TO_REMOVE[@]}"; do
    IFS='|' read -r peer reason <<< "$entry"
    SHORT_PEER="${peer:0:16}...${peer: -8}"
    log_warn "  $SHORT_PEER (Reason: $reason)"
done
echo ""

# Require explicit confirmation to prevent accidental deletion
echo ""
log_warn "⚠️  WARNING: This will permanently remove ${#PEERS_TO_REMOVE[@]} peer(s) from WireGuard"
log_warn "⚠️  This action cannot be undone (backup will be created)"
echo ""
echo -n "Type 'REMOVE' (in capitals) to confirm: "
read -r CONFIRM

if [ "$CONFIRM" != "REMOVE" ]; then
    log_info "Cleanup cancelled (confirmation not received)"
    exit 0
fi

log_info "Confirmation received - proceeding with cleanup..."

# ==================== Perform cleanup ====================
log_info "Starting cleanup..."
echo ""

REMOVED_COUNT=0
FAILED_COUNT=0
CONFIG_FILE="/etc/wireguard/${INTERFACE}.conf"

# Check if config file exists
if [ -f "$CONFIG_FILE" ]; then
    log_info "Using configuration file method (persistent changes)"
    
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
            PEER_SECTION_START="$line"
            continue
        fi
        
        # Detect other sections
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
                CURRENT_PEER_PUBKEY=$(echo "$CURRENT_PEER_PUBKEY" | xargs)
                
                # Check if this peer should be removed
                for entry in "${PEERS_TO_REMOVE[@]}"; do
                    IFS='|' read -r peer_to_remove reason <<< "$entry"
                    if [ "$CURRENT_PEER_PUBKEY" = "$peer_to_remove" ]; then
                        SKIP_CURRENT_PEER=true
                        SHORT_PEER="${peer_to_remove:0:16}...${peer_to_remove: -8}"
                        log_info "✓ Removing peer from config: $SHORT_PEER ($reason)"
                        ((REMOVED_COUNT++))
                        break
                    fi
                done
                
                # Write [Peer] section and this line if not skipping
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
    
    # Reload configuration
    log_info "Reloading WireGuard configuration..."
    
    if wg syncconf "$INTERFACE" <(wg-quick strip "$INTERFACE") 2>/dev/null; then
        log_info "✓ Configuration reloaded successfully (no downtime)"
    else
        log_warn "syncconf failed, attempting interface restart..."
        
        if systemctl is-active --quiet "wg-quick@${INTERFACE}"; then
            systemctl restart "wg-quick@${INTERFACE}" 2>/dev/null
            if [ $? -eq 0 ]; then
                log_info "✓ Interface restarted successfully"
            else
                log_error "Failed to restart interface"
                log_error "Backup available at: $BACKUP_FILE"
                exit 1
            fi
        else
            wg-quick down "$INTERFACE" 2>/dev/null
            wg-quick up "$INTERFACE" 2>/dev/null
            
            if [ $? -eq 0 ]; then
                log_info "✓ Interface restarted successfully"
            else
                log_error "Failed to restart interface"
                log_error "Backup available at: $BACKUP_FILE"
                exit 1
            fi
        fi
    fi
    
    # Clean up old backups (keep last 5)
    BACKUP_DIR=$(dirname "$CONFIG_FILE")
    ls -t "${BACKUP_DIR}/${INTERFACE}.conf.backup."* 2>/dev/null | tail -n +6 | xargs -r rm
    
else
    log_warn "Config file not found at $CONFIG_FILE"
    log_warn "Using runtime removal method - changes will not persist!"
    
    for entry in "${PEERS_TO_REMOVE[@]}"; do
        IFS='|' read -r peer reason <<< "$entry"
        SHORT_PEER="${peer:0:16}...${peer: -8}"
        
        if wg set "$INTERFACE" peer "$peer" remove 2>/dev/null; then
            log_info "✓ Removed peer: $SHORT_PEER ($reason)"
            ((REMOVED_COUNT++))
        else
            log_error "✗ Failed to remove peer: $SHORT_PEER"
            ((FAILED_COUNT++))
        fi
    done
fi

# ==================== Summary ====================
echo ""
log_info "═══════════════════════════════════════════════════════════"
log_info "Cleanup Summary"
log_info "═══════════════════════════════════════════════════════════"
echo "  Protected relay peers:  $PROTECTED_COUNT"
echo "  Peers removed:          $REMOVED_COUNT"
echo "  Failed:                 $FAILED_COUNT"
log_info "═══════════════════════════════════════════════════════════"

# Show final status
echo ""
log_info "Current interface status:"
wg show "$INTERFACE"

exit 0