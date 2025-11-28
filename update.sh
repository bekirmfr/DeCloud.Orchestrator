#!/bin/bash
#
# DeCloud Orchestrator Update Script
#
# Fetches latest code from GitHub, checks dependencies, rebuilds and restarts.
# Safe to run multiple times - only installs/updates what's needed.
#
# Usage:
#   sudo ./update.sh              # Normal update
#   sudo ./update.sh --force      # Force rebuild even if no changes
#   sudo ./update.sh --deps-only  # Only check/install dependencies
#

set -e

VERSION="1.0.0"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[✓]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[!]${NC} $1"; }
log_error() { echo -e "${RED}[✗]${NC} $1"; }
log_step() { echo -e "${CYAN}[STEP]${NC} $1"; }

# Configuration
INSTALL_DIR="/opt/decloud"
REPO_DIR="$INSTALL_DIR/DeCloud.Orchestrator"
REPO_URL="https://github.com/bekirmfr/DeCloud.Orchestrator.git"
SERVICE_NAME="decloud-orchestrator"
CONFIG_DIR="/etc/decloud"

# Flags
FORCE_REBUILD=false
DEPS_ONLY=false
CHANGES_DETECTED=false

# ============================================================
# Argument Parsing
# ============================================================
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --force|-f)
                FORCE_REBUILD=true
                shift
                ;;
            --deps-only|-d)
                DEPS_ONLY=true
                shift
                ;;
            --help|-h)
                show_help
                exit 0
                ;;
            *)
                log_error "Unknown option: $1"
                show_help
                exit 1
                ;;
        esac
    done
}

show_help() {
    cat << EOF
DeCloud Orchestrator Update Script v${VERSION}

Usage: $0 [options]

Options:
  --force, -f       Force rebuild even if no code changes detected
  --deps-only, -d   Only check and install dependencies, don't update code
  --help, -h        Show this help message

Examples:
  $0                # Normal update
  $0 --force        # Force rebuild
  $0 --deps-only    # Only check dependencies
EOF
}

# ============================================================
# Checks
# ============================================================
check_root() {
    if [ "$EUID" -ne 0 ]; then
        log_error "This script must be run as root (use sudo)"
        exit 1
    fi
}

check_installation() {
    if [ ! -d "$REPO_DIR" ]; then
        log_error "Orchestrator not installed at $REPO_DIR"
        log_error "Run the install script first:"
        log_error "  curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.Orchestrator/main/install.sh | sudo bash"
        exit 1
    fi
}

# ============================================================
# Dependency Checks & Installation
# ============================================================
check_dependencies() {
    log_step "Checking dependencies..."
    
    local deps_missing=false
    
    # Check .NET 8
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version 2>/dev/null | head -1)
        if [[ "$DOTNET_VERSION" == 8.* ]]; then
            log_success ".NET 8 SDK: $DOTNET_VERSION"
        else
            log_warn ".NET 8 required, found: $DOTNET_VERSION"
            deps_missing=true
        fi
    else
        log_warn ".NET SDK not found"
        deps_missing=true
    fi
    
    # Check git
    if command -v git &> /dev/null; then
        log_success "git: installed"
    else
        log_warn "git not found"
        deps_missing=true
    fi
    
    # Check jq (useful for API responses)
    if command -v jq &> /dev/null; then
        log_success "jq: installed"
    else
        log_warn "jq not found"
        deps_missing=true
    fi
    
    if [ "$deps_missing" = true ]; then
        return 1
    fi
    
    return 0
}

install_dependencies() {
    log_step "Installing missing dependencies..."
    
    # Detect OS
    if [ ! -f /etc/os-release ]; then
        log_error "Cannot detect OS"
        exit 1
    fi
    
    . /etc/os-release
    OS=$ID
    
    apt-get update -qq
    
    # Git
    if ! command -v git &> /dev/null; then
        log_info "Installing git..."
        apt-get install -y -qq git > /dev/null 2>&1
        log_success "git installed"
    fi
    
    # jq
    if ! command -v jq &> /dev/null; then
        log_info "Installing jq..."
        apt-get install -y -qq jq > /dev/null 2>&1
        log_success "jq installed"
    fi
    
    # .NET 8
    if ! command -v dotnet &> /dev/null || [[ "$(dotnet --version 2>/dev/null)" != 8.* ]]; then
        log_info "Installing .NET 8 SDK..."
        
        if [ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]; then
            wget -q https://packages.microsoft.com/config/$OS/$VERSION_ID/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
            dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
            rm /tmp/packages-microsoft-prod.deb
            apt-get update -qq
        fi
        
        apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
        log_success ".NET 8 SDK installed"
    fi
}

# ============================================================
# Update Functions
# ============================================================
fetch_updates() {
    log_step "Fetching updates from GitHub..."
    
    cd "$REPO_DIR"
    
    # Store current commit
    CURRENT_COMMIT=$(git rev-parse HEAD 2>/dev/null || echo "unknown")
    
    # Fetch updates
    git fetch origin --quiet
    
    # Check if there are updates
    LOCAL=$(git rev-parse HEAD)
    REMOTE=$(git rev-parse origin/master 2>/dev/null || git rev-parse origin/main 2>/dev/null)
    
    if [ "$LOCAL" = "$REMOTE" ]; then
        log_info "Already up to date (commit: ${LOCAL:0:8})"
        CHANGES_DETECTED=false
    else
        log_info "Updates available"
        log_info "  Current: ${LOCAL:0:8}"
        log_info "  Latest:  ${REMOTE:0:8}"
        
        # Show what changed
        echo ""
        log_info "Changes:"
        git log --oneline HEAD..origin/master 2>/dev/null || git log --oneline HEAD..origin/main 2>/dev/null | head -10
        echo ""
        
        # Pull updates
        git pull --quiet origin master 2>/dev/null || git pull --quiet origin main 2>/dev/null
        
        CHANGES_DETECTED=true
        log_success "Code updated to ${REMOTE:0:8}"
    fi
}

build_orchestrator() {
    log_step "Building Orchestrator..."
    
    cd "$REPO_DIR"
    
    # Clean previous build
    log_info "Cleaning previous build..."
    dotnet clean --verbosity quiet > /dev/null 2>&1 || true
    rm -rf src/Orchestrator/bin src/Orchestrator/obj 2>/dev/null || true
    
    # Restore packages
    log_info "Restoring packages..."
    dotnet restore --verbosity quiet
    
    # Build
    log_info "Compiling..."
    dotnet build -c Release --verbosity quiet --no-restore
    
    log_success "Build completed"
}

restart_service() {
    log_step "Restarting Orchestrator service..."
    
    # Check if service exists
    if ! systemctl list-unit-files | grep -q "$SERVICE_NAME"; then
        log_warn "Service $SERVICE_NAME not found, skipping restart"
        return
    fi
    
    # Stop service
    log_info "Stopping service..."
    systemctl stop $SERVICE_NAME 2>/dev/null || true
    
    # Wait a moment
    sleep 2
    
    # Start service
    log_info "Starting service..."
    systemctl start $SERVICE_NAME
    
    # Wait and verify
    sleep 3
    
    if systemctl is-active --quiet $SERVICE_NAME; then
        log_success "Service restarted successfully"
    else
        log_error "Service failed to start"
        log_error "Check logs: journalctl -u $SERVICE_NAME -n 50"
        exit 1
    fi
}

verify_health() {
    log_step "Verifying Orchestrator health..."
    
    # Get port from config
    local port=5050
    if [ -f "$CONFIG_DIR/orchestrator.appsettings.Production.json" ]; then
        port=$(grep -oP '"Url":\s*"http://[^:]+:\K\d+' "$CONFIG_DIR/orchestrator.appsettings.Production.json" 2>/dev/null || echo "5050")
    fi
    
    # Wait for service to be ready
    local max_attempts=10
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        if curl -s --max-time 2 "http://localhost:$port/health" > /dev/null 2>&1; then
            log_success "Orchestrator is healthy (port $port)"
            return 0
        fi
        
        log_info "Waiting for service to be ready... ($attempt/$max_attempts)"
        sleep 2
        ((attempt++))
    done
    
    log_warn "Health check timed out, but service may still be starting"
    log_info "Check status: systemctl status $SERVICE_NAME"
}

show_status() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║                   Orchestrator Status                        ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    # Service status
    if systemctl is-active --quiet $SERVICE_NAME; then
        log_success "Service: running"
    else
        log_error "Service: stopped"
    fi
    
    # Current version/commit
    if [ -d "$REPO_DIR/.git" ]; then
        cd "$REPO_DIR"
        COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")
        BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
        log_info "Version: $BRANCH @ $COMMIT"
    fi
    
    # Get config info
    local port=5050
    if [ -f "$CONFIG_DIR/orchestrator.appsettings.Production.json" ]; then
        port=$(grep -oP '"Url":\s*"http://[^:]+:\K\d+' "$CONFIG_DIR/orchestrator.appsettings.Production.json" 2>/dev/null || echo "5050")
    fi
    
    # Get public IP
    local public_ip=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')
    
    log_info "API: http://$public_ip:$port"
    log_info "Dashboard: http://$public_ip:$port/"
    log_info "Swagger: http://$public_ip:$port/swagger"
    
    # Node count
    local stats=$(curl -s --max-time 2 "http://localhost:$port/api/system/stats" 2>/dev/null)
    if echo "$stats" | grep -q '"success":true'; then
        local online_nodes=$(echo "$stats" | grep -o '"onlineNodes":[0-9]*' | cut -d: -f2)
        local total_nodes=$(echo "$stats" | grep -o '"totalNodes":[0-9]*' | cut -d: -f2)
        local running_vms=$(echo "$stats" | grep -o '"runningVms":[0-9]*' | cut -d: -f2)
        local total_vms=$(echo "$stats" | grep -o '"totalVms":[0-9]*' | cut -d: -f2)
        log_info "Nodes: $online_nodes online / $total_nodes total"
        log_info "VMs: $running_vms running / $total_vms total"
    fi
    
    echo ""
}

# ============================================================
# Main
# ============================================================
main() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║       DeCloud Orchestrator Update Script v${VERSION}              ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    parse_args "$@"
    check_root
    
    # Check dependencies
    if ! check_dependencies; then
        log_info "Some dependencies are missing, installing..."
        install_dependencies
        log_success "Dependencies installed"
        echo ""
    fi
    
    # If deps-only, stop here
    if [ "$DEPS_ONLY" = true ]; then
        log_success "Dependency check complete"
        exit 0
    fi
    
    check_installation
    
    # Fetch updates
    fetch_updates
    
    # Build if changes detected or forced
    if [ "$CHANGES_DETECTED" = true ] || [ "$FORCE_REBUILD" = true ]; then
        if [ "$FORCE_REBUILD" = true ] && [ "$CHANGES_DETECTED" = false ]; then
            log_info "Force rebuild requested"
        fi
        
        build_orchestrator
        restart_service
        verify_health
    else
        log_info "No changes detected, skipping build"
        log_info "Use --force to rebuild anyway"
    fi
    
    show_status
    
    log_success "Update complete!"
}

main "$@"
