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
    /// Full pipeline: sanitize → validate → append unique suffix → check uniqueness.
    /// Returns the canonical VM name (e.g., "my-awesome-vm-a1b2").
    /// For system VMs (userId == "system"), returns the name as-is (already well-formed).
    /// </summary>
    Task<(string? Name, string? Error)> GenerateCanonicalNameAsync(string rawName, string userId);
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

    public async Task<(string? Name, string? Error)> GenerateCanonicalNameAsync(string rawName, string userId)
    {
        // System VMs (relay, DHT) already have well-formed names — pass through
        if (userId == "system")
            return (rawName, null);

        var sanitized = Sanitize(rawName);
        var (valid, error) = Validate(sanitized);
        if (!valid)
            return (null, error);

        // Try to generate a unique name with suffix
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            var suffix = GenerateSuffix();
            var candidate = $"{sanitized}-{suffix}";

            var exists = await NameExistsForOwnerAsync(candidate, userId);
            if (!exists)
            {
                _logger.LogDebug(
                    "Generated canonical VM name: {Raw} → {Canonical} (attempt {Attempt})",
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

    private static string GenerateSuffix()
    {
        // 4 hex chars from a fresh GUID — 65,536 possibilities per base name
        return Guid.NewGuid().ToString("N")[..SuffixLength];
    }

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

    [GeneratedRegex(@"[^a-z0-9-]")]
    private static partial Regex NonAlphanumericHyphen();

    [GeneratedRegex(@"-{2,}")]
    private static partial Regex ConsecutiveHyphens();

    [GeneratedRegex(@"^[a-z][a-z0-9-]*[a-z0-9]$")]
    private static partial Regex ValidNamePattern();
}
