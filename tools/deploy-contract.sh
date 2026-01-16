#!/bin/bash
# deploy-contract.sh
# Deploy DeCloudEscrow contract to Polygon Amoy testnet

set -e

# ═══════════════════════════════════════════════════════════════════════════
# CONFIGURATION
# ═══════════════════════════════════════════════════════════════════════════

# Polygon Amoy testnet
RPC_URL="https://rpc-amoy.polygon.technology"
CHAIN_ID="80002"

# You need to set these
PRIVATE_KEY="${DEPLOYER_PRIVATE_KEY:-}"
ORCHESTRATOR_ADDRESS="${ORCHESTRATOR_WALLET:-}"

# For testnet, we'll deploy a mock USDC or use existing one
# Polygon Amoy has test tokens available
USDC_ADDRESS="${USDC_TOKEN_ADDRESS:-}"

# ═══════════════════════════════════════════════════════════════════════════
# PREREQUISITES
# ═══════════════════════════════════════════════════════════════════════════

echo "═══════════════════════════════════════════════════════════════════"
echo "DeCloud Escrow Contract Deployment"
echo "═══════════════════════════════════════════════════════════════════"
echo ""

# Check required tools
if ! command -v forge &> /dev/null; then
    echo "Error: Foundry (forge) is required"
    echo "Install: curl -L https://foundry.paradigm.xyz | bash"
    exit 1
fi

# Check required environment variables
if [ -z "$PRIVATE_KEY" ]; then
    echo "Error: DEPLOYER_PRIVATE_KEY environment variable required"
    exit 1
fi

if [ -z "$ORCHESTRATOR_ADDRESS" ]; then
    echo "Error: ORCHESTRATOR_WALLET environment variable required"
    exit 1
fi

# ═══════════════════════════════════════════════════════════════════════════
# DEPLOY MOCK USDC (if not provided)
# ═══════════════════════════════════════════════════════════════════════════

if [ -z "$USDC_ADDRESS" ]; then
    echo "No USDC address provided, deploying mock USDC..."
    
    # Create MockUSDC contract
    cat > contracts/MockUSDC.sol << 'EOF'
// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/ERC20.sol";

contract MockUSDC is ERC20 {
    constructor() ERC20("Mock USDC", "USDC") {
        _mint(msg.sender, 1_000_000 * 10**6); // 1M USDC
    }
    
    function decimals() public pure override returns (uint8) {
        return 6;
    }
    
    // Faucet function for testnet
    function faucet(address to, uint256 amount) external {
        _mint(to, amount);
    }
}
EOF

    echo "Deploying MockUSDC..."
    USDC_DEPLOY=$(forge create contracts/MockUSDC.sol:MockUSDC \
        --rpc-url "$RPC_URL" \
        --private-key "$PRIVATE_KEY" \
        --json)
    
    USDC_ADDRESS=$(echo "$USDC_DEPLOY" | jq -r '.deployedTo')
    echo "MockUSDC deployed at: $USDC_ADDRESS"
fi

# ═══════════════════════════════════════════════════════════════════════════
# DEPLOY ESCROW CONTRACT
# ═══════════════════════════════════════════════════════════════════════════

echo ""
echo "Deploying DeCloudEscrow contract..."
echo "  USDC Token: $USDC_ADDRESS"
echo "  Orchestrator: $ORCHESTRATOR_ADDRESS"
echo ""

# Install OpenZeppelin if not present
if [ ! -d "lib/openzeppelin-contracts" ]; then
    forge install OpenZeppelin/openzeppelin-contracts --no-commit
fi

# Deploy
ESCROW_DEPLOY=$(forge create contracts/DeCloudEscrow.sol:DeCloudEscrow \
    --rpc-url "$RPC_URL" \
    --private-key "$PRIVATE_KEY" \
    --constructor-args "$USDC_ADDRESS" "$ORCHESTRATOR_ADDRESS" \
    --json)

ESCROW_ADDRESS=$(echo "$ESCROW_DEPLOY" | jq -r '.deployedTo')
TX_HASH=$(echo "$ESCROW_DEPLOY" | jq -r '.transactionHash')

echo ""
echo "═══════════════════════════════════════════════════════════════════"
echo "DEPLOYMENT SUCCESSFUL"
echo "═══════════════════════════════════════════════════════════════════"
echo ""
echo "Escrow Contract: $ESCROW_ADDRESS"
echo "USDC Token:      $USDC_ADDRESS"
echo "Transaction:     $TX_HASH"
echo ""
echo "Block Explorer:  https://amoy.polygonscan.com/tx/$TX_HASH"
echo ""

# ═══════════════════════════════════════════════════════════════════════════
# GENERATE CONFIG
# ═══════════════════════════════════════════════════════════════════════════

echo "Generating configuration..."

cat > config/deployed-contracts.json << EOF
{
    "network": "polygon-amoy",
    "chainId": "$CHAIN_ID",
    "rpcUrl": "$RPC_URL",
    "contracts": {
        "escrow": "$ESCROW_ADDRESS",
        "usdc": "$USDC_ADDRESS"
    },
    "deployedAt": "$(date -u +"%Y-%m-%dT%H:%M:%SZ")",
    "transactionHash": "$TX_HASH"
}
EOF

echo "Configuration saved to config/deployed-contracts.json"
echo ""
echo "Update your appsettings.Payment.json with:"
echo "  EscrowContractAddress: $ESCROW_ADDRESS"
echo "  UsdcTokenAddress: $USDC_ADDRESS"
echo ""

# ═══════════════════════════════════════════════════════════════════════════
# VERIFY (optional)
# ═══════════════════════════════════════════════════════════════════════════

echo "To verify on Polygonscan (optional):"
echo "forge verify-contract $ESCROW_ADDRESS contracts/DeCloudEscrow.sol:DeCloudEscrow \\"
echo "  --chain-id $CHAIN_ID \\"
echo "  --constructor-args \$(cast abi-encode 'constructor(address,address)' $USDC_ADDRESS $ORCHESTRATOR_ADDRESS)"
echo ""
