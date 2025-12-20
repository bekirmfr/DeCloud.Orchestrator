using System.Net.WebSockets;
using Orchestrator.Data;
using Orchestrator.Models;

namespace Orchestrator.Middleware;

/// <summary>
/// WebSocket proxy middleware that routes terminal and SFTP connections through the orchestrator.
/// This solves the TLS issue where browsers can't connect directly to nodes without TLS.
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

        // Pass through query parameters and ensure ip is set
        var queryParams = context.Request.Query
            .ToDictionary(q => q.Key, q => q.Value.ToString());

        if (!queryParams.ContainsKey("ip"))
        {
            queryParams["ip"] = vmIp;
        }

        var queryString = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        var upstreamUrl = $"ws://{nodeHost}:{nodePort}/api/vms/{vmId}/{nodeEndpoint}?{queryString}";

        _logger.LogInformation("Proxying {Endpoint}: {VmId} → {Upstream}",
            nodeEndpoint, vmId, upstreamUrl.Split('?')[0] + "?...");

        try
        {
            // Accept incoming WebSocket
            using var clientWs = await context.WebSockets.AcceptWebSocketAsync();

            // Connect to node
            using var nodeWs = new ClientWebSocket();
            nodeWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await nodeWs.ConnectAsync(new Uri(upstreamUrl), cts.Token);

            _logger.LogInformation("{Endpoint} proxy connected for VM {VmId}", nodeEndpoint, vmId);

            // Bidirectional proxy
            var clientToNode = ProxyOneWay(clientWs, nodeWs, "client→node", vmId, nodeEndpoint);
            var nodeToClient = ProxyOneWay(nodeWs, clientWs, "node→client", vmId, nodeEndpoint);

            await Task.WhenAny(clientToNode, nodeToClient);

            // Close both connections gracefully
            await CloseWebSocket(clientWs, "Proxy closing");
            await CloseWebSocket(nodeWs, "Proxy closing");

            _logger.LogInformation("{Endpoint} proxy closed for VM {VmId}", nodeEndpoint, vmId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket error proxying {Endpoint} for VM {VmId}", nodeEndpoint, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying {Endpoint} for VM {VmId}", nodeEndpoint, vmId);
        }
    }

    private async Task ProxyOneWay(WebSocket source, WebSocket dest, string direction, string vmId, string endpoint)
    {
        var buffer = new byte[64 * 1024]; // 64KB buffer for file transfers

        try
        {
            while (source.State == WebSocketState.Open && dest.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogDebug("{Endpoint} proxy {Direction} close received for {VmId}",
                        endpoint, direction, vmId);
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
            _logger.LogDebug("{Endpoint} proxy {Direction} connection closed for {VmId}",
                endpoint, direction, vmId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Endpoint} proxy {Direction} error for {VmId}",
                endpoint, direction, vmId);
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
/// Extension methods for registering the WebSocket proxy middleware
/// </summary>
public static class WebSocketProxyExtensions
{
    public static IApplicationBuilder UseWebSocketProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<WebSocketProxyMiddleware>();
    }
}