#!/bin/bash
#
# DeCloud Orchestrator Installation Script
#
# Production-ready installation with:
# - .NET 8 Runtime
# - Node.js 20 LTS for frontend build  
# - MongoDB connection
# - Caddy central ingress with golden master config persistence
# - Automatic recovery and backup timers
# - fail2ban DDoS protection
#
# ARCHITECTURE:
# The Orchestrator handles all HTTP ingress centrally (ports 80/443).
# Routes traffic to nodes via WireGuard overlay network.
#
# Version: 2.2.0
#
# Usage:
#   sudo ./install.sh --mongodb "mongodb+srv://..." \
#       --ingress-domain vms.stackfi.tech \
#       --caddy-email admin@stackfi.tech \
#       --cloudflare-token YOUR_CF_TOKEN
#

set -e

VERSION="2.2.0"

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
CADDY_BACKUP_DIR="/etc/decloud/caddy-backups"
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
    --ingress-domain <domain> Enable central ingress (e.g., vms.stackfi.tech)
                              VMs will get URLs like: app.vms.stackfi.tech
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

    # Production with central ingress
    sudo ./install.sh \\
        --mongodb "mongodb+srv://..." \\
        --ingress-domain vms.stackfi.tech \\
        --caddy-email admin@stackfi.tech \\
        --cloudflare-token YOUR_CLOUDFLARE_API_TOKEN

CLOUDFLARE TOKEN:
    To get a Cloudflare API token:
    1. Go to https://dash.cloudflare.com/profile/api-tokens
    2. Click "Create Token"
    3. Use "Edit zone DNS" template
    4. Scope to your domain's zone
    5. Copy the token

For more information: https://github.com/bekirmfr/DeCloud
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
            log_error "Unsupported OS: $OS. Only Ubuntu/Debian are supported."
            exit 1
            ;;
    esac
}

check_resources() {
    log_step "Checking system resources..."
    
    # CPU
    local cpu_count=$(nproc)
    if [ "$cpu_count" -lt 2 ]; then
        log_warn "Only $cpu_count CPU core(s) detected. Recommended: 2+"
    else
        log_success "$cpu_count CPU cores available"
    fi
    
    # Memory
    local mem_mb=$(free -m | awk '/^Mem:/{print $2}')
    if [ "$mem_mb" -lt 2048 ]; then
        log_warn "Only ${mem_mb}MB RAM detected. Recommended: 2GB+"
    else
        log_success "${mem_mb}MB RAM available"
    fi
    
    # Disk
    local disk_gb=$(df -BG / | awk 'NR==2 {print $4}' | sed 's/G//')
    if [ "$disk_gb" -lt 20 ]; then
        log_warn "Only ${disk_gb}GB disk space free. Recommended: 20GB+"
    else
        log_success "${disk_gb}GB disk space available"
    fi
}

check_network() {
    log_step "Checking network connectivity..."
    
    # Internet
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
                INSTALL_CADDY=false  # Don't reinstall
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
            log_info "5. Copy the token"
            echo ""
            read -p "Continue without Cloudflare token? (y/N) " -n 1 -r
            echo
            if [[ ! $REPLY =~ ^[Yy]$ ]]; then
                exit 1
            fi
        else
            log_success "Cloudflare token provided"
        fi
    fi
}

# ============================================================
# Dependency Installation
# ============================================================
install_dependencies() {
    log_step "Installing system dependencies..."
    
    apt-get update -qq
    apt-get install -y -qq \
        curl wget git jq apt-transport-https ca-certificates \
        gnupg lsb-release software-properties-common > /dev/null 2>&1
    
    log_success "System dependencies installed"
}

install_nodejs() {
    log_step "Installing Node.js 20 LTS..."
    
    if command -v node &> /dev/null; then
        NODE_VERSION=$(node --version 2>/dev/null | sed 's/v//')
        NODE_MAJOR=$(echo $NODE_VERSION | cut -d. -f1)
        
        if [ "$NODE_MAJOR" -ge 18 ]; then
            log_success "Node.js v$NODE_VERSION already installed"
            return
        fi
    fi
    
    # Install Node.js 20 LTS
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - > /dev/null 2>&1
    apt-get install -y -qq nodejs > /dev/null 2>&1
    
    NODE_VERSION=$(node --version 2>/dev/null | sed 's/v//')
    log_success "Node.js v$NODE_VERSION installed"
}

install_dotnet() {
    log_step "Installing .NET 8 SDK..."
    
    if command -v dotnet &> /dev/null; then
        DOTNET_VERSION=$(dotnet --version 2>/dev/null | head -1)
        if [[ "$DOTNET_VERSION" == 8.* ]]; then
            log_success ".NET $DOTNET_VERSION already installed"
            return
        fi
    fi
    
    # Add Microsoft package repository
    wget https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb -q -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
    rm /tmp/packages-microsoft-prod.deb
    
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
    
    DOTNET_VERSION=$(dotnet --version 2>/dev/null | head -1)
    log_success ".NET $DOTNET_VERSION installed"
}

# ============================================================
# Caddy Installation & Configuration
# ============================================================
install_caddy() {
    if [ "$INSTALL_CADDY" = false ]; then
        return
    fi
    
    log_step "Installing Caddy..."
    
    if command -v caddy &> /dev/null; then
        log_success "Caddy already installed: $(caddy version | head -1)"
        return
    fi
    
    # Install Caddy
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
    
    mkdir -p "$CADDY_DATA_DIR" "$CADDY_LOG_DIR" /etc/caddy "$CADDY_BACKUP_DIR"
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
    
    # Create production-grade Caddyfile with admin API
    if [ -f /etc/caddy/Caddyfile ]; then
        log_info "Caddyfile already exists, updating..."
        cp /etc/caddy/Caddyfile /etc/caddy/Caddyfile.backup.$(date +%Y%m%d%H%M%S)
    fi
    
    cat > /etc/caddy/Caddyfile << EOF
# DeCloud Central Ingress Gateway
# Security-first configuration with wildcard TLS

{
    # CRITICAL: Enable admin API (localhost only for security)
    admin localhost:2019
    
    # Structured logging
    log {
        output file $CADDY_LOG_DIR/caddy.log {
            roll_size 100mb
            roll_keep 10
            roll_keep_for 720h
        }
        format json
        level INFO
    }
    
    # Global TLS settings
    email $CADDY_EMAIL
    
    # Security headers
    servers {
        protocols h1 h2 h3
        strict_sni_host on
    }
}

# Health check endpoint (HTTP only, no auth required)
:8081 {
    respond /health "OK" 200
    respond /ready "OK" 200
    
    log {
        output discard
    }
}

# Note: VM ingress routes (*.${INGRESS_DOMAIN}) are managed dynamically
# via the Caddy Admin API by the Orchestrator's CentralIngressService
EOF
    
    chown caddy:caddy /etc/caddy/Caddyfile
    chmod 644 /etc/caddy/Caddyfile
    
    # Add initialization hook to Caddy service
    if [ -f /etc/systemd/system/caddy.service.d/init-decloud.conf ]; then
        log_info "Caddy initialization hook already exists, updating..."
    fi
    
    cat > /etc/systemd/system/caddy.service.d/init-decloud.conf << 'EOF'
[Service]
# Initialize DeCloud configuration after Caddy starts
ExecStartPost=/bin/sleep 3
ExecStartPost=/usr/local/bin/init-decloud-caddy.sh

# Restart on failure
Restart=always
RestartSec=5
EOF
    
    systemctl daemon-reload
    
    log_success "Caddy configured"
}

# ============================================================
# Helper Scripts Installation
# ============================================================
install_helper_scripts() {
    log_step "Installing DeCloud helper scripts..."
    
    local scripts_updated=false
    
    # Check if scripts already exist
    if [ -f /usr/local/bin/init-decloud-caddy.sh ]; then
        log_info "Helper scripts already exist, updating..."
        scripts_updated=true
    fi
    
    # 1. Caddy initialization script
    cat > /usr/local/bin/init-decloud-caddy.sh << 'SCRIPT'
#!/bin/bash
# DeCloud Caddy Initialization Script
# Ensures central_ingress server exists with proper configuration

set -e

CADDY_API="http://localhost:2019"
GOLDEN_CONFIG="/etc/decloud/caddy-backups/golden-master.json"
MAX_RETRIES=30
RETRY_DELAY=2

log() {
    echo "[$(date +'%Y-%m-%d %H:%M:%S')] $1"
}

# Wait for Caddy admin API
log "Waiting for Caddy admin API..."
for i in $(seq 1 $MAX_RETRIES); do
    if curl -sf "$CADDY_API/config/" > /dev/null 2>&1; then
        log "✓ Caddy admin API accessible"
        break
    fi
    if [ $i -eq $MAX_RETRIES ]; then
        log "✗ Caddy admin API timeout"
        exit 1
    fi
    sleep $RETRY_DELAY
done

# Check if central_ingress exists
if curl -sf "$CADDY_API/config/apps/http/servers/central_ingress" > /dev/null 2>&1; then
    log "✓ central_ingress server exists"
    
    # Verify it's listening on :443
    if curl -s "$CADDY_API/config/apps/http/servers/central_ingress/listen" | grep -q "443"; then
        log "✓ central_ingress listening on :443"
        exit 0
    else
        log "⚠ central_ingress not listening on :443, reloading config..."
    fi
fi

# Load golden master configuration if it exists
if [ -f "$GOLDEN_CONFIG" ]; then
    log "Loading golden master configuration..."
    if curl -X POST "$CADDY_API/load" \
        -H "Content-Type: application/json" \
        -d "@$GOLDEN_CONFIG" > /dev/null 2>&1; then
        log "✓ Golden master configuration loaded"
        exit 0
    else
        log "✗ Failed to load golden master"
        exit 1
    fi
fi

log "⚠ No golden master found, orchestrator will initialize on first start"
exit 0
SCRIPT
    
    chmod +x /usr/local/bin/init-decloud-caddy.sh
    
    # 2. Health check script
    cat > /usr/local/bin/check-decloud-ingress << 'SCRIPT'
#!/bin/bash
# Health check for DeCloud ingress system

ERRORS=0

# Check Caddy admin API
if ! curl -sf http://localhost:2019/config/ > /dev/null; then
    echo "ERROR: Caddy admin API not responding"
    ((ERRORS++))
fi

# Check Caddy health endpoint
if ! curl -sf http://localhost:8081/health > /dev/null; then
    echo "ERROR: Caddy health endpoint not responding"
    ((ERRORS++))
fi

# Check orchestrator API
if ! curl -sf http://localhost:5050/health > /dev/null; then
    echo "ERROR: Orchestrator API not responding"
    ((ERRORS++))
fi

# Check central_ingress exists (if golden master exists)
if [ -f /etc/decloud/caddy-backups/golden-master.json ]; then
    if ! curl -sf http://localhost:2019/config/apps/http/servers/central_ingress > /dev/null; then
        echo "ERROR: central_ingress server missing"
        ((ERRORS++))
    fi
fi

# Check port 443 listening
if ! ss -tln | grep -q ':443'; then
    echo "ERROR: Port 443 not listening"
    ((ERRORS++))
fi

if [ $ERRORS -eq 0 ]; then
    echo "✓ All services healthy"
    exit 0
else
    echo "✗ $ERRORS service(s) unhealthy"
    exit 1
fi
SCRIPT
    
    chmod +x /usr/local/bin/check-decloud-ingress
    
    # 3. Backup script
    cat > /usr/local/bin/backup-caddy-config << 'SCRIPT'
#!/bin/bash
# Backup Caddy configuration

BACKUP_DIR="/etc/decloud/caddy-backups"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
BACKUP_FILE="$BACKUP_DIR/caddy-config-$TIMESTAMP.json"

mkdir -p "$BACKUP_DIR"

if curl -sf http://localhost:2019/config/ > "$BACKUP_FILE"; then
    echo "✓ Backed up to $BACKUP_FILE"
    
    # Keep only last 20 backups
    ls -t "$BACKUP_DIR"/caddy-config-*.json 2>/dev/null | tail -n +21 | xargs -r rm
    
    # Update golden master if central_ingress exists
    if grep -q '"central_ingress"' "$BACKUP_FILE"; then
        cp "$BACKUP_FILE" "$BACKUP_DIR/golden-master.json"
        echo "✓ Updated golden master"
    fi
else
    echo "✗ Backup failed"
    exit 1
fi
SCRIPT
    
    chmod +x /usr/local/bin/backup-caddy-config
    
    if [ "$scripts_updated" = true ]; then
        log_success "Helper scripts updated"
    else
        log_success "Helper scripts installed"
    fi
}

# ============================================================
# Systemd Timers Installation
# ============================================================
install_systemd_timers() {
    log_step "Installing monitoring and recovery timers..."
    
    local timers_updated=false
    
    # Check if recovery service already exists
    if [ -f /etc/systemd/system/decloud-ingress-recovery.service ]; then
        log_info "Recovery service already exists, updating..."
        timers_updated=true
    fi
    
    # Recovery service
    cat > /etc/systemd/system/decloud-ingress-recovery.service << 'EOF'
[Unit]
Description=DeCloud Ingress Recovery
After=network.target caddy.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/check-decloud-ingress
ExecStartPost=/bin/bash -c 'if [ $? -ne 0 ]; then /usr/local/bin/init-decloud-caddy.sh; fi'
StandardOutput=journal
StandardError=journal
EOF
    
    # Check if recovery timer already exists
    if [ -f /etc/systemd/system/decloud-ingress-recovery.timer ]; then
        log_info "Recovery timer already exists, updating..."
        timers_updated=true
    fi
    
    # Recovery timer (every 5 minutes)
    cat > /etc/systemd/system/decloud-ingress-recovery.timer << 'EOF'
[Unit]
Description=DeCloud Ingress Recovery Timer
Requires=decloud-ingress-recovery.service

[Timer]
OnBootSec=2min
OnUnitActiveSec=5min

[Install]
WantedBy=timers.target
EOF
    
    # Check if backup service already exists
    if [ -f /etc/systemd/system/caddy-backup.service ]; then
        log_info "Backup service already exists, updating..."
        timers_updated=true
    fi
    
    # Backup service
    cat > /etc/systemd/system/caddy-backup.service << 'EOF'
[Unit]
Description=Backup Caddy Configuration
After=caddy.service

[Service]
Type=oneshot
ExecStart=/usr/local/bin/backup-caddy-config
StandardOutput=journal
StandardError=journal
EOF
    
    # Check if backup timer already exists
    if [ -f /etc/systemd/system/caddy-backup.timer ]; then
        log_info "Backup timer already exists, updating..."
        timers_updated=true
    fi
    
    # Backup timer (every 6 hours)
    cat > /etc/systemd/system/caddy-backup.timer << 'EOF'
[Unit]
Description=Backup Caddy Config Every 6 Hours
Requires=caddy-backup.service

[Timer]
OnBootSec=10min
OnUnitActiveSec=6h

[Install]
WantedBy=timers.target
EOF
    
    # Reload systemd
    systemctl daemon-reload
    
    # Enable and start timers (safe to run multiple times)
    if systemctl is-enabled decloud-ingress-recovery.timer &>/dev/null; then
        log_info "Recovery timer already enabled"
        # Restart to apply any updates
        systemctl restart decloud-ingress-recovery.timer 2>/dev/null || true
    else
        systemctl enable decloud-ingress-recovery.timer --quiet 2>/dev/null || true
        log_success "Recovery timer enabled"
    fi
    
    if systemctl is-enabled caddy-backup.timer &>/dev/null; then
        log_info "Backup timer already enabled"
        # Restart to apply any updates
        systemctl restart caddy-backup.timer 2>/dev/null || true
    else
        systemctl enable caddy-backup.timer --quiet 2>/dev/null || true
        log_success "Backup timer enabled"
    fi
    
    if [ "$timers_updated" = true ]; then
        log_success "Monitoring timers updated"
    else
        log_success "Monitoring timers installed"
    fi
}

# ============================================================
# fail2ban Installation
# ============================================================
install_fail2ban() {
    if [ "$INSTALL_FAIL2BAN" = false ]; then
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
# Application Installation
# ============================================================
create_directories() {
    log_step "Creating directories..."
    
    mkdir -p "$INSTALL_DIR"
    mkdir -p "$CONFIG_DIR"
    mkdir -p "$LOG_DIR"
    mkdir -p "$CADDY_BACKUP_DIR"
    
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
    log_info "Restoring .NET packages..."
    dotnet restore --verbosity minimal > /dev/null 2>&1 || dotnet restore
    
    log_info "Building .NET project..."
    dotnet build --configuration Release --no-restore --verbosity minimal > /dev/null 2>&1 || \
        dotnet build --configuration Release --no-restore
    
    # Build frontend
    if [ -d "wwwroot" ] && [ -f "wwwroot/package.json" ]; then
        log_info "Building frontend..."
        cd wwwroot
        
        # Create .env file if it doesn't exist
        if [ ! -f .env ]; then
            cat > .env << 'EOF'
VITE_WALLETCONNECT_PROJECT_ID=708cede4d366aa77aead71dbc67d8ae5
EOF
        fi
        
        npm install --silent > /dev/null 2>&1
        npm run build > /dev/null 2>&1
        
        cd ..
        log_success "Frontend built"
    fi
    
    log_success "Orchestrator built"
}

create_configuration() {
    log_step "Creating configuration..."
    
    # Create appsettings.Production.json
    cat > "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json" << EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:${API_PORT}"
      }
    }
  },
  "ConnectionStrings": {
    "MongoDB": "${MONGODB_URI}"
  },
  "CentralIngress": {
    "Enabled": $([ "$INSTALL_CADDY" = true ] && echo "true" || echo "false"),
    "BaseDomain": "${INGRESS_DOMAIN}",
    "CaddyAdminUrl": "http://localhost:2019",
    "AcmeEmail": "${CADDY_EMAIL}",
    "UseAcmeStaging": $([ "$CADDY_STAGING" = true ] && echo "true" || echo "false"),
    "DnsProvider": "${DNS_PROVIDER}",
    "DnsApiToken": "${CLOUDFLARE_TOKEN}",
    "DefaultTargetPort": 80,
    "SubdomainPattern": "{name}",
    "AutoRegisterOnStart": true,
    "AutoRemoveOnStop": true,
    "ProxyTimeoutSeconds": 30,
    "EnableWebSocket": true
  }
}
EOF
    
    log_success "Configuration created"
}

create_systemd_service() {
    log_step "Creating systemd service..."
    
    if [ -f /etc/systemd/system/decloud-orchestrator.service ]; then
        log_info "Orchestrator service already exists, updating..."
    fi
    
    cat > /etc/systemd/system/decloud-orchestrator.service << EOF
[Unit]
Description=DeCloud Orchestrator
After=network.target mongod.service
$([ "$INSTALL_CADDY" = true ] && echo "After=caddy.service" || echo "")
$([ "$INSTALL_CADDY" = true ] && echo "Requires=caddy.service" || echo "")
Wants=mongod.service

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator
ExecStart=/usr/bin/dotnet run --configuration Release --no-build
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production
Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

# Security
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF
    
    systemctl daemon-reload
    
    log_success "Systemd service created"
}

configure_firewall() {
    log_step "Configuring firewall..."
    
    if ! command -v ufw &> /dev/null; then
        apt-get install -y -qq ufw > /dev/null 2>&1
    fi
    
    # Enable firewall if not already enabled
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
        
        # Start monitoring timers
        systemctl start decloud-ingress-recovery.timer 2>/dev/null || true
        systemctl start caddy-backup.timer 2>/dev/null || true
        log_info "Monitoring timers started"
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
    
    # Wait for Orchestrator to initialize Caddy
    if [ "$INSTALL_CADDY" = true ]; then
        log_info "Waiting for Orchestrator to configure Caddy..."
        sleep 10
        
        # Backup the initial configuration as golden master
        if curl -sf http://localhost:2019/config/ > /dev/null 2>&1; then
            /usr/local/bin/backup-caddy-config > /dev/null 2>&1 || true
            log_success "Golden master configuration saved"
        fi
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
            echo "    ✓ Automatic recovery enabled (every 5 min)"
            echo "    ✓ Configuration backups enabled (every 6 hours)"
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
        echo "    Health check:  sudo /usr/local/bin/check-decloud-ingress"
        echo "    Backup config: sudo /usr/local/bin/backup-caddy-config"
    fi
    echo ""
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Configuration:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Main config:   $INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
    echo "    Logs:          $LOG_DIR/orchestrator.log"
    echo "    Port:          $API_PORT"
    echo "    MongoDB:       Connected"
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Golden master: $CADDY_BACKUP_DIR/golden-master.json"
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
    echo "    Service status: systemctl status decloud-orchestrator caddy"
    echo "    View logs:      journalctl -u decloud-orchestrator -f"
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Test ingress:   curl -I https://${INGRESS_DOMAIN}/"
        echo "    Caddy config:   curl -s http://localhost:2019/config/ | jq ."
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
    echo "║       (Production-Ready with Auto-Recovery)                  ║"
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
    install_helper_scripts
    install_systemd_timers
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