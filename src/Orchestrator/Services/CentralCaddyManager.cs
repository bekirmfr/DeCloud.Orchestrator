using Microsoft.Extensions.Options;
using Orchestrator.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orchestrator.Services;

/// <summary>
/// Manages Caddy on the Orchestrator for central wildcard ingress.
/// Handles dynamic routing of *.{baseDomain} to appropriate nodes/VMs.
/// </summary>
public interface ICentralCaddyManager
{
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
    Task<bool> InitializeWildcardConfigAsync(CancellationToken ct = default);

    [Obsolete("Use UpsertVmRouteAsync (Step 0 surgical update). Remove in next release.")]
    Task<bool> UpsertRouteAsync(CentralIngressRoute route, CancellationToken ct = default);

    [Obsolete("Use RemoveVmRouteAsync (Step 0 surgical update). Remove in next release.")]
    Task<bool> RemoveRouteAsync(string subdomain, CancellationToken ct = default);

    Task<bool> ReloadAllRoutesAsync(
        IEnumerable<CentralIngressRoute> routes,
        IEnumerable<CustomDomain>? customDomains = null,
        CancellationToken ct = default);

    Task<string?> GetWildcardCertStatusAsync(CancellationToken ct = default);

    void EnsureOrchestratorRoute(string domain, string upstream);

    // ─── Step 0 surgical operations (INGRESS_DESIGN.md §8.1) ─────────────
    Task<bool> UpsertVmRouteAsync(CentralIngressRoute route, CancellationToken ct = default);
    Task<bool> RemoveVmRouteAsync(string vmId, CancellationToken ct = default);
    Task<bool> UpsertCustomDomainRouteAsync(
        CustomDomain customDomain, CentralIngressRoute vmRoute, CancellationToken ct = default);
    Task<bool> RemoveCustomDomainRouteAsync(string customDomainId, CancellationToken ct = default);
}

public class CentralCaddyManager : ICentralCaddyManager
{
    private readonly HttpClient _httpClient;
    private readonly CentralIngressOptions _options;
    private readonly ILogger<CentralCaddyManager> _logger;

    // Orchestrator upstream — set by EnsureOrchestratorRouteAsync, defaults to localhost:5050
    private string _orchestratorUpstream = "localhost:5050";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public CentralCaddyManager(
        HttpClient httpClient,
        IOptions<CentralIngressOptions> options,
        ILogger<CentralCaddyManager> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.CaddyAdminUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/config/", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Caddy health check failed");
            return false;
        }
    }

    public async Task<bool> InitializeWildcardConfigAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_options.BaseDomain))
        {
            _logger.LogWarning("Central ingress not configured - no base domain set");
            return false;
        }

        _logger.LogInformation("Initializing central ingress for *.{Domain}", _options.BaseDomain);

        var config = BuildBaseConfig();
        return await ApplyConfigAsync(config, ct);
    }

    public async Task<bool> UpsertRouteAsync(CentralIngressRoute route, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation(
                "Adding route: {Subdomain} → {NodeIp} → {VmIp}:{Port}",
                route.Subdomain, route.NodePublicIp, route.VmPrivateIp, route.TargetPort);

            return true; // Actual update happens in ReloadAllRoutesAsync
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error upserting route for {Subdomain}", route.Subdomain);
            return false;
        }
    }

    public async Task<bool> RemoveRouteAsync(string subdomain, CancellationToken ct = default)
    {
        try
        {
            _logger.LogInformation("Removing route: {Subdomain}", subdomain);
            return true; // Actual update happens in ReloadAllRoutesAsync
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing route for {Subdomain}", subdomain);
            return false;
        }
    }

    public async Task<bool> ReloadAllRoutesAsync(IEnumerable<CentralIngressRoute> routes, IEnumerable<CustomDomain>? customDomains = null, CancellationToken ct = default)
    {
        try
        {
            var routeList = routes.Where(r => r.Status == CentralRouteStatus.Active).ToList();
            var domainList = customDomains?.Where(d => d.Status == CustomDomainStatus.Active).ToList()
                             ?? new List<CustomDomain>();
            _logger.LogInformation(
                "Reloading central ingress with {RouteCount} VM routes + {DomainCount} custom domains",
                routeList.Count, domainList.Count);

            var config = await BuildFullConfigWithPreservedRoutesAsync(routeList, domainList, ct);
            return await ApplyConfigAsync(config, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reloading routes");
            return false;
        }
    }

    public async Task<string?> GetWildcardCertStatusAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/pki/ca/local/certificates", ct);
            if (response.IsSuccessStatusCode)
            {
                return "valid";
            }
            return "pending";
        }
        catch
        {
            return "unknown";
        }
    }

    public void EnsureOrchestratorRoute(
        string domain,
        string upstream)
    {
        // Store the upstream so BuildOrchestratorRoute() uses it.
        // The route itself is now included in every ReloadAllRoutesAsync call,
        // so no one-shot POST is needed.
        _orchestratorUpstream = upstream;

        _logger.LogInformation(
            "Orchestrator route configured: {Domain} → {Upstream} (included in every config reload)",
            domain, upstream);
    }

    private async Task<bool> ApplyConfigAsync(object config, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/load", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Failed to apply Caddy config: {Status} - {Error}",
                    response.StatusCode, error);
                return false;
            }

            _logger.LogInformation("✓ Central Caddy configuration applied");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error applying Caddy config");
            return false;
        }
    }

    // =========================================================================
    // Surgical route operations (Step 0 — INGRESS_DESIGN.md §8.1)
    //
    // Each VM and custom-domain route carries a stable @id (see BuildRouteConfig).
    // Lifecycle events use these methods instead of full-config rebuilds.
    //
    // Order invariant maintained: orchestrator-route at index 0, vm-catchall last,
    // everything else inserted between (order irrelevant — all specific FQDNs).
    // =========================================================================

    private const string RoutesBasePath = "/config/apps/http/servers/central_ingress/routes";

    /// <summary>
    /// Insert or replace a route by @id. Tries PUT first (replace existing);
    /// on 404 falls back to POST at index 1 (insert new).
    ///
    /// Used by AddVmRouteAsync, UpdateVmRouteAsync, AddCustomDomainRouteAsync —
    /// callers don't need to know whether the route already exists.
    /// </summary>
    private async Task<bool> UpsertRouteByIdAsync(
        string atId, object routeConfig, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(routeConfig, JsonOptions);

        // PUT /id/{atId} — atomic replace if exists
        using (var putContent = new StringContent(json, Encoding.UTF8, "application/json"))
        {
            var putResp = await _httpClient.PutAsync($"/id/{atId}", putContent, ct);
            if (putResp.IsSuccessStatusCode)
            {
                _logger.LogInformation("✓ Route updated: {AtId}", atId);
                return true;
            }

            if (putResp.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                var err = await putResp.Content.ReadAsStringAsync(ct);
                _logger.LogError("Caddy PUT /id/{AtId} failed: {Status} {Error}",
                    atId, putResp.StatusCode, err);
                return false;
            }
        }

        // 404 — route does not exist. Insert at index 1 (after orchestrator-route).
        using (var postContent = new StringContent(json, Encoding.UTF8, "application/json"))
        {
            var postResp = await _httpClient.PostAsync($"{RoutesBasePath}/1", postContent, ct);
            if (postResp.IsSuccessStatusCode)
            {
                _logger.LogInformation("✓ Route inserted: {AtId}", atId);
                return true;
            }

            var err = await postResp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Caddy POST {Path}/1 (for {AtId}) failed: {Status} {Error}",
                RoutesBasePath, atId, postResp.StatusCode, err);
            return false;
        }
    }

    /// <summary>
    /// Remove a route by @id. 404 is treated as success — the route was already absent.
    /// </summary>
    private async Task<bool> RemoveRouteByIdAsync(string atId, CancellationToken ct)
    {
        try
        {
            var resp = await _httpClient.DeleteAsync($"/id/{atId}", ct);
            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation("✓ Route removed: {AtId}", atId);
                return true;
            }

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogDebug("Route {AtId} already absent — no-op", atId);
                return true;
            }

            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError("Caddy DELETE /id/{AtId} failed: {Status} {Error}",
                atId, resp.StatusCode, err);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing route {AtId}", atId);
            return false;
        }
    }

    // ─── Public surgical API ─────────────────────────────────────────────────

    public Task<bool> UpsertVmRouteAsync(CentralIngressRoute route, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(route.NodePublicIp))
        {
            _logger.LogWarning("Skipping route upsert for VM {VmId}: no NodePublicIp", route.VmId);
            return Task.FromResult(false);
        }

        var atId = $"vm-{route.VmId}";
        return UpsertRouteByIdAsync(atId, BuildRouteConfig(route), ct);
    }

    public Task<bool> RemoveVmRouteAsync(string vmId, CancellationToken ct = default)
    {
        return RemoveRouteByIdAsync($"vm-{vmId}", ct);
    }

    public Task<bool> UpsertCustomDomainRouteAsync(
        CustomDomain customDomain, CentralIngressRoute vmRoute, CancellationToken ct = default)
    {
        var atId = $"cd-{customDomain.Id}";
        return UpsertRouteByIdAsync(atId, BuildCustomDomainRouteConfig(customDomain, vmRoute), ct);
    }

    public Task<bool> RemoveCustomDomainRouteAsync(string customDomainId, CancellationToken ct = default)
    {
        return RemoveRouteByIdAsync($"cd-{customDomainId}", ct);
    }

    /// <summary>
    /// Build full config while preserving non-VM routes (infrastructure routes)
    /// </summary>
    private async Task<object> BuildFullConfigWithPreservedRoutesAsync(
        List<CentralIngressRoute> vmRoutes,
        List<CustomDomain> customDomains,
        CancellationToken ct)
    {
        // Get current configuration from Caddy
        var existingRoutes = await GetExistingNonVmRoutesAsync(ct);

        // Build VM subdomain routes
        // VmPrivateIp is metadata only — BuildRouteConfig routes to NodePublicIp:5100
        // and rewrites to /api/vms/{vmId}/proxy/http/{port}. Filtering on VmPrivateIp
        // silently drops migrated VMs whose private IP hasn't been updated on the new node.
        var caddyVmRoutes = vmRoutes
            .Where(r => !string.IsNullOrEmpty(r.NodePublicIp))
            .Select(BuildRouteConfig)
            .ToList();

        // Build custom domain routes — each custom domain maps to a VM's existing route info
        var vmRouteLookup = vmRoutes.ToDictionary(r => r.VmId, r => r);
        foreach (var cd in customDomains)
        {
            if (vmRouteLookup.TryGetValue(cd.VmId, out var vmRoute))
            {
                caddyVmRoutes.Add(BuildCustomDomainRouteConfig(cd, vmRoute));
            }
        }

        // Add catch-all for unmatched VM subdomains
        caddyVmRoutes.Add(BuildCatchAllRoute());

        // Build the final route list:
        // 1. Orchestrator domain route (highest priority — always first)
        // 2. Preserved infrastructure routes (excluding orchestrator — we rebuild it)
        // 3. VM subdomain routes + custom domain routes + catch-all
        var allRoutes = new List<object>();

        // Always include orchestrator route if configured
        var orchRoute = BuildOrchestratorRoute();
        if (orchRoute != null)
        {
            allRoutes.Add(orchRoute);
        }

        // Filter orchestrator domain out of preserved routes to avoid duplication
        var orchDomain = _options.OrchestratorDomain;
        foreach (var preserved in existingRoutes)
        {
            // Skip if this is the orchestrator route (we already added it above)
            if (!string.IsNullOrEmpty(orchDomain))
            {
                var json = JsonSerializer.Serialize(preserved, JsonOptions);
                if (json.Contains(orchDomain, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            allRoutes.Add(preserved);
        }

        allRoutes.AddRange(caddyVmRoutes);

        _logger.LogInformation(
            "Combined routes: {InfraRoutes} infrastructure + {VmRoutes} VM + {CustomDomains} custom = {Total} total",
            existingRoutes.Count, vmRoutes.Count, customDomains.Count, allRoutes.Count);

        return new
        {
            admin = new { listen = "localhost:2019" },
            logging = new
            {
                logs = new
                {
                    @default = new
                    {
                        writer = new
                        {
                            output = "file",
                            filename = "/var/log/caddy/central-ingress.log"
                        },
                        encoder = new { format = "json" }
                    }
                }
            },
            apps = new
            {
                http = new
                {
                    servers = new
                    {
                        central_ingress = new
                        {
                            listen = new[] { ":80", ":443" },
                            routes = allRoutes,
                            protocols = new[] { "h1" },
                            automatic_https = new { disable = false, disable_redirects = false }
                        }
                    }
                },
                tls = BuildTlsConfig(customDomains.Any())
            }
        };
    }

    /// <summary>
    /// Get existing routes that are NOT VM routes (preserve infrastructure)
    /// </summary>
    private async Task<List<object>> GetExistingNonVmRoutesAsync(CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                "/config/apps/http/servers/central_ingress/routes", ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch existing routes, starting fresh");
                return new List<object>();
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var routes = JsonSerializer.Deserialize<JsonElement>(json);

            if (routes.ValueKind != JsonValueKind.Array)
            {
                return new List<object>();
            }

            var preservedRoutes = new List<object>();
            var vmDomainPattern = $".{_options.BaseDomain}"; // Match *.vms.stackfi.tech

            foreach (var route in routes.EnumerateArray())
            {
                // Check if this is a VM route (contains our VM domain pattern or VM proxy rewrite)
                var isVmRoute = false;

                if (route.TryGetProperty("match", out var matchArray) && matchArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var match in matchArray.EnumerateArray())
                    {
                        if (match.TryGetProperty("host", out var hostArray) && hostArray.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var host in hostArray.EnumerateArray())
                            {
                                var hostStr = host.GetString() ?? "";
                                // Check if this host matches VM pattern or wildcard
                                if (hostStr.Contains(vmDomainPattern) || hostStr.StartsWith("*."))
                                {
                                    isVmRoute = true;
                                    break;
                                }
                            }
                        }
                        if (isVmRoute) break;
                    }
                }

                // Also check if route has a rewrite handler targeting VM proxy (custom domain routes)
                if (!isVmRoute && route.TryGetProperty("handle", out var handleArray) &&
                    handleArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var handler in handleArray.EnumerateArray())
                    {
                        if (handler.TryGetProperty("handler", out var handlerName) &&
                            handlerName.GetString() == "rewrite" &&
                            handler.TryGetProperty("uri", out var uriProp))
                        {
                            var uri = uriProp.GetString() ?? "";
                            if (uri.Contains("/api/vms/") && uri.Contains("/proxy/http/"))
                            {
                                isVmRoute = true;
                                break;
                            }
                        }
                    }
                }

                // Preserve non-VM routes (infrastructure routes)
                if (!isVmRoute)
                {
                    // Convert JsonElement back to object for serialization
                    var routeObj = JsonSerializer.Deserialize<object>(route.GetRawText(), JsonOptions);
                    if (routeObj != null)
                    {
                        preservedRoutes.Add(routeObj);

                        // Log what we're preserving
                        if (route.TryGetProperty("match", out var m) &&
                            m.ValueKind == JsonValueKind.Array &&
                            m.EnumerateArray().FirstOrDefault().TryGetProperty("host", out var h) &&
                            h.ValueKind == JsonValueKind.Array)
                        {
                            var hosts = string.Join(", ", h.EnumerateArray().Select(x => x.GetString()));
                            _logger.LogInformation("Preserving infrastructure route: {Hosts}", hosts);
                        }
                    }
                }
            }

            return preservedRoutes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching existing routes, starting fresh");
            return new List<object>();
        }
    }

    /// <summary>
    /// Build base Caddy configuration with wildcard TLS
    /// </summary>
    private object BuildBaseConfig()
    {
        return new
        {
            admin = new { listen = "localhost:2019" },
            apps = new
            {
                http = new
                {
                    servers = new
                    {
                        central_ingress = new
                        {
                            listen = new[] { ":80", ":443" },
                            routes = new object[] { },
                            protocols = new[] { "h1" },  // Force HTTP/1.1 only (fixes code-server and other WebSocket services)
                            automatic_https = new
                            {
                                disable = false,
                                disable_redirects = false
                            }
                        }
                    }
                },
                tls = BuildTlsConfig()
            }
        };
    }

    /// <summary>
    /// Build route configuration for a single VM
    /// </summary>
    private object BuildRouteConfig(CentralIngressRoute route)
    {
        var subdomain = route.Subdomain.EndsWith($".{_options.BaseDomain}")
            ? route.Subdomain
            : $"{route.Subdomain}.{_options.BaseDomain}";

        return new Dictionary<string, object>
        {
            ["@id"] = $"vm-{route.VmId}",
            ["match"] = new[]
            {
            new { host = new[] { subdomain } }
        },
            ["handle"] = new object[]
            {
            new
            {
                handler = "rewrite",
                uri = $"/api/vms/{route.VmId}/proxy/http/{route.TargetPort}{{http.request.uri}}"
            },
            new
            {
                handler = "reverse_proxy",
                upstreams = new[]
                {
                    new { dial = $"{route.NodePublicIp}:5100" }
                },
                headers = new
                {
                    request = new
                    {
                        set = new Dictionary<string, string[]>
                        {
                            ["Host"] = new[] { "{http.request.host}" },
                            ["X-Forwarded-For"] = new[] { "{http.request.remote.host}" },
                            ["X-Forwarded-Proto"] = new[] { "{http.request.scheme}" },
                            ["X-Forwarded-Host"] = new[] { "{http.request.host}" },
                            ["X-Real-IP"] = new[] { "{http.request.remote.host}" }
                        }
                    }
                },
                transport = new
                {
                    protocol = "http",
                    read_buffer_size = 16384,
                    versions = new[] { "1.1" },
                    dial_timeout = "10s",
                    response_header_timeout = "600s",
                    read_timeout = "600s"
                },
                flush_interval = -1
            }
            },
            ["terminal"] = true
        };
    }

    /// <summary>
    /// Build route configuration for a custom domain pointing to a VM
    /// </summary>
    private object BuildCustomDomainRouteConfig(CustomDomain customDomain, CentralIngressRoute vmRoute)
    {
        return new Dictionary<string, object>
        {
            ["@id"] = $"cd-{customDomain.Id}",
            ["match"] = new[]
            {
            new { host = new[] { customDomain.Domain } }
        },
            ["handle"] = new object[]
            {
            new
            {
                handler = "rewrite",
                uri = $"/api/vms/{customDomain.VmId}/proxy/http/{customDomain.TargetPort}{{http.request.uri}}"
            },
            new
            {
                handler = "reverse_proxy",
                upstreams = new[]
                {
                    new { dial = $"{vmRoute.NodePublicIp}:5100" }
                },
                headers = new
                {
                    request = new
                    {
                        set = new Dictionary<string, string[]>
                        {
                            ["Host"] = new[] { "{http.request.host}" },
                            ["X-Forwarded-For"] = new[] { "{http.request.remote.host}" },
                            ["X-Forwarded-Proto"] = new[] { "{http.request.scheme}" },
                            ["X-Forwarded-Host"] = new[] { "{http.request.host}" },
                            ["X-Real-IP"] = new[] { "{http.request.remote.host}" }
                        }
                    }
                },
                transport = new
                {
                    protocol = "http",
                    read_buffer_size = 16384,
                    versions = new[] { "1.1" },
                    dial_timeout = "10s",
                    response_header_timeout = "600s",
                    read_timeout = "600s"
                },
                flush_interval = -1
            }
            },
            ["terminal"] = true
        };
    }

    /// <summary>
    /// Build catch-all route for unmatched subdomains
    /// </summary>
    private object BuildCatchAllRoute()
    {
        return new Dictionary<string, object>
        {
            ["@id"] = "vm-catchall",
            ["match"] = new[]
            {
            new { host = new[] { $"*.{_options.BaseDomain}" } }
        },
            ["handle"] = new[]
            {
            new
            {
                handler = "static_response",
                status_code = 404,
                headers = new
                {
                    content_Type = new[] { "application/json" }
                },
                body = "{\"error\":\"VM not found\",\"message\":\"This subdomain is not associated with any running VM\",\"hint\":\"The VM may be stopped or the subdomain may be incorrect\"}"
            }
        },
            ["terminal"] = true
        };
    }

    /// <summary>
    /// Build the reverse-proxy route for the orchestrator's own domain.
    /// Included in every config reload so it can never be lost.
    /// </summary>
    private object? BuildOrchestratorRoute()
    {
        if (string.IsNullOrEmpty(_options.OrchestratorDomain))
            return null;

        return new Dictionary<string, object>
        {
            ["@id"] = "orchestrator-route",
            ["match"] = new[] { new { host = new[] { _options.OrchestratorDomain } } },
            ["handle"] = new object[]
            {
                new
                {
                    handler = "reverse_proxy",
                    upstreams = new[] { new { dial = _orchestratorUpstream } },
                    headers = new
                    {
                        request = new
                        {
                            set = new Dictionary<string, string[]>
                            {
                                ["X-Forwarded-Proto"] = new[] { "{http.request.scheme}" },
                                ["X-Forwarded-For"]   = new[] { "{http.request.remote.host}" },
                                ["X-Real-IP"]         = new[] { "{http.request.remote.host}" }
                            }
                        }
                    },
                    transport = new
                    {
                        protocol = "http",
                        read_timeout = "300s",
                        response_header_timeout = "300s"
                    }
                }
            },
            ["terminal"] = true
        };
    }

    /// <summary>
    /// Build TLS configuration for wildcard certificate + on-demand TLS for custom domains
    /// </summary>
    private object? BuildTlsConfig(bool hasCustomDomains = false)
    {
        if (string.IsNullOrEmpty(_options.AcmeEmail) || string.IsNullOrEmpty(_options.DnsApiToken))
        {
            _logger.LogWarning("ACME email or DNS token not configured - TLS may not work");
            return null;
        }

        var dnsChallenge = _options.DnsProvider.ToLower() switch
        {
            "cloudflare" => new
            {
                provider = new
                {
                    name = "cloudflare",
                    api_token = _options.DnsApiToken
                }
            },
            "route53" => new
            {
                provider = new
                {
                    name = "route53"
                }
            },
            "digitalocean" => new
            {
                provider = new
                {
                    name = "digitalocean",
                    api_token = _options.DnsApiToken
                }
            },
            _ => (object?)null
        };

        var acmeCa = _options.UseAcmeStaging
            ? "https://acme-staging-v02.api.letsencrypt.org/directory"
            : "https://acme-v02.api.letsencrypt.org/directory";

        // Wildcard policy for *.baseDomain using DNS-01 challenge
        var wildcardPolicy = new Dictionary<string, object>
        {
            ["subjects"] = string.IsNullOrEmpty(_options.OrchestratorDomain)
                ? new[] { $"*.{_options.BaseDomain}" }
                : new[] { $"*.{_options.BaseDomain}", _options.OrchestratorDomain },
            ["issuers"] = new object[]
            {
                new
                {
                    module = "acme",
                    ca = acmeCa,
                    email = _options.AcmeEmail,
                    challenges = new { dns = dnsChallenge }
                }
            }
        };

        var policies = new List<object> { wildcardPolicy };

        // On-demand TLS policy for custom domains (catch-all, no subjects)
        // Caddy uses HTTP-01 challenge and calls our ask endpoint to verify the domain
        if (hasCustomDomains)
        {
            var onDemandPolicy = new Dictionary<string, object>
            {
                ["issuers"] = new object[]
                {
                    new
                    {
                        module = "acme",
                        ca = acmeCa,
                        email = _options.AcmeEmail,
                    }
                },
                ["on_demand"] = true
            };
            policies.Add(onDemandPolicy);
        }

        var automation = new Dictionary<string, object>
        {
            ["policies"] = policies
        };

        // on_demand lives inside automation (not a sibling of it).
        // Uses tls.permission.http module — "ask" was deprecated in Caddy 2.10.
        if (hasCustomDomains)
        {
            automation["on_demand"] = new Dictionary<string, object>
            {
                ["permission"] = new Dictionary<string, object>
                {
                    ["module"] = "http",
                    ["endpoint"] = "http://localhost:5050/api/central-ingress/domain-check"
                }
            };
        }

        return new { automation };
    }
}