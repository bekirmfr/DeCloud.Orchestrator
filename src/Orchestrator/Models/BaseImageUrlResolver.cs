namespace Orchestrator.Models;

/// <summary>
/// Descriptor for an orchestrator-curated base image: the URL the node
/// should download from, and the SHA256 the downloaded bytes must hash to.
///
/// CONTENT ADDRESSING
/// The hash is the authoritative identity of the image. Two nodes that
/// resolve the same imageId receive identical (Url, Sha256) and must
/// converge to byte-identical cached files. The URL is a fetch hint;
/// the hash is the contract.
///
/// EMPTY HASH IS PERMISSIVE
/// When <see cref="Sha256"/> is empty (initial rollout state), the node
/// downloads from <see cref="Url"/>, computes the SHA256, and reports it
/// back to the orchestrator via heartbeat. Subsequent migrations of that
/// VM carry the discovered hash and enforce strictly. Once the resolver's
/// table is populated with explicit hashes (Phase 2), enforcement applies
/// from the initial deploy too.
/// </summary>
/// <param name="Url">HTTPS download URL. Must be a stable, versioned URL —
///   avoid "latest" / "current" tags that drift between builds.</param>
/// <param name="Sha256">Lower-case hex SHA256 (64 chars), or empty for
///   permissive download-and-record mode.</param>
public sealed record BaseImageDescriptor(string Url, string Sha256);

/// <summary>
/// Single source of truth for base image (URL, SHA256) on the orchestrator side.
///
/// RESPONSIBILITY
/// Resolves a (imageId, architecture) pair to a fully-qualified
/// <see cref="BaseImageDescriptor"/> that the node agent's ImageManager
/// uses for download and verification. The orchestrator-side counterpart
/// of DeCloud.NodeAgent.Infrastructure.Libvirt.ArchitectureHelper, which
/// remains as a legacy fallback for the local-API path.
///
/// WHY HERE AND NOT IN DataStore
/// DataStore.VmImage carries catalogue metadata (name, description, sizeGb).
/// Download URLs and hashes are deployment-time operational data that must
/// be architecture-specific. Mixing them into VmImage would require either
/// duplicating the record per-arch or adding nested dictionaries — neither
/// is clean. A dedicated resolver keeps the boundary clear.
///
/// SECURITY
/// Only official distribution mirrors are listed. Third-party URLs are
/// explicitly excluded. Adding a new entry here requires a PR review.
/// Hashes are reviewed in the same pass and pinned to specific upstream
/// builds — see compute-base-image-hashes.sh (Phase 2) for the workflow
/// that populates them.
///
/// KISS
/// Static dictionary — no DB round-trip, no config file, no DI required.
/// Change a URL or hash → bump the orchestrator build → nodes pick it up
/// on next CreateVm dispatch. Image integrity is enforced by SHA256
/// verification in ImageManager, not by trusting the URL source.
/// </summary>
public static class BaseImageUrlResolver
{
    // ── Architecture tag normalisation ────────────────────────────────────

    /// <summary>
    /// Normalise a raw architecture string (as reported by the node agent's
    /// ResourceDiscoveryService) to the two-character tag used as the outer
    /// key in <see cref="ByArchThenImageId"/>.
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

    // ── Descriptor table ──────────────────────────────────────────────────

    /// <summary>
    /// Outer key: arch tag ("amd64" | "arm64").
    /// Inner key: image ID as registered in DataStore.InitialiseSeedData.
    /// Value: <see cref="BaseImageDescriptor"/> (URL + SHA256).
    ///
    /// HASH ROLLOUT (2026-05-31)
    /// Hashes are initially empty across all entries. Behaviour is
    /// permissive-then-strict: the node downloads on first use, computes
    /// the hash, records it, and reports back via heartbeat. Once
    /// Phase 2 populates the hash column, verification applies from the
    /// initial deploy.
    ///
    /// IMPORTANT
    /// Keep in sync with ArchitectureHelper.ImageUrlsByArchitecture on the
    /// node agent side. The orchestrator is the source of truth — the
    /// node agent table exists only as a local fallback for the local-API
    /// testing path.
    /// </summary>
    private static readonly Dictionary<string, Dictionary<string, BaseImageDescriptor>> ByArchThenImageId =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["amd64"] = new Dictionary<string, BaseImageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                // ── System VM images ─────────────────────────────────────
                ["debian-12-relay"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                    ""),
                ["debian-12-dht"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                    ""),
                ["debian-12-blockstore"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                    ""),

                // ── Community / user VM images ────────────────────────────
                ["debian-12"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-amd64.qcow2",
                    ""),
                ["debian-11"] = new(
                    "https://cloud.debian.org/images/cloud/bullseye/latest/debian-11-generic-amd64.qcow2",
                    ""),
                ["ubuntu-24.04"] = new(
                    "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-amd64.img",
                    ""),
                ["ubuntu-22.04"] = new(
                    "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-amd64.img",
                    ""),
                ["ubuntu-20.04"] = new(
                    "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-amd64.img",
                    ""),
                ["fedora-40"] = new(
                    "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/x86_64/images/Fedora-Cloud-Base-Generic.x86_64-40-1.14.qcow2",
                    ""),
                ["alpine-3.19"] = new(
                    "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/nocloud_alpine-3.19.1-x86_64-bios-cloudinit-r0.qcow2",
                    ""),
            },

            ["arm64"] = new Dictionary<string, BaseImageDescriptor>(StringComparer.OrdinalIgnoreCase)
            {
                // ── System VM images ─────────────────────────────────────
                ["debian-12-relay"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                    ""),
                ["debian-12-dht"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                    ""),
                ["debian-12-blockstore"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                    ""),

                // ── Community / user VM images ────────────────────────────
                ["debian-12"] = new(
                    "https://cloud.debian.org/images/cloud/bookworm/latest/debian-12-generic-arm64.qcow2",
                    ""),
                ["debian-11"] = new(
                    "https://cloud.debian.org/images/cloud/bullseye/latest/debian-11-generic-arm64.qcow2",
                    ""),
                ["ubuntu-24.04"] = new(
                    "https://cloud-images.ubuntu.com/noble/current/noble-server-cloudimg-arm64.img",
                    ""),
                ["ubuntu-22.04"] = new(
                    "https://cloud-images.ubuntu.com/jammy/current/jammy-server-cloudimg-arm64.img",
                    ""),
                ["ubuntu-20.04"] = new(
                    "https://cloud-images.ubuntu.com/focal/current/focal-server-cloudimg-arm64.img",
                    ""),
                ["fedora-40"] = new(
                    "https://download.fedoraproject.org/pub/fedora/linux/releases/40/Cloud/aarch64/images/Fedora-Cloud-Base-Generic-40-1.14.aarch64.qcow2",
                    ""),
                ["alpine-3.19"] = new(
                    "https://dl-cdn.alpinelinux.org/alpine/v3.19/releases/cloud/nocloud_alpine-3.19.1-aarch64-uefi-cloudinit-r0.qcow2",
                    ""),
            },
        };

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>
    /// Resolve an image ID and raw architecture string to a (URL, SHA256)
    /// descriptor.
    ///
    /// Falls back to <c>"amd64"</c> when the architecture is null or
    /// unrecognised — covers nodes that pre-date the Architecture field.
    ///
    /// Returns <c>null</c> when the image ID is not registered for the
    /// resolved architecture. Callers must handle null and should log a
    /// warning before skipping VM creation.
    /// </summary>
    public static BaseImageDescriptor? Resolve(string imageId, string? rawArchitecture)
    {
        var archTag = NormaliseArchTag(rawArchitecture) ?? "amd64";

        if (!ByArchThenImageId.TryGetValue(archTag, out var imageMap))
            return null;

        return imageMap.GetValueOrDefault(imageId);
    }
}
