using Orchestrator.Models;

namespace Orchestrator.Persistence;

/// <summary>
/// Extension methods for DataStore to handle attestation data.
/// Add these methods to the existing DataStore class.
/// </summary>
public static class DataStoreAttestationExtensions
{
    // In-memory cache for recent attestation records
    // In production, these would be stored in MongoDB
    private static readonly Dictionary<string, List<AttestationRecord>> _attestationRecords = new();
    private static readonly object _recordsLock = new();

    /// <summary>
    /// Save an attestation record for audit trail
    /// </summary>
    public static Task SaveAttestationRecordAsync(this DataStore dataStore, AttestationRecord record)
    {
        lock (_recordsLock)
        {
            if (!_attestationRecords.TryGetValue(record.VmId, out var records))
            {
                records = new List<AttestationRecord>();
                _attestationRecords[record.VmId] = records;
            }

            records.Add(record);

            // Keep only last 1000 records per VM
            if (records.Count > 1000)
            {
                records.RemoveAt(0);
            }
        }

        // In production, also persist to MongoDB:
        // await dataStore.MongoAttestationRecords.InsertOneAsync(record);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Get attestation records for a VM
    /// </summary>
    public static Task<List<AttestationRecord>> GetAttestationRecordsAsync(
        this DataStore dataStore,
        string vmId,
        int limit = 100,
        DateTime? since = null)
    {
        lock (_recordsLock)
        {
            if (!_attestationRecords.TryGetValue(vmId, out var records))
            {
                return Task.FromResult(new List<AttestationRecord>());
            }

            var query = records.AsEnumerable();

            if (since.HasValue)
            {
                query = query.Where(r => r.Timestamp >= since.Value);
            }

            return Task.FromResult(query
                .OrderByDescending(r => r.Timestamp)
                .Take(limit)
                .ToList());
        }
    }

    /// <summary>
    /// Get aggregated attestation stats for a VM
    /// </summary>
    public static Task<AttestationAggregateStats> GetAttestationStatsAsync(
        this DataStore dataStore,
        string vmId,
        DateTime? since = null)
    {
        lock (_recordsLock)
        {
            if (!_attestationRecords.TryGetValue(vmId, out var records))
            {
                return Task.FromResult(new AttestationAggregateStats { VmId = vmId });
            }

            var query = records.AsEnumerable();

            if (since.HasValue)
            {
                query = query.Where(r => r.Timestamp >= since.Value);
            }

            var list = query.ToList();

            if (list.Count == 0)
            {
                return Task.FromResult(new AttestationAggregateStats { VmId = vmId });
            }

            var successful = list.Where(r => r.Success).ToList();

            return Task.FromResult(new AttestationAggregateStats
            {
                VmId = vmId,
                TotalChallenges = list.Count,
                SuccessfulChallenges = successful.Count,
                FailedChallenges = list.Count - successful.Count,
                SuccessRate = list.Count > 0 ? (double)successful.Count / list.Count * 100.0 : 0,
                AverageResponseTimeMs = successful.Count > 0
                    ? successful.Average(r => r.ResponseTimeMs)
                    : 0,
                MinResponseTimeMs = successful.Count > 0
                    ? successful.Min(r => r.ResponseTimeMs)
                    : 0,
                MaxResponseTimeMs = successful.Count > 0
                    ? successful.Max(r => r.ResponseTimeMs)
                    : 0,
                FirstAttestation = list.Min(r => r.Timestamp),
                LastAttestation = list.Max(r => r.Timestamp),
                CommonErrors = list
                    .Where(r => !r.Success)
                    .SelectMany(r => r.Errors)
                    .GroupBy(e => e)
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => new ErrorCount { Error = g.Key, Count = g.Count() })
                    .ToList()
            });
        }
    }

    /// <summary>
    /// Clean up old attestation records
    /// </summary>
    public static Task CleanupOldAttestationRecordsAsync(
        this DataStore dataStore,
        TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;

        lock (_recordsLock)
        {
            foreach (var vmId in _attestationRecords.Keys.ToList())
            {
                var records = _attestationRecords[vmId];
                records.RemoveAll(r => r.Timestamp < cutoff);

                if (records.Count == 0)
                {
                    _attestationRecords.Remove(vmId);
                }
            }
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Aggregated attestation statistics
/// </summary>
public class AttestationAggregateStats
{
    public string VmId { get; set; } = string.Empty;
    public int TotalChallenges { get; set; }
    public int SuccessfulChallenges { get; set; }
    public int FailedChallenges { get; set; }
    public double SuccessRate { get; set; }
    public double AverageResponseTimeMs { get; set; }
    public double MinResponseTimeMs { get; set; }
    public double MaxResponseTimeMs { get; set; }
    public DateTime? FirstAttestation { get; set; }
    public DateTime? LastAttestation { get; set; }
    public List<ErrorCount> CommonErrors { get; set; } = new();
}

public class ErrorCount
{
    public string Error { get; set; } = string.Empty;
    public int Count { get; set; }
}