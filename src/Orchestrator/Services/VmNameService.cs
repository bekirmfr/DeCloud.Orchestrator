using System.Text.RegularExpressions;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

public interface IVmNameService
{
    /// <summary>
    /// Sanitize raw user input into a DNS-safe base name.
    /// Lowercase, alphanumeric + hyphens, max 40 chars.
    /// </summary>
    string Sanitize(string raw);

    /// <summary>
    /// Validate a sanitized base name. Returns (true, null) on success,
    /// (false, errorMessage) on failure.
    /// </summary>
    (bool Valid, string? Error) Validate(string sanitized);

    /// <summary>
    /// Free-tier pipeline: sanitize → validate → append unique suffix → check per-owner uniqueness.
    /// Returns the canonical VM name (e.g., "my-awesome-vm-a1b2").
    /// For system VMs (userId == "system"), returns the name as-is (already well-formed).
    /// </summary>
    Task<(string? Name, string? Error)> GenerateCanonicalNameAsync(string rawName, string userId);

    /// <summary>
    /// Premium-tier pipeline: sanitize → validate → check global uniqueness (no suffix).
    /// Returns the exact name if available globally, or an error if taken.
    /// Used by the upgrade flow on the VM dashboard (not at creation time).
    /// </summary>
    Task<(string? Name, string? Error)> ClaimPremiumNameAsync(string rawName);

    /// <summary>
    /// Check if a name is available for premium claim (globally unique, not taken by any user).
    /// </summary>
    Task<(bool Available, string? SanitizedName)> CheckPremiumAvailabilityAsync(string rawName);
}

public partial class VmNameService : IVmNameService
{
    private const int MaxBaseLength = 40;
    private const int MinBaseLength = 2;
    private const int SuffixLength = 4;
    private const int MaxRetries = 5;

    private readonly DataStore _dataStore;
    private readonly ILogger<VmNameService> _logger;

    public VmNameService(DataStore dataStore, ILogger<VmNameService> logger)
    {
        _dataStore = dataStore;
        _logger = logger;
    }

    public string Sanitize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "vm";

        // Lowercase
        var result = raw.ToLowerInvariant();

        // Replace spaces and underscores with hyphens
        result = result.Replace(' ', '-').Replace('_', '-');

        // Remove anything that isn't alphanumeric or hyphen
        result = NonAlphanumericHyphen().Replace(result, "");

        // Collapse consecutive hyphens
        result = ConsecutiveHyphens().Replace(result, "-");

        // Trim leading/trailing hyphens
        result = result.Trim('-');

        // Truncate to max base length
        if (result.Length > MaxBaseLength)
            result = result[..MaxBaseLength].TrimEnd('-');

        return string.IsNullOrEmpty(result) ? "vm" : result;
    }

    public (bool Valid, string? Error) Validate(string sanitized)
    {
        if (string.IsNullOrEmpty(sanitized))
            return (false, "VM name is required");

        if (sanitized.Length < MinBaseLength)
            return (false, $"VM name must be at least {MinBaseLength} characters");

        if (sanitized.Length > MaxBaseLength)
            return (false, $"VM name must be at most {MaxBaseLength} characters");

        if (!char.IsLetter(sanitized[0]))
            return (false, "VM name must start with a letter");

        if (sanitized.EndsWith('-'))
            return (false, "VM name must not end with a hyphen");

        if (!ValidNamePattern().IsMatch(sanitized))
            return (false, "VM name must contain only lowercase letters, numbers, and hyphens");

        return (true, null);
    }

    // =========================================================================
    // Free Tier: sanitize → validate → append unique suffix → per-owner check
    // =========================================================================

    public async Task<(string? Name, string? Error)> GenerateCanonicalNameAsync(string rawName, string userId)
    {
        // System VMs (relay, DHT) already have well-formed names — pass through
        if (userId == "system")
            return (rawName, null);

        var sanitized = Sanitize(rawName);
        var (valid, error) = Validate(sanitized);
        if (!valid)
            return (null, error);

        // Free tier: append unique suffix and check per-owner uniqueness
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var suffix = GenerateSuffix();
            var candidate = $"{sanitized}-{suffix}";

            var exists = await NameExistsForOwnerAsync(candidate, userId);
            if (!exists)
            {
                _logger.LogDebug(
                    "Generated free-tier VM name: {Raw} → {Canonical} (attempt {Attempt})",
                    rawName, candidate, attempt + 1);
                return (candidate, null);
            }
        }

        // Extremely unlikely: all retries collided. Use a longer suffix.
        var fallbackSuffix = Guid.NewGuid().ToString("N")[..8];
        var fallback = $"{sanitized}-{fallbackSuffix}";
        _logger.LogWarning(
            "All {MaxRetries} suffix attempts collided for name {Name}, using 8-char fallback: {Fallback}",
            MaxRetries, sanitized, fallback);
        return (fallback, null);
    }

    // =========================================================================
    // Premium Tier: sanitize → validate → global uniqueness check (no suffix)
    // =========================================================================

    public async Task<(string? Name, string? Error)> ClaimPremiumNameAsync(string rawName)
    {
        var sanitized = Sanitize(rawName);
        var (valid, error) = Validate(sanitized);
        if (!valid)
            return (null, error);

        // Premium tier: use exact name, enforce global uniqueness
        var globallyTaken = await NameExistsGloballyAsync(sanitized);
        if (globallyTaken)
        {
            _logger.LogInformation(
                "Premium name claim rejected — name already taken globally: {Name}",
                sanitized);
            return (null, $"The subdomain \"{sanitized}\" is already taken. Please choose a different name.");
        }

        _logger.LogInformation("Premium name claimed: {Raw} → {Name}", rawName, sanitized);
        return (sanitized, null);
    }

    public async Task<(bool Available, string? SanitizedName)> CheckPremiumAvailabilityAsync(string rawName)
    {
        var sanitized = Sanitize(rawName);
        var (valid, _) = Validate(sanitized);
        if (!valid)
            return (false, sanitized);

        var taken = await NameExistsGloballyAsync(sanitized);
        return (!taken, sanitized);
    }

    // =========================================================================
    // Uniqueness Checks
    // =========================================================================

    private static string GenerateSuffix()
    {
        // 4 hex chars from a fresh GUID — 65,536 possibilities per base name
        return Guid.NewGuid().ToString("N")[..SuffixLength];
    }

    /// <summary>
    /// Per-owner uniqueness check (used by free tier).
    /// Two different users can have the same suffixed name — the suffix makes collisions unlikely anyway.
    /// </summary>
    private async Task<bool> NameExistsForOwnerAsync(string canonicalName, string ownerId)
    {
        // Check in-memory active VMs first (fast path)
        var activeMatch = _dataStore.ActiveVMs.Values
            .Any(vm => vm.Name == canonicalName
                       && vm.OwnerId == ownerId
                       && vm.Status != VmStatus.Deleted);

        if (activeMatch)
            return true;

        // Check MongoDB for non-active VMs (stopped, error, etc.)
        var allVms = await _dataStore.GetVmsByUserAsync(ownerId);
        return allVms.Any(vm => vm.Name == canonicalName && vm.Status != VmStatus.Deleted);
    }

    /// <summary>
    /// Global uniqueness check (used by premium tier).
    /// No two VMs across any user may share a premium name — required for clean DNS subdomains.
    /// </summary>
    private async Task<bool> NameExistsGloballyAsync(string name)
    {
        // Check in-memory active VMs first (fast path)
        var activeMatch = _dataStore.ActiveVMs.Values
            .Any(vm => vm.Name == name && vm.Status != VmStatus.Deleted);

        if (activeMatch)
            return true;

        // Check MongoDB for all VMs across all users
        var allVms = await _dataStore.GetAllVMsAsync();
        return allVms.Any(vm => vm.Name == name && vm.Status != VmStatus.Deleted);
    }

    [GeneratedRegex(@"[^a-z0-9-]")]
    private static partial Regex NonAlphanumericHyphen();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex ConsecutiveHyphens();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*[a-z0-9]$")]
    private static partial Regex ValidNamePattern();
}
