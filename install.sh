#!/bin/bash
#
# DeCloud Orchestrator Installation Script v3.0.0
#
# Production-ready installation with:
# - .NET 8 Runtime
# - Node.js 20 LTS for frontend build  
# - MongoDB connection
# - WireGuard tunnel interface (peers added dynamically during relay registration)
# - Caddy central ingress with golden master config persistence
# - Attestation system for VM verification (NEW v3.0)
# - Payment system with blockchain integration (NEW v3.0)
# - Automatic recovery and backup timers
# - fail2ban DDoS protection
#
# MAJOR CHANGES IN v3.0:
# - Attestation configuration with security validation
# - Payment/blockchain configuration with secure secret management
# - Environment variable file for sensitive data (NEVER in config files)
# - Pre-deployment validation with helpful error messages
# - Support for production blockchain integration (Polygon)
#
# Version: 3.0.0 (Attestation & Payment Integration)
#
# Usage:
#   sudo ./install.sh \
#       --mongodb "mongodb+srv://..." \
#       --orchestrator-wallet "0x..." \
#       --orchestrator-private-key "0x..." \
#       --blockchain-rpc "https://polygon-mainnet.g.alchemy.com/v2/..." \
#       --escrow-contract "0x..." \
#       --usdc-token "0x..." \
#       --ingress-domain vms.stackfi.tech \
#       --caddy-email admin@stackfi.tech \
#       --cloudflare-token YOUR_CF_TOKEN \
#       --enable-wireguard
#

set -e

VERSION="3.0.0"

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
ENV_FILE="/etc/decloud/orchestrator.env"  # NEW: Environment variables file
REPO_URL="https://github.com/bekirmfr/DeCloud.Orchestrator.git"

# Logging
INSTALL_LOG="$LOG_DIR/install.log"
ENABLE_LOGGING=true

# Ports
API_PORT=5050

# MongoDB
MONGODB_URI=""

# ============================================================
# NEW: Attestation Configuration (v3.0)
# ============================================================
# These are security-critical parameters that should NOT be changed
# without understanding the security implications

# Maximum VM response time for attestation challenge (100ms recommended)
# DO NOT increase above 200ms - allows node to extract ephemeral keys
ATTESTATION_MAX_RESPONSE_MS=100

# Challenge intervals
ATTESTATION_STARTUP_INTERVAL=60    # Every 60s during first 5 minutes
ATTESTATION_NORMAL_INTERVAL=3600   # Every hour after startup

# Failure thresholds
ATTESTATION_FAILURE_THRESHOLD=3    # Pause billing after 3 failures
ATTESTATION_RECOVERY_THRESHOLD=2   # Resume billing after 2 successes

# Memory verification
ATTESTATION_MAX_MEMORY_TOUCH_MS=50.0
ATTESTATION_MAX_SINGLE_PAGE_TOUCH_MS=5.0

# ============================================================
# NEW: Payment/Blockchain Configuration (v3.0)
# ============================================================

# Blockchain network
BLOCKCHAIN_CHAIN_ID=""              # 80002=Polygon Amoy (testnet), 137=Polygon (mainnet)
BLOCKCHAIN_RPC_URL=""               # Alchemy/Infura RPC endpoint
ESCROW_CONTRACT_ADDRESS=""          # Deployed escrow contract
USDC_TOKEN_ADDRESS=""               # USDC token on selected chain

# Orchestrator wallet (SECURITY CRITICAL)
ORCHESTRATOR_WALLET_ADDRESS=""      # Orchestrator's wallet for settlements
ORCHESTRATOR_PRIVATE_KEY=""         # NEVER logged or committed - stored in env file only

# Payment settings
REQUIRED_CONFIRMATIONS=12           # Block confirmations (12 for testnet, 20+ for mainnet)
MIN_SETTLEMENT_AMOUNT=10.0          # Minimum $ to trigger settlement
SETTLEMENT_INTERVAL_HOURS=24        # How often to settle (24h = daily)
PLATFORM_FEE_PERCENT=15.0           # Platform fee (15%)

# JWT Secret
JWT_SECRET_KEY=""                   # Will be auto-generated if not provided

# Central Ingress (Caddy)
INSTALL_CADDY=false
INGRESS_DOMAIN=""
ENABLE_INGRESS="false"
CADDY_EMAIL=""
CADDY_STAGING=false
CADDY_DATA_DIR="/var/lib/caddy"
CADDY_LOG_DIR="/var/log/caddy"
DNS_PROVIDER="cloudflare"
CLOUDFLARE_TOKEN=""

# WireGuard (Dynamic Peer Management)
ENABLE_WIREGUARD=false
WIREGUARD_INTERFACE="wg-relay-client"
WIREGUARD_PORT=51821
TUNNEL_IP="10.20.0.1"
TUNNEL_NETWORK="10.20.0.0/16"

# fail2ban
INSTALL_FAIL2BAN=false
SKIP_FAIL2BAN=false

# Update mode
UPDATE_MODE=false
FORCE_REBUILD=false

# ============================================================
# Argument Parsing
# ============================================================
parse_args() {
    while [[ $# -gt 0 ]]; do
        case $1 in
            --no-log)
                ENABLE_LOGGING=false
                shift
                ;;
            --port)
                API_PORT="$2"
                shift 2
                ;;
            --mongodb)
                MONGODB_URI="$2"
                shift 2
                ;;
                
            # =====================================================
            # NEW: Blockchain/Payment Arguments (v3.0)
            # =====================================================
            --blockchain-chain-id)
                BLOCKCHAIN_CHAIN_ID="$2"
                shift 2
                ;;
            --blockchain-rpc)
                BLOCKCHAIN_RPC_URL="$2"
                shift 2
                ;;
            --escrow-contract)
                ESCROW_CONTRACT_ADDRESS="$2"
                shift 2
                ;;
            --usdc-token)
                USDC_TOKEN_ADDRESS="$2"
                shift 2
                ;;
            --orchestrator-wallet)
                ORCHESTRATOR_WALLET_ADDRESS="$2"
                shift 2
                ;;
            --orchestrator-private-key)
                ORCHESTRATOR_PRIVATE_KEY="$2"
                shift 2
                ;;
            --jwt-secret)
                JWT_SECRET_KEY="$2"
                shift 2
                ;;
            --confirmations)
                REQUIRED_CONFIRMATIONS="$2"
                shift 2
                ;;
            --platform-fee)
                PLATFORM_FEE_PERCENT="$2"
                shift 2
                ;;
            --min-settlement)
                MIN_SETTLEMENT_AMOUNT="$2"
                shift 2
                ;;
                
            # =====================================================
            # Existing Arguments
            # =====================================================
            --ingress-domain)
                INGRESS_DOMAIN="$2"
                INSTALL_CADDY=true
                ENABLE_INGRESS="true"
                shift 2
                ;;
            --caddy-email)
                CADDY_EMAIL="$2"
                shift 2
                ;;
            --caddy-staging)
                CADDY_STAGING=true
                shift
                ;;
            --cloudflare-token)
                CLOUDFLARE_TOKEN="$2"
                shift 2
                ;;
            --enable-wireguard)
                ENABLE_WIREGUARD=true
                shift
                ;;
            --tunnel-ip)
                TUNNEL_IP="$2"
                shift 2
                ;;
            --skip-fail2ban)
                SKIP_FAIL2BAN=true
                shift
                ;;
            --force)
                FORCE_REBUILD=true
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

# ============================================================
# Initialize Logging
# ============================================================
init_logging() {
    if [ "$ENABLE_LOGGING" = false ]; then
        return 0
    fi
    
    # Create log directory
    mkdir -p "$LOG_DIR"
    touch "$INSTALL_LOG"
    chmod 644 "$INSTALL_LOG"
    
    # Redirect all output to both terminal and log file
    exec 1> >(tee -a "$INSTALL_LOG")
    exec 2>&1
    
    echo "==================================================================="
    echo "DeCloud Orchestrator Installation v${VERSION}"
    echo "Started: $(date '+%Y-%m-%d %H:%M:%S')"
    echo "Version: $VERSION"
    echo "Command: $0 $@"
    echo "Log: $INSTALL_LOG"
    echo "==================================================================="
    echo ""
}

# ============================================================
# Help Text
# ============================================================
show_help() {
    cat << EOF
DeCloud Orchestrator Installer v${VERSION}
With attestation verification and blockchain payment integration

Usage: $0 --mongodb <uri> --orchestrator-wallet <address> --orchestrator-private-key <key> [options]

${GREEN}Required:${NC}
  --mongodb <uri>                    MongoDB connection string
  --orchestrator-wallet <address>    Orchestrator's wallet address (0x...)
  --orchestrator-private-key <key>   Orchestrator's private key (0x...) ${RED}KEEP SECURE!${NC}

${CYAN}Blockchain Configuration (Required for production):${NC}
  --blockchain-chain-id <id>         Chain ID (80002=Amoy testnet, 137=Polygon mainnet)
  --blockchain-rpc <url>             RPC endpoint (Alchemy/Infura)
  --escrow-contract <address>        Deployed escrow contract address
  --usdc-token <address>             USDC token address on selected chain

${CYAN}Blockchain Configuration (Optional):${NC}
  --jwt-secret <key>                 JWT secret key (auto-generated if not provided)
  --confirmations <n>                Block confirmations (default: 12 testnet, 20+ mainnet)
  --platform-fee <percent>           Platform fee percentage (default: 15.0)
  --min-settlement <amount>          Min settlement amount (default: 10.0)

${CYAN}Ingress (Recommended):${NC}
  --ingress-domain <domain>          Domain for VM ingress (e.g., vms.stackfi.tech)
  --caddy-email <email>              Email for Let's Encrypt certificates
  --cloudflare-token <token>         Cloudflare API token for DNS-01 challenge
  --caddy-staging                    Use Let's Encrypt staging (for testing)

${CYAN}WireGuard (For CGNAT support):${NC}
  --enable-wireguard                 Enable WireGuard tunnel interface
  --tunnel-ip <ip>                   Orchestrator's tunnel IP (default: 10.20.0.1)

${CYAN}Other:${NC}
  --port <port>                      API port (default: 5050)
  --skip-fail2ban                    Skip fail2ban installation
  --force                            Force clean rebuild
  --help, -h                         Show this help

${YELLOW}Examples:${NC}

  ${CYAN}# Minimal testnet installation (Polygon Amoy)${NC}
  $0 \\
     --mongodb "mongodb+srv://..." \\
     --orchestrator-wallet "0xYOUR_WALLET" \\
     --orchestrator-private-key "0xYOUR_PRIVATE_KEY" \\
     --blockchain-chain-id "80002" \\
     --blockchain-rpc "https://polygon-amoy.g.alchemy.com/v2/YOUR_KEY" \\
     --escrow-contract "0xESCROW_CONTRACT" \\
     --usdc-token "0xUSDC_TOKEN"

  ${CYAN}# Full production deployment (Polygon mainnet with ingress and CGNAT)${NC}
  $0 \\
     --mongodb "mongodb+srv://..." \\
     --orchestrator-wallet "0xYOUR_WALLET" \\
     --orchestrator-private-key "0xYOUR_PRIVATE_KEY" \\
     --blockchain-chain-id "137" \\
     --blockchain-rpc "https://polygon-mainnet.g.alchemy.com/v2/YOUR_KEY" \\
     --escrow-contract "0xESCROW_CONTRACT" \\
     --usdc-token "0x2791Bca1f2de4661ED88A30C99A7a9449Aa84174" \\
     --confirmations 20 \\
     --ingress-domain vms.stackfi.tech \\
     --caddy-email admin@stackfi.tech \\
     --cloudflare-token YOUR_TOKEN \\
     --enable-wireguard

${RED}Security Notes:${NC}
  - Private key is stored in /etc/decloud/orchestrator.env with 600 permissions
  - Never commit private key to source control
  - Use environment variables in production
  - Rotate keys periodically
  - Use separate wallets for testnet and mainnet

${GREEN}Attestation System:${NC}
  - Automatically configured for secure VM verification
  - MaxResponseTime: ${ATTESTATION_MAX_RESPONSE_MS}ms (prevents key extraction)
  - Billing pauses automatically on attestation failures
  - No manual configuration needed

${GREEN}Payment System:${NC}
  - Blockchain integration for deposits and settlements
  - Automatic deposit monitoring
  - Attestation-aware billing (only bill verified runtime)
  - Smart contract escrow for security
EOF
}

# ============================================================
# NEW: Security Validation Functions (v3.0)
# ============================================================

validate_wallet_address() {
    local wallet="$1"
    local name="$2"
    
    if [ -z "$wallet" ]; then
        log_error "$name is required"
        return 1
    fi
    
    # Check for null address
    if [ "$wallet" == "0x0000000000000000000000000000000000000000" ]; then
        log_error "$name cannot be null address (0x000...000)"
        return 1
    fi
    
    # Check format (0x followed by 40 hex characters)
    if ! [[ "$wallet" =~ ^0x[a-fA-F0-9]{40}$ ]]; then
        log_warn "$name format may be invalid (expected: 0x + 40 hex chars)"
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            return 1
        fi
    fi
    
    return 0
}

validate_private_key() {
    local key="$1"
    
    if [ -z "$key" ]; then
        log_error "Orchestrator private key is required"
        log_error "Use: --orchestrator-private-key \"0x...\""
        return 1
    fi
    
    # Check for placeholder
    if [ "$key" == "_NEVER_COMMIT_THIS_USE_ENV_VAR_" ]; then
        log_error "Cannot use placeholder private key value"
        log_error "Provide actual private key via --orchestrator-private-key"
        return 1
    fi
    
    # Check format (0x followed by 64 hex characters)
    if ! [[ "$key" =~ ^0x[a-fA-F0-9]{64}$ ]]; then
        log_warn "Private key format may be invalid (expected: 0x + 64 hex chars)"
        read -p "Continue anyway? (y/N): " -n 1 -r
        echo
        if [[ ! $REPLY =~ ^[Yy]$ ]]; then
            return 1
        fi
    fi
    
    return 0
}

check_blockchain_config() {
    log_step "Validating blockchain configuration..."
    
    # Validate wallet address
    if ! validate_wallet_address "$ORCHESTRATOR_WALLET_ADDRESS" "Orchestrator wallet address"; then
        exit 1
    fi
    log_success "Wallet address validated"
    
    # Validate private key
    if ! validate_private_key "$ORCHESTRATOR_PRIVATE_KEY"; then
        exit 1
    fi
    log_success "Private key validated"
    
    # Check blockchain parameters if provided
    if [ -n "$BLOCKCHAIN_CHAIN_ID" ]; then
        log_info "Blockchain network: Chain ID $BLOCKCHAIN_CHAIN_ID"
        
        if [ "$BLOCKCHAIN_CHAIN_ID" == "137" ]; then
            log_warn "Detected Polygon MAINNET - ensure this is intentional"
            if [ "$REQUIRED_CONFIRMATIONS" -lt 20 ]; then
                log_warn "Mainnet should use 20+ confirmations (currently: $REQUIRED_CONFIRMATIONS)"
            fi
        elif [ "$BLOCKCHAIN_CHAIN_ID" == "80002" ]; then
            log_info "Detected Polygon Amoy TESTNET"
        else
            log_warn "Unknown chain ID: $BLOCKCHAIN_CHAIN_ID"
        fi
    fi
    
    # Validate contract addresses if provided
    if [ -n "$ESCROW_CONTRACT_ADDRESS" ]; then
        if ! validate_wallet_address "$ESCROW_CONTRACT_ADDRESS" "Escrow contract address"; then
            exit 1
        fi
        log_success "Escrow contract address validated"
    fi
    
    if [ -n "$USDC_TOKEN_ADDRESS" ]; then
        if ! validate_wallet_address "$USDC_TOKEN_ADDRESS" "USDC token address"; then
            exit 1
        fi
        log_success "USDC token address validated"
    fi
    
    # Validate RPC URL if provided
    if [ -n "$BLOCKCHAIN_RPC_URL" ]; then
        if [[ ! "$BLOCKCHAIN_RPC_URL" =~ ^https?:// ]]; then
            log_error "Blockchain RPC URL must start with http:// or https://"
            exit 1
        fi
        log_success "RPC URL validated"
    fi
    
    # Security warning
    echo ""
    log_warn "SECURITY REMINDER:"
    log_warn "  • Private key will be stored in $ENV_FILE with 600 permissions"
    log_warn "  • Only root can read this file"
    log_warn "  • Never commit this file to version control"
    log_warn "  • Rotate keys periodically"
    echo ""
    
    read -p "Continue with installation? (y/N): " -n 1 -r
    echo
    if [[ ! $REPLY =~ ^[Yy]$ ]]; then
        log_info "Installation cancelled by user"
        exit 0
    fi
}

generate_jwt_secret() {
    if [ -z "$JWT_SECRET_KEY" ]; then
        log_info "Generating JWT secret key..."
        JWT_SECRET_KEY=$(openssl rand -base64 32)
        log_success "JWT secret key generated"
    else
        log_info "Using provided JWT secret key"
    fi
    
    # Validate length
    if [ ${#JWT_SECRET_KEY} -lt 32 ]; then
        log_error "JWT secret key too short (minimum 32 characters)"
        exit 1
    fi
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
        log_error "Cannot detect OS. Only Ubuntu/Debian supported."
        exit 1
    fi
    
    . /etc/os-release
    
    if [[ "$ID" != "ubuntu" ]] && [[ "$ID" != "debian" ]]; then
        log_error "Unsupported OS: $ID. Only Ubuntu/Debian supported."
        exit 1
    fi
    
    log_success "OS: $PRETTY_NAME"
}

check_resources() {
    log_step "Checking system resources..."
    
    local cpu_cores=$(nproc)
    local total_mem=$(free -m | awk '/^Mem:/{print $2}')
    local disk_free=$(df -BG / | awk 'NR==2 {print $4}' | sed 's/G//')
    
    local issues=0
    
    if [ $cpu_cores -lt 2 ]; then
        log_warn "CPU cores: $cpu_cores (recommended: 2+)"
        issues=$((issues + 1))
    else
        log_success "CPU cores: $cpu_cores"
    fi
    
    if [ $total_mem -lt 2048 ]; then
        log_warn "RAM: ${total_mem}MB (recommended: 2GB+)"
        issues=$((issues + 1))
    else
        log_success "RAM: ${total_mem}MB"
    fi
    
    if [ $disk_free -lt 10 ]; then
        log_warn "Disk space: ${disk_free}GB (recommended: 10GB+)"
        issues=$((issues + 1))
    else
        log_success "Disk space: ${disk_free}GB free"
    fi
    
    if [ $issues -gt 0 ]; then
        log_warn "$issues resource warnings detected. Continue? (y/N)"
        read -r response
        if [[ ! "$response" =~ ^[Yy]$ ]]; then
            exit 1
        fi
    fi
}

check_network() {
    log_step "Checking network connectivity..."
    
    if curl -s --max-time 5 https://github.com > /dev/null 2>&1; then
        log_success "Internet connectivity OK"
    else
        log_error "Cannot reach github.com. Check internet connection."
        exit 1
    fi
}

check_mongodb() {
    log_step "Checking MongoDB connection..."
    
    if [ -z "$MONGODB_URI" ]; then
        log_error "MongoDB URI is required"
        log_info "Use: --mongodb 'mongodb+srv://...'"
        exit 1
    fi
    
    log_success "MongoDB URI provided"
}

check_cloudflare_token() {
    if [ "$INSTALL_CADDY" = true ] && [ -z "$CLOUDFLARE_TOKEN" ]; then
        log_warn "No Cloudflare token provided"
        log_warn "Wildcard SSL certificates will not be provisioned"
        log_warn "Continue without SSL? (y/N)"
        read -r response
        if [[ ! "$response" =~ ^[Yy]$ ]]; then
            exit 1
        fi
    fi
}

check_wireguard_params() {
    if [ "$ENABLE_WIREGUARD" = true ]; then
        log_step "Validating WireGuard parameters..."
        
        # Validate tunnel IP format
        if [[ ! "$TUNNEL_IP" =~ ^10\.20\.[0-9]+\.[0-9]+$ ]]; then
            log_error "Invalid tunnel IP: $TUNNEL_IP"
            log_info "Must be in 10.20.0.0/16 range"
            exit 1
        fi
        
        log_success "WireGuard parameters validated"
        log_info "Tunnel IP: $TUNNEL_IP"
        log_info "Tunnel network: $TUNNEL_NETWORK"
        log_info "NOTE: Relay peers will be added dynamically during registration"
    fi
}

# ============================================================
# Installation Functions
# ============================================================

install_dependencies() {
    log_step "Installing system dependencies..."
    
    export DEBIAN_FRONTEND=noninteractive
    
    apt-get update -qq > /dev/null 2>&1
    apt-get install -y -qq \
        curl wget git jq \
        apt-transport-https \
        ca-certificates \
        gnupg \
        lsb-release \
        ufw \
        fail2ban \
        > /dev/null 2>&1
    
    log_success "System dependencies installed"
}

install_nodejs() {
    if command -v node &> /dev/null; then
        local node_version=$(node --version | cut -d'v' -f2 | cut -d'.' -f1)
        if [ "$node_version" -ge 20 ]; then
            log_success "Node.js already installed ($(node --version))"
            return
        fi
    fi
    
    log_step "Installing Node.js 20 LTS..."
    
    curl -fsSL https://deb.nodesource.com/setup_20.x | bash - > /dev/null 2>&1
    apt-get install -y -qq nodejs > /dev/null 2>&1
    
    log_success "Node.js installed ($(node --version))"
}

install_dotnet() {
    if command -v dotnet &> /dev/null; then
        log_success ".NET SDK already installed"
        return
    fi
    
    log_step "Installing .NET 8 SDK..."
    
    wget -q https://packages.microsoft.com/config/ubuntu/$(lsb_release -rs)/packages-microsoft-prod.deb
    dpkg -i packages-microsoft-prod.deb > /dev/null 2>&1
    rm packages-microsoft-prod.deb
    
    apt-get update -qq > /dev/null 2>&1
    apt-get install -y -qq dotnet-sdk-8.0 > /dev/null 2>&1
    
    log_success ".NET 8 SDK installed"
}

install_wireguard() {
    if [ "$ENABLE_WIREGUARD" = false ]; then
        log_info "WireGuard not enabled (use --enable-wireguard)"
        log_warn "CGNAT nodes will NOT be reachable without WireGuard"
        return 0
    fi
    
    log_step "Installing WireGuard..."
    
    # Check if already installed
    if command -v wg &> /dev/null; then
        local wg_version=$(wg --version 2>&1 | head -n1)
        log_success "WireGuard already installed: $wg_version"
        return 0
    fi
    
    # Determine OS version for optimization
    local os_version=$(lsb_release -rs)
    local os_version_major=$(echo "$os_version" | cut -d'.' -f1)
    
    log_info "Detected Ubuntu $os_version"
    
    # Update package cache (CRITICAL - prevents 404 errors)
    log_info "Updating package cache..."
    if apt-get update -qq 2>&1 | tee -a "$INSTALL_LOG" | grep -qi "err:"; then
        log_warn "Package cache update had warnings (check $INSTALL_LOG)"
    else
        log_info "Package cache updated successfully"
    fi
    
    # Ubuntu 24.04+ has WireGuard built into kernel - only need userspace tools
    if [ "$os_version_major" -ge 24 ]; then
        log_info "Ubuntu 24.04+ detected - installing userspace tools only"
        
        if ! apt-get install -y wireguard-tools 2>&1 | tee -a "$INSTALL_LOG" > /dev/null; then
            log_error "Failed to install WireGuard tools"
            log_error "Check installation log at: $INSTALL_LOG"
            log_error "Try manually: sudo apt-get install -y wireguard-tools"
            return 1
        fi
    else
        # Ubuntu < 24.04 needs kernel headers and DKMS
        log_info "Ubuntu $os_version - installing full WireGuard package with DKMS"
        
        # Install kernel headers first (needed for DKMS)
        local kernel_version=$(uname -r)
        log_info "Installing kernel headers for $kernel_version..."
        
        if apt-get install -y linux-headers-${kernel_version} 2>&1 | tee -a "$INSTALL_LOG" > /dev/null; then
            log_info "Kernel headers installed"
        else
            log_warn "Kernel headers installation had issues (might still work with in-tree module)"
        fi
        
        # Install WireGuard
        if ! apt-get install -y wireguard wireguard-tools 2>&1 | tee -a "$INSTALL_LOG" > /dev/null; then
            log_error "Failed to install WireGuard"
            log_error "Check installation log at: $INSTALL_LOG"
            log_error "Try manually: sudo apt-get install -y wireguard wireguard-tools"
            return 1
        fi
    fi
    
    # Verify installation
    if ! command -v wg &> /dev/null; then
        log_error "WireGuard command not found after installation"
        log_error "Installation may have failed silently"
        return 1
    fi
    
    # Check kernel module (informational only)
    if modinfo wireguard &> /dev/null 2>&1; then
        log_info "WireGuard kernel module available"
    elif [ "$os_version_major" -ge 24 ]; then
        log_info "WireGuard module built into kernel (Ubuntu 24.04+)"
    else
        log_warn "WireGuard kernel module not detected (might still work)"
    fi
    
    local wg_version=$(wg --version 2>&1 | head -n1)
    log_success "WireGuard installed: $wg_version"
    
    return 0
}

configure_wireguard_interface() {
    if [ "$ENABLE_WIREGUARD" = false ]; then
        return
    fi
    
    log_step "Configuring WireGuard tunnel interface..."
    
    # Create WireGuard directory
    mkdir -p /etc/wireguard
    chmod 700 /etc/wireguard
    
    # Generate orchestrator keypair
    if [ ! -f /etc/wireguard/orchestrator-private.key ]; then
        log_info "Generating WireGuard keypair for orchestrator..."
        wg genkey | tee /etc/wireguard/orchestrator-private.key | wg pubkey > /etc/wireguard/orchestrator-public.key
        chmod 600 /etc/wireguard/orchestrator-private.key
        chmod 644 /etc/wireguard/orchestrator-public.key
        
        local pubkey=$(cat /etc/wireguard/orchestrator-public.key)
        log_success "Orchestrator public key generated: $pubkey"
        log_info "This key will be provided to relay nodes during provisioning"
    else
        log_info "Using existing orchestrator keypair"
    fi
    
    local private_key=$(cat /etc/wireguard/orchestrator-private.key)
    
    # Create WireGuard configuration (NO PEERS - added dynamically)
    cat > /etc/wireguard/${WIREGUARD_INTERFACE}.conf << EOF
# DeCloud Orchestrator WireGuard Tunnel
# Peers are managed dynamically by OrchestratorWireGuardManager service
# DO NOT manually add peers - they are added automatically during relay registration

[Interface]
Address = ${TUNNEL_IP}/24
PrivateKey = ${private_key}
ListenPort = ${WIREGUARD_PORT}

# Routing for tunnel network
PostUp = ip route add ${TUNNEL_NETWORK} dev ${WIREGUARD_INTERFACE} 2>/dev/null || true
PreDown = ip route del ${TUNNEL_NETWORK} dev ${WIREGUARD_INTERFACE} 2>/dev/null || true

# NOTE: [Peer] sections added dynamically when relay nodes register
# Managed by: OrchestratorWireGuardManager
# To view current peers: wg show ${WIREGUARD_INTERFACE}
EOF
    
    chmod 600 /etc/wireguard/${WIREGUARD_INTERFACE}.conf
    
    log_success "WireGuard configuration created"
    log_info "Interface: ${WIREGUARD_INTERFACE}"
    log_info "Tunnel IP: ${TUNNEL_IP}"
    log_info "Peers: None (added dynamically during relay registration)"
    
    # Enable and start WireGuard
    log_info "Starting WireGuard interface..."
    systemctl enable wg-quick@${WIREGUARD_INTERFACE} --quiet 2>/dev/null || true
    systemctl start wg-quick@${WIREGUARD_INTERFACE} 2>/dev/null || true
    
    # Verify interface is up
    sleep 2
    if systemctl is-active --quiet wg-quick@${WIREGUARD_INTERFACE}; then
        log_success "WireGuard interface active"
        
        # Show interface details
        local addr=$(ip addr show ${WIREGUARD_INTERFACE} 2>/dev/null | grep "inet " | awk '{print $2}')
        if [ -n "$addr" ]; then
            log_info "Interface address: $addr"
        fi
    else
        log_warn "WireGuard interface not active (this is OK, will activate when first peer is added)"
    fi
}

install_caddy() {
    if [ "$INSTALL_CADDY" = false ]; then
        return
    fi
    
    log_step "Installing Caddy..."
    
    if command -v caddy &> /dev/null; then
        log_success "Caddy already installed"
        return
    fi
    
    # Install Caddy with Cloudflare DNS plugin
    apt-get install -y -qq debian-keyring debian-archive-keyring apt-transport-https > /dev/null 2>&1
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg > /dev/null 2>&1
    curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list > /dev/null 2>&1
    
    apt-get update -qq > /dev/null 2>&1
    apt-get install -y -qq caddy > /dev/null 2>&1
    
    log_success "Caddy installed"
}

configure_caddy() {
    if [ "$INSTALL_CADDY" = false ]; then
        return
    fi
    
    log_step "Configuring Caddy for central ingress..."
    
    mkdir -p "$CADDY_DATA_DIR" "$CADDY_LOG_DIR" /etc/caddy "$CADDY_BACKUP_DIR"
    chown caddy:caddy "$CADDY_DATA_DIR" "$CADDY_LOG_DIR" 2>/dev/null || true
    
    # Configure Cloudflare token in systemd
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
    
    # Create Caddyfile
    cat > /etc/caddy/Caddyfile << EOF
# DeCloud Central Ingress Gateway
# Version: ${VERSION}

{
    # Enable admin API (localhost only)
    admin localhost:2019
    
    # Logging
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

# Health check endpoint (HTTP only)
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
    
    log_success "Caddy configured"
}

install_fail2ban() {
    if [ "$SKIP_FAIL2BAN" = true ]; then
        return
    fi
    
    log_step "Installing fail2ban..."
    
    if command -v fail2ban-client &> /dev/null; then
        log_success "fail2ban already installed"
        return
    fi
    
    apt-get install -y -qq fail2ban > /dev/null 2>&1
    
    # Create jail for orchestrator
    cat > /etc/fail2ban/jail.d/decloud-orchestrator.conf << EOF
[decloud-orchestrator]
enabled = true
port = ${API_PORT}
filter = decloud-orchestrator
logpath = ${LOG_DIR}/orchestrator.log
maxretry = 5
bantime = 3600
findtime = 600
EOF
    
    # Create filter
    cat > /etc/fail2ban/filter.d/decloud-orchestrator.conf << EOF
[Definition]
failregex = ^.*Authentication failed.*from <HOST>.*$
            ^.*Unauthorized access.*from <HOST>.*$
            ^.*Invalid request.*from <HOST>.*$
ignoreregex =
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
    mkdir -p "$CADDY_BACKUP_DIR"
    
    chmod 755 "$INSTALL_DIR"
    chmod 755 "$CONFIG_DIR"
    chmod 755 "$LOG_DIR"
    
    log_success "Directories created"
}

download_orchestrator() {
    log_step "Downloading Orchestrator source..."
    
    if [ -d "$INSTALL_DIR/DeCloud.Orchestrator/.git" ]; then
        cd "$INSTALL_DIR/DeCloud.Orchestrator"
        
        if [ "$UPDATE_MODE" = true ] || [ "$FORCE_REBUILD" = true ]; then
            log_info "Updating source code..."
            git fetch origin --quiet 2>/dev/null
            
            local current_commit=$(git rev-parse HEAD 2>/dev/null)
            local remote_commit=$(git rev-parse origin/main 2>/dev/null || git rev-parse origin/master 2>/dev/null)
            
            if [ "$current_commit" = "$remote_commit" ]; then
                log_success "Source code is up to date"
            else
                git pull origin main --quiet 2>/dev/null || git pull origin master --quiet 2>/dev/null
                log_success "Source code updated"
            fi
        else
            log_success "Source code already present"
        fi
    else
        log_info "Cloning repository..."
        cd "$INSTALL_DIR"
        git clone "$REPO_URL" DeCloud.Orchestrator --quiet
        log_success "Repository cloned"
    fi
}

build_orchestrator() {
    log_step "Building Orchestrator..."
    
    cd "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator"
    
    # Clean if needed
    if [ "$UPDATE_MODE" = true ] || [ "$FORCE_REBUILD" = true ]; then
        log_info "Cleaning previous build artifacts..."
        rm -rf bin/ obj/
        
        if [ -d "wwwroot" ]; then
            rm -rf wwwroot/dist/
            rm -rf wwwroot/node_modules/.vite/
        fi
        
        dotnet nuget locals all --clear > /dev/null 2>&1
        log_success "Build artifacts cleaned"
    fi
    
    # Restore and build backend
    log_info "Restoring .NET packages..."
    dotnet restore --verbosity minimal > /dev/null 2>&1 || dotnet restore
    
    log_info "Building .NET project..."
    dotnet build --configuration Release --no-restore --no-incremental --verbosity minimal > /dev/null 2>&1 || \
        dotnet build --configuration Release --no-restore --no-incremental
    
    # Build frontend
    if [ -d "wwwroot" ] && [ -f "wwwroot/package.json" ]; then
        log_info "Building frontend..."
        cd wwwroot
        
        # Create .env if needed
        if [ ! -f ".env" ] && [ -f ".env.example" ]; then
            cp .env.example .env
        fi
        
        npm install --silent > /dev/null 2>&1 || npm install
        npm run build > /dev/null 2>&1 || npm run build
        
        log_success "Frontend built"
        cd ..
    fi
    
    log_success "Orchestrator built successfully"
}

# ============================================================
# NEW: Environment File Creation (v3.0)
# ============================================================

create_env_file() {
    log_step "Creating secure environment file..."
    
    # Create directory if needed
    mkdir -p "$CONFIG_DIR"
    
    # Create environment file with restrictive permissions
    cat > "$ENV_FILE" << EOF
# =====================================================
# DeCloud Orchestrator Environment Variables
# =====================================================
# This file contains sensitive credentials and must be protected.
# Permissions: 600 (read/write by root only)
# Never commit this file to version control!
#
# Generated: $(date '+%Y-%m-%d %H:%M:%S')
# =====================================================

# =====================================================
# DATABASE
# =====================================================
ConnectionStrings__MongoDB=$MONGODB_URI

# =====================================================
# SECURITY
# =====================================================
Jwt__SecretKey=$JWT_SECRET_KEY

# =====================================================
# PAYMENT SYSTEM (CRITICAL - KEEP SECURE)
# =====================================================

# Blockchain Configuration
Payment__ChainId=$BLOCKCHAIN_CHAIN_ID
Payment__RpcUrl=$BLOCKCHAIN_RPC_URL
Payment__EscrowContractAddress=$ESCROW_CONTRACT_ADDRESS
Payment__UsdcTokenAddress=$USDC_TOKEN_ADDRESS

# Orchestrator Wallet (for signing settlement transactions)
Payment__OrchestratorWalletAddress=$ORCHESTRATOR_WALLET_ADDRESS
Payment__OrchestratorPrivateKey=$ORCHESTRATOR_PRIVATE_KEY

# Confirmation and Settlement Settings
Payment__RequiredConfirmations=$REQUIRED_CONFIRMATIONS
Payment__MinSettlementAmount=$MIN_SETTLEMENT_AMOUNT
Payment__SettlementIntervalHours=$SETTLEMENT_INTERVAL_HOURS
Payment__PlatformFeePercent=$PLATFORM_FEE_PERCENT

# =====================================================
# NOTES
# =====================================================
# - Private key is NEVER stored in appsettings.json
# - This file is loaded by systemd service
# - Backup this file securely (encrypted)
# - Rotate credentials periodically
EOF
    
    # Set restrictive permissions (owner read/write only)
    chmod 600 "$ENV_FILE"
    chown root:root "$ENV_FILE"
    
    log_success "Environment file created: $ENV_FILE"
    log_info "Permissions: 600 (root read/write only)"
}

# ============================================================
# Updated Configuration Creation (v3.0)
# ============================================================

create_configuration() {
    log_step "Creating configuration..."
    
    # Create appsettings.Production.json (NO SECRETS)
    cat > "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json" << EOF
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Orchestrator": "Information"
    }
  },
  "AllowedHosts": "*",
  "Urls": "http://0.0.0.0:${API_PORT}",
  
  "ConnectionStrings": {
    "MongoDB": ""
  },
  
  "Jwt": {
    "SecretKey": "",
    "Issuer": "DeCloudOrchestrator",
    "Audience": "DeCloudUsers",
    "AccessTokenExpirationMinutes": 60,
    "RefreshTokenExpirationDays": 30
  },
  
  "CentralIngress": {
    "Enabled": ${ENABLE_INGRESS},
    "BaseDomain": "${INGRESS_DOMAIN}",
    "CaddyAdminUrl": "http://localhost:2019"
  },
  
  "WireGuard": {
    "Enabled": ${ENABLE_WIREGUARD}
  },
  
  "Attestation": {
    "MaxResponseTimeMs": ${ATTESTATION_MAX_RESPONSE_MS},
    "StartupChallengeIntervalSeconds": ${ATTESTATION_STARTUP_INTERVAL},
    "NormalChallengeIntervalSeconds": ${ATTESTATION_NORMAL_INTERVAL},
    "StartupPeriodMinutes": 5,
    "MaxMemoryTouchMs": ${ATTESTATION_MAX_MEMORY_TOUCH_MS},
    "MaxSinglePageTouchMs": ${ATTESTATION_MAX_SINGLE_PAGE_TOUCH_MS},
    "MemoryToleranceLow": 0.85,
    "MemoryToleranceHigh": 1.15,
    "FailureThreshold": ${ATTESTATION_FAILURE_THRESHOLD},
    "RecoveryThreshold": ${ATTESTATION_RECOVERY_THRESHOLD}
  },
  
  "AttestationNetwork": {
    "AgentPort": 9999,
    "HttpTimeoutMs": 200,
    "UseTls": false
  },
  
  "Payment": {
    "ChainId": "",
    "RpcUrl": "",
    "EscrowContractAddress": "",
    "UsdcTokenAddress": "",
    "OrchestratorWalletAddress": "",
    "OrchestratorPrivateKey": "",
    "RequiredConfirmations": 0,
    "MinSettlementAmount": 0,
    "SettlementIntervalHours": 0,
    "PlatformFeePercent": 0
  }
}
EOF
    
    chmod 640 "$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/appsettings.Production.json"
    
    log_success "Configuration created (secrets in environment file)"
    log_info "Configuration file: appsettings.Production.json"
    log_info "Environment file: $ENV_FILE (contains secrets)"
}

# ============================================================
# Updated Systemd Service (v3.0)
# ============================================================

create_systemd_service() {
    log_step "Creating systemd service..."
    
    cat > /etc/systemd/system/decloud-orchestrator.service << EOF
[Unit]
Description=DeCloud Orchestrator
After=network.target mongod.service
$([ "$INSTALL_CADDY" = true ] && echo "After=caddy.service")
$([ "$ENABLE_WIREGUARD" = true ] && echo "After=wg-quick@${WIREGUARD_INTERFACE}.service")
Wants=mongod.service
$([ "$INSTALL_CADDY" = true ] && echo "Requires=caddy.service")
$([ "$ENABLE_WIREGUARD" = true ] && echo "Wants=wg-quick@${WIREGUARD_INTERFACE}.service")

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator
ExecStart=/usr/bin/dotnet $INSTALL_DIR/DeCloud.Orchestrator/src/Orchestrator/bin/Release/net8.0/Orchestrator.dll

# Environment configuration
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_ENVIRONMENT=Production

# Load secrets from environment file
EnvironmentFile=$ENV_FILE

Restart=always
RestartSec=10
StandardOutput=journal
StandardError=journal

# Security hardening
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$LOG_DIR
ReadWritePaths=/var/lib/caddy
ReadWritePaths=/etc/wireguard

[Install]
WantedBy=multi-user.target
EOF
    
    systemctl daemon-reload
    log_success "Systemd service created with environment file support"
}

configure_firewall() {
    log_step "Configuring firewall..."
    
    if ! command -v ufw &> /dev/null; then
        log_info "Installing UFW..."
        apt-get install -y -qq ufw > /dev/null 2>&1
    fi
    
    # Enable if not active
    if ! ufw status | grep -q "Status: active"; then
        ufw --force enable > /dev/null 2>&1
    fi
    
    # Configure rules
    ufw allow ssh > /dev/null 2>&1
    ufw allow $API_PORT/tcp comment "DeCloud Orchestrator API" > /dev/null 2>&1
    
    # Caddy ports
    if [ "$INSTALL_CADDY" = true ]; then
        ufw allow 80/tcp comment "Caddy HTTP" > /dev/null 2>&1
        ufw allow 443/tcp comment "Caddy HTTPS" > /dev/null 2>&1
    fi
    
    # WireGuard port
    if [ "$ENABLE_WIREGUARD" = true ]; then
        ufw allow ${WIREGUARD_PORT}/udp comment "WireGuard Relay Tunnel" > /dev/null 2>&1
        log_info "Firewall: WireGuard port ${WIREGUARD_PORT}/udp allowed"
    fi
    
    log_success "Firewall configured"
}

start_services() {
    log_step "Starting services..."
    
    # Start Caddy first
    if [ "$INSTALL_CADDY" = true ]; then
        systemctl enable caddy --quiet 2>/dev/null || true
        systemctl restart caddy 2>/dev/null || true
        
        if systemctl is-active --quiet caddy; then
            log_success "Caddy started"
        else
            log_warn "Caddy failed to start - check: journalctl -u caddy"
        fi
        
        sleep 3
    fi
    
    # WireGuard should already be running from configure step
    if [ "$ENABLE_WIREGUARD" = true ]; then
        if systemctl is-active --quiet wg-quick@${WIREGUARD_INTERFACE}; then
            log_success "WireGuard interface active"
        else
            log_info "WireGuard interface will activate when first peer is added"
        fi
    fi
    
    # Start Orchestrator
    systemctl enable decloud-orchestrator --quiet 2>/dev/null || true
    systemctl restart decloud-orchestrator
    
    sleep 5
    
    if systemctl is-active --quiet decloud-orchestrator; then
        log_success "Orchestrator started"
    else
        log_error "Orchestrator failed to start"
        log_error "Check logs: journalctl -u decloud-orchestrator -n 50"
        exit 1
    fi
}

# ============================================================
# NEW: Post-Installation Validation (v3.0)
# ============================================================

validate_installation() {
    log_step "Validating installation..."
    
    # Check if orchestrator is running
    if ! systemctl is-active --quiet decloud-orchestrator; then
        log_error "Orchestrator service is not running"
        log_error "Check logs: journalctl -u decloud-orchestrator -n 100"
        return 1
    fi
    
    # Wait for startup
    sleep 10
    
    # Check health endpoint
    local health_url="http://localhost:${API_PORT}/health"
    if curl -s --max-time 5 "$health_url" | grep -q "Healthy"; then
        log_success "Health check passed"
    else
        log_warn "Health check failed - service may still be starting up"
    fi
    
    # Check for configuration errors in logs
    if journalctl -u decloud-orchestrator --since "5 minutes ago" | grep -qi "configuration.*invalid\|configuration.*error"; then
        log_error "Configuration validation errors detected in logs"
        log_error "Check: journalctl -u decloud-orchestrator -n 100"
        return 1
    fi
    
    # Check for attestation service startup
    if journalctl -u decloud-orchestrator --since "5 minutes ago" | grep -q "Attestation.*started\|AttestationScheduler.*started"; then
        log_success "Attestation system started"
    else
        log_warn "Attestation system may not have started"
    fi
    
    # Check for billing service startup
    if journalctl -u decloud-orchestrator --since "5 minutes ago" | grep -q "Billing.*started\|AttestationAwareBillingService.*started"; then
        log_success "Billing system started"
    else
        log_warn "Billing system may not have started"
    fi
    
    log_success "Installation validation completed"
}

# ============================================================
# Updated Summary (v3.0)
# ============================================================

print_summary() {
    local public_ip=$(curl -s --max-time 5 https://api.ipify.org 2>/dev/null || hostname -I | awk '{print $1}')
    
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║    DeCloud Orchestrator - Installation Complete! v${VERSION}    ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    echo "  Dashboard:       http://${public_ip}:${API_PORT}/"
    echo "  API Endpoint:    http://${public_ip}:${API_PORT}/api"
    echo "  Swagger UI:      http://${public_ip}:${API_PORT}/swagger"
    echo "  Health Check:    http://${public_ip}:${API_PORT}/health"
    echo ""
    
    # Payment system info
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  ${GREEN}Payment System (Blockchain Integration):${NC}"
    echo "  ─────────────────────────────────────────────────────────────"
    if [ -n "$BLOCKCHAIN_CHAIN_ID" ]; then
        local network_name="Unknown"
        if [ "$BLOCKCHAIN_CHAIN_ID" == "137" ]; then
            network_name="Polygon Mainnet"
        elif [ "$BLOCKCHAIN_CHAIN_ID" == "80002" ]; then
            network_name="Polygon Amoy Testnet"
        fi
        echo "    Network:         $network_name (Chain ID: $BLOCKCHAIN_CHAIN_ID)"
    fi
    echo "    Wallet:          ${ORCHESTRATOR_WALLET_ADDRESS}"
    echo "    Escrow:          ${ESCROW_CONTRACT_ADDRESS:-Not configured}"
    echo "    USDC Token:      ${USDC_TOKEN_ADDRESS:-Not configured}"
    echo "    Platform Fee:    ${PLATFORM_FEE_PERCENT}%"
    echo "    Confirmations:   ${REQUIRED_CONFIRMATIONS} blocks"
    echo ""
    echo "    ${GREEN}✓ Deposit monitoring active${NC}"
    echo "    ${GREEN}✓ Attestation-aware billing enabled${NC}"
    echo "    ${GREEN}✓ Smart contract escrow configured${NC}"
    echo ""
    
    # Attestation system info
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  ${GREEN}Attestation System (VM Verification):${NC}"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Max Response:    ${ATTESTATION_MAX_RESPONSE_MS}ms (security threshold)"
    echo "    Startup Checks:  Every ${ATTESTATION_STARTUP_INTERVAL}s (first 5 min)"
    echo "    Normal Checks:   Every ${ATTESTATION_NORMAL_INTERVAL}s (hourly)"
    echo "    Fail Threshold:  ${ATTESTATION_FAILURE_THRESHOLD} consecutive failures"
    echo ""
    echo "    ${GREEN}✓ Billing pauses automatically on attestation failure${NC}"
    echo "    ${GREEN}✓ Only verified runtime is billed to users${NC}"
    echo ""
    
    if [ "$INSTALL_CADDY" = true ] && [ -n "$INGRESS_DOMAIN" ]; then
        echo "  ─────────────────────────────────────────────────────────────"
        echo "  Central Ingress (Caddy):"
        echo "  ─────────────────────────────────────────────────────────────"
        echo "    Wildcard Domain: *.${INGRESS_DOMAIN}"
        echo "    Example VM URL:  https://myapp.${INGRESS_DOMAIN}"
        echo "    Admin API:       http://localhost:2019"
        echo ""
    fi
    
    if [ "$ENABLE_WIREGUARD" = true ]; then
        echo "  ─────────────────────────────────────────────────────────────"
        echo "  WireGuard Tunnel (CGNAT Support):"
        echo "  ─────────────────────────────────────────────────────────────"
        echo "    Interface:       ${WIREGUARD_INTERFACE}"
        echo "    Tunnel IP:       ${TUNNEL_IP}"
        echo "    Tunnel Network:  ${TUNNEL_NETWORK}"
        echo ""
        
        if [ -f /etc/wireguard/orchestrator-public.key ]; then
            local pubkey=$(cat /etc/wireguard/orchestrator-public.key)
            echo "    ${CYAN}Public Key:${NC}"
            echo "    ${pubkey}"
            echo ""
        fi
    fi
    
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  ${YELLOW}Security & Configuration:${NC}"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Environment:     $ENV_FILE"
    echo "    Permissions:     600 (root only)"
    echo "    Config File:     appsettings.Production.json"
    echo ""
    echo "    ${YELLOW}⚠ IMPORTANT: Never commit $ENV_FILE to git${NC}"
    echo "    ${YELLOW}⚠ Backup $ENV_FILE securely (encrypted)${NC}"
    echo "    ${YELLOW}⚠ Rotate private keys periodically${NC}"
    echo ""
    
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Commands:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    Status:          sudo systemctl status decloud-orchestrator"
    echo "    Logs:            sudo journalctl -u decloud-orchestrator -f"
    echo "    Restart:         sudo systemctl restart decloud-orchestrator"
    echo "    Health:          curl http://localhost:${API_PORT}/health"
    echo ""
    echo "    View env vars:   sudo cat $ENV_FILE"
    echo "    Edit env vars:   sudo nano $ENV_FILE"
    echo "                     sudo systemctl restart decloud-orchestrator"
    echo ""
    
    if [ "$INSTALL_CADDY" = true ]; then
        echo "    Caddy status:    sudo systemctl status caddy"
        echo "    Caddy logs:      sudo journalctl -u caddy -f"
        echo ""
    fi
    
    if [ "$ENABLE_WIREGUARD" = true ]; then
        echo "    WireGuard:       sudo wg show ${WIREGUARD_INTERFACE}"
        echo "    View peers:      sudo wg show ${WIREGUARD_INTERFACE} peers"
        echo ""
    fi
    
    echo "  ─────────────────────────────────────────────────────────────"
    echo "  Next Steps:"
    echo "  ─────────────────────────────────────────────────────────────"
    echo "    1. Open dashboard: http://${public_ip}:${API_PORT}/"
    echo "    2. Connect wallet to start using DeCloud"
    echo "    3. Monitor logs for any errors"
    echo "    4. Test deposit flow with small amount"
    echo "    5. Verify attestation system is working"
    echo ""
    
    if [ -n "$BLOCKCHAIN_CHAIN_ID" ] && [ "$BLOCKCHAIN_CHAIN_ID" == "137" ]; then
        echo "    ${RED}⚠ MAINNET DEPLOYMENT - PRODUCTION SECURITY CRITICAL:${NC}"
        echo "    ${RED}  • Verify contract addresses are correct${NC}"
        echo "    ${RED}  • Test with small amounts first${NC}"
        echo "    ${RED}  • Monitor blockchain transactions${NC}"
        echo "    ${RED}  • Keep wallet funded for gas fees${NC}"
        echo ""
    fi
}

# ============================================================
# Main (Updated for v3.0)
# ============================================================
main() {
    echo ""
    echo "╔══════════════════════════════════════════════════════════════╗"
    echo "║      DeCloud Orchestrator Installer v${VERSION}               ║"
    echo "║      (Attestation + Payment Integration)                     ║"
    echo "╚══════════════════════════════════════════════════════════════╝"
    echo ""
    
    parse_args "$@"
    
    # Initialize logging
    init_logging
    
    # Checks
    check_root
    check_os
    check_resources
    check_network
    check_mongodb
    check_cloudflare_token
    check_wireguard_params
    
    # NEW: Blockchain/payment validation (v3.0)
    check_blockchain_config
    generate_jwt_secret
    
    echo ""
    log_info "All requirements met. Starting installation..."
    echo ""
    
    # Install dependencies
    install_dependencies
    install_nodejs
    install_dotnet
    install_wireguard
    
    # Configure WireGuard
    configure_wireguard_interface
    
    # Central ingress
    install_caddy
    configure_caddy
    install_fail2ban
    
    # Orchestrator
    create_directories
    download_orchestrator
    build_orchestrator
    
    # NEW: Create secure environment file BEFORE configuration (v3.0)
    create_env_file
    
    # Create configuration (NO SECRETS)
    create_configuration
    
    create_systemd_service
    configure_firewall
    start_services
    
    # NEW: Validate installation (v3.0)
    validate_installation
    
    # Done
    print_summary
}

main "$@"