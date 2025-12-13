using Orchestrator.Models;

namespace Orchestrator.Services;

/// <summary>
/// Service interface for SSH certificate management
/// </summary>
public interface ISshCertificateService
{
    /// <summary>
    /// Issue SSH certificate for VM access
    /// Handles both user-registered keys and wallet-derived keys
    /// </summary>
    Task<CertificateResult> IssueCertificateAsync(
        User user,
        VirtualMachine vm,
        string? walletSignature,
        TimeSpan validity,
        CancellationToken ct = default);
}

/// <summary>
/// SSH Certificate service with wallet-derived key support
/// </summary>
public class SshCertificateService : ISshCertificateService
{
    private readonly IWalletSshKeyService _walletSshKeyService;
    private readonly INodeService _nodeService;
    private readonly ILogger<SshCertificateService> _logger;

    public SshCertificateService(
        IWalletSshKeyService walletSshKeyService,
        INodeService nodeService,
        ILogger<SshCertificateService> logger)
    {
        _walletSshKeyService = walletSshKeyService;
        _nodeService = nodeService;
        _logger = logger;
    }

    public async Task<CertificateResult> IssueCertificateAsync(
        User user,
        VirtualMachine vm,
        string? walletSignature,
        TimeSpan validity,
        CancellationToken ct = default)
    {
        SshKeyPair keyPair;
        bool isWalletDerived = false;

        // CASE 1: User has registered SSH keys - use them
        var userKey = user.SshKeys.FirstOrDefault();
        if (userKey != null)
        {
            _logger.LogInformation(
                "Using user's registered SSH key for certificate: {KeyId}",
                userKey.Id);

            keyPair = new SshKeyPair
            {
                PublicKey = userKey.PublicKey,
                PrivateKey = null, // User already has their private key
                Fingerprint = userKey.Fingerprint,
                KeyType = "user-registered",
                Comment = userKey.Name
            };
        }
        // CASE 2: No SSH key - derive from wallet signature
        else if (!string.IsNullOrEmpty(walletSignature))
        {
            _logger.LogInformation(
                "No SSH key registered - deriving from wallet for VM {VmId}",
                vm.Id);

            keyPair = await _walletSshKeyService.DeriveKeysFromWalletSignatureAsync(
                user.WalletAddress,
                walletSignature,
                ct);
            isWalletDerived = true;

            // Inject the wallet-derived public key into the VM
            await InjectPublicKeyIntoVmAsync(vm.Id, vm.NodeId, keyPair.PublicKey, ct);
        }
        else
        {
            throw new InvalidOperationException(
                "User has no SSH key registered and no wallet signature provided. " +
                "Either register an SSH key or provide a wallet signature.");
        }

        // Generate certificate ID
        var certId = $"decloud-{user.Id.Substring(0, 8)}-{vm.Id.Substring(0, 8)}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        // Define principals (what this certificate can access)
        var principals = new List<string>
        {
            $"vm-{vm.Id}",                          // VM-specific access
            $"user-{user.Id}",                       // User-specific access
            $"wallet-{user.WalletAddress.ToLower()}" // Wallet-specific access
        };

        _logger.LogInformation(
            "Issuing SSH certificate {CertId} for VM {VmId}, principals: {Principals}",
            certId, vm.Id, string.Join(", ", principals));

        // Request certificate signature from node's CA
        var signedCert = await RequestCertificateFromNodeAsync(
            vm.NodeId,
            keyPair.PublicKey,
            certId,
            principals,
            validity,
            vm.NetworkConfig?.PrivateIp,
            ct);

        var certificate = new SshCertificate
        {
            Id = certId,
            UserId = user.Id,
            VmId = vm.Id,
            NodeId = vm.NodeId,
            CertificateData = signedCert,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.Add(validity),
            Principals = principals,
            IsWalletDerived = isWalletDerived,
            Fingerprint = keyPair.Fingerprint
        };

        return new CertificateResult
        {
            Certificate = certificate,
            PrivateKey = isWalletDerived ? keyPair.PrivateKey : null,
            PublicKey = keyPair.PublicKey,
            IsWalletDerived = isWalletDerived
        };
    }

    /// <summary>
    /// Inject wallet-derived public key into running VM
    /// Uses the node agent's SSH key injection mechanism
    /// </summary>
    private async Task InjectPublicKeyIntoVmAsync(
        string vmId,
        string nodeId,
        string publicKey,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Injecting wallet-derived public key into VM {VmId}",
            vmId);

        // This uses the existing ephemeral SSH key service on the node
        // The node agent will add the public key to /home/ubuntu/.ssh/authorized_keys
        await _nodeService.InjectSshKeyAsync(
            nodeId,
            vmId,
            publicKey,
            "ubuntu",
            ct);
    }

    /// <summary>
    /// Request certificate signature from node's SSH CA
    /// </summary>
    private async Task<string> RequestCertificateFromNodeAsync(
        string nodeId,
        string publicKey,
        string certificateId,
        List<string> principals,
        TimeSpan validity,
        string? vmIp,
        CancellationToken ct)
    {
        var request = new CertificateSignRequest
        {
            PublicKey = publicKey,
            CertificateId = certificateId,
            Principals = principals,
            ValiditySeconds = (int)validity.TotalSeconds,
            Extensions = new Dictionary<string, string>
            {
                ["permit-pty"] = "",
                ["permit-port-forwarding"] = ""
            }
        };

        // Add VM-specific port forwarding restriction if VM IP is known
        if (!string.IsNullOrEmpty(vmIp))
        {
            request.Extensions["permitopen"] = $"{vmIp}:22";
        }

        var response = await _nodeService.SignCertificateAsync(nodeId, request, ct);

        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"Certificate signing failed: {response.Error}");
        }

        return response.SignedCertificate;
    }
}

/// <summary>
/// Result of certificate issuance
/// </summary>
public class CertificateResult
{
    public SshCertificate Certificate { get; set; } = null!;
    public string? PrivateKey { get; set; }  // Only populated for wallet-derived keys
    public string PublicKey { get; set; } = "";
    public bool IsWalletDerived { get; set; }
}
