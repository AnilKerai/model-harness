using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Model;

/// <summary>
/// Transport abstraction over a chat-style model. The loop hands this a message list
/// and the currently available tools; the implementation translates them into its
/// provider-specific wire format and returns a <see cref="ModelResponse"/>.
/// </summary>
public interface IModelClient
{
    /// <summary>
    /// Sends <paramref name="messages"/> to the model with <paramref name="availableTools"/>
    /// declared, and returns the response. Pass an empty tool list to disable tool use
    /// (used during budget finalisation).
    /// </summary>
    Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct);
}
