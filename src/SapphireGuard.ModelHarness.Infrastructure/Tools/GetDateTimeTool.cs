using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

public sealed class GetDateTimeTool(TimeProvider timeProvider) : ITool
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{}}""").RootElement;

    public string Name => "get_date_time";

    public string Description =>
        "Returns the current UTC date and time in ISO 8601 format. " +
        "Use this whenever you need to know the current date or time.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var utc = timeProvider.GetUtcNow().UtcDateTime;
        return Task.FromResult(new ToolResult(call.CallId, utc.ToString("s") + "Z"));
    }
}
