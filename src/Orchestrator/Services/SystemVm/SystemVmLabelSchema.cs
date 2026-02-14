using Orchestrator.Models;

namespace Orchestrator.Services.SystemVm;

/// <summary>
/// Defines the required Labels for each system VM role.
/// The NodeAgent reads these labels to render the appropriate cloud-init template.
/// Validation here catches missing configuration early — before the VM is dispatched
/// to a node where it would fail silently.
/// </summary>
public static class SystemVmLabelSchema
{
    private static readonly Dictionary<VmType, string[]> RequiredLabels = new()
    {
        [VmType.Relay] = [
            "role",
            "wireguard-private-key",
            "relay-region",
            "node-public-ip",
            "relay-subnet",
        ],
        [VmType.Dht] = [
            "role",
            "dht-listen-port",
            "dht-api-port",
            "dht-advertise-ip",
            "node-region",
            "node-id",
            // wg-relay-endpoint, wg-relay-pubkey, wg-tunnel-ip, wg-relay-api
            // are optional — resolved by DhtNodeService.ResolveWireGuardLabelsAsync
            // when a relay is available. DHT VMs can boot without WG mesh.
        ],
    };

    /// <summary>
    /// Validate that all required labels are present and non-empty for the given VM type.
    /// Returns null if valid, or an error message listing missing labels.
    /// </summary>
    public static string? Validate(VmType vmType, Dictionary<string, string>? labels)
    {
        if (!RequiredLabels.TryGetValue(vmType, out var required))
            return null; // No schema defined for this type — skip validation

        if (labels == null || labels.Count == 0)
            return $"{vmType} VM requires labels: [{string.Join(", ", required)}]";

        var missing = required
            .Where(key => !labels.TryGetValue(key, out var val) || string.IsNullOrEmpty(val))
            .ToList();

        if (missing.Count == 0)
            return null;

        return $"{vmType} VM is missing required labels: [{string.Join(", ", missing)}]";
    }
}
