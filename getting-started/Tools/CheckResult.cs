using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace GettingStarted.Tools;

internal static class CheckResult
{
    internal static Task<ToolResult> Build(string callId, string result, string confidence, string reason)
    {
        var json = JsonSerializer.Serialize(new { result, confidence, reason });
        return Task.FromResult(new ToolResult(callId, json));
    }
}
