using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Compaction;

/// <summary>
/// Compaction strategy that calls a model to produce a concise prose summary of the
/// evicted trajectory segment, preserving more signal than a bare omission note.
/// Pass a fast, cheap model (Haiku-class) to keep compaction overhead low.
/// Fails open: if the model call fails or returns empty text, the standard omission
/// note is injected so the run is never blocked by a compaction failure.
/// </summary>
public sealed class AiCompactionStrategy(IModelClient modelClient) : ICompactionStrategy
{
    private static readonly Message SystemMessage = new(MessageRole.System,
        """
        You are a context compactor for an AI agent. Summarise the key facts, decisions, and outcomes from the provided agent trajectory segment in 3-5 concise sentences. Focus only on what the agent will still need to complete its task: what was tried, what was discovered, what decisions were made. Do not include preamble or meta-commentary — output the summary directly.
        """);

    public async Task<string> SummariseAsync(
        int evictedStepCount,
        IReadOnlyList<string> evictedContent,
        int remainingTokenBudget,
        CancellationToken ct)
    {
        try
        {
            var content = string.Join("\n\n", evictedContent);
            var response = await modelClient.CallAsync(
                [
                    SystemMessage,
                    new Message(MessageRole.User, $"Summarise this agent trajectory segment:\n\n{content}")
                ],
                [],
                ct);

            return string.IsNullOrWhiteSpace(response.Text)
                ? FallbackNote(evictedStepCount)
                : $"[Earlier context summary — {evictedStepCount} step(s) compacted] {response.Text.Trim()}";
        }
        catch
        {
            return FallbackNote(evictedStepCount);
        }
    }

    private static string FallbackNote(int count) =>
        $"[{count} earlier step(s) omitted — context window limit]";
}
