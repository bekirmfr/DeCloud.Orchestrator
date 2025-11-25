using System.Collections.Concurrent;
using Orchestrator.Models;

namespace Orchestrator.Data;

/// <summary>
/// In-memory data store for the orchestrator.
/// Replace with a proper database (PostgreSQL, etc.) for production.
/// </summary>
public class OrchestratorDataStore
{
    // Thread-safe collections for concurrent access
    public ConcurrentDictionary<string, Node> Nodes { get; } = new();
    public ConcurrentDictionary<string, VirtualMachine> VirtualMachines { get; } = new();
    public ConcurrentDictionary<string, User> Users { get; } = new();
    public ConcurrentDictionary<string, VmImage> Images { get; } = new();
    public ConcurrentDictionary<string, VmPricingTier> PricingTiers { get; } = new();
    
    // Node auth tokens (nodeId -> token hash)
    public ConcurrentDictionary<string, string> NodeAuthTokens { get; } = new();
    
    // User sessions (refresh token hash -> user id)
    public ConcurrentDictionary<string, string> UserSessions { get; } = new();
    
    // Pending commands for nodes (nodeId -> commands)
    public ConcurrentDictionary<string, ConcurrentQueue<NodeCommand>> PendingNodeCommands { get; } = new();
    
    // Event history (for debugging/auditing)
    public ConcurrentQueue<OrchestratorEvent> EventHistory { get; } = new();
    private const int MaxEventHistory = 10000;

    public OrchestratorDataStore()
    {
        SeedDefaultData();
    }

    private void SeedDefaultData()
    {
        // Default VM images
        var images = new[]
        {
            new VmImage
            {
                Id = "ubuntu-24.04",
                Name = "Ubuntu 24.04 LTS",
                Description = "Ubuntu Noble Numbat - Long Term Support",
                OsFamily = "linux",
                OsName = "ubuntu",
                Version = "24.04",
                SizeGb = 4,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "ubuntu-22.04",
                Name = "Ubuntu 22.04 LTS",
                Description = "Ubuntu Jammy Jellyfish - Long Term Support",
                OsFamily = "linux",
                OsName = "ubuntu",
                Version = "22.04",
                SizeGb = 4,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "debian-12",
                Name = "Debian 12",
                Description = "Debian Bookworm - Stable",
                OsFamily = "linux",
                OsName = "debian",
                Version = "12",
                SizeGb = 3,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "fedora-40",
                Name = "Fedora 40",
                Description = "Fedora Workstation",
                OsFamily = "linux",
                OsName = "fedora",
                Version = "40",
                SizeGb = 5,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            },
            new VmImage
            {
                Id = "alpine-3.19",
                Name = "Alpine Linux 3.19",
                Description = "Lightweight Linux distribution",
                OsFamily = "linux",
                OsName = "alpine",
                Version = "3.19",
                SizeGb = 1,
                IsPublic = true,
                CreatedAt = DateTime.UtcNow
            }
        };

        foreach (var image in images)
        {
            Images.TryAdd(image.Id, image);
        }

        // Default pricing tiers
        var tiers = new[]
        {
            new VmPricingTier
            {
                Id = "xs",
                Name = "Extra Small",
                CpuCores = 1,
                MemoryMb = 512,
                StorageGb = 10,
                HourlyPriceUsd = 0.005m,
                HourlyPriceCrypto = 0.005m
            },
            new VmPricingTier
            {
                Id = "small",
                Name = "Small",
                CpuCores = 1,
                MemoryMb = 1024,
                StorageGb = 20,
                HourlyPriceUsd = 0.01m,
                HourlyPriceCrypto = 0.01m
            },
            new VmPricingTier
            {
                Id = "medium",
                Name = "Medium",
                CpuCores = 2,
                MemoryMb = 4096,
                StorageGb = 40,
                HourlyPriceUsd = 0.04m,
                HourlyPriceCrypto = 0.04m
            },
            new VmPricingTier
            {
                Id = "large",
                Name = "Large",
                CpuCores = 4,
                MemoryMb = 8192,
                StorageGb = 80,
                HourlyPriceUsd = 0.08m,
                HourlyPriceCrypto = 0.08m
            },
            new VmPricingTier
            {
                Id = "xl",
                Name = "Extra Large",
                CpuCores = 8,
                MemoryMb = 16384,
                StorageGb = 160,
                HourlyPriceUsd = 0.16m,
                HourlyPriceCrypto = 0.16m
            },
            new VmPricingTier
            {
                Id = "2xl",
                Name = "2X Large",
                CpuCores = 16,
                MemoryMb = 32768,
                StorageGb = 320,
                HourlyPriceUsd = 0.32m,
                HourlyPriceCrypto = 0.32m
            }
        };

        foreach (var tier in tiers)
        {
            PricingTiers.TryAdd(tier.Id, tier);
        }
    }

    public void AddEvent(OrchestratorEvent evt)
    {
        EventHistory.Enqueue(evt);
        
        // Keep history bounded
        while (EventHistory.Count > MaxEventHistory && EventHistory.TryDequeue(out _))
        {
            // Dequeue oldest
        }
    }

    public void AddPendingCommand(string nodeId, NodeCommand command)
    {
        var queue = PendingNodeCommands.GetOrAdd(nodeId, _ => new ConcurrentQueue<NodeCommand>());
        queue.Enqueue(command);
    }

    public List<NodeCommand> GetAndClearPendingCommands(string nodeId)
    {
        var commands = new List<NodeCommand>();
        
        if (PendingNodeCommands.TryGetValue(nodeId, out var queue))
        {
            while (queue.TryDequeue(out var cmd))
            {
                commands.Add(cmd);
            }
        }
        
        return commands;
    }

    public SystemStats GetSystemStats()
    {
        var nodes = Nodes.Values.ToList();
        var vms = VirtualMachines.Values.ToList();
        var users = Users.Values.ToList();

        return new SystemStats(
            TotalNodes: nodes.Count,
            OnlineNodes: nodes.Count(n => n.Status == NodeStatus.Online),
            TotalVms: vms.Count,
            RunningVms: vms.Count(v => v.Status == VmStatus.Running),
            TotalCpuCores: nodes.Sum(n => n.TotalResources.CpuCores),
            AvailableCpuCores: nodes.Where(n => n.Status == NodeStatus.Online).Sum(n => n.AvailableResources.CpuCores),
            TotalMemoryMb: nodes.Sum(n => n.TotalResources.MemoryMb),
            AvailableMemoryMb: nodes.Where(n => n.Status == NodeStatus.Online).Sum(n => n.AvailableResources.MemoryMb),
            TotalStorageGb: nodes.Sum(n => n.TotalResources.StorageGb),
            AvailableStorageGb: nodes.Where(n => n.Status == NodeStatus.Online).Sum(n => n.AvailableResources.StorageGb),
            TotalUsers: users.Count,
            TotalRevenue: vms.Sum(v => v.BillingInfo.TotalBilled)
        );
    }
}
