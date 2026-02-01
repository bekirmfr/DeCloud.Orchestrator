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
    Task<bool> UpsertRouteAsync(CentralIngressRoute route, CancellationToken ct = default);
    Task<bool> RemoveRouteAsync(string subdomain, CancellationToken ct = default);
    Task<bool> ReloadAllRoutesAsync(IEnumerable<CentralIngressRoute> routes, CancellationToken ct = default);
    Task<string?> GetWildcardCertStatusAsync(CancellationToken ct = default);
}

public class CentralCaddyManager : ICentralCaddyManager
{
    private readonly HttpClient _httpClient;
    private readonly CentralIngressOptions _options;
    private readonly ILogger<CentralCaddyManager> _logger;

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

    public async Task<bool> ReloadAllRoutesAsync(IEnumerable<CentralIngressRoute> routes, CancellationToken ct = default)
    {
        try
        {
            var routeList = routes.Where(r => r.Status == CentralRouteStatus.Active).ToList();
            _logger.LogInformation("Reloading central ingress with {Count} VM routes", routeList.Count);

            var config = await BuildFullConfigWithPreservedRoutesAsync(routeList, ct);
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

    /// <summary>
    /// Build full config while preserving non-VM routes (infrastructure routes)
    /// </summary>
    private async Task<object> BuildFullConfigWithPreservedRoutesAsync(
        List<CentralIngressRoute> vmRoutes,
        CancellationToken ct)
    {
        // Get current configuration from Caddy
        var existingRoutes = await GetExistingNonVmRoutesAsync(ct);

        // Build VM routes
        var caddyVmRoutes = vmRoutes
            .Where(r => !string.IsNullOrEmpty(r.VmPrivateIp) && !string.IsNullOrEmpty(r.NodePublicIp))
            .Select(BuildRouteConfig)
            .ToList();

        // Add catch-all for unmatched VM subdomains
        caddyVmRoutes.Add(BuildCatchAllRoute());

        // Combine: Infrastructure routes FIRST, then VM routes
        var allRoutes = existingRoutes.Concat(caddyVmRoutes).ToList();

        _logger.LogInformation(
            "Combined routes: {InfraRoutes} infrastructure + {VmRoutes} VM routes = {Total} total",
            existingRoutes.Count, caddyVmRoutes.Count, allRoutes.Count);

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
                // Check if this is a VM route (contains our VM domain pattern)
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
                                // Check if this host matches VM pattern
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

        return new
        {
            match = new[]
            {
            new { host = new[] { subdomain } }
        },
            handle = new object[]
            {
                new
                {
                    handler = "rewrite",
                    uri = $"/api/vms/{route.VmId}/proxy/http/{route.TargetPort}{{http.request.uri}}"
                },
                // Proxy to node agent
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
                        read_buffer_size = 4096,
                        versions = new[] { "1.1" }  // Force HTTP/1.1 to backend for reliability
                    },
                    flush_interval = 0,      // Enable streaming (0 = flush immediately)
                    buffer_requests = false,  // Don't buffer requests (better for WebSocket upgrades)
                    buffer_responses = false  // Don't buffer responses (enable streaming)
                }
            },
            terminal = true
        };
    }

    /// <summary>
    /// Build catch-all route for unmatched subdomains
    /// </summary>
    private object BuildCatchAllRoute()
    {
        return new
        {
            match = new[]
            {
                new { host = new[] { $"*.{_options.BaseDomain}" } }
            },
            handle = new[]
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
            terminal = true
        };
    }

    /// <summary>
    /// Build TLS configuration for wildcard certificate
    /// </summary>
    private object? BuildTlsConfig()
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

        return new
        {
            automation = new
            {
                policies = new[]
                {
                    new
                    {
                        subjects = new[] { $"*.{_options.BaseDomain}" },
                        issuers = new[]
                        {
                            new
                            {
                                module = "acme",
                                ca = _options.UseAcmeStaging
                                    ? "https://acme-staging-v02.api.letsencrypt.org/directory"
                                    : "https://acme-v02.api.letsencrypt.org/directory",
                                email = _options.AcmeEmail,
                                challenges = new { dns = dnsChallenge }
                            }
                        }
                    }
                }
            }
        };
    }
}