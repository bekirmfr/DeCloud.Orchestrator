using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Orchestrator.Services;

/// <summary>
/// Service for managing central ingress routes with automatic subdomain assignment.
/// Integrates with VM lifecycle to auto-register/remove routes.
/// </summary>
public interface ICentralIngressService
{
    /// <summary>
    /// Check if central ingress is enabled and configured
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Get the base domain (e.g., "vms.decloud.io")
    /// </summary>
    string? BaseDomain { get; }

    /// <summary>
    /// Generate subdomain for a VM based on configured pattern
    /// </summary>
    string GenerateSubdomain(VirtualMachine vm);

    /// <summary>
    /// Get the full URL for a VM
    /// </summary>
    string? GetVmUrl(VirtualMachine vm);

    /// <summary>
    /// Register a VM for central ingress routing
    /// </summary>
    Task<CentralIngressRoute?> RegisterVmAsync(string vmId, int? targetPort = null, CancellationToken ct = default);

    /// <summary>
    /// Unregister a VM from central ingress routing
    /// </summary>
    Task<bool> UnregisterVmAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Update the target port for a VM's route
    /// </summary>
    Task<bool> UpdatePortAsync(string vmId, int port, CancellationToken ct = default);

    /// <summary>
    /// Enable/disable the default subdomain for a VM
    /// </summary>
    Task<bool> SetEnabledAsync(string vmId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Get route for a specific VM
    /// </summary>
    Task<CentralIngressRoute?> GetRouteAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Get all active routes
    /// </summary>
    Task<List<CentralIngressRoute>> GetAllRoutesAsync(CancellationToken ct = default);

    /// <summary>
    /// Get status of the central ingress system
    /// </summary>
    Task<CentralIngressStatusResponse> GetStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Force reload all routes (admin operation)
    /// </summary>
    Task<bool> ReloadAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Called when a VM starts - auto-registers if enabled
    /// </summary>
    Task OnVmStartedAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Called when a VM stops - auto-removes route if enabled
    /// </summary>
    Task OnVmStoppedAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Called when a VM is deleted
    /// </summary>
    Task OnVmDeletedAsync(string vmId, CancellationToken ct = default);

    // =========================================================================
    // Custom Domain Management
    // =========================================================================

    /// <summary>
    /// Add a custom domain to a VM
    /// </summary>
    Task<CustomDomain?> AddCustomDomainAsync(string vmId, string domain, int targetPort, CancellationToken ct = default);

    /// <summary>
    /// Remove a custom domain from a VM
    /// </summary>
    Task<bool> RemoveCustomDomainAsync(string vmId, string domainId, CancellationToken ct = default);

    /// <summary>
    /// Verify DNS for a custom domain and activate it
    /// </summary>
    Task<CustomDomain?> VerifyCustomDomainDnsAsync(string vmId, string domainId, CancellationToken ct = default);

    /// <summary>
    /// Get all custom domains for a VM
    /// </summary>
    Task<List<CustomDomain>> GetCustomDomainsAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Check if a custom domain is registered and active (used by Caddy on-demand TLS)
    /// </summary>
    bool IsCustomDomainRegistered(string domain);
}

public class CentralIngressService : ICentralIngressService
{
    private readonly DataStore _dataStore;
    private readonly ICentralCaddyManager _caddyManager;
    private readonly CentralIngressOptions _options;
    private readonly ILogger<CentralIngressService> _logger;

    // In-memory route cache (also persisted in DataStore)
    private readonly ConcurrentDictionary<string, CentralIngressRoute> _routes = new();
    // Custom domains cache keyed by lowercase domain name for fast on-demand TLS lookups
    private readonly ConcurrentDictionary<string, CustomDomain> _customDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private const int MaxCustomDomainsPerVm = 5;

    // Subdomain validation
    private static readonly Regex SubdomainRegex = new(
        @"^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public CentralIngressService(
        DataStore dataStore,
        ICentralCaddyManager caddyManager,
        IOptions<CentralIngressOptions> options,
        ILogger<CentralIngressService> logger)
    {
        _dataStore = dataStore;
        _caddyManager = caddyManager;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => _options.Enabled && !string.IsNullOrEmpty(_options.BaseDomain);
    public string? BaseDomain => _options.BaseDomain;

    public string GenerateSubdomain(VirtualMachine vm)
    {
        // VM names are now canonical (DNS-safe + unique suffix) from VmNameService.
        // Use the name directly as the subdomain — no extra sanitization or
        // collision handling needed.
        var subdomain = SanitizeForSubdomain(vm.Name);
        return $"{subdomain}.{_options.BaseDomain}";
    }

    public string? GetVmUrl(VirtualMachine vm)
    {
        if (!IsEnabled) return null;

        // Check if VM has a registered route
        if (_routes.TryGetValue(vm.Id, out var route) && route.Status == CentralRouteStatus.Active)
        {
            return route.PublicUrl;
        }

        // Generate what the URL would be
        var subdomain = GenerateSubdomain(vm);
        return $"https://{subdomain}";
    }

    public async Task<CentralIngressRoute?> RegisterVmAsync(
        string vmId,
        int? targetPort = null,
        CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("Central ingress not enabled");
            return null;
        }

        // Get VM
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            _logger.LogWarning("Cannot register route: VM {VmId} not found", vmId);
            return null;
        }

        // Validate VM state
        if (vm.Status != VmStatus.Running)
        {
            _logger.LogDebug("VM {VmId} not running, skipping route registration", vmId);
            return null;
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            _logger.LogWarning("VM {VmId} has no node assignment", vmId);
            return null;
        }

        // Get node info
        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (node == null)
        {
            _logger.LogWarning("Node {NodeId} not found for VM {VmId}", vm.NodeId, vmId);
            return null;
        }

        var vmPrivateIp = vm.NetworkConfig.PrivateIp;
        if (string.IsNullOrEmpty(vmPrivateIp))
        {
            _logger.LogWarning("VM {VmId} has no private IP", vmId);
            return null;
        }

        // Create or update route
        var subdomain = GenerateSubdomain(vm);

        string nodeHost = node.CgnatInfo?.TunnelIp ?? node.PublicIp;

        var route = new CentralIngressRoute
        {
            VmId = vmId,
            VmName = vm.Name,
            Subdomain = subdomain,
            OwnerWallet = vm.OwnerWallet,
            NodeId = vm.NodeId,
            NodePublicIp = nodeHost,
            VmPrivateIp = vmPrivateIp,
            TargetPort = targetPort ?? _options.DefaultTargetPort,
            Status = CentralRouteStatus.Active,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _routes[vmId] = route;

        _logger.LogInformation(
            "Registered central ingress: {Subdomain} → VM {VmId} on {NodeIp}",
            route.Subdomain, vmId, route.NodePublicIp);

        // Update VM record with ingress config
        vm.IngressConfig ??= new VmIngressConfig();
        vm.IngressConfig.DefaultSubdomain = subdomain;
        vm.IngressConfig.DefaultPort = route.TargetPort;
        vm.IngressConfig.DefaultSubdomainEnabled = true;
        await _dataStore.SaveVmAsync(vm);

        // Reload Caddy
        await ReloadAllAsync(ct);

        return route;
    }

    public async Task<bool> UnregisterVmAsync(string vmId, CancellationToken ct = default)
    {
        if (_routes.TryRemove(vmId, out var route))
        {
            _logger.LogInformation("Unregistered central ingress for VM {VmId}", vmId);

            // Update VM record
            var vm = await _dataStore.GetVmAsync(vmId);
            if (vm != null)
            {
                if (vm.IngressConfig != null)
                {
                    vm.IngressConfig.DefaultSubdomainEnabled = false;
                }
                await _dataStore.SaveVmAsync(vm);
            }

            // Reload Caddy
            await ReloadAllAsync(ct);
            return true;
        }

        return false;
    }

    public async Task<bool> UpdatePortAsync(string vmId, int port, CancellationToken ct = default)
    {
        if (!_routes.TryGetValue(vmId, out var route))
        {
            return false;
        }

        route.TargetPort = port;
        route.UpdatedAt = DateTime.UtcNow;

        // Update VM record
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm != null && vm.IngressConfig != null)
        {
            vm.IngressConfig.DefaultPort = port;
            await _dataStore.SaveVmAsync(vm);
        }

        await ReloadAllAsync(ct);
        return true;
    }

    public async Task<bool> SetEnabledAsync(string vmId, bool enabled, CancellationToken ct = default)
    {
        if (enabled)
        {
            var route = await RegisterVmAsync(vmId, ct: ct);
            return route != null;
        }
        else
        {
            return await UnregisterVmAsync(vmId, ct);
        }
    }

    public Task<CentralIngressRoute?> GetRouteAsync(string vmId, CancellationToken ct = default)
    {
        _routes.TryGetValue(vmId, out var route);
        return Task.FromResult(route);
    }

    public Task<List<CentralIngressRoute>> GetAllRoutesAsync(CancellationToken ct = default)
    {
        return Task.FromResult(_routes.Values.ToList());
    }

    public async Task<CentralIngressStatusResponse> GetStatusAsync(CancellationToken ct = default)
    {
        var healthy = await _caddyManager.IsHealthyAsync(ct);
        var certStatus = await _caddyManager.GetWildcardCertStatusAsync(ct);

        return new CentralIngressStatusResponse(
            Enabled: IsEnabled,
            BaseDomain: _options.BaseDomain,
            TotalRoutes: _routes.Count,
            ActiveRoutes: _routes.Values.Count(r => r.Status == CentralRouteStatus.Active),
            CaddyHealthy: healthy,
            WildcardCertStatus: certStatus
        );
    }

    public async Task<bool> ReloadAllAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return false;

        await _reloadLock.WaitAsync(ct);
        try
        {
            var healthy = await _caddyManager.IsHealthyAsync(ct);
            if (!healthy)
            {
                _logger.LogError("Caddy not healthy - cannot reload routes");
                return false;
            }

            var activeRoutes = _routes.Values
                .Where(r => r.Status == CentralRouteStatus.Active)
                .ToList();

            var activeCustomDomains = _customDomains.Values
                .Where(d => d.Status == CustomDomainStatus.Active)
                .ToList();

            var success = await _caddyManager.ReloadAllRoutesAsync(activeRoutes, activeCustomDomains, ct);

            if (success)
            {
                _logger.LogInformation(
                    "✓ Central ingress reloaded with {RouteCount} routes + {DomainCount} custom domains",
                    activeRoutes.Count, activeCustomDomains.Count);
            }

            return success;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    // =========================================================================
    // VM Lifecycle Hooks
    // =========================================================================

    public async Task OnVmStartedAsync(string vmId, CancellationToken ct = default)
    {
        if (!IsEnabled || !_options.AutoRegisterOnStart)
        {
            return;
        }

        _logger.LogDebug("VM {VmId} started - checking for auto-registration", vmId);

        // Check if VM already has a route or if it should be registered
        if (!_routes.ContainsKey(vmId))
        {
            // Check if VM had ingress enabled before
            var vm = await _dataStore.GetVmAsync(vmId);
            if (vm != null)
            {
                if (vm.IngressConfig?.DefaultSubdomainEnabled != false)
                {
                    await RegisterVmAsync(vmId, vm.IngressConfig?.DefaultPort, ct);
                }
            }
        }
        else
        {
            // Re-activate existing route
            if (_routes.TryGetValue(vmId, out var route))
            {
                route.Status = CentralRouteStatus.Active;
                route.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Re-activate paused custom domains for this VM
        var reactivated = false;
        foreach (var cd in _customDomains.Values.Where(d => d.VmId == vmId && d.Status == CustomDomainStatus.Paused))
        {
            cd.Status = CustomDomainStatus.Active;
            reactivated = true;
        }

        if (reactivated || _routes.ContainsKey(vmId))
        {
            await ReloadAllAsync(ct);
        }
    }

    public async Task OnVmStoppedAsync(string vmId, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        _logger.LogDebug("VM {VmId} stopped - updating route status", vmId);

        if (_routes.TryGetValue(vmId, out var route))
        {
            if (_options.AutoRemoveOnStop)
            {
                route.Status = CentralRouteStatus.Paused;
                route.UpdatedAt = DateTime.UtcNow;
            }
        }

        // Pause active custom domains for this VM
        foreach (var cd in _customDomains.Values.Where(d => d.VmId == vmId && d.Status == CustomDomainStatus.Active))
        {
            cd.Status = CustomDomainStatus.Paused;
        }

        await ReloadAllAsync(ct);

        // Persist paused status
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm?.IngressConfig?.CustomDomains != null)
        {
            foreach (var cd in vm.IngressConfig.CustomDomains.Where(d => d.Status == CustomDomainStatus.Active))
            {
                cd.Status = CustomDomainStatus.Paused;
            }
            await _dataStore.SaveVmAsync(vm);
        }
    }

    public async Task OnVmDeletedAsync(string vmId, CancellationToken ct = default)
    {
        _logger.LogDebug("VM {VmId} deleted - removing route and custom domains", vmId);

        // Remove all custom domains for this VM from cache
        var domainsToRemove = _customDomains.Values.Where(d => d.VmId == vmId).ToList();
        foreach (var cd in domainsToRemove)
        {
            _customDomains.TryRemove(cd.Domain.ToLowerInvariant(), out _);
        }

        await UnregisterVmAsync(vmId, ct);
    }

    // =========================================================================
    // Custom Domain Management
    // =========================================================================

    private static readonly Regex DomainRegex = new(
        @"^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<CustomDomain?> AddCustomDomainAsync(
        string vmId, string domain, int targetPort, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            _logger.LogWarning("Central ingress not enabled");
            return null;
        }

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found", vmId);
            return null;
        }

        // Sanitize domain
        domain = domain.Trim().ToLowerInvariant();

        // Validate format
        if (!DomainRegex.IsMatch(domain))
        {
            _logger.LogWarning("Invalid domain format: {Domain}", domain);
            return null;
        }

        // Reject platform base domain
        if (!string.IsNullOrEmpty(_options.BaseDomain) &&
            domain.EndsWith($".{_options.BaseDomain}", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Cannot add platform subdomain as custom domain: {Domain}", domain);
            return null;
        }

        // Reject IP addresses
        if (IPAddress.TryParse(domain, out _))
        {
            _logger.LogWarning("Cannot use IP address as custom domain: {Domain}", domain);
            return null;
        }

        // Check global uniqueness
        if (_customDomains.ContainsKey(domain))
        {
            _logger.LogWarning("Domain {Domain} already registered", domain);
            return null;
        }

        // Enforce per-VM limit
        vm.IngressConfig ??= new VmIngressConfig();
        if (vm.IngressConfig.CustomDomains.Count >= MaxCustomDomainsPerVm)
        {
            _logger.LogWarning("VM {VmId} has reached max custom domains ({Max})", vmId, MaxCustomDomainsPerVm);
            return null;
        }

        // Validate port
        if (targetPort < 1 || targetPort > 65535)
            targetPort = 80;

        var customDomain = new CustomDomain
        {
            VmId = vmId,
            Domain = domain,
            OwnerWallet = vm.OwnerWallet,
            TargetPort = targetPort,
            Status = CustomDomainStatus.PendingDns,
            CreatedAt = DateTime.UtcNow
        };

        // Persist
        vm.IngressConfig.CustomDomains.Add(customDomain);
        await _dataStore.SaveVmAsync(vm);

        // Add to cache
        _customDomains[domain] = customDomain;

        _logger.LogInformation("Custom domain added: {Domain} → VM {VmId} (PendingDns)", domain, vmId);

        return customDomain;
    }

    public async Task<bool> RemoveCustomDomainAsync(string vmId, string domainId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm?.IngressConfig?.CustomDomains == null)
            return false;

        var cd = vm.IngressConfig.CustomDomains.FirstOrDefault(d => d.Id == domainId);
        if (cd == null)
            return false;

        // Remove from cache
        _customDomains.TryRemove(cd.Domain.ToLowerInvariant(), out _);

        // Remove from VM
        vm.IngressConfig.CustomDomains.Remove(cd);
        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation("Custom domain removed: {Domain} from VM {VmId}", cd.Domain, vmId);

        // Reload Caddy to remove the route
        if (cd.Status == CustomDomainStatus.Active)
        {
            await ReloadAllAsync(ct);
        }

        return true;
    }

    public async Task<CustomDomain?> VerifyCustomDomainDnsAsync(
        string vmId, string domainId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm?.IngressConfig?.CustomDomains == null)
            return null;

        var cd = vm.IngressConfig.CustomDomains.FirstOrDefault(d => d.Id == domainId);
        if (cd == null)
            return null;

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(cd.Domain, ct);

            if (addresses.Length == 0)
            {
                _logger.LogWarning("DNS lookup returned no addresses for {Domain}", cd.Domain);
                cd.Status = CustomDomainStatus.Error;
                await _dataStore.SaveVmAsync(vm);
                _customDomains[cd.Domain.ToLowerInvariant()] = cd;
                return cd;
            }

            // DNS resolves — activate the domain
            // (Caddy on-demand TLS will verify the domain further during cert issuance)
            cd.Status = CustomDomainStatus.Active;
            cd.VerifiedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(vm);

            // Update cache
            _customDomains[cd.Domain.ToLowerInvariant()] = cd;

            _logger.LogInformation(
                "Custom domain DNS verified: {Domain} resolves to {IPs}",
                cd.Domain, string.Join(", ", addresses.Select(a => a.ToString())));

            // Reload Caddy to add the route
            await ReloadAllAsync(ct);

            return cd;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS verification failed for {Domain}", cd.Domain);
            cd.Status = CustomDomainStatus.Error;
            await _dataStore.SaveVmAsync(vm);
            _customDomains[cd.Domain.ToLowerInvariant()] = cd;
            return cd;
        }
    }

    public async Task<List<CustomDomain>> GetCustomDomainsAsync(string vmId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        return vm?.IngressConfig?.CustomDomains ?? new List<CustomDomain>();
    }

    public bool IsCustomDomainRegistered(string domain)
    {
        if (string.IsNullOrEmpty(domain)) return false;
        return _customDomains.TryGetValue(domain.ToLowerInvariant(), out var cd)
               && cd.Status == CustomDomainStatus.Active;
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string SanitizeForSubdomain(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "vm";

        // Convert to lowercase
        var result = input.ToLowerInvariant();

        // Replace spaces and underscores with hyphens
        result = result.Replace(' ', '-').Replace('_', '-');

        // Remove any character that's not alphanumeric or hyphen
        result = Regex.Replace(result, @"[^a-z0-9-]", "");

        // Remove consecutive hyphens
        result = Regex.Replace(result, @"-+", "-");

        // Remove leading/trailing hyphens
        result = result.Trim('-');

        // Limit length (max 63 chars for subdomain label)
        if (result.Length > 63)
            result = result[..63].TrimEnd('-');

        // Ensure not empty
        if (string.IsNullOrEmpty(result))
            result = "vm";

        return result;
    }
}