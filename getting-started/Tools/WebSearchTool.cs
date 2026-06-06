using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

[ExcludeFromCodeCoverage]
public sealed class WebSearchTool(string apiKey, HttpClient http) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "query": {
              "type": "string",
              "description": "The search query to run."
            },
            "limit": {
              "type": "integer",
              "description": "Maximum number of results to return (1–10).",
              "minimum": 1,
              "maximum": 10,
              "default": 5
            }
          },
          "required": ["query"]
        }
        """).RootElement;

    public string Name => "web_search";
    public string Description => "Search the public web. Returns a list of results with title, URL, and snippet.";
    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var query = call.Arguments.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var limit = call.Arguments.TryGetProperty("limit", out var l) ? l.GetInt32() : 5;

        var url = $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={limit}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-Subscription-Token", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(call.CallId, $"web_search failed: {ex.Message}", IsError: true);
        }

        if (!response.IsSuccessStatusCode)
            return new ToolResult(call.CallId, $"web_search returned {(int)response.StatusCode} {response.ReasonPhrase}", IsError: true);

        var body = await response.Content.ReadAsStringAsync(ct);

        using var doc = JsonDocument.Parse(body);
        var results = doc.RootElement
            .TryGetProperty("web", out var web) && web.TryGetProperty("results", out var arr)
            ? arr.EnumerateArray().ToList()
            : [];

        if (results.Count == 0)
            return new ToolResult(call.CallId, "No results found.");

        var sb = new StringBuilder();
        var i = 1;
        foreach (var r in results)
        {
            var title   = r.TryGetProperty("title", out var t)       ? t.GetString() : "(no title)";
            var resUrl  = r.TryGetProperty("url", out var u)         ? u.GetString() : "(no url)";
            var snippet = r.TryGetProperty("description", out var d) ? d.GetString() : "(no snippet)";
            sb.AppendLine($"{i}. {title}");
            sb.AppendLine($"   {resUrl}");
            sb.AppendLine($"   {snippet}");
            sb.AppendLine();
            i++;
        }

        return new ToolResult(call.CallId, sb.ToString().TrimEnd());
    }
}
