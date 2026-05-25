namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Fire-and-forget delivery channel for human-in-the-loop questions raised by <c>ask_human</c>.
/// Implement this to dispatch the request through your preferred channel — HTTP webhook, Slack,
/// a message bus, an approval queue, etc. The harness suspends immediately after calling
/// <see cref="NotifyAsync"/> and returns <see cref="Framework.State.AgentStatus.AwaitingHuman"/>.
/// The caller resumes the run by calling <see cref="Framework.State.AgentState.ResumeWithHumanAnswer"/>
/// and passing the updated state back to <c>HarnessLoop.RunAsync</c>.
/// </summary>
public interface IHumanNotifier
{
    /// <summary>Dispatches <paramref name="request"/> through the delivery channel. Must not block for a human response.</summary>
    Task NotifyAsync(HumanInputRequest request, CancellationToken ct = default);
}
