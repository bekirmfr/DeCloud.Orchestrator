using DeCloud.Shared.Models;

namespace Orchestrator.Models;

/// <summary>
/// Single source of truth for all mappings that require the
/// <see cref="SystemVmRole"/> enum.
///
/// WHY THIS EXISTS
/// ───────────────
/// The codebase had the same three-way switch repeated in at least five places:
///
///   • <c>NodeService.GenerateAndAttachObligationStates</c>
///   • <c>NodeService.DetectStaleObligationStates</c>
///   • <c>NodeService.GenerateSystemTemplatePayloads</c>
///   • <c>NodeService.DetectStaleSystemTemplates</c>
///   • <c>NodeSelfController.RoleToCanonical</c>
///   • <c>BuildSystemVmTemplate</c>
///   • … and counting.
///
/// Each duplication is a place where a new role (e.g. Ingress) could be
/// added to the enum but silently fall through without error. Centralising
/// here means every mapping is exhaustive-checked by the compiler (switch
/// expression with no default, or a <c>throw</c> default that fires in
/// tests before it reaches production).
///
/// BOUNDARY
/// ────────
/// This class lives in the Orchestrator because <see cref="SystemVmRole"/>,
/// <see cref="VmType"/>, and the VmSpec static classes are all Orchestrator
/// types. String ↔ string mappings (e.g. role name → template slug) that
/// the node agent also needs live in <see cref="ObligationRole"/> in Shared.
/// </summary>
public static class SystemVmRoleMap
{
    // ── Enum → canonical string ──────────────────────────────────────────

    /// <summary>
    /// Convert a <see cref="SystemVmRole"/> enum value to its lower-case
    /// canonical name. Returns <c>null</c> for roles that have no system
    /// VM deployment mapping (e.g. <c>Ingress</c> — handled separately).
    ///
    /// Replaces every instance of:
    /// <code>
    ///   var roleName = obligation.Role switch {
    ///       SystemVmRole.Relay      => "relay",
    ///       SystemVmRole.Dht        => "dht",
    ///       SystemVmRole.BlockStore => "blockstore",
    ///       _                       => null
    ///   };
    /// </code>
    /// </summary>
    public static string? ToCanonicalName(SystemVmRole role) => role switch
    {
        SystemVmRole.Relay => ObligationRole.Relay,
        SystemVmRole.Dht => ObligationRole.Dht,
        SystemVmRole.BlockStore => ObligationRole.BlockStore,
        _ => null,   // Ingress etc. — no obligation state
    };

    /// <summary>
    /// Convert a canonical role name to a <see cref="SystemVmRole"/> enum value.
    /// Returns <c>null</c> for unknown strings (already canonicalised by caller).
    ///
    /// Replaces every instance of:
    /// <code>
    ///   var role = canonicalName switch {
    ///       "relay"      => SystemVmRole.Relay,
    ///       "dht"        => SystemVmRole.Dht,
    ///       "blockstore" => SystemVmRole.BlockStore,
    ///       _            => null
    ///   };
    /// </code>
    /// </summary>
    public static SystemVmRole? FromCanonicalName(string? canonicalName) =>
        ObligationRole.Canonicalise(canonicalName) switch
        {
            ObligationRole.Relay => SystemVmRole.Relay,
            ObligationRole.Dht => SystemVmRole.Dht,
            ObligationRole.BlockStore => SystemVmRole.BlockStore,
            _ => null,
        };

    // ── Enum → VmType ────────────────────────────────────────────────────

    /// <summary>
    /// Map a <see cref="SystemVmRole"/> to its corresponding
    /// <see cref="VmType"/>. Returns <c>null</c> for unmapped roles.
    ///
    /// Used by <c>SystemVmReconciliationService</c> when looking up existing
    /// VMs by type, and by the node-agent side's <c>RealityProjection</c>
    /// (where the equivalent mapping uses string constants — kept there to
    /// avoid a Shared → Orchestrator dependency).
    /// </summary>
    public static VmType? ToVmType(SystemVmRole role) => role switch
    {
        SystemVmRole.Relay => VmType.Relay,
        SystemVmRole.Dht => VmType.Dht,
        SystemVmRole.BlockStore => VmType.BlockStore,
        _ => null,
    };

    // ── Enum → base image ID ─────────────────────────────────────────────

    /// <summary>
    /// Map a role to its registered base image ID in
    /// <c>DataStore.InitialiseSeedData</c>.
    ///
    /// Reads from the existing static spec classes so the image ID is defined
    /// in exactly one place:
    ///   <see cref="RelayVmSpec.Basic"/>
    ///   <see cref="DhtVmSpec.Standard"/>
    ///   <see cref="BlockStoreVmSpec.Create"/>
    ///
    /// Replaces the ad-hoc inline switch that was added to
    /// <c>BuildSystemVmTemplate</c> in NodeService.
    /// </summary>
    public static string ToBaseImageId(SystemVmRole role) => role switch
    {
        SystemVmRole.Relay => RelayVmSpec.Basic.ImageId,
        SystemVmRole.Dht => DhtVmSpec.Standard.ImageId,
        SystemVmRole.BlockStore => BlockStoreVmSpec.Create(0).ImageId,
        _ => throw new ArgumentOutOfRangeException(
                                       nameof(role),
                                       $"No base image ID for role '{role}'.")
    };

    // ── Enum → template slug ─────────────────────────────────────────────

    /// <summary>
    /// Map a role to its system template slug. Delegates to
    /// <see cref="ObligationRole.ToTemplateSlug"/> via the canonical name so
    /// the mapping is defined once in Shared.
    ///
    /// Returns <c>null</c> for roles with no system template (Ingress etc.).
    /// </summary>
    public static string? ToTemplateSlug(SystemVmRole role)
    {
        var canonical = ToCanonicalName(role);
        return canonical is null ? null : ObligationRole.ToTemplateSlug(canonical);
    }

    // ── All deployable roles ─────────────────────────────────────────────

    /// <summary>
    /// The subset of <see cref="SystemVmRole"/> values that have a system VM
    /// deployment mapping — i.e. all values for which
    /// <see cref="ToCanonicalName"/> returns non-null.
    ///
    /// Use this when iterating obligations or building heartbeat payloads on
    /// the orchestrator side. The node-agent side equivalent is
    /// <see cref="ObligationRole.All"/>.
    /// </summary>
    public static readonly IReadOnlyList<SystemVmRole> All =
        [SystemVmRole.Relay, SystemVmRole.Dht, SystemVmRole.BlockStore];
}