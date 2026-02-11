using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// Background service that periodically scans for VMs in stuck states
/// and creates obligations to recover them.
///
/// This catches cases that fall through the cracks of the normal flow:
///   - VM stuck in Pending (scheduler failed, no node available at the time)
///   - VM Running but missing ingress (side effect failed during transition)
///   - VM Running from template but ports not allocated
///   - VM stuck in Provisioning (command lost, ack never arrived)
///
/// Runs every 60 seconds. Creates obligations with deduplication
/// (ObligationService.Create skips if an active obligation already exists).
/// </summary>
public class VmRecoveryScanner : BackgroundService
{
    private readonly DataStore _dataStore;
    private readonly IObligationService _obligationService;
    private readonly ICentralIngressService _ingressService;
    private readonly ITemplateService _templateService;
    private readonly ILogger<VmRecoveryScanner> _logger;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan PendingThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProvisioningThreshold = TimeSpan.FromMinutes(7);

    public VmRecoveryScanner(
        DataStore dataStore,
        IObligationService obligationService,
        ICentralIngressService ingressService,
        ITemplateService templateService,
        ILogger<VmRecoveryScanner> logger)
    {
        _dataStore = dataStore;
        _obligationService = obligationService;
        _ingressService = ingressService;
        _templateService = templateService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VmRecoveryScanner started (interval: {Interval}s)", ScanInterval.TotalSeconds);

        // Wait for initial startup to complete
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in VmRecoveryScanner");
            }

            await Task.Delay(ScanInterval, stoppingToken);
        }
    }

    private async Task ScanAsync(CancellationToken ct)
    {
        var created = 0;

        created += ScanPendingVms();
        created += ScanProvisioningVms();
        created += await ScanRunningVmsMissingIngressAsync(ct);
        created += await ScanRunningVmsMissingPortsAsync(ct);

        if (created > 0)
        {
            _logger.LogInformation("VmRecoveryScanner created {Count} obligation(s)", created);
        }
    }

    /// <summary>
    /// VMs stuck in Pending — need scheduling.
    /// </summary>
    private int ScanPendingVms()
    {
        var created = 0;
        var now = DateTime.UtcNow;

        var pendingVms = _dataStore.ActiveVMs.Values
            .Where(vm => vm.Status == VmStatus.Pending)
            .Where(vm => (now - vm.CreatedAt) > PendingThreshold)
            .ToList();

        foreach (var vm in pendingVms)
        {
            var obligation = _obligationService.Create(new Obligation
            {
                Id = $"{ObligationTypes.VmSchedule}:{vm.Id}:{Guid.NewGuid().ToString()[..8]}",
                Type = ObligationTypes.VmSchedule,
                ResourceType = "vm",
                ResourceId = vm.Id,
                Priority = 1,
                Deadline = DateTime.UtcNow.AddMinutes(30),
                Data = !string.IsNullOrEmpty(vm.TargetNodeId)
                    ? new Dictionary<string, string> { ["targetNodeId"] = vm.TargetNodeId }
                    : new Dictionary<string, string>()
            });

            if (obligation != null)
            {
                created++;
                _logger.LogInformation(
                    "Recovery: created vm.schedule for stuck Pending VM {VmId} (age: {Age}s)",
                    vm.Id, (now - vm.CreatedAt).TotalSeconds);
            }
        }

        return created;
    }

    /// <summary>
    /// VMs stuck in Provisioning — command might be lost.
    /// Only recover if command tracking indicates the command is stale.
    /// </summary>
    private int ScanProvisioningVms()
    {
        var created = 0;
        var now = DateTime.UtcNow;

        var stuckVms = _dataStore.ActiveVMs.Values
            .Where(vm => vm.Status == VmStatus.Provisioning)
            .Where(vm => string.IsNullOrEmpty(vm.ActiveCommandId) ||
                         (vm.ActiveCommandIssuedAt.HasValue &&
                          (now - vm.ActiveCommandIssuedAt.Value) > ProvisioningThreshold))
            .ToList();

        foreach (var vm in stuckVms)
        {
            var obligation = _obligationService.Create(new Obligation
            {
                Id = $"{ObligationTypes.VmProvision}:{vm.Id}:{Guid.NewGuid().ToString()[..8]}",
                Type = ObligationTypes.VmProvision,
                ResourceType = "vm",
                ResourceId = vm.Id,
                Priority = 1,
                Deadline = DateTime.UtcNow.AddMinutes(15),
                Data = new Dictionary<string, string>
                {
                    ["nodeId"] = vm.NodeId ?? "",
                    ["recovery"] = "true"
                }
            });

            if (obligation != null)
            {
                created++;
                _logger.LogInformation(
                    "Recovery: created vm.provision for stuck Provisioning VM {VmId} " +
                    "(command: {CommandId}, age: {Age}s)",
                    vm.Id,
                    vm.ActiveCommandId ?? "none",
                    vm.ActiveCommandIssuedAt.HasValue
                        ? (now - vm.ActiveCommandIssuedAt.Value).TotalSeconds
                        : -1);
            }
        }

        return created;
    }

    /// <summary>
    /// Running VMs that don't have ingress registered (side effect failed).
    /// </summary>
    private async Task<int> ScanRunningVmsMissingIngressAsync(CancellationToken ct)
    {
        if (!_ingressService.IsEnabled)
            return 0;

        var created = 0;

        var runningVms = _dataStore.ActiveVMs.Values
            .Where(vm => vm.Status == VmStatus.Running)
            .Where(vm => !string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
            .Where(vm => vm.IngressConfig == null ||
                         !vm.IngressConfig.DefaultSubdomainEnabled)
            .ToList();

        foreach (var vm in runningVms)
        {
            var obligation = _obligationService.Create(new Obligation
            {
                Id = $"{ObligationTypes.VmRegisterIngress}:{vm.Id}:{Guid.NewGuid().ToString()[..8]}",
                Type = ObligationTypes.VmRegisterIngress,
                ResourceType = "vm",
                ResourceId = vm.Id,
                MaxAttempts = 5,
                BackoffBaseSeconds = 10
            });

            if (obligation != null)
            {
                created++;
                _logger.LogInformation(
                    "Recovery: created vm.register-ingress for Running VM {VmId} missing ingress",
                    vm.Id);
            }
        }

        return created;
    }

    /// <summary>
    /// Running template VMs that have unallocated ports (side effect failed).
    /// </summary>
    private async Task<int> ScanRunningVmsMissingPortsAsync(CancellationToken ct)
    {
        var created = 0;

        // Protocols handled by ingress, not direct access
        var ingressProtocols = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "http", "https", "ws", "wss" };

        var templateVms = _dataStore.ActiveVMs.Values
            .Where(vm => vm.Status == VmStatus.Running)
            .Where(vm => !string.IsNullOrEmpty(vm.TemplateId))
            .Where(vm => !string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
            .ToList();

        foreach (var vm in templateVms)
        {
            var template = await _templateService.GetTemplateByIdAsync(vm.TemplateId!);
            if (template?.ExposedPorts == null || !template.ExposedPorts.Any())
                continue;

            var portsNeeded = template.ExposedPorts
                .Where(p => p.IsPublic)
                .Where(p => !ingressProtocols.Contains(p.Protocol ?? ""))
                .Where(p => vm.DirectAccess?.PortMappings?.Any(m => m.VmPort == p.Port) != true)
                .ToList();

            if (portsNeeded.Count == 0)
                continue;

            var obligation = _obligationService.Create(new Obligation
            {
                Id = $"{ObligationTypes.VmAllocatePorts}:{vm.Id}:{Guid.NewGuid().ToString()[..8]}",
                Type = ObligationTypes.VmAllocatePorts,
                ResourceType = "vm",
                ResourceId = vm.Id,
                MaxAttempts = 5,
                BackoffBaseSeconds = 10
            });

            if (obligation != null)
            {
                created++;
                _logger.LogInformation(
                    "Recovery: created vm.allocate-ports for Running VM {VmId} " +
                    "missing {Count} template port(s)",
                    vm.Id, portsNeeded.Count);
            }
        }

        return created;
    }
}
