using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Implementation of <see cref="IObligationEligibility"/>.
///
/// All eligibility thresholds live here and nowhere else.
/// Services that previously maintained their own copies of these constants
/// (RelayNodeService, etc.) now inject this interface and delegate to it.
/// </summary>
public sealed class ObligationEligibility : IObligationEligibility
{
    // ── Relay thresholds ──────────────────────────────────────────────────

    /// <summary>Minimum physical CPU cores for a relay node.</summary>
    public const int RelayMinComputePoints = 10;

    /// <summary>Minimum total RAM for a relay node (4 GB).</summary>
    public const long RelayMinRamBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Minimum measured bandwidth for a relay node (50 Mbps in bits/sec).</summary>
    public const long RelayMinBandwidthBps = 50_000_000;

    // ── BlockStore thresholds ─────────────────────────────────────────────

    /// <summary>Minimum total storage across all disks for a block store node (100 GB).</summary>
    public const long BlockStoreMinStorageBytes = 100L * 1024 * 1024 * 1024;

    /// <summary>Minimum total RAM for a block store node (2 GB).</summary>
    public const long BlockStoreMinRamBytes = 2L * 1024 * 1024 * 1024;

    // ── Inference thresholds ──────────────────────────────────────────────

    /// <summary>Minimum GPU VRAM for an inference node (8 GB).</summary>
    public const long InferenceMinVramBytes = 8L * 1024 * 1024 * 1024;

    // ═════════════════════════════════════════════════════════════════════
    // Per-role checks
    // ═════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public EligibilityResult CheckDht(Node node)
    {
        // DHT is universal — every accepted node runs it.
        return EligibilityResult.Eligible();
    }

    /// <inheritdoc/>
    public EligibilityResult CheckRelay(Node node)
    {
        var failures = new List<string>();

        // ── Public IP ────────────────────────────────────────────────────
        // NatType.None  = directly-assigned public IP.
        // NatType.Unknown = NAT detection not yet complete or timed out.
        //   When PublicIp == PrivateIp the interface is directly routable,
        //   so treat Unknown as None rather than blocking all such nodes.
        var natType = node.HardwareInventory.Network.NatType;
        var pubIp = node.HardwareInventory.Network.PublicIp;
        var privIp = node.HardwareInventory.Network.PrivateIp;

        bool hasPublicIp = natType == NatType.None
            || (natType == NatType.Unknown
                && !string.IsNullOrEmpty(pubIp)
                && pubIp == privIp);

        if (!hasPublicIp)
            failures.Add($"no public IP (NAT type: {natType})");

        // ── Physical cores ───────────────────────────────────────────────
        // Compare against physical cores, not compute points — the relay VM
        // needs real CPU headroom, not a benchmark-derived score.
        var totalComputePoints = node.TotalResources.ComputePoints;
        if (totalComputePoints < RelayMinComputePoints)
            failures.Add(
                $"insufficient CPU: {totalComputePoints} physical cores " +
                $"(minimum {RelayMinComputePoints})");

        // ── RAM ──────────────────────────────────────────────────────────
        var ramBytes = node.HardwareInventory.Memory.TotalBytes;
        if (ramBytes < RelayMinRamBytes)
            failures.Add(
                $"insufficient RAM: {ramBytes / (1024 * 1024 * 1024):F1} GB " +
                $"(minimum {RelayMinRamBytes / (1024 * 1024 * 1024)} GB)");

        // ── Bandwidth ────────────────────────────────────────────────────
        // Null = not yet measured — don't penalise nodes still running their
        // speed test. Only enforce the threshold once a measurement exists.
        var bw = node.HardwareInventory.Network.BandwidthBitsPerSecond;
        if (bw.HasValue && bw.Value < RelayMinBandwidthBps)
            failures.Add(
                $"insufficient bandwidth: {bw.Value / 1_000_000} Mbps " +
                $"(minimum {RelayMinBandwidthBps / 1_000_000} Mbps)");

        return failures.Count == 0
            ? EligibilityResult.Eligible()
            : EligibilityResult.Ineligible(failures);
    }

    /// <inheritdoc/>
    public EligibilityResult CheckBlockStore(Node node)
    {
        var failures = new List<string>();

        var totalStorage = node.HardwareInventory.Storage.Sum(s => s.TotalBytes);
        if (totalStorage < BlockStoreMinStorageBytes)
            failures.Add(
                $"insufficient storage: {totalStorage / (1024L * 1024 * 1024)} GB " +
                $"(minimum {BlockStoreMinStorageBytes / (1024L * 1024 * 1024)} GB)");

        var ramBytes = node.HardwareInventory.Memory.TotalBytes;
        if (ramBytes < BlockStoreMinRamBytes)
            failures.Add(
                $"insufficient RAM: {ramBytes / (1024 * 1024 * 1024):F1} GB " +
                $"(minimum {BlockStoreMinRamBytes / (1024 * 1024 * 1024)} GB)");

        return failures.Count == 0
            ? EligibilityResult.Eligible()
            : EligibilityResult.Ineligible(failures);
    }

    /// <inheritdoc/>
    public EligibilityResult CheckInference(Node node)
    {
        if (!node.HardwareInventory.SupportsGpu || node.HardwareInventory.Gpus.Count == 0)
            return EligibilityResult.Ineligible("no GPU detected");

        var bestVram = node.HardwareInventory.Gpus.Max(g => g.MemoryBytes);
        if (bestVram < InferenceMinVramBytes)
            return EligibilityResult.Ineligible(
                $"insufficient VRAM: {bestVram / (1024L * 1024 * 1024)} GB " +
                $"(minimum {InferenceMinVramBytes / (1024L * 1024 * 1024)} GB)");

        return EligibilityResult.Eligible();
    }

    // ═════════════════════════════════════════════════════════════════════
    // Aggregate
    // ═════════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public List<SystemVmRole> ComputeObligations(Node node)
    {
        var roles = new List<SystemVmRole>();

        // DHT is universal
        roles.Add(SystemVmRole.Dht);

        // Relay
        var relay = CheckRelay(node);
        if (relay.IsEligible)
        {
            roles.Add(SystemVmRole.Relay);
            // TODO: Ingress obligation disabled until DeployIngressVmAsync is implemented.
            // roles.Add(SystemVmRole.Ingress);
        }

        // BlockStore
        if (CheckBlockStore(node).IsEligible)
            roles.Add(SystemVmRole.BlockStore);

        // Inference (no SystemVmRole yet — placeholder for future GPU scheduling)
        // if (CheckInference(node).IsEligible)
        //     roles.Add(SystemVmRole.Inference);

        return roles;
    }
}