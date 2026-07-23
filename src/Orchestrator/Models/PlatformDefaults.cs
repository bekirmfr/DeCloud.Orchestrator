namespace Orchestrator.Models;

/// <summary>
/// Platform-wide defaults for values a user may choose but need not.
/// </summary>
public class PlatformDefaults
{
    /// <summary>
    /// Base image used when a VM creation request doesn't name one.
    ///
    /// The OS is a user choice with a default: OS-agnostic templates leave
    /// RecommendedSpec.ImageId empty on purpose, and templates whose cloud-init
    /// is OS-specific pin it themselves. Previously the default lived only in
    /// the legacy web form, so every other client (API, CLI, the new app)
    /// produced an unresolvable empty ImageId.
    ///
    /// ubuntu-22.04 is chosen because it is the only registry image with a
    /// PINNED SHA256 today — the default path is the content-verified one.
    /// Before moving to ubuntu-24.04 (longer support runway), pin its hash in
    /// the image registry first, or the default drops to permissive
    /// download-and-record mode.
    /// </summary>
    public string DefaultImageId { get; set; } = "ubuntu-22.04";
}