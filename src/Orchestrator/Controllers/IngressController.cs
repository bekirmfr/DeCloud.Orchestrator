using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Data;
using Orchestrator.Models;
using Orchestrator.Services;
using System.Net.Http.Json;
using System.Text.Json;

namespace Orchestrator.Controllers;

/// <summary>
/// API controller for managing ingress rules across nodes.
/// Proxies requests to the appropriate NodeAgent based on VM location.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class IngressController : ControllerBase
{
    private readonly DataStore _dataStore;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<IngressController> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public IngressController(
        DataStore dataStore,
        IHttpClientFactory httpClientFactory,
        ILogger<IngressController> logger)
    {
        _dataStore = dataStore;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get all ingress rules for the authenticated user
    /// </summary>
    [HttpGet]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressListResponse>>> GetAll()
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressListResponse>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        try
        {
            // Get user's VMs
            var userVms = _dataStore.VirtualMachines.Values
                .Where(vm => vm.OwnerId == userId || vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (userVms.Count == 0)
            {
                return Ok(ApiResponse<IngressListResponse>.Ok(new IngressListResponse(new List<IngressResponse>(), 0)));
            }

            // Collect ingress rules from all nodes hosting user's VMs
            var allIngress = new List<IngressResponse>();
            var nodeVmMap = userVms
                .Where(vm => !string.IsNullOrEmpty(vm.NodeId))
                .GroupBy(vm => vm.NodeId!);

            foreach (var nodeGroup in nodeVmMap)
            {
                var nodeId = nodeGroup.Key;
                if (!_dataStore.Nodes.TryGetValue(nodeId, out var node))
                    continue;

                var vmIds = nodeGroup.Select(vm => vm.Id).ToHashSet();

                try
                {
                    var client = CreateNodeClient(node);
                    var response = await client.GetAsync("/api/ingress");

                    if (response.IsSuccessStatusCode)
                    {
                        var nodeIngress = await response.Content.ReadFromJsonAsync<List<IngressResponse>>(JsonOptions);
                        if (nodeIngress != null)
                        {
                            // Filter to user's VMs and enrich with node info
                            var filtered = nodeIngress
                                .Where(i => vmIds.Contains(i.VmId))
                                .Select(i => i with
                                {
                                    NodeId = nodeId,
                                    NodePublicIp = node.PublicIp
                                });

                            allIngress.AddRange(filtered);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to fetch ingress from node {NodeId}", nodeId);
                }
            }

            return Ok(ApiResponse<IngressListResponse>.Ok(
                new IngressListResponse(allIngress, allIngress.Count)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching ingress rules");
            return StatusCode(500, ApiResponse<IngressListResponse>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Get all ingress rules for a specific VM
    /// </summary>
    [HttpGet("vm/{vmId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressListResponse>>> GetByVmId(string vmId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressListResponse>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        // Find VM and verify ownership
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<IngressListResponse>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            return Ok(ApiResponse<IngressListResponse>.Ok(new IngressListResponse(new List<IngressResponse>(), 0)));
        }

        // Get node and fetch ingress rules
        if (!_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<IngressListResponse>.Fail("NODE_NOT_FOUND", "Node not found"));
        }

        try
        {
            var client = CreateNodeClient(node);
            var response = await client.GetAsync($"/api/ingress/vm/{vmId}");

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                return StatusCode((int)response.StatusCode,
                    ApiResponse<IngressListResponse>.Fail("NODE_ERROR", error));
            }

            var ingress = await response.Content.ReadFromJsonAsync<List<IngressResponse>>(JsonOptions);

            // Enrich with node info
            var enriched = ingress?.Select(i => i with
            {
                NodeId = vm.NodeId,
                NodePublicIp = node.PublicIp
            }).ToList() ?? new List<IngressResponse>();

            return Ok(ApiResponse<IngressListResponse>.Ok(
                new IngressListResponse(enriched, enriched.Count)));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching ingress for VM {VmId}", vmId);
            return StatusCode(500, ApiResponse<IngressListResponse>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Get a specific ingress rule
    /// </summary>
    [HttpGet("{ingressId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressResponse>>> GetById(string ingressId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressResponse>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        // Search across nodes for this ingress rule
        foreach (var node in _dataStore.Nodes.Values.Where(n => n.Status == NodeStatus.Online))
        {
            try
            {
                var client = CreateNodeClient(node);
                var response = await client.GetAsync($"/api/ingress/{ingressId}");

                if (response.IsSuccessStatusCode)
                {
                    var ingress = await response.Content.ReadFromJsonAsync<IngressResponse>(JsonOptions);
                    if (ingress != null)
                    {
                        // Verify ownership via VM
                        if (_dataStore.VirtualMachines.TryGetValue(ingress.VmId, out var vm))
                        {
                            if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
                            {
                                return Forbid();
                            }
                        }

                        return Ok(ApiResponse<IngressResponse>.Ok(ingress with
                        {
                            NodeId = node.Id,
                            NodePublicIp = node.PublicIp
                        }));
                    }
                }
            }
            catch
            {
                // Try next node
            }
        }

        return NotFound(ApiResponse<IngressResponse>.Fail("NOT_FOUND", "Ingress rule not found"));
    }

    /// <summary>
    /// Create a new ingress rule
    /// </summary>
    [HttpPost]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressOperationResult>>> Create(
        [FromBody] CreateIngressRequest request)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        var walletAddress = User.FindFirst("wallet")?.Value ?? userId;

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressOperationResult>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        // Find VM and verify ownership
        if (!_dataStore.VirtualMachines.TryGetValue(request.VmId, out var vm))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            return BadRequest(ApiResponse<IngressOperationResult>.Fail(
                "VM_NOT_SCHEDULED", "VM is not scheduled on any node"));
        }

        if (vm.Status != VmStatus.Running)
        {
            return BadRequest(ApiResponse<IngressOperationResult>.Fail(
                "VM_NOT_RUNNING", "VM must be running to create ingress rules"));
        }

        // Get node
        if (!_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("NODE_NOT_FOUND", "Node not found"));
        }

        _logger.LogInformation(
            "Creating ingress: {Domain} → VM {VmId} on node {NodeId}",
            request.Domain, request.VmId, node.Id);

        try
        {
            var client = CreateNodeClient(node);

            // Add owner wallet header
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/ingress")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            httpRequest.Headers.Add("X-Owner-Wallet", walletAddress);

            var response = await client.SendAsync(httpRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Node rejected ingress creation: {Status} - {Content}",
                    response.StatusCode, content);

                return StatusCode((int)response.StatusCode,
                    ApiResponse<IngressOperationResult>.Fail("NODE_ERROR", content));
            }

            var result = JsonSerializer.Deserialize<IngressOperationResult>(content, JsonOptions);

            // Enrich response with node info
            if (result?.Ingress != null)
            {
                result = result with
                {
                    Ingress = result.Ingress with
                    {
                        NodeId = node.Id,
                        NodePublicIp = node.PublicIp
                    }
                };
            }

            _logger.LogInformation("✓ Ingress created: {Domain} → {NodeIp}", request.Domain, node.PublicIp);

            return Ok(ApiResponse<IngressOperationResult>.Ok(result!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating ingress for VM {VmId}", request.VmId);
            return StatusCode(500, ApiResponse<IngressOperationResult>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Update an existing ingress rule
    /// </summary>
    [HttpPatch("{ingressId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressOperationResult>>> Update(
        string ingressId,
        [FromBody] UpdateIngressRequest request,
        [FromQuery] string vmId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        var walletAddress = User.FindFirst("wallet")?.Value ?? userId;

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressOperationResult>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        // Find VM to get node
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(vm.NodeId) || !_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("NODE_NOT_FOUND", "Node not found"));
        }

        try
        {
            var client = CreateNodeClient(node);

            var httpRequest = new HttpRequestMessage(HttpMethod.Patch, $"/api/ingress/{ingressId}")
            {
                Content = JsonContent.Create(request, options: JsonOptions)
            };
            httpRequest.Headers.Add("X-Owner-Wallet", walletAddress);

            var response = await client.SendAsync(httpRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode,
                    ApiResponse<IngressOperationResult>.Fail("NODE_ERROR", content));
            }

            var result = JsonSerializer.Deserialize<IngressOperationResult>(content, JsonOptions);

            return Ok(ApiResponse<IngressOperationResult>.Ok(result!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating ingress {IngressId}", ingressId);
            return StatusCode(500, ApiResponse<IngressOperationResult>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Delete an ingress rule
    /// </summary>
    [HttpDelete("{ingressId}")]
    [Authorize]
    public async Task<ActionResult<ApiResponse<IngressOperationResult>>> Delete(
        string ingressId,
        [FromQuery] string vmId)
    {
        var userId = User.FindFirst("sub")?.Value ?? User.FindFirst("wallet")?.Value;
        var walletAddress = User.FindFirst("wallet")?.Value ?? userId;

        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<IngressOperationResult>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        // Find VM to get node
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !vm.OwnerWallet.Equals(userId, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(vm.NodeId) || !_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<IngressOperationResult>.Fail("NODE_NOT_FOUND", "Node not found"));
        }

        _logger.LogInformation("Deleting ingress {IngressId} on node {NodeId}", ingressId, node.Id);

        try
        {
            var client = CreateNodeClient(node);

            var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/ingress/{ingressId}");
            httpRequest.Headers.Add("X-Owner-Wallet", walletAddress);

            var response = await client.SendAsync(httpRequest);
            var content = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return StatusCode((int)response.StatusCode,
                    ApiResponse<IngressOperationResult>.Fail("NODE_ERROR", content));
            }

            var result = JsonSerializer.Deserialize<IngressOperationResult>(content, JsonOptions);

            _logger.LogInformation("✓ Ingress {IngressId} deleted", ingressId);

            return Ok(ApiResponse<IngressOperationResult>.Ok(result!));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting ingress {IngressId}", ingressId);
            return StatusCode(500, ApiResponse<IngressOperationResult>.Fail("INTERNAL_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// Get DNS setup instructions for a domain
    /// </summary>
    [HttpGet("dns-instructions")]
    [Authorize]
    public ActionResult<ApiResponse<object>> GetDnsInstructions([FromQuery] string vmId)
    {
        if (!_dataStore.VirtualMachines.TryGetValue(vmId, out var vm))
        {
            return NotFound(ApiResponse<object>.Fail("VM_NOT_FOUND", "VM not found"));
        }

        if (string.IsNullOrEmpty(vm.NodeId) || !_dataStore.Nodes.TryGetValue(vm.NodeId, out var node))
        {
            return NotFound(ApiResponse<object>.Fail("NODE_NOT_FOUND", "VM is not assigned to a node"));
        }

        var instructions = new
        {
            nodePublicIp = node.PublicIp,
            steps = new[]
            {
                "1. Log in to your domain registrar or DNS provider",
                $"2. Create an A record pointing your domain to: {node.PublicIp}",
                $"3. Example: myapp.example.com → A → {node.PublicIp}",
                "4. Wait for DNS propagation (usually 5-30 minutes, can take up to 48 hours)",
                "5. Create the ingress rule using POST /api/ingress",
                "6. TLS certificates will be automatically provisioned via Let's Encrypt"
            },
            example = new
            {
                type = "A",
                name = "myapp.example.com",
                value = node.PublicIp,
                ttl = 3600
            },
            notes = new[]
            {
                "Ensure your domain points to the correct node IP before creating the ingress rule",
                "Let's Encrypt requires the domain to be publicly accessible for certificate issuance",
                "Wildcard domains are not currently supported"
            }
        };

        return Ok(ApiResponse<object>.Ok(instructions));
    }

    private HttpClient CreateNodeClient(Node node)
    {
        var client = _httpClientFactory.CreateClient();
        var port = node.AgentPort > 0 ? node.AgentPort : 5100;
        client.BaseAddress = new Uri($"http://{node.PublicIp}:{port}");
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }
}