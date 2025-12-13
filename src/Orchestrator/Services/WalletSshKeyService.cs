using System.Net;
using System.Security.Cryptography;
using System.Text;
using Nethereum.Signer;
using Nethereum.Util;
using NSec.Cryptography;
using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Service interface for deriving SSH keys from wallet signatures
/// </summary>
public interface IWalletSshKeyService
{
    /// <summary>
    /// Derive deterministic SSH key pair from wallet signature
    /// Same wallet signature = same SSH keys!
    /// </summary>
    Task<SshKeyPair> DeriveKeysFromWalletSignatureAsync(
        string walletAddress,
        string signature,
        CancellationToken ct = default);

    /// <summary>
    /// Verify that a wallet signature is valid
    /// </summary>
    bool VerifyWalletSignature(string walletAddress, string message, string signature);

    /// <summary>
    /// Get the message that should be signed for SSH key derivation
    /// </summary>
    string GetSshKeyDerivationMessage();
}

/// <summary>
/// Wallet-based SSH key derivation service
/// Uses Ed25519 keys derived deterministically from wallet signatures
/// </summary>
public class WalletSshKeyService : IWalletSshKeyService
{
    private const string SSH_KEY_DERIVATION_MESSAGE = "DeCloud SSH Key Derivation v1";
    private readonly ILogger<WalletSshKeyService> _logger;

    public WalletSshKeyService(ILogger<WalletSshKeyService> logger)
    {
        _logger = logger;
    }

    public string GetSshKeyDerivationMessage() => SSH_KEY_DERIVATION_MESSAGE;

    public async Task<SshKeyPair> DeriveKeysFromWalletSignatureAsync(
        string walletAddress,
        string signature,
        CancellationToken ct = default)
    {
        try
        {
            // 1. Verify signature authenticity
            if (!VerifyWalletSignature(walletAddress, SSH_KEY_DERIVATION_MESSAGE, signature))
            {
                throw new UnauthorizedAccessException(
                    "Invalid wallet signature for SSH key derivation");
            }

            _logger.LogInformation(
                "Deriving SSH keys for wallet {Wallet}",
                walletAddress.Substring(0, Math.Min(10, walletAddress.Length)) + "...");

            // 2. Derive deterministic 32-byte seed from signature
            var seed = DeriveKeyMaterialFromSignature(signature);

            // 3. Generate Ed25519 key pair from seed
            var keyPair = await GenerateEd25519KeyPairAsync(seed, walletAddress, ct);

            _logger.LogInformation(
                "✓ SSH keys derived successfully for wallet {Wallet} (fingerprint: {Fingerprint})",
                walletAddress.Substring(0, Math.Min(10, walletAddress.Length)) + "...",
                keyPair.Fingerprint);

            return keyPair;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to derive SSH keys from wallet signature");
            throw;
        }
    }

    public bool VerifyWalletSignature(string walletAddress, string message, string signature)
    {
        try
        {
            // Use Nethereum to recover address from signature
            var signer = new EthereumMessageSigner();
            var recoveredAddress = signer.EncodeUTF8AndEcRecover(message, signature);

            // Normalize both addresses for comparison
            var addressUtil = new AddressUtil();
            var normalizedRecovered = addressUtil.ConvertToChecksumAddress(recoveredAddress);
            var normalizedProvided = addressUtil.ConvertToChecksumAddress(walletAddress);

            var isValid = normalizedRecovered.Equals(
                normalizedProvided,
                StringComparison.OrdinalIgnoreCase);

            if (!isValid)
            {
                _logger.LogWarning(
                    "Signature verification failed: expected {Expected}, got {Actual}",
                    normalizedProvided,
                    normalizedRecovered);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying wallet signature");
            return false;
        }
    }

    /// <summary>
    /// Derive 32-byte key material from wallet signature using SHA-256
    /// This ensures deterministic key generation (same signature = same key)
    /// </summary>
    private byte[] DeriveKeyMaterialFromSignature(string signature)
    {
        using var sha256 = SHA256.Create();
        var signatureBytes = Encoding.UTF8.GetBytes(signature);
        return sha256.ComputeHash(signatureBytes);
    }

    /// <summary>
    /// Generate Ed25519 SSH key pair from seed using NSec library
    /// </summary>
    private async Task<SshKeyPair> GenerateEd25519KeyPairAsync(
        byte[] seed,
        string walletAddress,
        CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            // Use NSec's Ed25519 signature algorithm
            var algorithm = SignatureAlgorithm.Ed25519;

            // Import the seed as a private key
            // Ed25519 private keys are 32 bytes - perfect match for our SHA-256 hash
            var key = Key.Import(
                algorithm,
                seed,
                KeyBlobFormat.RawPrivateKey);

            // Get public key
            var publicKey = key.PublicKey;

            // Convert to OpenSSH format
            var privateKeyPem = ExportPrivateKeyToOpenSSH(key, walletAddress);
            var publicKeySsh = ExportPublicKeyToOpenSSH(publicKey, walletAddress);
            var fingerprint = CalculateFingerprint(publicKey);

            return new SshKeyPair
            {
                PrivateKey = privateKeyPem,
                PublicKey = publicKeySsh,
                Fingerprint = fingerprint,
                KeyType = "ssh-ed25519",
                Comment = $"decloud-wallet-{walletAddress.Substring(0, Math.Min(8, walletAddress.Length))}"
            };
        }, ct);
    }

    /// <summary>
    /// Export Ed25519 private key in OpenSSH format (PEM-like)
    /// </summary>
    private string ExportPrivateKeyToOpenSSH(Key key, string walletAddress)
    {
        // Export the private key bytes
        var privateKeyBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicKeyBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        // OpenSSH private key format structure
        var comment = $"decloud-wallet-{walletAddress.Substring(0, Math.Min(8, walletAddress.Length))}";

        // Build the OpenSSH private key blob
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Magic bytes for OpenSSH format
        writer.Write(Encoding.ASCII.GetBytes("openssh-key-v1\0"));

        // Cipher name (none - unencrypted)
        WriteSshString(writer, "none");

        // KDF name (none)
        WriteSshString(writer, "none");

        // KDF options (empty)
        WriteSshString(writer, Array.Empty<byte>());

        // Number of keys (1)
        writer.Write(IPAddress.HostToNetworkOrder(1));

        // Public key section
        using var publicMs = new MemoryStream();
        using var publicWriter = new BinaryWriter(publicMs);
        WriteSshString(publicWriter, "ssh-ed25519");
        WriteSshString(publicWriter, publicKeyBytes);
        var publicBlob = publicMs.ToArray();
        WriteSshString(writer, publicBlob);

        // Private key section
        using var privateMs = new MemoryStream();
        using var privateWriter = new BinaryWriter(privateMs);

        // Check bytes (for encryption verification - we use random for unencrypted)
        var checkInt = Random.Shared.Next();
        privateWriter.Write(IPAddress.HostToNetworkOrder(checkInt));
        privateWriter.Write(IPAddress.HostToNetworkOrder(checkInt));

        // Key type
        WriteSshString(privateWriter, "ssh-ed25519");

        // Public key
        WriteSshString(privateWriter, publicKeyBytes);

        // Private key (Ed25519: 32 bytes private + 32 bytes public)
        var fullPrivateKey = new byte[64];
        Array.Copy(privateKeyBytes, 0, fullPrivateKey, 0, 32);
        Array.Copy(publicKeyBytes, 0, fullPrivateKey, 32, 32);
        WriteSshString(privateWriter, fullPrivateKey);

        // Comment
        WriteSshString(privateWriter, comment);

        // Padding to 8-byte boundary
        var privateBlob = privateMs.ToArray();
        var paddingLength = (8 - (privateBlob.Length % 8)) % 8;
        privateWriter.Write(new byte[paddingLength]);

        privateBlob = privateMs.ToArray();
        WriteSshString(writer, privateBlob);

        var blob = ms.ToArray();
        var base64 = Convert.ToBase64String(blob);

        // Format as OpenSSH private key
        var sb = new StringBuilder();
        sb.AppendLine("-----BEGIN OPENSSH PRIVATE KEY-----");

        // Split base64 into 70-character lines
        for (int i = 0; i < base64.Length; i += 70)
        {
            var length = Math.Min(70, base64.Length - i);
            sb.AppendLine(base64.Substring(i, length));
        }

        sb.AppendLine("-----END OPENSSH PRIVATE KEY-----");

        return sb.ToString();
    }

    /// <summary>
    /// Export Ed25519 public key in OpenSSH format (ssh-ed25519 AAAA... comment)
    /// </summary>
    private string ExportPublicKeyToOpenSSH(PublicKey publicKey, string walletAddress)
    {
        var keyBytes = publicKey.Export(KeyBlobFormat.RawPublicKey);

        // Build OpenSSH public key blob
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        // Write key type
        WriteSshString(writer, "ssh-ed25519");

        // Write key data
        WriteSshString(writer, keyBytes);

        var blob = ms.ToArray();
        var base64 = Convert.ToBase64String(blob);
        var comment = $"decloud-wallet-{walletAddress.Substring(0, Math.Min(8, walletAddress.Length))}";

        return $"ssh-ed25519 {base64} {comment}";
    }

    /// <summary>
    /// Calculate SSH fingerprint (SHA256 hash of public key)
    /// </summary>
    private string CalculateFingerprint(PublicKey publicKey)
    {
        // Get the SSH public key blob
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);

        WriteSshString(writer, "ssh-ed25519");
        WriteSshString(writer, publicKey.Export(KeyBlobFormat.RawPublicKey));

        var blob = ms.ToArray();

        // Calculate SHA256 hash
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(blob);

        // Format as SSH fingerprint
        var base64 = Convert.ToBase64String(hash).TrimEnd('=');
        return $"SHA256:{base64}";
    }

    /// <summary>
    /// Write SSH string format (4-byte length + data)
    /// </summary>
    private void WriteSshString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteSshString(writer, bytes);
    }

    private void WriteSshString(BinaryWriter writer, byte[] data)
    {
        writer.Write(IPAddress.HostToNetworkOrder(data.Length));
        writer.Write(data);
    }
}
