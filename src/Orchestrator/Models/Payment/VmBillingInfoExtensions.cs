using Orchestrator.Models.Payment;

namespace Orchestrator.Models;

/// <summary>
/// Extension properties for VmBillingInfo to support attestation tracking.
/// These are stored as additional properties without modifying the base class.
/// </summary>
public static class VmBillingInfoAttestationExtensions
{
    // In-memory storage for extension properties
    // In production, persist these to database
    private static readonly Dictionary<string, AttestationBillingData> _attestationData = new();
    private static readonly object _lock = new();

    public static TimeSpan GetVerifiedRuntime(this VmBillingInfo info, string vmId)
    {
        lock (_lock)
        {
            return _attestationData.TryGetValue(vmId, out var data)
                ? data.VerifiedRuntime
                : TimeSpan.Zero;
        }
    }

    public static void AddVerifiedRuntime(this VmBillingInfo info, string vmId, TimeSpan duration)
    {
        lock (_lock)
        {
            if (!_attestationData.TryGetValue(vmId, out var data))
            {
                data = new AttestationBillingData();
                _attestationData[vmId] = data;
            }
            data.VerifiedRuntime += duration;
        }
    }

    public static TimeSpan GetUnverifiedRuntime(this VmBillingInfo info, string vmId)
    {
        lock (_lock)
        {
            return _attestationData.TryGetValue(vmId, out var data)
                ? data.UnverifiedRuntime
                : TimeSpan.Zero;
        }
    }

    public static void AddUnverifiedRuntime(this VmBillingInfo info, string vmId, TimeSpan duration)
    {
        lock (_lock)
        {
            if (!_attestationData.TryGetValue(vmId, out var data))
            {
                data = new AttestationBillingData();
                _attestationData[vmId] = data;
            }
            data.UnverifiedRuntime += duration;
        }
    }

    private class AttestationBillingData
    {
        public TimeSpan VerifiedRuntime { get; set; }
        public TimeSpan UnverifiedRuntime { get; set; }
    }
}