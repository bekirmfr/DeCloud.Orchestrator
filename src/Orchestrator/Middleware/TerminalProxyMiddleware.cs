using System.Net.WebSockets;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Middleware;

/// <summary>
/// WebSocket proxy middleware that routes terminal connections through the orchestrator.
/// This solves the TLS issue where browsers can't connect directly to nodes without TLS.
/// 
/// Flow:
/// Browser → wss://orchestrator/api/terminal-proxy/{vmId}?password=... 
///         → ws://node:5100/api/vms/{vmId}/terminal?ip={vmIp}&password=...
/// </summary>
public class TerminalProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TerminalProxyMiddleware> _logger;

    public TerminalProxyMiddleware(RequestDelegate next, ILogger<TerminalProxyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, DataStore dataStore)
    {
        // Only handle /api/terminal-proxy/{vmId} WebSocket requests
        if (!context.Request.Path.StartsWithSegments("/api/terminal-proxy", out var remaining))
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("WebSocket connection required");
            return;
        }

        // Extract vmId from path
        var vmId = remaining.Value?.TrimStart('/');
        if (string.IsNullOrEmpty(vmId))
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("VM ID required");
            return;
        }

        _logger.LogInformation("Terminal proxy request for VM {VmId}", vmId);

        // Look up VM to get node info
        if (!dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
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

        if (string.IsNullOrEmpty(vm.NodeId) || !dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
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

        // Build upstream WebSocket URL
        var nodePort = node.AgentPort > 0 ? node.AgentPort : 5100;
        var nodeHost = node.PublicIp;

        // Pass through query parameters
        var query = context.Request.QueryString.Value ?? "";

        // Ensure ip parameter is set
        if (!query.Contains("ip="))
        {
            query = string.IsNullOrEmpty(query)
                ? $"?ip={vmIp}"
                : $"{query}&ip={vmIp}";
        }

        var upstreamUrl = $"ws://{nodeHost}:{nodePort}/api/vms/{vmId}/terminal{query}";

        _logger.LogInformation("Proxying terminal: {VmId} → {Upstream}", vmId, upstreamUrl);

        try
        {
            // Accept incoming WebSocket
            using var clientWs = await context.WebSockets.AcceptWebSocketAsync();

            // Connect to node
            using var nodeWs = new ClientWebSocket();
            nodeWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await nodeWs.ConnectAsync(new Uri(upstreamUrl), cts.Token);

            _logger.LogInformation("Terminal proxy connected for VM {VmId}", vmId);

            // Bidirectional proxy
            var clientToNode = ProxyOneWay(clientWs, nodeWs, "client→node", vmId);
            var nodeToClient = ProxyOneWay(nodeWs, clientWs, "node→client", vmId);

            await Task.WhenAny(clientToNode, nodeToClient);

            // Close both connections gracefully
            await CloseWebSocket(clientWs, "Proxy closing");
            await CloseWebSocket(nodeWs, "Proxy closing");

            _logger.LogInformation("Terminal proxy closed for VM {VmId}", vmId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket error proxying terminal for VM {VmId}", vmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying terminal for VM {VmId}", vmId);
        }
    }

    private async Task ProxyOneWay(WebSocket source, WebSocket dest, string direction, string vmId)
    {
        var buffer = new byte[4096];

        try
        {
            while (source.State == WebSocketState.Open && dest.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("Terminal proxy {Direction} close received for {VmId}", direction, vmId);
                    break;
                }

                await dest.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    CancellationToken.None);
            }
        }
        catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            _logger.LogDebug("Terminal proxy {Direction} connection closed for {VmId}", direction, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Terminal proxy {Direction} error for {VmId}", direction, vmId);
        }
    }

    private async Task CloseWebSocket(WebSocket ws, string reason)
    {
        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, CancellationToken.None);
            }
        }
        catch
        {
            // Ignore close errors
        }
    }
}

/// <summary>
/// Extension methods for registering the terminal proxy middleware
/// </summary>
public static class TerminalProxyExtensions
{
    public static IApplicationBuilder UseTerminalProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TerminalProxyMiddleware>();
    }
}