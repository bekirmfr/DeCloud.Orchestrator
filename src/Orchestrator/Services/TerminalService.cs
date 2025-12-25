using System.Collections.Concurrent;
using System.Text.Json;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing terminal sessions with ephemeral key injection.
/// Coordinates between the dashboard, orchestrator, and node agent.
/// </summary>
public interface ITerminalService
{
    /// <summary>
    /// Setup terminal access for a VM by injecting an ephemeral SSH key.
    /// Returns credentials needed to connect via WebSocket.
    /// </summary>
    Task<TerminalAccessResult> SetupTerminalAccessAsync(
        string vmId,
        string userId,
        int ttlSeconds = 300);

    /// <summary>
    /// Get active terminal sessions for a user
    /// </summary>
    Task<List<TerminalSessionInfo>> GetActiveSessionsAsync(string userId);

    /// <summary>
    /// End a terminal session and cleanup
    /// </summary>
    Task<bool> EndSessionAsync(string sessionId);
}

public class TerminalService : ITerminalService
{
    private readonly DataStore _dataStore;
    private readonly IVmService _vmService;
    private readonly INodeService _nodeService;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TerminalService> _logger;

    // Track active sessions
    private static readonly ConcurrentDictionary<string, TerminalSessionInfo> ActiveSessions = new();

    public TerminalService(
        DataStore dataStore,
        IVmService vmService,
        INodeService nodeService,
        HttpClient httpClient,
        ILogger<TerminalService> logger)
    {
        _dataStore = dataStore;
        _vmService = vmService;
        _nodeService = nodeService;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<TerminalAccessResult> SetupTerminalAccessAsync(
        string vmId,
        string userId,
        int ttlSeconds = 300)
    {
        _logger.LogInformation(
            "Setting up terminal access for VM {VmId} by user {UserId}",
            vmId, userId);

        // Validate VM
        var vm = await _vmService.GetVmAsync(vmId);
        if (vm == null)
        {
            return TerminalAccessResult.Fail("VM not found");
        }

        // Check ownership
        if (vm.OwnerId != userId)
        {
            _logger.LogWarning(
                "User {UserId} attempted to access terminal for VM {VmId} owned by {OwnerId}",
                userId, vmId, vm.OwnerId);
            return TerminalAccessResult.Fail("Access denied");
        }

        // Check VM is running
        if (vm.Status != VmStatus.Running)
        {
            return TerminalAccessResult.Fail($"VM is not running (status: {vm.Status})");
        }

        // Get the node
        if (string.IsNullOrEmpty(vm.NodeId) ||
            !_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return TerminalAccessResult.Fail("Node not available");
        }

        // Get VM IP
        var vmIp = vm.NetworkConfig?.PrivateIp;
        if (string.IsNullOrEmpty(vmIp))
        {
            return TerminalAccessResult.Fail("VM IP not available");
        }

        try
        {
            // Call Node Agent to setup terminal access with ephemeral key
            var nodeUrl = GetNodeApiUrl(node);
            var setupUrl = $"{nodeUrl}/api/vms/{vmId}/terminal/connect";

            var setupRequest = new
            {
                Username = "root",
                TtlSeconds = ttlSeconds,
                VmIp = vmIp,
                Port = vm.AccessInfo?.SshPort ?? 22,
                Password = vm.Spec?.Password
            };

            var response = await _httpClient.PostAsJsonAsync(setupUrl, setupRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError(
                    "Node Agent terminal setup failed for VM {VmId}: {Status} - {Content}",
                    vmId, response.StatusCode, content);
                return TerminalAccessResult.Fail($"Node setup failed: {response.StatusCode}");
            }

            var setupResult = JsonSerializer.Deserialize<NodeTerminalSetupResponse>(content,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (setupResult == null || !setupResult.Success)
            {
                return TerminalAccessResult.Fail(setupResult?.Error ?? "Unknown error from node");
            }

            // Create session record
            var sessionId = Guid.NewGuid().ToString();
            var session = new TerminalSessionInfo
            {
                SessionId = sessionId,
                VmId = vmId,
                VmName = vm.Name,
                UserId = userId,
                NodeId = vm.NodeId,
                VmIp = vmIp,
                Username = "root",
                Fingerprint = setupResult.Fingerprint,
                StartedAt = DateTime.UtcNow,
                ExpiresAt = setupResult.ExpiresAt ?? DateTime.UtcNow.AddSeconds(ttlSeconds)
            };

            ActiveSessions[sessionId] = session;

            _logger.LogInformation(
                "Terminal session {SessionId} created for VM {VmId}, expires at {ExpiresAt}",
                sessionId, vmId, session.ExpiresAt);

            // Build the WebSocket URL for the client
            var wsUrl = BuildWebSocketUrl(node, vmId, vmIp);
            var nodePort = node.AgentPort > 0 ? node.AgentPort : 5100;

            return TerminalAccessResult.Ok(new TerminalCredentials
            {
                SessionId = sessionId,
                WebSocketUrl = wsUrl,
                NodeIp = node.PublicIp,
                NodePort = nodePort,
                VmIp = vmIp,
                Username = "root",
                PrivateKey = setupResult.PrivateKey,
                PrivateKeyBase64 = setupResult.PrivateKeyBase64,
                Fingerprint = setupResult.Fingerprint,
                Password = setupResult.Password,
                ExpiresAt = session.ExpiresAt
            });
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to node agent for VM {VmId}", vmId);
            return TerminalAccessResult.Fail($"Cannot reach node: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Terminal setup failed for VM {VmId}", vmId);
            return TerminalAccessResult.Fail($"Setup error: {ex.Message}");
        }
    }

    public Task<List<TerminalSessionInfo>> GetActiveSessionsAsync(string userId)
    {
        var sessions = ActiveSessions.Values
            .Where(s => s.UserId == userId && s.ExpiresAt > DateTime.UtcNow)
            .ToList();

        // Cleanup expired sessions
        var expired = ActiveSessions
            .Where(kvp => kvp.Value.ExpiresAt <= DateTime.UtcNow)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expired)
        {
            ActiveSessions.TryRemove(key, out _);
        }

        return Task.FromResult(sessions);
    }

    public async Task<bool> EndSessionAsync(string sessionId)
    {
        if (!ActiveSessions.TryRemove(sessionId, out var session))
        {
            return false;
        }

        _logger.LogInformation("Ending terminal session {SessionId} for VM {VmId}", sessionId, session.VmId);

        // Try to remove the ephemeral key from the VM
        try
        {
            if (!_dataStore.Nodes.TryGetValue(session.NodeId, out var node))
            {
                return true; // Session removed, but can't cleanup key
            }

            var nodeUrl = GetNodeApiUrl(node);
            var removeUrl = $"{nodeUrl}/api/vms/{session.VmId}/terminal/keys/{Uri.EscapeDataString(session.Fingerprint)}?username={session.Username}";

            await _httpClient.DeleteAsync(removeUrl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup ephemeral key for session {SessionId}", sessionId);
        }

        return true;
    }

    private string GetNodeApiUrl(Node node)
    {
        var port = node.AgentPort > 0 ? node.AgentPort : 5100;
        var ip = node.PublicIp;
        return $"http://{ip}:{port}";
    }

    private string BuildWebSocketUrl(Node node, string vmId, string vmIp)
    {
        var port = node.AgentPort > 0 ? node.AgentPort : 5100;
        var ip = node.PublicIp;

        // Note: We don't include the private key in the URL for security.
        // The client should add it via header or the private key should be passed separately.
        return $"ws://{ip}:{port}/api/vms/{vmId}/terminal?ip={vmIp}&user=root";
    }
}

#region DTOs

public class TerminalAccessResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TerminalCredentials? Credentials { get; init; }

    public static TerminalAccessResult Ok(TerminalCredentials credentials)
        => new() { Success = true, Credentials = credentials };

    public static TerminalAccessResult Fail(string error)
        => new() { Success = false, Error = error };
}

public class TerminalCredentials
{
    public string SessionId { get; init; } = "";
    public string WebSocketUrl { get; init; } = "";
    public string NodeIp { get; init; } = "";
    public int NodePort { get; init; }
    public string VmIp { get; init; } = "";
    public string Username { get; init; } = "root";
    public string PrivateKey { get; init; } = "";
    public string PrivateKeyBase64 { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public string? Password { get; init; }
    public DateTime ExpiresAt { get; init; }
}

public class TerminalSessionInfo
{
    public string SessionId { get; init; } = "";
    public string VmId { get; init; } = "";
    public string VmName { get; init; } = "";
    public string UserId { get; init; } = "";
    public string NodeId { get; init; } = "";
    public string VmIp { get; init; } = "";
    public string Username { get; init; } = "";
    public string Fingerprint { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}

// Response from Node Agent
internal class NodeTerminalSetupResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string PrivateKey { get; set; } = "";
    public string PrivateKeyBase64 { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Username { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }
    public string? MethodUsed { get; set; }
    public string? Password { get; set; }
}

#endregion