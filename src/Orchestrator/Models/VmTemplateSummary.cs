using DeCloud.Shared.Enums;

namespace Orchestrator.Models;

/// <summary>
/// What a template LISTING needs — marketplace grid, featured strip, search
/// results. Deliberately not <see cref="VmTemplate"/>.
///
/// <para>
/// The list endpoints returned full domain objects, which meant every response
/// carried <c>CloudInitTemplate</c> — a multi-KB YAML body per template that no
/// listing renders. That pushed <c>GET /api/marketplace/templates</c> to ~1 MB,
/// large enough that the Mongo driver's socket read timed out mid-stream against
/// a remote cluster (~20s, so it failed intermittently rather than always).
/// </para>
///
/// <para>
/// This mirrors the decision already made for VMs: <c>GET /api/vms</c> returns
/// <c>VmSummaryDto</c>, never <c>VirtualMachine</c>, which is exactly why that
/// endpoint never shipped <c>CloudInitUserData</c> to a browser.
/// </para>
///
/// <para>
/// A projection alone would have fixed the payload, but not the contract: the
/// method would still have returned <c>List&lt;VmTemplate&gt;</c> with some
/// fields silently empty, and the next heavy field added to the domain object
/// would recreate the bug. A summary type is additive-by-default — new domain
/// fields don't leak unless someone deliberately adds them here.
/// </para>
///
/// <para>
/// Headline specs are FLATTENED rather than exposing <c>MinimumSpec</c> /
/// <c>RecommendedSpec</c> directly: those are <c>VmSpec</c>, which itself
/// carries <c>CloudInitUserData</c>, and embedding them would reintroduce the
/// same problem through a different door. Consumers needing the full spec or
/// the cloud-init body fetch one template via
/// <c>GET /api/marketplace/templates/{slugOrId}</c>.
/// </para>
/// </summary>
public record VmTemplateSummary
{
    // ── Identity ──────────────────────────────────────────────────────────
    public string Id { get; init; } = string.Empty;

    /// <summary>Used for deep links, e.g. /app/marketplace/{slug}/deploy.</summary>
    public string Slug { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public List<string> Tags { get; init; } = new();
    public string? IconUrl { get; init; }

    // ── Provenance / trust badges ─────────────────────────────────────────
    public string AuthorName { get; init; } = string.Empty;
    public bool IsCommunity { get; init; }
    public bool IsVerified { get; init; }
    public bool IsFeatured { get; init; }

    // ── Headline requirements (flattened — see remarks) ───────────────────
    public bool RequiresGpu { get; init; }
    public int RecommendedCpuCores { get; init; }
    public long RecommendedMemoryBytes { get; init; }
    public long RecommendedDiskBytes { get; init; }

    // ── Cost ──────────────────────────────────────────────────────────────
    public decimal EstimatedCostPerHour { get; init; }
    public TemplatePricingModel PricingModel { get; init; }
    public decimal TemplatePrice { get; init; }

    // ── Social proof ──────────────────────────────────────────────────────
    public int DeploymentCount { get; init; }
    public double AverageRating { get; init; }
    public int TotalReviews { get; init; }
}