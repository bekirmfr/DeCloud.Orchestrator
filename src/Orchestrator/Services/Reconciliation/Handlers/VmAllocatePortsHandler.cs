using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles vm.allocate-ports obligations: auto-allocate template-defined ports
/// for a running VM using the DirectAccessService.
///
/// Preconditions: VM must be Running, have a PrivateIp, and have a template with ExposedPorts.
/// Idempotency: Skips ports that are already allocated.
///
/// This is typically spawned as a child of vm.provision after the VM becomes Running,
/// or created directly for recovery when port allocation failed.
/// </summary>
public class VmAllocatePortsHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly ITemplateService _templateService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<VmAllocatePortsHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.VmAllocatePorts];

    // Protocols handled by CentralIngress (subdomain routing), not DirectAccess
    private static readonly HashSet<string> IngressProtocols =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "ws", "wss" };

    public VmAllocatePortsHandler(
        DataStore dataStore,
        ITemplateService templateService,
        IServiceProvider serviceProvider,
        ILogger<VmAllocatePortsHandler> logger)
    {
        _dataStore = dataStore;
        _templateService = templateService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        var vmId = obligation.ResourceId;
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
            return ObligationResult.Fail($"VM {vmId} not found");

        // VM must be running
        if (vm.Status != VmStatus.Running)
        {
            if (vm.Status is VmStatus.Deleted or VmStatus.Deleting or VmStatus.Error)
                return ObligationResult.Fail($"VM {vmId} is {vm.Status}");

            return ObligationResult.Retry($"VM is {vm.Status} — waiting for Running state");
        }

        // Must have a template
        if (string.IsNullOrEmpty(vm.TemplateId))
            return ObligationResult.Completed("No template — nothing to allocate");

        var template = await _templateService.GetTemplateByIdAsync(vm.TemplateId);
        if (template?.ExposedPorts == null || !template.ExposedPorts.Any())
            return ObligationResult.Completed("Template has no exposed ports");

        // Filter to ports that need allocation:
        //   - Must be public
        //   - Must NOT be ingress protocols (handled by Caddy subdomain)
        //   - Must NOT already be allocated
        var portsToAllocate = template.ExposedPorts
            .Where(p => p.IsPublic)
            .Where(p => !IngressProtocols.Contains(p.Protocol ?? ""))
            .Where(p => vm.DirectAccess?.PortMappings?.Any(m => m.VmPort == p.Port) != true)
            .ToList();

        if (portsToAllocate.Count == 0)
            return ObligationResult.Completed("All template ports already allocated");

        // Allocate ports via DirectAccessService
        var directAccessService = _serviceProvider.GetRequiredService<DirectAccessService>();
        var allocated = 0;
        var failed = 0;

        foreach (var exposedPort in portsToAllocate)
        {
            var protocol = exposedPort.Protocol?.ToLower() switch
            {
                "tcp" => PortProtocol.TCP,
                "udp" => PortProtocol.UDP,
                "both" or "tcp_and_udp" => PortProtocol.Both,
                _ => PortProtocol.TCP
            };

            try
            {
                var result = await directAccessService.AllocatePortAsync(
                    vm.Id,
                    exposedPort.Port,
                    protocol,
                    exposedPort.Description ?? exposedPort.Port.ToString(),
                    ct);

                if (result.Success)
                {
                    allocated++;
                    _logger.LogDebug(
                        "Allocated port {VmPort} → {PublicPort} ({Protocol}) for VM {VmId}",
                        exposedPort.Port, result.PublicPort, protocol, vmId);
                }
                else
                {
                    failed++;
                    _logger.LogWarning(
                        "Failed to allocate port {Port} for VM {VmId}: {Error}",
                        exposedPort.Port, vmId, result.Error);
                }
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogWarning(ex,
                    "Exception allocating port {Port} for VM {VmId}",
                    exposedPort.Port, vmId);
            }
        }

        if (failed > 0 && allocated == 0)
            return ObligationResult.Retry($"All {failed} port allocations failed");

        if (failed > 0)
        {
            _logger.LogWarning(
                "Partial port allocation for VM {VmId}: {Allocated} succeeded, {Failed} failed",
                vmId, allocated, failed);
            return ObligationResult.Retry(
                $"Partial: {allocated}/{portsToAllocate.Count} ports allocated, {failed} failed");
        }

        return ObligationResult.Completed(
            $"Allocated {allocated} template port(s) for VM {vmId}");
    }
}
