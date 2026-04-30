namespace Orchestrator.Models;

/// <summary>
/// Single source of truth for base image download URLs on the orchestrator side.
///
/// RESPONSIBILITY
/// Resolves a (imageId, architecture) pair to a fully-qualified HTTPS download
/// URL that the node agent's ImageManager can hand to HttpClient directly.
/// This is the orchestrator-side counterpart of
/// DeCloud.NodeAgent.Infrastructure.Libvirt.ArchitectureHelper.ImageUrlsByArchitecture.
///
/// WHY HERE AND NOT IN DataStore
/// DataStore.VmImage carries catalogue metadata (name, description, sizeGb).
/// Download URLs are deployment-time operational data that must be
/// architecture-specific. Mixing them into VmImage would require either
/// duplicating the record per-arch or adding a Dictionary field — neither
/// is clean. A dedicated resolver keeps the boundary clear.
///
/// SECURITY
/// Only official distribution mirrors are listed. Third-party URLs are
/// explicitly excluded. Adding a new URL here requires a PR review.
///
/// KISS
/// Static dictionary — no DB round-trip, no config file, no DI required.
/// Change a URL → bump the orchestrator build → nodes pick it up on next
/// registration. Image integrity is enforced by SHA256 verification in
/// ImageManager, not by trusting the URL source.
/// </summary>
public static class BaseImageUrlResolver
{
    // ── Architecture tag normalisation ────────────────────────────────────

    /// <summary>
    /// Normalise a raw architecture string (as reported by the node agent's
    /// ResourceDiscoveryService) to the two-character tag used as the outer
    /// key in <see cref="UrlsByArchThenImageId"/>.
    ///
    /// Returns <c>null</c> for unrecognised architectures — callers should
    /// fall back to <c>"amd64"</c> for nodes that pre-date the Architecture
    /// field (null Architecture on CpuInfo).
    /// </summary>
    public static string? NormaliseArchTag(string? rawArchitecture) =>
        rawArchitecture?.Trim().ToLowerInvariant() switch
        {
            "x86_64" or "amd64" or "x64" => "amd64",
            "aarch64" or "arm64" => "arm64",
            _ => null,
        };

    // ── URL table ─────────────────────────────────────────────────────────

    /// <summary>
    /// Outer key: arch tag ("amd64" | "arm64").
    /// Inner key: image ID as registered in DataStore.InitialiseSeedData.
    /// Value: HTTPS download URL for the qcow2 base image.
    ///
    /// IMPORTANT: keep in sync with ArchitectureHelper.ImageUrlsByArchitecture
    /// on the node agent side. The orchestrator is the source of truth — the
    /// node agent table exists only as a local fallback.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, string>> UrlsByArchThenImageId =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["amd64"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ── System VM images ─────────────────────────────────────
                ["debian-12-relay"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                ["debian-12-dht"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                ["debian-12-blockstore"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",

                // ── Community / user VM images ────────────────────────────
                ["debian-12"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                ["debian-11"] = "https://cloud.debian.org/images/cloud/bullseye/latest/debian-11-generic-amd64.qcow2",
                ["ubuntu-24.04"] = "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img",
                ["ubuntu-22.04"] = "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-amd64.img",
                ["ubuntu-20.04"] = "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-amd64.img",
                ["fedora-40"] = "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/x86_64/images/Fedora-Cloud-Base-Generic.x86_64-40-1.14.qcow2",
                ["alpine-3.19"] = "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/nocloud_alpine-3.19.1-x86_64-bios-cloudinit-r0.qcow2",
            },

            ["arm64"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ── System VM images ─────────────────────────────────────
                ["debian-12-relay"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                ["debian-12-dht"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                ["debian-12-blockstore"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",

                // ── Community / user VM images ────────────────────────────
                ["debian-12"] = "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                ["debian-11"] = "https://cloud.debian.org/images/cloud/bullseye/latest/debian-11-generic-arm64.qcow2",
                ["ubuntu-24.04"] = "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-arm64.img",
                ["ubuntu-22.04"] = "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-arm64.img",
                ["ubuntu-20.04"] = "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-arm64.img",
                ["fedora-40"] = "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/aarch64/images/Fedora-Cloud-Base-Generic-40-1.14.aarch64.qcow2",
                ["alpine-3.19"] = "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/nocloud_alpine-3.19.1-aarch64-uefi-cloudinit-r0.qcow2",
            },
        };

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve an image ID and raw architecture string to a download URL.
    ///
    /// Falls back to <c>"amd64"</c> when the architecture is null or
    /// unrecognised — covers nodes that pre-date the Architecture field.
    ///
    /// Returns <c>null</c> when the image ID is not registered for the
    /// resolved architecture. Callers must handle null and should log a
    /// warning before skipping VM creation.
    /// </summary>
    public static string? Resolve(string imageId, string? rawArchitecture)
    {
        var archTag = NormaliseArchTag(rawArchitecture) ?? "amd64";

        if (!UrlsByArchThenImageId.TryGetValue(archTag, out var imageMap))
            return null;

        return imageMap.GetValueOrDefault(imageId);
    }
}