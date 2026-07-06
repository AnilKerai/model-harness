using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OllamaSharp;
using OllamaSharp.Models;
using OllamaSharp.Models.Chat;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

using FrameworkMessage = SapphireGuard.ModelHarness.Framework.State.Message;
using FrameworkUsage = SapphireGuard.ModelHarness.Framework.State.Usage;
using FrameworkStopReason = SapphireGuard.ModelHarness.Framework.State.StopReason;
using FrameworkToolCall = SapphireGuard.ModelHarness.Framework.Tools.ToolCall;
using OllamaMessage = OllamaSharp.Models.Chat.Message;
using OllamaToolCall = OllamaSharp.Models.Chat.Message.ToolCall;
using OllamaFunction = OllamaSharp.Models.Chat.Message.Function;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

/// <summary>
/// <see cref="IModelClient"/> adapter over OllamaSharp.
///
/// Mapping notes:
///   • Ollama tool calls have no call ID in the protocol; OllamaSharp v5 exposes an Id
///     property that may be null. We use it when present, otherwise synthesise a GUID.
///   • HeadEvictionTrajectoryGuide emits interleaved ToolUse/Tool pairs
///     (ToolUse1, Tool1, ToolUse2, Tool2) for a single model turn, but Ollama expects
///     all calls from one turn in a single assistant message followed by separate tool
///     result messages. <see cref="BuildMessages"/> groups them with a stateful pass.
///   • Tool arguments come back from Ollama as a parsed IDictionary&lt;string,object&gt;,
///     not a JSON string — we re-serialise to JsonElement so the framework stays
///     unaware of the difference.
///   • Ollama runs locally; cost is always zero.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class OllamaModelClient : IModelClient
{
    private readonly OllamaApiClient _client;
    private readonly string _model;
    private readonly int? _maxOutputTokens;

    public OllamaModelClient(OllamaClientOptions options)
    {
        var http = new HttpClient { BaseAddress = new Uri(options.BaseUrl) };
        options.ConfigureHttpClient?.Invoke(http);
        _client = new OllamaApiClient(http);
        _model = options.ModelId;
        _maxOutputTokens = options.MaxOutputTokens;
    }

    public async Task<ModelResponse> CallAsync(
        IReadOnlyList<FrameworkMessage> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var ollamaMessages = BuildMessages(messages);
        var ollamaTools = availableTools.Select(MapTool).ToList();

        var request = new ChatRequest
        {
            Model = _model,
            Messages = ollamaMessages,
            Tools = ollamaTools.Count > 0 ? ollamaTools : null,
            Stream = false,
            Options = _maxOutputTokens is { } max ? new RequestOptions { NumPredict = max } : null
        };

        ChatResponseStream? last = null;
        await foreach (var chunk in _client.ChatAsync(request, ct).ConfigureAwait(false))
            last = chunk;

        return last is null
            ? EmptyResponse()
            : MapResponse(last, _model);
    }

    // ── Message building ─────────────────────────────────────────────────────

    private static List<OllamaMessage> BuildMessages(IReadOnlyList<FrameworkMessage> messages)
    {
        var result = new List<OllamaMessage>();
        string? systemText = null;

        // Pending state for one model turn: an optional text + N tool call/result pairs.
        // We accumulate them until we see a User or a new Assistant message, then flush
        // as one assistant message (with all tool_calls) followed by separate tool messages.
        string? pendingText = null;
        var pendingCalls = new List<OllamaToolCall>();
        var pendingCallNames = new List<string>(); // parallel to pendingCalls
        var pendingResults = new List<(string ToolName, string Content)>();

        void Flush()
        {
            if (pendingText is null && pendingCalls.Count == 0)
                return;

            result.Add(new OllamaMessage
            {
                Role = ChatRole.Assistant,
                Content = pendingText,
                ToolCalls = pendingCalls.Count > 0 ? [.. pendingCalls] : null
            });

            foreach (var (toolName, content) in pendingResults)
                result.Add(new OllamaMessage { Role = ChatRole.Tool, Content = content, ToolName = toolName });

            pendingText = null;
            pendingCalls.Clear();
            pendingCallNames.Clear();
            pendingResults.Clear();
        }

        foreach (var msg in messages)
        {
            switch (msg.Role)
            {
                case MessageRole.System:
                    // Only the first system message is used as the Ollama system prompt.
                    // Subsequent ones are sensor intervention notes — same treatment as
                    // the Anthropic adapter (they are dropped here; the suppression of
                    // flagged model responses by HeadEvictionTrajectoryGuide is the primary mechanism).
                    systemText ??= msg.Content;
                    break;

                case MessageRole.User:
                    Flush();
                    result.Add(new OllamaMessage(ChatRole.User, msg.Content));
                    break;

                case MessageRole.Assistant:
                    Flush();
                    pendingText = msg.Content;
                    break;

                case MessageRole.ToolUse:
                    var (callName, callId, argsJson) = OllamaWireFormat.ParseToolUse(msg.Content);
                    var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson)
                               ?? new Dictionary<string, object?>();
                    pendingCalls.Add(new OllamaToolCall
                    {
                        Id = callId,
                        Function = new OllamaFunction { Name = callName, Arguments = args! }
                    });
                    pendingCallNames.Add(callName);
                    break;

                case MessageRole.Tool:
                    var (_, _, resultText) = OllamaWireFormat.ParseToolResult(msg.Content);
                    // Match this result to the n-th pending call by position.
                    var matchedName = pendingResults.Count < pendingCallNames.Count
                        ? pendingCallNames[pendingResults.Count]
                        : string.Empty;
                    pendingResults.Add((matchedName, resultText));
                    break;
            }
        }

        Flush();

        if (systemText is not null)
            result.Insert(0, new OllamaMessage(ChatRole.System, systemText));

        return result;
    }

    // ── Tool definition mapping ───────────────────────────────────────────────

    private static Tool MapTool(ToolDefinition def) =>
        new()
        {
            Type = "function",
            Function = new Function
            {
                Name = def.Name,
                Description = def.Description,
                Parameters = MapParameters(def.InputSchema)
            }
        };

    private static Parameters MapParameters(JsonElement schema)
    {
        var parameters = new Parameters
        {
            Type = "object",
            Properties = new Dictionary<string, Property>()
        };

        if (schema.TryGetProperty("required", out var req))
            parameters.Required = req.EnumerateArray().Select(x => x.GetString()!).ToArray();

        if (!schema.TryGetProperty("properties", out var props))
            return parameters;

        foreach (var prop in props.EnumerateObject())
        {
            var property = new Property();
            if (prop.Value.TryGetProperty("type", out var typeEl))
                property.Type = typeEl.GetString();
            if (prop.Value.TryGetProperty("description", out var descEl))
                property.Description = descEl.GetString();
            if (prop.Value.TryGetProperty("enum", out var enumEl))
                property.Enum = enumEl.EnumerateArray().Select(x => x.GetString()!).ToArray();
            parameters.Properties[prop.Name] = property;
        }

        return parameters;
    }

    // ── Response mapping ─────────────────────────────────────────────────────

    private static ModelResponse MapResponse(ChatResponseStream response, string model)
    {
        var msg = response.Message;
        var toolCalls = new List<FrameworkToolCall>();

        if (msg?.ToolCalls is not null)
        {
            foreach (var tc in msg.ToolCalls)
            {
                if (tc.Function is null) continue;
                // Use the Ollama-provided call ID if present; synthesise one otherwise.
                var callId = string.IsNullOrEmpty(tc.Id) ? Guid.NewGuid().ToString("n") : tc.Id;
                var argsJson = JsonSerializer.Serialize(tc.Function.Arguments);
                var argsElement = JsonDocument.Parse(argsJson).RootElement.Clone();
                toolCalls.Add(new FrameworkToolCall(callId, tc.Function.Name ?? string.Empty, argsElement));
            }
        }

        int inputTokens = 0, outputTokens = 0;
        if (response is ChatDoneResponseStream done)
        {
            inputTokens = done.PromptEvalCount;
            outputTokens = done.EvalCount;
        }

        var stopReason = toolCalls.Count > 0
            ? FrameworkStopReason.ToolUse
            : FrameworkStopReason.EndTurn;

        return new ModelResponse
        {
            Text = string.IsNullOrEmpty(msg?.Content) ? null : msg.Content,
            ToolCalls = toolCalls,
            StopReason = stopReason,
            Usage = new FrameworkUsage(inputTokens, outputTokens),
            Cost = 0m, // Ollama is local
            Model = model,
            Provider = "ollama"
        };
    }

    private static ModelResponse EmptyResponse() => new()
    {
        Text = null,
        ToolCalls = [],
        StopReason = FrameworkStopReason.Other,
        Usage = FrameworkUsage.Zero,
        Cost = 0m
    };

}
