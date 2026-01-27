using System.Net.WebSockets;
using Orchestrator.Persistence;
using Orchestrator.Models;

namespace Orchestrator.Middleware;

/// <summary>
/// WebSocket proxy middleware that routes terminal and SFTP connections through the orchestrator.
/// This solves the TLS issue where browsers can't connect directly to nodes without TLS.
/// 
/// CGNAT Support:
/// - Detects nodes behind NAT/CGNAT
/// - Routes connections through relay node's WireGuard tunnel
/// - Uses tunnel IP (10.20.x.x) instead of public IP for CGNAT nodes
/// 
/// Endpoints:
/// - /api/terminal-proxy/{vmId} → ws://node:5100/api/vms/{vmId}/terminal
/// - /api/sftp-proxy/{vmId}     → ws://node:5100/api/vms/{vmId}/sftp
/// </summary>
public class WebSocketProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebSocketProxyMiddleware> _logger;

    public WebSocketProxyMiddleware(RequestDelegate next, ILogger<WebSocketProxyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, DataStore dataStore)
    {
        var path = context.Request.Path.Value ?? "";

        // Determine proxy type and extract vmId
        string? vmId = null;
        string nodeEndpoint;

        if (path.StartsWith("/api/terminal-proxy/"))
        {
            vmId = path.Substring("/api/terminal-proxy/".Length).Split('?')[0];
            nodeEndpoint = "terminal";
        }
        else if (path.StartsWith("/api/sftp-proxy/"))
        {
            vmId = path.Substring("/api/sftp-proxy/".Length).Split('?')[0];
            nodeEndpoint = "sftp";
        }
        else
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrEmpty(vmId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("VM ID required");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket connection required");
            return;
        }

        _logger.LogInformation("{Endpoint} proxy request for VM {VmId}", nodeEndpoint, vmId);

        // Look up VM to get node info
        var vm = await dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("VM not found");
            return;
        }

        if (vm.Status != VmStatus.Running)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync($"VM is not running (status: {vm.Status})");
            return;
        }

        var node = await dataStore.GetNodeAsync(vm.NodeId);
        if (string.IsNullOrEmpty(vm.NodeId) || node == null)
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("Node not available");
            return;
        }

        var vmIp = vm.NetworkConfig?.PrivateIp;
        if (string.IsNullOrEmpty(vmIp))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("VM IP not available");
            return;
        }

        // ========================================
        // CGNAT RELAY ROUTING LOGIC
        // ========================================
        var nodePort = node.AgentPort > 0 ? node.AgentPort : 5100;
        string nodeHost;
        string routingInfo;

        if (node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
        {
            // Node is behind CGNAT - route through WireGuard tunnel
            nodeHost = node.CgnatInfo.TunnelIp;
            routingInfo = $"CGNAT node via relay tunnel (Relay: {node.CgnatInfo.AssignedRelayNodeId})";

            _logger.LogInformation(
                "Routing {Endpoint} for VM {VmId} through CGNAT relay: " +
                "Node {NodeId} → Tunnel IP {TunnelIp}",
                nodeEndpoint, vmId, node.Id, nodeHost);
        }
        else
        {
            // Regular node with public IP - direct connection
            nodeHost = node.PublicIp;
            routingInfo = "Direct connection to public IP";

            _logger.LogInformation(
                "Routing {Endpoint} for VM {VmId} directly: " +
                "Node {NodeId} → Public IP {PublicIp}",
                nodeEndpoint, vmId, node.Id, nodeHost);
        }

        // Build upstream WebSocket URL
        var queryParams = context.Request.Query
            .ToDictionary(q => q.Key, q => q.Value.ToString());

        if (!queryParams.ContainsKey("ip"))
        {
            queryParams["ip"] = vmIp;
        }

        var queryString = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var upstreamUrl = $"ws://{nodeHost}:{nodePort}/api/vms/{vmId}/{nodeEndpoint}?{queryString}";

        _logger.LogInformation(
            "Proxying {Endpoint}: {VmId} → {Upstream} ({RoutingInfo})",
            nodeEndpoint, vmId, upstreamUrl.Split('?')[0] + "?...", routingInfo);

        // Proxy the WebSocket connection
        await ProxyWebSocketAsync(context, upstreamUrl, vmId);
    }

    private async Task ProxyWebSocketAsync(HttpContext context, string upstreamUrl, string vmId)
    {
        try
        {
            // Accept client WebSocket connection
            using var clientWebSocket = await context.WebSockets.AcceptWebSocketAsync();

            // Connect to upstream (node agent)
            using var upstreamWebSocket = new ClientWebSocket();

            // Configure connection with longer timeout for CGNAT routes
            upstreamWebSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted,
                new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token);

            try
            {
                await upstreamWebSocket.ConnectAsync(new Uri(upstreamUrl), connectionTimeout.Token);

                _logger.LogInformation(
                    "WebSocket connected for VM {VmId}, starting bidirectional relay",
                    vmId);

                // Bidirectional relay
                var clientToUpstream = RelayAsync(
                    clientWebSocket,
                    upstreamWebSocket,
                    "client→node",
                    vmId,
                    context.RequestAborted);

                var upstreamToClient = RelayAsync(
                    upstreamWebSocket,
                    clientWebSocket,
                    "node→client",
                    vmId,
                    context.RequestAborted);

                // Wait for either direction to close
                await Task.WhenAny(clientToUpstream, upstreamToClient);

                // Close both connections gracefully
                await CloseWebSocketAsync(clientWebSocket, vmId, "client");
                await CloseWebSocketAsync(upstreamWebSocket, vmId, "upstream");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "WebSocket connection timeout for VM {VmId}. " +
                    "This may indicate CGNAT relay routing issues or node agent unavailability.",
                    vmId);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error proxying terminal for VM {VmId}",
                vmId);
            throw;
        }
    }

    private async Task RelayAsync(
        WebSocket source,
        WebSocket destination,
        string direction,
        string vmId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1024 * 4];

        try
        {
            while (source.State == WebSocketState.Open &&
                   destination.State == WebSocketState.Open &&
                   !cancellationToken.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(
                    new ArraySegment<byte>(buffer),
                    cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug(
                        "WebSocket close received from {Direction} for VM {VmId}",
                        direction, vmId);
                    break;
                }

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "WebSocket relay cancelled ({Direction}) for VM {VmId}",
                direction, vmId);
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug(
                "WebSocket closed prematurely ({Direction}) for VM {VmId}",
                direction, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Error in WebSocket relay ({Direction}) for VM {VmId}",
                direction, vmId);
            throw;
        }
    }

    private async Task CloseWebSocketAsync(WebSocket webSocket, string vmId, string socketType)
    {
        try
        {
            if (webSocket.State == WebSocketState.Open)
            {
                await webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Connection closed",
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Error closing {SocketType} WebSocket for VM {VmId} (state: {State})",
                socketType, vmId, webSocket.State);
        }
    }
}

/// <summary>
/// Extension methods for registering the WebSocket proxy middleware
/// </summary>
public static class WebSocketProxyExtensions
{
    public static IApplicationBuilder UseWebSocketProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<WebSocketProxyMiddleware>();
    }
}