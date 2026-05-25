using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

[ExcludeFromCodeCoverage]
public sealed class AskHumanTool(IHumanNotifier notifier) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "question": { "type": "string", "description": "The question to ask the human operator." }
          },
          "required": ["question"]
        }
        """).RootElement;

    public string Name => "ask_human";

    public string Description =>
        "Ask the human operator a question. The run suspends immediately after this call; " +
        "the human's answer is injected when the run resumes. " +
        "Use when you need clarification, approval, or information only a human can provide.";

    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var question = call.Arguments.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
        await notifier.NotifyAsync(new HumanInputRequest(context.TaskId, call.CallId, question), ct);
        return new ToolResult(call.CallId, question, IsPending: true);
    }
}
