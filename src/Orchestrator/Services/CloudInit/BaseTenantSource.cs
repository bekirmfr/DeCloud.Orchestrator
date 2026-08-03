using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace DeCloud.Orchestrator.Services.CloudInit;

/// <summary>
/// Supplies the verbatim <c>base-tenant.yaml</c> content that the authored-
/// template compose path (create/update) merges role layers over.
///
/// <para>
/// Fetched once from <c>DeCloud.Builds/{ref}/base-templates/base-tenant.yaml</c>
/// and memoized for the process lifetime — the same freshness model the seeders
/// use (base is pinned at startup; a base change propagates on the next restart,
/// via the Phase-3 re-compose pass). Deliberately minimal: no persisted cache,
/// no TTL, no content hash. Composition is deploy-invariant and happens upstream
/// at create/update, so this never sits on the deploy hot path.
/// </para>
///
/// <para>
/// Fail-closed: if the first fetch fails (Builds unreachable), <see cref="GetAsync"/>
/// throws and the authoring save fails — the correct boundary, since a template
/// cannot be composed without its base. Once fetched, the memo serves every
/// subsequent compose for the process.
/// </para>
/// </summary>
public interface IBaseTenantSource
{
    Task<string> GetAsync(CancellationToken ct);
}

public sealed class BaseTenantSource : IBaseTenantSource
{
    // Kept in step with the seeders' CloudInitRef. When a base-v* tag stream is
    // adopted (documented upgrade path), this ref and the seeders' move together.
    private const string CloudInitRef = "main";
    private const string BaseTenantUrl =
        "https://raw.githubusercontent.com/bekirmfr/DeCloud.Builds/" +
        CloudInitRef + "/base-templates/base-tenant.yaml";

    private readonly HttpClient _http;
    private readonly ILogger<BaseTenantSource> _logger;
    private volatile string? _memo;

    public BaseTenantSource(HttpClient http, ILogger<BaseTenantSource> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<string> GetAsync(CancellationToken ct)
    {
        var cached = _memo;
        if (cached is not null) return cached;

        // A concurrent first-call race fetches twice and last-writer-wins — both
        // fetch identical content, so it's harmless and not worth locking for.
        _logger.LogDebug("Fetching base-tenant from {Url}", BaseTenantUrl);
        var fetched = await _http.GetStringAsync(BaseTenantUrl, ct);
        _memo = fetched;
        return fetched;
    }
}
