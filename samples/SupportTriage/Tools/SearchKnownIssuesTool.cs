using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.SupportTriage.Tools;

[ExcludeFromCodeCoverage]
public sealed class SearchKnownIssuesTool : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "keywords": { "type": "string", "description": "Space-separated keywords to search for in known issues." }
          },
          "required": ["keywords"]
        }
        """).RootElement;

    public string Name => "search_known_issues";
    public string Description => "Search the known-issues database for issues matching the given keywords. Returns matching issues with their workarounds.";
    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var keywords = call.Arguments.TryGetProperty("keywords", out var k) ? k.GetString() ?? "" : "";
        var issues = TicketDatabase.SearchIssues(keywords);

        if (issues.Count == 0)
            return Task.FromResult(new ToolResult(call.CallId, "No known issues matched those keywords."));

        var sb = new StringBuilder();
        foreach (var issue in issues)
        {
            sb.AppendLine($"[{issue.Id}] {issue.Title}");
            sb.AppendLine($"  Workaround: {issue.Workaround}");
        }

        return Task.FromResult(new ToolResult(call.CallId, sb.ToString().TrimEnd()));
    }
}
