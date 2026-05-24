namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>
/// Delivery mechanism for human-in-the-loop questions raised by <c>ask_human</c>.
/// Implement this to surface questions through your preferred channel — stdin, Slack,
/// a webhook, an approval queue, etc. The default implementation for local development
/// is <c>ConsoleHumanChannel</c>.
/// </summary>
public interface IHumanChannel
{
    /// <summary>Sends <paramref name="question"/> through the channel and awaits the human's response.</summary>
    Task<string> AskAsync(string question, CancellationToken ct = default);
}
