using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Samples.StructuredOutput;

/// <summary>
/// Scripted stand-in so the sample tells the whole story without an API key. The shared
/// <c>FakeModelClient</c> is hardwired to a calculator scenario, so this sample scripts its own:
/// <list type="number">
///   <item>Turn 1: call a tool — the output contract constrains the final answer, not the ReAct turns.</item>
///   <item>Turn 2: answer with an incomplete object — the sensor challenges it and hands the binder's
///   own error back, naming the members that are missing.</item>
///   <item>Turn 3: answer correctly, but fenced and wrapped in prose — bound anyway, because that is
///   what weaker models actually emit.</item>
/// </list>
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class ScriptedTriageModel : IModelClient
{
    private int _turn;

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        var turn = Interlocked.Increment(ref _turn);

        return Task.FromResult(turn switch
        {
            1 => LooksUpTheCustomer(),
            2 => AnswersWithMissingFields(),
            _ => AnswersCorrectlyButUntidily()
        });
    }

    private static ModelResponse LooksUpTheCustomer()
    {
        var args = JsonDocument.Parse("""{"email":"ada@contoso.com"}""").RootElement;

        return Response(
            text: "Let me look up this customer first.",
            toolCalls: [new ToolCall(Guid.NewGuid().ToString("n"), "lookup_customer", args)],
            stopReason: StopReason.ToolUse);
    }

    private static ModelResponse AnswersWithMissingFields() =>
        Response("""{"category":"billing"}""", [], StopReason.EndTurn);

    private static ModelResponse AnswersCorrectlyButUntidily() =>
        Response(
            """
            Sure — here is the triage:

            ```json
            {
              "category": "billing",
              "priority": 4,
              "summary": "Enterprise customer double-charged on their February invoice; needs a refund."
            }
            ```

            Let me know if you need anything else!
            """,
            [],
            StopReason.EndTurn);

    private static ModelResponse Response(string text, IReadOnlyList<ToolCall> toolCalls, StopReason stopReason) => new()
    {
        Text = text,
        ToolCalls = toolCalls,
        StopReason = stopReason,
        Usage = new Usage(InputTokens: 140, OutputTokens: 30),
        Cost = 0.0011m,
        Model = "scripted-model",
        Provider = "scripted"
    };
}
