using System.Text.Json.Serialization;

namespace Orchestrator.Models;

/// <summary>
/// A unit of work that the system is obligated to fulfill.
/// Obligations are declarative: they describe WHAT should happen, not HOW.
/// The reconciliation loop evaluates obligations, resolves dependencies,
/// and dispatches them to type-specific handlers.
///
/// Design principles:
///   1. Obligations are idempotent — re-executing a completed obligation is a no-op
///   2. Dependencies form a DAG — cycles are detected and rejected
///   3. Handlers are responsible for convergence, not the loop
///   4. Failed obligations retry with exponential backoff
///   5. Obligations are serialization-friendly (simple types only)
/// </summary>
public class Obligation
{
    /// <summary>
    /// Unique identifier for this obligation.
    /// Format: "{type}:{resourceId}:{suffix}" for deduplication.
    /// Example: "vm.provision:vm-abc123"
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Obligation type — determines which handler processes it.
    /// Convention: "{domain}.{action}" (e.g., "vm.provision", "ingress.register", "node.assign-relay")
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Resource type this obligation acts on (e.g., "vm", "node", "ingress")
    /// </summary>
    public string ResourceType { get; set; } = string.Empty;

    /// <summary>
    /// Resource identifier (e.g., VM ID, Node ID)
    /// </summary>
    public string ResourceId { get; set; } = string.Empty;

    /// <summary>
    /// Current status of this obligation
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ObligationStatus Status { get; set; } = ObligationStatus.Pending;

    /// <summary>
    /// Human-readable status message (errors, progress, etc.)
    /// </summary>
    public string? StatusMessage { get; set; }

    // ════════════════════════════════════════════════════════════════════════
    // Dependency Tracking
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// IDs of obligations this one depends on.
    /// This obligation won't execute until ALL dependencies are Completed.
    /// </summary>
    public List<string> DependsOn { get; set; } = new();

    /// <summary>
    /// IDs of obligations that were created as children of this one.
    /// When this obligation completes, children become eligible for execution.
    /// </summary>
    public List<string> ChildObligationIds { get; set; } = new();

    /// <summary>
    /// ID of the parent obligation that created this one (if any).
    /// </summary>
    public string? ParentId { get; set; }

    // ════════════════════════════════════════════════════════════════════════
    // Retry & Backoff
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Number of execution attempts so far
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Maximum number of attempts before marking as Failed.
    /// Default: 5
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Earliest time the next attempt can be made.
    /// Used for exponential backoff after failures.
    /// </summary>
    public DateTime? NextAttemptAfter { get; set; }

    /// <summary>
    /// When the last execution attempt was made
    /// </summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// Base backoff duration in seconds (doubles each retry).
    /// Default: 5 seconds → 5s, 10s, 20s, 40s, 80s
    /// </summary>
    public int BackoffBaseSeconds { get; set; } = 5;

    // ════════════════════════════════════════════════════════════════════════
    // Payload
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Type-specific data needed by the handler.
    /// Stored as string values for serialization compatibility.
    /// Example for vm.provision: { "nodeId": "node-xyz", "commandId": "cmd-123" }
    /// </summary>
    public Dictionary<string, string> Data { get; set; } = new();

    // ════════════════════════════════════════════════════════════════════════
    // Timestamps
    // ════════════════════════════════════════════════════════════════════════

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? FailedAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Absolute deadline — if not completed by this time, mark as Expired.
    /// Null = no deadline.
    /// </summary>
    public DateTime? Deadline { get; set; }

    // ════════════════════════════════════════════════════════════════════════
    // Priority
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Execution priority. Higher = executed first within the same dependency level.
    /// Default: 0 (normal). Use positive values for urgent obligations.
    /// </summary>
    public int Priority { get; set; }

    // ════════════════════════════════════════════════════════════════════════
    // Computed Properties
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// True if this obligation is in a terminal state (no further processing needed)
    /// </summary>
    [JsonIgnore]
    public bool IsTerminal => Status is ObligationStatus.Completed
        or ObligationStatus.Failed
        or ObligationStatus.Expired
        or ObligationStatus.Cancelled;

    /// <summary>
    /// True if this obligation is ready for its next attempt (backoff elapsed)
    /// </summary>
    [JsonIgnore]
    public bool IsReadyForAttempt =>
        !IsTerminal &&
        (!NextAttemptAfter.HasValue || DateTime.UtcNow >= NextAttemptAfter.Value);

    /// <summary>
    /// True if this obligation has exceeded its deadline
    /// </summary>
    [JsonIgnore]
    public bool IsExpired =>
        Deadline.HasValue && DateTime.UtcNow > Deadline.Value;

    /// <summary>
    /// True if this obligation has exhausted all retry attempts
    /// </summary>
    [JsonIgnore]
    public bool IsExhausted => AttemptCount >= MaxAttempts;

    /// <summary>
    /// Compute the next backoff delay based on attempt count
    /// </summary>
    public TimeSpan GetNextBackoff()
    {
        var seconds = BackoffBaseSeconds * Math.Pow(2, AttemptCount - 1);
        var maxSeconds = 300; // 5 minute cap
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }

    public override string ToString() =>
        $"[{Id}] {Type} ({Status}) resource={ResourceType}/{ResourceId} attempts={AttemptCount}/{MaxAttempts}";
}

/// <summary>
/// Obligation lifecycle states
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ObligationStatus
{
    /// <summary>Waiting for dependencies or first execution attempt</summary>
    Pending,

    /// <summary>Dependencies met, handler is actively working on it</summary>
    InProgress,

    /// <summary>Waiting for an external signal (e.g., command ack from node)</summary>
    WaitingForSignal,

    /// <summary>Successfully completed</summary>
    Completed,

    /// <summary>Failed after exhausting all retries</summary>
    Failed,

    /// <summary>Exceeded deadline without completing</summary>
    Expired,

    /// <summary>Explicitly cancelled (by user or system)</summary>
    Cancelled
}

/// <summary>
/// Result returned by an obligation handler after attempting execution
/// </summary>
public class ObligationResult
{
    /// <summary>
    /// Outcome of the execution attempt
    /// </summary>
    public ObligationOutcome Outcome { get; init; }

    /// <summary>
    /// Human-readable message
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// New obligations to create as a result of this execution.
    /// These become children of the current obligation.
    /// </summary>
    public List<Obligation>? SpawnedObligations { get; init; }

    /// <summary>
    /// Signal key to wait for (when Outcome is WaitingForSignal).
    /// Example: "command-ack:{commandId}"
    /// </summary>
    public string? SignalKey { get; init; }

    /// <summary>
    /// Updated data to merge into the obligation's Data dictionary
    /// </summary>
    public Dictionary<string, string>? UpdatedData { get; init; }

    // ── Factory methods ──

    public static ObligationResult Completed(string? message = null) => new()
    {
        Outcome = ObligationOutcome.Completed,
        Message = message
    };

    public static ObligationResult CompletedWithChildren(
        List<Obligation> children, string? message = null) => new()
    {
        Outcome = ObligationOutcome.Completed,
        Message = message,
        SpawnedObligations = children
    };

    public static ObligationResult InProgress(string? message = null,
        Dictionary<string, string>? data = null) => new()
    {
        Outcome = ObligationOutcome.InProgress,
        Message = message,
        UpdatedData = data
    };

    public static ObligationResult WaitForSignal(string signalKey, string? message = null,
        Dictionary<string, string>? data = null) => new()
    {
        Outcome = ObligationOutcome.WaitingForSignal,
        SignalKey = signalKey,
        Message = message,
        UpdatedData = data
    };

    public static ObligationResult Retry(string? message = null) => new()
    {
        Outcome = ObligationOutcome.Retry,
        Message = message
    };

    public static ObligationResult Fail(string message) => new()
    {
        Outcome = ObligationOutcome.PermanentFailure,
        Message = message
    };
}

/// <summary>
/// Possible outcomes from an obligation handler execution
/// </summary>
public enum ObligationOutcome
{
    /// <summary>Work is done — mark obligation as Completed</summary>
    Completed,

    /// <summary>Work started but not done — keep as InProgress, check again next tick</summary>
    InProgress,

    /// <summary>Waiting for external signal — park until signal arrives</summary>
    WaitingForSignal,

    /// <summary>Transient failure — retry with backoff</summary>
    Retry,

    /// <summary>Permanent failure — mark as Failed, do not retry</summary>
    PermanentFailure
}
