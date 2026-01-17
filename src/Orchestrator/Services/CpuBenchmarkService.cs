using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Orchestrator.Services;

/// <summary>
/// CPU benchmarking service for measuring node performance
/// </summary>
public interface ICpuBenchmarkService
{
    Task<BenchmarkResult> RunBenchmarkAsync(CancellationToken ct = default);
}

public class CpuBenchmarkService : ICpuBenchmarkService
{
    private readonly ILogger<CpuBenchmarkService> _logger;

    public CpuBenchmarkService(ILogger<CpuBenchmarkService> logger)
    {
        _logger = logger;
    }

    public async Task<BenchmarkResult> RunBenchmarkAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting CPU benchmark (30 seconds)...");

        var startTime = DateTime.UtcNow;

        try
        {
            // Try sysbench first (preferred for Linux)
            var sysbenchResult = await TrySysbenchAsync(ct);
            if (sysbenchResult != null)
            {
                _logger.LogInformation(
                    "Sysbench completed: {Score} score in {Duration:F1}s",
                    sysbenchResult.Score,
                    (DateTime.UtcNow - startTime).TotalSeconds);
                return sysbenchResult;
            }

            // Fallback to custom benchmark
            _logger.LogInformation("Sysbench not available, using custom benchmark...");
            var customResult = await RunCustomBenchmarkAsync(ct);

            _logger.LogInformation(
                "Custom benchmark completed: {Score} score in {Duration:F1}s",
                customResult.Score,
                (DateTime.UtcNow - startTime).TotalSeconds);

            return customResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Benchmark failed, using minimum score");
            return new BenchmarkResult
            {
                Score = 500, // Conservative fallback
                Method = "error-fallback",
                Duration = DateTime.UtcNow - startTime,
                Error = ex.Message
            };
        }
    }

    private async Task<BenchmarkResult?> TrySysbenchAsync(CancellationToken ct)
    {
        try
        {
            var startTime = DateTime.UtcNow;

            // Run sysbench CPU test (prime number calculation)
            var psi = new ProcessStartInfo
            {
                FileName = "sysbench",
                Arguments = "cpu --cpu-max-prime=20000 --threads=1 --time=15 run",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
                return null;

            // Parse events per second
            var match = Regex.Match(output, @"events per second:\s+([\d.]+)");
            if (!match.Success)
                return null;

            if (!double.TryParse(match.Groups[1].Value, out var eventsPerSecond))
                return null;

            // Normalize to our scoring scale
            // Baseline: ~200 events/sec = 1000 score
            var score = (int)(eventsPerSecond * 5);

            return new BenchmarkResult
            {
                Score = score,
                Method = "sysbench",
                Duration = DateTime.UtcNow - startTime,
                RawMetric = eventsPerSecond,
                Details = $"Sysbench events/sec: {eventsPerSecond:F2}"
            };
        }
        catch
        {
            return null;
        }
    }

    private Task<BenchmarkResult> RunCustomBenchmarkAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var startTime = DateTime.UtcNow;
            var stopwatch = Stopwatch.StartNew();

            // Run single-threaded prime calculation benchmark
            int primeCount = 0;
            const int maxNumber = 100000;

            for (int n = 2; n < maxNumber && !ct.IsCancellationRequested; n++)
            {
                if (IsPrime(n))
                    primeCount++;
            }

            stopwatch.Stop();

            // Calculate score based on primes found per second
            // Baseline: ~5000 primes in 1 second = 1000 score
            var primesPerSecond = primeCount / stopwatch.Elapsed.TotalSeconds;
            var score = (int)(primesPerSecond / 5);

            // Clamp to reasonable range
            score = Math.Clamp(score, 100, 10000);

            return new BenchmarkResult
            {
                Score = score,
                Method = "custom-prime",
                Duration = DateTime.UtcNow - startTime,
                RawMetric = primesPerSecond,
                Details = $"Primes found: {primeCount} in {stopwatch.Elapsed.TotalSeconds:F2}s " +
                         $"({primesPerSecond:F2} primes/sec)"
            };
        }, ct);
    }

    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n == 2) return true;
        if (n % 2 == 0) return false;

        var sqrt = (int)Math.Sqrt(n);
        for (int i = 3; i <= sqrt; i += 2)
        {
            if (n % i == 0)
                return false;
        }
        return true;
    }
}

/// <summary>
/// Benchmark result
/// </summary>
public class BenchmarkResult
{
    /// <summary>
    /// Normalized benchmark score (0-10000 scale)
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Benchmark method used (sysbench, custom-prime, etc.)
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Time taken to run benchmark
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Raw metric from benchmark tool
    /// </summary>
    public double RawMetric { get; set; }

    /// <summary>
    /// Additional details
    /// </summary>
    public string? Details { get; set; }

    /// <summary>
    /// Error message if benchmark failed
    /// </summary>
    public string? Error { get; set; }
}