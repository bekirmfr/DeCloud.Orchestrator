using MongoDB.Bson;
using MongoDB.Driver;
using Orchestrator.Interfaces;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Self-contained store for abuse reports. Owns "abuse_reports" and a per-year sequence in
/// "abuse_counters". Singleton, factory-registered so the nullable IMongoDatabase passes
/// through (mirrors WalletBlocklistService). Intake only — it never blocks or suspends.
/// </summary>
public sealed class AbuseReportService : IAbuseReportService
{
    private readonly IMongoCollection<AbuseReport>? _reports;
    private readonly IMongoCollection<BsonDocument>? _counters;
    private readonly ILogger<AbuseReportService> _logger;

    public AbuseReportService(IMongoDatabase? database, ILogger<AbuseReportService> logger)
    {
        _reports = database?.GetCollection<AbuseReport>("abuse_reports");
        _counters = database?.GetCollection<BsonDocument>("abuse_counters");
        _logger = logger;

        // Best-effort index — the admin queue (slice 2) reads open reports, most urgent first.
        try
        {
            _reports?.Indexes.CreateOne(new CreateIndexModel<AbuseReport>(
                Builders<AbuseReport>.IndexKeys
                    .Ascending(r => r.Status)
                    .Ascending(r => r.Priority)
                    .Ascending(r => r.CreatedAt)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure abuse_reports index");
        }
    }

    public bool Available => _reports != null;

    public async Task<AbuseReport> SubmitAsync(
        AbuseCategory category, string reportedResource, string description,
        string? targetWallet, string? reporterContact, CancellationToken ct = default)
    {
        if (_reports == null)
            throw new InvalidOperationException("Abuse reporting requires the database.");

        var (priority, sla) = AbuseTriage.Map[category];
        var year = DateTime.UtcNow.Year;
        var seq = await NextSequenceAsync(year, ct);

        var report = new AbuseReport
        {
            Reference = $"ABU-{year}-{seq:D5}",
            Category = category,
            Priority = priority,
            Sla = sla,
            ReportedResource = reportedResource,
            TargetWallet = targetWallet,
            Description = description,
            ReporterContact = reporterContact,
            Status = AbuseReportStatus.Open,
            CreatedAt = DateTime.UtcNow
        };

        await _reports.InsertOneAsync(report, cancellationToken: ct);
        _logger.LogInformation("Abuse report {Reference} filed: {Category} ({Priority})",
            report.Reference, category, priority);
        return report;
    }

    /// <summary>Atomic per-year sequence: one counter doc per year, $inc on each submit.</summary>
    private async Task<long> NextSequenceAsync(int year, CancellationToken ct)
    {
        var filter = Builders<BsonDocument>.Filter.Eq("_id", $"abuse-{year}");
        var update = Builders<BsonDocument>.Update.Inc("seq", 1L);
        var opts = new FindOneAndUpdateOptions<BsonDocument>
        {
            IsUpsert = true,
            ReturnDocument = ReturnDocument.After
        };
        var doc = await _counters!.FindOneAndUpdateAsync(filter, update, opts, ct);
        return doc["seq"].ToInt64();
    }
}