namespace SapphireGuard.ModelHarness.Framework.State;

/// <summary>
/// Why the model stopped producing tokens for a given response. Only <see cref="EndTurn"/>,
/// <see cref="StopSequence"/> and <see cref="ToolUse"/> mean the response ended normally; the loop
/// treats every other value — including any it does not recognise — as cut short, and returns
/// <see cref="AgentStatus.PartialResult"/> rather than <see cref="AgentStatus.Done"/>.
/// </summary>
/// <remarks>
/// Checkpoints persist this as an integer, so values are explicit and new members are only ever
/// appended. Model adapters should map a provider reason they do not recognise to <see cref="Other"/>,
/// never to <see cref="EndTurn"/>.
/// </remarks>
public enum StopReason
{
    /// <summary>The model finished its response naturally.</summary>
    EndTurn = 0,

    /// <summary>The model is calling one or more tools.</summary>
    ToolUse = 1,

    /// <summary>The response was truncated at the request's output-token limit.</summary>
    MaxTokens = 2,

    /// <summary>The model emitted one of the request's stop sequences.</summary>
    StopSequence = 3,

    /// <summary>A reason the adapter did not recognise. Treated as cut short.</summary>
    Other = 4,

    /// <summary>The model declined to respond (Anthropic <c>refusal</c>).</summary>
    Refusal = 5,

    /// <summary>The provider's content filter withheld or cut the response (Azure OpenAI <c>content_filter</c>).</summary>
    ContentFilter = 6,

    /// <summary>
    /// The response filled the model's context window before reaching the output-token limit
    /// (Anthropic <c>model_context_window_exceeded</c>). Truncated, like <see cref="MaxTokens"/>.
    /// </summary>
    ContextWindowExceeded = 7
}
