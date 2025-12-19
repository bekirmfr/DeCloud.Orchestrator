using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orchestrator.Models;
using System.Net.Http.Json;
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

            // For efficiency, we rebuild the entire routes array
            // Caddy's API supports PATCH but rebuilding is simpler for our use case
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
            _logger.LogInformation("Reloading central ingress with {Count} routes", routeList.Count);

            var config = BuildFullConfig(routeList);
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
            // Check if certificate exists for wildcard domain
            var response = await _httpClient.GetAsync("/pki/ca/local/certificates", ct);
            if (response.IsSuccessStatusCode)
            {
                return "valid"; // Simplified - would need parsing for real status
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
    /// Build full config with routes
    /// </summary>
    private object BuildFullConfig(List<CentralIngressRoute> routes)
    {
        var caddyRoutes = routes
            .Where(r => !string.IsNullOrEmpty(r.VmPrivateIp) && !string.IsNullOrEmpty(r.NodePublicIp))
            .Select(BuildRouteConfig)
            .ToList();

        // Add a catch-all route for unmatched subdomains
        caddyRoutes.Add(BuildCatchAllRoute());

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
                            routes = caddyRoutes,
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
    /// Build TLS configuration for wildcard certificate
    /// </summary>
    private object? BuildTlsConfig()
    {
        if (string.IsNullOrEmpty(_options.AcmeEmail) || string.IsNullOrEmpty(_options.DnsApiToken))
        {
            _logger.LogWarning("ACME email or DNS token not configured - TLS may not work");
            return null;
        }

        // Build DNS challenge configuration based on provider
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
                    name = "route53",
                    // AWS credentials from environment
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
                        issuers = new object[]
                        {
                            new
                            {
                                module = "acme",
                                email = _options.AcmeEmail,
                                ca = _options.UseAcmeStaging
                                    ? "https://acme-staging-v02.api.letsencrypt.org/directory"
                                    : "https://acme-v02.api.letsencrypt.org/directory",
                                challenges = new
                                {
                                    dns = dnsChallenge
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Build a single route configuration
    /// </summary>
    private object BuildRouteConfig(CentralIngressRoute route)
    {
        // Route through the node to reach the VM
        // The node proxies to the VM's private IP
        var upstream = $"{route.NodePublicIp}:5100";  // NodeAgent port

        // We'll use a special endpoint on NodeAgent that proxies to VMs
        // Format: /proxy/{vmId}/* → VM's private IP
        var proxyPath = $"/internal/proxy/{route.VmId}";

        var handlers = new List<object>();

        // Rewrite the path to go through NodeAgent's proxy endpoint
        handlers.Add(new
        {
            handler = "rewrite",
            uri = $"{proxyPath}{{http.request.uri}}"
        });

        // Reverse proxy to the node
        handlers.Add(new
        {
            handler = "reverse_proxy",
            upstreams = new[] { new { dial = upstream } },
            transport = new
            {
                protocol = "http",
                read_buffer_size = 4096
            },
            headers = new
            {
                request = new
                {
                    set = new Dictionary<string, string[]>
                    {
                        ["X-Real-IP"] = new[] { "{http.request.remote.host}" },
                        ["X-Forwarded-For"] = new[] { "{http.request.remote.host}" },
                        ["X-Forwarded-Proto"] = new[] { "{http.request.scheme}" },
                        ["X-Forwarded-Host"] = new[] { "{http.request.host}" },
                        ["X-DeCloud-VM-Id"] = new[] { route.VmId },
                        ["X-DeCloud-Target-Port"] = new[] { route.TargetPort.ToString() }
                    }
                }
            },
            flush_interval = -1 // For WebSocket support
        });

        return new
        {
            match = new[]
            {
                new { host = new[] { route.Subdomain } }
            },
            handle = handlers,
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
                        Content_Type = new[] { "application/json" }
                    },
                    body = JsonSerializer.Serialize(new
                    {
                        error = "VM not found",
                        message = "This subdomain is not associated with any running VM",
                        hint = "The VM may be stopped or the subdomain may be incorrect"
                    })
                }
            },
            terminal = true
        };
    }
}