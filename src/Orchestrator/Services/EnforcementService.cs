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
    private readonly ITemplateService _templateService;
    private readonly IAbuseReportService _abuse;
    private readonly DataStore _dataStore;
    private readonly ILogger<EnforcementService> _logger;
    private readonly AddressUtil _addr = new();

    public EnforcementService(
        IWalletBlocklistService blocklist,
        IUserService userService,
        IVmService vmService,
        INodeService nodeService,
        ITemplateService templateService,
        IAbuseReportService abuse,
        DataStore dataStore,
        ILogger<EnforcementService> logger)
    {
        _blocklist = blocklist;
        _userService = userService;
        _vmService = vmService;
        _nodeService = nodeService;
        _templateService = templateService;
        _abuse = abuse;
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

    public async Task<EnforcementResult> SuspendAsync(string walletAddress, string reason, string actor, string? reference = null, CancellationToken ct = default)
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
        var (stopped, nodesSuspended, templatesArchived) = await WithholdServiceAsync(wallet, ct);

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Suspend,
            Reason = reason,
            Reference = reference,
            ActorWallet = actor,
            Metadata = new()
            {
                ["vmsStopped"] = stopped.ToString(),
                ["nodesSuspended"] = nodesSuspended.ToString(),
                ["templatesArchived"] = templatesArchived.ToString()
            }
        }, ct);

        _logger.LogInformation(
            "Suspended {Wallet}; stopped {VmCount} VM(s), suspended {NodeCount} node(s), archived {TemplateCount} template(s)",
            wallet, stopped, nodesSuspended, templatesArchived);
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

    public async Task<EnforcementResult> SuspendVmAsync(string vmId, string reason, string actor, string? reference = null, CancellationToken ct = default)
    {
        var hold = await _vmService.SetVmComplianceHoldAsync(vmId, true, reference);
        if (!hold.Success)
            return EnforcementResult.Fail(hold.Error ?? "HOLD_FAILED",
                hold.Error == "SYSTEM_VM" ? "System VMs cannot be suspended." : "VM not found.");

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = hold.OwnerId ?? "",
            Type = EnforcementActionType.SuspendVm,
            Reason = reason,
            Reference = reference,
            ActorWallet = actor,
            Metadata = new() { ["vmId"] = vmId, ["vmStopped"] = hold.VmStopped.ToString() }
        }, ct);
        return EnforcementResult.Ok(hold.VmStopped ? 1 : 0);
    }

    public async Task<EnforcementResult> ResumeVmAsync(string vmId, string reason, string actor, CancellationToken ct = default)
    {
        var hold = await _vmService.SetVmComplianceHoldAsync(vmId, false, null);
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

    public async Task<EnforcementResult> ScanVmAsync(string vmId, string reference, string reason, string actor,
        CancellationToken ct = default)
    {
        // The guard that does four jobs at once (plan §4.2): the VM must be under a
        // ComplianceHold whose reference matches the one cited. No hold ⇒ no scan;
        // wrong reference ⇒ no scan. "On cause" is structural, not policy.
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
            return EnforcementResult.Fail("VM_NOT_FOUND", "VM not found.");
        if (!vm.ComplianceHold)
            return EnforcementResult.Fail("NOT_HELD",
                "This VM is not under a compliance hold. Apply the hold first (suspend-vm).");
        if (string.IsNullOrEmpty(vm.ComplianceHoldReference) ||
            !string.Equals(vm.ComplianceHoldReference, reference, StringComparison.Ordinal))
            return EnforcementResult.Fail("REFERENCE_MISMATCH",
                "The cited report does not match the reference this VM is held under.");

        // The report must exist (the reference is provenance the reviewer will read).
        var report = await _abuse.GetByReferenceAsync(reference, ct);
        if (report == null)
            return EnforcementResult.Fail("REPORT_NOT_FOUND", "No abuse report with that reference.");

        // Issue the command first so we have the id, then record it as Ordered against
        // the report. If issue fails (no host node), nothing is recorded — nothing to undo.
        var commandId = await _vmService.RequestScanAsync(vmId, ct);
        if (commandId == null)
            return EnforcementResult.Fail("NO_HOST", "VM has no host node to scan.");

        var record = new CsamScanRecord
        {
            CommandId = commandId,
            Status = CsamScanStatus.Ordered,
            OrderedBy = actor,
            OrderedAt = DateTime.UtcNow
        };
        await _abuse.AppendScanOrderedAsync(reference, record, ct);

        // Audit row: a scan is an admin-only, reason-required, reference-bound compliance
        // action — the same KIND as everything else here, so it earns an EnforcementAction.
        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = vm.OwnerId ?? "",
            Type = EnforcementActionType.ScanVm,
            Reason = reason,
            Reference = reference,
            ActorWallet = actor,
            Metadata = new() { ["vmId"] = vmId, ["commandId"] = commandId }
        }, ct);

        _logger.LogInformation("ScanVmAsync: ordered scan {CommandId} for VM {VmId} under {Reference}",
            commandId, vmId, reference);
        return EnforcementResult.Ok(0);
    }

    // ── Withhold-of-service primitives ───────────────────────────────────────

    /// <summary>
    /// Withhold service from a wallet, on all three surfaces it can reach others through:
    /// stop its running VMs (reversible — disk state preserved), suspend any nodes it
    /// operates, and archive its Published community templates. Node suspension pauses
    /// scheduling and marks the node Suspended; existing tenant VMs keep running (graceful
    /// drain/cutoff is a separate step) and login is gated so the operator cannot re-enable
    /// scheduling. Templates are archived because they are the amplification surface — a
    /// takedown that leaves the actor's live listings deployable by everyone else is not a
    /// takedown; review cleared the content, not the author. Not reversed on unsuspend (the
    /// author republishes → re-review); nodes and VMs are infrastructure, a published
    /// template is distribution.
    /// Never touches funds.
    /// </summary>
    private async Task<(int vmsStopped, int nodesSuspended, int templatesArchived)> WithholdServiceAsync(
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
        var templatesArchived = await _templateService.ArchiveAuthorTemplatesAsync(wallet, ct);
        return (vmsStopped, nodesSuspended, templatesArchived);
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
        var (vmsStopped, nodesSuspended, templatesArchived) = await WithholdServiceAsync(walletAddress, ct);
        _logger.LogInformation(
            "Blocked {Wallet} ({Source}); stopped {VmCount} VM(s), suspended {NodeCount} node(s), archived {TemplateCount} template(s)",
            walletAddress, source, vmsStopped, nodesSuspended, templatesArchived);
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
