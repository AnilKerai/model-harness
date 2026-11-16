using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

// Pins the Messages API request body ClaudeModelClient.CallAsync sends. ClaudeWireFormatTests cover BuildMessages,
// MapTool and the response mapping one at a time, but CallAsync is what assembles the system text, the tools,
// max_tokens and the top-level cache_control, and nothing else reads that whole body. The body is captured at the
// HTTP layer, so a change of Anthropic SDK version that alters what goes on the wire fails here.
//
// The golden files under Golden/ were captured on Anthropic 12.60.0 and reviewed by hand against the Messages API.
// A golden that fails after an SDK bump means the request body changed. The goldens' "model" value is
// ClaudeClientOptions.ModelId's default, which moved to claude-sonnet-5-5 in 5.0.0; no other byte of the goldens
// changed.
public sealed class ClaudeRequestGoldenTests
{
    private const string CannedResponse = """
        {
          "id": "msg_01",
          "type": "message",
          "role": "assistant",
          "model": "claude-sonnet-5-5",
          "content": [ { "type": "text", "text": "ok" } ],
          "stop_reason": "end_turn",
          "stop_sequence": null,
          "usage": { "input_tokens": 1, "output_tokens": 1 }
        }
        """;

    private static readonly IReadOnlyList<Message> Conversation =
    [
        new(MessageRole.System, "You are a careful assistant."),
        // Two consecutive user messages share one user turn.
        new(MessageRole.User, "Check order 42."),
        new(MessageRole.User, "It is urgent."),
        // Assistant text and its tool call share one assistant turn.
        new(MessageRole.Assistant, "Looking it up."),
        new(MessageRole.ToolUse, "[tool_call name=lookup id=call_1] {\"orderId\":42}"),
        // The tool result becomes a block of the next user turn, which the next user message joins.
        new(MessageRole.Tool, "[tool_result id=call_1 error=true] order service unavailable"),
        // Only the first system message is forwarded; a later one is dropped.
        new(MessageRole.System, "A later system message that must not be sent."),
        new(MessageRole.User, "Try once more."),
        // A turn with a single text block goes as the plain string form.
        new(MessageRole.Assistant, "It is still failing."),
        new(MessageRole.User, "Thanks, stop there.")
    ];

    private static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        new("lookup", "Looks up an order by its number.", JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": { "orderId": { "type": "integer", "description": "The order number." } },
              "required": [ "orderId" ]
            }
            """).RootElement.Clone())
    ];

    [Fact]
    public async Task CallAsync_WithPromptCaching_SendsTheGoldenRequest()
    {
        var body = await SendAsync(new ClaudeClientOptions { ApiKey = "test-key" }, Conversation, Tools);

        AssertGolden("request-with-prompt-caching", body);
    }

    [Fact]
    public async Task CallAsync_WithoutPromptCaching_SendsTheGoldenRequest()
    {
        var body = await SendAsync(
            new ClaudeClientOptions { ApiKey = "test-key", EnablePromptCaching = false, MaxOutputTokens = 2048 },
            Conversation,
            Tools);

        AssertGolden("request-without-prompt-caching", body);
    }

    [Fact]
    public async Task CallAsync_WithNoSystemMessageAndNoTools_OmitsSystemAndTools()
    {
        var body = await SendAsync(
            new ClaudeClientOptions { ApiKey = "test-key" },
            [new Message(MessageRole.User, "hello")],
            []);

        AssertGolden("request-without-system-or-tools", body);
    }

    private static async Task<string> SendAsync(
        ClaudeClientOptions options,
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var client = new ClaudeModelClient(options with
        {
            ConfigureClient = o => o with { HttpClient = http, MaxRetries = 0 }
        });

        var response = await client.CallAsync(messages, tools, CancellationToken.None);

        Assert.Equal("ok", response.Text);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/v1/messages", handler.Path);
        return handler.Body ?? throw new InvalidOperationException("The request had no body.");
    }

    private static void AssertGolden(string name, string actualBody)
    {
        var expected = Canonical(JsonNode.Parse(ReadGolden(name)));
        var actual = Canonical(JsonNode.Parse(actualBody));

        // Compared as parsed JSON with the members of every object in a fixed order, so only a difference in
        // content fails; the assertion's diff then shows exactly which member changed.
        Assert.Equal(expected, actual);
    }

    private static string Canonical(JsonNode? node) => Sorted(node)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null";

    private static JsonNode? Sorted(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sorted(p.Value)))),
        JsonArray arr => new JsonArray(arr.Select(Sorted).ToArray()),
        _ => node?.DeepClone()
    };

    private static string ReadGolden(string name)
    {
        var resource = $"{typeof(ClaudeRequestGoldenTests).Namespace}.Golden.{name}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Golden resource '{resource}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(CannedResponse, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
