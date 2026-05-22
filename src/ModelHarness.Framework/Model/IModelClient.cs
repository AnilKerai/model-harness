using ModelHarness.Framework.State;
using ModelHarness.Framework.Tools;

namespace ModelHarness.Framework.Model;

/// <summary>
/// Transport abstraction over a chat-style model. Implementations translate
/// <see cref="ToolDefinition"/> instances into their provider-specific tool format.
/// </summary>
public interface IModelClient
{
    Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct);
}
