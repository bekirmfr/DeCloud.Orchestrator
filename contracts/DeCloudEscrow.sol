// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

import "@openzeppelin/contracts/token/ERC20/IERC20.sol";
import "@openzeppelin/contracts/token/ERC20/utils/SafeERC20.sol";
import "@openzeppelin/contracts/access/Ownable.sol";
import "@openzeppelin/contracts/utils/ReentrancyGuard.sol";
import "@openzeppelin/contracts/utils/Pausable.sol";

/**
 * @title DeCloudEscrow v3
 * @notice Escrow and settlement contract for the DeCloud compute + storage marketplace.
 *
 * ── Payment model ────────────────────────────────────────────────────────────
 *   Primary currency : USDC (stablecoin, 6 decimals)
 *   Platform token   : XDE / 0xde.cloud (variable decimals, set at init)
 *
 *   All internal accounting is in USDC-equivalent units.
 *   Platform token deposits are converted to USDC-equivalent credit at the
 *   current owner-set rate plus a configurable discount - after deposit,
 *   the billing engine is currency-agnostic.
 *
 * ── Settlement model ─────────────────────────────────────────────────────────
 *   batchReportUsage()  Ephemeral VMs (replicationFactor=0). Compute billing only.
 *   settleCycle()       Replicated VMs. Atomic compute + storage in one transaction.
 *                       Storage pool is collected and distributed in the same tx -
 *                       structurally impossible to collect without distributing.
 *
 * ── Decentralization path ────────────────────────────────────────────────────
 *   authorizedCallers mapping replaces single orchestrator address.
 *   Master nodes are added via authorizeCaller() - no redeployment needed.
 *
 * ── Staking hook ─────────────────────────────────────────────────────────────
 *   Optional IDeCloudStaking interface. When set, node compute shares receive
 *   a bonus funded from the platform fee portion. Staking logic lives in a
 *   separate contract - this contract only reads the bonus BPS.
 *
 * ── Platform token (XDE) integration ─────────────────────────────────────────
 *   Disabled at deploy (platformTokenEnabled = false).
 *   Enabled by owner once XDE token is live:
 *     setPlatformToken(xdeAddress, decimals)
 *     setPlatformTokenTreasury(multisig)
 *     setPlatformTokenEnabled(true)
 *   Collected XDE is forwarded to platformTokenTreasury (burn, staking pool, etc.)
 *   USDC solvency note: XDE deposits credit USDC-equivalent from the contract's
 *   existing USDC pool. Treasury must maintain adequate USDC reserves to cover
 *   XDE-funded credits.
 *
 * ── Migration model ──────────────────────────────────────────────────────────
 *   This contract is immutable - no proxy, no upgradeable pattern.
 *   If a new contract must be deployed, migration proceeds as follows:
 *
 *   On the NEW contract (before any deposits):
 *     1. Owner calls migrateBalances() with snapshot from old contract events.
 *        One-time only - migrationComplete flag prevents re-entry.
 *        Owner must have deposited sufficient USDC to cover imported balances.
 *
 *   On the OLD contract (after migration is verified on-chain):
 *     2. Owner calls initiateFreeze(newContractAddress).
 *        A 3-day timelock begins. FreezeInitiated event is emitted so users
 *        can see the new contract address and withdraw their balances.
 *     3. After 3 days, owner calls executeFreeze().
 *        New deposits are permanently blocked. Existing withdrawBalance() and
 *        nodeWithdraw() remain functional indefinitely - users and nodes can
 *        always claim their funds from the old contract.
 *
 *   There is NO admin function to drain user funds. The operator is responsible
 *   for funding USDC reserves on the new contract to cover migrated balances.
 *   No redeployment required for: fee changes, platform token launch,
 *   master node additions, staking activation.
 */
/// @notice Per-VM billing arrays for settleCycle(). Packed into a struct
/// to avoid stack-too-deep with 10 calldata parameters.
struct CycleVmData {
    address[] users;
    address[] computeNodes;
    uint256[] computeAmounts;
    uint256[] blockCounts;
    uint256[] blockSizeKbs;
    uint256[] replicationFactors;
    string[]  vmIds;
}

/// @notice Per-storage-node arrays for settleCycle().
struct CycleStorageData {
    address[] storageNodes;
    uint256[] storageBytes;
}

contract DeCloudEscrow is Ownable, ReentrancyGuard, Pausable {
    using SafeERC20 for IERC20;

    // ═══════════════════════════════════════════════════════════════════
    // CONSTANTS
    // ═══════════════════════════════════════════════════════════════════

    uint256 public constant BPS_DENOMINATOR          = 10000;
    /// @notice Hard cap on platform fee - protects users from admin abuse
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
     * blockSizeKbs, and replicationFactors - fully verifiable from tx calldata.
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
     * Set by owner/keeper. Not an on-chain oracle - intentionally off-chain.
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
    // MIGRATION STATE
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice True once migrateBalances() has been called.
     * Permanently prevents a second migration import on this contract.
     */
    bool public migrationComplete = false;

    /**
     * @notice True once executeFreeze() has been called on this contract.
     * Permanently blocks new deposits. Withdrawals remain open indefinitely.
     */
    bool public frozen = false;

    /**
     * @notice Timestamp after which executeFreeze() may be called.
     * Set by initiateFreeze(). Zero means freeze has not been initiated.
     */
    uint256 public freezeUnlocksAt = 0;

    /// @notice Address of the replacement contract (set by initiateFreeze).
    address public replacementContract;

    /// @notice Duration of the freeze timelock - gives users time to withdraw.
    uint256 public constant FREEZE_TIMELOCK = 3 days;

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
     * All inputs are on-chain in calldata - proportional calculation is
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

    // ── Migration ─────────────────────────────────────────────────────

    /// @notice Emitted when migrateBalances() is called on a new contract.
    event BalancesMigrated(
        uint256 userCount,
        uint256 nodeCount,
        uint256 totalUsdcImported
    );

    /// @notice Emitted when freeze is initiated. Users have FREEZE_TIMELOCK to withdraw.
    event FreezeInitiated(
        address indexed newContract,
        uint256 unlocksAt
    );

    /// @notice Emitted when freeze is executed. New deposits permanently blocked.
    event ContractFrozen(address indexed newContract);

    // ═══════════════════════════════════════════════════════════════════
    // MODIFIERS
    // ═══════════════════════════════════════════════════════════════════

    modifier onlyAuthorized() {
        require(authorizedCallers[msg.sender], "Not authorized");
        _;
    }

    modifier whenNotFrozen() {
        require(!frozen, "Contract frozen - see replacementContract");
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
    function deposit(uint256 amount) external nonReentrant whenNotPaused whenNotFrozen {
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
    ) external nonReentrant whenNotPaused whenNotFrozen {
        require(platformTokenEnabled,                "Platform token not enabled");
        require(address(platformToken) != address(0),"Platform token not set");
        require(platformTokenTreasury != address(0), "Treasury not set");
        require(tokenAmount > 0,                     "Zero amount");

        // Convert to USDC-equivalent
        // usdcBase = tokenAmount * 1e6 / (rate * 10^platformTokenDecimals)
        uint256 usdcBase   = (tokenAmount * 1e6) /
                             (platformTokenRate * (10 ** platformTokenDecimals));
        require(usdcBase > 0, "Credit rounds to zero - increase deposit");

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
    // SETTLEMENT - compute only (ephemeral VMs, replicationFactor = 0)
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
    // SETTLEMENT - atomic compute + storage (replicated VMs)
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice Atomic settlement for one billing cycle.
     *
     * Phase 1 - per-VM billing:
     *   computeAmount  → billed as compute cost
     *   storageAmount  → computed on-chain:
     *       blockCounts[i] × blockSizeKbs[i] × replicationFactors[i]
     *       × costPerMbPerHour / (1024 × 1e6)
     *
     *   Compute split: 85% → compute node, 15% → platform (adjusted by staking bonus)
     *   Storage split: 85% → storagePool, 15% → platform
     *
     * Phase 2 - storage pool distribution:
     *   Each storage node receives: storagePool × (storageBytes[j] / Σ storageBytes)
     *   Integer dust stays in storagePool and rolls over to next cycle.
     *
     * @param vmData              Per-VM billing arrays (users, computeNodes, computeAmounts,
     *                            blockCounts, blockSizeKbs, replicationFactors, vmIds)
     * @param storageData         Per-storage-node arrays (storageNodes, storageBytes)
     * @param cycleId             ISO-8601 timestamp for on-chain auditability
     */
    function settleCycle(
        CycleVmData      calldata vmData,
        CycleStorageData calldata storageData,
        string           calldata cycleId
    ) external onlyAuthorized nonReentrant whenNotPaused {
        require(
            vmData.users.length == vmData.computeNodes.length &&
            vmData.computeNodes.length == vmData.computeAmounts.length &&
            vmData.computeAmounts.length == vmData.blockCounts.length &&
            vmData.blockCounts.length == vmData.blockSizeKbs.length &&
            vmData.blockSizeKbs.length == vmData.replicationFactors.length &&
            vmData.replicationFactors.length == vmData.vmIds.length,
            "VM array length mismatch"
        );
        require(
            storageData.storageNodes.length == storageData.storageBytes.length,
            "Storage array length mismatch"
        );

        _billingPhase(vmData);
        _distributionPhase(storageData, cycleId);
    }

    /// @dev Phase 1: bill each VM - compute + storage deduction and split.
    function _billingPhase(CycleVmData calldata d) internal {
        uint256 feeBps = platformFeeBps;
        uint256 rate   = costPerMbPerHour;

        for (uint256 i = 0; i < d.users.length; i++) {
            _billVm(
                d.users[i],
                d.computeNodes[i],
                d.computeAmounts[i],
                d.blockCounts[i],
                d.blockSizeKbs[i],
                d.replicationFactors[i],
                d.vmIds[i],
                feeBps,
                rate
            );
        }
    }

    /// @dev Bill a single VM — deduct from user balance, split compute + storage.
    /// Parameters passed individually (not struct) so calldata slots stay cheap.
    /// Local variables minimised to stay within EVM 16-slot stack limit.
    function _billVm(
        address user,
        address cNode,
        uint256 compAmt,
        uint256 blockCount,
        uint256 blockSizeKb,
        uint256 replFactor,
        string calldata vmId,
        uint256 feeBps,
        uint256 rate
    ) internal {
        // Compute storageAmt inline — no named local to save a slot
        uint256 storageAmt = replFactor > 0
            ? (blockCount * blockSizeKb * replFactor * rate) / (1024 * 1e6)
            : 0;

        if (compAmt + storageAmt == 0) return;
        if (userBalances[user] < compAmt + storageAmt) return;

        userBalances[user]    -= compAmt + storageAmt;
        totalComputeSettled   += compAmt;
        totalStorageCollected += storageAmt;

        // Compute split — delegate to avoid local variable explosion
        _creditCompute(user, cNode, compAmt, storageAmt, feeBps, vmId);
    }

    /// @dev Apply compute + storage splits and emit events for one VM.
    /// Separated from _billVm to distribute local variables across two frames.
    function _creditCompute(
        address user,
        address cNode,
        uint256 compAmt,
        uint256 storageAmt,
        uint256 feeBps,
        string calldata vmId
    ) internal {
        (uint256 nodeShare, uint256 compFee) = _splitWithStakingBonus(cNode, compAmt, feeBps);
        nodePendingPayouts[cNode] += nodeShare;
        platformFees              += compFee;
        emit UsageReported(user, cNode, compAmt, nodeShare, compFee, vmId);

        if (storageAmt > 0) {
            uint256 storeFee  = (storageAmt * feeBps) / BPS_DENOMINATOR;
            storagePool      += storageAmt - storeFee;
            platformFees     += storeFee;
            emit StorageCollected(user, vmId, storageAmt, storeFee, storageAmt - storeFee);
        }

        _checkLowBalance(user);
    }

    /// @dev Phase 2: distribute storagePool proportionally to storage nodes.
    /// Extracted to avoid stack-too-deep on settleCycle's 10 calldata params.
    function _distributionPhase(
        CycleStorageData calldata s,
        string           calldata cycleId
    ) internal {
        if (storagePool == 0 || s.storageNodes.length == 0) return;

        uint256 totalBytes = 0;
        for (uint256 j = 0; j < s.storageBytes.length; j++) {
            totalBytes += s.storageBytes[j];
        }
        if (totalBytes == 0) return;

        uint256 pool        = storagePool;
        uint256 distributed = 0;

        for (uint256 j = 0; j < s.storageNodes.length; j++) {
            uint256 reward = (pool * s.storageBytes[j]) / totalBytes;
            if (reward == 0) continue;

            nodePendingPayouts[s.storageNodes[j]] += reward;
            distributed                           += reward;
            totalStorageDistributed               += reward;

            emit StorageRewarded(s.storageNodes[j], reward, s.storageBytes[j], totalBytes, cycleId);
        }

        // Integer dust rolls over - never lost
        storagePool -= distributed;
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
     * the USDC balance - this view helps the treasury monitor reserves.
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
     * Bonus is funded from the platform fee - platform takes less, node earns more.
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
                // Bonus funded from fee - cap at available fee to stay solvent
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

    // ═══════════════════════════════════════════════════════════════════
    // MIGRATION
    // ═══════════════════════════════════════════════════════════════════

    /**
     * @notice One-time import of balances from a previous contract.
     * Called by owner on a NEW contract before any user deposits.
     * Owner must have transferred sufficient USDC to this contract first
     * to cover all imported user balances and node payouts.
     *
     * Permanently sets migrationComplete = true after execution - cannot
     * be called again, preventing double-import attacks.
     *
     * Data source: enumerate Deposited, UsageReported, NodeWithdrawal,
     * and UserWithdrawal events from the old contract to compute net balances.
     *
     * @param users        Addresses with non-zero user balances on old contract
     * @param usdcBalances Corresponding USDC-equivalent balances (6 decimals)
     * @param nodes        Addresses with non-zero node payouts on old contract
     * @param nodePayouts  Corresponding pending payouts (6 decimals)
     * @param _storagePool Remaining storagePool dust from old contract
     * @param _platformFees Remaining platformFees from old contract
     */
    function migrateBalances(
        address[] calldata users,
        uint256[] calldata usdcBalances,
        address[] calldata nodes,
        uint256[] calldata nodePayouts,
        uint256 _storagePool,
        uint256 _platformFees
    ) external onlyOwner {
        require(!migrationComplete,               "Migration already complete");
        require(users.length == usdcBalances.length, "User array mismatch");
        require(nodes.length == nodePayouts.length,  "Node array mismatch");

        uint256 totalImported = 0;

        for (uint256 i = 0; i < users.length; i++) {
            require(users[i] != address(0), "Invalid user address");
            userBalances[users[i]] += usdcBalances[i];
            totalImported          += usdcBalances[i];
        }

        for (uint256 i = 0; i < nodes.length; i++) {
            require(nodes[i] != address(0), "Invalid node address");
            nodePendingPayouts[nodes[i]] += nodePayouts[i];
            totalImported                += nodePayouts[i];
        }

        storagePool  += _storagePool;
        platformFees += _platformFees;
        totalImported += _storagePool + _platformFees;

        // Verify contract holds enough USDC to cover all imported liabilities
        require(
            paymentToken.balanceOf(address(this)) >= totalImported,
            "Insufficient USDC to cover imported balances"
        );

        totalDeposited    += totalImported;
        migrationComplete  = true;

        emit BalancesMigrated(users.length, nodes.length, totalImported);
    }

    /**
     * @notice Initiate the freeze process on this (old) contract.
     * Starts a 3-day timelock. Users see FreezeInitiated event with the
     * new contract address and can withdraw their balances during this window.
     *
     * @param newContract Address of the replacement contract for user reference
     */
    function initiateFreeze(address newContract) external onlyOwner {
        require(!frozen,             "Already frozen");
        require(freezeUnlocksAt == 0, "Freeze already initiated");
        require(newContract != address(0), "Invalid new contract");

        freezeUnlocksAt      = block.timestamp + FREEZE_TIMELOCK;
        replacementContract  = newContract;

        emit FreezeInitiated(newContract, freezeUnlocksAt);
    }

    /**
     * @notice Execute the freeze after the timelock has elapsed.
     * Permanently blocks new deposits on this contract.
     * withdrawBalance() and nodeWithdraw() remain functional indefinitely.
     */
    function executeFreeze() external onlyOwner {
        require(!frozen,              "Already frozen");
        require(freezeUnlocksAt > 0,  "Freeze not initiated");
        require(block.timestamp >= freezeUnlocksAt, "Timelock not elapsed");

        frozen = true;
        if (!paused()) _pause(); // also pause settlement calls on the old contract

        emit ContractFrozen(replacementContract);
    }
}

// ═══════════════════════════════════════════════════════════════════════
// STAKING INTERFACE
// ═══════════════════════════════════════════════════════════════════════

/**
 * @notice Interface for the DeCloud staking contract.
 * Implemented by a separate contract - escrow only reads the bonus.
 */
interface IDeCloudStaking {
    /**
     * @notice Returns compute earnings bonus for a node in BPS.
     * e.g. 500 = node earns 5% extra on top of standard nodeShare.
     * Returns 0 if node has no stake or staking is inactive.
     */
    function nodeComputeBonusBps(address node) external view returns (uint256);
}