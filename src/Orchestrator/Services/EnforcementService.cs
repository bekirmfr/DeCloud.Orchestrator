using DeCloud.Shared.Enums;
using Nethereum.Util;
using Orchestrator.Interfaces;
using Orchestrator.Models;

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
    private readonly ILogger<EnforcementService> _logger;
    private readonly AddressUtil _addr = new();

    public EnforcementService(
        IWalletBlocklistService blocklist,
        IUserService userService,
        IVmService vmService,
        ILogger<EnforcementService> logger)
    {
        _blocklist = blocklist;
        _userService = userService;
        _vmService = vmService;
        _logger = logger;
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

        // Withhold service: stop the wallet's running VMs. Stop (not delete) — it is
        // reversible and preserves disk state for evidence or reinstatement.
        var vms = await _vmService.GetVmsByUserAsync(wallet);
        var stopped = 0;
        foreach (var vm in vms.Where(v =>
                     v.Status is not (VmStatus.Stopped or VmStatus.Stopping or VmStatus.Deleted or VmStatus.Deleting)))
        {
            if (await _vmService.PerformVmActionAsync(vm.Id, VmAction.Stop))
                stopped++;
        }

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Suspend,
            Reason = reason,
            ActorWallet = actor,
            Metadata = new() { ["vmsStopped"] = stopped.ToString() }
        }, ct);

        _logger.LogInformation("Suspended {Wallet}; stopped {Count} VM(s)", wallet, stopped);
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

        await _blocklist.RecordActionAsync(new EnforcementAction
        {
            WalletAddress = wallet,
            Type = EnforcementActionType.Unsuspend,
            Reason = reason,
            ActorWallet = actor
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

    public Task BlockAsync(string walletAddress, BlockSource source, string reason, string? reference, string actor, CancellationToken ct = default)
        => _blocklist.AddBlockAsync(walletAddress, source, reason, reference, actor, ct);

    public Task<bool> UnblockAsync(string walletAddress, BlockSource source, string reason, string actor, CancellationToken ct = default)
        => _blocklist.RemoveBlockAsync(walletAddress, source, actor, reason, ct);

    public Task<int> BulkImportBlocksAsync(IEnumerable<string> wallets, BlockSource source, string reason, string actor, CancellationToken ct = default)
        => _blocklist.BulkImportAsync(wallets, source, reason, actor, ct);

    public Task<List<BlockedWallet>> ListBlocksAsync(string? walletAddress = null, CancellationToken ct = default)
        => _blocklist.ListBlocksAsync(walletAddress, ct);

    public Task<List<EnforcementAction>> GetActionsAsync(string? walletAddress = null, CancellationToken ct = default)
        => _blocklist.GetActionsAsync(walletAddress, ct: ct);
}
