using Orchestrator.Models;
using Orchestrator.Services;
using System.Net.Http.Headers;

namespace Orchestrator.Middleware;

/// <summary>
/// HTTP proxy middleware that routes *.vms.{baseDomain} requests to VMs.
/// Works with Caddy's X-DeCloud-Subdomain header to identify the target VM.
/// 
/// Flow:
/// 1. Caddy receives request for r2d2.vms.stackfi.tech
/// 2. Caddy adds X-DeCloud-Subdomain: r2d2 header and proxies to orchestrator
/// 3. This middleware looks up the route and proxies to NodeAgent
/// 4. NodeAgent proxies to the VM's private IP
/// </summary>
public class SubdomainProxyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<SubdomainProxyMiddleware> _logger;

    // Headers that should not be forwarded
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    public SubdomainProxyMiddleware(
        RequestDelegate next,
        IHttpClientFactory httpClientFactory,
        ILogger<SubdomainProxyMiddleware> logger)
    {
        _next = next;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, ICentralIngressService ingressService)
    {
        // Check for subdomain header from Caddy
        if (!context.Request.Headers.TryGetValue("X-DeCloud-Subdomain", out var subdomainHeader))
        {
            // No subdomain header - not a VM request
            await _next(context);
            return;
        }

        var subdomain = subdomainHeader.FirstOrDefault()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(subdomain))
        {
            await _next(context);
            return;
        }

        // Skip API routes, health checks, etc.
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/api/") ||
            path.StartsWith("/hub/") ||
            path.StartsWith("/swagger") ||
            path.StartsWith("/health"))
        {
            await _next(context);
            return;
        }

        // Look up the route
        var routes = await ingressService.GetAllRoutesAsync(context.RequestAborted);
        var route = routes.FirstOrDefault(r =>
            r.Subdomain.StartsWith(subdomain + ".", StringComparison.OrdinalIgnoreCase) ||
            r.Subdomain.Equals(subdomain, StringComparison.OrdinalIgnoreCase));

        if (route == null)
        {
            _logger.LogDebug("No route found for subdomain: {Subdomain}", subdomain);
            context.Response.StatusCode = 404;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "VM not found",
                message = $"No VM is associated with subdomain '{subdomain}'",
                hint = "The VM may be stopped or the subdomain may be incorrect"
            });
            return;
        }

        if (route.Status != CentralRouteStatus.Active)
        {
            _logger.LogDebug("Route for {Subdomain} is not active: {Status}", subdomain, route.Status);
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "VM not available",
                message = "The VM is currently not running",
                status = route.Status.ToString()
            });
            return;
        }

        // Build target URL: NodeAgent's internal proxy endpoint
        var nodePort = 5100; // NodeAgent port
        var targetPath = $"/internal/proxy/{route.VmId}{path}";
        var queryString = context.Request.QueryString.Value ?? "";
        var targetUrl = $"http://{route.NodePublicIp}:{nodePort}{targetPath}{queryString}";

        _logger.LogInformation(
            "Proxying {Method} {Subdomain}{Path} → {NodeIp} → {VmIp}:{Port}",
            context.Request.Method, subdomain, path,
            route.NodePublicIp, route.VmPrivateIp, route.TargetPort);

        // Handle WebSocket upgrades
        if (context.WebSockets.IsWebSocketRequest)
        {
            await ProxyWebSocket(context, route, path);
            return;
        }

        // Proxy HTTP request
        await ProxyHttp(context, targetUrl, route);
    }

    private async Task ProxyHttp(HttpContext context, string targetUrl, CentralIngressRoute route)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("SubdomainProxy");
            client.Timeout = TimeSpan.FromSeconds(60);

            // Build request
            var request = new HttpRequestMessage
            {
                Method = new HttpMethod(context.Request.Method),
                RequestUri = new Uri(targetUrl)
            };

            // Copy headers
            foreach (var header in context.Request.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                    continue;
                if (header.Key.StartsWith("X-DeCloud-", StringComparison.OrdinalIgnoreCase))
                    continue;

                request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }

            // Add forwarding headers
            var clientIp = context.Request.Headers["X-Real-IP"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString()
                ?? "unknown";

            request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
            request.Headers.TryAddWithoutValidation("X-Forwarded-Proto",
                context.Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https");
            request.Headers.TryAddWithoutValidation("X-Forwarded-Host",
                context.Request.Headers["X-Forwarded-Host"].FirstOrDefault()
                ?? context.Request.Host.Value);
            request.Headers.TryAddWithoutValidation("X-DeCloud-Target-Port", route.TargetPort.ToString());
            request.Headers.TryAddWithoutValidation("X-DeCloud-VM-Id", route.VmId);

            // Copy body
            if (context.Request.ContentLength > 0 ||
                context.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                request.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                {
                    request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
            }

            // Send request
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Failed to connect to node {NodeIp} for VM {VmId}",
                    route.NodePublicIp, route.VmId);
                context.Response.StatusCode = 502;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Bad Gateway",
                    message = "Failed to connect to the VM's host node",
                    details = "The node may be offline or the VM may not be accessible"
                });
                return;
            }

            // Copy response status
            context.Response.StatusCode = (int)response.StatusCode;

            // Copy response headers
            foreach (var header in response.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                    continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in response.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Stream response body
            await using var responseStream = await response.Content.ReadAsStreamAsync(context.RequestAborted);
            await responseStream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Proxy request cancelled for VM {VmId}", route.VmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying request to VM {VmId}", route.VmId);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = 500;
                await context.Response.WriteAsJsonAsync(new
                {
                    error = "Internal Server Error",
                    message = "An error occurred while proxying the request"
                });
            }
        }
    }

    private async Task ProxyWebSocket(HttpContext context, CentralIngressRoute route, string path)
    {
        try
        {
            var nodePort = 5100;
            var wsPath = $"/internal/proxy/{route.VmId}/ws{path}";
            var queryString = context.Request.QueryString.Value ?? "";
            var targetUrl = $"ws://{route.NodePublicIp}:{nodePort}{wsPath}{queryString}";

            _logger.LogInformation("Proxying WebSocket to VM {VmId}: {Url}", route.VmId, targetUrl);

            using var clientWs = await context.WebSockets.AcceptWebSocketAsync();
            using var nodeWs = new System.Net.WebSockets.ClientWebSocket();

            // Add headers
            nodeWs.Options.SetRequestHeader("X-DeCloud-Target-Port", route.TargetPort.ToString());
            nodeWs.Options.SetRequestHeader("X-DeCloud-VM-Id", route.VmId);
            nodeWs.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await nodeWs.ConnectAsync(new Uri(targetUrl), cts.Token);

            // Bidirectional proxy
            var clientToNode = ProxyWebSocketOneWay(clientWs, nodeWs, "client→node", route.VmId);
            var nodeToClient = ProxyWebSocketOneWay(nodeWs, clientWs, "node→client", route.VmId);

            await Task.WhenAny(clientToNode, nodeToClient);

            // Close gracefully
            await CloseWebSocket(clientWs);
            await CloseWebSocket(nodeWs);

            _logger.LogInformation("WebSocket proxy closed for VM {VmId}", route.VmId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket proxy error for VM {VmId}", route.VmId);
        }
    }

    private async Task ProxyWebSocketOneWay(
        System.Net.WebSockets.WebSocket source,
        System.Net.WebSockets.WebSocket dest,
        string direction,
        string vmId)
    {
        var buffer = new byte[64 * 1024];

        try
        {
            while (source.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer, CancellationToken.None);

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    break;
                }

                if (dest.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    await dest.SendAsync(
                        new ArraySegment<byte>(buffer, 0, result.Count),
                        result.MessageType,
                        result.EndOfMessage,
                        CancellationToken.None);
                }
            }
        }
        catch (System.Net.WebSockets.WebSocketException)
        {
            // Connection closed
        }
    }

    private async Task CloseWebSocket(System.Net.WebSockets.WebSocket ws)
    {
        try
        {
            if (ws.State == System.Net.WebSockets.WebSocketState.Open)
            {
                await ws.CloseAsync(
                    System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                    "Closed",
                    CancellationToken.None);
            }
        }
        catch { }
    }
}

/// <summary>
/// Extension methods for registering the subdomain proxy middleware
/// </summary>
public static class SubdomainProxyExtensions
{
    public static IApplicationBuilder UseSubdomainProxy(this IApplicationBuilder app)
    {
        return app.UseMiddleware<SubdomainProxyMiddleware>();
    }
}