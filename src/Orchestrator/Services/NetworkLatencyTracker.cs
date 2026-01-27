using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Diagnostics;
using System.Net.NetworkInformation;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Orchestrator.Services;

/// <summary>
/// Network latency tracker for VMs - CGNAT-aware version
/// Handles both public IP nodes and CGNAT nodes correctly
/// </summary>
public interface INetworkLatencyTracker
{
    /// <summary>
    /// Calibrate baseline RTT for a new VM
    /// </summary>
    Task<double> CalibrateBaselineRttAsync(string vmId, CancellationToken ct = default);

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
    Task RecalibrateAsync(string vmId, CancellationToken ct = default);

    /// <summary>
    /// Measure current RTT to a VM (CGNAT-aware)
    /// </summary>
    Task<double> MeasureRttAsync(string vmId, CancellationToken ct = default);
}

public class NetworkLatencyTracker : INetworkLatencyTracker
{
    private readonly DataStore _dataStore;
    private readonly ILogger<NetworkLatencyTracker> _logger;
    private readonly HttpClient _httpClient;

    // Configuration
    private const int INITIAL_CALIBRATION_PINGS = 5;
    private const double SMOOTHING_FACTOR = 0.3;
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
    /// CGNAT-AWARE: Uses NodeAgent endpoint for CGNAT VMs
    /// </summary>
    public async Task<double> CalibrateBaselineRttAsync(string vmId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found for RTT calibration", vmId);
            return DEFAULT_RTT_MS;
        }

        _logger.LogInformation("Calibrating baseline RTT for VM {VmId}", vmId);

        var measurements = new List<double>();

        for (int i = 0; i < INITIAL_CALIBRATION_PINGS; i++)
        {
            try
            {
                var rtt = await MeasureRttAsync(vmId, ct);
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
                "Failed to calibrate RTT for VM {VmId} (only {Count} successful measurements), using default {Default}ms",
                vmId, measurements.Count, DEFAULT_RTT_MS);
            return DEFAULT_RTT_MS;
        }

        // Use median to avoid outliers
        measurements.Sort();
        var median = measurements[measurements.Count / 2];

        _logger.LogInformation(
            "Baseline RTT calibrated for VM {VmId}: {Median:F1}ms (min: {Min:F1}ms, max: {Max:F1}ms, samples: {Count})",
            vmId, median, measurements.Min(), measurements.Max(), measurements.Count);

        return median;
    }

    /// <summary>
    /// Measure RTT to a VM - CGNAT-AWARE
    /// For public nodes: pings VM directly
    /// For CGNAT nodes: pings NodeAgent (since VM is not directly reachable)
    /// </summary>
    public async Task<double> MeasureRttAsync(string vmId, CancellationToken ct = default)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            throw new InvalidOperationException($"VM {vmId} not found");
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            throw new InvalidOperationException($"VM {vmId} has no assigned node");
        }

        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (node == null)
        {
            throw new InvalidOperationException($"Node {vm.NodeId} not found");
        }

        // =====================================================
        // CRITICAL: Determine reachable endpoint based on node type
        // =====================================================

        string targetHost;
        int targetPort;
        string targetDescription;

        var nodeAgentUrl = GetNodeAgentUrl(node);

        // =====================================================
        // Measure RTT using HTTP health check
        // =====================================================
        try
        {
            var rtt = await MeasureHttpRttAsync(nodeAgentUrl, vm.Id, ct);

            _logger.LogDebug(
                "RTT measured for VM {VmId} ({Target}): {Rtt:F1}ms",
                vmId, nodeAgentUrl, rtt);

            return rtt;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                "HTTP RTT measurement failed for VM {VmId} ({Target}): {Error}, trying ICMP",
                vmId, nodeAgentUrl, ex.Message);

            targetHost = node.CgnatInfo.TunnelIp ?? node.PublicIp;

            // Fallback to ICMP ping
            return await MeasureIcmpRttAsync(targetHost, ct);
        }
    }

    /// <summary>
    /// Get NodeAgent URL for proxying requests
    /// </summary>
    private string? GetNodeAgentUrl(Node node)
    {
        if (string.IsNullOrEmpty(node.PublicIp))
        {
            return null;
        }

        var port = node.AgentPort > 0 ? node.AgentPort : 5100;

        // For CGNAT nodes, use tunnel IP if available
        if (node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
        {
            return $"http://{node.CgnatInfo.TunnelIp}:{port}";
        }

        // Regular nodes: use public IP
        return $"http://{node.PublicIp}:{port}";
    }

    /// <summary>
    /// Measure RTT using HTTP GET to health endpoint
    /// More realistic than ICMP as it includes TCP handshake
    /// </summary>
    private async Task<double> MeasureHttpRttAsync(
    string nodeAgentUrl,
    string vmId,
    CancellationToken ct)
    {
        var url = $"{nodeAgentUrl}/api/vms/{vmId}/proxy/http/9999/health";

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var reasonPhrase = response.ReasonPhrase ?? "Unknown";

                // 504 Gateway Timeout = NodeAgent proxy timeout (150ms is too short!)
                if (statusCode == 504)
                {
                    _logger.LogWarning(
                        "NodeAgent proxy timeout (504) for VM {VmId} - VM may be slow to respond or proxy timeout too short",
                        vmId);
                }

                throw new HttpRequestException($"HTTP {statusCode} {reasonPhrase}");
            }

            return stopwatch.Elapsed.TotalMilliseconds;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            _logger.LogDebug(
                "HTTP request timeout after {Elapsed}ms for VM {VmId} - HttpClient timeout exceeded",
                stopwatch.Elapsed.TotalMilliseconds, vmId);
            throw new Exception($"HTTP timeout after {stopwatch.Elapsed.TotalMilliseconds:F0}ms", ex);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogDebug(
                "HTTP RTT measurement failed after {Elapsed}ms for {HostUrl}:{VmId}: {Error}",
                stopwatch.Elapsed.TotalMilliseconds, nodeAgentUrl, vmId, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Fallback: Measure RTT using ICMP ping
    /// </summary>
    private async Task<double> MeasureIcmpRttAsync(string host, CancellationToken ct)
    {
        using var ping = new Ping();

        try
        {
            var reply = await ping.SendPingAsync(host, 2000);

            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }

            throw new Exception($"Ping failed: {reply.Status}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("ICMP RTT measurement failed for {Host}: {Error}", host, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Get network metrics for a VM
    /// </summary>
    public async Task<VmNetworkMetrics?> GetMetricsAsync(string vmId)
    {
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            return null;
        }

        return vm.NetworkMetrics;
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
        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found, cannot update network metrics", vmId);
            return;
        }

        if (vm.NetworkMetrics == null)
        {
            _logger.LogWarning("VM {VmId} has no network metrics initialized", vmId);
            return;
        }

        var metrics = vm.NetworkMetrics;

        // Update moving average of RTT (exponential smoothing)
        metrics.CurrentRttMs = (SMOOTHING_FACTOR * networkRttMs) +
                               ((1 - SMOOTHING_FACTOR) * metrics.CurrentRttMs);

        // Update average processing time
        metrics.AvgProcessingTimeMs = (SMOOTHING_FACTOR * processingTimeMs) +
                                      ((1 - SMOOTHING_FACTOR) * metrics.AvgProcessingTimeMs);

        // Add to recent measurements
        metrics.RecentMeasurements.Add(new RttMeasurement
        {
            Timestamp = DateTime.UtcNow,
            RttMs = networkRttMs,
            ProcessingTimeMs = processingTimeMs,
            WasSuccessful = success
        });

        // Keep only last N measurements
        if (metrics.RecentMeasurements.Count > MAX_RECENT_MEASUREMENTS)
        {
            metrics.RecentMeasurements.RemoveAt(0);
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

        // Save the entire VM (metrics are embedded)
        await _dataStore.SaveVmAsync(vm);

        _logger.LogDebug(
            "Updated metrics for VM {VmId}: RTT={Rtt:F1}ms, Processing={Proc:F1}ms, Success={Success}",
            vmId, networkRttMs, processingTimeMs, success);
    }

    /// <summary>
    /// Re-calibrate baseline RTT (called when significant changes detected)
    /// </summary>
    public async Task RecalibrateAsync(string vmId, CancellationToken ct = default)
    {
        _logger.LogInformation("Re-calibrating baseline RTT for VM {VmId}", vmId);

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null)
        {
            _logger.LogWarning("VM {VmId} not found, cannot recalibrate", vmId);
            return;
        }

        if (vm.NetworkMetrics == null)
        {
            _logger.LogWarning("VM {VmId} has no network metrics to recalibrate", vmId);
            return;
        }

        var newBaseline = await CalibrateBaselineRttAsync(vmId, ct);

        var oldBaseline = vm.NetworkMetrics.BaselineRttMs;
        vm.NetworkMetrics.BaselineRttMs = newBaseline;
        vm.NetworkMetrics.CurrentRttMs = newBaseline;
        vm.NetworkMetrics.LastCalibrationAt = DateTime.UtcNow;

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "Re-calibrated baseline RTT for VM {VmId}: {Old:F1}ms → {New:F1}ms",
            vmId, oldBaseline, newBaseline);
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