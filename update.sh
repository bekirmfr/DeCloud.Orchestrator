#!/bin/bash
#
# DeCloud Orchestrator Update Script
#
# Updates the Orchestrator including:
# - Code updates from GitHub
# - Frontend + backend rebuild
# - Caddy central ingress verification
# - fail2ban security verification
#
# Version: 2.0.0
#
# Usage:
#   sudo ./update.sh              # Normal update
#   sudo ./update.sh --force      # Force rebuild even if no changes
#   sudo ./update.sh --deps-only  # Only check/install dependencies
#

set -e

VERSION="2.0.0"

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
CADDY_LOG_DIR="/var/log/caddy"

# Flags
FORCE_REBUILD=false
DEPS_ONLY=false
CHANGES_DETECTED=false
SKIP_CADDY=false
SKIP_FAIL2BAN=false

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
            --skip-caddy)
                SKIP_CADDY=true
                shift
                ;;
            --skip-fail2ban)
                SKIP_FAIL2BAN=true
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
  --skip-caddy      Skip Caddy checks and updates
  --skip-fail2ban   Skip fail2ban checks and updates
  --help, -h        Show this help message

Examples:
  $0                # Normal update (code + security services)
  $0 --force        # Force rebuild
  $0 --deps-only    # Only check dependencies

Components Updated:
  - Orchestrator code and build
  - Caddy central ingress configuration
  - fail2ban DDoS protection rules
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
        log_error "Run the install script first"
        exit 1
    fi
}

# ============================================================
# Dependency Checks & Installation
# ============================================================
check_dependencies() {
    log_step "Checking dependencies..."
    
    local deps_missing=false
    
    # Check Node.js
    if command -v node &> /dev/null; then
        NODE_VERSION=$(node --version 2>/dev/null | sed 's/v//')
        NODE_MAJOR=$(echo $NODE_VERSION | cut -d. -f1)
        
        if [ "$NODE_MAJOR" -ge 18 ]; then
            log_success "Node.js: v$NODE_VERSION"
        else
            log_warn "Node.js $NODE_VERSION found (need 18+)"
            deps_missing=true
        fi
    else
        log_warn "Node.js not found"
        deps_missing=true
    fi
    
    # Check npm
    if command -v npm &> /dev/null; then
        NPM_VERSION=$(npm --version 2>/dev/null)
        log_success "npm: v$NPM_VERSION"
    else
        log_warn "npm not found"
        deps_missing=true
    fi
    
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
    
    # Check jq
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
    
    # Node.js 20 LTS
    if ! command -v node &> /dev/null; then
        log_info "Installing Node.js 20 LTS..."
        curl -fsSL https://deb.nodesource.com/setup_20.x | bash - > /dev/null 2>&1
        apt-get install -y -qq nodejs > /dev/null 2>&1
        log_success "Node.js installed: $(node --version)"
    else
        NODE_VERSION=$(node --version 2>/dev/null | sed 's/v//')
        NODE_MAJOR=$(echo $NODE_VERSION | cut -d. -f1)
        
        if [ "$NODE_MAJOR" -lt 18 ]; then
            log_info "Upgrading Node.js..."
            curl -fsSL https://deb.nodesource.com/setup_20.x | bash - > /dev/null 2>&1
            apt-get install -y -qq nodejs > /dev/null 2>&1
            log_success "Node.js upgraded: $(node --version)"
        fi
    fi
    
    # .NET 8
    if ! command -v dotnet &> /dev/null || [[ "$(dotnet --version 2>/dev/null)" != 8.* ]]; then
        log_info "Installing .NET 8 SDK..."
        
        if [ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]; then
            wget -q https://packages.microsoft.com/config/$OS/$VERSION_ID/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null || \
            wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
            dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
            rm /tmp/packages-microsoft-prod.deb
            apt-get update -qq
        fi
        
        apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
        log_success ".NET 8 SDK installed"
    fi
}

# ============================================================
# Caddy Central Ingress
# ============================================================
check_caddy() {
    if [ "$SKIP_CADDY" = true ]; then
        log_info "Skipping Caddy checks (--skip-caddy)"
        return 0
    fi
    
    log_step "Checking Caddy central ingress..."
    
    local caddy_issues=false
    
    # Check if Caddy is installed
    if ! command -v caddy &> /dev/null; then
        log_warn "Caddy not installed"
        caddy_issues=true
    else
        log_success "Caddy installed: $(caddy version 2>/dev/null | head -1 | cut -d' ' -f1)"
    fi
    
    # Check if Caddy service exists and is running
    if systemctl list-unit-files | grep -q "caddy.service"; then
        if systemctl is-active --quiet caddy; then
            log_success "Caddy service: running"
        else
            log_warn "Caddy service: not running"
            caddy_issues=true
        fi
    else
        log_warn "Caddy service not found"
        caddy_issues=true
    fi
    
    # Check Caddy admin API
    if curl -s --max-time 2 http://localhost:2019/config/ > /dev/null 2>&1; then
        log_success "Caddy admin API: accessible"
    else
        log_warn "Caddy admin API: not accessible"
        caddy_issues=true
    fi
    
    # Check Caddyfile exists
    if [ -f /etc/caddy/Caddyfile ]; then
        log_success "Caddyfile: exists"
    else
        log_warn "Caddyfile not found"
        caddy_issues=true
    fi
    
    # Check log directory
    if [ -d "$CADDY_LOG_DIR" ]; then
        log_success "Caddy logs: $CADDY_LOG_DIR"
    else
        log_warn "Caddy log directory missing"
    fi
    
    if [ "$caddy_issues" = true ]; then
        return 1
    fi
    
    return 0
}

install_caddy() {
    log_step "Installing/configuring Caddy..."
    
    # Install Caddy if not present
    if ! command -v caddy &> /dev/null; then
        log_info "Installing Caddy..."
        
        apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl > /dev/null 2>&1
        
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | \
            gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg 2>/dev/null
        
        curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | \
            tee /etc/apt/sources.list.d/caddy-stable.list > /dev/null
        
        apt-get update -qq
        apt-get install -y -qq caddy > /dev/null 2>&1
        
        log_success "Caddy installed"
    fi
    
    # Create directories
    mkdir -p /var/lib/caddy "$CADDY_LOG_DIR" /etc/caddy
    chown caddy:caddy /var/lib/caddy "$CADDY_LOG_DIR" 2>/dev/null || true
    
    # Create Caddyfile if missing
    if [ ! -f /etc/caddy/Caddyfile ]; then
        log_info "Creating default Caddyfile..."
        
        # Get API port from config
        local api_port=5050
        if [ -f "$CONFIG_DIR/orchestrator.appsettings.Production.json" ]; then
            api_port=$(grep -oP '"Urls":\s*"http://[^:]+:\K\d+' "$CONFIG_DIR/orchestrator.appsettings.Production.json" 2>/dev/null || echo "5050")
        fi
        
        cat > /etc/caddy/Caddyfile << EOF
# DeCloud Orchestrator - Central Ingress Gateway
# Generated by update.sh on $(date)

{
    admin localhost:2019
    
    log {
        output file $CADDY_LOG_DIR/caddy.log {
            roll_size 100mb
            roll_keep 5
        }
        format json
    }
}

# Health check endpoint
:8080 {
    respond /health "OK" 200
    respond /ready "OK" 200
}

# Note: VM ingress routes (*.vms.decloud.io) are managed dynamically
# via the Caddy Admin API by CentralIngressService
EOF
        log_success "Default Caddyfile created"
    fi
    
    # Create systemd override
    mkdir -p /etc/systemd/system/caddy.service.d
    cat > /etc/systemd/system/caddy.service.d/decloud.conf << 'EOF'
[Service]
AmbientCapabilities=CAP_NET_BIND_SERVICE
LimitNOFILE=1048576
Restart=always
RestartSec=5
EOF
    
    systemctl daemon-reload
    
    # Enable and start
    systemctl enable caddy --quiet 2>/dev/null || true
    
    if ! systemctl is-active --quiet caddy; then
        log_info "Starting Caddy..."
        systemctl start caddy 2>/dev/null || true
        sleep 2
    fi
    
    if systemctl is-active --quiet caddy; then
        log_success "Caddy running"
    else
        log_warn "Failed to start Caddy"
    fi
}

# ============================================================
# fail2ban Security
# ============================================================
check_fail2ban() {
    if [ "$SKIP_FAIL2BAN" = true ]; then
        log_info "Skipping fail2ban checks (--skip-fail2ban)"
        return 0
    fi
    
    log_step "Checking fail2ban security..."
    
    local f2b_issues=false
    
    # Check if installed
    if ! command -v fail2ban-client &> /dev/null; then
        log_warn "fail2ban not installed"
        f2b_issues=true
    else
        log_success "fail2ban installed"
    fi
    
    # Check service status
    if systemctl is-active --quiet fail2ban; then
        log_success "fail2ban service: running"
        
        # Check jail status
        local jails=$(fail2ban-client status 2>/dev/null | grep "Jail list" | cut -d: -f2 | tr -d ' \t')
        if [ -n "$jails" ]; then
            log_success "Active jails: $jails"
        fi
    else
        log_warn "fail2ban service: not running"
        f2b_issues=true
    fi
    
    # Check DeCloud jail config
    if [ -f /etc/fail2ban/jail.d/decloud.conf ]; then
        log_success "DeCloud jail config: exists"
    else
        log_warn "DeCloud jail config: missing"
        f2b_issues=true
    fi
    
    # Check Caddy filter
    if [ -f /etc/fail2ban/filter.d/caddy-abuse.conf ]; then
        log_success "Caddy abuse filter: exists"
    else
        log_warn "Caddy abuse filter: missing"
        f2b_issues=true
    fi
    
    if [ "$f2b_issues" = true ]; then
        return 1
    fi
    
    return 0
}

install_fail2ban() {
    log_step "Installing/configuring fail2ban..."
    
    # Install if not present
    if ! command -v fail2ban-client &> /dev/null; then
        log_info "Installing fail2ban..."
        apt-get install -y -qq fail2ban > /dev/null 2>&1
        log_success "fail2ban installed"
    fi
    
    # Create DeCloud jail config
    log_info "Configuring DeCloud jails..."
    cat > /etc/fail2ban/jail.d/decloud.conf << EOF
# DeCloud fail2ban configuration
# Generated by update.sh on $(date)

[DEFAULT]
bantime = 3600
findtime = 600
maxretry = 10
backend = auto

[sshd]
enabled = true
port = ssh
maxretry = 5
bantime = 86400

[caddy-abuse]
enabled = true
port = http,https
filter = caddy-abuse
logpath = ${CADDY_LOG_DIR}/caddy.log
maxretry = 30
findtime = 300
bantime = 3600

[caddy-auth]
enabled = true
port = http,https
filter = caddy-auth
logpath = ${CADDY_LOG_DIR}/caddy.log
maxretry = 10
findtime = 600
bantime = 7200
EOF
    
    # Create Caddy abuse filter
    cat > /etc/fail2ban/filter.d/caddy-abuse.conf << 'EOF'
# Caddy abuse filter - rate limiting and bad requests
[Definition]
failregex = ^.*"client_ip":"<HOST>".*"status":(400|401|403|404|405|429|503).*$
ignoreregex =
datepattern = "ts":{EPOCH}
EOF
    
    # Create Caddy auth filter
    cat > /etc/fail2ban/filter.d/caddy-auth.conf << 'EOF'
# Caddy authentication failure filter
[Definition]
failregex = ^.*"client_ip":"<HOST>".*"status":401.*$
            ^.*"client_ip":"<HOST>".*"status":403.*$
ignoreregex =
datepattern = "ts":{EPOCH}
EOF
    
    # Enable and restart
    systemctl enable fail2ban --quiet 2>/dev/null || true
    systemctl restart fail2ban 2>/dev/null || true
    
    sleep 2
    
    if systemctl is-active --quiet fail2ban; then
        log_success "fail2ban running"
        
        # Show jail status
        local banned=$(fail2ban-client status 2>/dev/null | grep -c "Currently banned" || echo "0")
        log_info "Currently banned IPs: $banned"
    else
        log_warn "Failed to start fail2ban"
    fi
}

# ============================================================
# Update Functions
# ============================================================
fetch_updates() {
    log_step "Fetching updates from GitHub..."
    
    cd "$REPO_DIR"
    
    CURRENT_COMMIT=$(git rev-parse HEAD 2>/dev/null || echo "unknown")
    
    git fetch origin --quiet
    
    LOCAL=$(git rev-parse HEAD)
    REMOTE=$(git rev-parse origin/master 2>/dev/null || git rev-parse origin/main 2>/dev/null)
    
    if [ "$LOCAL" = "$REMOTE" ]; then
        log_info "Already up to date (commit: ${LOCAL:0:8})"
        CHANGES_DETECTED=false
    else
        log_info "Updates available: ${LOCAL:0:8} → ${REMOTE:0:8}"
        
        echo ""
        log_info "Recent changes:"
        git log --oneline HEAD..origin/master 2>/dev/null || git log --oneline HEAD..origin/main 2>/dev/null | head -10
        echo ""
        
        git pull --quiet origin master 2>/dev/null || git pull --quiet origin main 2>/dev/null
        
        CHANGES_DETECTED=true
        log_success "Code updated to ${REMOTE:0:8}"
    fi
}

build_orchestrator() {
    log_step "Building Orchestrator (frontend + backend)..."
    
    cd "$REPO_DIR"
    
    # Clean
    log_info "Cleaning previous build..."
    dotnet clean --verbosity quiet > /dev/null 2>&1 || true
    rm -rf src/Orchestrator/bin src/Orchestrator/obj 2>/dev/null || true
    
    # Build
    log_info "Building... (this may take 2-3 minutes)"
    
    cd src/Orchestrator
    
    if dotnet build -c Release --verbosity minimal 2>&1 | tee /tmp/build.log; then
        log_success "Build completed"
        
        # Verify frontend
        if [ -d "wwwroot/dist" ]; then
            DIST_FILES=$(find wwwroot/dist -type f | wc -l)
            log_success "Frontend: $DIST_FILES files"
        else
            log_warn "Frontend dist/ not found"
        fi
        
        # Verify backend
        if [ -f "bin/Release/net8.0/Orchestrator.dll" ]; then
            log_success "Backend: built"
        else
            log_error "Backend build failed"
            cat /tmp/build.log
            exit 1
        fi
    else
        log_error "Build failed!"
        cat /tmp/build.log
        exit 1
    fi
}

restart_service() {
    log_step "Restarting Orchestrator service..."
    
    if ! systemctl list-unit-files | grep -q "$SERVICE_NAME"; then
        log_warn "Service $SERVICE_NAME not found, skipping restart"
        return
    fi
    
    systemctl stop $SERVICE_NAME 2>/dev/null || true
    sleep 2
    systemctl start $SERVICE_NAME
    sleep 3
    
    if systemctl is-active --quiet $SERVICE_NAME; then
        log_success "Service restarted"
    else
        log_error "Service failed to start"
        log_error "Check logs: journalctl -u $SERVICE_NAME -n 50"
        exit 1
    fi
}

verify_health() {
    log_step "Verifying Orchestrator health..."
    
    local port=5050
    if [ -f "$CONFIG_DIR/orchestrator.appsettings.Production.json" ]; then
        port=$(grep -oP '"Urls":\s*"http://[^:]+:\K\d+' "$CONFIG_DIR/orchestrator.appsettings.Production.json" 2>/dev/null || echo "5050")
    fi
    
    local max_attempts=10
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        if curl -s --max-time 2 "http://localhost:$port/health" > /dev/null 2>&1; then
            log_success "Orchestrator healthy (port $port)"
            return 0
        fi
        
        log_info "Waiting for service... ($attempt/$max_attempts)"
        sleep 2
        ((attempt++))
    done
    
    log_warn "Health check timed out"
}

show_status() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║                   Orchestrator Status                        ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    # Orchestrator service
    echo "  Orchestrator:"
    if systemctl is-active --quiet $SERVICE_NAME; then
        echo -e "    Service:       ${GREEN}Running${NC}"
    else
        echo -e "    Service:       ${RED}Stopped${NC}"
    fi
    
    if [ -d "$REPO_DIR/.git" ]; then
        cd "$REPO_DIR"
        COMMIT=$(git rev-parse --short HEAD 2>/dev/null || echo "unknown")
        BRANCH=$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo "unknown")
        echo "    Version:       $BRANCH @ $COMMIT"
    fi
    
    # Caddy
    echo ""
    echo "  Caddy (Central Ingress):"
    if systemctl is-active --quiet caddy; then
        echo -e "    Service:       ${GREEN}Running${NC}"
        if curl -s --max-time 2 http://localhost:2019/config/ > /dev/null 2>&1; then
            echo -e "    Admin API:     ${GREEN}Accessible${NC}"
        else
            echo -e "    Admin API:     ${YELLOW}Not accessible${NC}"
        fi
    else
        echo -e "    Service:       ${YELLOW}Not running${NC}"
    fi
    
    # fail2ban
    echo ""
    echo "  fail2ban (Security):"
    if systemctl is-active --quiet fail2ban; then
        echo -e "    Service:       ${GREEN}Running${NC}"
        local banned=$(fail2ban-client status 2>/dev/null | grep -oP "Currently banned:\s+\K\d+" | head -1 || echo "0")
        echo "    Banned IPs:    $banned"
    else
        echo -e "    Service:       ${YELLOW}Not running${NC}"
    fi
    
    # Endpoints
    echo ""
    local port=5050
    if [ -f "$CONFIG_DIR/orchestrator.appsettings.Production.json" ]; then
        port=$(grep -oP '"Urls":\s*"http://[^:]+:\K\d+' "$CONFIG_DIR/orchestrator.appsettings.Production.json" 2>/dev/null || echo "5050")
    fi
    
    local public_ip=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')
    
    echo "  Endpoints:"
    echo "    Dashboard:     http://$public_ip:$port/"
    echo "    API:           http://$public_ip:$port/api"
    echo "    Swagger:       http://$public_ip:$port/swagger"
    echo "    Caddy Health:  http://localhost:8080/health"
    
    # Stats
    local stats=$(curl -s --max-time 2 "http://localhost:$port/api/system/stats" 2>/dev/null)
    if echo "$stats" | grep -q '"success":true'; then
        local online_nodes=$(echo "$stats" | grep -o '"onlineNodes":[0-9]*' | cut -d: -f2)
        local total_nodes=$(echo "$stats" | grep -o '"totalNodes":[0-9]*' | cut -d: -f2)
        local running_vms=$(echo "$stats" | grep -o '"runningVms":[0-9]*' | cut -d: -f2)
        local total_vms=$(echo "$stats" | grep -o '"totalVms":[0-9]*' | cut -d: -f2)
        echo ""
        echo "  Stats:"
        echo "    Nodes:         $online_nodes online / $total_nodes total"
        echo "    VMs:           $running_vms running / $total_vms total"
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
    echo "║       (Central Ingress Architecture)                         ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    parse_args "$@"
    check_root
    
    # Check core dependencies
    if ! check_dependencies; then
        log_info "Installing missing dependencies..."
        install_dependencies
        echo ""
    fi
    
    # Check Caddy
    if ! check_caddy; then
        log_info "Installing/configuring Caddy..."
        install_caddy
        echo ""
    fi
    
    # Check fail2ban
    if ! check_fail2ban; then
        log_info "Installing/configuring fail2ban..."
        install_fail2ban
        echo ""
    fi
    
    # If deps-only, stop here
    if [ "$DEPS_ONLY" = true ]; then
        log_success "Dependency check complete"
        show_status
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