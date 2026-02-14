using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Persistence;

namespace Orchestrator.Controllers;

/// <summary>
/// Node Operator Earnings Dashboard API.
/// Provides node operators with visibility into their earnings,
/// payouts, and performance — critical for supply-side growth.
/// </summary>
[ApiController]
[Route("api/node-earnings")]
[Authorize]
public class NodeEarningsController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NodeEarningsController> _logger;

    public NodeEarningsController(DataStore dataStore, ILogger<NodeEarningsController> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    /// <summary>
    /// Get earnings summary for all nodes owned by the current user's wallet
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult> GetEarningsSummary()
    {
        var walletAddress = GetUserId();
        if (walletAddress == null) return Unauthorized();

        var allNodes = await _dataStore.GetAllNodesAsync();
        var myNodes = allNodes.Where(n =>
            n.WalletAddress.Equals(walletAddress, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!myNodes.Any())
            return Ok(new
            {
                totalNodes = 0,
                totalEarned = 0m,
                pendingPayout = 0m,
                activeVms = 0,
                message = "No nodes registered with this wallet. Register a node to start earning!"
            });

        var allVms = _dataStore.ActiveVMs.Values.ToList();

        var totalEarned = myNodes.Sum(n => n.TotalEarned);
        var pendingPayout = myNodes.Sum(n => n.PendingPayout);
        var activeVms = allVms.Count(vm =>
            myNodes.Any(n => n.Id == vm.NodeId) &&
            vm.Status == Models.VmStatus.Running);

        // Calculate daily/weekly/monthly earnings from usage records
        var now = DateTime.UtcNow;
        var usageRecords = _dataStore.UnsettledUsage.Values
            .Where(u => myNodes.Any(n => n.Id == u.NodeId))
            .ToList();

        var earningsToday = usageRecords
            .Where(u => u.CreatedAt >= now.Date)
            .Sum(u => u.NodeShare);

        var earningsThisWeek = usageRecords
            .Where(u => u.CreatedAt >= now.AddDays(-7))
            .Sum(u => u.NodeShare);

        var earningsThisMonth = usageRecords
            .Where(u => u.CreatedAt >= now.AddDays(-30))
            .Sum(u => u.NodeShare);

        return Ok(new
        {
            totalNodes = myNodes.Count,
            onlineNodes = myNodes.Count(n => n.IsOnline),
            totalEarned,
            pendingPayout,
            activeVms,
            earnings = new
            {
                today = earningsToday,
                thisWeek = earningsThisWeek,
                thisMonth = earningsThisMonth
            },
            nodes = myNodes.Select(n => new
            {
                n.Id,
                n.Name,
                n.IsOnline,
                n.PublicIp,
                n.Architecture,
                totalEarned = n.TotalEarned,
                pendingPayout = n.PendingPayout,
                vms = allVms.Count(vm => vm.NodeId == n.Id && vm.Status == Models.VmStatus.Running),
                cpu = new
                {
                    total = n.HardwareInventory?.CpuCores ?? 0,
                    used = allVms.Where(vm => vm.NodeId == n.Id && vm.Status == Models.VmStatus.Running)
                        .Sum(vm => vm.Spec?.VirtualCpuCores ?? 0)
                },
                memory = new
                {
                    totalGb = (n.HardwareInventory?.TotalMemoryBytes ?? 0) / (1024.0 * 1024 * 1024),
                    usedGb = allVms.Where(vm => vm.NodeId == n.Id && vm.Status == Models.VmStatus.Running)
                        .Sum(vm => (vm.Spec?.MemoryBytes ?? 0) / (1024.0 * 1024 * 1024))
                },
                reputation = n.Reputation != null ? new
                {
                    n.Reputation.UptimePercent,
                    n.Reputation.ReputationScore,
                    n.Reputation.TotalVmsHosted,
                    n.Reputation.SuccessfulVms
                } : null,
                lastHeartbeat = n.LastHeartbeat
            })
        });
    }

    /// <summary>
    /// Get detailed earning history for a specific node
    /// </summary>
    [HttpGet("{nodeId}/history")]
    public async Task<ActionResult> GetNodeEarningHistory(string nodeId)
    {
        var walletAddress = GetUserId();
        if (walletAddress == null) return Unauthorized();

        var allNodes = await _dataStore.GetAllNodesAsync();
        var node = allNodes.FirstOrDefault(n =>
            n.Id == nodeId &&
            n.WalletAddress.Equals(walletAddress, StringComparison.OrdinalIgnoreCase));

        if (node == null)
            return NotFound(new { message = "Node not found or you don't own this node" });

        // Get usage records for this node
        var usageRecords = _dataStore.UnsettledUsage.Values
            .Where(u => u.NodeId == nodeId)
            .OrderByDescending(u => u.CreatedAt)
            .Take(100)
            .Select(u => new
            {
                u.Id,
                u.VmId,
                u.UserId,
                u.PeriodStart,
                u.PeriodEnd,
                durationMinutes = u.Duration.TotalMinutes,
                u.TotalCost,
                u.NodeShare,
                u.PlatformFee,
                u.SettledOnChain,
                u.CreatedAt
            })
            .ToList();

        // Calculate daily earnings for the past 30 days
        var now = DateTime.UtcNow;
        var allUsage = _dataStore.UnsettledUsage.Values
            .Where(u => u.NodeId == nodeId && u.CreatedAt >= now.AddDays(-30))
            .ToList();

        var dailyEarnings = Enumerable.Range(0, 30)
            .Select(daysAgo =>
            {
                var date = now.AddDays(-daysAgo).Date;
                var dayUsage = allUsage.Where(u => u.CreatedAt.Date == date);
                return new
                {
                    date = date.ToString("yyyy-MM-dd"),
                    earned = dayUsage.Sum(u => u.NodeShare),
                    vms = dayUsage.Select(u => u.VmId).Distinct().Count()
                };
            })
            .Reverse()
            .ToList();

        return Ok(new
        {
            node = new
            {
                node.Id,
                node.Name,
                node.TotalEarned,
                node.PendingPayout
            },
            dailyEarnings,
            recentUsage = usageRecords
        });
    }

    private string? GetUserId()
    {
        return User.FindFirst("sub")?.Value
            ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}
