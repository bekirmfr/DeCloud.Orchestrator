#!/bin/bash
#
# DeCloud Orchestrator Installation Script
# Usage: curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.Orchestrator/main/install.sh | sudo bash
#
# Or download and run:
#   chmod +x install.sh
#   sudo ./install.sh --port 5050
#

set -e

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

log_info() { echo -e "${BLUE}[INFO]${NC} $1"; }
log_success() { echo -e "${GREEN}[✓]${NC} $1"; }
log_warn() { echo -e "${YELLOW}[!]${NC} $1"; }
log_error() { echo -e "${RED}[✗]${NC} $1"; }

# Default values
INSTALL_DIR="/opt/decloud"
CONFIG_DIR="/etc/decloud"
PORT=5050
JWT_SECRET=$(openssl rand -base64 32)

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --port)
            PORT="$2"
            shift 2
            ;;
        --jwt-secret)
            JWT_SECRET="$2"
            shift 2
            ;;
        --help)
            echo "DeCloud Orchestrator Installer"
            echo ""
            echo "Usage: $0 [options]"
            echo ""
            echo "Optional:"
            echo "  --port <port>          API port (default: 5050)"
            echo "  --jwt-secret <secret>  JWT signing key (default: auto-generated)"
            echo ""
            exit 0
            ;;
        *)
            log_error "Unknown option: $1"
            exit 1
            ;;
    esac
done

echo ""
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║           DeCloud Orchestrator Installer v1.0.0              ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo ""

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    log_error "Please run as root (sudo)"
    exit 1
fi

# Detect OS
if [ -f /etc/os-release ]; then
    . /etc/os-release
    OS=$ID
    VERSION=$VERSION_ID
else
    log_error "Cannot detect OS"
    exit 1
fi

log_info "Detected OS: $OS $VERSION"
log_info "Port: $PORT"

# ============================================================
# Step 1: Install dependencies
# ============================================================
log_info "Installing dependencies..."

apt-get update -qq
apt-get install -y -qq curl wget git jq > /dev/null 2>&1

# Install .NET 8
if ! command -v dotnet &> /dev/null; then
    log_info "Installing .NET 8 SDK..."
    wget -q https://packages.microsoft.com/config/$OS/$VERSION/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
    dpkg -i /tmp/packages-microsoft-prod.deb > /dev/null 2>&1
    rm /tmp/packages-microsoft-prod.deb
    apt-get update -qq
    apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
    log_success ".NET 8 SDK installed"
else
    log_success ".NET already installed: $(dotnet --version)"
fi

# ============================================================
# Step 2: Create directories
# ============================================================
log_info "Creating directories..."

mkdir -p $INSTALL_DIR
mkdir -p $CONFIG_DIR

# ============================================================
# Step 3: Download Orchestrator
# ============================================================
log_info "Downloading Orchestrator..."

cd $INSTALL_DIR

if [ -d "DeCloud.Orchestrator" ]; then
    log_info "Updating existing installation..."
    cd DeCloud.Orchestrator
    git pull --quiet
else
    git clone --quiet https://github.com/bekirmfr/DeCloud.Orchestrator.git
    cd DeCloud.Orchestrator
fi

log_success "Orchestrator downloaded"

# ============================================================
# Step 4: Build
# ============================================================
log_info "Building Orchestrator..."

dotnet restore --verbosity quiet
dotnet build -c Release --verbosity quiet

log_success "Orchestrator built"

# ============================================================
# Step 5: Create configuration
# ============================================================
log_info "Creating configuration..."

cat > $CONFIG_DIR/orchestrator.appsettings.Production.json << EOF
{
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
    }
  },
  "Jwt": {
    "Key": "${JWT_SECRET}",
    "Issuer": "orchestrator",
    "Audience": "orchestrator-client"
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:${PORT}"
      }
    }
  }
}
EOF

ln -sf $CONFIG_DIR/orchestrator.appsettings.Production.json $INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json

log_success "Configuration created"

# ============================================================
# Step 6: Create systemd service
# ============================================================
log_info "Creating systemd service..."

cat > /etc/systemd/system/decloud-orchestrator.service << EOF
[Unit]
Description=DeCloud Orchestrator
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=${INSTALL_DIR}/DeCloud.Orchestrator
ExecStart=/usr/bin/dotnet run --project src/Orchestrator -c Release --no-build --environment Production
Restart=always
RestartSec=10
Environment=DOTNET_ENVIRONMENT=Production
Environment=ASPNETCORE_ENVIRONMENT=Production

StandardOutput=journal
StandardError=journal
SyslogIdentifier=decloud-orchestrator

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable decloud-orchestrator --quiet

log_success "Systemd service created"

# ============================================================
# Step 7: Configure firewall
# ============================================================
log_info "Configuring firewall..."

if command -v ufw &> /dev/null; then
    ufw allow $PORT/tcp > /dev/null 2>&1 || true
fi
iptables -I INPUT 1 -p tcp --dport $PORT -j ACCEPT 2>/dev/null || true

# ============================================================
# Step 8: Start service
# ============================================================
log_info "Starting Orchestrator..."

systemctl start decloud-orchestrator
sleep 5

if systemctl is-active --quiet decloud-orchestrator; then
    log_success "Orchestrator is running"
else
    log_error "Orchestrator failed to start. Check: journalctl -u decloud-orchestrator -f"
    exit 1
fi

# ============================================================
# Summary
# ============================================================
PUBLIC_IP=$(curl -s --max-time 5 https://api.ipify.org || hostname -I | awk '{print $1}')

echo ""
echo "╔══════════════════════════════════════════════════════════════╗"
echo "║              Installation Complete!                          ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo ""
log_success "DeCloud Orchestrator installed successfully!"
echo ""
echo "  API Endpoint:    http://${PUBLIC_IP}:${PORT}"
echo "  Swagger UI:      http://${PUBLIC_IP}:${PORT}/swagger"
echo "  Health Check:    http://${PUBLIC_IP}:${PORT}/health"
echo ""
echo "Useful commands:"
echo "  Status:          sudo systemctl status decloud-orchestrator"
echo "  Logs:            sudo journalctl -u decloud-orchestrator -f"
echo "  Restart:         sudo systemctl restart decloud-orchestrator"
echo ""
echo "To add a node, run on the node server:"
echo "  curl -sSL https://raw.githubusercontent.com/bekirmfr/DeCloud.NodeAgent/main/install.sh | sudo bash -s -- --orchestrator http://${PUBLIC_IP}:${PORT}"
echo ""
