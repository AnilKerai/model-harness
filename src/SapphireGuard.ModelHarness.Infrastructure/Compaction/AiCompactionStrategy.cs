using SapphireGuard.ModelHarness.Framework.Guides;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Compaction;

/// <summary>
/// Incremental fold compaction: summarises only the newly evicted steps on top of the prior rolling
/// summary, so summarisation cost stays flat as the run grows instead of re-summarising the whole
/// evicted head every turn. Persists the folded summary via <see cref="CompactionResult.UpdatedSummary"/>
/// so a resumed run continues without recompute. Pass a fast, cheap model (Haiku-class).
/// Fails open: on model error or empty output the prior summary is preserved and a bare note is
/// injected for the new slice (which is retried next turn), so a compaction failure never blocks the run.
/// </summary>
public sealed class AiCompactionStrategy(IModelClient modelClient) : ICompactionStrategy
{
    private static readonly Message SystemMessage = new(MessageRole.System,
        """
        You are a context compactor for an AI agent. You are given the running summary of earlier work (may be empty) and a new segment of the agent's trajectory. Produce a single updated summary that folds the new segment into the running summary in 3-6 concise sentences. Preserve the key facts, decisions, and outcomes the agent will still need; drop redundancy. Output only the summary, with no preamble.
        """);

    public async Task<CompactionResult> CompactAsync(CompactionRequest request, CancellationToken ct)
    {
        var prior = request.PriorSummary?.Text ?? "(none)";
        var newSegment = string.Join("\n\n", request.EvictedSteps.Select(RenderStep));
        var foldedCount = (request.PriorSummary?.FoldedStepCount ?? 0) + request.EvictedSteps.Count;

        try
        {
            var response = await modelClient.CallAsync(
                [
                    SystemMessage,
                    new Message(MessageRole.User,
                        $"Running summary so far:\n{prior}\n\nNew trajectory segment to fold in:\n{newSegment}")
                ],
                [],
                ct);

            if (string.IsNullOrWhiteSpace(response.Text))
                return FailOpen(request);

            var text = $"[Summary of {foldedCount} earlier step(s)] {response.Text.Trim()}";
            return new CompactionResult
            {
                InjectedText = text,
                UpdatedSummary = new RollingSummary(text, foldedCount),
                Usage = response.Usage,
                Cost = response.Cost
            };
        }
        catch
        {
            return FailOpen(request);
        }
    }

    // On failure keep the prior summary intact (don't advance the watermark) and inject a bare note
    // for the new slice — the guide re-evicts and retries it next turn, so nothing is lost.
    private static CompactionResult FailOpen(CompactionRequest request)
    {
        var note = $"[{request.EvictedSteps.Count} recent step(s) omitted — compaction unavailable]";
        var text = request.PriorSummary is { } prior ? $"{prior.Text}\n{note}" : note;
        return new CompactionResult { InjectedText = text, UpdatedSummary = request.PriorSummary };
    }

    // The strategy owns its rendering — it receives typed steps, not pre-rendered prompt text, so it
    // chooses what detail to keep.
    private static string RenderStep(Step step) => step switch
    {
        UserMessageStep um => $"User: {um.Content}",
        ModelCallStep mc => mc.Response.Text is { Length: > 0 } t ? $"Assistant: {t}" : "Assistant: (tool call)",
        ToolCallStep tc => $"Tool {tc.Call.ToolName} → {(tc.Result.IsError ? "ERROR " : "")}{tc.Result.Content}",
        SensorInterventionStep si => $"Harness note ({si.SensorName}): {si.Reason}",
        _ => step.ToString() ?? string.Empty
    };
}
