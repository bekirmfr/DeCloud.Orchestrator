using System.Diagnostics;
using System.Net.NetworkInformation;
using Orchestrator.Models;
using Orchestrator.Persistence;

namespace Orchestrator.Services;

/// <summary>
/// Network latency tracker for VMs
/// Measures and tracks RTT to calculate adaptive attestation timeouts
/// </summary>
public interface INetworkLatencyTracker
{
    /// <summary>
    /// Calibrate baseline RTT for a new VM
    /// </summary>
    Task<double> CalibrateBaselineRttAsync(string vmIp, CancellationToken ct = default);

    /// <summary>
    /// Get network metrics for a VM
    /// </summary>
    Task<VmNetworkMetrics?> GetMetricsAsync(string vmId);

    /// <summary>
    /// Update metrics after an attestation
    /// </summary>
    Task UpdateMetricsAsync(string vmId, double networkRttMs, double processingTimeMs, bool success);

    /// <summary>
    /// Re-calibrate baseline RTT
    /// </summary>
    Task RecalibrateAsync(string vmId, string vmIp, CancellationToken ct = default);

    /// <summary>
    /// Measure current RTT to a VM
    /// </summary>
    Task<double> MeasureRttAsync(string vmIp, CancellationToken ct = default);
}

public class NetworkLatencyTracker : INetworkLatencyTracker
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NetworkLatencyTracker> _logger;
    private readonly HttpClient _httpClient;

    // In-memory cache of network metrics (also persisted to DB)
    private readonly Dictionary<string, VmNetworkMetrics> _metricsCache = new();
    private readonly SemaphoreSlim _cacheLock = new(1);

    // Configuration
    private const int INITIAL_CALIBRATION_PINGS = 5;
    private const double SMOOTHING_FACTOR = 0.3; // For exponential moving average
    private const int MAX_RECENT_MEASUREMENTS = 10;
    private const double DEFAULT_RTT_MS = 50.0;

    public NetworkLatencyTracker(
        DataStore dataStore,
        ILogger<NetworkLatencyTracker> logger,
        IHttpClientFactory httpClientFactory)
    {
        _dataStore = dataStore;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("latency-tracker");
        _httpClient.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Calibrate baseline RTT during VM creation
    /// Takes median of 5 ping measurements to avoid outliers
    /// </summary>
    public async Task<double> CalibrateBaselineRttAsync(string vmIp, CancellationToken ct = default)
    {
        _logger.LogInformation("Calibrating baseline RTT for VM at {VmIp}", vmIp);

        var measurements = new List<double>();

        for (int i = 0; i < INITIAL_CALIBRATION_PINGS; i++)
        {
            try
            {
                var rtt = await MeasureRttAsync(vmIp, ct);
                measurements.Add(rtt);

                _logger.LogDebug("RTT measurement {Index}/{Total}: {Rtt:F1}ms",
                    i + 1, INITIAL_CALIBRATION_PINGS, rtt);

                // Wait between measurements to avoid congestion
                if (i < INITIAL_CALIBRATION_PINGS - 1)
                    await Task.Delay(1000, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed RTT measurement {Index}/{Total}",
                    i + 1, INITIAL_CALIBRATION_PINGS);
            }
        }

        if (measurements.Count < 3)
        {
            _logger.LogWarning(
                "Failed to calibrate RTT for {VmIp} (only {Count} successful measurements), using default {Default}ms",
                vmIp, measurements.Count, DEFAULT_RTT_MS);
            return DEFAULT_RTT_MS;
        }

        // Use median to avoid outliers
        measurements.Sort();
        var median = measurements[measurements.Count / 2];

        _logger.LogInformation(
            "Baseline RTT calibrated for {VmIp}: {Median:F1}ms (min: {Min:F1}ms, max: {Max:F1}ms, samples: {Count})",
            vmIp, median, measurements.Min(), measurements.Max(), measurements.Count);

        return median;
    }

    /// <summary>
    /// Measure RTT to a VM using lightweight HTTP health check
    /// </summary>
    public async Task<double> MeasureRttAsync(string vmIp, CancellationToken ct = default)
    {
        // Try HTTP health check first (preferred - more realistic)
        try
        {
            return await MeasureHttpRttAsync(vmIp, ct);
        }
        catch
        {
            // Fallback to ICMP ping
            return await MeasureIcmpRttAsync(vmIp, ct);
        }
    }

    /// <summary>
    /// Measure RTT using HTTP GET to health endpoint
    /// More realistic than ICMP as it includes TCP handshake
    /// </summary>
    private async Task<double> MeasureHttpRttAsync(string vmIp, CancellationToken ct)
    {
        var url = $"http://{vmIp}:9999/health"; // Attestation agent health endpoint

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            stopwatch.Stop();

            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("HTTP RTT measurement failed for {VmIp}: {Error}", vmIp, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Fallback: Measure RTT using ICMP ping
    /// </summary>
    private async Task<double> MeasureIcmpRttAsync(string vmIp, CancellationToken ct)
    {
        using var ping = new Ping();

        try
        {
            var reply = await ping.SendPingAsync(vmIp, 5000);

            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }

            throw new Exception($"Ping failed: {reply.Status}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("ICMP RTT measurement failed for {VmIp}: {Error}", vmIp, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get or create network metrics for a VM
    /// </summary>
    public async Task<VmNetworkMetrics?> GetMetricsAsync(string vmId)
    {
        await _cacheLock.WaitAsync();
        try
        {
            // Check cache first
            if (_metricsCache.TryGetValue(vmId, out var cached))
            {
                return cached;
            }

            // Load from database
            if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
                {
                _logger.LogWarning("VM {VmId} not found in datastore", vmId);
                return null;
            }
            var metrics = vm.NetworkMetrics;

            if (metrics != null)
            {
                _metricsCache[vmId] = metrics;
            }

            return metrics;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Update metrics after an attestation
    /// </summary>
    public async Task UpdateMetricsAsync(
        string vmId,
        double networkRttMs,
        double processingTimeMs,
        bool success)
    {
        await _cacheLock.WaitAsync();
        try
        {
            var metrics = await GetMetricsAsync(vmId);

            if (metrics == null)
            {
                _logger.LogWarning("No network metrics found for VM {VmId}, cannot update", vmId);
                return;
            }

            // Update moving average of RTT (exponential smoothing)
            metrics.CurrentRttMs = (SMOOTHING_FACTOR * networkRttMs) +
                                   ((1 - SMOOTHING_FACTOR) * metrics.CurrentRttMs);

            // Update average processing time
            metrics.AvgProcessingTimeMs = (SMOOTHING_FACTOR * processingTimeMs) +
                                          ((1 - SMOOTHING_FACTOR) * metrics.AvgProcessingTimeMs);

            // Add to recent measurements
            metrics.RecentMeasurements.Enqueue(new RttMeasurement
            {
                Timestamp = DateTime.UtcNow,
                RttMs = networkRttMs,
                ProcessingTimeMs = processingTimeMs,
                WasSuccessful = success
            });

            // Keep only last N measurements
            while (metrics.RecentMeasurements.Count > MAX_RECENT_MEASUREMENTS)
            {
                metrics.RecentMeasurements.Dequeue();
            }

            // Update statistics
            var recentRtts = metrics.RecentMeasurements.Select(m => m.RttMs).ToList();
            if (recentRtts.Any())
            {
                metrics.MinRttMs = recentRtts.Min();
                metrics.MaxRttMs = recentRtts.Max();
                metrics.StdDevRttMs = CalculateStdDev(recentRtts);
            }

            metrics.TotalMeasurements++;
            metrics.UpdatedAt = DateTime.UtcNow;

            // Update cache and persist
            _metricsCache[vmId] = metrics;
            if(!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
            {
                _logger.LogWarning("VM {VmId} not found in datastore", vmId);
                return;
            }
            vm.NetworkMetrics = metrics;
            await _dataStore.SaveVmAsync(vm);

            _logger.LogDebug(
                "Updated metrics for VM {VmId}: RTT={Rtt:F1}ms, Processing={Proc:F1}ms, Success={Success}",
                vmId, networkRttMs, processingTimeMs, success);
        }
        catch
        {
            _logger.LogError("Failed to update network metrics for VM {VmId}", vmId);
            throw;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Re-calibrate baseline RTT (called when significant changes detected)
    /// </summary>
    public async Task RecalibrateAsync(string vmId, string vmIp, CancellationToken ct = default)
    {
        _logger.LogInformation("Re-calibrating baseline RTT for VM {VmId}", vmId);

        var newBaseline = await CalibrateBaselineRttAsync(vmIp, ct);

        await _cacheLock.WaitAsync(ct);
        try
        {
            var metrics = await GetMetricsAsync(vmId);

            if (metrics != null)
            {
                var oldBaseline = metrics.BaselineRttMs;
                metrics.BaselineRttMs = newBaseline;
                metrics.CurrentRttMs = newBaseline;
                metrics.LastCalibrationAt = DateTime.UtcNow;

                _metricsCache[vmId] = metrics;

                if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
                {
                    _logger.LogWarning("VM {VmId} not found in datastore", vmId);
                    return;
                }
                vm.NetworkMetrics = metrics;
                await _dataStore.SaveVmAsync(vm);

                _logger.LogInformation(
                    "Re-calibrated baseline RTT for VM {VmId}: {Old:F1}ms → {New:F1}ms",
                    vmId, oldBaseline, newBaseline);
            }
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    /// <summary>
    /// Calculate standard deviation of RTT measurements
    /// </summary>
    private double CalculateStdDev(List<double> values)
    {
        if (values.Count < 2)
            return 0;

        var avg = values.Average();
        var sumOfSquares = values.Sum(v => Math.Pow(v - avg, 2));
        return Math.Sqrt(sumOfSquares / values.Count);
    }
}