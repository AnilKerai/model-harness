using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

/// <summary>Returns a minimal single-message context for every state.</summary>
public sealed class StubContextBuilder : IContextBuilder
{
    public Task<ContextBuildResult> BuildAsync(AgentState state, IReadOnlyList<ITool> tools, CancellationToken ct)
    {
        var messages = new List<Message> { new(MessageRole.User, state.TaskText) };
        return Task.FromResult(new ContextBuildResult(messages, tools));
    }
}
