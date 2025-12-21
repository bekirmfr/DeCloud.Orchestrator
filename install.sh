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
# Version: 2.1.0
#
# Usage:
#   sudo ./install.sh --port 5050 --mongodb "mongodb+srv://..." \
#       --ingress-domain vms.decloud.io --caddy-email admin@example.com \
#       --cloudflare-token YOUR_CF_TOKEN
#

set -e

VERSION="2.1.0"

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
DNS_PROVIDER="cloudflare"
CLOUDFLARE_TOKEN=""

# fail2ban
INSTALL_FAIL2BAN=false

# Update mode (detected if orchestrator already running)
UPDATE_MODE=false

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
                shift 2
                ;;
            --cloudflare-token)
                CLOUDFLARE_TOKEN="$2"
                DNS_PROVIDER="cloudflare"
                shift 2
                ;;
            --dns-provider)
                DNS_PROVIDER="$2"
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

USAGE:
    sudo ./install.sh [OPTIONS]

REQUIRED:
    --mongodb <uri>           MongoDB connection string
                              Example: mongodb+srv://user:pass@cluster.mongodb.net/decloud

OPTIONAL - Central Ingress (Caddy):
    --ingress-domain <domain> Enable central ingress (e.g., vms.decloud.io)
                              VMs will get URLs like: app.vms.decloud.io
    --caddy-email <email>     Email for Let's Encrypt certificates
    --cloudflare-token <token> Cloudflare API token for DNS-01 challenge
                              Required for wildcard certificates
    --dns-provider <provider> DNS provider (default: cloudflare)
                              Options: cloudflare, route53, digitalocean
    --caddy-staging           Use Let's Encrypt staging (for testing)
    --skip-caddy              Skip Caddy installation

OPTIONAL - General:
    --port <port>             API port (default: 5050)
    --skip-fail2ban           Skip fail2ban installation
    --help, -h                Show this help message

EXAMPLES:
    # Minimal installation (no ingress)
    sudo ./install.sh --mongodb "mongodb+srv://..."

    # With central ingress
    sudo ./install.sh \\
        --mongodb "mongodb+srv://..." \\
        --ingress-domain vms.decloud.io \\
        --caddy-email admin@decloud.io \\
        --cloudflare-token YOUR_CLOUDFLARE_API_TOKEN

    # Testing with Let's Encrypt staging
    sudo ./install.sh \\
        --mongodb "mongodb+srv://..." \\
        --ingress-domain vms.decloud.io \\
        --caddy-email admin@decloud.io \\
        --cloudflare-token YOUR_TOKEN \\
        --caddy-staging

CLOUDFLARE TOKEN:
    To get a Cloudflare API token:
    1. Go to https://dash.cloudflare.com/profile/api-tokens
    2. Click "Create Token"
    3. Use "Edit zone DNS" template
    4. Scope to your domain's zone
    5. Create token and copy it

EOF
}

# ============================================================
# Requirement Checks
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
        log_error "Cannot detect OS. Only Ubuntu/Debian are supported."
        exit 1
    fi
    
    . /etc/os-release
    OS=$ID
    OS_VERSION=$VERSION_ID
    
    case $OS in
        ubuntu)
            if [[ "${VERSION_ID%%.*}" -lt 20 ]]; then
                log_error "Ubuntu 20.04 or later required. Found: $VERSION_ID"
                exit 1
            fi
            log_success "Ubuntu $VERSION_ID detected"
            ;;
        debian)
            if [[ "${VERSION_ID%%.*}" -lt 11 ]]; then
                log_error "Debian 11 or later required. Found: $VERSION_ID"
                exit 1
            fi
            log_success "Debian $VERSION_ID detected"
            ;;
        *)
            log_error "Unsupported OS: $OS"
            log_error "Only Ubuntu 20.04+ and Debian 11+ are supported"
            exit 1
            ;;
    esac
}

check_resources() {
    log_step "Checking system resources..."
    
    # CPU cores
    local cpu_cores=$(nproc)
    if [ "$cpu_cores" -lt 2 ]; then
        log_warn "Only $cpu_cores CPU core(s) - 2+ recommended"
    else
        log_success "$cpu_cores CPU cores available"
    fi
    
    # Memory
    local mem_mb=$(free -m | awk '/^Mem:/{print $2}')
    if [ "$mem_mb" -lt 2048 ]; then
        log_warn "Only ${mem_mb}MB RAM - 2GB+ recommended"
    else
        log_success "${mem_mb}MB RAM available"
    fi
    
    # Disk space
    local disk_gb=$(df -BG / | awk 'NR==2 {print int($4)}')
    if [ "$disk_gb" -lt 20 ]; then
        log_warn "Only ${disk_gb}GB free disk space - 20GB+ recommended"
    else
        log_success "${disk_gb}GB free disk space"
    fi
}

check_network() {
    log_step "Checking network connectivity..."
    
    if ! ping -c 1 google.com &> /dev/null; then
        log_error "No internet connectivity"
        exit 1
    fi
    
    # Get public IP
    PUBLIC_IP=$(curl -s ifconfig.me 2>/dev/null || curl -s icanhazip.com 2>/dev/null || echo "unknown")
    log_success "Network OK (Public IP: $PUBLIC_IP)"
    
    # Check port availability
    log_step "Checking port availability..."
    
    # Check API port
    if ss -tlnp 2>/dev/null | grep -q ":$API_PORT "; then
        local api_port_process=$(ss -tlnp 2>/dev/null | grep ":$API_PORT " | sed -n 's/.*users:(("\([^"]*\)".*/\1/p' | head -1)
        
        if [ "$api_port_process" = "decloud-orch" ] || [ "$api_port_process" = "dotnet" ]; then
            log_warn "Orchestrator already running on port $API_PORT - will update in place"
            UPDATE_MODE=true
        else
            log_error "Port $API_PORT is already in use by '$api_port_process'"
            exit 1
        fi
    else
        log_success "Port $API_PORT is available"
    fi
    
    # Check 80/443 if Caddy is enabled
    if [ "$INSTALL_CADDY" = true ]; then
        local port80_process=""
        local port443_process=""
        
        port80_process=$(ss -tlnp 2>/dev/null | grep ":80 " | sed -n 's/.*users:(("\([^"]*\)".*/\1/p' | head -1)
        port443_process=$(ss -tlnp 2>/dev/null | grep ":443 " | sed -n 's/.*users:(("\([^"]*\)".*/\1/p' | head -1)
        
        if [ -n "$port80_process" ]; then
            if [ "$port80_process" = "caddy" ]; then
                log_success "Caddy already running on ports 80/443"
                INSTALL_CADDY=false
            else
                log_error "Port 80 is already in use by '$port80_process' (required for Caddy)"
                log_info "Disable the conflicting service or use --skip-caddy"
                exit 1
            fi
        elif [ -n "$port443_process" ] && [ "$port443_process" != "caddy" ]; then
            log_error "Port 443 is already in use by '$port443_process' (required for Caddy)"
            exit 1
        else
            log_success "Ports 80/443 available for central ingress"
        fi
    fi
}

check_mongodb() {
    log_step "Checking MongoDB configuration..."
    
    if [ -z "$MONGODB_URI" ]; then
        log_error "MongoDB URI is required"
        log_error "Use: --mongodb \"mongodb+srv://...\""
        exit 1
    fi
    
    if [[ ! "$MONGODB_URI" =~ ^mongodb(\+srv)?:// ]]; then
        log_error "Invalid MongoDB URI format"
        exit 1
    fi
    
    log_success "MongoDB URI provided"
    log_info "Connection will be verified at startup"
}

check_cloudflare_token() {
    if [ "$INSTALL_CADDY" = true ] && [ -n "$INGRESS_DOMAIN" ]; then
        if [ -z "$CLOUDFLARE_TOKEN" ]; then
            log_warn "Cloudflare token not provided - wildcard certificates will fail!"
            echo ""
            log_info "To get a Cloudflare API token:"
            log_info "1. Go to https://dash.cloudflare.com/profile/api-tokens"
            log_info "2. Click 'Create Token'"
            log_info "3. Use 'Edit zone DNS' template"
            log_info "4. Scope to your domain's zone"
            log_info "5. Create and copy the token"
            echo ""
            read -p "Enter Cloudflare API token (or press Enter to skip): " CLOUDFLARE_TOKEN
            
            if [ -z "$CLOUDFLARE_TOKEN" ]; then
                log_warn "Skipping Cloudflare token - central ingress will be disabled"
                INSTALL_CADDY=false
            fi
        fi
        
        if [ -n "$CLOUDFLARE_TOKEN" ]; then
            log_success "Cloudflare token provided"
        fi
    fi
}

# ============================================================
# Installation Functions
# ============================================================
install_dependencies() {
    log_step "Installing base dependencies..."
    
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -qq
    apt-get install -y -qq \
        curl wget git jq apt-transport-https ca-certificates \
        gnupg lsb-release software-properties-common openssl > /dev/null 2>&1
    
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
    
    # CRITICAL: Add Cloudflare token to systemd environment
    if [ -n "$CLOUDFLARE_TOKEN" ]; then
        log_info "Configuring Cloudflare API token for Caddy..."
        mkdir -p /etc/systemd/system/caddy.service.d
        
        cat > /etc/systemd/system/caddy.service.d/cloudflare.conf << EOF
[Service]
Environment="CF_API_TOKEN=${CLOUDFLARE_TOKEN}"
AmbientCapabilities=CAP_NET_BIND_SERVICE
LimitNOFILE=1048576
Restart=always
RestartSec=5
EOF
        
        chmod 600 /etc/systemd/system/caddy.service.d/cloudflare.conf
        systemctl daemon-reload
        log_success "Cloudflare token configured in systemd"
    fi
    
    # Build ACME configuration
    local acme_config=""
    if [ -n "$CADDY_EMAIL" ]; then
        acme_config="    email $CADDY_EMAIL"
    fi
    if [ "$CADDY_STAGING" = true ]; then
        acme_config="$acme_config
    acme_ca https://acme-staging-v02.api.letsencrypt.org/directory"
    fi
    
    # Wildcard domain config (Caddyfile is managed by Orchestrator via Admin API)
    # This creates a minimal Caddyfile - the real config comes from CentralCaddyManager
    cat > /etc/caddy/Caddyfile << EOF
# DeCloud Orchestrator - Central Ingress Gateway
# This file is managed by the Orchestrator via Admin API
# Do not edit manually - changes will be overwritten

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

# Health check endpoint (HTTP only, no TLS)
:8081 {
    respond /health "OK" 200
    respond /ready "OK" 200
}
EOF

    log_success "Caddy configured"
    log_info "Caddy will be dynamically managed by Orchestrator"
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
EOF

    systemctl enable fail2ban --quiet 2>/dev/null || true
    systemctl restart fail2ban 2>/dev/null || true
    
    log_success "fail2ban installed and configured"
}

# ============================================================
# Application Installation
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
        if [ "$UPDATE_MODE" = true ]; then
            log_info "Updating existing installation..."
            cd "$INSTALL_DIR/DeCloud.Orchestrator"
            git pull origin main --quiet 2>/dev/null || git pull origin master --quiet 2>/dev/null
        else
            log_success "Source already present"
            return
        fi
    else
        cd "$INSTALL_DIR"
        git clone "$REPO_URL" DeCloud.Orchestrator --quiet
    fi
    
    log_success "Orchestrator downloaded"
}

build_orchestrator() {
    log_step "Building Orchestrator..."
    
    cd "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator"
    
    # Build backend
    dotnet restore --quiet
    dotnet build --configuration Release --quiet --no-restore
    
    # Build frontend
    if [ -d "wwwroot" ] && [ -f "wwwroot/package.json" ]; then
        log_info "Building frontend..."
        cd wwwroot
        
        # Create .env file if it doesn't exist
        if [ ! -f .env ]; then
            if [ -f .env.example ]; then
                cp .env.example .env
                log_info "Created .env from .env.example"
            fi
        fi
        
        npm install --quiet --no-progress > /dev/null 2>&1
        npm run build --quiet > /dev/null 2>&1
        cd ..
        
        log_success "Frontend built"
    else
        log_warn "Frontend source not found - skipping frontend build"
    fi
    
    log_success "Orchestrator built"
}

create_configuration() {
    log_step "Creating configuration..."
    
    # Generate JWT key
    local jwt_key=$(openssl rand -base64 32)
    
    # MongoDB config
    local mongodb_config=""
    if [ -n "$MONGODB_URI" ]; then
        mongodb_config=',
  "ConnectionStrings": {
    "MongoDB": "'$MONGODB_URI'"
  }'
    fi
    
    # Central ingress config
    # CRITICAL: Include DnsProvider and DnsApiToken for CentralCaddyManager
    local central_ingress_config=""
    if [ -n "$INGRESS_DOMAIN" ]; then
        local dns_token_json="null"
        if [ -n "$CLOUDFLARE_TOKEN" ]; then
            dns_token_json="\"$CLOUDFLARE_TOKEN\""
        fi
        
        central_ingress_config=',
  "CentralIngress": {
    "Enabled": true,
    "BaseDomain": "'$INGRESS_DOMAIN'",
    "CaddyAdminUrl": "http://localhost:2019",
    "AcmeEmail": "'${CADDY_EMAIL:-admin@$INGRESS_DOMAIN}'",
    "UseAcmeStaging": '$([ "$CADDY_STAGING" = true ] && echo "true" || echo "false")',
    "DnsProvider": "'$DNS_PROVIDER'",
    "DnsApiToken": '$dns_token_json',
    "DefaultTargetPort": 80,
    "SubdomainPattern": "{name}",
    "AutoRegisterOnStart": true,
    "AutoRemoveOnStop": true,
    "ProxyTimeoutSeconds": 30,
    "EnableWebSocket": true
  }'
    fi
    
    # Create appsettings.Production.json in the Orchestrator directory
    cat > "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json" << EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "DeCloud": "Debug"
    }
  },
  "Urls": "http://0.0.0.0:${API_PORT}",
  "Jwt": {
    "Key": "${jwt_key}",
    "Issuer": "decloud-orchestrator",
    "Audience": "decloud-users",
    "ExpiryMinutes": 1440
  }${mongodb_config}${central_ingress_config}
}
EOF

    chmod 640 "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
    
    # Also create a copy in /etc/decloud for reference
    cp "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json" \
       "$CONFIG_DIR/orchestrator.appsettings.Production.json"
    
    log_success "Configuration created"
    log_info "Config file: $INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
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
StandardOutput=append:$LOG_DIR/orchestrator.log
StandardError=append:$LOG_DIR/orchestrator.log

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    log_success "Systemd service created"
}

configure_firewall() {
    log_step "Configuring firewall..."
    
    # Enable UFW if not already enabled
    if ! ufw status | grep -q "Status: active"; then
        log_info "Enabling firewall..."
        ufw --force enable > /dev/null 2>&1
    fi
    
    # Allow SSH
    ufw allow ssh > /dev/null 2>&1
    
    # Allow API port
    ufw allow $API_PORT/tcp comment "DeCloud Orchestrator API" > /dev/null 2>&1
    
    # Allow Caddy if enabled
    if [ "$INSTALL_CADDY" = true ]; then
        ufw allow 80/tcp comment "Caddy HTTP" > /dev/null 2>&1
        ufw allow 443/tcp comment "Caddy HTTPS" > /dev/null 2>&1
    fi
    
    log_success "Firewall configured"
}

start_services() {
    log_step "Starting services..."
    
    # Start Caddy first if enabled
    if [ "$INSTALL_CADDY" = true ]; then
        systemctl enable caddy --quiet 2>/dev/null || true
        systemctl restart caddy 2>/dev/null || true
        
        if systemctl is-active --quiet caddy; then
            log_success "Caddy started"
        else
            log_warn "Caddy failed to start - check: journalctl -u caddy"
        fi
    fi
    
    # Start Orchestrator
    systemctl enable decloud-orchestrator --quiet 2>/dev/null || true
    systemctl restart decloud-orchestrator
    
    # Wait for startup
    sleep 5
    
    if systemctl is-active --quiet decloud-orchestrator; then
        log_success "Orchestrator started"
    else
        log_error "Orchestrator failed to start"
        log_error "Check logs: journalctl -u decloud-orchestrator -n 50"
        exit 1
    fi
    
    # Wait a bit more for Caddy config to be applied by Orchestrator
    if [ "$INSTALL_CADDY" = true ]; then
        log_info "Waiting for Orchestrator to configure Caddy..."
        sleep 10
    fi
}

print_summary() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║       DeCloud Orchestrator - Installation Complete!         ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    echo "  Dashboard:       http://${PUBLIC_IP}:${API_PORT}/"
    echo "  API Endpoint:    http://${PUBLIC_IP}:${API_PORT}/api"
    echo "  Swagger UI:      http://${PUBLIC_IP}:${API_PORT}/swagger"
    echo ""
    
    if [ "$INSTALL_CADDY" = true ] && [ -n "$INGRESS_DOMAIN" ]; then
        echo "  ─────────────────────────────────────────────────────────────"
        echo "  Central Ingress (Caddy):"
        echo "  ─────────────────────────────────────────────────────────────"
        echo "    Wildcard Domain: *.${INGRESS_DOMAIN}"
        echo "    Example VM URL:  https://myapp.${INGRESS_DOMAIN}"
        echo "    Admin API:       http://localhost:2019"
        echo "    Health Check:    http://localhost:8081/health"
        echo ""
        if [ -n "$CLOUDFLARE_TOKEN" ]; then
            echo "    ✓ Cloudflare token configured"
            echo "    ✓ Wildcard certificates enabled"
        else
            echo "    ✗ No Cloudflare token - certificates may fail"
        fi
        echo ""
    fi
    
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Commands:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Status:        sudo systemctl status decloud-orchestrator"
    echo "    Logs:          sudo journalctl -u decloud-orchestrator -f"
    echo "    Restart:       sudo systemctl restart decloud-orchestrator"
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Caddy status:  sudo systemctl status caddy"
        echo "    Caddy logs:    sudo journalctl -u caddy -f"
        echo "    Caddy config:  curl -s http://localhost:2019/config/ | jq ."
    fi
    echo ""
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Configuration:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Main config:   $INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
    echo "    Logs:          $LOG_DIR/orchestrator.log"
    echo "    Port:          $API_PORT"
    if [ -n "$MONGODB_URI" ]; then
        echo "    MongoDB:       Connected"
    else
        echo "    MongoDB:       Not configured (using in-memory storage)"
    fi
    echo ""
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Next Steps:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    1. Open dashboard: http://${PUBLIC_IP}:${API_PORT}/"
    echo "    2. Connect your wallet to start using DeCloud"
    if [ "$INSTALL_CADDY" = true ] && [ -n "$INGRESS_DOMAIN" ]; then
        echo "    3. Configure DNS:"
        echo "       - Point *.${INGRESS_DOMAIN} to ${PUBLIC_IP}"
        echo "       - Wildcard certificates will be auto-provisioned"
    fi
    echo ""
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Troubleshooting:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Check service:  systemctl status decloud-orchestrator"
    echo "    View logs:      journalctl -u decloud-orchestrator -n 100"
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Check TLS:      curl -s http://localhost:2019/config/apps/tls | jq ."
        echo "    Test HTTPS:     curl -I https://${INGRESS_DOMAIN}/"
    fi
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
    check_cloudflare_token
    
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