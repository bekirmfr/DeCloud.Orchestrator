using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Models;
using Orchestrator.Services;

namespace Orchestrator.Controllers;

/// <summary>
/// SSH Certificate API
/// Handles SSH certificate issuance for VM access with wallet-derived keys
/// </summary>
[ApiController]
[Route("api/vms/{vmId}/ssh")]
[Authorize]
public class SshCertificateController : ControllerBase
{
    private readonly ISshCertificateService _certificateService;
    private readonly IVmService _vmService;
    private readonly IUserService _userService;
    private readonly IWalletSshKeyService _walletSshKeyService;
    private readonly INodeService _nodeService;
    private readonly ILogger<SshCertificateController> _logger;

    public SshCertificateController(
        ISshCertificateService certificateService,
        IVmService vmService,
        IUserService userService,
        IWalletSshKeyService walletSshKeyService,
        INodeService nodeService,
        ILogger<SshCertificateController> logger)
    {
        _certificateService = certificateService;
        _vmService = vmService;
        _userService = userService;
        _walletSshKeyService = walletSshKeyService;
        _nodeService = nodeService;
        _logger = logger;
    }

    /// <summary>
    /// Get SSH certificate for VM access
    /// Automatically handles both user SSH keys and wallet-derived keys
    /// </summary>
    /// <param name="vmId">VM ID</param>
    /// <param name="request">Certificate request with optional wallet signature</param>
    [HttpPost("certificate")]
    [ProducesResponseType(typeof(ApiResponse<SshCertificateResponse>), 200)]
    [ProducesResponseType(typeof(ApiResponse<SshCertificateResponse>), 400)]
    [ProducesResponseType(401)]
    [ProducesResponseType(404)]
    public async Task<ActionResult<ApiResponse<SshCertificateResponse>>> GetCertificate(
        string vmId,
        [FromBody] SshCertificateRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<SshCertificateResponse>.Fail(
                "UNAUTHORIZED", "User not authenticated"));
        }

        try
        {
            // Get user and VM
            var user = await _userService.GetUserByIdAsync(userId);
            if (user == null)
            {
                return Unauthorized(ApiResponse<SshCertificateResponse>.Fail(
                    "USER_NOT_FOUND", "User not found"));
            }

            var vm = await _vmService.GetVmAsync(vmId);
            if (vm == null)
            {
                return NotFound(ApiResponse<SshCertificateResponse>.Fail(
                    "VM_NOT_FOUND", "VM not found"));
            }

            if (vm.OwnerId != userId)
            {
                return Forbid();
            }

            // Verify VM is running
            if (vm.Status != VmStatus.Running)
            {
                return BadRequest(ApiResponse<SshCertificateResponse>.Fail(
                    "VM_NOT_RUNNING",
                    $"VM must be running to issue certificate (current status: {vm.Status})"));
            }

            // Validate wallet signature if provided
            if (!string.IsNullOrEmpty(request.WalletSignature))
            {
                var isValidSignature = _walletSshKeyService.VerifyWalletSignature(
                    user.WalletAddress,
                    _walletSshKeyService.GetSshKeyDerivationMessage(),
                    request.WalletSignature);

                if (!isValidSignature)
                {
                    return BadRequest(ApiResponse<SshCertificateResponse>.Fail(
                        "INVALID_SIGNATURE",
                        "Invalid wallet signature for SSH key derivation"));
                }
            }

            // Issue certificate (handles both cases automatically)
            var validity = TimeSpan.FromSeconds(
                Math.Clamp(request.TtlSeconds, 300, 86400)); // 5 min to 24 hours

            var certResult = await _certificateService.IssueCertificateAsync(
                user,
                vm,
                request.WalletSignature,
                validity,
                HttpContext.RequestAborted);

            // Look up node to get public IP
            var nodeIp = "";
            if (!string.IsNullOrEmpty(vm.NodeId))
            {
                var node = await _nodeService.GetNodeAsync(vm.NodeId);
                nodeIp = node?.PublicIp ?? "";
            }

            var response = new SshCertificateResponse
            {
                Certificate = certResult.Certificate.CertificateData,
                PublicKey = certResult.PublicKey,
                Fingerprint = certResult.Certificate.Fingerprint,
                ValidFrom = certResult.Certificate.ValidFrom,
                ValidUntil = certResult.Certificate.ValidUntil,
                Principals = certResult.Certificate.Principals,
                IsWalletDerived = certResult.IsWalletDerived,
                VmIp = vm.NetworkConfig?.PrivateIp ?? "",
                NodeIp = nodeIp,
                NodePort = 2222 // Alternative SSH port
            };

            // Only include private key for wallet-derived keys
            if (certResult.IsWalletDerived && !string.IsNullOrEmpty(certResult.PrivateKey))
            {
                response.PrivateKey = certResult.PrivateKey;
                
                _logger.LogInformation(
                    "Wallet-derived SSH certificate issued for VM {VmId}, valid until {ValidUntil}",
                    vmId, response.ValidUntil);
            }
            else
            {
                _logger.LogInformation(
                    "SSH certificate issued for VM {VmId} using user's registered key, valid until {ValidUntil}",
                    vmId, response.ValidUntil);
            }

            return Ok(ApiResponse<SshCertificateResponse>.Ok(response));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(ApiResponse<SshCertificateResponse>.Fail(
                "UNAUTHORIZED", ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ApiResponse<SshCertificateResponse>.Fail(
                "INVALID_OPERATION", ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to issue SSH certificate for VM {VmId}", vmId);
            return StatusCode(500, ApiResponse<SshCertificateResponse>.Fail(
                "INTERNAL_ERROR", "Failed to issue SSH certificate"));
        }
    }

    /// <summary>
    /// Get the message that should be signed for SSH key derivation
    /// </summary>
    [HttpGet("derivation-message")]
    [ProducesResponseType(typeof(ApiResponse<string>), 200)]
    public ActionResult<ApiResponse<string>> GetDerivationMessage()
    {
        var message = _walletSshKeyService.GetSshKeyDerivationMessage();
        return Ok(ApiResponse<string>.Ok(message));
    }

    /// <summary>
    /// Get connection instructions for SSH access
    /// </summary>
    [HttpGet("connection-info")]
    [ProducesResponseType(typeof(ApiResponse<SshConnectionInfo>), 200)]
    public async Task<ActionResult<ApiResponse<SshConnectionInfo>>> GetConnectionInfo(string vmId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized();
        }

        var vm = await _vmService.GetVmAsync(vmId);
        if (vm == null || vm.OwnerId != userId)
        {
            return NotFound();
        }

        var user = await _userService.GetUserByIdAsync(userId);
        var hasUserKey = user?.SshKeys.Any() ?? false;

        // Look up node to get public IP
        var nodeIp = "";
        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            var node = await _nodeService.GetNodeAsync(vm.NodeId);
            nodeIp = node?.PublicIp ?? "";
        }

        var info = new SshConnectionInfo
        {
            VmIp = vm.NetworkConfig?.PrivateIp ?? "",
            NodeIp = nodeIp,
            NodePort = 22,
            Username = "root",
            HasUserSshKey = hasUserKey,
            RequiresWalletSignature = !hasUserKey,
            SshCommand = GenerateSshCommand(nodeIp, vm.NetworkConfig?.PrivateIp ?? "", vm.Id, hasUserKey)
        };

        return Ok(ApiResponse<SshConnectionInfo>.Ok(info));
    }

    private string GenerateSshCommand(string nodeIp, string vmIp, string vmId, bool hasUserKey)
    {
        var shortVmId = vmId.Substring(0, 8);

        if (hasUserKey)
        {
            return $"ssh -i ~/.ssh/id_ed25519 -o CertificateFile=~/.ssh/decloud-{shortVmId}-cert.pub decloud@{nodeIp} ssh root@{vmIp}";  // ← Changed to root
        }
        else
        {
            return $"ssh -i ~/.ssh/decloud-wallet.pem -o CertificateFile=~/.ssh/decloud-{shortVmId}-cert.pub decloud@{nodeIp} ssh root@{vmIp}";  // ← Changed to root
        }
    }

    private string? GetUserId()
    {
        return User.FindFirst("sub")?.Value ??
               User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    }
}

#region DTOs

/// <summary>
/// Request for SSH certificate
/// </summary>
public class SshCertificateRequest
{
    /// <summary>
    /// Wallet signature for key derivation (optional if user has SSH key registered)
    /// Should sign the message from /api/vms/{vmId}/ssh/derivation-message
    /// </summary>
    public string? WalletSignature { get; set; }

    /// <summary>
    /// Certificate validity in seconds (default: 3600, min: 300, max: 86400)
    /// </summary>
    public int TtlSeconds { get; set; } = 3600;
}

/// <summary>
/// Response with SSH certificate
/// </summary>
public class SshCertificateResponse
{
    public string Certificate { get; set; } = "";
    public string? PrivateKey { get; set; }      // Only for wallet-derived keys
    public string PublicKey { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime ValidFrom { get; set; }
    public DateTime ValidUntil { get; set; }
    public List<string> Principals { get; set; } = new();
    public bool IsWalletDerived { get; set; }
    public string VmIp { get; set; } = "";
    public string NodeIp { get; set; } = "";
    public int NodePort { get; set; }
}

/// <summary>
/// SSH connection information
/// </summary>
public class SshConnectionInfo
{
    public string VmIp { get; set; } = "";
    public string NodeIp { get; set; } = "";
    public int NodePort { get; set; }
    public string Username { get; set; } = "";
    public bool HasUserSshKey { get; set; }
    public bool RequiresWalletSignature { get; set; }
    public string SshCommand { get; set; } = "";
}

#endregion
