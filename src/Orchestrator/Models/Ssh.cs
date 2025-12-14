using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// SSH key pair (public + private)
/// </summary>
public class SshKeyPair
{
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string KeyType { get; set; } = "ssh-ed25519";
    public string Comment { get; set; } = "";
}

/// <summary>
/// SSH certificate for VM access
/// </summary>
public class SshCertificate
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string UserId { get; set; } = "";
    public string VmId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public string CertificateData { get; set; } = "";
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public List<string> Principals { get; set; } = new();
    public bool IsWalletDerived { get; set; }
    public string Fingerprint { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Request to sign an SSH certificate
/// </summary>
public class CertificateSignRequest
{
    public string PublicKey { get; set; } = "";
    public string CertificateId { get; set; } = "";
    public List<string> Principals { get; set; } = new();
    public int ValiditySeconds { get; set; }
    public Dictionary<string, string> Extensions { get; set; } = new();
}

/// <summary>
/// Response from certificate signing
/// </summary>
public class CertificateSignResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("signedCertificate")]
    public string SignedCertificate { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("validUntil")]
    public DateTime? ValidUntil { get; set; }
}

/// <summary>
/// Request to inject SSH key into VM
/// </summary>
public class InjectSshKeyRequest
{
    public string PublicKey { get; set; } = "";
    public string Username { get; set; } = "ubuntu";
    public bool Temporary { get; set; } = true;
}
