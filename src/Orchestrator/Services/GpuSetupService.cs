using System.Text.Json;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Manages automated GPU setup on nodes.
///
/// Implements the hybrid approach:
///   1. GPU detected + IOMMU already enabled → configure VFIO → passthrough ready
///   2. GPU detected + no IOMMU → install NVIDIA drivers + Container Toolkit → container sharing ready immediately
///   3. No GPU → no-op
///
/// The orchestrator evaluates GPU readiness during node registration and sends
/// a ConfigureGpu command to the node agent when setup is needed. The node agent
/// performs the actual driver installation and kernel configuration.
/// </summary>
public interface IGpuSetupService
{
    /// <summary>
    /// Evaluate a node's GPU hardware and queue a ConfigureGpu command if setup is needed.
    /// Called during node registration after hardware inventory is reported.
    /// </summary>
    Task EvaluateAndQueueSetupAsync(Node node, CancellationToken ct = default);

    /// <summary>
    /// Manually trigger GPU setup on a node (e.g., after a reboot or retry after failure).
    /// </summary>
    Task<GpuSetupResult> TriggerSetupAsync(string nodeId, GpuSetupMode mode = GpuSetupMode.Auto, CancellationToken ct = default);

    /// <summary>
    /// Process the acknowledgment of a ConfigureGpu command from a node agent.
    /// Updates GPU readiness flags on the node based on the agent's report.
    /// </summary>
    Task ProcessSetupAcknowledgmentAsync(Node node, CommandAcknowledgment ack, CancellationToken ct = default);
}

public class GpuSetupService : IGpuSetupService
{
    private readonly DataStore _dataStore;
    private readonly INodeCommandService _commandService;
    private readonly IEventService _eventService;
    private readonly ILogger<GpuSetupService> _logger;

    public GpuSetupService(
        DataStore dataStore,
        INodeCommandService commandService,
        IEventService eventService,
        ILogger<GpuSetupService> logger)
    {
        _dataStore = dataStore;
        _commandService = commandService;
        _eventService = eventService;
        _logger = logger;
    }

    public async Task EvaluateAndQueueSetupAsync(Node node, CancellationToken ct = default)
    {
        var inventory = node.HardwareInventory;

        // No GPUs detected at PCI level — nothing to do
        if (!inventory.SupportsGpu || inventory.Gpus.Count == 0)
        {
            node.GpuSetupStatus = GpuSetupStatus.NotNeeded;
            return;
        }

        // Check if any GPU already has a working mode
        var hasPassthrough = inventory.Gpus.Any(g => g.IsAvailableForPassthrough);
        var hasContainerSharing = inventory.SupportsGpuContainers &&
            inventory.Gpus.Any(g => g.IsAvailableForContainerSharing);

        if (hasPassthrough || hasContainerSharing)
        {
            // GPU is already usable — mark as completed
            node.GpuSetupStatus = GpuSetupStatus.Completed;
            foreach (var gpu in inventory.Gpus)
            {
                gpu.SetupStatus = GpuSetupStatus.Completed;
            }

            _logger.LogInformation(
                "Node {NodeId} GPU already configured: passthrough={Passthrough}, container={Container}",
                node.Id, hasPassthrough, hasContainerSharing);
            return;
        }

        // GPU detected but not ready — check if setup is already in progress
        if (node.GpuSetupStatus == GpuSetupStatus.InProgress)
        {
            _logger.LogDebug(
                "Node {NodeId} GPU setup already in progress, skipping duplicate command",
                node.Id);
            return;
        }

        // GPU detected but not configured — queue setup
        _logger.LogInformation(
            "Node {NodeId} has {GpuCount} GPU(s) but no working mode (passthrough or container). " +
            "Queuing automatic GPU setup.",
            node.Id, inventory.Gpus.Count);

        // Determine best mode based on what the node reports
        var mode = DetermineSetupMode(node);

        await SendConfigureGpuCommandAsync(node, mode, ct);
    }

    public async Task<GpuSetupResult> TriggerSetupAsync(
        string nodeId,
        GpuSetupMode mode = GpuSetupMode.Auto,
        CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return new GpuSetupResult(false, "Node not found");

        if (!node.HardwareInventory.SupportsGpu || node.HardwareInventory.Gpus.Count == 0)
            return new GpuSetupResult(false, "No GPU detected on this node");

        if (node.GpuSetupStatus == GpuSetupStatus.InProgress)
            return new GpuSetupResult(false, "GPU setup already in progress");

        await SendConfigureGpuCommandAsync(node, mode, ct);
        await _dataStore.SaveNodeAsync(node);

        return new GpuSetupResult(true, $"ConfigureGpu command sent (mode: {mode})");
    }

    public async Task ProcessSetupAcknowledgmentAsync(
        Node node,
        CommandAcknowledgment ack,
        CancellationToken ct = default)
    {
        if (!ack.Success)
        {
            node.GpuSetupStatus = GpuSetupStatus.Failed;
            foreach (var gpu in node.HardwareInventory.Gpus)
            {
                gpu.SetupStatus = GpuSetupStatus.Failed;
            }

            _logger.LogWarning(
                "GPU setup failed on node {NodeId}: {Error}",
                node.Id, ack.ErrorMessage ?? "Unknown error");

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError, // Node-level error — no dedicated NodeError type yet
                ResourceType = "node",
                ResourceId = node.Id,
                NodeId = node.Id,
                Payload = new Dictionary<string, object>
                {
                    ["event"] = "gpu_setup_failed",
                    ["error"] = ack.ErrorMessage ?? "Unknown error"
                }
            });

            await _dataStore.SaveNodeAsync(node);
            return;
        }

        // Parse the acknowledgment data to update GPU capabilities
        GpuSetupAckData? setupData = null;
        if (!string.IsNullOrEmpty(ack.Data))
        {
            try
            {
                setupData = JsonSerializer.Deserialize<GpuSetupAckData>(ack.Data,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to parse GPU setup ack data from node {NodeId}, using defaults",
                    node.Id);
            }
        }

        if (setupData?.RebootRequired == true)
        {
            node.GpuSetupStatus = GpuSetupStatus.RebootRequired;
            foreach (var gpu in node.HardwareInventory.Gpus)
            {
                gpu.SetupStatus = GpuSetupStatus.RebootRequired;
            }

            _logger.LogInformation(
                "Node {NodeId} GPU setup requires reboot (IOMMU enabled in grub, pending activation)",
                node.Id);
        }
        else
        {
            node.GpuSetupStatus = GpuSetupStatus.Completed;

            // Update individual GPU capabilities based on what the agent configured
            foreach (var gpu in node.HardwareInventory.Gpus)
            {
                gpu.SetupStatus = GpuSetupStatus.Completed;

                if (setupData != null)
                {
                    gpu.IsAvailableForContainerSharing = setupData.ContainerSharingReady;
                    gpu.IsAvailableForPassthrough = setupData.VfioPassthroughReady;
                    gpu.IsIommuEnabled = setupData.IommuEnabled;

                    if (!string.IsNullOrEmpty(setupData.DriverVersion))
                        gpu.DriverVersion = setupData.DriverVersion;
                }
                else
                {
                    // No detailed data — assume container sharing is ready (most common path)
                    gpu.IsAvailableForContainerSharing = true;
                }
            }

            // Update inventory-level flags
            node.HardwareInventory.SupportsGpuContainers =
                node.HardwareInventory.Gpus.Any(g => g.IsAvailableForContainerSharing);

            _logger.LogInformation(
                "Node {NodeId} GPU setup completed: passthrough={Passthrough}, container={Container}, driver={Driver}",
                node.Id,
                setupData?.VfioPassthroughReady ?? false,
                setupData?.ContainerSharingReady ?? true,
                setupData?.DriverVersion ?? "unknown");
        }

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.NodeRegistered, // Re-use — signals node capability change
            ResourceType = "node",
            ResourceId = node.Id,
            NodeId = node.Id,
            Payload = new Dictionary<string, object>
            {
                ["event"] = "gpu_setup_completed",
                ["passthrough"] = setupData?.VfioPassthroughReady ?? false,
                ["containerSharing"] = setupData?.ContainerSharingReady ?? true,
                ["rebootRequired"] = setupData?.RebootRequired ?? false
            }
        });

        await _dataStore.SaveNodeAsync(node);
    }

    // ============================================================================
    // Private helpers
    // ============================================================================

    /// <summary>
    /// Determine the best GPU setup mode based on node capabilities.
    /// Prefers ContainerToolkit (no reboot needed) unless IOMMU is already active.
    /// </summary>
    private GpuSetupMode DetermineSetupMode(Node node)
    {
        // If any GPU reports IOMMU already enabled (e.g., host BIOS has it on),
        // we can set up VFIO without a reboot — prefer passthrough
        var anyIommuEnabled = node.HardwareInventory.Gpus.Any(g => g.IsIommuEnabled);

        if (anyIommuEnabled)
        {
            _logger.LogInformation(
                "Node {NodeId}: IOMMU already enabled, recommending VFIO passthrough setup",
                node.Id);
            return GpuSetupMode.VfioPassthrough;
        }

        // No IOMMU — container toolkit is the immediate path (no reboot)
        // The agent can optionally enable IOMMU in grub for future passthrough
        _logger.LogInformation(
            "Node {NodeId}: IOMMU not enabled, recommending Container Toolkit setup (immediate, no reboot)",
            node.Id);
        return GpuSetupMode.Auto;
    }

    private async Task SendConfigureGpuCommandAsync(Node node, GpuSetupMode mode, CancellationToken ct)
    {
        var gpuSummary = node.HardwareInventory.Gpus
            .Select(g => new { g.Vendor, g.Model, g.PciAddress, g.MemoryBytes, g.IsIommuEnabled })
            .ToList();

        var payload = JsonSerializer.Serialize(new
        {
            Mode = mode.ToString(),
            Gpus = gpuSummary,
            // Tell the agent which container runtimes are available
            ContainerRuntimes = node.HardwareInventory.ContainerRuntimes
        });

        var command = new NodeCommand(
            CommandId: Guid.NewGuid().ToString(),
            Type: NodeCommandType.ConfigureGpu,
            Payload: payload,
            RequiresAck: true,
            TargetResourceId: node.Id // Target is the node itself, not a VM
        );

        node.GpuSetupStatus = GpuSetupStatus.InProgress;
        foreach (var gpu in node.HardwareInventory.Gpus)
        {
            gpu.SetupStatus = GpuSetupStatus.InProgress;
        }

        // Register command so ProcessCommandAcknowledgmentAsync can route the ack.
        // Uses node.Id as the "VmId" since ConfigureGpu targets the node, not a VM.
        _dataStore.RegisterCommand(command.CommandId, node.Id, node.Id, NodeCommandType.ConfigureGpu);

        var result = await _commandService.DeliverCommandAsync(node.Id, command, ct);

        if (result.Success)
        {
            _logger.LogInformation(
                "ConfigureGpu command {CommandId} sent to node {NodeId} (mode: {Mode})",
                command.CommandId, node.Id, mode);
        }
        else
        {
            // Command delivery failed — mark as pending so next registration retries
            node.GpuSetupStatus = GpuSetupStatus.Pending;
            foreach (var gpu in node.HardwareInventory.Gpus)
            {
                gpu.SetupStatus = GpuSetupStatus.Pending;
            }

            _logger.LogWarning(
                "Failed to deliver ConfigureGpu command to node {NodeId}: {Message}. " +
                "Will retry on next registration/heartbeat.",
                node.Id, result.Message);
        }
    }
}

/// <summary>
/// Result of a manual GPU setup trigger
/// </summary>
public record GpuSetupResult(bool Success, string Message);

/// <summary>
/// Data returned by the node agent in the ConfigureGpu command acknowledgment.
/// Reports what was configured and current GPU readiness state.
/// </summary>
public class GpuSetupAckData
{
    /// <summary>Whether NVIDIA drivers + Container Toolkit are installed and working</summary>
    public bool ContainerSharingReady { get; set; }

    /// <summary>Whether VFIO/IOMMU passthrough is configured and ready</summary>
    public bool VfioPassthroughReady { get; set; }

    /// <summary>Whether IOMMU is enabled (may have been enabled during setup)</summary>
    public bool IommuEnabled { get; set; }

    /// <summary>Whether a host reboot is required to complete setup (e.g., IOMMU grub change)</summary>
    public bool RebootRequired { get; set; }

    /// <summary>Installed NVIDIA driver version (e.g., "535.129.03")</summary>
    public string? DriverVersion { get; set; }

    /// <summary>Error message if setup partially failed</summary>
    public string? ErrorMessage { get; set; }
}
