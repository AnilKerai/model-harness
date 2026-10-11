using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Ollama;

// End to end through the client: a recorded /api/chat body in, a mapped StopReason out. Covers what the
// OllamaWireFormat tests cannot — that OllamaSharp surfaces done_reason on a non-streamed response, and that
// the client actually feeds it to the mapping.
public sealed class OllamaModelClientStopReasonTests
{
    private sealed class CannedResponse(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }

    // The shape of a non-streamed Ollama chat response; done_reason is omitted when null, as Ollama does.
    // Minified because Ollama writes one JSON object per line and OllamaSharp reads the body line by line.
    private static string Body(string? doneReason, bool withToolCall)
    {
        var toolCalls = withToolCall
            ? """, "tool_calls": [{ "function": { "name": "lookup", "arguments": { "q": "x" } } }]"""
            : "";
        var reason = doneReason is null ? "" : $"""  "done_reason": "{doneReason}",""";
        return JsonNode.Parse($$"""
            {
              "model": "qwen2.5:7b",
              "created_at": "2026-10-11T09:00:00Z",
              "message": { "role": "assistant", "content": "partial"{{toolCalls}} },
              "done": true,
            {{reason}}
              "total_duration": 1, "load_duration": 1,
              "prompt_eval_count": 12, "prompt_eval_duration": 1,
              "eval_count": 5, "eval_duration": 1
            }
            """)!.ToJsonString();
    }

    [Theory]
    [InlineData("stop", false, StopReason.EndTurn)]
    [InlineData("stop", true, StopReason.ToolUse)]
    [InlineData("length", false, StopReason.MaxTokens)]
    [InlineData("length", true, StopReason.MaxTokens)]
    [InlineData(null, false, StopReason.EndTurn)]   // a server before v0.1.35 sends no done_reason
    [InlineData("unload", false, StopReason.Other)]
    public async Task CallAsync_MapsTheWireDoneReason(string? doneReason, bool withToolCall, StopReason expected)
    {
        var client = new OllamaModelClient(
            new OllamaClientOptions { ModelId = "qwen2.5:7b" },
            new CannedResponse(Body(doneReason, withToolCall)));

        var response = await client.CallAsync([new Message(MessageRole.User, "hi")], [], CancellationToken.None);

        Assert.Equal(expected, response.StopReason);
        Assert.Equal(new Usage(12, 5), response.Usage); // read from the same done chunk as done_reason
    }
}
