namespace Orchestrator.Models;

public enum EnforcementActionType
{
    Suspend,
    Unsuspend,
    Block,
    Unblock,
    TerminateVms
}

/// <summary>
/// Append-only audit record of an enforcement action. Every suspend / unsuspend /
/// block / unblock is recorded with the actor, reason, and an external reference so
/// enforcement is accountable and reversible-with-history. Never updated in place.
/// </summary>
public class EnforcementAction
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Target wallet (checksum), or "(bulk)" for a bulk import.</summary>
    public string WalletAddress { get; set; } = string.Empty;

    public EnforcementActionType Type { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string? Reference { get; set; }

    /// <summary>Admin wallet that performed the action, or "system".</summary>
    public string ActorWallet { get; set; } = string.Empty;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Action-specific context, e.g. {"source":"Sanctions"} or {"vmsStopped":"3"}.</summary>
    public Dictionary<string, string>? Metadata { get; set; }
}
