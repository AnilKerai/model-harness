using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted;

/// <summary>
/// A deterministic stand-in model so the sample runs with no API key — it implements the
/// <see cref="IModelClient"/> port exactly like a real adapter. It walks the same fixed path a
/// real model would: load the skill, look up the order, draft a reply that leaks the customer's
/// email, then — once the PII sensor blocks that reply — restate it cleanly.
/// </summary>
public sealed class SupportScriptedModelClient : IModelClient
{
    private int _turn;

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var response = Interlocked.Increment(ref _turn) switch
        {
            1 => ToolTurn("Let me load the order-enquiry procedure first.", "skill_view", new { name = "answer-order-enquiry" }),
            2 => ToolTurn("I'll look up order A1001.", "get_order_status", new { orderId = "A1001" }),
            // Deliberately pastes the customer's email — the PII sensor blocks this at PostModelCall.
            3 => Final("Hi jordan.lee@example.com, thanks for reaching out! Your order A1001 is shipped and out for delivery today (placed 2 July 2026). — The Support Team"),
            // Fresh turn after the block: restate without the email.
            _ => Final("Hi there, thanks for reaching out! Your order A1001 is shipped and out for delivery today (placed 2 July 2026). If there's anything else we can help with, just let us know. — The Support Team"),
        };
        return Task.FromResult(response);
    }

    private static ModelResponse ToolTurn(string text, string tool, object args) => new()
    {
        Text = text,
        ToolCalls = [new ToolCall(Guid.NewGuid().ToString("n"), tool, JsonSerializer.SerializeToElement(args))],
        StopReason = StopReason.ToolUse,
        Usage = new Usage(120, 20),
        Cost = 0m,
        Model = "scripted",
        Provider = "none"
    };

    private static ModelResponse Final(string text) => new()
    {
        Text = text,
        ToolCalls = [],
        StopReason = StopReason.EndTurn,
        Usage = new Usage(90, 30),
        Cost = 0m,
        Model = "scripted",
        Provider = "none"
    };
}
