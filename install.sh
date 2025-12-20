#!/bin/bash
#
# DeCloud Orchestrator Installation Script
#
# Installs the Orchestrator with:
# - .NET 8 Runtime
# - Node.js for frontend build
# - MongoDB connection
# - Caddy for central ingress (*.vms.decloud.io)
# - fail2ban for DDoS protection
#
# ARCHITECTURE:
# The Orchestrator is the ONLY component that needs ports 80/443.
# It handles all HTTP ingress centrally and routes to nodes via WireGuard.
#
# Version: 2.0.0
#
# Usage:
#   sudo ./install.sh --port 5050 --mongodb "mongodb+srv://..." \
#       --ingress-domain vms.decloud.io --caddy-email admin@example.com
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

# ============================================================
# Configuration
# ============================================================

# Paths
INSTALL_DIR="/opt/decloud"
CONFIG_DIR="/etc/decloud"
LOG_DIR="/var/log/decloud"
REPO_URL="https://github.com/bekirmfr/DeCloud.Orchestrator.git"

# Ports
API_PORT=5050

# MongoDB
MONGODB_URI=""

# Central Ingress (Caddy)
INSTALL_CADDY=false
INGRESS_DOMAIN=""
CADDY_EMAIL=""
CADDY_STAGING=false
CADDY_DATA_DIR="/var/lib/caddy"
CADDY_LOG_DIR="/var/log/caddy"

# fail2ban
INSTALL_FAIL2BAN=false

# ============================================================
# Argument Parsing
# ============================================================
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --port)
                API_PORT="$2"
                shift 2
                ;;
            --mongodb)
                MONGODB_URI="$2"
                shift 2
                ;;
            --ingress-domain)
                INGRESS_DOMAIN="$2"
                INSTALL_CADDY=true
                INSTALL_FAIL2BAN=true
                shift 2
                ;;
            --caddy-email)
                CADDY_EMAIL="$2"
                INSTALL_CADDY=true
                INSTALL_FAIL2BAN=true
                shift 2
                ;;
            --caddy-staging)
                CADDY_STAGING=true
                shift
                ;;
            --skip-caddy)
                INSTALL_CADDY=false
                shift
                ;;
            --skip-fail2ban)
                INSTALL_FAIL2BAN=false
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
DeCloud Orchestrator Installer v${VERSION}

Usage: $0 [options]

Required:
  --port <port>              API port (default: 5050)

Database:
  --mongodb <uri>            MongoDB connection string (recommended for persistence)

Central Ingress (for *.vms.decloud.io routing):
  --ingress-domain <domain>  Base domain for VM ingress (e.g., vms.decloud.io)
  --caddy-email <email>      Email for Let's Encrypt certificates
  --caddy-staging            Use Let's Encrypt staging (for testing)
  --skip-caddy               Skip Caddy installation
  --skip-fail2ban            Skip fail2ban installation

Examples:
  # Basic (no ingress, in-memory storage)
  $0 --port 5050

  # With MongoDB persistence
  $0 --port 5050 --mongodb "mongodb+srv://user:pass@cluster/db"

  # Full setup with central ingress
  $0 --port 5050 \\
     --mongodb "mongodb+srv://user:pass@cluster/db" \\
     --ingress-domain vms.decloud.io \\
     --caddy-email admin@decloud.io

ARCHITECTURE:
  The Orchestrator is the central entry point for all HTTP traffic.
  Node Agents do NOT need ports 80/443 - they connect via WireGuard.

  User Request → Orchestrator (80/443) → WireGuard → Node → VM
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

check_os() {
    log_step "Checking operating system..."
    
    if [ ! -f /etc/os-release ]; then
        log_error "Cannot detect OS"
        exit 1
    fi
    
    . /etc/os-release
    OS=$ID
    OS_VERSION=$VERSION_ID
    
    log_success "OS: $OS $OS_VERSION"
}

check_resources() {
    log_step "Checking system resources..."
    
    CPU_CORES=$(nproc)
    MEMORY_MB=$(free -m | awk '/^Mem:/{print $2}')
    DISK_GB=$(df -BG / | awk 'NR==2{print $4}' | tr -d 'G')
    
    log_success "CPU cores: $CPU_CORES"
    log_success "Memory: ${MEMORY_MB}MB"
    log_success "Free disk: ${DISK_GB}GB"
}

check_network() {
    log_step "Checking network connectivity..."
    
    if curl -s --max-time 5 https://github.com > /dev/null 2>&1; then
        log_success "Internet connectivity OK"
    else
        log_error "Cannot reach github.com"
        exit 1
    fi
    
    PUBLIC_IP=$(curl -s --max-time 5 ifconfig.me 2>/dev/null || curl -s --max-time 5 icanhazip.com 2>/dev/null || echo "unknown")
    log_success "Public IP: $PUBLIC_IP"
    
    # Check API port
    if ss -tlnp | grep -q ":${API_PORT} "; then
        log_error "Port $API_PORT is already in use"
        exit 1
    fi
    log_success "Port $API_PORT is available"
    
    # Check 80/443 if Caddy is enabled
    if [ "$INSTALL_CADDY" = true ]; then
        if ss -tlnp | grep -q ":80 "; then
            log_error "Port 80 is already in use (required for Caddy)"
            log_info "Disable the conflicting service or use --skip-caddy"
            exit 1
        fi
        if ss -tlnp | grep -q ":443 "; then
            log_error "Port 443 is already in use (required for Caddy)"
            exit 1
        fi
        log_success "Ports 80/443 available for central ingress"
    fi
}

check_mongodb() {
    log_step "Checking MongoDB connectivity..."
    
    if [ -n "$MONGODB_URI" ]; then
        if [[ ! "$MONGODB_URI" =~ ^mongodb(\+srv)?:// ]]; then
            log_error "Invalid MongoDB URI format"
            exit 1
        fi
        log_success "MongoDB URI provided"
        log_info "Connection will be verified at startup"
    else
        log_warn "No MongoDB URI - using in-memory storage (data will not persist!)"
    fi
}

# ============================================================
# Installation Functions
# ============================================================
install_dependencies() {
    log_step "Installing base dependencies..."
    
    apt-get update -qq
    apt-get install -y -qq \
        curl wget git jq apt-transport-https ca-certificates \
        gnupg lsb-release software-properties-common > /dev/null 2>&1
    
    log_success "Base dependencies installed"
}

install_nodejs() {
    log_step "Installing Node.js 20 LTS..."
    
    if command -v node &> /dev/null; then
        NODE_VERSION=$(node --version 2>/dev/null | sed 's/v//')
        NODE_MAJOR=$(echo $NODE_VERSION | cut -d. -f1)
        
        if [ "$NODE_MAJOR" -ge 18 ]; then
            log_success "Node.js already installed: v$NODE_VERSION"
            return
        fi
    fi
    
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - > /dev/null 2>&1
    apt-get install -y -qq nodejs > /dev/null 2>&1
    
    log_success "Node.js installed: $(node --version)"
}

install_dotnet() {
    log_step "Installing .NET 8 SDK..."
    
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version 2>/dev/null | head -1)
        if [[ "$DOTNET_VERSION" == 8.* ]]; then
            log_success ".NET 8 already installed: $DOTNET_VERSION"
            return
        fi
    fi
    
    wget -q https://packages.microsoft.com/config/$OS/$OS_VERSION/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb 2>/dev/null || \
    wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    
    dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
    rm /tmp/packages-microsoft-prod.deb
    
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
    
    log_success ".NET 8 SDK installed"
}

# ============================================================
# Caddy Installation (Central Ingress)
# ============================================================
install_caddy() {
    if [ "$INSTALL_CADDY" = false ]; then
        log_info "Skipping Caddy installation"
        return
    fi
    
    log_step "Installing Caddy for central ingress..."
    
    if command -v caddy &> /dev/null; then
        log_success "Caddy already installed: $(caddy version | head -1)"
        return
    fi
    
    apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https curl > /dev/null 2>&1
    
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | \
        gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg 2>/dev/null
    
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | \
        tee /etc/apt/sources.list.d/caddy-stable.list > /dev/null
    
    apt-get update -qq
    apt-get install -y -qq caddy > /dev/null 2>&1
    
    log_success "Caddy installed: $(caddy version | head -1)"
}

configure_caddy() {
    if [ "$INSTALL_CADDY" = false ]; then
        return
    fi
    
    log_step "Configuring Caddy for central ingress..."
    
    mkdir -p "$CADDY_DATA_DIR" "$CADDY_LOG_DIR" /etc/caddy
    chown caddy:caddy "$CADDY_DATA_DIR" "$CADDY_LOG_DIR" 2>/dev/null || true
    
    # Build ACME configuration
    local acme_config=""
    if [ -n "$CADDY_EMAIL" ]; then
        acme_config="    email $CADDY_EMAIL"
    fi
    if [ "$CADDY_STAGING" = true ]; then
        acme_config="$acme_config
    acme_ca https://acme-staging-v02.api.letsencrypt.org/directory"
    fi
    
    # Wildcard domain config
    local wildcard_config=""
    if [ -n "$INGRESS_DOMAIN" ]; then
        wildcard_config="
# Central Ingress: Route *.${INGRESS_DOMAIN} to VMs
# Routes are managed dynamically via Admin API
*.${INGRESS_DOMAIN} {
    tls {
        dns cloudflare {env.CF_API_TOKEN}
    }
    
    # Default: proxy to orchestrator for routing decisions
    reverse_proxy localhost:${API_PORT}
}"
    fi
    
    cat > /etc/caddy/Caddyfile << EOF
# DeCloud Orchestrator - Central Ingress Gateway
# All VM HTTP traffic routes through here

{
    admin localhost:2019
    
    log {
        output file $CADDY_LOG_DIR/caddy.log {
            roll_size 100mb
            roll_keep 5
        }
        format json
    }
    
$acme_config
}

# Health check endpoint
:8080 {
    respond /health "OK" 200
    respond /ready "OK" 200
}

# Orchestrator API and Dashboard
:${API_PORT} {
    reverse_proxy localhost:5000
}
$wildcard_config
EOF

    # Systemd override
    mkdir -p /etc/systemd/system/caddy.service.d
    cat > /etc/systemd/system/caddy.service.d/decloud.conf << 'EOF'
[Service]
AmbientCapabilities=CAP_NET_BIND_SERVICE
LimitNOFILE=1048576
Restart=always
RestartSec=5
EOF

    systemctl daemon-reload
    log_success "Caddy configured"
}

# ============================================================
# fail2ban Installation
# ============================================================
install_fail2ban() {
    if [ "$INSTALL_FAIL2BAN" = false ]; then
        log_info "Skipping fail2ban installation"
        return
    fi
    
    log_step "Installing fail2ban..."
    
    apt-get install -y -qq fail2ban > /dev/null 2>&1
    
    # Configure jails
    cat > /etc/fail2ban/jail.d/decloud.conf << EOF
[DEFAULT]
bantime = 3600
findtime = 600
maxretry = 10

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
EOF

    # Caddy abuse filter
    cat > /etc/fail2ban/filter.d/caddy-abuse.conf << 'EOF'
[Definition]
failregex = ^.*"client_ip":"<HOST>".*"status":(400|401|403|404|405|429|503).*$
ignoreregex =
datepattern = "ts":{EPOCH}
EOF

    systemctl enable fail2ban --quiet 2>/dev/null || true
    systemctl restart fail2ban 2>/dev/null || true
    
    log_success "fail2ban installed and configured"
}

# ============================================================
# Orchestrator Setup
# ============================================================
create_directories() {
    log_step "Creating directories..."
    
    mkdir -p "$INSTALL_DIR"
    mkdir -p "$CONFIG_DIR"
    mkdir -p "$LOG_DIR"
    
    log_success "Directories created"
}

download_orchestrator() {
    log_step "Downloading Orchestrator..."
    
    if [ -d "$INSTALL_DIR/DeCloud.Orchestrator" ]; then
        log_info "Removing existing installation..."
        rm -rf "$INSTALL_DIR/DeCloud.Orchestrator"
    fi
    
    cd "$INSTALL_DIR"
    git clone --depth 1 "$REPO_URL" DeCloud.Orchestrator > /dev/null 2>&1
    
    cd DeCloud.Orchestrator
    COMMIT=$(git rev-parse --short HEAD)
    
    log_success "Code downloaded (commit: $COMMIT)"
}

build_orchestrator() {
    log_step "Building Orchestrator (frontend + backend)..."
    
    cd "$INSTALL_DIR/DeCloud.Orchestrator"
    
    log_info "This may take 2-3 minutes..."
    
    # Clean
    dotnet clean --verbosity quiet > /dev/null 2>&1 || true
    
    # Build (includes frontend via MSBuild targets)
    cd src/Orchestrator
    dotnet build --configuration Release --verbosity quiet 2>&1 | grep -E "(error|warning CS)" || true
    
    log_success "Build completed"
    
    # Verify frontend
    if [ -d "wwwroot/dist" ]; then
        local file_count=$(find wwwroot/dist -type f | wc -l)
        log_success "Frontend built: $file_count files"
    else
        log_warn "Frontend dist not found - may need manual build"
    fi
}

create_configuration() {
    log_step "Creating configuration..."
    
    # Central ingress config - add if ingress domain is provided (regardless of Caddy install)
    local central_ingress_config=""
    if [ -n "$INGRESS_DOMAIN" ]; then
        central_ingress_config=',
  "CentralIngress": {
    "Enabled": true,
    "BaseDomain": "'$INGRESS_DOMAIN'",
    "CaddyAdminUrl": "http://localhost:2019",
    "AcmeEmail": "'${CADDY_ACME_EMAIL:-admin@$INGRESS_DOMAIN}'",
    "DefaultTargetPort": 80,
    "SubdomainPattern": "{name}",
    "AutoRegisterOnStart": true,
    "AutoRemoveOnStop": true,
    "ProxyTimeoutSeconds": 30,
    "EnableWebSocket": true
  }'
    fi
    
    # MongoDB config
    local mongodb_config=""
    if [ -n "$MONGODB_URI" ]; then
        mongodb_config=',
  "MongoDB": {
    "ConnectionString": "'$MONGODB_URI'"
  }'
    fi
    
    cat > "$CONFIG_DIR/orchestrator.appsettings.Production.json" << EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Urls": "http://0.0.0.0:${API_PORT}",
  "Jwt": {
    "Key": "$(openssl rand -base64 32)",
    "Issuer": "decloud-orchestrator",
    "Audience": "decloud-users",
    "ExpiryMinutes": 1440
  }$mongodb_config$central_ingress_config
}
EOF

    chmod 640 "$CONFIG_DIR/orchestrator.appsettings.Production.json"
    log_success "Configuration created"
}

create_systemd_service() {
    log_step "Creating systemd service..."
    
    cat > /etc/systemd/system/decloud-orchestrator.service << EOF
[Unit]
Description=DeCloud Orchestrator
After=network.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator
ExecStart=/usr/bin/dotnet run --configuration Release --no-build
Restart=always
RestartSec=10
TimeoutStartSec=120
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
StandardOutput=journal
StandardError=journal
SyslogIdentifier=decloud-orchestrator

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    log_success "Systemd service created"
}

configure_firewall() {
    log_step "Configuring firewall..."
    
    if command -v ufw &> /dev/null && ufw status | grep -q "Status: active"; then
        ufw allow ${API_PORT}/tcp comment "DeCloud Orchestrator API" > /dev/null 2>&1 || true
        
        if [ "$INSTALL_CADDY" = true ]; then
            ufw allow 80/tcp comment "DeCloud Ingress HTTP" > /dev/null 2>&1 || true
            ufw allow 443/tcp comment "DeCloud Ingress HTTPS" > /dev/null 2>&1 || true
        fi
        
        log_success "Firewall configured"
    else
        log_info "UFW not active, skipping"
    fi
}

start_services() {
    log_step "Starting services..."
    
    # Start Caddy if installed
    if [ "$INSTALL_CADDY" = true ]; then
        systemctl enable caddy --quiet 2>/dev/null || true
        systemctl start caddy 2>/dev/null || true
        
        if systemctl is-active --quiet caddy; then
            log_success "Caddy started"
        else
            log_warn "Caddy failed to start"
        fi
    fi
    
    # Start Orchestrator
    systemctl enable decloud-orchestrator --quiet 2>/dev/null || true
    systemctl start decloud-orchestrator 2>/dev/null || true
    
    sleep 3
    
    if systemctl is-active --quiet decloud-orchestrator; then
        log_success "Orchestrator is running"
    else
        log_error "Orchestrator failed to start"
        log_info "Check logs: journalctl -u decloud-orchestrator -n 50"
    fi
}

# ============================================================
# Summary
# ============================================================
print_summary() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║              Installation Complete!                          ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    echo "  Dashboard:       http://${PUBLIC_IP}:${API_PORT}/"
    echo "  API Endpoint:    http://${PUBLIC_IP}:${API_PORT}/api"
    echo "  Swagger UI:      http://${PUBLIC_IP}:${API_PORT}/swagger"
    echo ""
    
    if [ "$INSTALL_CADDY" = true ]; then
        echo "  ─────────────────────────────────────────────────────────────"
        echo "  Central Ingress (Caddy):"
        echo "  ─────────────────────────────────────────────────────────────"
        if [ -n "$INGRESS_DOMAIN" ]; then
            echo "    Base Domain:   *.${INGRESS_DOMAIN}"
            echo "    Example:       https://vm-abc123.${INGRESS_DOMAIN}"
        fi
        echo "    Admin API:     http://localhost:2019"
        echo "    Health:        http://localhost:8080/health"
        echo ""
    fi
    
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Commands:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Status:        sudo systemctl status decloud-orchestrator"
    echo "    Logs:          sudo journalctl -u decloud-orchestrator -f"
    echo "    Restart:       sudo systemctl restart decloud-orchestrator"
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Caddy logs:    sudo tail -f $CADDY_LOG_DIR/caddy.log"
    fi
    echo ""
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  To add a node, run on the node server:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo ""
    echo "    curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.NodeAgent/main/install.sh | \\"
    echo "      sudo bash -s -- --orchestrator http://${PUBLIC_IP}:${API_PORT}"
    echo ""
    echo "  Note: Nodes do NOT need ports 80/443 - ingress is handled here!"
    echo ""
}

# ============================================================
# Main
# ============================================================
main() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║       DeCloud Orchestrator Installer v${VERSION}                  ║"
    echo "║       (Central Ingress Architecture)                         ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    parse_args "$@"
    
    # Checks
    check_root
    check_os
    check_resources
    check_network
    check_mongodb
    
    echo ""
    log_info "All requirements met. Starting installation..."
    echo ""
    
    # Install dependencies
    install_dependencies
    install_nodejs
    install_dotnet
    
    # Central ingress
    install_caddy
    configure_caddy
    install_fail2ban
    
    # Orchestrator
    create_directories
    download_orchestrator
    build_orchestrator
    create_configuration
    create_systemd_service
    configure_firewall
    start_services
    
    # Done
    print_summary
}

main "$@"