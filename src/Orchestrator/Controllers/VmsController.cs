using DeCloud.Shared.Enums;
using DeCloud.Shared.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Orchestrator.Interfaces;
using Orchestrator.Interfaces.VmScheduling;
using Orchestrator.Models;
using Orchestrator.Persistence;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Orchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class VmsController : ControllerBase
{
    private readonly IVmService _vmService;
    private readonly INodeService _nodeService;
    private readonly IConstraintEvaluator _constraintEvaluator;
    private readonly IVmSchedulingService _schedulingService;
    private readonly DataStore _dataStore;
    private readonly HttpClient _httpClient;
    private readonly ILogger<VmsController> _logger;

    public VmsController(
        IVmService vmService,
        INodeService nodeService,
        IConstraintEvaluator constraintEvaluator,
        IVmSchedulingService schedulingService,
        DataStore dataStore,
        HttpClient httpClient,
        ILogger<VmsController> logger)
    {
        _vmService = vmService;
        _nodeService = nodeService;
        _constraintEvaluator = constraintEvaluator;
        _schedulingService = schedulingService;
        _dataStore = dataStore;
        _httpClient = httpClient;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    /// <summary>
    /// Create a new VM
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CreateVmResponse>>> Create([FromBody] CreateVmRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
        {
            return Unauthorized(ApiResponse<CreateVmResponse>.Fail("UNAUTHORIZED", "User not authenticated"));
        }

        try
        {
            // Support targeted node deployment (user can select specific node from marketplace)
            var response = await _vmService.CreateVmAsync(userId, request, request.NodeId);

            if (string.IsNullOrEmpty(response.VmId))
            {
                // Propagate the specific code (e.g. TOS_NOT_ACCEPTED, QUOTA_EXCEEDED)
                // rather than a generic one, so callers can detect the cause.
                return BadRequest(ApiResponse<CreateVmResponse>.Fail(
                    response.Error ?? "CREATE_ERROR", response.Message));
            }

            return Ok(ApiResponse<CreateVmResponse>.Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating VM");
            return BadRequest(ApiResponse<CreateVmResponse>.Fail("CREATE_ERROR", ex.Message));
        }
    }

    /// <summary>
    /// List VMs for the authenticated user
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<ApiResponse<PagedResult<VmSummary>>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? status = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool sortDesc = false)
    {
        var userId = GetUserId();

        var filters = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status)) filters["status"] = status;

        var queryParams = new ListQueryParams(page, pageSize, sortBy, sortDesc, search, filters);
        var result = await _vmService.ListVmsAsync(userId, queryParams);

        return Ok(ApiResponse<PagedResult<VmSummaryDto>>.Ok(result));
    }

    /// <summary>
    /// Get a specific VM
    /// </summary>
    [HttpGet("{vmId}")]
    public async Task<ActionResult<ApiResponse<VmDetailResponse>>> Get(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmDetailResponse>.Fail("NOT_FOUND", "VM not found"));
        }

        // Check ownership (unless admin)
        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        // Get host node info
        Node? hostNode = null;
        if (!string.IsNullOrEmpty(vm.NodeId))
        {
            hostNode = await _dataStore.GetNodeAsync(vm.NodeId);
        }

        return Ok(ApiResponse<VmDetailResponse>.Ok(new VmDetailResponse(vm, hostNode)));
    }

    /// <summary>
    /// Perform an action on a VM (start, stop, restart, etc.)
    /// </summary>
    [HttpPost("{vmId}/action")]
    public async Task<ActionResult<ApiResponse<bool>>> Action(string vmId, [FromBody] VmActionRequest request)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        // Compliance hold: an administratively held VM cannot be started or resumed,
        // even by an admin via this endpoint. Lift it via POST /api/admin/compliance/resume-vm.
        if (vm.ComplianceHold && request.Action is VmAction.Start or VmAction.Resume)
        {
            return BadRequest(ApiResponse<bool>.Fail(
                "VM_HELD", "This VM is administratively held and cannot be started."));
        }

        // Validate action based on current state
        var validAction = (request.Action, vm.Status) switch
        {
            (VmAction.Start, VmStatus.Stopped) => true,
            (VmAction.Stop, VmStatus.Running) => true,
            (VmAction.ForceStop, VmStatus.Running) => true,
            (VmAction.Restart, VmStatus.Running) => true,
            (VmAction.Pause, VmStatus.Running) => true,
            (VmAction.Resume, VmStatus.Running) => vm.PowerState == VmPowerState.Paused,
            _ => false
        };

        if (!validAction)
        {
            return BadRequest(ApiResponse<bool>.Fail(
                "INVALID_ACTION",
                $"Cannot {request.Action} a VM in {vm.Status} state"));
        }

        var success = await _vmService.PerformVmActionAsync(vmId, request.Action, userId);
        if (!success)
        {
            return BadRequest(ApiResponse<bool>.Fail("ACTION_FAILED", "Failed to perform action"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Delete a VM
    /// </summary>
    [HttpDelete("{vmId}")]
    public async Task<ActionResult<ApiResponse<bool>>> Delete(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        // Compliance hold: a held VM is preserved for investigation and cannot be
        // deleted — by the owner or an admin via this endpoint. Lift it first via
        // POST /api/admin/compliance/resume-vm.
        if (vm.ComplianceHold)
        {
            return BadRequest(ApiResponse<bool>.Fail(
                "VM_HELD", "This VM is administratively held and cannot be deleted."));
        }

        var success = await _vmService.DeleteVmAsync(vmId, userId);
        if (!success)
        {
            return BadRequest(ApiResponse<bool>.Fail("DELETE_FAILED", "Failed to delete VM"));
        }

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Replace the scheduling constraints on a stranded VM.
    /// Only allowed when VM is in Error state — constraints are fixed once running.
    /// The migration scheduler picks up the change on its next cycle (≤10 seconds).
    ///
    /// Replaces <c>vm.Spec.Constraints</c> entirely with the supplied list.
    /// Pass null or an empty list to remove all constraints (schedule to any node).
    /// Constraints are validated before saving; malformed entries are rejected.
    ///
    /// Example — broaden from a single region to two:
    /// <code>
    /// PATCH /api/vms/{id}/scheduling
    /// { "constraints": [
    ///     { "target": "node.locality.region", "operator": "in",
    ///       "value": ["eu-central", "eu-west"] }
    ///   ] }
    /// </code>
    /// </summary>
    [HttpPatch("{vmId}/scheduling")]
    public async Task<ActionResult<ApiResponse<bool>>> UpdateSchedulingConstraints(
        string vmId,
        [FromBody] UpdateSchedulingRequest request)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
            return NotFound(ApiResponse<bool>.Fail("NOT_FOUND", "VM not found"));

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
            return Forbid();

        if (vm.Status != VmStatus.Error)
            return BadRequest(ApiResponse<bool>.Fail(
                "INVALID_STATE",
                "Scheduling constraints can only be changed when the VM is in Error state."));

        var incoming = request.Constraints ?? new List<Constraint>();

        if (incoming.Count > 0)
        {
            var validationError = _constraintEvaluator.ValidateSet(incoming);
            if (validationError is not null)
                return BadRequest(ApiResponse<bool>.Fail("INVALID_CONSTRAINT", validationError));
            // Unwrap JsonElement values before MongoDB persistence.
            foreach (var c in incoming) c.NormalizeValue();
        }

        vm.Spec.Constraints = incoming.Count > 0 ? incoming : null;
        vm.StatusMessage = incoming.Count == 0
            ? "Scheduling constraints cleared — migrating to any eligible node."
            : $"Scheduling constraints updated ({incoming.Count} constraint(s)) — awaiting eligible node.";
        vm.UpdatedAt = DateTime.UtcNow;

        await _dataStore.SaveVmAsync(vm);

        _logger.LogInformation(
            "VM {VmId}: scheduling constraints updated ({Count} constraint(s)) by user {UserId}",
            vmId, incoming.Count, userId);

        return Ok(ApiResponse<bool>.Ok(true));
    }

    public record UpdateSchedulingRequest(List<Constraint>? Constraints);

    /// <summary>
    /// Live eligibility preview for a constraint set.
    ///
    /// Returns the count of currently-online nodes that satisfy the supplied
    /// constraints at the requested quality tier, plus the top rejection
    /// reasons among ineligible nodes.
    ///
    /// Intentionally uses a minimal VmSpec (1 vCPU / 256 MB / 1 GB) so that
    /// resource-capacity filters (FILTER 6/7) do not mask constraint results.
    /// This endpoint answers "how many nodes satisfy my placement requirements"
    /// — not "how many nodes can host my exact VM right now". The quality tier
    /// is honoured via the derived node.tier constraint evaluated in FILTER 10,
    /// which is relevant to any deployment.
    ///
    /// The constraint builder debounces calls to 500 ms to limit scheduler load.
    /// Note: each call runs the full filter chain across all online nodes —
    /// avoid calling in tight loops.
    /// </summary>
    [HttpPost("scheduling/preview")]
    public async Task<ActionResult<ApiResponse<SchedulingPreviewResponse>>> SchedulingPreview(
        [FromBody] SchedulingPreviewRequest request,
        CancellationToken ct)
    {
        if (request.Constraints is { Count: > 0 })
        {
            var validationError = _constraintEvaluator.ValidateSet(request.Constraints);
            if (validationError is not null)
                return BadRequest(ApiResponse<SchedulingPreviewResponse>.Fail(
                    "INVALID_CONSTRAINT", validationError));
        }

        // Resolve quality tier — accept numeric ("1") or named ("Standard") strings.
        var tier = Enum.TryParse<QualityTier>(request.QualityTier, ignoreCase: true, out var parsed)
            ? parsed
            : QualityTier.Standard;

        // Minimal spec: constraints and tier are all that matter for this preview.
        // ComputePointCost = 0 (default) means the capacity-points check always
        // passes — resource-based rejections don't mask constraint mismatches.
        var previewSpec = new VmSpec
        {
            VirtualCpuCores = 1,
            MemoryBytes = 256L * 1024 * 1024,
            DiskBytes = 1L * 1024 * 1024 * 1024,
            QualityTier = tier,
            GpuMode = GpuMode.None,
            ReplicationFactor = 0,
            Constraints = request.Constraints
        };

        var scored = await _schedulingService.GetScoredNodesForVmAsync(
            previewSpec, ct);

        var totalOnline = scored.Count;
        var eligible = scored.Count(sn => sn.RejectionReason is null);
        var rejected = totalOnline - eligible;

        // Normalize rejection reasons by stripping the per-node actual value
        // so that structurally identical rejections collapse into one count.
        //   raw:        "node.locality.country (BR) not_in [DE, FR]"
        //   normalized: "node.locality.country not_in [DE, FR]"
        // Non-constraint reasons (e.g. "Load average too high: 9.5 (max: 8.0)")
        // are also normalized: "Load average too high: 9.5"
        var rejectionReasons = scored
            .Where(sn => sn.RejectionReason is not null)
            .Select(sn => NormalizeRejectionReason(sn.RejectionReason!))
            .GroupBy(r => r, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => new RejectionReasonCount(g.Count(), g.Key))
            .ToList();

        return Ok(ApiResponse<SchedulingPreviewResponse>.Ok(
            new SchedulingPreviewResponse(totalOnline, eligible, rejected, rejectionReasons)));
    }

    // Strips " (actual_value)" from the middle of a rejection reason string.
    private static string NormalizeRejectionReason(string reason) =>
        Regex.Replace(reason, @"\s*\([^)]+\)", string.Empty).Trim();

    // QualityTier is accepted as a string to handle both numeric ("1") and
    // named ("Standard") representations from the frontend select element.
    // Enum.TryParse resolves both; unrecognised values default to Standard.
    public record SchedulingPreviewRequest(
        List<Constraint>? Constraints,
        string? QualityTier);

    public record SchedulingPreviewResponse(
        int TotalOnline,
        int Eligible,
        int Rejected,
        List<RejectionReasonCount> RejectionReasons);

    public record RejectionReasonCount(int Count, string Reason);

    /// <summary>
    /// Returns the registered constraint vocabulary for the dashboard constraint
    /// builder. The frontend uses this to populate dropdowns and render typed
    /// value inputs without hardcoding the vocabulary client-side.
    ///
    /// <c>targets</c> — sorted list of target name strings (backward-compatible).
    /// <c>targetTypes</c> — mapping from target name to value type string
    ///   ("String", "Numeric", "Boolean", "StringList"). Added in the same
    ///   response so callers don't need a second round-trip.
    /// <c>operators</c> — sorted list of operator name strings.
    ///
    /// Adding a new target in <c>ConstraintEvaluator.BuildTargetRegistry</c>
    /// makes it available here on the next request — no deploy required.
    /// </summary>
    [HttpGet("constraint-vocabulary")]
    [AllowAnonymous]
    public IActionResult GetConstraintVocabulary()
    {
        return Ok(new
        {
            targets = _constraintEvaluator.KnownTargets.Order(),
            targetTypes = _constraintEvaluator.TargetTypes,
            operators = _constraintEvaluator.KnownOperators.Order()
        });
    }

    /// <summary>
    /// Get VM metrics
    /// </summary>
    [HttpGet("{vmId}/metrics")]
    public async Task<ActionResult<ApiResponse<VmMetrics>>> GetMetrics(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmMetrics>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        if (vm.LatestMetrics == null)
        {
            return NotFound(ApiResponse<VmMetrics>.Fail("NO_METRICS", "No metrics available"));
        }

        return Ok(ApiResponse<VmMetrics>.Ok(vm.LatestMetrics));
    }

    /// <summary>
    /// Get console/terminal access info
    /// </summary>
    [HttpGet("{vmId}/console")]
    public async Task<ActionResult<ApiResponse<VmConsoleResponse>>> GetConsole(string vmId)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<VmConsoleResponse>.Fail("NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        if (vm.Status != VmStatus.Running)
        {
            return BadRequest(ApiResponse<VmConsoleResponse>.Fail("NOT_RUNNING", "VM is not running"));
        }

        // Return WebSocket URL for terminal
        var wsUrl = $"/hub/orchestrator?vmId={vmId}";

        return Ok(ApiResponse<VmConsoleResponse>.Ok(new VmConsoleResponse(
            vmId,
            wsUrl,
            vm.AccessInfo?.SshHost,
            vm.AccessInfo?.SshPort ?? 22,
            vm.AccessInfo?.VncHost,
            vm.AccessInfo?.VncPort ?? 5900
        )));
    }

    /// <summary>
    /// Get a diagnostic log stream for a VM (host-side console capture by default).
    ///
    /// Proxies to the node agent's <c>GET /api/vms/{vmId}/logs</c> after a
    /// JWT + ownership check. The default <c>Console</c> source returns the
    /// tail of the host-side <c>console.log</c> captured by libvirt's
    /// <c>&lt;log file='...'/&gt;</c> directive — works even when the qemu
    /// guest-agent never started (cloud-init parse error, apt failure,
    /// network never up, kernel panic). Other sources (cloud-init logs,
    /// journal) are accepted by the wire contract but the node currently
    /// returns <c>Unavailable</c> for them — they land in follow-up commits.
    ///
    /// Works for VMs in any state with a node assignment, including
    /// <c>Error</c> (the case that motivated the feature). For VMs without a
    /// node yet (<c>Pending</c>, <c>Scheduling</c>), returns 400. For
    /// <c>Deleted</c> VMs the on-disk log is gone — the node returns 404
    /// which surfaces here as <c>LOG_UNAVAILABLE</c>.
    /// </summary>
    [HttpGet("{vmId}/logs")]
    public async Task<ActionResult<ApiResponse<DiagnosticsResult>>> GetLogs(
        string vmId,
        [FromQuery] DiagnosticSource source = DiagnosticSource.Console,
        [FromQuery] int maxBytes = 0,
        CancellationToken ct = default)
    {
        var userId = GetUserId();
        var vm = await _dataStore.GetVmAsync(vmId);

        if (vm == null)
        {
            return NotFound(ApiResponse<DiagnosticsResult>.Fail(
                "NOT_FOUND", "VM not found"));
        }

        if (vm.OwnerId != userId && !User.IsInRole("admin"))
        {
            return Forbid();
        }

        if (string.IsNullOrEmpty(vm.NodeId))
        {
            return BadRequest(ApiResponse<DiagnosticsResult>.Fail(
                "NO_NODE",
                "VM is not assigned to a node yet — logs become available " +
                "once scheduling completes"));
        }

        var node = await _dataStore.GetNodeAsync(vm.NodeId);
        if (node == null)
        {
            return NotFound(ApiResponse<DiagnosticsResult>.Fail(
                "NODE_NOT_FOUND",
                "The node hosting this VM is no longer registered"));
        }

        var baseUrl = GetNodeAgentUrl(node);
        if (baseUrl == null)
        {
            return StatusCode(503, ApiResponse<DiagnosticsResult>.Fail(
                "NODE_UNREACHABLE",
                "The node hosting this VM has no reachable address"));
        }

        var url = $"{baseUrl}/api/vms/{vmId}/logs?source={source}&maxBytes={maxBytes}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _httpClient.GetAsync(url, cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);

            // The node returns DiagnosticsResult JSON whether the log is
            // available or not: 200 when content is present, 404 when the
            // source is unavailable. Either way we parse the structured
            // result and re-wrap in ApiResponse so the dashboard sees the
            // same envelope it sees on every other VM endpoint.
            DiagnosticsResult? result;
            try
            {
                result = JsonSerializer.Deserialize<DiagnosticsResult>(
                    body, DeCloud.Shared.Json.JsonOptions.Wire);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "Node {NodeId} returned malformed diagnostics response " +
                    "for VM {VmId}: {Body}",
                    node.Id, vmId, body);
                return StatusCode(502, ApiResponse<DiagnosticsResult>.Fail(
                    "NODE_BAD_RESPONSE",
                    "Node returned a malformed diagnostics response"));
            }

            if (result is null)
            {
                return StatusCode(502, ApiResponse<DiagnosticsResult>.Fail(
                    "NODE_BAD_RESPONSE",
                    "Node returned an empty diagnostics response"));
            }

            if (!result.Available)
            {
                return NotFound(ApiResponse<DiagnosticsResult>.Fail(
                    "LOG_UNAVAILABLE",
                    result.Message ?? "Log not available"));
            }

            return Ok(ApiResponse<DiagnosticsResult>.Ok(result));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Node {NodeId} timed out fetching logs for VM {VmId}",
                node.Id, vmId);
            return StatusCode(504, ApiResponse<DiagnosticsResult>.Fail(
                "NODE_TIMEOUT",
                "Node did not respond within 10 seconds"));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Network error fetching logs from node {NodeId} for VM {VmId}",
                node.Id, vmId);
            return StatusCode(503, ApiResponse<DiagnosticsResult>.Fail(
                "NODE_UNREACHABLE",
                $"Could not reach node: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Unexpected error fetching logs from node {NodeId} for VM {VmId}",
                node.Id, vmId);
            return StatusCode(500, ApiResponse<DiagnosticsResult>.Fail(
                "PROXY_ERROR",
                "Unexpected error retrieving logs"));
        }
    }

    /// <summary>
    /// Returns the node agent HTTP base URL for a node, handling CGNAT.
    /// Mirrors <c>LazysyncManager.GetNodeAgentUrl</c>. When this helper
    /// appears in a fourth place across the orchestrator, it should be
    /// extracted to a shared utility — premature extraction now would
    /// widen the PR for no compounding benefit.
    /// </summary>
    private static string? GetNodeAgentUrl(Node node)
    {
        if (string.IsNullOrEmpty(node.PublicIp)) return null;
        var port = node.AgentPort > 0 ? node.AgentPort : 5100;

        if (node.CgnatInfo != null && !string.IsNullOrEmpty(node.CgnatInfo.TunnelIp))
            return $"http://{node.CgnatInfo.TunnelIp}:{port}";

        return $"http://{node.PublicIp}:{port}";
    }

    /// <summary>
    /// Store the encrypted password for a VM.
    /// Called by dashboard after user encrypts the password client-side.
    /// </summary>
    [HttpPost("{vmId}/secure-password")]
    public async Task<ActionResult<ApiResponse<bool>>> SecurePassword(
        string vmId,
        [FromBody] SecurePasswordRequest request)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(ApiResponse<bool>.Fail("UNAUTHORIZED", "Not authenticated"));

        var result = await _vmService.SecurePasswordAsync(vmId, userId, request.EncryptedPassword);

        if (!result)
            return BadRequest(ApiResponse<bool>.Fail("SECURE_FAILED", "Failed to secure password"));

        return Ok(ApiResponse<bool>.Ok(true));
    }

    /// <summary>
    /// Get encrypted password for decryption client-side.
    /// </summary>
    [HttpGet("{vmId}/encrypted-password")]
    public async Task<ActionResult<ApiResponse<EncryptedPasswordResponse>>> GetEncryptedPassword(string vmId)
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId))
            return Unauthorized();

        var vm = await _dataStore.GetVmAsync(vmId);
        if (vm == null || vm.OwnerId != userId)
            return NotFound();

        if (string.IsNullOrEmpty(vm.Spec.WalletEncryptedPassword))
        {
            return BadRequest(ApiResponse<EncryptedPasswordResponse>.Fail("FETCH_FAILED", "Failed to fetch encrypted password"));
        }

        // Use positional constructor for the record
        return Ok(ApiResponse<EncryptedPasswordResponse>.Ok(
            new EncryptedPasswordResponse(vm.Spec.WalletEncryptedPassword, true)));
    }
}

// DTOs
public record SecurePasswordRequest(string EncryptedPassword);
public record EncryptedPasswordResponse(string? EncryptedPassword, bool IsSecured);

public record VmConsoleResponse(
    string VmId,
    string WebSocketUrl,
    string? SshHost,
    int SshPort,
    string? VncHost,
    int VncPort
);