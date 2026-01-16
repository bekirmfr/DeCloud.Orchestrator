// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import "@openzeppelin/contracts/access/Ownable.sol";
import "@openzeppelin/contracts/utils/ReentrancyGuard.sol";

/**
 * @title DeCloudEscrow
 * @notice Simple escrow contract for DeCloud platform
 * @dev Users deposit USDC, orchestrator reports usage, nodes withdraw earnings
 * 
 * KISS Design:
 * - Users deposit directly to contract
 * - Orchestrator (trusted) reports usage off-chain, settles on-chain periodically
 * - Nodes withdraw their accumulated payouts
 * - Platform fee is retained in contract
 */
contract DeCloudEscrow is Ownable, ReentrancyGuard {
    using SafeERC20 for IERC20;

    // ═══════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ═══════════════════════════════════════════════════════════════════
    
    uint256 public constant PLATFORM_FEE_BPS = 1500; // 15% = 1500 basis points
    uint256 public constant BPS_DENOMINATOR = 10000;
    uint256 public constant MIN_DEPOSIT = 1e6; // 1 USDC (6 decimals)

    // ═══════════════════════════════════════════════════════════════════
    // STATE
    // ═══════════════════════════════════════════════════════════════════
    
    IERC20 public immutable paymentToken; // USDC
    address public orchestrator;
    
    // User balances (available for VM usage)
    mapping(address => uint256) public userBalances;
    
    // Node pending payouts (accumulated from usage)
    mapping(address => uint256) public nodePendingPayouts;
    
    // Platform accumulated fees
    uint256 public platformFees;
    
    // Total deposited (for analytics)
    uint256 public totalDeposited;
    uint256 public totalWithdrawn;
    uint256 public totalUsageReported;

    // ═══════════════════════════════════════════════════════════════════
    // EVENTS
    // ═══════════════════════════════════════════════════════════════════
    
    event Deposited(
        address indexed user, 
        uint256 amount, 
        uint256 newBalance,
        uint256 timestamp
    );
    
    event UsageReported(
        address indexed user,
        address indexed node,
        uint256 amount,
        uint256 nodeShare,
        uint256 platformFee,
        string vmId
    );
    
    event NodeWithdrawal(
        address indexed node, 
        uint256 amount,
        uint256 timestamp
    );
    
    event UserWithdrawal(
        address indexed user, 
        uint256 amount,
        uint256 timestamp
    );
    
    event PlatformWithdrawal(
        address indexed to, 
        uint256 amount
    );
    
    event OrchestratorUpdated(
        address indexed oldOrchestrator, 
        address indexed newOrchestrator
    );

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIERS
    // ═══════════════════════════════════════════════════════════════════
    
    modifier onlyOrchestrator() {
        require(msg.sender == orchestrator, "Only orchestrator");
        _;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @param _paymentToken USDC token address
     * @param _orchestrator Initial orchestrator address
     */
    constructor(
        address _paymentToken, 
        address _orchestrator
    ) Ownable(msg.sender) {
        require(_paymentToken != address(0), "Invalid token");
        require(_orchestrator != address(0), "Invalid orchestrator");
        
        paymentToken = IERC20(_paymentToken);
        orchestrator = _orchestrator;
    }

    // ═══════════════════════════════════════════════════════════════════
    // USER FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @notice Deposit USDC to fund VM usage
     * @param amount Amount of USDC to deposit (6 decimals)
     */
    function deposit(uint256 amount) external nonReentrant {
        require(amount >= MIN_DEPOSIT, "Below minimum deposit");
        
        // Transfer tokens from user to contract
        paymentToken.safeTransferFrom(msg.sender, address(this), amount);
        
        // Credit user balance
        userBalances[msg.sender] += amount;
        totalDeposited += amount;
        
        emit Deposited(
            msg.sender, 
            amount, 
            userBalances[msg.sender],
            block.timestamp
        );
    }
    
    /**
     * @notice Withdraw unused balance
     * @param amount Amount to withdraw
     */
    function withdrawBalance(uint256 amount) external nonReentrant {
        require(amount > 0, "Zero amount");
        require(userBalances[msg.sender] >= amount, "Insufficient balance");
        
        userBalances[msg.sender] -= amount;
        totalWithdrawn += amount;
        
        paymentToken.safeTransfer(msg.sender, amount);
        
        emit UserWithdrawal(msg.sender, amount, block.timestamp);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ORCHESTRATOR FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @notice Report usage for a VM (called periodically by orchestrator)
     * @param user User who owns the VM
     * @param node Node operator running the VM
     * @param amount Total usage amount in USDC
     * @param vmId VM identifier for tracking
     */
    function reportUsage(
        address user,
        address node,
        uint256 amount,
        string calldata vmId
    ) external onlyOrchestrator nonReentrant {
        require(user != address(0), "Invalid user");
        require(node != address(0), "Invalid node");
        require(amount > 0, "Zero amount");
        require(userBalances[user] >= amount, "Insufficient user balance");
        
        // Deduct from user
        userBalances[user] -= amount;
        
        // Calculate splits
        uint256 platformFee = (amount * PLATFORM_FEE_BPS) / BPS_DENOMINATOR;
        uint256 nodeShare = amount - platformFee;
        
        // Credit node and platform
        nodePendingPayouts[node] += nodeShare;
        platformFees += platformFee;
        totalUsageReported += amount;
        
        emit UsageReported(user, node, amount, nodeShare, platformFee, vmId);
    }
    
    /**
     * @notice Batch report usage for multiple VMs
     * @param users Array of user addresses
     * @param nodes Array of node addresses
     * @param amounts Array of usage amounts
     * @param vmIds Array of VM identifiers
     */
    function batchReportUsage(
        address[] calldata users,
        address[] calldata nodes,
        uint256[] calldata amounts,
        string[] calldata vmIds
    ) external onlyOrchestrator nonReentrant {
        require(
            users.length == nodes.length && 
            nodes.length == amounts.length && 
            amounts.length == vmIds.length,
            "Array length mismatch"
        );
        require(users.length <= 100, "Batch too large");
        
        for (uint256 i = 0; i < users.length; i++) {
            address user = users[i];
            address node = nodes[i];
            uint256 amount = amounts[i];
            
            if (amount == 0 || userBalances[user] < amount) {
                continue; // Skip invalid entries
            }
            
            // Deduct from user
            userBalances[user] -= amount;
            
            // Calculate splits
            uint256 platformFee = (amount * PLATFORM_FEE_BPS) / BPS_DENOMINATOR;
            uint256 nodeShare = amount - platformFee;
            
            // Credit node and platform
            nodePendingPayouts[node] += nodeShare;
            platformFees += platformFee;
            totalUsageReported += amount;
            
            emit UsageReported(user, node, amount, nodeShare, platformFee, vmIds[i]);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @notice Node withdraws accumulated earnings
     */
    function nodeWithdraw() external nonReentrant {
        uint256 amount = nodePendingPayouts[msg.sender];
        require(amount > 0, "No pending payout");
        
        nodePendingPayouts[msg.sender] = 0;
        
        paymentToken.safeTransfer(msg.sender, amount);
        
        emit NodeWithdrawal(msg.sender, amount, block.timestamp);
    }
    
    /**
     * @notice Node withdraws specific amount
     * @param amount Amount to withdraw
     */
    function nodeWithdrawAmount(uint256 amount) external nonReentrant {
        require(amount > 0, "Zero amount");
        require(nodePendingPayouts[msg.sender] >= amount, "Insufficient payout");
        
        nodePendingPayouts[msg.sender] -= amount;
        
        paymentToken.safeTransfer(msg.sender, amount);
        
        emit NodeWithdrawal(msg.sender, amount, block.timestamp);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ADMIN FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @notice Update orchestrator address
     * @param newOrchestrator New orchestrator address
     */
    function setOrchestrator(address newOrchestrator) external onlyOwner {
        require(newOrchestrator != address(0), "Invalid address");
        
        address old = orchestrator;
        orchestrator = newOrchestrator;
        
        emit OrchestratorUpdated(old, newOrchestrator);
    }
    
    /**
     * @notice Withdraw accumulated platform fees
     * @param to Recipient address
     * @param amount Amount to withdraw
     */
    function withdrawPlatformFees(
        address to, 
        uint256 amount
    ) external onlyOwner nonReentrant {
        require(to != address(0), "Invalid address");
        require(amount <= platformFees, "Exceeds available fees");
        
        platformFees -= amount;
        
        paymentToken.safeTransfer(to, amount);
        
        emit PlatformWithdrawal(to, amount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // VIEW FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════
    
    /**
     * @notice Get user's available balance
     */
    function getBalance(address user) external view returns (uint256) {
        return userBalances[user];
    }
    
    /**
     * @notice Get node's pending payout
     */
    function getNodePayout(address node) external view returns (uint256) {
        return nodePendingPayouts[node];
    }
    
    /**
     * @notice Get contract statistics
     */
    function getStats() external view returns (
        uint256 _totalDeposited,
        uint256 _totalWithdrawn,
        uint256 _totalUsageReported,
        uint256 _platformFees,
        uint256 _contractBalance
    ) {
        return (
            totalDeposited,
            totalWithdrawn,
            totalUsageReported,
            platformFees,
            paymentToken.balanceOf(address(this))
        );
    }
}
