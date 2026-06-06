using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

[ExcludeFromCodeCoverage]
public sealed partial class WebFetchTool(HttpClient http) : ITool
{
    private const int MaxChars = 8_000;

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """
        {
          "type": "object",
          "properties": {
            "url": {
              "type": "string",
              "format": "uri",
              "description": "The absolute URL of the web page to fetch."
            }
          },
          "required": ["url"]
        }
        """).RootElement;

    public string Name => "web_fetch";
    public string Description => "Fetch the text content of a public web page by URL.";
    public JsonElement InputSchema => Schema;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var url = call.Arguments.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";

        string body;
        try
        {
            body = await http.GetStringAsync(url, ct);
        }
        catch (Exception ex)
        {
            return new ToolResult(call.CallId, $"web_fetch failed: {ex.Message}", IsError: true);
        }

        var text = StripHtml(body);

        if (text.Length > MaxChars)
            text = text[..MaxChars] + "\n[truncated]";

        return new ToolResult(call.CallId, text);
    }

    private static string StripHtml(string html)
    {
        var s = ScriptStylePattern().Replace(html, " ");
        s = TagPattern().Replace(s, " ");
        s = WhitespacePattern().Replace(s, " ").Trim();
        return s;
    }

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</(script|style)>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStylePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();
}
