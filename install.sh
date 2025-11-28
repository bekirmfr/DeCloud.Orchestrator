#!/bin/bash
#
# DeCloud Orchestrator Installation Script
#
# Installs the DeCloud Orchestrator - the central coordination service
# for managing nodes, VMs, and user requests.
#
# Usage:
#   curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.Orchestrator/main/install.sh | sudo bash
#
# Or with options:
#   sudo ./install.sh --port 5050 --mongodb "mongodb+srv://..."
#

set -e

VERSION="1.1.0"

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
# Default Configuration
# ============================================================
INSTALL_DIR="/opt/decloud"
CONFIG_DIR="/etc/decloud"
REPO_URL="https://github.com/bekirmfr/DeCloud.Orchestrator.git"
SERVICE_NAME="decloud-orchestrator"

# Settings (can be overridden via arguments)
PORT=5050
MONGODB_URI=""
JWT_SECRET=""
SKIP_MONGODB_CHECK=false

# ============================================================
# Argument Parsing
# ============================================================
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --port)
                PORT="$2"
                shift 2
                ;;
            --mongodb)
                MONGODB_URI="$2"
                shift 2
                ;;
            --jwt-secret)
                JWT_SECRET="$2"
                shift 2
                ;;
            --skip-mongodb-check)
                SKIP_MONGODB_CHECK=true
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

Options:
  --port <port>              API port (default: 5050)
  --mongodb <uri>            MongoDB connection string (optional, uses in-memory if not set)
  --jwt-secret <secret>      JWT signing key (default: auto-generated)
  --skip-mongodb-check       Skip MongoDB connectivity check
  --help, -h                 Show this help message

Examples:
  $0
  $0 --port 5050
  $0 --mongodb "mongodb+srv://user:pass@cluster.mongodb.net/decloud"
  
Environment Variables:
  MONGODB_URI                MongoDB connection string
  JWT_SECRET                 JWT signing key
EOF
}

# ============================================================
# System Checks
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
        log_error "Cannot detect OS. This script requires Ubuntu 20.04+ or Debian 11+"
        exit 1
    fi
    
    . /etc/os-release
    OS=$ID
    OS_VERSION=$VERSION_ID
    
    case $OS in
        ubuntu)
            if [ "${OS_VERSION%%.*}" -lt 20 ]; then
                log_error "Ubuntu 20.04 or later required (found: $OS_VERSION)"
                exit 1
            fi
            ;;
        debian)
            if [ "${OS_VERSION%%.*}" -lt 11 ]; then
                log_error "Debian 11 or later required (found: $OS_VERSION)"
                exit 1
            fi
            ;;
        *)
            log_warn "Untested OS: $OS $OS_VERSION (continuing anyway)"
            ;;
    esac
    
    log_success "OS: $OS $OS_VERSION"
}

check_resources() {
    log_step "Checking system resources..."
    
    # CPU cores
    CPU_CORES=$(nproc)
    if [ "$CPU_CORES" -lt 1 ]; then
        log_error "At least 1 CPU core required"
        exit 1
    fi
    log_success "CPU cores: $CPU_CORES"
    
    # Memory (need at least 512MB)
    TOTAL_MEM=$(free -m | awk '/^Mem:/{print $2}')
    if [ "$TOTAL_MEM" -lt 512 ]; then
        log_error "At least 512MB RAM required (found: ${TOTAL_MEM}MB)"
        exit 1
    fi
    log_success "Memory: ${TOTAL_MEM}MB"
    
    # Disk space (need at least 2GB)
    FREE_DISK=$(df -BG / | awk 'NR==2 {print $4}' | sed 's/G//')
    if [ "$FREE_DISK" -lt 2 ]; then
        log_error "At least 2GB free disk space required (found: ${FREE_DISK}GB)"
        exit 1
    fi
    log_success "Free disk: ${FREE_DISK}GB"
}

check_network() {
    log_step "Checking network connectivity..."
    
    # Internet connectivity
    if ! curl -s --max-time 10 https://api.github.com > /dev/null 2>&1; then
        log_error "No internet connectivity (cannot reach GitHub)"
        exit 1
    fi
    log_success "Internet connectivity OK"
    
    # Get public IP
    PUBLIC_IP=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || \
                curl -s --max-time 5 https://ifconfig.me 2>/dev/null || \
                hostname -I | awk '{print $1}')
    
    if [ -z "$PUBLIC_IP" ]; then
        log_warn "Could not detect public IP"
        PUBLIC_IP=$(hostname -I | awk '{print $1}')
    fi
    log_success "Public IP: $PUBLIC_IP"
    
    # Check if port is available
    if ss -tuln | grep -q ":${PORT} "; then
        log_warn "Port $PORT is already in use"
        log_info "Finding available port..."
        
        local new_port=$((PORT + 1))
        while ss -tuln | grep -q ":${new_port} " && [ $new_port -lt $((PORT + 100)) ]; do
            ((new_port++))
        done
        
        if [ $new_port -lt $((PORT + 100)) ]; then
            PORT=$new_port
            log_success "Using available port: $PORT"
        else
            log_error "Could not find available port"
            exit 1
        fi
    else
        log_success "Port $PORT is available"
    fi
}

check_mongodb() {
    if [ -n "$MONGODB_URI" ] && [ "$SKIP_MONGODB_CHECK" = false ]; then
        log_step "Checking MongoDB connectivity..."
        
        # Basic URI validation
        if [[ ! "$MONGODB_URI" =~ ^mongodb(\+srv)?:// ]]; then
            log_error "Invalid MongoDB URI format"
            exit 1
        fi
        
        log_success "MongoDB URI provided"
        log_info "Connection will be verified at startup"
    else
        log_info "No MongoDB URI provided - using in-memory storage"
        log_warn "Data will not persist across restarts!"
    fi
}

# ============================================================
# Installation Functions
# ============================================================
install_dependencies() {
    log_step "Installing dependencies..."
    
    apt-get update -qq
    apt-get install -y -qq curl wget git jq apt-transport-https ca-certificates gnupg lsb-release > /dev/null 2>&1
    
    log_success "Base dependencies installed"
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
    
    # Add Microsoft repository
    if [ ! -f /etc/apt/sources.list.d/microsoft-prod.list ]; then
        wget -q https://packages.microsoft.com/config/$OS/$OS_VERSION/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
        dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
        rm /tmp/packages-microsoft-prod.deb
        apt-get update -qq
    fi
    
    apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
    
    log_success ".NET 8 SDK installed: $(dotnet --version)"
}

create_directories() {
    log_step "Creating directories..."
    
    mkdir -p "$INSTALL_DIR"
    mkdir -p "$CONFIG_DIR"
    mkdir -p /var/log/decloud
    
    log_success "Directories created"
}

download_orchestrator() {
    log_step "Downloading Orchestrator..."
    
    cd "$INSTALL_DIR"
    
    if [ -d "DeCloud.Orchestrator" ]; then
        log_info "Updating existing installation..."
        cd DeCloud.Orchestrator
        git fetch --quiet origin
        git reset --hard origin/master 2>/dev/null || git reset --hard origin/main 2>/dev/null
    else
        git clone --quiet "$REPO_URL"
        cd DeCloud.Orchestrator
    fi
    
    log_success "Orchestrator downloaded"
}

build_orchestrator() {
    log_step "Building Orchestrator..."
    
    cd "$INSTALL_DIR/DeCloud.Orchestrator"
    
    dotnet restore --verbosity quiet
    dotnet build -c Release --verbosity quiet
    
    log_success "Orchestrator built"
}

create_configuration() {
    log_step "Creating configuration..."
    
    # Generate JWT secret if not provided
    if [ -z "$JWT_SECRET" ]; then
        JWT_SECRET=$(openssl rand -base64 32)
    fi
    
    # MongoDB section
    local mongodb_config=""
    if [ -n "$MONGODB_URI" ]; then
        mongodb_config="\"ConnectionStrings\": {
    \"MongoDB\": \"$MONGODB_URI\"
  },"
    fi
    
    cat > "$CONFIG_DIR/orchestrator.appsettings.Production.json" << EOF
{
  $mongodb_config
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "Microsoft": "Warning",
        "System": "Warning"
      }
    },
    "WriteTo": [
      {
        "Name": "Console"
      },
      {
        "Name": "File",
        "Args": {
          "path": "/var/log/decloud/orchestrator-.log",
          "rollingInterval": "Day",
          "retainedFileCountLimit": 7
        }
      }
    ]
  },
  "Jwt": {
    "Key": "$JWT_SECRET",
    "Issuer": "decloud-orchestrator",
    "Audience": "decloud-client",
    "ExpiryMinutes": 1440
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:$PORT"
      }
    }
  }
}
EOF

    # Symlink to project directory
    ln -sf "$CONFIG_DIR/orchestrator.appsettings.Production.json" \
        "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
    
    log_success "Configuration created"
}

create_systemd_service() {
    log_step "Creating systemd service..."
    
    cat > /etc/systemd/system/$SERVICE_NAME.service << EOF
[Unit]
Description=DeCloud Orchestrator - Decentralized Cloud Coordination Service
Documentation=https://github.com/bekirmfr/DeCloud.Orchestrator
After=network.target
Wants=network-online.target

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR/DeCloud.Orchestrator
ExecStart=/usr/bin/dotnet run --project src/Orchestrator -c Release --no-build --environment Production
Restart=always
RestartSec=10

# Environment
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1

# Logging
StandardOutput=journal
StandardError=journal
SyslogIdentifier=$SERVICE_NAME

# Security hardening
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/var/log/decloud $INSTALL_DIR $CONFIG_DIR

[Install]
WantedBy=multi-user.target
EOF

    systemctl daemon-reload
    systemctl enable $SERVICE_NAME --quiet
    
    log_success "Systemd service created"
}

configure_firewall() {
    log_step "Configuring firewall..."
    
    # UFW
    if command -v ufw &> /dev/null; then
        ufw allow $PORT/tcp > /dev/null 2>&1 || true
    fi
    
    # iptables fallback
    iptables -C INPUT -p tcp --dport $PORT -j ACCEPT 2>/dev/null || \
        iptables -I INPUT 1 -p tcp --dport $PORT -j ACCEPT 2>/dev/null || true
    
    log_success "Firewall configured"
}

create_cli_tool() {
    log_step "Installing DeCloud CLI..."
    
    # Download CLI if not bundled
    if [ -f "$INSTALL_DIR/DeCloud.Orchestrator/decloud.sh" ]; then
        cp "$INSTALL_DIR/DeCloud.Orchestrator/decloud.sh" /usr/local/bin/decloud
    else
        cat > /usr/local/bin/decloud << 'EOF'
#!/bin/bash
# DeCloud CLI - See 'decloud help' for usage
ORCHESTRATOR_URL="${DECLOUD_ORCHESTRATOR_URL:-http://localhost:5050}"
CONFIG_FILE="$HOME/.decloud/config"
[ -f "$CONFIG_FILE" ] && source "$CONFIG_FILE"

case "${1:-help}" in
    status)
        curl -s "$ORCHESTRATOR_URL/api/system/stats" | jq .
        ;;
    nodes)
        curl -s "$ORCHESTRATOR_URL/api/nodes" | jq '.data[] | {id: .id[0:8], name, status, ip: .publicIp}'
        ;;
    vms)
        curl -s "$ORCHESTRATOR_URL/api/vms" | jq '.data.items[] | {id: .id[0:8], name, status}'
        ;;
    help|*)
        echo "DeCloud CLI - Quick Commands"
        echo ""
        echo "Usage: decloud <command>"
        echo ""
        echo "Commands:"
        echo "  status    Show system status"
        echo "  nodes     List nodes"
        echo "  vms       List VMs"
        echo ""
        echo "For full CLI, download from:"
        echo "  https://raw.githubusercontent.com/bekirmfr/DeCloud.Orchestrator/main/decloud.sh"
        ;;
esac
EOF
    fi
    
    chmod +x /usr/local/bin/decloud
    
    log_success "CLI installed: decloud"
}

start_service() {
    log_step "Starting Orchestrator..."
    
    systemctl start $SERVICE_NAME
    
    # Wait for service to be ready
    local max_attempts=15
    local attempt=1
    
    while [ $attempt -le $max_attempts ]; do
        if curl -s --max-time 2 "http://localhost:$PORT/health" > /dev/null 2>&1; then
            log_success "Orchestrator is running"
            return 0
        fi
        
        sleep 2
        ((attempt++))
    done
    
    if systemctl is-active --quiet $SERVICE_NAME; then
        log_success "Orchestrator is running (health check pending)"
    else
        log_error "Orchestrator failed to start"
        log_error "Check logs: journalctl -u $SERVICE_NAME -n 50"
        exit 1
    fi
}

# ============================================================
# Main Installation
# ============================================================
main() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║       DeCloud Orchestrator Installer v${VERSION}                  ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    parse_args "$@"
    
    # Environment variable overrides
    MONGODB_URI="${MONGODB_URI:-$MONGODB_URI}"
    JWT_SECRET="${JWT_SECRET:-$JWT_SECRET}"
    
    check_root
    check_os
    check_resources
    check_network
    check_mongodb
    
    echo ""
    log_info "All requirements met. Starting installation..."
    echo ""
    
    install_dependencies
    install_dotnet
    create_directories
    download_orchestrator
    build_orchestrator
    create_configuration
    create_systemd_service
    configure_firewall
    create_cli_tool
    start_service
    
    # ============================================================
    # Summary
    # ============================================================
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║              Installation Complete!                          ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    log_success "DeCloud Orchestrator v${VERSION} installed successfully!"
    echo ""
    echo "  API Endpoint:    http://${PUBLIC_IP}:${PORT}"
    echo "  Dashboard:       http://${PUBLIC_IP}:${PORT}/"
    echo "  Swagger UI:      http://${PUBLIC_IP}:${PORT}/swagger"
    echo "  Health Check:    http://${PUBLIC_IP}:${PORT}/health"
    echo ""
    if [ -n "$MONGODB_URI" ]; then
        echo "  Database:        MongoDB (persistent)"
    else
        echo "  Database:        In-memory (non-persistent)"
        echo ""
        echo -e "  ${YELLOW}Warning: Data will be lost on restart!${NC}"
        echo "  Add --mongodb to use persistent storage"
    fi
    echo ""
    echo "─────────────────────────────────────────────────────────────────"
    echo ""
    echo "Useful commands:"
    echo "  Status:          sudo systemctl status $SERVICE_NAME"
    echo "  Logs:            sudo journalctl -u $SERVICE_NAME -f"
    echo "  Restart:         sudo systemctl restart $SERVICE_NAME"
    echo "  Update:          sudo ./update.sh"
    echo "  CLI:             decloud status"
    echo ""
    echo "Configuration:     $CONFIG_DIR/orchestrator.appsettings.Production.json"
    echo "Logs directory:    /var/log/decloud/"
    echo ""
    echo "─────────────────────────────────────────────────────────────────"
    echo ""
    echo "To add a node, run on the node server:"
    echo ""
    echo "  curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.NodeAgent/main/install.sh | \\"
    echo "    sudo bash -s -- --orchestrator http://${PUBLIC_IP}:${PORT}"
    echo ""
}

main "$@"