using DeCloud.Shared.Enums;
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
    /// Batch-restores routes for all Running VMs on orchestrator startup.
    /// Populates _routes without triggering a per-VM Caddy reload,
    /// then performs a single reload at the end.
    /// Bypasses AutoRegisterOnStart — startup restoration is always required.
    /// </summary>
    Task RestoreRunningVmRoutesAsync(CancellationToken ct = default);

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

        // VmPrivateIp is stored as metadata but is not used for Caddy routing
        // (the upstream is the node agent at NodePublicIp:5100, which proxies
        // internally to the VM). Log a warning for observability but continue.
        var vmPrivateIp = vm.NetworkConfig?.PrivateIp ?? string.Empty;
        if (string.IsNullOrEmpty(vmPrivateIp))
        {
            _logger.LogWarning(
                "VM {VmId} has no private IP on orchestrator record — ingress will route " +
                "via node agent ({NodeId}); this is normal for freshly migrated VMs",
                vmId, vm.NodeId);
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

        // Surgical: upsert just this VM's route (Step 0)
        await _caddyManager.UpsertVmRouteAsync(route, ct);

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

            // Surgical: remove just this VM's route (Step 0)
            await _caddyManager.RemoveVmRouteAsync(vmId, ct);
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

        // Surgical: PUT the updated route (Step 0)
        await _caddyManager.UpsertVmRouteAsync(route, ct);
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

    public async Task RestoreRunningVmRoutesAsync(CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        // Use ActiveVMs (in-memory hot cache) — already loaded by DataStore.LoadStateFromDatabaseAsync.
        // Filtering here avoids a second full MongoDB scan on startup.
        var runningVms = _dataStore.GetActiveVMs()
            .Where(v => v.Status == VmStatus.Running &&
                        v.IngressConfig?.DefaultSubdomainEnabled != false)
            .ToList();

        _logger.LogInformation(
            "Restoring ingress routes for {Count} Running VMs on startup", runningVms.Count);

        int restored = 0;
        int skipped = 0;

        foreach (var vm in runningVms)
        {
            if (string.IsNullOrEmpty(vm.NodeId)) { skipped++; continue; }

            var node = await _dataStore.GetNodeAsync(vm.NodeId);
            if (node == null) { skipped++; continue; }

            // Build route directly — no per-VM Caddy reload.
            // We call ReloadAllAsync once at the end.
            var subdomain = GenerateSubdomain(vm);
            var nodeHost = node.CgnatInfo?.TunnelIp ?? node.PublicIp ?? string.Empty;
            if (string.IsNullOrEmpty(nodeHost)) { skipped++; continue; }

            _routes[vm.Id] = new CentralIngressRoute
            {
                VmId = vm.Id,
                VmName = vm.Name,
                Subdomain = subdomain,
                OwnerWallet = vm.OwnerWallet,
                NodeId = vm.NodeId,
                NodePublicIp = nodeHost,
                VmPrivateIp = vm.NetworkConfig?.PrivateIp ?? string.Empty,
                TargetPort = vm.IngressConfig?.DefaultPort ?? _options.DefaultTargetPort,
                Status = CentralRouteStatus.Active,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _logger.LogDebug(
                "Restored ingress route: {Subdomain} → {NodeHost} (VM {VmId})",
                subdomain, nodeHost, vm.Id);
            restored++;
        }

        // Always reload — the orchestrator's own domain route is built into every
        // config reload, so this must run even when there are no VM routes.
        await ReloadAllAsync(ct);

        _logger.LogInformation(
            "✓ Ingress route restore complete: {Restored} routes loaded, {Skipped} skipped",
            restored, skipped);
    }

    public async Task OnVmStartedAsync(string vmId, CancellationToken ct = default)
    {
        if (!IsEnabled || !_options.AutoRegisterOnStart)
        {
            return;
        }

        _logger.LogDebug("VM {VmId} started - checking for auto-registration", vmId);

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null) return;

        if (vm.IngressConfig?.DefaultSubdomainEnabled == false) return;

        // Always re-register — the VM may have migrated to a different node
        // since the route was first created. Re-registering rebuilds the
        // upstream target from the current vm.NodeId, pointing Caddy to
        // the correct node agent after migration.
        await RegisterVmAsync(vmId, vm.IngressConfig?.DefaultPort, ct);

        // Re-activate paused custom domains for this VM — surgical upsert each (Step 0)
        _routes.TryGetValue(vmId, out var vmRoute);
        foreach (var cd in _customDomains.Values.Where(d => d.VmId == vmId && d.Status == CustomDomainStatus.Paused))
        {
            cd.Status = CustomDomainStatus.Active;
            if (vmRoute != null)
            {
                await _caddyManager.UpsertCustomDomainRouteAsync(cd, vmRoute, ct);
            }
        }
    }

    public async Task OnVmStoppedAsync(string vmId, CancellationToken ct = default)
    {
        if (!IsEnabled)
        {
            return;
        }

        // Intent vs state: a route describes the user's intent that a hostname
        // should reach a VM. Whether the underlying node is online right now
        // is state. Removing routes from Caddy on every transient stop or
        // node-offline event conflates the two — and the recovery path is
        // weak (no automatic re-add when the node returns short of a full
        // ReloadAllRoutesAsync, which doesn't fire on heartbeat resume).
        //
        // Design: keep routes installed in Caddy across transient outages.
        // Caddy will return a clean 502 from the dead upstream while the
        // node is gone, which is the truthful state of the world; when the
        // node returns and lifecycle transitions the VM back to Running,
        // OnVmStartedAsync's RegisterVmAsync re-derives the upstream from
        // the current vm.NodeId and updates the route in place. Routes are
        // only torn down for terminal events: VM deleted (OnVmDeletedAsync)
        // or the user explicitly disables ingress.
        //
        // We still update in-memory status to Paused so the dashboard can
        // reflect "ingress paused" without lying about the Caddy state.

        _logger.LogDebug("VM {VmId} stopped — keeping Caddy route in place, marking Paused", vmId);

        if (_routes.TryGetValue(vmId, out var route))
        {
            route.Status = CentralRouteStatus.Paused;
            route.UpdatedAt = DateTime.UtcNow;
        }

        // Same for custom domains: keep the Caddy route, mark the in-memory
        // entry Paused. The cert automation policy (on-demand TLS) keeps
        // working because the entry is still in _customDomains; if the user
        // explicitly removes the custom domain, RemoveCustomDomainAsync tears
        // down both the entry and the route.
        foreach (var cd in _customDomains.Values.Where(d => d.VmId == vmId && d.Status == CustomDomainStatus.Active))
        {
            cd.Status = CustomDomainStatus.Paused;
        }

        // Persist paused status for the dashboard.
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

        // Per-VM uniqueness only — global uniqueness is enforced at Verify time
        // (the CNAME proof-of-control check is the real gate). Allowing the
        // same domain to sit in PendingDns across multiple VMs prevents an
        // attacker from squatting on names they do not control: the squatter
        // can never Verify (no DNS for that name → no CNAME pointing here →
        // verification fails), so the legitimate owner is free to add it on
        // their own VM and verify normally. The first VM to successfully
        // Verify claims the domain globally (see VerifyCustomDomainDnsAsync).
        if (vm.IngressConfig?.CustomDomains?.Any(d =>
                string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase)) == true)
        {
            _logger.LogWarning("Domain {Domain} already added to VM {VmId}", domain, vmId);
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

        // Persist. The in-memory _customDomains cache is intentionally NOT
        // populated here — it backs IsCustomDomainRegistered, which Caddy's
        // on-demand TLS permission endpoint consults to decide whether to
        // mint a certificate. Only DNS-verified (Active) domains belong
        // there; populating it for PendingDns would let an attacker who
        // registers victim.com on their VM trigger a cert issuance attempt
        // the moment any browser sends a request with that Host header.
        // VerifyCustomDomainDnsAsync adds the entry once DNS proves control.
        vm.IngressConfig.CustomDomains.Add(customDomain);
        await _dataStore.SaveVmAsync(vm);

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

        // Surgical: remove just this custom domain's route (Step 0)
        if (cd.Status == CustomDomainStatus.Active)
        {
            await _caddyManager.RemoveCustomDomainRouteAsync(cd.Id, ct);
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
            // Proof-of-control check: the domain's DNS chain must end at the
            // platform base domain (or, for zone apexes where CNAME is illegal,
            // at one of the configured platform public IPs). Only the DNS
            // owner could have set such a record, so a passing check proves
            // the verifier controls the domain.
            //
            // Built-in System.Net.Dns.GetHostAddressesAsync gives us only the
            // final A/AAAA records — it does not surface CNAME aliases. We
            // need both: prefer CNAME chain matching, fall back to A-record
            // intersection with platform IPs.
            var baseDomain = (_options.BaseDomain ?? "").TrimEnd('.').ToLowerInvariant();
            var (pointsHere, evidence) = await CheckDnsPointsHereAsync(cd.Domain, baseDomain, ct);

            if (!pointsHere)
            {
                _logger.LogWarning(
                    "DNS proof-of-control failed for {Domain}: {Evidence}",
                    cd.Domain, evidence);
                cd.Status = CustomDomainStatus.Error;
                await _dataStore.SaveVmAsync(vm);
                // Failure path does NOT touch _customDomains — unverified
                // domains never enter the on-demand-TLS allowlist.
                return cd;
            }

            // Global uniqueness is enforced HERE, at the moment of proof.
            // Two VMs may hold the same PendingDns domain, but only the first
            // to verify wins — and because verification requires DNS control,
            // only the legitimate owner can win the race.
            if (_customDomains.TryGetValue(cd.Domain.ToLowerInvariant(), out var existing)
                && existing.VmId != vmId
                && existing.Status == CustomDomainStatus.Active)
            {
                _logger.LogWarning(
                    "Domain {Domain} already verified on VM {OtherVmId}; cannot claim for VM {VmId}",
                    cd.Domain, existing.VmId, vmId);
                cd.Status = CustomDomainStatus.Error;
                await _dataStore.SaveVmAsync(vm);
                return cd;
            }

            cd.Status = CustomDomainStatus.Active;
            cd.VerifiedAt = DateTime.UtcNow;
            await _dataStore.SaveVmAsync(vm);

            // Promote into the on-demand-TLS allowlist now that proof passed.
            _customDomains[cd.Domain.ToLowerInvariant()] = cd;

            _logger.LogInformation(
                "Custom domain DNS verified: {Domain} ({Evidence})",
                cd.Domain, evidence);

            // Surgical: add just this custom domain's route (Step 0)
            if (_routes.TryGetValue(vmId, out var vmRoute))
            {
                await _caddyManager.UpsertCustomDomainRouteAsync(cd, vmRoute, ct);
            }
            else
            {
                _logger.LogWarning(
                    "Custom domain {Domain} verified but VM {VmId} has no active route — route will be added when VM starts",
                    cd.Domain, vmId);
            }

            return cd;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DNS verification failed for {Domain}", cd.Domain);
            cd.Status = CustomDomainStatus.Error;
            await _dataStore.SaveVmAsync(vm);
            // Failure path does NOT touch _customDomains.
            return cd;
        }
    }

    /// <summary>
    /// DNS proof-of-control check. Returns true iff the domain's resolution
    /// chain demonstrably points at this platform — either via a CNAME chain
    /// ending at the configured base domain, or (for zone apexes where CNAME
    /// is illegal) via an A-record matching one of the configured platform
    /// public IPs.
    ///
    /// <para>
    /// Built on System.Net.Dns to avoid a new dependency: GetHostEntry on the
    /// domain returns the resolved A-record set plus, on most resolvers, the
    /// canonical hostname (which is the last name in the CNAME chain). When
    /// the resolver does not surface the canonical hostname, we also resolve
    /// the base domain's A-records and check whether the two A-record sets
    /// overlap — a positive overlap is a strong signal that the chain ends at
    /// the platform.
    /// </para>
    /// </summary>
    private async Task<(bool ok, string evidence)> CheckDnsPointsHereAsync(
        string domain, string baseDomain, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(baseDomain))
            return (false, "platform base domain not configured");

        IPHostEntry entry;
        try
        {
            entry = await Dns.GetHostEntryAsync(domain, ct);
        }
        catch (Exception ex)
        {
            return (false, $"DNS lookup failed: {ex.Message}");
        }

        if (entry.AddressList.Length == 0)
            return (false, "no A/AAAA records");

        // CNAME-chain evidence: GetHostEntry.HostName is the canonical name
        // after following CNAMEs. If it ends at the base domain, the chain
        // demonstrably terminates at the platform.
        var canonical = (entry.HostName ?? "").TrimEnd('.').ToLowerInvariant();
        if (canonical == baseDomain ||
            canonical.EndsWith("." + baseDomain, StringComparison.Ordinal))
        {
            return (true, $"CNAME → {canonical}");
        }

        // Apex fallback: resolve the base domain's own A-records and check
        // whether the verified domain's A-records intersect. An intersecting
        // A-record set means the domain ultimately resolves to platform IPs,
        // which only the DNS owner could arrange.
        try
        {
            var platformEntry = await Dns.GetHostEntryAsync(baseDomain, ct);
            var domainIps = entry.AddressList.Select(a => a.ToString()).ToHashSet();
            var platformIps = platformEntry.AddressList.Select(a => a.ToString()).ToHashSet();
            var overlap = domainIps.Intersect(platformIps).ToList();
            if (overlap.Count > 0)
            {
                return (true, $"A → {string.Join(",", overlap)} (matches {baseDomain})");
            }

            return (false,
                $"resolves to {string.Join(",", domainIps)}, expected CNAME → {baseDomain} " +
                $"or A → one of {string.Join(",", platformIps)}");
        }
        catch (Exception ex)
        {
            return (false, $"platform base-domain lookup failed: {ex.Message}");
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