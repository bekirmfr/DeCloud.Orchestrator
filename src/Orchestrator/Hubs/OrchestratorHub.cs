using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Orchestrator.Persistence;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Hubs;

/// <summary>
/// SignalR Hub for real-time communication
/// - Event notifications (VM status changes, node updates)
/// - Terminal/Console proxy
/// </summary>
public class OrchestratorHub : Hub
{
    private readonly DataStore _dataStore;
    private readonly IVmService _vmService;
    private readonly INodeService _nodeService;
    private readonly ILogger<OrchestratorHub> _logger;

    // Track terminal sessions: connectionId -> (vmId, nodeConnectionId)
    private static readonly ConcurrentDictionary<string, TerminalSession> ActiveTerminals = new();

    public OrchestratorHub(
        DataStore dataStore,
        IVmService vmService,
        INodeService nodeService,
        ILogger<OrchestratorHub> logger)
    {
        _dataStore = dataStore;
        _vmService = vmService;
        _nodeService = nodeService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // Clean up any terminal sessions
        if (ActiveTerminals.TryRemove(Context.ConnectionId, out var session))
        {
            // Notify node to close terminal
            await Clients.Group($"node:{session.NodeId}").SendAsync("TerminalClose", new
            {
                SessionId = session.SessionId,
                VmId = session.VmId
            });
        }

        _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    #region Group Subscriptions

    /// <summary>
    /// Subscribe to events for a specific VM
    /// </summary>
    public async Task SubscribeToVm(string vmId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"vm:{vmId}");
        _logger.LogDebug("Client {ConnectionId} subscribed to VM {VmId}", Context.ConnectionId, vmId);
    }

    /// <summary>
    /// Unsubscribe from VM events
    /// </summary>
    public async Task UnsubscribeFromVm(string vmId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"vm:{vmId}");
    }

    /// <summary>
    /// Subscribe to events for a specific node
    /// </summary>
    public async Task SubscribeToNode(string nodeId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"node:{nodeId}");
        _logger.LogDebug("Client {ConnectionId} subscribed to node {NodeId}", Context.ConnectionId, nodeId);
    }

    /// <summary>
    /// Subscribe to user-specific events
    /// </summary>
    public async Task SubscribeToUser(string userId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"user:{userId}");
    }

    /// <summary>
    /// Node agents call this to register themselves
    /// </summary>
    public async Task RegisterAsNode(string nodeId, string authToken)
    {
        // Validate auth
        /*
        if (!await _nodeAuthService.ValidateTokenAsync(nodeId, authToken))
        {
            _logger.LogWarning("Invalid node auth token for {NodeId}", nodeId);
            await Clients.Caller.SendAsync("Error", new { Message = "Invalid authentication" });
            return;
        }
        */

        await Groups.AddToGroupAsync(Context.ConnectionId, $"node:{nodeId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, "nodes");
        
        _logger.LogInformation("Node {NodeId} registered via SignalR: {ConnectionId}", nodeId, Context.ConnectionId);
    }

    #endregion

    #region Terminal Proxy

    /// <summary>
    /// Client requests to open a terminal to a VM
    /// </summary>
    public async Task OpenTerminal(string vmId, string terminalType = "ssh")
    {
        var vm = await _vmService.GetVmAsync(vmId);
        if (vm == null || string.IsNullOrEmpty(vm.NodeId))
        {
            await Clients.Caller.SendAsync("TerminalError", new { Message = "VM not found or not running" });
            return;
        }

        if (vm.Status != VmStatus.Running)
        {
            await Clients.Caller.SendAsync("TerminalError", new { Message = "VM is not running" });
            return;
        }

        var sessionId = Guid.NewGuid().ToString();
        var session = new TerminalSession
        {
            SessionId = sessionId,
            VmId = vmId,
            NodeId = vm.NodeId,
            ClientConnectionId = Context.ConnectionId,
            TerminalType = terminalType,
            StartedAt = DateTime.UtcNow
        };

        ActiveTerminals[Context.ConnectionId] = session;

        // Request node to open terminal
        await Clients.Group($"node:{vm.NodeId}").SendAsync("TerminalOpen", new
        {
            SessionId = sessionId,
            VmId = vmId,
            ClientConnectionId = Context.ConnectionId,
            TerminalType = terminalType
        });

        await Clients.Caller.SendAsync("TerminalOpening", new
        {
            SessionId = sessionId,
            VmId = vmId
        });

        _logger.LogInformation("Terminal session {SessionId} opening for VM {VmId}", sessionId, vmId);
    }

    /// <summary>
    /// Client sends terminal input (keystrokes)
    /// </summary>
    public async Task TerminalInput(string sessionId, string data)
    {
        if (!ActiveTerminals.TryGetValue(Context.ConnectionId, out var session) || 
            session.SessionId != sessionId)
        {
            return;
        }

        // Forward to node
        await Clients.Group($"node:{session.NodeId}").SendAsync("TerminalInput", new
        {
            SessionId = sessionId,
            Data = data
        });
    }

    /// <summary>
    /// Client resizes terminal window
    /// </summary>
    public async Task TerminalResize(string sessionId, int cols, int rows)
    {
        if (!ActiveTerminals.TryGetValue(Context.ConnectionId, out var session) || 
            session.SessionId != sessionId)
        {
            return;
        }

        await Clients.Group($"node:{session.NodeId}").SendAsync("TerminalResize", new
        {
            SessionId = sessionId,
            Cols = cols,
            Rows = rows
        });
    }

    /// <summary>
    /// Client closes terminal
    /// </summary>
    public async Task CloseTerminal(string sessionId)
    {
        if (!ActiveTerminals.TryRemove(Context.ConnectionId, out var session) || 
            session.SessionId != sessionId)
        {
            return;
        }

        await Clients.Group($"node:{session.NodeId}").SendAsync("TerminalClose", new
        {
            SessionId = sessionId,
            VmId = session.VmId
        });

        _logger.LogInformation("Terminal session {SessionId} closed", sessionId);
    }

    /// <summary>
    /// Node sends terminal output back to client
    /// </summary>
    public async Task TerminalOutput(string sessionId, string clientConnectionId, string data)
    {
        await Clients.Client(clientConnectionId).SendAsync("TerminalOutput", new
        {
            SessionId = sessionId,
            Data = data
        });
    }

    /// <summary>
    /// Node confirms terminal is ready
    /// </summary>
    public async Task TerminalReady(string sessionId, string clientConnectionId)
    {
        await Clients.Client(clientConnectionId).SendAsync("TerminalReady", new
        {
            SessionId = sessionId
        });

        _logger.LogInformation("Terminal session {SessionId} ready", sessionId);
    }

    /// <summary>
    /// Node reports terminal error
    /// </summary>
    public async Task TerminalError(string sessionId, string clientConnectionId, string error)
    {
        await Clients.Client(clientConnectionId).SendAsync("TerminalError", new
        {
            SessionId = sessionId,
            Message = error
        });

        // Clean up session on the client side
        var clientSession = ActiveTerminals.Values.FirstOrDefault(s => s.SessionId == sessionId);
        if (clientSession != null)
        {
            ActiveTerminals.TryRemove(clientSession.ClientConnectionId, out _);
        }
    }

    #endregion

    #region VM Status Updates (from Node)

    /// <summary>
    /// Node reports VM status change
    /// </summary>
    public async Task ReportVmStatus(string vmId, string status, string? message = null)
    {
        if (Enum.TryParse<VmStatus>(status, true, out var vmStatus))
        {
            await _vmService.UpdateVmStatusAsync(vmId, vmStatus, message);
            
            // Broadcast to subscribers
            await Clients.Group($"vm:{vmId}").SendAsync("VmStatusChanged", new
            {
                VmId = vmId,
                Status = status,
                Message = message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Node reports VM metrics
    /// </summary>
    public async Task ReportVmMetrics(string vmId, VmMetrics metrics)
    {
        await _vmService.UpdateVmMetricsAsync(vmId, metrics);
        
        await Clients.Group($"vm:{vmId}").SendAsync("VmMetricsUpdated", new
        {
            VmId = vmId,
            Metrics = metrics
        });
    }

    /// <summary>
    /// Node reports VM access info (SSH/VNC endpoints)
    /// </summary>
    public async Task ReportVmAccessInfo(string vmId, VmAccessInfo accessInfo)
    {
        var vm = await _vmService.GetVmAsync(vmId);
        if (vm != null)
        {
            vm.AccessInfo = accessInfo;
            
            await Clients.Group($"vm:{vmId}").SendAsync("VmAccessInfoUpdated", new
            {
                VmId = vmId,
                AccessInfo = accessInfo
            });
        }
    }

    #endregion
}

public class TerminalSession
{
    public string SessionId { get; set; } = string.Empty;
    public string VmId { get; set; } = string.Empty;
    public string NodeId { get; set; } = string.Empty;
    public string ClientConnectionId { get; set; } = string.Empty;
    public string TerminalType { get; set; } = "ssh";
    public DateTime StartedAt { get; set; }
}
