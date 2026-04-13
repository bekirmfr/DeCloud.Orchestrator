namespace Orchestrator.Models;

/// <summary>
/// Extension methods for VirtualMachine that keep message-queue logic
/// out of the model class itself and out of every service that touches VMs.
///
/// Usage:
///   vm.PushMessage("Node went offline — classifying VM.", VmMessageLevel.Warning, "healthmonitor");
///   vm.PushMessage("Migrating to node abc123.", source: "scheduler");
/// </summary>
public static class VirtualMachineExtensions
{
    /// <summary>Maximum number of messages retained per VM.</summary>
    private const int MessageCap = 100;

    /// <summary>
    /// Append a message to the VM's message queue.
    /// Drops the oldest entry when the queue exceeds <see cref="MessageCap"/>.
    /// Also syncs <see cref="VirtualMachine.StatusMessage"/> with the latest text
    /// so existing callers that read StatusMessage still work without changes.
    /// </summary>
    public static void PushMessage(
        this VirtualMachine vm,
        string text,
        VmMessageLevel level = VmMessageLevel.Info,
        string source = "orchestrator")
    {
        vm.Messages.Add(new VmMessage
        {
            Timestamp = DateTime.UtcNow,
            Level = level,
            Source = source,
            Text = text
        });

        if (vm.Messages.Count > MessageCap)
            vm.Messages.RemoveAt(0);

        // Keep StatusMessage in sync — callers that read it directly still work.
        vm.StatusMessage = text;
    }

    /// <summary>
    /// Returns the most recent message, or null if the queue is empty.
    /// </summary>
    public static VmMessage? LatestMessage(this VirtualMachine vm)
        => vm.Messages.Count > 0 ? vm.Messages[^1] : null;

    /// <summary>
    /// Returns the last <paramref name="n"/> messages in chronological order
    /// (oldest first), suitable for rendering a timeline.
    /// </summary>
    public static IEnumerable<VmMessage> RecentMessages(this VirtualMachine vm, int n = 20)
        => vm.Messages.TakeLast(n);
}