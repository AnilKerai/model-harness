using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SubAgent;

/// <summary>
/// Scripted model client for the research specialist agent.
/// Always returns a single final answer — no tool calls needed.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ResearchModelClient : IModelClient
{
    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct) =>
        Task.FromResult(new ModelResponse
        {
            Text = "Quantum computing uses quantum-mechanical phenomena such as superposition and " +
                   "entanglement to perform computations. Unlike classical bits (0 or 1), qubits can " +
                   "exist in both states simultaneously, enabling certain problems — such as integer " +
                   "factorisation and unstructured search — to be solved exponentially faster than any " +
                   "known classical algorithm.",
            ToolCalls = [],
            StopReason = StopReason.EndTurn,
            Usage = new Usage(InputTokens: 80, OutputTokens: 60),
            Cost = 0m
        });
}
