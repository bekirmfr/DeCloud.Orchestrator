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

        // Best-effort indexes — the admin queue reads open reports most urgent first, and
        // resolve looks a report up by its reference (also enforce reference uniqueness).
        try
        {
            _reports?.Indexes.CreateOne(new CreateIndexModel<AbuseReport>(
                Builders<AbuseReport>.IndexKeys
                    .Ascending(r => r.Status)
                    .Ascending(r => r.Priority)
                    .Ascending(r => r.CreatedAt)));
            _reports?.Indexes.CreateOne(new CreateIndexModel<AbuseReport>(
                Builders<AbuseReport>.IndexKeys.Ascending(r => r.Reference),
                new CreateIndexOptions { Name = "idx_abuse_reference", Unique = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ensure abuse_reports indexes");
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

    public async Task<List<AbuseReport>> GetOpenQueueAsync(int limit = 200, CancellationToken ct = default)
    {
        if (_reports == null) return new();
        return await _reports
            .Find(r => r.Status == AbuseReportStatus.Open)
            .Sort(Builders<AbuseReport>.Sort.Ascending(r => r.Priority).Ascending(r => r.CreatedAt))
            .Limit(limit)
            .ToListAsync(ct);
    }

    public async Task<AbuseReport?> GetByReferenceAsync(string reference, CancellationToken ct = default)
    {
        if (_reports == null) return null;
        return await _reports.Find(r => r.Reference == reference).FirstOrDefaultAsync(ct);
    }

    public async Task<AbuseReport?> ResolveAsync(string reference, AbuseReportStatus status,
        string resolvedBy, string note, CancellationToken ct = default)
    {
        if (_reports == null) return null;
        var filter = Builders<AbuseReport>.Filter.Eq(r => r.Reference, reference);
        var update = Builders<AbuseReport>.Update
            .Set(r => r.Status, status)
            .Set(r => r.ResolvedBy, resolvedBy)
            .Set(r => r.ResolvedAt, DateTime.UtcNow)
            .Set(r => r.ResolutionNote, note);
        var opts = new FindOneAndUpdateOptions<AbuseReport> { ReturnDocument = ReturnDocument.After };
        return await _reports.FindOneAndUpdateAsync(filter, update, opts, ct);
    }

    public async Task<AbuseReport?> AppendScanOrderedAsync(string reference, CsamScanRecord record,
        CancellationToken ct = default)
    {
        if (_reports == null) return null;
        var filter = Builders<AbuseReport>.Filter.Eq(r => r.Reference, reference);
        var update = Builders<AbuseReport>.Update.Push(r => r.ScanRecords, record);
        var opts = new FindOneAndUpdateOptions<AbuseReport> { ReturnDocument = ReturnDocument.After };
        return await _reports.FindOneAndUpdateAsync(filter, update, opts, ct);
    }

    public async Task<AbuseReport?> CompleteScanAsync(string commandId, CsamScanStatus status,
        string outcome, string? matcher, string? fileMap, string? error,
        CancellationToken ct = default)
    {
        if (_reports == null) return null;

        // The record lives inside a report's ScanRecords array, keyed by commandId. Match the
        // element and set its fields positionally. If nothing matches, the ack was for a scan
        // this store never recorded as Ordered — return null and let the caller log it.
        var filter = Builders<AbuseReport>.Filter.ElemMatch(
            r => r.ScanRecords, sr => sr.CommandId == commandId);
        var update = Builders<AbuseReport>.Update
            .Set("ScanRecords.$.Status", status)
            .Set("ScanRecords.$.Outcome", outcome)
            .Set("ScanRecords.$.Matcher", matcher)
            .Set("ScanRecords.$.FileMap", fileMap)
            .Set("ScanRecords.$.Error", error)
            .Set("ScanRecords.$.CompletedAt", DateTime.UtcNow);
        var opts = new FindOneAndUpdateOptions<AbuseReport> { ReturnDocument = ReturnDocument.After };
        return await _reports.FindOneAndUpdateAsync(filter, update, opts, ct);
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