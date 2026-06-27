using DeCloud.Shared.Enums;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Scoped admin facade for enforcement. Suspends/unsuspends users and stops their VMs;
/// delegates denylist + audit storage to the singleton IWalletBlocklistService.
/// Withhold-of-service only — never touches escrow funds.
/// </summary>
public sealed class EnforcementService : IEnforcementService
{
    private readonly IWalletBlocklistService _blocklist;
    private readonly IUserService _userService;
    private readonly IVmService _vmService;
    private readonly INodeService _nodeService;
    private readonly DataStore _dataStore;
    private readonly ILogger<EnforcementService> _logger;
    private readonly AddressUtil _addr = new();

    public EnforcementService(
        IWalletBlocklistService blocklist,
        IUserService userService,
        IVmService vmService,
        INodeService nodeService,
        DataStore dataStore,
        ILogger<EnforcementService> logger)
    {
        _blocklist = blocklist;
        _userService = userService;
        _vmService = vmService;
        _nodeService = nodeService;
        _dataStore = dataStore;
        _logger = logger;
    }

    public async Task<EnforcementResult> CutoffOperatorNodesNowAsync(string walletAddress, string reason, string actor, CancellationToken ct = default)
    {
        var wallet = _addr.ConvertToChecksumAddress(walletAddress);

        var nodes = await _dataStore.GetAllNodesAsync();
        var suspended = nodes
            .Where(n => string.Equals(n.WalletAddress, wallet, StringComparison.OrdinalIgnoreCase)
                     && n.Status == NodeStatus.Suspended)
            .ToList();

        if (suspended.Count == 0)
            return EnforcementResult.Fail("NO_SUSPENDED_NODES",
                "No suspended nodes for this wallet. Suspend or block the operator first, " +
                "then escalate to immediate cutoff.");

        var count = 0;
        foreach (var node in suspended)
        {
            try
            {
                await _nodeService.CutoffSuspendedNodeNowAsync(node.Id, reason, ct);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Immediate cutoff failed for node {NodeId}", node.Id);
            }
        }

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.TerminateVms,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new()
            {
                ["mode"] = "immediate-node-cutoff",
                ["nodesCutoff"] = count.ToString()
            }
        }, ct);

        _logger.LogWarning("Immediate cutoff for {Wallet}: {Count} node(s) severed", wallet, count);
        return EnforcementResult.Ok(count);
    }

    public async Task<EnforcementResult> SuspendAsync(string walletAddress, string reason, string actor, CancellationToken ct = default)
    {
        var wallet = _addr.ConvertToChecksumAddress(walletAddress);
        var user = await _userService.GetUserByIdAsync(wallet);
        if (user == null)
            return EnforcementResult.Fail("USER_NOT_FOUND", "No such wallet. To block a wallet with no account, use a denylist block instead.");

        if (user.Status != UserStatus.Suspended)
        {
            user.Status = UserStatus.Suspended;
            await _userService.UpdateUserAsync(user);
        }

        // Withhold service: stop the wallet's running VMs and suspend any nodes it
        // operates. Stop (not delete) — reversible, disk state preserved. Existing
        // tenant VMs on a suspended node keep running; graceful drain is a separate
        // step, and login is gated so the operator cannot re-enable scheduling.
        var (stopped, nodesSuspended) = await WithholdServiceAsync(wallet, ct);

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Suspend,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new()
            {
                ["vmsStopped"] = stopped.ToString(),
                ["nodesSuspended"] = nodesSuspended.ToString()
            }
        }, ct);

        _logger.LogInformation(
            "Suspended {Wallet}; stopped {VmCount} VM(s), suspended {NodeCount} node(s)",
            wallet, stopped, nodesSuspended);
        return EnforcementResult.Ok(stopped);
    }

    public async Task<EnforcementResult> UnsuspendAsync(string walletAddress, string reason, string actor, CancellationToken ct = default)
    {
        var wallet = _addr.ConvertToChecksumAddress(walletAddress);
        var user = await _userService.GetUserByIdAsync(wallet);
        if (user == null)
            return EnforcementResult.Fail("USER_NOT_FOUND", "No such wallet.");

        if (user.Status == UserStatus.Suspended)
        {
            user.Status = UserStatus.Active;
            await _userService.UpdateUserAsync(user);
        }

        // Resume the operator's nodes only if the wallet is no longer blocked by any
        // other source (a standing sanctions/denylist block must keep them suspended).
        var nodesResumed = 0;
        if (!await _blocklist.IsWalletBlockedAsync(wallet, ct))
            nodesResumed = await ResumeOperatorNodesAsync(wallet, ct);

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Unsuspend,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new() { ["nodesResumed"] = nodesResumed.ToString() }
        }, ct);
        return EnforcementResult.Ok(0);
    }

    public async Task<EnforcementResult> SuspendVmAsync(string vmId, string reason, string actor, CancellationToken ct = default)
    {
        var hold = await _vmService.SetVmComplianceHoldAsync(vmId, true);
        if (!hold.Success)
            return EnforcementResult.Fail(hold.Error ?? "HOLD_FAILED",
                hold.Error == "SYSTEM_VM" ? "System VMs cannot be suspended." : "VM not found.");

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = hold.OwnerId ?? "",
            Type = EnforcementActionType.SuspendVm,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new() { ["vmId"] = vmId, ["vmStopped"] = hold.VmStopped.ToString() }
        }, ct);
        return EnforcementResult.Ok(hold.VmStopped ? 1 : 0);
    }

    public async Task<EnforcementResult> ResumeVmAsync(string vmId, string reason, string actor, CancellationToken ct = default)
    {
        var hold = await _vmService.SetVmComplianceHoldAsync(vmId, false);
        if (!hold.Success)
            return EnforcementResult.Fail(hold.Error ?? "HOLD_FAILED",
                hold.Error == "SYSTEM_VM" ? "System VMs are not subject to holds." : "VM not found.");

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = hold.OwnerId ?? "",
            Type = EnforcementActionType.ResumeVm,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new() { ["vmId"] = vmId }
        }, ct);
        return EnforcementResult.Ok(0);
    }

    // ── Withhold-of-service primitives ───────────────────────────────────────

    /// <summary>
    /// Stop the wallet's running VMs (reversible — disk state preserved) and suspend
    /// any nodes it operates. Node suspension pauses scheduling and marks the node
    /// Suspended; existing tenant VMs keep running (graceful drain/cutoff is a
    /// separate step) and login is gated so the operator cannot re-enable scheduling.
    /// Never touches funds.
    /// </summary>
    private async Task<(int vmsStopped, int nodesSuspended)> WithholdServiceAsync(
        string walletAddress, CancellationToken ct)
    {
        var wallet = _addr.ConvertToChecksumAddress(walletAddress);

        var vms = await _vmService.GetVmsByUserAsync(wallet);
        var vmsStopped = 0;
        foreach (var vm in vms.Where(v =>
                     v.Status is not (VmStatus.Stopped or VmStatus.Stopping
                         or VmStatus.Deleted or VmStatus.Deleting)))
        {
            if (await _vmService.PerformVmActionAsync(vm.Id, VmAction.Stop))
                vmsStopped++;
        }

        var nodesSuspended = await SuspendOperatorNodesAsync(wallet, ct);
        return (vmsStopped, nodesSuspended);
    }

    /// <summary>
    /// Pause scheduling and mark Suspended on every node the wallet operates.
    /// Idempotent. Address match is case-insensitive (Ethereum addresses differ only
    /// by checksum casing, so this is exact regardless of stored casing).
    /// </summary>
    private async Task<int> SuspendOperatorNodesAsync(string walletAddress, CancellationToken ct)
    {
        var nodes = await _dataStore.GetAllNodesAsync();
        var count = 0;
        foreach (var node in nodes.Where(n =>
                     string.Equals(n.WalletAddress, walletAddress, StringComparison.OrdinalIgnoreCase)))
        {
            if (node.Status == NodeStatus.Suspended && !node.IsSchedulingReady)
                continue; // already enforced

            node.IsSchedulingReady = false;
            node.Status = NodeStatus.Suspended;
            await _dataStore.SaveNodeAsync(node);

            _logger.LogWarning(
                "Node {NodeId} suspended — operator {Wallet} blocked; scheduling paused",
                node.Id, walletAddress);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Lift node suspension for a wallet. Sets Status back to Online (heartbeat/offline
    /// sweep corrects it if the node is actually down) and leaves IsSchedulingReady
    /// false — the operator must log in again to resume scheduling. Only call when the
    /// wallet is no longer blocked by any source.
    /// </summary>
    private async Task<int> ResumeOperatorNodesAsync(string walletAddress, CancellationToken ct)
    {
        var nodes = await _dataStore.GetAllNodesAsync();
        var count = 0;
        foreach (var node in nodes.Where(n =>
                     string.Equals(n.WalletAddress, walletAddress, StringComparison.OrdinalIgnoreCase)
                     && n.Status == NodeStatus.Suspended))
        {
            node.Status = NodeStatus.Online;
            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Node {NodeId} un-suspended — operator {Wallet} cleared; operator must log in to resume scheduling",
                node.Id, walletAddress);
            count++;
        }
        return count;
    }

    public async Task BlockAsync(string walletAddress, BlockSource source, string reason, string? reference, string actor, CancellationToken ct = default)
    {
        await _blocklist.AddBlockAsync(walletAddress, source, reason, reference, actor, ct);

        // A targeted block withholds service immediately: stop the wallet's running
        // VMs and suspend any nodes it operates. Bulk import is data-only — it does
        // NOT enforce per wallet (see BulkImportBlocksAsync).
        var (vmsStopped, nodesSuspended) = await WithholdServiceAsync(walletAddress, ct);
        _logger.LogInformation(
            "Blocked {Wallet} ({Source}); stopped {VmCount} VM(s), suspended {NodeCount} node(s)",
            walletAddress, source, vmsStopped, nodesSuspended);
    }

    public async Task<bool> UnblockAsync(string walletAddress, BlockSource source, string reason, string actor, CancellationToken ct = default)
    {
        var removed = await _blocklist.RemoveBlockAsync(walletAddress, source, actor, reason, ct);

        // Resume nodes only once the wallet is clear of every source AND not
        // internally suspended (IsWalletBlockedAsync covers both).
        if (removed && !await _blocklist.IsWalletBlockedAsync(walletAddress, ct))
        {
            var wallet = _addr.ConvertToChecksumAddress(walletAddress);
            var nodesResumed = await ResumeOperatorNodesAsync(wallet, ct);
            _logger.LogInformation(
                "Unblocked {Wallet} ({Source}); resumed {NodeCount} node(s)",
                walletAddress, source, nodesResumed);
        }
        return removed;
    }

    public Task<int> BulkImportBlocksAsync(IEnumerable<string> wallets, BlockSource source, string reason, string actor, CancellationToken ct = default)
        => _blocklist.BulkImportAsync(wallets, source, reason, actor, ct);

    public Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default)
        => _blocklist.ListBlocksAsync(walletAddress, ct);

    public Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null, CancellationToken ct = default)
        => _blocklist.GetActionsAsync(walletAddress, ct: ct);
}
