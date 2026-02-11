using Orchestrator.Models;

namespace Orchestrator.Services.Reconciliation;

/// <summary>
/// High-level API for creating and managing obligations.
/// Provides convenience methods for common obligation patterns,
/// deduplication, and signal delivery.
///
/// This is the primary interface that other services use to interact
/// with the reconciliation system.
/// </summary>
public interface IObligationService
{
    /// <summary>
    /// Create a single obligation (with deduplication)
    /// </summary>
    Obligation? Create(Obligation obligation);

    /// <summary>
    /// Create a chain of obligations with sequential dependencies.
    /// Returns the final obligation in the chain.
    /// Example: Create(A) → Create(B, dependsOn: A) → Create(C, dependsOn: B)
    /// </summary>
    Obligation? CreateChain(params Obligation[] obligations);

    /// <summary>
    /// Deliver a signal to a waiting obligation.
    /// Returns the obligation that was woken up, or null if no one was waiting.
    /// </summary>
    Obligation? Signal(string signalKey, Dictionary<string, string>? data = null);

    /// <summary>
    /// Cancel all obligations for a resource
    /// </summary>
    void CancelForResource(string resourceType, string resourceId, string reason);

    /// <summary>
    /// Get all active obligations for a resource
    /// </summary>
    IReadOnlyList<Obligation> GetForResource(string resourceType, string resourceId);

    /// <summary>
    /// Get reconciliation loop metrics
    /// </summary>
    ReconciliationMetrics GetMetrics();
}

public class ObligationService : IObligationService
{
    private readonly ObligationStore _store;
    private readonly ReconciliationLoop _loop;
    private readonly ILogger<ObligationService> _logger;

    public ObligationService(
        ObligationStore store,
        ReconciliationLoop loop,
        ILogger<ObligationService> logger)
    {
        _store = store;
        _loop = loop;
        _logger = logger;
    }

    public Obligation? Create(Obligation obligation)
    {
        // Deduplication: skip if an active obligation of the same type
        // already exists for this resource
        if (_store.HasActiveObligation(obligation.Type, obligation.ResourceType, obligation.ResourceId))
        {
            _logger.LogDebug(
                "Skipping duplicate obligation {Type} for {ResourceType}/{ResourceId}",
                obligation.Type, obligation.ResourceType, obligation.ResourceId);
            return null;
        }

        // Generate ID if not provided
        if (string.IsNullOrEmpty(obligation.Id))
        {
            obligation.Id = $"{obligation.Type}:{obligation.ResourceId}:{Guid.NewGuid().ToString()[..8]}";
        }

        if (_store.TryAdd(obligation))
        {
            _logger.LogInformation("Created obligation: {Obligation}", obligation);
            return obligation;
        }

        return null;
    }

    public Obligation? CreateChain(params Obligation[] obligations)
    {
        if (obligations.Length == 0)
            return null;

        Obligation? previous = null;

        foreach (var ob in obligations)
        {
            // Chain dependency
            if (previous != null && !ob.DependsOn.Contains(previous.Id))
            {
                ob.DependsOn.Add(previous.Id);
            }

            var created = Create(ob);
            if (created == null && previous != null)
            {
                // Deduplication hit — find the existing obligation to chain from
                var existing = _store.GetByResource(ob.ResourceType, ob.ResourceId)
                    .FirstOrDefault(e => e.Type == ob.Type && !e.IsTerminal);

                if (existing != null)
                {
                    previous = existing;
                    continue;
                }

                // Can't create and can't find existing — chain is broken
                _logger.LogWarning(
                    "Chain broken at {Type} for {ResourceType}/{ResourceId}",
                    ob.Type, ob.ResourceType, ob.ResourceId);
                return previous;
            }

            previous = created ?? previous;
        }

        return previous;
    }

    public Obligation? Signal(string signalKey, Dictionary<string, string>? data = null)
    {
        var woken = _store.DeliverSignal(signalKey, data);

        if (woken != null)
        {
            _logger.LogInformation(
                "Signal {Key} delivered to obligation {Id} ({Type})",
                signalKey, woken.Id, woken.Type);
        }
        else
        {
            _logger.LogDebug("Signal {Key} delivered but no obligation was waiting", signalKey);
        }

        return woken;
    }

    public void CancelForResource(string resourceType, string resourceId, string reason)
    {
        var obligations = _store.GetByResource(resourceType, resourceId);
        var cancelled = 0;

        foreach (var ob in obligations)
        {
            if (!ob.IsTerminal)
            {
                _store.Cancel(ob.Id, reason);
                cancelled++;
            }
        }

        if (cancelled > 0)
        {
            _logger.LogInformation(
                "Cancelled {Count} obligations for {ResourceType}/{ResourceId}: {Reason}",
                cancelled, resourceType, resourceId, reason);
        }
    }

    public IReadOnlyList<Obligation> GetForResource(string resourceType, string resourceId)
    {
        return _store.GetByResource(resourceType, resourceId);
    }

    public ReconciliationMetrics GetMetrics() => _loop.GetMetrics();
}

/// <summary>
/// Well-known obligation types for the orchestrator.
/// Using constants avoids typos and enables discoverability.
/// </summary>
public static class ObligationTypes
{
    // VM lifecycle
    public const string VmSchedule = "vm.schedule";
    public const string VmProvision = "vm.provision";
    public const string VmStart = "vm.start";
    public const string VmStop = "vm.stop";
    public const string VmDelete = "vm.delete";
    public const string VmMigrate = "vm.migrate";

    // VM side effects
    public const string VmRegisterIngress = "vm.register-ingress";
    public const string VmAllocatePorts = "vm.allocate-ports";
    public const string VmSettleTemplateFee = "vm.settle-template-fee";

    // Node management
    public const string NodeAssignRelay = "node.assign-relay";
    public const string NodeDeployRelayVm = "node.deploy-relay-vm";
    public const string NodeEvaluatePerformance = "node.evaluate-performance";

    // Infrastructure (future)
    public const string DhtJoin = "dht.join";
    public const string DhtReplicate = "dht.replicate";
    public const string BlockStoreSync = "blockstore.sync";

    // Billing
    public const string BillingRecordUsage = "billing.record-usage";
    public const string BillingSettle = "billing.settle";
}

/// <summary>
/// Well-known signal key patterns.
/// Format: "{domain}:{identifier}"
/// </summary>
public static class SignalKeys
{
    /// <summary>
    /// Signal sent when a command is acknowledged by a node.
    /// Format: "command-ack:{commandId}"
    /// </summary>
    public static string CommandAck(string commandId) => $"command-ack:{commandId}";

    /// <summary>
    /// Signal sent when a VM's private IP becomes available.
    /// Format: "vm-ip-assigned:{vmId}"
    /// </summary>
    public static string VmIpAssigned(string vmId) => $"vm-ip-assigned:{vmId}";

    /// <summary>
    /// Signal sent when a node comes online.
    /// Format: "node-online:{nodeId}"
    /// </summary>
    public static string NodeOnline(string nodeId) => $"node-online:{nodeId}";
}
