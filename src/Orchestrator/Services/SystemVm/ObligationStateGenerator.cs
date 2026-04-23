using DeCloud.Shared.Models;
using NSec.Cryptography;
using Orchestrator.Models;
using System.Numerics;
using System.Security.Cryptography;

namespace Orchestrator.Services.SystemVm;

// ============================================================
// Placement: src/Orchestrator/Services/SystemVm/ObligationStateGenerator.cs
// ============================================================

/// <summary>
/// Generates cryptographic identity state for system VM obligations.
/// Called once at obligation creation time (StateVersion == 0).
/// The generated state is stored on the MongoDB Node document and
/// delivered to the node agent via the registration response.
///
/// CRYPTOGRAPHY
/// ────────────
/// WireGuard keys:  X25519 (Curve25519 DH) via NSec.Cryptography.
/// Ed25519 keys:    Ed25519 signature keys via NSec.Cryptography,
///                  used as the libp2p peer identity.
/// PeerId:          Identity multihash of the Ed25519 protobuf-encoded
///                  public key, base58btc-encoded (libp2p standard).
/// Auth tokens:     32 cryptographically random bytes, base64url-encoded.
///
/// IP ALLOCATION (Phase 3 — deterministic stubs)
/// ─────────────────────────────────────────────
/// Relay and DHT tunnel IPs use lightweight deterministic allocation
/// based on a hash of the node ID. Full pool allocation with
/// collision avoidance is wired in Phase C (system template conversion).
/// </summary>
public class ObligationStateGenerator
{
    private readonly ILogger<ObligationStateGenerator> _logger;

    // ── Subnet reservations ───────────────────────────────────────
    // Relay mesh:      10.20.0.0/16  — relay gets .0.1, CGNAT nodes get /24 slices
    // DHT mesh:        10.30.0.0/16  — each DHT node gets a /32 in this space
    // BlockStore mesh: 10.40.0.0/16  — each BlockStore node gets a /32
    private const string RelayTunnelBase    = "10.20.0";
    private const string DhtTunnelBase      = "10.30.0";
    private const string BlockStoreTunnelBase = "10.40.0";

    public ObligationStateGenerator(ILogger<ObligationStateGenerator> logger)
    {
        _logger = logger;
    }

    // ----------------------------------------------------------------
    // Public API
    // ----------------------------------------------------------------

    /// <summary>
    /// Generate fresh identity state for <paramref name="role"/> on <paramref name="node"/>.
    /// Every call produces a new key pair — call only when StateVersion == 0.
    /// </summary>
    public ObligationStateBase GenerateState(SystemVmRole role, Node node)
    {
        _logger.LogInformation(
            "Generating obligation state for role {Role} on node {NodeId}", role, node.Id);

        return role switch
        {
            SystemVmRole.Relay      => GenerateRelayState(node),
            SystemVmRole.Dht        => GenerateDhtState(node),
            SystemVmRole.BlockStore => GenerateBlockStoreState(node),
            _ => throw new ArgumentException($"No state generator for role: {role}", nameof(role))
        };
    }

    // ----------------------------------------------------------------
    // Per-role generators
    // ----------------------------------------------------------------

    private RelayObligationState GenerateRelayState(Node node)
    {
        var (wgPriv, wgPub) = GenerateWireGuardKeyPair();

        // Use the actual allocated subnet from RelayInfo if already set by RelayNodeService.
        // AllocateRelaySubnet is a deterministic stub that may not match the real allocation.
        var relaySubnet = node.RelayInfo?.RelaySubnet > 0
            ? $"10.20.{node.RelayInfo.RelaySubnet}.0/24"
            : AllocateRelaySubnet(node);

        return new RelayObligationState
        {
            WireGuardPrivateKey = wgPriv,
            WireGuardPublicKey = wgPub,
            TunnelIp = $"10.20.{node.RelayInfo?.RelaySubnet ?? 0}.254",
            RelaySubnet = relaySubnet,
            AuthToken = GenerateAuthToken(),
            Version             = 1,
            UpdatedAt           = DateTime.UtcNow,
        };
    }

    private DhtObligationState GenerateDhtState(Node node)
    {
        var (edPriv, edPub, peerId) = GenerateEd25519KeyPair();
        var (wgPriv, wgPub)         = GenerateWireGuardKeyPair();

        // Prefer the CGNAT tunnel IP if this is a CGNAT node — the tunnel is already
        // allocated by RelayNodeService and must not be duplicated.
        var tunnelIp = node.CgnatInfo?.TunnelIp ?? AllocateDhtTunnelIp(node);

        return new DhtObligationState
        {
            Ed25519PrivateKeyBase64 = edPriv,
            PeerId                  = peerId,
            WireGuardPrivateKey     = wgPriv,
            WireGuardPublicKey      = wgPub,
            TunnelIp                = tunnelIp,
            AuthToken               = GenerateAuthToken(),
            Version                 = 1,
            UpdatedAt               = DateTime.UtcNow,
        };
    }

    private BlockStoreObligationState GenerateBlockStoreState(Node node)
    {
        var (edPriv, edPub, peerId) = GenerateEd25519KeyPair();
        var (wgPriv, wgPub)         = GenerateWireGuardKeyPair();

        return new BlockStoreObligationState
        {
            Ed25519PrivateKeyBase64 = edPriv,
            PeerId                  = peerId,
            WireGuardPrivateKey     = wgPriv,
            WireGuardPublicKey      = wgPub,
            TunnelIp                = AllocateBlockStoreTunnelIp(node),
            AuthToken               = GenerateAuthToken(),
            StorageQuotaBytes       = CalculateStorageQuota(node),
            Version                 = 1,
            UpdatedAt               = DateTime.UtcNow,
        };
    }

    // ----------------------------------------------------------------
    // Cryptographic key generation
    // ----------------------------------------------------------------

    /// <summary>
    /// Generate a WireGuard (X25519 / Curve25519) key pair.
    /// Returns (privateKeyBase64, publicKeyBase64).
    /// Keys are standard base64 — the format WireGuard reads from wg.conf.
    /// </summary>
    private static (string privateKey, string publicKey) GenerateWireGuardKeyPair()
    {
        var algorithm = KeyAgreementAlgorithm.X25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var privBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var pubBytes  = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        return (Convert.ToBase64String(privBytes), Convert.ToBase64String(pubBytes));
    }

    /// <summary>
    /// Generate an Ed25519 key pair for libp2p peer identity.
    /// Returns (privateKeyBase64, publicKeyBase64, peerId).
    ///
    /// PeerId encoding follows the libp2p spec:
    ///   1. Encode public key as protobuf KeyType=Ed25519 (36 bytes)
    ///   2. Wrap in identity multihash (code=0x00, since 36 ≤ 42 bytes)
    ///   3. Base58btc-encode the multihash (38 bytes → ~50-char string)
    ///
    /// The resulting PeerId is directly readable by go-libp2p's
    /// <c>peer.Decode()</c> and <c>loadOrCreateIdentity()</c> functions.
    /// </summary>
    private static (string privateKeyBase64, string publicKeyBase64, string peerId) GenerateEd25519KeyPair()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        // NSec exports Ed25519 private key as 32-byte seed (not the 64-byte expanded form).
        // go-libp2p's loadOrCreateIdentity reads exactly this 32-byte seed format.
        var privBytes = key.Export(KeyBlobFormat.RawPrivateKey);   // 32 bytes
        var pubBytes  = key.PublicKey.Export(KeyBlobFormat.RawPublicKey); // 32 bytes

        var peerId = DerivePeerId(pubBytes);

        return (Convert.ToBase64String(privBytes), Convert.ToBase64String(pubBytes), peerId);
    }

    /// <summary>
    /// Derive a libp2p PeerId from a 32-byte Ed25519 public key.
    ///
    /// Encoding:
    ///   pubKeyProtobuf = [0x08, 0x01, 0x12, 0x20, ...32 bytes...] (36 bytes)
    ///   multihash      = [0x00, 0x24, ...36 bytes...] (identity multihash, 38 bytes)
    ///   peerId         = base58btc(multihash)
    /// </summary>
    private static string DerivePeerId(byte[] ed25519PublicKeyBytes)
    {
        // Protobuf encoding of libp2p PublicKey { KeyType=Ed25519(1), Data=pubkey }
        // Field 1 (KeyType): tag=0x08 (field 1, varint), value=0x01 (Ed25519)
        // Field 2 (Data):    tag=0x12 (field 2, length-delimited), length=0x20 (32)
        Span<byte> pubKeyProto = stackalloc byte[4 + ed25519PublicKeyBytes.Length];
        pubKeyProto[0] = 0x08;
        pubKeyProto[1] = 0x01;
        pubKeyProto[2] = 0x12;
        pubKeyProto[3] = (byte)ed25519PublicKeyBytes.Length; // 0x20
        ed25519PublicKeyBytes.CopyTo(pubKeyProto[4..]);

        // Identity multihash: varint(0x00), varint(length), data
        // 36 bytes ≤ 42 → identity multihash (no hashing)
        Span<byte> multihash = stackalloc byte[2 + pubKeyProto.Length];
        multihash[0] = 0x00; // identity multihash code
        multihash[1] = (byte)pubKeyProto.Length; // 0x24 = 36
        pubKeyProto.CopyTo(multihash[2..]);

        return Base58BtcEncode(multihash);
    }

    /// <summary>Minimal base58btc encoder (Bitcoin alphabet, big-endian).</summary>
    private static string Base58BtcEncode(ReadOnlySpan<byte> data)
    {
        const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

        int leadingZeros = 0;
        foreach (var b in data)
        {
            if (b != 0) break;
            leadingZeros++;
        }

        var intData = new BigInteger(data, isUnsigned: true, isBigEndian: true);
        var digits  = new List<int>();

        while (intData > 0)
        {
            intData = BigInteger.DivRem(intData, 58, out var rem);
            digits.Add((int)rem);
        }

        for (int i = 0; i < leadingZeros; i++)
            digits.Add(0);

        digits.Reverse();
        return new string(digits.Select(d => Alphabet[d]).ToArray());
    }

    /// <summary>
    /// Generate a 32-byte cryptographically random auth token, base64url-encoded.
    /// </summary>
    private static string GenerateAuthToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // ----------------------------------------------------------------
    // IP allocation (Phase 3 stubs — wired to pool allocation in Phase C)
    // ----------------------------------------------------------------

    /// <summary>
    /// Allocate a tunnel IP for a relay node.
    /// Phase 3: deterministic from node ID hash to avoid collisions during testing.
    /// Phase C: replace with pool-backed allocation from RelayNodeService.
    /// </summary>
    private static string AllocateRelayTunnelIp(Node node)
    {
        // Use bytes 0-1 of the node ID hash to pick a /24 host address.
        // Relay always gets .1 in its own subnet (it's the gateway).
        var slot = NodeIdToSlot(node.Id, max: 254);
        return $"{RelayTunnelBase}.{slot}";
    }

    /// <summary>
    /// Allocate the CGNAT relay subnet — the /24 block from which CGNAT clients
    /// on this relay get their addresses.
    /// Phase 3 stub — Phase C: allocate non-overlapping /24 blocks from a pool.
    /// </summary>
    private static string AllocateRelaySubnet(Node node)
    {
        var slot = NodeIdToSlot(node.Id, max: 254);
        // Relay mesh: 10.20.0.0/16. Each relay owns one /24 block.
        // e.g. slot=5 → relay tunnel 10.20.0.5, CGNAT subnet 10.20.5.0/24
        return $"10.20.{slot}.0/24";
    }

    /// <summary>
    /// Allocate a tunnel IP for a DHT node.
    /// Phase 3 stub — Phase C: wired to DhtNodeService tunnel pool.
    /// </summary>
    private static string AllocateDhtTunnelIp(Node node)
    {
        var slot = NodeIdToSlot(node.Id, max: 254);
        return $"{DhtTunnelBase}.{slot}";
    }

    /// <summary>
    /// Allocate a tunnel IP for a BlockStore node.
    /// Phase 3 stub — Phase C: wired to BlockStoreService tunnel pool.
    /// </summary>
    private static string AllocateBlockStoreTunnelIp(Node node)
    {
        var slot = NodeIdToSlot(node.Id, max: 254);
        return $"{BlockStoreTunnelBase}.{slot}";
    }

    /// <summary>
    /// Derive a stable 1-254 slot number from a node ID string.
    /// Uses SHA-256 so the mapping is deterministic and uniform.
    /// </summary>
    private static int NodeIdToSlot(string nodeId, int max)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(nodeId));
        return (int)(BitConverter.ToUInt16(hash, 0) % max) + 1;
    }

    // ----------------------------------------------------------------
    // Storage quota
    // ----------------------------------------------------------------

    /// <summary>
    /// Calculate the block store storage quota for a node (5 % of total disk).
    /// Minimum 10 GB, maximum 500 GB.
    /// </summary>
    private static long CalculateStorageQuota(Node node)
    {
        const long MinBytes  = 10L  * 1024 * 1024 * 1024; //  10 GB
        const long MaxBytes  = 500L * 1024 * 1024 * 1024; // 500 GB
        const double Ratio   = 0.05;

        var totalDisk = node.HardwareInventory?.Storage?.Sum(s => s.TotalBytes) ?? 0L;
        var quota     = (long)(totalDisk * Ratio);

        return Math.Clamp(quota, MinBytes, MaxBytes);
    }
}
