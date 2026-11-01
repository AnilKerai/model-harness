using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Guides;

/// <summary>
/// Primes the model to interleave reasoning with action — the ReAct pattern.
/// The loop already executes the act/observe half (model emits tool calls, the
/// harness dispatches them and feeds results back); this guide elicits the
/// explicit Thought before each action and the Observation after each result
/// that make the reasoning trace inspectable and the decisions better.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ReActGuide : IGuide
{
    public string Name => "react";

    private const string Instructions = """


        ## Reason and act

        Work in explicit Thought → Action → Observation cycles while you are still calling tools:
        - Before calling a tool, state a one-line Thought: why this action and what you expect it to tell you.
        - After a tool result, when you will call another tool, open that turn with a one-line Observation: what the result tells you and how it changes your plan.
        - When you have enough to answer, stop calling tools and reply with the final answer only. Write no Observation for the last tool result: go straight to the answer. That reply goes to the user exactly as you write it, so it has no Thought or Observation lines, no labels, and no headings or separators that frame it as an answer.
        Keep each Thought and Observation to a single line — reasoning, not narration.
        """;

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt += Instructions;
        return Task.CompletedTask;
    }
}
