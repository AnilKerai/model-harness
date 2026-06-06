using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.Tools;

public sealed class GetDateTimeTool(Func<DateTimeOffset>? clock = null) : ITool
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);

    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","properties":{}}""").RootElement;

    public string Name => "get_date_time";

    public string Description =>
        "Returns the current UTC date and time in ISO 8601 format. " +
        "Use this whenever you need to know the current date or time.";

    public JsonElement InputSchema => Schema;

    public Task<ToolResult> ExecuteAsync(ToolCall call, ToolContext context, CancellationToken ct)
    {
        var utc = _clock().UtcDateTime;
        return Task.FromResult(new ToolResult(call.CallId, utc.ToString("s") + "Z"));
    }
}
