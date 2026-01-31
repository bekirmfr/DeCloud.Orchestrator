using DeCloud.Shared;
using Microsoft.IdentityModel.Tokens;
using Orchestrator.Models;
using Orchestrator.Persistence;
using Orchestrator.Services;
using Orchestrator.Services.VmScheduling;
using Org.BouncyCastle.Asn1.Ocsp;
using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Orchestrator.Services;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request, CancellationToken ct = default);
    Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat, CancellationToken ct = default);
    /// <summary>
    /// Process command acknowledgment from node
    /// </summary>
    Task<bool> ProcessCommandAcknowledgmentAsync(
        string nodeId,
        string commandId,
        CommandAcknowledgment ack);

    Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status);
    Task CheckNodeHealthAsync();
    /// <summary>
    /// Request node to sign an SSH certificate using its CA
    /// </summary>
    Task<CertificateSignResponse> SignCertificateAsync(
        string nodeId,
        CertificateSignRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Inject SSH public key into a VM's authorized_keys
    /// </summary>
    Task<bool> InjectSshKeyAsync(
        string nodeId,
        string vmId,
        string publicKey,
        string username = "root",
        CancellationToken ct = default);

    /// <summary>
    /// Search nodes based on criteria (for marketplace/public browsing)
    /// </summary>
    Task<List<NodeAdvertisement>> SearchNodesAsync(NodeSearchCriteria criteria);

    /// <summary>
    /// Get featured nodes (high uptime, good capacity)
    /// </summary>
    Task<List<NodeAdvertisement>> GetFeaturedNodesAsync();

    /// <summary>
    /// Get node advertisement details
    /// </summary>
    Task<NodeAdvertisement?> GetNodeAdvertisementAsync(string nodeId);
}

public class NodeService : INodeService
{
    private readonly DataStore _dataStore;
    private readonly IVmSchedulingService _schedulingService;
    private readonly ISchedulingConfigService _configService;
    private readonly IEventService _eventService;
    private readonly ICentralIngressService _ingressService;
    private readonly ILogger<NodeService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly HttpClient _httpClient;
    private readonly IRelayNodeService _relayNodeService;
    private readonly IServiceProvider _serviceProvider;
    private readonly IWireGuardManager _wireGuardManager;
    private readonly IConfiguration _configuration;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _cgnatSyncLocks = new();

    public NodeService(
        DataStore dataStore,
        IVmSchedulingService schedulingService,
        ISchedulingConfigService configService,
        IEventService eventService,
        ICentralIngressService ingressService,
        ILogger<NodeService> logger,
        ILoggerFactory loggerFactory,
        HttpClient httpClient,
        IRelayNodeService relayNodeService,
        IServiceProvider serviceProvider,
        IWireGuardManager wireGuardManager,
        IConfiguration configuration)
    {
        _dataStore = dataStore;
        _schedulingService = schedulingService;
        _configService = configService;
        _eventService = eventService;
        _ingressService = ingressService;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _httpClient = httpClient;
        _relayNodeService = relayNodeService;
        _serviceProvider = serviceProvider;
        _wireGuardManager = wireGuardManager;
        _configuration = configuration;
    }

    // ============================================================================
    // Registration and Heartbeat
    // ============================================================================

    // Implements deterministic node ID validation and registration

    public async Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request, CancellationToken ct = default)
    {
        // =====================================================
        // STEP 1: Validate Wallet Address
        // =====================================================
        if (string.IsNullOrWhiteSpace(request.WalletAddress) ||
            request.WalletAddress == "0x0000000000000000000000000000000000000000")
        {
            _logger.LogError("Node registration rejected: null wallet address");
            throw new ArgumentException(
                "Valid wallet address required for node registration. " +
                "Null address (0x000...000) is not allowed.");
        }

        // =====================================================
        // NEW: STEP 1.5: Verify Wallet Signature
        // =====================================================
        if (!VerifyWalletSignature(request.WalletAddress, request.Message, request.Signature))
        {
            _logger.LogError("Node registration rejected: Invalid wallet signature");
            throw new UnauthorizedAccessException("Invalid wallet signature. Please ensure you're using the correct wallet.");
        }

        // =====================================================
        // STEP 2: Validate Machine ID
        // =====================================================
        if (string.IsNullOrWhiteSpace(request.MachineId))
        {
            _logger.LogError("Node registration rejected: missing machine ID");
            throw new ArgumentException("Machine ID is required for node registration");
        }

        // =====================================================
        // STEP 3: Generate and Validate Node ID
        // =====================================================
        string nodeId;
        try
        {
            nodeId = NodeIdGenerator.GenerateNodeId(request.MachineId, request.WalletAddress);

            _logger.LogInformation(
                "✓ Wallet signature generated for node {NodeId}",
                nodeId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate node ID");
            throw new ArgumentException("Invalid machine ID or wallet address", ex);
        }

        // =====================================================
        // Get orchestrator WireGuard public key if available
        // =====================================================
        string? orchestratorPublicKey = null;

        orchestratorPublicKey = await _wireGuardManager.GetOrchestratorPublicKeyAsync(ct);
        _logger.LogInformation(
            "Including orchestrator WireGuard public key {PublicKey} in registration response for node {NodeId}",
            orchestratorPublicKey, nodeId);

        // =====================================================
        // Get scheduling configuration
        // =====================================================

        SchedulingConfig? schedulingConfig = await _configService.GetConfigAsync(ct);

        // =====================================================
        // Generate JWT token for node authentication
        // =====================================================
        var apiKey = GenerateNodeJwtToken(nodeId, request.WalletAddress, request.MachineId);
        var apiKeyHash = GenerateHash(apiKey);

        _logger.LogInformation("Generated JWT token for node {NodeId}", nodeId);

        // =====================================================
        // STEP 4: Create Node
        // =====================================================
        _logger.LogInformation(
            "Node registration: {NodeId} (Machine: {MachineId}, Wallet: {Wallet})",
            nodeId, request.MachineId, request.WalletAddress);

        var node = new Node
        {
            Id = nodeId,
            MachineId = request.MachineId,
            Name = request.Name,
            WalletAddress = request.WalletAddress,
            PublicIp = request.PublicIp,
            AgentPort = request.AgentPort,
            Status = NodeStatus.Online,
            HardwareInventory = request.HardwareInventory,
            // TotalResources will be set after performance evaluation
            TotalResources = new ResourceSnapshot(),
            ReservedResources = new ResourceSnapshot(),
            AgentVersion = request.AgentVersion,
            SupportedImages = request.SupportedImages,
            Region = request.Region ?? "default",
            Zone = request.Zone ?? "default",
            RegisteredAt = request.RegisteredAt,
            LastSeenAt = DateTime.UtcNow
        };

        // =====================================================
        // STEP 5: Performance Evaluation & Capacity Calculation
        // =====================================================
        var performanceLogger = _loggerFactory.CreateLogger<NodePerformanceEvaluator>();
        var performanceEvaluator = new NodePerformanceEvaluator(
            performanceLogger,
            _configService); // Pass config service

        node.PerformanceEvaluation = await performanceEvaluator.EvaluateNodeAsync(request.HardwareInventory, ct);

        if (!node.PerformanceEvaluation.IsAcceptable)
        {
            _logger.LogWarning(
                "Node {NodeId} rejected during registration: {Reason}",
                nodeId,
                node.PerformanceEvaluation.RejectionReason);

            throw new InvalidOperationException(
                $"Node performance below minimum requirements: {node.PerformanceEvaluation.RejectionReason}");
        }

        // Calculate total capacity using NodeCapacityCalculator
        var capacityLogger = _loggerFactory.CreateLogger<NodeCapacityCalculator>();
        var capacityCalculator = new NodeCapacityCalculator(
            capacityLogger,
            _configService); // Pass config service

        var totalCapacity = await capacityCalculator.CalculateTotalCapacityAsync(node, ct);

        node.TotalResources = new ResourceSnapshot
        {
            ComputePoints = totalCapacity.TotalComputePoints,
            MemoryBytes = totalCapacity.TotalMemoryBytes,
            StorageBytes = totalCapacity.TotalStorageBytes
        };

        _logger.LogInformation(
            "Node {NodeId} accepted: Highest tier={Tier}, Total capacity={Points} points",
            nodeId,
            node.PerformanceEvaluation.HighestTier,
            totalCapacity.TotalComputePoints);

        // =====================================================
        // Generate and save API key
        // =====================================================
        node.ApiKeyHash = apiKeyHash;
        node.ApiKeyCreatedAt = DateTime.UtcNow;
        node.ApiKeyLastUsedAt = null;

        await _dataStore.SaveNodeAsync(node);

        _logger.LogInformation(
            "✓ Node registered successfully: {NodeId}",
            node.Id);

        // =====================================================
        // STEP 6: Relay Node Deployment & Assignment
        // =====================================================
        if (_relayNodeService.IsEligibleForRelay(node) && node.RelayInfo == null)
        {
            _logger.LogInformation(
                "Node {NodeId} is eligible for relay - deploying relay VM",
                node.Id);

            var vmService = _serviceProvider.GetRequiredService<IVmService>();
            var relayVmId = await _relayNodeService.DeployRelayVmAsync(node, vmService);

            if (relayVmId != null)
            {
                _logger.LogInformation(
                    "Relay VM {VmId} deployed successfully for node {NodeId}",
                    relayVmId, node.Id);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to deploy relay VM for eligible node {NodeId}",
                    node.Id);
            }
            await _dataStore.SaveNodeAsync(node);
        }
        else
        {
            _logger.LogInformation(
                "Node {NodeId} is not eligible for relay",
                node.Id);
        }

        // Check if node is behind CGNAT and needs relay assignment
        if (node.HardwareInventory.Network.NatType != NatType.None)
        {
            if (node.CgnatInfo == null)
            {
                _logger.LogInformation(
                "Node {NodeId} is behind CGNAT (type: {NatType}) - assigning to relay",
                node.Id, node.HardwareInventory.Network.NatType);

                var relay = await _relayNodeService.FindBestRelayForCgnatNodeAsync(node);

                if (relay != null)
                {
                    await _relayNodeService.AssignCgnatNodeToRelayAsync(node, relay);
                }
                else
                {
                    _logger.LogWarning(
                        "No available relay found for CGNAT node {NodeId}",
                        node.Id);
                }
            }
            else
            {
                _logger.LogInformation(
                "Node {NodeId} is behind CGNAT (type: {NatType}) - already assigned to relay {RelayNodeId}",
                node.Id, node.HardwareInventory.Network.NatType, node.CgnatInfo.AssignedRelayNodeId);

                // This is probably a re-registration of a cgnat node already assigned with a relay - verify relay is still valid


            }
        }

        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.NodeRegistered,
            ResourceType = "node",
            ResourceId = node.Id,
            NodeId = node.Id,
            Payload = new Dictionary<string, object>
            {
                ["name"] = node.Name,
                ["region"] = node.Region,
                ["machineId"] = node.MachineId,
                ["wallet"] = node.WalletAddress,
                ["resources"] = JsonSerializer.Serialize(node.TotalResources)
            }
        });

        return new NodeRegistrationResponse(
            node.Id,
            node.PerformanceEvaluation,
            apiKey,
            schedulingConfig,
            orchestratorPublicKey,
            TimeSpan.FromSeconds(15));
    }

    private bool VerifyWalletSignature(string walletAddress, string message, string signature)
    {
        try
        {
            var signer = new Nethereum.Signer.EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            return string.Equals(
                recoveredAddress,
                walletAddress,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying wallet signature");
            return false;
        }
    }

    /// <summary>
    /// Process heartbeat without overwriting orchestrator resource tracking
    /// </summary>
    public async Task<NodeHeartbeatResponse> ProcessHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat,
    CancellationToken ct = default)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
        {
            return new NodeHeartbeatResponse(false, null, null, null);
        }

        var currentConfig = await _configService.GetConfigAsync(ct);

        var wasOffline = node.Status == NodeStatus.Offline;
        node.Status = NodeStatus.Online;
        node.LastHeartbeat = DateTime.UtcNow;
        node.LatestMetrics = heartbeat.Metrics;
        node.LastSeenAt = DateTime.UtcNow;

        if (node.IsBehindCgnat)
        {
            await SyncCgnatStateFromHeartbeatAsync(node, heartbeat.CgnatInfo, ct);
        }

        // Log discrepancy between node-reported and orchestrator-tracked resources
        var nodeReportedFree = heartbeat.AvailableResources;
        var orchestratorTrackedFree = new ResourceSnapshot
        {
            ComputePoints = node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints,
            MemoryBytes = node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes,
            StorageBytes = node.TotalResources.StorageBytes - node.ReservedResources.StorageBytes
        };

        var computePointDiff = Math.Abs(
            (node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints) -
            (nodeReportedFree.ComputePoints));
        var memDiff = Math.Abs(nodeReportedFree.MemoryBytes - orchestratorTrackedFree.MemoryBytes);

        if (computePointDiff > 1 || memDiff > 1024)
        {
            _logger.LogWarning("Resource drift detected on node {NodeId}", nodeId);

            _logger.LogDebug(
                "Resource tracking drift on node {NodeId}: " +
                "Node reports {NodeComputePoints} point(s) / {NodeMem} MB free, " +
                "Orchestrator tracks {OrcComputePoints} point(s) / {OrcMem}MB free " + 
                "(Reserved: {ResComputePoints} point(s) / {ResMem} MB)",
                nodeId,
                nodeReportedFree.ComputePoints, nodeReportedFree.MemoryBytes,
                orchestratorTrackedFree.ComputePoints, orchestratorTrackedFree.MemoryBytes,
                node.ReservedResources.ComputePoints, node.ReservedResources.MemoryBytes);

            // TO-DO: Implement resource reconciliation logic here
            // For now, we just log the discrepancy and update the node based on the ehartbeat
        }

        // If node was offline and is now back online, reset downtime tracking
        if (wasOffline)
        {
            node.LastFailedHeartbeatCheckAt = null; // Reset for next potential downtime
        }

        await _dataStore.SaveNodeAsync(node);

        // Update node reputation metrics (uptime tracking)
        // Note: We use IServiceProvider to avoid circular dependency
        var reputationService = _serviceProvider.GetService<INodeReputationService>();
        if (reputationService != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await reputationService.UpdateUptimeAsync(nodeId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to update uptime for node {NodeId}", nodeId);
                }
            });
        }

        if (wasOffline)
        {
            _logger.LogInformation("Node {NodeId} came back online", nodeId);
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.NodeOnline,
                ResourceType = "node",
                ResourceId = nodeId
            });
        }

        // Synchronize VM state from heartbeat
        await SyncVmStateFromHeartbeatAsync(nodeId, heartbeat);

        AgentSchedulingConfig? agentSchedulingConfig = null;

        if (heartbeat.SchedulingConfigVersion == 0 ||
        heartbeat.SchedulingConfigVersion != currentConfig.Version)
        {
            agentSchedulingConfig = currentConfig.MapToAgentConfig();

            _logger.LogInformation(
                "Node {NodeId} has outdated config (node: v{NodeVersion}, current: v{CurrentVersion}), " +
                "sending updated config in heartbeat response. " +
                "Baseline: {Baseline}, Overcommit: {Overcommit:F1}",
                nodeId,
                heartbeat.SchedulingConfigVersion,
                currentConfig.Version,
                currentConfig.BaselineBenchmark,
                currentConfig.Tiers[QualityTier.Burstable].CpuOvercommitRatio);
        }
        else
        {
            // Config is up-to-date, don't send it (saves bandwidth)
            _logger.LogDebug(
                "Node {NodeId} has current config v{Version}, no update needed",
                nodeId, currentConfig.Version);
        }

        // Get pending commands for this node
        var commands = _dataStore.GetAndClearPendingCommands(nodeId);

        return new NodeHeartbeatResponse(true, commands.Count > 0 ? commands : null, agentSchedulingConfig, node.CgnatInfo);
    }

    /// <summary>
    /// Process command acknowledgment from node.
    /// Uses multiple lookup strategies for reliability.
    /// </summary>
    public async Task<bool> ProcessCommandAcknowledgmentAsync(
        string nodeId,
        string commandId,
        CommandAcknowledgment ack)
    {
        _logger.LogInformation(
            "Processing acknowledgment for command {CommandId} from node {NodeId}: Success={Success}",
            commandId, nodeId, ack.Success);

        // ====================================================================
        // MULTI-STRATEGY VM LOOKUP (in order of reliability)
        // ====================================================================

        VirtualMachine? affectedVm = null;
        string lookupMethod = "none";
        CommandRegistration? registration = null;

        // Strategy 1: Command Registry (most reliable)
        if (_dataStore.TryCompleteCommand(commandId, out registration))
        {
            var affectedm = await _dataStore.GetVmAsync(registration!.VmId);
            if (affectedVm != null)
            {
                lookupMethod = "command_registry";
                _logger.LogDebug(
                    "Found VM {VmId} via command registry for command {CommandId}",
                    registration.VmId, commandId);
            }
            else
            {
                _logger.LogWarning(
                    "Command registry pointed to non-existent VM {VmId} for command {CommandId}",
                    registration.VmId, commandId);
            }
        }

        // Strategy 2: VM's ActiveCommandId field (backup)
        if (affectedVm == null)
        {
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms.FirstOrDefault(vm =>
                    vm.ActiveCommandId == commandId);

            if (affectedVm != null)
            {
                lookupMethod = "active_command_id";
                _logger.LogDebug(
                    "Found VM {VmId} via ActiveCommandId for command {CommandId}",
                    affectedVm.Id, commandId);
            }
        }

        // Strategy 3: StatusMessage contains commandId (legacy fallback)
        if (affectedVm == null)
        {
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms
                .FirstOrDefault(vm =>
                    vm.StatusMessage != null &&
                    vm.StatusMessage.Contains(commandId));

            if (affectedVm != null)
            {
                lookupMethod = "status_message_legacy";
                _logger.LogWarning(
                    "Found VM {VmId} via StatusMessage fallback for command {CommandId}. " +
                    "Command tracking may be degraded - check if ActiveCommandId is being set.",
                    affectedVm.Id, commandId);
            }
        }

        // Strategy 4: For DeleteVm commands, try to find VM in Deleting status on this node
        if (affectedVm == null && registration?.CommandType == NodeCommandType.DeleteVm)
        {
            var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
            affectedVm = nodeVms.FirstOrDefault(vm =>
                    vm.Status == VmStatus.Deleting);

            if (affectedVm != null)
            {
                lookupMethod = "deleting_status_heuristic";
                _logger.LogWarning(
                    "Found VM {VmId} via Deleting status heuristic for command {CommandId}. " +
                    "This is a last-resort lookup - investigate why primary methods failed.",
                    affectedVm.Id, commandId);
            }
        }

        // ====================================================================
        // HANDLE LOOKUP FAILURE
        // ====================================================================

        if (affectedVm == null)
        {
            _logger.LogError(
                "CRITICAL: Could not find VM for command {CommandId} from node {NodeId}. " +
                "Command type: {Type}. Resources may be leaked if this was a deletion. " +
                "Manual cleanup may be required.",
                commandId, nodeId, registration?.CommandType.ToString() ?? "unknown");

            // Emit alert event for monitoring/alerting systems
            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "command",
                ResourceId = commandId,
                NodeId = nodeId,
                Payload = new Dictionary<string, object>
                {
                    ["Error"] = "Orphaned command acknowledgement - VM not found",
                    ["CommandId"] = commandId,
                    ["CommandType"] = registration?.CommandType.ToString() ?? "unknown",
                    ["ExpectedVmId"] = registration?.VmId ?? "unknown",
                    ["AckSuccess"] = ack.Success
                }
            });

            // Return true - we received the ack even if we couldn't process it
            // The stale command cleanup service will handle stuck VMs
            return true;
        }

        _logger.LogInformation(
            "Processing command {CommandId} for VM {VmId} (lookup: {Method})",
            commandId, affectedVm.Id, lookupMethod);

        // ====================================================================
        // CLEAR COMMAND TRACKING ON VM
        // ====================================================================

        affectedVm.ActiveCommandId = null;
        affectedVm.ActiveCommandType = null;
        affectedVm.ActiveCommandIssuedAt = null;

        // ====================================================================
        // HANDLE COMMAND FAILURE
        // ====================================================================

        if (!ack.Success)
        {
            _logger.LogError(
                "Command {CommandId} failed on node {NodeId}: {Error}",
                commandId, nodeId, ack.ErrorMessage ?? "Unknown error");

            // Handle failure based on VM status
            if (affectedVm.Status == VmStatus.Deleting)
            {
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Deletion failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);

                _logger.LogWarning(
                    "VM {VmId} deletion failed - resources remain reserved. " +
                    "Manual intervention may be required.",
                    affectedVm.Id);
            }
            else if (affectedVm.Status == VmStatus.Provisioning)
            {
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Creation failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);
            }
            else if (affectedVm.Status == VmStatus.Stopping)
            {
                // Stop failed - VM might still be running
                affectedVm.Status = VmStatus.Error;
                affectedVm.StatusMessage = $"Stop failed: {ack.ErrorMessage ?? "Unknown error"}";
                affectedVm.UpdatedAt = DateTime.UtcNow;
                await _dataStore.SaveVmAsync(affectedVm);
            }

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["CommandId"] = commandId,
                    ["CommandType"] = registration?.CommandType.ToString() ?? "unknown",
                    ["Error"] = ack.ErrorMessage ?? "Unknown error"
                }
            });

            return true;
        }

        // ====================================================================
        // HANDLE COMMAND SUCCESS
        // ====================================================================

        if (affectedVm.Status == VmStatus.Deleting)
        {
            _logger.LogInformation(
                "Deletion confirmed for VM {VmId} - completing deletion and freeing resources",
                affectedVm.Id);

            await CompleteVmDeletionAsync(affectedVm);
        }
        else if (affectedVm.Status == VmStatus.Provisioning)
        {
            _logger.LogInformation(
                "Creation confirmed for VM {VmId} - marking as running",
                affectedVm.Id);

            affectedVm.Status = VmStatus.Running;
            affectedVm.PowerState = VmPowerState.Running;
            affectedVm.StartedAt = ack.CompletedAt;
            affectedVm.StatusMessage = null;
            affectedVm.UpdatedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(affectedVm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStarted,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId
            });

            await _ingressService.OnVmStartedAsync(affectedVm.Id);
        }
        else if (affectedVm.Status == VmStatus.Stopping)
        {
            _logger.LogInformation(
                "Stop confirmed for VM {VmId} - marking as stopped",
                affectedVm.Id);

            affectedVm.Status = VmStatus.Stopped;
            affectedVm.PowerState = VmPowerState.Off;
            affectedVm.StoppedAt = ack.CompletedAt;
            affectedVm.StatusMessage = null;
            affectedVm.UpdatedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(affectedVm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmStopped,
                ResourceType = "vm",
                ResourceId = affectedVm.Id,
                NodeId = nodeId,
                UserId = affectedVm.OwnerId
            });

            await _ingressService.OnVmStoppedAsync(affectedVm.Id);
        }

        return true;
    }

    /// <summary>
    /// Complete VM deletion after node confirmation.
    /// Frees all reserved resources and updates quotas.
    /// </summary>
    private async Task CompleteVmDeletionAsync(VirtualMachine vm)
    {
        _logger.LogInformation(
            "Completing deletion for VM {VmId} (Owner: {Owner}, Node: {Node})",
            vm.Id, vm.OwnerId, vm.NodeId ?? "none");

        // Step 1: Mark as Deleted
        vm.Status = VmStatus.Deleted;
        vm.StatusMessage = "Deletion confirmed by node";
        vm.StoppedAt = DateTime.UtcNow;
        vm.UpdatedAt = DateTime.UtcNow;

        // Clear any remaining command tracking
        vm.ActiveCommandId = null;
        vm.ActiveCommandType = null;
        vm.ActiveCommandIssuedAt = null;

        await _dataStore.SaveVmAsync(vm);

        // Step 2: Free reserved resources from node
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (!string.IsNullOrEmpty(vm.NodeId) &&
            node != null)
        {
            var computePointsToFree = vm.Spec.ComputePointCost;
            var memToFree = vm.Spec.MemoryBytes;
            var memToFreeMb = memToFree / (1024 * 1024);
            var storageToFree = vm.Spec.DiskBytes;
            var storageToFreeGb = storageToFree / (1024 * 1024 * 1024);

            // Free CPU cores (legacy)
            node.ReservedResources.ComputePoints = Math.Max(0,
                node.ReservedResources.ComputePoints - computePointsToFree);
            node.ReservedResources.MemoryBytes = Math.Max(0,
                node.ReservedResources.MemoryBytes - memToFree);
            node.ReservedResources.StorageBytes = Math.Max(0,
                node.ReservedResources.StorageBytes - storageToFree);

            await _dataStore.SaveNodeAsync(node);

            _logger.LogInformation(
                "Released reserved resources for VM {VmId} on node {NodeId}: " +
                "{ComputePoints} point(s), {MemoryMb} MB, {StorageGb} GB. " +
                "Node now has: Reserved={ResComputePoints} point(s), Available={AvComputePoints}pts",
                vm.Id, node.Id, computePointsToFree, memToFreeMb, storageToFreeGb,
                node.ReservedResources.ComputePoints, node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints);
        }
        else
        {
            _logger.LogWarning(
                "Could not find node {NodeId} to release resources for VM {VmId}",
                vm.NodeId, vm.Id);
        }

        // Step 3: Update user quotas
        if (_dataStore.Users.TryGetValue(vm.OwnerId, out var user))
        {
            user.Quotas.CurrentVms = Math.Max(0, user.Quotas.CurrentVms - 1);
            user.Quotas.CurrentVirtualCpuCores = Math.Max(0,
                user.Quotas.CurrentVirtualCpuCores - vm.Spec.VirtualCpuCores);
            user.Quotas.CurrentMemoryBytes = Math.Max(0,
                user.Quotas.CurrentMemoryBytes - vm.Spec.MemoryBytes);
            user.Quotas.CurrentStorageBytes = Math.Max(0,
                user.Quotas.CurrentStorageBytes - vm.Spec.DiskBytes);

            await _dataStore.SaveUserAsync(user);

            _logger.LogInformation(
                "Updated quotas for user {UserId}: VMs={VMs}/{MaxVMs}, CPU={CPU}c, MEM={MEM}MB",
                user.Id, user.Quotas.CurrentVms, user.Quotas.MaxVms,
                user.Quotas.CurrentVirtualCpuCores, user.Quotas.CurrentMemoryBytes);
        }

        await _ingressService.OnVmDeletedAsync(vm.Id);

        // Step 4: Emit completion event
        await _eventService.EmitAsync(new OrchestratorEvent
        {
            Type = EventType.VmDeleted,
            ResourceType = "vm",
            ResourceId = vm.Id,
            NodeId = vm.NodeId,
            UserId = vm.OwnerId,
            Payload = new Dictionary<string, object>
            {
                ["FreedCpu"] = vm.Spec.VirtualCpuCores,
                ["FreedMemoryBytes"] = vm.Spec.MemoryBytes,
                ["FreedStorageBytes"] = vm.Spec.DiskBytes
            }
        });

        _logger.LogInformation(
            "VM {VmId} deletion completed successfully - all resources freed",
            vm.Id);
    }

    /// <summary>
    /// Synchronizes CGNAT node relay assignment between orchestrator state and node heartbeat.
    /// Ensures CGNAT nodes maintain valid relay assignments by:
    /// - Detecting and resolving relay assignment mismatches
    /// - Validating relay node and VM health
    /// - Triggering relay failover when assignments become invalid
    /// - Preventing assignment thrashing via per-node locking
    /// </summary>
    /// <remarks>
    /// Thread-safe: Uses per-node semaphore to prevent concurrent modifications.
    /// Idempotent: Can be called multiple times safely without side effects.
    /// </remarks>
    private async Task SyncCgnatStateFromHeartbeatAsync(
        Node node,
        CgnatNodeInfo? heartbeatCgnatInfo,
        CancellationToken ct = default)
    {
        // ========================================
        // STEP 0: Validate Node Should Be Behind CGNAT
        // ========================================
        if (node.HardwareInventory.Network.NatType == NatType.None)
        {
            // Node is NOT behind CGNAT - should not have CgnatInfo
            if (node.CgnatInfo != null)
            {
                _logger.LogWarning(
                    "Node {NodeId} is not behind CGNAT (NAT type: None) but has CgnatInfo - clearing it",
                    node.Id);
                node.CgnatInfo = null;
                await _dataStore.SaveNodeAsync(node);
            }
            return;
        }

        // ========================================
        // STEP 1: Acquire Node-Specific Lock
        // ========================================
        // Prevent concurrent heartbeats from the same node from racing
        var nodeLock = _cgnatSyncLocks.GetOrAdd(node.Id, _ => new SemaphoreSlim(1, 1));

        if (!await nodeLock.WaitAsync(0, ct))
        {
            _logger.LogDebug(
                "Skipping CGNAT sync for node {NodeId} - already in progress",
                node.Id);
            return;
        }

        try
        {
            // ========================================
            // STEP 2: Safely Get Relay Node References
            // ========================================
            Node? trackedRelayNode = null;
            Node? reportedRelayNode = null;

            // Get tracked relay (what orchestrator thinks node is assigned to)
            if (!string.IsNullOrEmpty(node.CgnatInfo?.AssignedRelayNodeId))
            {
                trackedRelayNode = await _dataStore.GetNodeAsync(node.CgnatInfo.AssignedRelayNodeId);
            }

            // Get reported relay (what node reports in heartbeat)
            if (!string.IsNullOrEmpty(heartbeatCgnatInfo?.AssignedRelayNodeId))
            {
                reportedRelayNode = await _dataStore.GetNodeAsync(heartbeatCgnatInfo.AssignedRelayNodeId);
            }

            _logger.LogDebug(
                "CGNAT sync for node {NodeId}: Tracked={TrackedId}, Reported={ReportedId}",
                node.Id,
                trackedRelayNode?.Id ?? "null",
                reportedRelayNode?.Id ?? "null");

            // ========================================
            // STEP 3: Handle Edge Cases
            // ========================================

            // Case 1: Orchestrator has no record of relay assignment (error state)
            if (node.CgnatInfo == null || trackedRelayNode == null)
            {
                await HandleMissingTrackedRelayAsync(
                    node,
                    reportedRelayNode,
                    heartbeatCgnatInfo,
                    ct);
                return;
            }

            // Case 2: Node heartbeat has no relay info (error state)
            if (heartbeatCgnatInfo == null || reportedRelayNode == null)
            {
                await HandleMissingReportedRelayAsync(
                    node,
                    trackedRelayNode,
                    ct);
                return;
            }

            // ========================================
            // STEP 4: Check for Assignment Mismatch
            // ========================================
            if (trackedRelayNode.Id != reportedRelayNode.Id)
            {
                await HandleRelayMismatchAsync(
                    node,
                    trackedRelayNode,
                    reportedRelayNode,
                    heartbeatCgnatInfo,
                    ct);
                return;
            }

            // ========================================
            // STEP 5: Verify Current Assignment is Still Valid
            // ========================================
            if (!await IsRelayValidAsync(trackedRelayNode.Id))
            {
                _logger.LogWarning(
                    "Assigned relay {RelayId} for CGNAT node {NodeId} is no longer valid - finding replacement",
                    trackedRelayNode.Id,
                    node.Id);

                await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, trackedRelayNode, ct);
                await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
                return;
            }

            // ========================================
            // STEP 6: All Good - Assignment is Valid
            // ========================================
            // Self-healing - always ensure peer is registered
            // This auto-recovers from:
            // - Relay VM restart (WireGuard config lost)
            // - Database corruption (ConnectedNodeIds empty)
            // - Manual intervention (peer manually removed)

            if (!string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
            {
                _logger.LogDebug(
                    "CGNAT node {NodeId} relay assignment is valid: {RelayId} - ensuring peer registered",
                    node.Id, trackedRelayNode.Id);

                // ✅ SELF-HEALING: Idempotent peer registration
                // If peer exists on relay VM → no-op
                // If peer missing → auto-register
                await _relayNodeService.EnsurePeerRegisteredAsync(node, trackedRelayNode, ct);
            }
            else
            {
                _logger.LogDebug(
                    "CGNAT node {NodeId} relay assignment is valid: {RelayId}",
                    node.Id, trackedRelayNode.Id);
            }
        }
        finally
        {
            nodeLock.Release();
        }
    }

    // ============================================================================
    // HELPER METHODS
    // ============================================================================

    /// <summary>
    /// Handle case where orchestrator has no record of relay assignment
    /// but node may be reporting one
    /// </summary>
    private async Task HandleMissingTrackedRelayAsync(
        Node node,
        Node? reportedRelayNode,
        CgnatNodeInfo? heartbeatCgnatInfo,
        CancellationToken ct)
    {
        _logger.LogError(
            "Node {NodeId} is behind CGNAT but has no relay assignment in orchestrator records",
            node.Id);

        // Try to use what the node reports if valid
        if (reportedRelayNode != null &&
            heartbeatCgnatInfo != null &&
            await IsRelayValidAsync(reportedRelayNode.Id) &&
            await ValidateNodeIsKnownToRelay(node, reportedRelayNode))
        {
            _logger.LogInformation(
                "Adopting reported relay {RelayId} for CGNAT node {NodeId}",
                reportedRelayNode.Id, node.Id);

            var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, reportedRelayNode, ct);
            if (success)
                return;
        }

        // Reported relay not valid or assignment failed - find new one
        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Handle case where node heartbeat has no relay info
    /// but orchestrator has a tracked assignment
    /// </summary>
    private async Task HandleMissingReportedRelayAsync(
        Node node,
        Node trackedRelayNode,
        CancellationToken ct)
    {
        _logger.LogError(
            "Node {NodeId} is behind CGNAT but did not report relay assignment in heartbeat",
            node.Id);

        // ========================================
        // GRACEFUL RECONCILIATION:
        // Check if orchestrator already has valid assignment
        // If yes, REUSE existing config instead of creating new one
        // This prevents duplicate peer accumulation on relay VMs
        // ========================================

        if (await IsRelayValidAsync(trackedRelayNode.Id))
        {
            // Check if node has existing tunnel IP and config
            if (!string.IsNullOrEmpty(node.CgnatInfo?.TunnelIp))
            {
                _logger.LogInformation(
                    "Node {NodeId} has existing valid relay assignment to {RelayId} (Tunnel IP: {TunnelIp}) - reusing config",
                    node.Id, trackedRelayNode.Id, node.CgnatInfo.TunnelIp);

                // Optionally: Ensure peer is registered on relay (idempotent)
                // This handles cases where relay VM was restarted and lost WireGuard config
                await _relayNodeService.EnsurePeerRegisteredAsync(node, trackedRelayNode, ct);

                // ✅ Early return - existing assignment is valid
                // The heartbeat response will include this existing config from node.CgnatInfo
                return;
            }
            else
            {
                // Node assigned to relay but has no tunnel IP - needs new config
                _logger.LogWarning(
                    "Node {NodeId} assigned to relay {RelayId} but has no tunnel IP - creating new assignment",
                    node.Id, trackedRelayNode.Id);

                var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, trackedRelayNode, ct);
                if (success)
                    return;
            }
        }

        // ========================================
        // Only reach here if:
        // - Tracked relay is invalid, OR
        // - Node has no tunnel IP in CgnatInfo, OR
        // - Assignment failed
        // ========================================
        _logger.LogWarning(
            "No valid relay assignment exists for node {NodeId} - finding new relay",
            node.Id);

        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Handle case where node reports different relay than orchestrator has tracked
    /// </summary>
    private async Task HandleRelayMismatchAsync(
        Node node,
        Node trackedRelayNode,
        Node reportedRelayNode,
        CgnatNodeInfo heartbeatCgnatInfo,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Relay assignment mismatch for node {NodeId}: " +
            "Orchestrator has {TrackedId}, node reports {ReportedId}",
            node.Id,
            trackedRelayNode.Id,
            reportedRelayNode.Id);

        // Remove from tracked relay (it's out of sync)
        await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, trackedRelayNode, ct);

        // Check if reported relay is valid and recognizes this node
        if (await IsRelayValidAsync(reportedRelayNode.Id) &&
            await ValidateNodeIsKnownToRelay(node, reportedRelayNode))
        {
            _logger.LogInformation(
                "Reported relay {RelayId} is valid and recognizes node {NodeId} - adopting it",
                reportedRelayNode.Id,
                node.Id);

            var success = await _relayNodeService.AssignCgnatNodeToRelayAsync(node, reportedRelayNode, ct);
            if (success)
                return; // ← CRITICAL: Early return on success
        }
        else
        {
            // Reported relay is invalid or doesn't recognize node
            _logger.LogWarning(
                "Reported relay {RelayId} is not valid or doesn't recognize node {NodeId} - finding new relay",
                reportedRelayNode.Id,
                node.Id);

            // Clean up reported relay if it exists but is invalid
            await _relayNodeService.RemoveCgnatNodeFromRelayAsync(node, reportedRelayNode, ct);
        }

        // Find a new relay as fallback
        await _relayNodeService.FindAndAssignNewRelayAsync(node, ct);
    }

    /// <summary>
    /// Validate that a relay node and its VM are in acceptable state
    /// CHANGE: Accept Degraded status - it might just be tunnel issue
    /// </summary>
    private async Task<bool> IsRelayValidAsync(string relayNodeId)
    {
        if (string.IsNullOrEmpty(relayNodeId))
            return false;

        var relayNode = await _dataStore.GetNodeAsync(relayNodeId);
        if (relayNode == null)
            return false;

        if (relayNode.Status != NodeStatus.Online)
            return false;

        if (relayNode.RelayInfo == null)
            return false;

        // Degraded means "tunnel issues" not "relay broken"
        // We'll try to heal the tunnel rather than abandon the relay
        if (relayNode.RelayInfo.Status != RelayStatus.Active &&
            relayNode.RelayInfo.Status != RelayStatus.Degraded)
        {
            return false;
        }

        if (string.IsNullOrEmpty(relayNode.RelayInfo.RelayVmId))
            return false;

        var relayVm = await _dataStore.GetVmAsync(relayNode.RelayInfo.RelayVmId);
        if (relayVm == null)
            return false;

        if (relayVm.Status != VmStatus.Running)
            return false;

        return true;
    }

    /// <summary>
    /// Validate that the relay node actually recognizes this CGNAT node
    /// Prevents accepting fake relay assignments from malicious nodes
    /// </summary>
    private async Task<bool> ValidateNodeIsKnownToRelay(
        Node cgnatNode,
        Node relayNode)
    {
        if (relayNode.RelayInfo == null)
        {
            return false;
        }

        // Check if relay has this node in its connected list
        var isKnown = relayNode.RelayInfo.ConnectedNodeIds?.Contains(cgnatNode.Id) ?? false;

        if (!isKnown)
        {
            _logger.LogWarning(
                "CGNAT node {NodeId} claims assignment to relay {RelayId} but relay doesn't recognize it",
                cgnatNode.Id, relayNode.Id);
        }

        return isKnown;
    }

    /// <summary>
    /// Synchronize VM state reported by node agent with orchestrator's view
    /// Handles VM state updates and orphan VM recovery
    /// </summary>
    private async Task SyncVmStateFromHeartbeatAsync(string nodeId, NodeHeartbeat heartbeat)
    {
        if (heartbeat.ActiveVms == null || !heartbeat.ActiveVms.Any())
            return;

        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
        var knownVmIds = nodeVms.Select(v => v.Id).ToHashSet();

        var node = await _dataStore.GetNodeAsync(nodeId);

        foreach (var reported in heartbeat.ActiveVms)
        {
            var vmId = reported.VmId;

            var vm = await _dataStore.GetVmAsync(vmId);
            if (vm != null)
            {
                // Update existing VM state
                var newStatus = ParseVmStatus(reported.State);
                var newPowerState = ParsePowerState(reported.State);

                if (vm.Status != newStatus || vm.PowerState != newPowerState)
                {
                    var wasRunning = vm.Status == VmStatus.Running;
                    vm.Status = newStatus;
                    vm.PowerState = newPowerState;

                    if (newStatus == VmStatus.Running && vm.StartedAt == null)
                        vm.StartedAt = reported.StartedAt ?? DateTime.UtcNow;

                    await _dataStore.SaveVmAsync(vm);

                    if (newStatus == VmStatus.Running && !wasRunning)
                        await _ingressService.OnVmStartedAsync(vmId);
                    else if (newStatus != VmStatus.Running && wasRunning)
                        await _ingressService.OnVmStoppedAsync(vmId);

                    _logger.LogInformation(
                        "VM {VmId} state updated from heartbeat: {Status}/{PowerState}",
                        vmId, newStatus, newPowerState);
                }

                // Update access info if available
                if (reported.IsIpAssigned)
                {
                    // Update network config with actual libvirt IP
                    vm.NetworkConfig.PrivateIp = reported.IpAddress;
                    vm.NetworkConfig.IsIpAssigned = reported.IsIpAssigned;

                    vm.AccessInfo ??= new VmAccessInfo();
                    vm.AccessInfo.SshHost = reported.IpAddress;
                    vm.AccessInfo.SshPort = 22;

                    if (reported.VncPort != null)
                    {
                        // VNC accessible through WireGuard at node IP
                        vm.AccessInfo.VncHost = node?.PublicIp;
                        vm.AccessInfo.VncPort = reported.VncPort ?? 5900;
                    }

                    await _dataStore.SaveVmAsync(vm);
                }
            }
            else if (!string.IsNullOrEmpty(reported.OwnerId))
            {
                // Orphaned VM recovery
                await RecoverOrphanedVmAsync(nodeId, reported);
            }
        }
    }

    /// <summary>
    /// Recover VMs that exist on node but are unknown to orchestrator
    /// This handles orchestrator restarts where node state persists
    /// </summary>
    private async Task RecoverOrphanedVmAsync(string nodeId, HeartbeatVmInfo reported)
    {
        var vmId = reported.VmId;

        try
        {
            _logger.LogInformation(
                "Recovering orphaned VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, reported.OwnerId, reported.State);

            var recoveredVm = new VirtualMachine
            {
                Id = vmId,
                Name = reported.Name ?? $"recovered-{vmId[..8]}",
                OwnerId = reported.OwnerId,
                NodeId = nodeId,
                Status = ParseVmStatus(reported.State),
                PowerState = ParsePowerState(reported.State),
                StartedAt = reported.StartedAt,
                CreatedAt = reported.StartedAt ?? DateTime.UtcNow,
                NetworkConfig = new VmNetworkConfig
                {
                    PrivateIp = reported.IpAddress ?? "",
                    Hostname = reported.Name ?? "",
                    MacAddress = reported.MacAddress ?? "",
                    //PublicIp = ,
                    //PortMappings = [reported.VncPort],
                    //OverlayNetworkId = 
                },
                AccessInfo = new VmAccessInfo
                {
                    SshHost = reported.IpAddress,
                    SshPort = reported.SshPort ?? 2222,
                    VncHost = reported.IpAddress,
                    VncPort = reported.VncPort ?? 5900
                },
                Spec = new VmSpec
                {
                    VirtualCpuCores = reported.VirtualCpuCores,
                    MemoryBytes = reported.MemoryBytes.Value,
                    DiskBytes = reported.DiskBytes.Value,
                    ImageId = reported.ImageId ?? "Unknown",
                    QualityTier = (QualityTier)reported.QualityTier,
                    ComputePointCost = reported.ComputePointCost,
                },
                StatusMessage = "Recovered from node heartbeat after orchestrator restart",
                Labels = new Dictionary<string, string>
                {
                    ["recovered"] = "true",
                    ["recovery-date"] = DateTime.UtcNow.ToString("O"),
                    ["recovery-node"] = nodeId
                }
            };

            await _dataStore.SaveVmAsync(recoveredVm);

            _logger.LogInformation(
                "✓ Successfully recovered VM {VmId} on node {NodeId} (Owner: {OwnerId}, State: {State})",
                vmId, nodeId, recoveredVm.OwnerId, recoveredVm.Status);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmRecovered,
                ResourceType = "vm",
                ResourceId = vmId,
                NodeId = nodeId,
                UserId = recoveredVm.OwnerId,
                Payload = new Dictionary<string, object>
                {
                    ["nodeId"] = nodeId,
                    ["state"] = recoveredVm.Status.ToString(),
                    ["ipAddress"] = recoveredVm.NetworkConfig.PrivateIp ?? "",
                    ["recoveryTimestamp"] = DateTime.UtcNow
                }
            });

            if (recoveredVm.Status == VmStatus.Running)
            {
                _logger.LogInformation(
                    "Registering recovered running VM {VmId} with CentralIngress",
                    vmId);
                await _ingressService.OnVmStartedAsync(vmId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to recover orphaned VM {VmId} on node {NodeId}",
                vmId, nodeId);
        }
    }

    private VmStatus ParseVmStatus(string? state) => state?.ToLower() switch
    {
        "running" => VmStatus.Running,
        "stopped" => VmStatus.Stopped,
        "paused" => VmStatus.Running,
        "stopping" => VmStatus.Stopping,
        "failed" => VmStatus.Error,
        "error" => VmStatus.Error,
        _ => VmStatus.Error
    };

    private VmPowerState ParsePowerState(string? state) => state?.ToLower() switch
    {
        "running" => VmPowerState.Running,
        "paused" => VmPowerState.Paused,
        "stopped" => VmPowerState.Off,
        _ => VmPowerState.Off
    };

    // ============================================================================
    // SSH Methods
    // ============================================================================

    /// <summary>
    /// Request node to sign an SSH certificate using its CA
    /// </summary>
    public async Task<CertificateSignResponse> SignCertificateAsync(
        string nodeId,
        CertificateSignRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var node = await _dataStore.GetNodeAsync(nodeId);
            if (node == null)
            {
                _logger.LogWarning("Node {NodeId} not found for certificate signing", nodeId);
                return new CertificateSignResponse
                {
                    Success = false,
                    Error = "Node not found"
                };
            }

            var url = $"http://{node.PublicIp}:{node.AgentPort}/api/ssh/sign-certificate";

            _logger.LogInformation(
                "Requesting certificate signing from node {NodeId} at {Url}",
                nodeId, url);

            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(url, content, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "Certificate signing failed: HTTP {StatusCode}, Body: {Body}",
                    httpResponse.StatusCode, errorBody);

                return new CertificateSignResponse
                {
                    Success = false,
                    Error = $"HTTP {httpResponse.StatusCode}: {errorBody}"
                };
            }

            var responseJson = await httpResponse.Content.ReadAsStringAsync(ct);
            var response = System.Text.Json.JsonSerializer.Deserialize<CertificateSignResponse>(responseJson);

            if (response == null)
            {
                return new CertificateSignResponse
                {
                    Success = false,
                    Error = "Failed to deserialize response"
                };
            }

            _logger.LogInformation(
                "✓ Certificate signed successfully for cert ID {CertId}",
                request.CertificateId);

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error requesting certificate signature from node {NodeId}", nodeId);
            return new CertificateSignResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    /// <summary>
    /// Inject SSH public key into a VM's authorized_keys
    /// </summary>
    [Obsolete("Use SSH certificates instead - VMs validate certificates end-to-end")]
    public async Task<bool> InjectSshKeyAsync(
        string nodeId,
        string vmId,
        string publicKey,
        string username = "root",
        CancellationToken ct = default)
    {
        try
        {
            var node = await _dataStore.GetNodeAsync(nodeId);
            if (node == null)
            {
                _logger.LogWarning("Node {NodeId} not found for SSH key injection", nodeId);
                return false;
            }

            var url = $"http://{node.PublicIp}:{node.AgentPort}/api/vms/{vmId}/ssh/inject-key";

            _logger.LogInformation(
                "Injecting SSH key into VM {VmId} on node {NodeId}",
                vmId, nodeId);

            var request = new InjectSshKeyRequest
            {
                PublicKey = publicKey,
                Username = username,
                Temporary = true
            };

            var json = System.Text.Json.JsonSerializer.Serialize(request);
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var httpResponse = await _httpClient.PostAsync(url, content, ct);

            if (!httpResponse.IsSuccessStatusCode)
            {
                var errorBody = await httpResponse.Content.ReadAsStringAsync(ct);
                _logger.LogError(
                    "SSH key injection failed: HTTP {StatusCode}, Body: {Body}",
                    httpResponse.StatusCode, errorBody);
                return false;
            }

            _logger.LogInformation(
                "✓ SSH key injected successfully into VM {VmId}",
                vmId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error injecting SSH key into VM {VmId} on node {NodeId}", vmId, nodeId);
            return false;
        }
    }

    // ============================================================================
    // Simple Getters
    // ============================================================================


    public async Task<bool> UpdateNodeStatusAsync(string nodeId, NodeStatus status)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return false;

        node.Status = status;
        await _dataStore.SaveNodeAsync(node);
        return true;
    }

    // ============================================================================
    // Health Monitoring
    // ============================================================================

    public async Task CheckNodeHealthAsync()
    {
        var heartbeatTimeout = TimeSpan.FromMinutes(2);
        var now = DateTime.UtcNow;

        var onlineNodes = _dataStore.GetActiveNodes()
            .Where(n => n.Status == NodeStatus.Online)
            .ToList();

        foreach (var node in onlineNodes)
        {
            var timeSinceLastHeartbeat = now - node.LastSeenAt;

            if (timeSinceLastHeartbeat > heartbeatTimeout)
            {
                _logger.LogWarning(
                    "Node {NodeId} ({Name}) marked as offline - no heartbeat for {Minutes:F1} minutes",
                    node.Id, node.Name, timeSinceLastHeartbeat?.TotalMinutes ?? 0);

                node.Status = NodeStatus.Offline;
                await _dataStore.SaveNodeAsync(node);

                await _eventService.EmitAsync(new OrchestratorEvent
                {
                    Type = EventType.NodeOffline,
                    ResourceType = "node",
                    ResourceId = node.Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["lastHeartbeat"] = node.LastHeartbeat,
                        ["timeoutMinutes"] = timeSinceLastHeartbeat?.TotalMinutes ?? 0
                    }
                });

                await MarkNodeVmsAsErrorAsync(node.Id);

                // Start tracking downtime for reputation
                // Record the moment node went offline
                var reputationService = _serviceProvider.GetService<INodeReputationService>();
                if (reputationService != null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Mark when downtime started (for tracking purposes)
                            node.LastFailedHeartbeatCheckAt = node.LastSeenAt;
                            await _dataStore.SaveNodeAsync(node);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to initialize downtime tracking for node {NodeId}", node.Id);
                        }
                    });
                }
            }
        }

        // For all offline nodes, record ongoing failures
        var offlineNodes = _dataStore.GetActiveNodes()
            .Where(n => n.Status == NodeStatus.Offline)
            .ToList();

        foreach (var node in offlineNodes)
        {
            var reputationService = _serviceProvider.GetService<INodeReputationService>();
            if (reputationService != null)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await reputationService.RecordFailedHeartbeatsSinceLastCheckAsync(node.Id);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to record failed heartbeats for node {NodeId}", node.Id);
                    }
                });
            }
        }
    }

    private async Task MarkNodeVmsAsErrorAsync(string nodeId)
    {
        var nodeVms = await _dataStore.GetVmsByNodeAsync(nodeId);
        
        nodeVms = nodeVms.Where(v => v.NodeId == nodeId && v.Status == VmStatus.Running)
            .ToList();

        foreach (var vm in nodeVms)
        {
            _logger.LogWarning("VM {VmId} on offline node {NodeId} marked as error",
                vm.Id, nodeId);

            vm.Status = VmStatus.Error;
            vm.StatusMessage = "Node went offline";
            vm.UpdatedAt = DateTime.UtcNow;

            await _dataStore.SaveVmAsync(vm);

            await _eventService.EmitAsync(new OrchestratorEvent
            {
                Type = EventType.VmError,
                ResourceType = "vm",
                ResourceId = vm.Id,
                UserId = vm.OwnerId,
                NodeId = nodeId,
                Payload = new Dictionary<string, object>
                {
                    ["reason"] = "Node offline",
                    ["nodeId"] = nodeId
                }
            });
        }
    }

    private string GenerateNodeJwtToken(string nodeId, string walletAddress, string machineId)
    {
        // Get JWT configuration (same as user JWT)
        var jwtKey = _configuration["Jwt:Key"]
            ?? "default-dev-key-change-in-production-min-32-chars!";
        var jwtIssuer = _configuration["Jwt:Issuer"] ?? "orchestrator";
        var jwtAudience = _configuration["Jwt:Audience"] ?? "orchestrator-client";

        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
        var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

        // Create claims for the node
        var claims = new[]
        {
        new Claim(ClaimTypes.NameIdentifier, nodeId),
        new Claim("node_id", nodeId),
        new Claim("wallet", walletAddress),
        new Claim("machine_id", machineId),
        new Claim(ClaimTypes.Role, "node"),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        new Claim(JwtRegisteredClaimNames.Iat,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            ClaimValueTypes.Integer64)
    };

        // Create long-lived token for nodes (1 year expiration)
        // Nodes don't need frequent token refresh like users
        var token = new JwtSecurityToken(
            issuer: jwtIssuer,
            audience: jwtAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddYears(1), // Long-lived for nodes
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private string GenerateHash(string data)
    {
        return Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(data)));
    }

    // ============================================================================
    // Node Search & Marketplace (moved from NodeMarketplaceService)
    // ============================================================================

    public async Task<List<NodeAdvertisement>> SearchNodesAsync(NodeSearchCriteria criteria)
    {
        var nodes = (await _dataStore.GetAllNodesAsync()).AsEnumerable();

        // Filter by online status
        if (criteria.OnlineOnly)
        {
            nodes = nodes.Where(n => n.Status == NodeStatus.Online);
        }

        // Filter by tags (node must have ALL specified tags)
        if (criteria.Tags?.Any() == true)
        {
            nodes = nodes.Where(n =>
                criteria.Tags.All(tag =>
                    n.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)));
        }

        // Filter by region
        if (!string.IsNullOrEmpty(criteria.Region))
        {
            nodes = nodes.Where(n =>
                n.Region.Equals(criteria.Region, StringComparison.OrdinalIgnoreCase));
        }

        // Filter by GPU requirement
        if (criteria.RequiresGpu == true)
        {
            nodes = nodes.Where(n => n.HardwareInventory.SupportsGpu);
        }

        // Filter by uptime
        if (criteria.MinUptimePercent.HasValue)
        {
            nodes = nodes.Where(n => n.UptimePercentage >= criteria.MinUptimePercent.Value);
        }

        // Filter by price
        if (criteria.MaxPricePerPoint.HasValue)
        {
            nodes = nodes.Where(n => n.BasePrice <= criteria.MaxPricePerPoint.Value);
        }

        // Filter by available capacity
        if (criteria.MinAvailableComputePoints.HasValue)
        {
            nodes = nodes.Where(n =>
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints) >=
                criteria.MinAvailableComputePoints.Value);
        }

        // Convert to advertisements
        var advertisements = nodes.Select(n => ToAdvertisement(n)).ToList();

        // Sort
        advertisements = criteria.SortBy?.ToLower() switch
        {
            "price" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.BasePrice).ToList()
                : advertisements.OrderBy(a => a.BasePrice).ToList(),
            "uptime" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.UptimePercentage).ToList()
                : advertisements.OrderBy(a => a.UptimePercentage).ToList(),
            "capacity" => criteria.SortDescending
                ? advertisements.OrderByDescending(a => a.AvailableComputePoints).ToList()
                : advertisements.OrderBy(a => a.AvailableComputePoints).ToList(),
            _ => advertisements.OrderByDescending(a => a.UptimePercentage).ToList() // Default sort by uptime
        };

        return await Task.FromResult(advertisements);
    }

    public async Task<List<NodeAdvertisement>> GetFeaturedNodesAsync()
    {
        // Featured nodes criteria:
        // 1. High uptime (>95%)
        // 2. Good capacity
        // 3. Online
        // 4. Has description (curated)

        var featuredNodes = _dataStore.ActiveNodes.Values
            .Where(n =>
                n.Status == NodeStatus.Online &&
                n.UptimePercentage >= 95.0 &&
                !string.IsNullOrEmpty(n.Description) &&
                (n.TotalResources.ComputePoints - n.ReservedResources.ComputePoints) > 10)
            .OrderByDescending(n => n.UptimePercentage)
            .ThenByDescending(n => n.TotalVmsHosted)
            .Take(10)
            .Select(n => ToAdvertisement(n))
            .ToList();

        return await Task.FromResult(featuredNodes);
    }

    public async Task<NodeAdvertisement?> GetNodeAdvertisementAsync(string nodeId)
    {
        var node = await _dataStore.GetNodeAsync(nodeId);
        if (node == null)
            return null;

        return ToAdvertisement(node);
    }

    /// <summary>
    /// Convert Node to NodeAdvertisement (DTO for marketplace/public browsing)
    /// </summary>
    private NodeAdvertisement ToAdvertisement(Node node)
    {
        return new NodeAdvertisement
        {
            NodeId = node.Id,
            OperatorName = node.Name,
            Description = node.Description,
            Region = node.Region,
            Zone = node.Zone,
            Tags = node.Tags,

            Capabilities = new NodeCapabilities
            {
                HasGpu = node.HardwareInventory.SupportsGpu,
                GpuModel = node.HardwareInventory.Gpus.FirstOrDefault()?.Model,
                GpuCount = node.HardwareInventory.Gpus.Count > 0 ? node.HardwareInventory.Gpus.Count : null,
                GpuMemoryBytes = node.HardwareInventory.Gpus.FirstOrDefault()?.MemoryBytes,
                HasNvmeStorage = node.HardwareInventory.Storage
                    .Any(s => s.Type == StorageType.NVMe),
                HighBandwidth = (node.HardwareInventory.Network.BandwidthBitsPerSecond ?? 0)
                    > 1_000_000_000, // > 1 Gbps
                CpuModel = node.HardwareInventory.Cpu.Model,
                CpuCores = node.HardwareInventory.Cpu.PhysicalCores,
                TotalMemoryBytes = node.HardwareInventory.Memory.TotalBytes,
                TotalStorageBytes = node.HardwareInventory.Storage.Sum(s => s.TotalBytes)
            },

            UptimePercentage = node.UptimePercentage,
            TotalVmsHosted = node.TotalVmsHosted,
            SuccessfulVmCompletions = node.SuccessfulVmCompletions,
            RegisteredAt = node.RegisteredAt,

            BasePrice = node.BasePrice,

            IsOnline = node.Status == NodeStatus.Online,
            AvailableComputePoints = node.TotalResources.ComputePoints - node.ReservedResources.ComputePoints,
            AvailableMemoryBytes = node.TotalResources.MemoryBytes - node.ReservedResources.MemoryBytes,
            AvailableStorageBytes = node.TotalResources.StorageBytes - node.ReservedResources.StorageBytes
        };
    }
}