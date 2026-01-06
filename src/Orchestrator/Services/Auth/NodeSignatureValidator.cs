using Nethereum.Signer;
using Nethereum.Util;
using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Services.Auth;

/// <summary>
/// Service interface for node signature validation
/// </summary>
public interface INodeSignatureValidator
{
    /// <summary>
    /// Validate a node's wallet signature for authentication
    /// </summary>
    Task<bool> ValidateNodeSignatureAsync(
        string nodeId,
        string? signature,
        long? timestamp,
        string requestPath);

    /// <summary>
    /// Get the maximum age for timestamps (prevents replay attacks)
    /// </summary>
    TimeSpan GetMaxTimestampAge();
}

/// <summary>
/// Validates node authentication using Web3 wallet signatures.
/// Provides stateless authentication - no token storage required!
/// </summary>
public class NodeSignatureValidator : INodeSignatureValidator
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NodeSignatureValidator> _logger;
    private readonly IWebHostEnvironment _environment;

    // Security configuration
    private const int MaxTimestampAgeSeconds = 300; // 5 minutes

    public NodeSignatureValidator(
        DataStore dataStore,
        ILogger<NodeSignatureValidator> logger,
        IWebHostEnvironment environment)
    {
        _dataStore = dataStore;
        _logger = logger;
        _environment = environment;
    }

    public TimeSpan GetMaxTimestampAge() => TimeSpan.FromSeconds(MaxTimestampAgeSeconds);

    /// <summary>
    /// Validate node signature for authentication.
    /// This is stateless - no token storage needed!
    /// </summary>
    public async Task<bool> ValidateNodeSignatureAsync(
        string nodeId,
        string? signature,
        long? timestamp,
        string requestPath)
    {
        try
        {
            // =====================================================
            // STEP 1: Validate Input Parameters
            // =====================================================
            if (string.IsNullOrEmpty(signature) || !timestamp.HasValue)
            {
                _logger.LogWarning("Node {NodeId}: Missing signature or timestamp", nodeId);
                return false;
            }

            // =====================================================
            // STEP 2: Validate Timestamp (Prevent Replay Attacks)
            // =====================================================
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var timeDiff = Math.Abs(now - timestamp.Value);

            if (timeDiff > MaxTimestampAgeSeconds)
            {
                _logger.LogWarning(
                    "Node {NodeId}: Timestamp too old/new. Diff: {Diff}s (max: {Max}s)",
                    nodeId, timeDiff, MaxTimestampAgeSeconds);
                return false;
            }

            // =====================================================
            // STEP 3: Get Node's Wallet Address
            // =====================================================
            if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
            {
                _logger.LogWarning("Node {NodeId}: Not found in registry", nodeId);
                return false;
            }

            var nodeWallet = node.WalletAddress;

            if (string.IsNullOrEmpty(nodeWallet) ||
                nodeWallet == "0x0000000000000000000000000000000000000000")
            {
                _logger.LogError("Node {NodeId}: Invalid wallet address", nodeId);
                return false;
            }

            // =====================================================
            // STEP 4: Reconstruct the Signed Message
            // =====================================================
            // Message format: {nodeId}:{timestamp}:{requestPath}
            var message = $"{nodeId}:{timestamp}:{requestPath}";

            // =====================================================
            // STEP 5: Allow Mock Signatures in Development
            // =====================================================
            if (_environment.IsDevelopment() && IsMockSignature(signature))
            {
                _logger.LogWarning(
                    "DEV MODE: Accepting mock signature for node {NodeId}",
                    nodeId);
                return true;
            }

            // =====================================================
            // STEP 6: Verify Signature Using Nethereum
            // =====================================================
            var recoveredAddress = RecoverAddressFromSignature(message, signature);

            if (string.IsNullOrEmpty(recoveredAddress))
            {
                _logger.LogWarning(
                    "Node {NodeId}: Failed to recover address from signature",
                    nodeId);
                return false;
            }

            // =====================================================
            // STEP 7: Compare Addresses (Case-Insensitive)
            // =====================================================
            var normalizedNode = nodeWallet.ToLowerInvariant();
            var normalizedRecovered = recoveredAddress.ToLowerInvariant();

            if (normalizedNode != normalizedRecovered)
            {
                _logger.LogWarning(
                    "Node {NodeId}: Address mismatch. Expected: {Expected}, Recovered: {Recovered}",
                    nodeId, nodeWallet, recoveredAddress);
                return false;
            }

            // =====================================================
            // SUCCESS! Signature Valid
            // =====================================================
            _logger.LogDebug(
                "✓ Node {NodeId} authenticated via wallet signature (wallet: {Wallet})",
                nodeId, nodeWallet.Substring(0, 10) + "...");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error validating signature for node {NodeId}",
                nodeId);
            return false;
        }
    }

    /// <summary>
    /// Check if signature is a mock/test signature (dev mode only)
    /// </summary>
    private bool IsMockSignature(string signature)
    {
        if (string.IsNullOrEmpty(signature))
            return false;

        var mockSignatures = new[]
        {
            "0xmocksig",
            "0xmocksignature",
            "0xtest",
            "mock"
        };

        return mockSignatures.Any(m =>
            signature.StartsWith(m, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Recover Ethereum address from signed message using Nethereum.
    /// Uses EIP-191 signature verification.
    /// </summary>
    private string? RecoverAddressFromSignature(string message, string signature)
    {
        try
        {
            // Nethereum's EthereumMessageSigner handles EIP-191 prefix automatically
            var signer = new EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            // Normalize to checksum address
            return new AddressUtil().ConvertToChecksumAddress(recoveredAddress);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover address from signature");
            return null;
        }
    }
}