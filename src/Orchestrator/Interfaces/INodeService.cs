using DeCloud.Shared.Contracts;
using DeCloud.Shared.Models;
using Orchestrator.Models;

namespace Orchestrator.Interfaces;

public interface INodeService
{
    /// <summary>
    /// Process a resource allocation request. Validates percentages,
    /// merges with existing allocation, persists. Concrete capacity
    /// is computed at login time.
    /// </summary>
    Task<NodeAllocateResponse> AllocateNodeAsync(string nodeId, NodeAllocateRequest request, CancellationToken ct = default);

    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request, CancellationToken ct = default);

    Task<(Dictionary<string, ObligationStatePayload>, Dictionary<string, SystemVmTemplatePayload>)>
    GenerateObligationPayloadsAsync(Node node, CancellationToken ct = default);

    /// <summary>
    /// Deregister a node. Refuses with tenant VMs unless force=true.
    /// Revokes JWT on success.
    /// </summary>
    Task<NodeDeregisterResponse> DeregisterNodeAsync(
        string nodeId, bool force, string reason,
        CancellationToken ct = default);

    /// <summary>
    /// Hard cutoff for a compliance-suspended node once it has been drained:
    /// terminalize any leftover tenant VMs (ephemeral → Lost, unconfirmed → Unrecoverable),
    /// then revoke the node's JWT and delete its record. Defers (no-op) if any leftover
    /// replicated VM still has a confirmed replica per its manifest — that VM is
    /// recoverable and must drain first.
    /// </summary>
    Task CutoffSuspendedNodeAsync(string nodeId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Immediate-cutoff override for a suspended node: skip the graceful drain wait and
    /// force every running VM into the offline-DR path now (ephemeral → Lost, unconfirmed
    /// → Unrecoverable, confirmed → migrating from the DHT replica). The node stays
    /// Suspended; the scan migrates the recoverable VMs and slice 3 deregisters it once
    /// they're off. Refuses if the node is not Suspended.
    /// </summary>
    Task CutoffSuspendedNodeNowAsync(string nodeId, string reason, CancellationToken ct = default);

    /// <summary>
    /// Set scheduling-ready flag. Lightweight, JWT-authenticated.
    /// </summary>
    Task<NodeLoginResponse> LoginNodeAsync(string nodeId, CancellationToken ct = default);

    /// <summary>
    /// Clear scheduling-ready flag. Node continues heartbeating.
    /// </summary>
    Task<NodeLogoutResponse> LogoutNodeAsync(string nodeId, CancellationToken ct = default);

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
