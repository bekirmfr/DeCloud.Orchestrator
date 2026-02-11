using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services.Reconciliation.Handlers;

/// <summary>
/// Handles vm.register-ingress obligations: register a running VM with the
/// central ingress gateway (Caddy reverse proxy) so it's accessible via subdomain.
///
/// Preconditions: VM must be Running and have a PrivateIp assigned.
/// Idempotency: CentralIngressService.OnVmStartedAsync checks if already registered.
///
/// This is typically spawned as a child of vm.provision after the VM becomes Running,
/// or created directly for recovery when ingress registration failed.
/// </summary>
public class VmRegisterIngressHandler : IObligationHandler
{
    private readonly DataStore _dataStore;
    private readonly ICentralIngressService _ingressService;
    private readonly ILogger<VmRegisterIngressHandler> _logger;

    public IReadOnlyList<string> SupportedTypes => [ObligationTypes.VmRegisterIngress];

    public VmRegisterIngressHandler(
        DataStore dataStore,
        ICentralIngressService ingressService,
        ILogger<VmRegisterIngressHandler> logger)
    {
        _dataStore = dataStore;
        _ingressService = ingressService;
        _logger = logger;
    }

    public async Task<ObligationResult> ExecuteAsync(Obligation obligation, CancellationToken ct)
    {
        if (!_ingressService.IsEnabled)
            return ObligationResult.Completed("Central ingress not enabled — skipping");

        var vmId = obligation.ResourceId;
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
            return ObligationResult.Fail($"VM {vmId} not found");

        // VM must be running
        if (vm.Status != VmStatus.Running)
        {
            if (vm.Status is VmStatus.Deleted or VmStatus.Deleting or VmStatus.Error)
                return ObligationResult.Fail($"VM {vmId} is {vm.Status} — cannot register ingress");

            // Still provisioning — retry later
            return ObligationResult.Retry($"VM is {vm.Status} — waiting for Running state");
        }

        // VM must have a private IP
        if (string.IsNullOrEmpty(vm.NetworkConfig?.PrivateIp))
        {
            return ObligationResult.Retry("VM has no private IP assigned yet");
        }

        // Register with ingress
        try
        {
            await _ingressService.OnVmStartedAsync(vmId, ct);

            _logger.LogInformation(
                "Ingress registered for VM {VmId} ({Name})",
                vmId, vm.Name);

            return ObligationResult.Completed($"Ingress registered: {vm.IngressConfig?.DefaultSubdomain}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to register ingress for VM {VmId}", vmId);
            return ObligationResult.Retry($"Ingress registration failed: {ex.Message}");
        }
    }
}
