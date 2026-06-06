using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

[ExcludeFromCodeCoverage]
public sealed class CreateJiraTicketTool : ITool
{
    private static int _counter = 1000;

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "summary": {
              "type": "string",
              "description": "Short summary of the issue (becomes the Jira ticket title)."
            },
            "description": {
              "type": "string",
              "description": "Full details of the concern for the credit control team."
            },
            "priority": {
              "type": "string",
              "enum": ["High", "Medium", "Low"],
              "description": "Ticket priority. Use High for hard red flags."
            }
          },
          "required": ["summary", "description", "priority"]
        }
        """).RootElement;

    public string Name => "create_jira_ticket";
    public string Description => "Raise a Jira ticket to escalate a credit control concern for human review. Use this whenever a hard red flag is identified during verification.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var summary     = call.Arguments.TryGetProperty("summary", out var s)     ? s.GetString() ?? "" : "";
        var description = call.Arguments.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        var priority    = call.Arguments.TryGetProperty("priority", out var p)    ? p.GetString() ?? "Medium" : "Medium";

        var ticketId = $"CREDIT-{Interlocked.Increment(ref _counter)}";

        var prevColour = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("  ┌─ Jira ticket raised ──────────────────────────────");
        Console.WriteLine($"  │  ID       : {ticketId}");
        Console.WriteLine($"  │  Priority : {priority}");
        Console.WriteLine($"  │  Summary  : {summary}");
        if (!string.IsNullOrWhiteSpace(description))
            Console.WriteLine($"  │  Details  : {description}");
        Console.WriteLine("  └────────────────────────────────────────────────────");
        Console.ForegroundColor = prevColour;

        return Task.FromResult(new ToolResult(call.CallId,
            $"{{\"ticket_id\":\"{ticketId}\",\"status\":\"created\",\"message\":\"Ticket {ticketId} has been raised and assigned to the credit control queue.\"}}"));
    }
}
