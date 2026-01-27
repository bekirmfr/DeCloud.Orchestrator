using Microsoft.Extensions.Options;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Collections.Concurrent;
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
}

public class CentralIngressService : ICentralIngressService
{
    private readonly DataStore _dataStore;
    private readonly ICentralCaddyManager _caddyManager;
    private readonly CentralIngressOptions _options;
    private readonly ILogger<CentralIngressService> _logger;

    // In-memory route cache (also persisted in DataStore)
    private readonly ConcurrentDictionary<string, CentralIngressRoute> _routes = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

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
        var pattern = _options.SubdomainPattern;

        // Replace placeholders
        var subdomain = pattern
            .Replace("{name}", SanitizeForSubdomain(vm.Name))
            .Replace("{id}", vm.Id)
            .Replace("{id8}", vm.Id.Length >= 8 ? vm.Id[..8] : vm.Id)
            .Replace("{id4}", vm.Id.Length >= 4 ? vm.Id[..4] : vm.Id);

        // Ensure valid subdomain
        subdomain = SanitizeForSubdomain(subdomain);

        // Handle collisions by appending ID fragment
        var fullSubdomain = $"{subdomain}.{_options.BaseDomain}";
        if (_routes.Values.Any(r => r.Subdomain == fullSubdomain && r.VmId != vm.Id))
        {
            var idSuffix = vm.Id.Length >= 6 ? vm.Id[..6] : vm.Id;
            subdomain = $"{subdomain}-{idSuffix}";
        }

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

            var success = await _caddyManager.ReloadAllRoutesAsync(activeRoutes, ct);

            if (success)
            {
                _logger.LogInformation("✓ Central ingress reloaded with {Count} routes", activeRoutes.Count);
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
                await ReloadAllAsync(ct);
            }
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
                await ReloadAllAsync(ct);
            }
        }
    }

    public async Task OnVmDeletedAsync(string vmId, CancellationToken ct = default)
    {
        _logger.LogDebug("VM {VmId} deleted - removing route", vmId);
        await UnregisterVmAsync(vmId, ct);
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