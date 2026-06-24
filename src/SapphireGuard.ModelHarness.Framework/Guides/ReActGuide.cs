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

        Work in explicit Thought → Action → Observation cycles:
        - Before calling a tool, state a one-line Thought: why this action and what you expect it to tell you.
        - After each tool result, state a one-line Observation: what it tells you and how it changes your plan.
        - When you have enough to answer, stop calling tools and give the final answer directly.
        Keep each Thought and Observation to a single line — reasoning, not narration.
        """;

    public Task ContributeAsync(ContextDraft draft, AgentState state, CancellationToken ct)
    {
        draft.SystemPrompt += Instructions;
        return Task.CompletedTask;
    }
}
