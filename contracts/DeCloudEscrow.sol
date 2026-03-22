// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import "@openzeppelin/contracts/access/Ownable.sol";
import "@openzeppelin/contracts/utils/ReentrancyGuard.sol";
import "@openzeppelin/contracts/utils/Pausable.sol";

/**
 * @title DeCloudEscrow v2
 * @notice Escrow and settlement contract for the DeCloud compute + storage marketplace.
 *
 * ── Payment model ────────────────────────────────────────────────────────────
 *   Primary currency : USDC (stablecoin, 6 decimals)
 *   Platform token   : XDE / 0xde.cloud (variable decimals, set at init)
 *
 *   All internal accounting is in USDC-equivalent units.
 *   Platform token deposits are converted to USDC-equivalent credit at the
 *   current owner-set rate plus a configurable discount — after deposit,
 *   the billing engine is currency-agnostic.
 *
 * ── Settlement model ─────────────────────────────────────────────────────────
 *   batchReportUsage()  Ephemeral VMs (replicationFactor=0). Compute billing only.
 *   settleCycle()       Replicated VMs. Atomic compute + storage in one transaction.
 *                       Storage pool is collected and distributed in the same tx —
 *                       structurally impossible to collect without distributing.
 *
 * ── Decentralization path ────────────────────────────────────────────────────
 *   authorizedCallers mapping replaces single orchestrator address.
 *   Master nodes are added via authorizeCaller() — no redeployment needed.
 *
 * ── Staking hook ─────────────────────────────────────────────────────────────
 *   Optional IDeCloudStaking interface. When set, node compute shares receive
 *   a bonus funded from the platform fee portion. Staking logic lives in a
 *   separate contract — this contract only reads the bonus BPS.
 *
 * ── Platform token (XDE) integration ─────────────────────────────────────────
 *   Disabled at deploy (platformTokenEnabled = false).
 *   Enabled by owner once XDE token is live:
 *     setPlatformToken(xdeAddress)
 *     setPlatformTokenTreasury(multisig)
 *     setPlatformTokenEnabled(true)
 *   Collected XDE is forwarded to platformTokenTreasury (burn, staking pool, etc.)
 *   USDC solvency note: XDE deposits credit USDC-equivalent from the contract's
 *   existing USDC pool. Treasury must maintain adequate USDC reserves to cover
 *   XDE-funded credits.
 */
contract DeCloudEscrow is Ownable, ReentrancyGuard, Pausable {
    using SafeERC20 for IERC20;

    // ═══════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ═══════════════════════════════════════════════════════════════════

    uint256 public constant BPS_DENOMINATOR          = 10000;
    /// @notice Hard cap on platform fee — protects users from admin abuse
    uint256 public constant MAX_FEE_BPS              = 3000;  // 30%
    /// @notice Hard cap on platform token discount
    uint256 public constant MAX_PLATFORM_TOKEN_DISCOUNT_BPS = 5000; // 50%

    // ═══════════════════════════════════════════════════════════════════
    // CONFIGURABLE PARAMETERS (owner-settable)
    // ═══════════════════════════════════════════════════════════════════

    /// @notice Platform fee in basis points (default 15%)
    uint256 public platformFeeBps       = 1500;

    /// @notice Minimum USDC deposit (default 1 USDC = 1e6)
    uint256 public minDeposit           = 1e6;

    /**
     * @notice On-chain storage rate reference: micro-USDC per MB per hour.
     * e.g. 1 = 0.000001 USDC/MB/hr.
     * Used by settleCycle() to compute storageAmount on-chain from blockCounts,
     * blockSizeKbs, and replicationFactors — fully verifiable from tx calldata.
     */
    uint256 public costPerMbPerHour     = 1;

    // ═══════════════════════════════════════════════════════════════════
    // AUTHORIZATION
    // ═══════════════════════════════════════════════════════════════════

    /// @notice Authorized settlement callers (orchestrator + future master nodes)
    mapping(address => bool) public authorizedCallers;

    // ═══════════════════════════════════════════════════════════════════
    // PAYMENT TOKEN (USDC)
    // ═══════════════════════════════════════════════════════════════════

    /// @notice Primary payment token (USDC). Immutable after deploy.
    IERC20 public immutable paymentToken;

    // ═══════════════════════════════════════════════════════════════════
    // PLATFORM TOKEN (XDE / 0xde.cloud)
    // ═══════════════════════════════════════════════════════════════════

    /// @notice Platform token contract (address(0) until token launches)
    IERC20 public platformToken;

    /**
     * @notice Platform token decimals. Must be set when platformToken is set.
     * Stored explicitly because IERC20 does not expose decimals().
     * Used to normalise amounts: usdcBase = amount * 1e6 / (rate * 10^platformTokenDecimals)
     */
    uint8 public platformTokenDecimals  = 18;

    /**
     * @notice Platform token rate: how many platform tokens equal 1 USDC.
     * e.g. 1000 = 1000 XDE per 1 USDC (6 decimal USDC).
     * Set by owner/keeper. Not an on-chain oracle — intentionally off-chain.
     */
    uint256 public platformTokenRate    = 1000;

    /**
     * @notice Discount for paying in platform token, in BPS.
     * e.g. 2000 = 20% bonus credit.
     * Depositing 1000 XDE at rate 1000 + 20% discount → 1.20 USDC credit.
     */
    uint256 public platformTokenDiscountBps = 2000;

    /// @notice Destination for collected platform tokens (treasury, burn, staking pool)
    address public platformTokenTreasury;

    /// @notice Whether platform token deposits are currently accepted
    bool public platformTokenEnabled    = false;

    // ═══════════════════════════════════════════════════════════════════
    // BALANCES
    // ═══════════════════════════════════════════════════════════════════

    /// @notice User USDC-equivalent balances (covers both USDC and platform token deposits)
    mapping(address => uint256) public userBalances;

    /// @notice Accumulated compute + storage earnings per node operator
    mapping(address => uint256) public nodePendingPayouts;

    /// @notice Platform fee accumulator
    uint256 public platformFees;

    // ═══════════════════════════════════════════════════════════════════
    // STORAGE POOL (settleCycle only)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Undistributed storage pool dust from integer division.
     * Rolls over automatically to the next settleCycle() call.
     * In normal operation this is a tiny fractional amount.
     */
    uint256 public storagePool;

    // ═══════════════════════════════════════════════════════════════════
    // ANALYTICS
    // ═══════════════════════════════════════════════════════════════════

    uint256 public totalDeposited;                // USDC-equivalent lifetime deposits
    uint256 public totalUserWithdrawn;            // USDC withdrawn by users
    uint256 public totalNodeWithdrawn;            // USDC withdrawn by node operators
    uint256 public totalComputeSettled;           // Compute billing lifetime
    uint256 public totalStorageCollected;         // Storage billing lifetime
    uint256 public totalStorageDistributed;       // Storage rewards distributed lifetime
    uint256 public totalPlatformTokenCollected;   // Platform tokens received lifetime (raw units)
    uint256 public totalPlatformTokenUsdcCredit;  // USDC-equivalent credited for platform token deposits

    // ═══════════════════════════════════════════════════════════════════
    // STAKING HOOK
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Optional staking contract. address(0) = no staking bonus active.
     * When set, node compute shares receive a bonus funded from platform fee.
     * All staking logic (lock periods, slashing, rewards) lives in that contract.
     */
    IDeCloudStaking public stakingContract;

    // ═══════════════════════════════════════════════════════════════════
    // EVENTS
    // ═══════════════════════════════════════════════════════════════════

    // ── User ──────────────────────────────────────────────────────────

    /// @notice Emitted on USDC deposit
    event Deposited(
        address indexed user,
        uint256 amount,
        uint256 newBalance
    );

    /// @notice Emitted on platform token deposit
    event PlatformTokenDeposited(
        address indexed user,
        uint256 tokenAmount,
        uint256 usdcEquivalent,
        uint256 rateUsed,
        uint256 discountBpsUsed
    );

    event UserWithdrawal(address indexed user, uint256 amount);

    /**
     * @notice Emitted when user balance drops below threshold after settlement.
     * Threshold = minDeposit × 5. Off-chain monitoring hook for the orchestrator.
     */
    event LowBalance(address indexed user, uint256 balance, uint256 threshold);

    // ── Node ──────────────────────────────────────────────────────────

    event NodeWithdrawal(address indexed node, uint256 amount);

    // ── Settlement ────────────────────────────────────────────────────

    /// @notice Emitted per VM by batchReportUsage and settleCycle (compute portion)
    event UsageReported(
        address indexed user,
        address indexed node,
        uint256 amount,
        uint256 nodeShare,
        uint256 platformFee,
        string  vmId
    );

    /// @notice Emitted per VM by settleCycle when storage cost is collected
    event StorageCollected(
        address indexed user,
        string  indexed vmId,
        uint256 storageAmount,
        uint256 platformFee,
        uint256 poolContribution
    );

    /**
     * @notice Emitted per storage node per settleCycle.
     * All inputs are on-chain in calldata — proportional calculation is
     * independently verifiable: reward = pool × contributedBytes / totalNetworkBytes.
     */
    event StorageRewarded(
        address indexed storageNode,
        uint256 rewardAmount,
        uint256 contributedBytes,
        uint256 totalNetworkBytes,
        string  cycleId
    );

    // ── Admin ─────────────────────────────────────────────────────────

    event PlatformWithdrawal(address indexed to, uint256 amount);
    event CallerAuthorized(address indexed caller);
    event CallerRevoked(address indexed caller);
    event PlatformFeeBpsUpdated(uint256 oldBps, uint256 newBps);
    event MinDepositUpdated(uint256 oldMin, uint256 newMin);
    event CostPerMbPerHourUpdated(uint256 oldRate, uint256 newRate);

    event PlatformTokenUpdated(address oldToken, address newToken, uint8 decimals);
    event PlatformTokenRateUpdated(uint256 oldRate, uint256 newRate);
    event PlatformTokenDiscountUpdated(uint256 oldBps, uint256 newBps);
    event PlatformTokenTreasuryUpdated(address oldTreasury, address newTreasury);
    event PlatformTokenEnabledUpdated(bool enabled);

    event StakingContractUpdated(address oldContract, address newContract);

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIERS
    // ═══════════════════════════════════════════════════════════════════

    modifier onlyAuthorized() {
        require(authorizedCallers[msg.sender], "Not authorized");
        _;
    }

    // ═══════════════════════════════════════════════════════════════════
    // CONSTRUCTOR
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @param _paymentToken  USDC token address (immutable)
     * @param _orchestrator  Initial authorized caller (orchestrator)
     */
    constructor(
        address _paymentToken,
        address _orchestrator
    ) Ownable(msg.sender) {
        require(_paymentToken != address(0), "Invalid payment token");
        require(_orchestrator != address(0), "Invalid orchestrator");

        paymentToken                     = IERC20(_paymentToken);
        authorizedCallers[_orchestrator] = true;

        emit CallerAuthorized(_orchestrator);
    }

    // ═══════════════════════════════════════════════════════════════════
    // USER FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Deposit USDC to fund VM usage.
     * @param amount Amount of USDC (6 decimals)
     */
    function deposit(uint256 amount) external nonReentrant whenNotPaused {
        require(amount >= minDeposit, "Below minimum deposit");

        paymentToken.safeTransferFrom(msg.sender, address(this), amount);

        userBalances[msg.sender] += amount;
        totalDeposited           += amount;

        emit Deposited(msg.sender, amount, userBalances[msg.sender]);
    }

    /**
     * @notice Deposit platform token (XDE) to fund VM usage.
     * Platform tokens are forwarded to platformTokenTreasury.
     * User receives USDC-equivalent credit at current rate + discount.
     *
     * USDC solvency: credit is funded from the contract's USDC pool.
     * Treasury must maintain adequate USDC reserves to cover XDE-funded credits.
     *
     * @param tokenAmount Amount of platform tokens (platformTokenDecimals precision)
     */
    function depositPlatformToken(
        uint256 tokenAmount
    ) external nonReentrant whenNotPaused {
        require(platformTokenEnabled,                "Platform token not enabled");
        require(address(platformToken) != address(0),"Platform token not set");
        require(platformTokenTreasury != address(0), "Treasury not set");
        require(tokenAmount > 0,                     "Zero amount");

        // Convert to USDC-equivalent
        // usdcBase = tokenAmount * 1e6 / (rate * 10^platformTokenDecimals)
        uint256 usdcBase   = (tokenAmount * 1e6) /
                             (platformTokenRate * (10 ** platformTokenDecimals));
        require(usdcBase > 0, "Credit rounds to zero — increase deposit");

        uint256 usdcCredit = usdcBase +
                             (usdcBase * platformTokenDiscountBps / BPS_DENOMINATOR);

        // Pull tokens, forward to treasury
        platformToken.safeTransferFrom(msg.sender, platformTokenTreasury, tokenAmount);

        // Credit user balance (USDC-equivalent, funded from USDC reserves)
        userBalances[msg.sender] += usdcCredit;

        totalDeposited                   += usdcCredit;
        totalPlatformTokenCollected      += tokenAmount;
        totalPlatformTokenUsdcCredit     += usdcCredit;

        emit PlatformTokenDeposited(
            msg.sender,
            tokenAmount,
            usdcCredit,
            platformTokenRate,
            platformTokenDiscountBps
        );
    }

    /**
     * @notice Withdraw unused USDC balance.
     * @param amount Amount to withdraw (0 = full balance)
     */
    function withdrawBalance(uint256 amount) external nonReentrant {
        uint256 available = userBalances[msg.sender];
        require(available > 0, "No balance");

        uint256 toWithdraw = amount == 0 ? available : amount;
        require(toWithdraw <= available, "Insufficient balance");

        userBalances[msg.sender] -= toWithdraw;
        totalUserWithdrawn       += toWithdraw;

        paymentToken.safeTransfer(msg.sender, toWithdraw);
        emit UserWithdrawal(msg.sender, toWithdraw);
    }

    // ═══════════════════════════════════════════════════════════════════
    // NODE FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Withdraw accumulated node earnings.
     * @param amount Amount to withdraw (0 = full balance)
     */
    function nodeWithdraw(uint256 amount) external nonReentrant {
        uint256 available = nodePendingPayouts[msg.sender];
        require(available > 0, "No pending payout");

        uint256 toWithdraw = amount == 0 ? available : amount;
        require(toWithdraw <= available, "Exceeds payout balance");

        nodePendingPayouts[msg.sender] -= toWithdraw;
        totalNodeWithdrawn             += toWithdraw;

        paymentToken.safeTransfer(msg.sender, toWithdraw);
        emit NodeWithdrawal(msg.sender, toWithdraw);
    }

    // ═══════════════════════════════════════════════════════════════════
    // SETTLEMENT — compute only (ephemeral VMs, replicationFactor = 0)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Batch compute-only settlement for ephemeral VMs.
     * Use settleCycle() for VMs with replicationFactor > 0.
     */
    function batchReportUsage(
        address[] calldata users,
        address[] calldata nodes,
        uint256[] calldata amounts,
        string[]  calldata vmIds
    ) external onlyAuthorized nonReentrant whenNotPaused {
        require(
            users.length == nodes.length   &&
            nodes.length == amounts.length &&
            amounts.length == vmIds.length,
            "Array length mismatch"
        );

        uint256 feeBps = platformFeeBps;

        for (uint256 i = 0; i < users.length; i++) {
            address user   = users[i];
            address node   = nodes[i];
            uint256 amount = amounts[i];

            if (amount == 0 || userBalances[user] < amount) continue;

            userBalances[user]    -= amount;
            totalComputeSettled   += amount;

            (uint256 nodeShare, uint256 fee) =
                _splitWithStakingBonus(node, amount, feeBps);

            nodePendingPayouts[node] += nodeShare;
            platformFees             += fee;

            emit UsageReported(user, node, amount, nodeShare, fee, vmIds[i]);
            _checkLowBalance(user);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // SETTLEMENT — atomic compute + storage (replicated VMs)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Atomic settlement for one billing cycle.
     *
     * Phase 1 — per-VM billing:
     *   computeAmount  → billed as compute cost
     *   storageAmount  → computed on-chain:
     *       blockCounts[i] × blockSizeKbs[i] × replicationFactors[i]
     *       × costPerMbPerHour / (1024 × 1e6)
     *
     *   Compute split: 85% → compute node, 15% → platform (adjusted by staking bonus)
     *   Storage split: 85% → storagePool, 15% → platform
     *
     * Phase 2 — storage pool distribution:
     *   Each storage node receives: storagePool × (storageBytes[j] / Σ storageBytes)
     *   Integer dust stays in storagePool and rolls over to next cycle.
     *
     * @param users               VM owner wallets
     * @param computeNodes        Hosting node wallets
     * @param computeAmounts      Compute cost per VM in USDC-equivalent (6 decimals)
     * @param blockCounts         Manifest block count per VM (from LazysyncManager)
     * @param blockSizeKbs        Block size in KB per VM (protocol constant per ManifestType)
     * @param replicationFactors  Per-VM replication factor (0/1/3/5). 0 = ephemeral.
     * @param vmIds               VM identifiers for event tracking
     * @param storageNodes        BlockStore operator wallets
     * @param storageBytes        node.BlockStoreInfo.UsedBytes per storage node
     * @param cycleId             ISO-8601 timestamp for on-chain auditability
     */
    function settleCycle(
        address[] calldata users,
        address[] calldata computeNodes,
        uint256[] calldata computeAmounts,
        uint256[] calldata blockCounts,
        uint256[] calldata blockSizeKbs,
        uint256[] calldata replicationFactors,
        string[]  calldata vmIds,
        address[] calldata storageNodes,
        uint256[] calldata storageBytes,
        string    calldata cycleId
    ) external onlyAuthorized nonReentrant whenNotPaused {
        require(
            users.length        == computeNodes.length     &&
            computeNodes.length == computeAmounts.length   &&
            computeAmounts.length == blockCounts.length    &&
            blockCounts.length  == blockSizeKbs.length     &&
            blockSizeKbs.length == replicationFactors.length &&
            replicationFactors.length == vmIds.length,
            "VM array length mismatch"
        );
        require(
            storageNodes.length == storageBytes.length,
            "Storage array length mismatch"
        );

        uint256 feeBps = platformFeeBps;
        uint256 rate   = costPerMbPerHour;

        // ── Phase 1: bill each VM ────────────────────────────────────────
        for (uint256 i = 0; i < users.length; i++) {
            address user       = users[i];
            address cNode      = computeNodes[i];
            uint256 compAmt    = computeAmounts[i];
            uint256 replFactor = replicationFactors[i];

            // Storage cost computed on-chain for verifiability
            uint256 storageAmt = replFactor > 0
                ? (blockCounts[i] * blockSizeKbs[i] * replFactor * rate) / (1024 * 1e6)
                : 0;

            uint256 total = compAmt + storageAmt;
            if (total == 0 || userBalances[user] < total) continue;

            userBalances[user] -= total;

            // Compute split (with optional staking bonus)
            (uint256 nodeShare, uint256 compFee) =
                _splitWithStakingBonus(cNode, compAmt, feeBps);

            // Storage split (no staking bonus on storage fees)
            uint256 storeFee  = (storageAmt * feeBps) / BPS_DENOMINATOR;
            uint256 poolShare = storageAmt - storeFee;

            nodePendingPayouts[cNode] += nodeShare;
            platformFees              += compFee + storeFee;
            storagePool               += poolShare;
            totalComputeSettled       += compAmt;
            totalStorageCollected     += storageAmt;

            emit UsageReported(user, cNode, compAmt, nodeShare, compFee, vmIds[i]);
            emit StorageCollected(user, vmIds[i], storageAmt, storeFee, poolShare);
            _checkLowBalance(user);
        }

        // ── Phase 2: distribute storage pool ────────────────────────────
        if (storagePool > 0 && storageNodes.length > 0) {
            uint256 totalBytes = 0;
            for (uint256 j = 0; j < storageBytes.length; j++) {
                totalBytes += storageBytes[j];
            }

            if (totalBytes > 0) {
                uint256 pool        = storagePool;
                uint256 distributed = 0;

                for (uint256 j = 0; j < storageNodes.length; j++) {
                    uint256 reward = (pool * storageBytes[j]) / totalBytes;
                    if (reward == 0) continue;

                    nodePendingPayouts[storageNodes[j]] += reward;
                    distributed                         += reward;
                    totalStorageDistributed             += reward;

                    emit StorageRewarded(
                        storageNodes[j],
                        reward,
                        storageBytes[j],
                        totalBytes,
                        cycleId
                    );
                }

                // Integer dust rolls over — never lost
                storagePool -= distributed;
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ADMIN
    // ═══════════════════════════════════════════════════════════════════

    // ── Authorization ─────────────────────────────────────────────────

    /// @notice Add an authorized settlement caller (master node, new orchestrator)
    function authorizeCaller(address caller) external onlyOwner {
        require(caller != address(0), "Invalid address");
        authorizedCallers[caller] = true;
        emit CallerAuthorized(caller);
    }

    /// @notice Remove an authorized settlement caller
    function revokeCaller(address caller) external onlyOwner {
        authorizedCallers[caller] = false;
        emit CallerRevoked(caller);
    }

    // ── Fee & rate config ─────────────────────────────────────────────

    function setPlatformFeeBps(uint256 newBps) external onlyOwner {
        require(newBps <= MAX_FEE_BPS, "Exceeds max fee cap");
        emit PlatformFeeBpsUpdated(platformFeeBps, newBps);
        platformFeeBps = newBps;
    }

    function setMinDeposit(uint256 newMin) external onlyOwner {
        emit MinDepositUpdated(minDeposit, newMin);
        minDeposit = newMin;
    }

    function setCostPerMbPerHour(uint256 newRate) external onlyOwner {
        require(newRate > 0, "Zero rate");
        emit CostPerMbPerHourUpdated(costPerMbPerHour, newRate);
        costPerMbPerHour = newRate;
    }

    // ── Platform token config ─────────────────────────────────────────

    /**
     * @notice Set platform token contract and its decimals.
     * @param token    Platform token address (XDE)
     * @param decimals Token decimals (e.g. 18 for standard ERC20)
     */
    function setPlatformToken(
        address token,
        uint8   decimals
    ) external onlyOwner {
        require(token != address(0), "Invalid token address");
        emit PlatformTokenUpdated(address(platformToken), token, decimals);
        platformToken         = IERC20(token);
        platformTokenDecimals = decimals;
    }

    /// @notice Update platform token → USDC rate. Called by owner/keeper.
    function setPlatformTokenRate(uint256 newRate) external onlyOwner {
        require(newRate > 0, "Zero rate");
        emit PlatformTokenRateUpdated(platformTokenRate, newRate);
        platformTokenRate = newRate;
    }

    /// @notice Update discount for platform token deposits.
    function setPlatformTokenDiscount(uint256 newBps) external onlyOwner {
        require(newBps <= MAX_PLATFORM_TOKEN_DISCOUNT_BPS, "Discount too large");
        emit PlatformTokenDiscountUpdated(platformTokenDiscountBps, newBps);
        platformTokenDiscountBps = newBps;
    }

    /// @notice Set destination for collected platform tokens.
    function setPlatformTokenTreasury(address treasury) external onlyOwner {
        require(treasury != address(0), "Invalid treasury");
        emit PlatformTokenTreasuryUpdated(platformTokenTreasury, treasury);
        platformTokenTreasury = treasury;
    }

    /// @notice Enable or disable platform token deposits.
    function setPlatformTokenEnabled(bool enabled) external onlyOwner {
        platformTokenEnabled = enabled;
        emit PlatformTokenEnabledUpdated(enabled);
    }

    // ── Staking ───────────────────────────────────────────────────────

    /**
     * @notice Set optional staking contract.
     * Pass address(0) to disable staking bonuses.
     */
    function setStakingContract(address _staking) external onlyOwner {
        emit StakingContractUpdated(address(stakingContract), _staking);
        stakingContract = IDeCloudStaking(_staking);
    }

    // ── Emergency ─────────────────────────────────────────────────────

    function pause()   external onlyOwner { _pause(); }
    function unpause() external onlyOwner { _unpause(); }

    // ── Fee withdrawal ────────────────────────────────────────────────

    function withdrawPlatformFees(
        address to,
        uint256 amount
    ) external onlyOwner nonReentrant {
        require(to != address(0),       "Invalid address");
        require(amount <= platformFees, "Exceeds available fees");
        platformFees -= amount;
        paymentToken.safeTransfer(to, amount);
        emit PlatformWithdrawal(to, amount);
    }

    // ═══════════════════════════════════════════════════════════════════
    // VIEW FUNCTIONS
    // ═══════════════════════════════════════════════════════════════════

    function getBalance(address user)   external view returns (uint256) {
        return userBalances[user];
    }

    function getNodePayout(address node) external view returns (uint256) {
        return nodePendingPayouts[node];
    }

    /**
     * @notice Full contract statistics for off-chain dashboards.
     */
    function getStats() external view returns (
        uint256 _totalDeposited,
        uint256 _totalUserWithdrawn,
        uint256 _totalNodeWithdrawn,
        uint256 _totalComputeSettled,
        uint256 _totalStorageCollected,
        uint256 _totalStorageDistributed,
        uint256 _platformFees,
        uint256 _storagePool,
        uint256 _totalPlatformTokenCollected,
        uint256 _totalPlatformTokenUsdcCredit,
        uint256 _contractBalance
    ) {
        return (
            totalDeposited,
            totalUserWithdrawn,
            totalNodeWithdrawn,
            totalComputeSettled,
            totalStorageCollected,
            totalStorageDistributed,
            platformFees,
            storagePool,
            totalPlatformTokenCollected,
            totalPlatformTokenUsdcCredit,
            paymentToken.balanceOf(address(this))
        );
    }

    /**
     * @notice Solvency check: USDC balance vs known liabilities.
     * Platform token deposits create a USDC liability not reflected in
     * the USDC balance — this view helps the treasury monitor reserves.
     * Note: userBalances sum requires off-chain indexing from Deposited events.
     */
    function isSolvent() external view returns (bool solvent, uint256 surplus) {
        uint256 usdcBalance  = paymentToken.balanceOf(address(this));
        uint256 liabilities  = platformFees + storagePool;
        solvent = usdcBalance >= liabilities;
        surplus = solvent ? usdcBalance - liabilities : 0;
    }

    // ═══════════════════════════════════════════════════════════════════
    // INTERNAL
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @dev Split amount between node and platform, applying staking bonus if set.
     * Bonus is funded from the platform fee — platform takes less, node earns more.
     * If staking contract is not set or returns 0, standard split applies.
     */
    function _splitWithStakingBonus(
        address node,
        uint256 amount,
        uint256 feeBps
    ) internal view returns (uint256 nodeShare, uint256 fee) {
        fee       = (amount * feeBps) / BPS_DENOMINATOR;
        nodeShare = amount - fee;

        if (address(stakingContract) != address(0)) {
            uint256 bonusBps = stakingContract.nodeComputeBonusBps(node);
            if (bonusBps > 0) {
                uint256 bonus = (nodeShare * bonusBps) / BPS_DENOMINATOR;
                // Bonus funded from fee — cap at available fee to stay solvent
                if (bonus > fee) bonus = fee;
                nodeShare += bonus;
                fee       -= bonus;
            }
        }
    }

    /**
     * @dev Emit LowBalance when user balance drops below 5× minDeposit.
     * Signals to the orchestrator that the user is running low on credits.
     */
    function _checkLowBalance(address user) internal {
        uint256 threshold = minDeposit * 5;
        if (userBalances[user] < threshold) {
            emit LowBalance(user, userBalances[user], threshold);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
// STAKING INTERFACE
// ═══════════════════════════════════════════════════════════════════════

/**
 * @notice Interface for the DeCloud staking contract.
 * Implemented by a separate contract — escrow only reads the bonus.
 */
interface IDeCloudStaking {
    /**
     * @notice Returns compute earnings bonus for a node in BPS.
     * e.g. 500 = node earns 5% extra on top of standard nodeShare.
     * Returns 0 if node has no stake or staking is inactive.
     */
    function nodeComputeBonusBps(address node) external view returns (uint256);
}
