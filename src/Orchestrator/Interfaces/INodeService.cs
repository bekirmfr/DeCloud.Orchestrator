using Orchestrator.Models;

namespace Orchestrator.Interfaces;

public interface INodeService
{
    Task<NodeRegistrationResponse> RegisterNodeAsync(NodeRegistrationRequest request, CancellationToken ct = default);

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
