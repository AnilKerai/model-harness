using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// At <see cref="HookPoint.PreReturn"/>, scores the agent's proposed final answer against the
/// original task using <paramref name="modelClient"/> and challenges it back for revision when
/// the score falls below <paramref name="passThreshold"/>. The loop renders the challenge as a
/// harness observation and gives the model a fresh turn with its prior answer still visible; the
/// loop's own consecutive-intervention cap bounds how many revision rounds run before it
/// force-finalises, so no separate refinement counter is needed.
/// Fails open — any model or parse failure passes the answer through, so a flaky critic never
/// blocks a return. Pass a fast, cheap model (Haiku-class) to keep the per-answer overhead low.
/// </summary>
public sealed class CriticSensor(IModelClient modelClient, double passThreshold = 0.6) : ISensor
{
    public string Name => "critic";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreReturn };

    private static readonly Message SystemMessage = new(MessageRole.System,
        """
        You are a strict quality reviewer for an AI agent's final answer. Judge only whether the answer
        actually and completely satisfies the task — ignore tone and style. Respond with a single JSON
        object and nothing else: {"score": <number 0.0-1.0>, "deficiencies": ["<gap>", ...]}. score is
        your predicted probability that the answer fully satisfies the task. deficiencies lists concrete,
        actionable gaps the agent must fix to satisfy the task; use [] when the answer is satisfactory.
        """);

    public async Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ModelCallStep { Response.Text: { Length: > 0 } answer })
            return SensorResult.Pass;

        ModelResponse response;
        try
        {
            response = await modelClient.CallAsync(
                [
                    SystemMessage,
                    new Message(MessageRole.User, $"Task:\n{state.TaskText}\n\nProposed answer:\n{answer}")
                ],
                [],
                ct);
        }
        catch
        {
            return SensorResult.Pass; // fail open — a flaky critic must never block a return
        }

        if (!TryParseVerdict(response.Text, out var score, out var deficiencies) || score >= passThreshold)
            return SensorResult.PassWithUsage(response.Usage, response.Cost);

        var gaps = deficiencies.Count > 0
            ? string.Join("; ", deficiencies)
            : "the answer does not fully satisfy the task";
        return SensorResult.InterveneWithUsage(
            $"Self-review scored this answer {score:0.00}, below the {passThreshold:0.00} quality bar. " +
            $"Revise before finalising — address: {gaps}.",
            response.Usage,
            response.Cost);
    }

    // ponytail: tolerant extraction — models often wrap JSON in prose or code fences, so take the
    // outermost braces. Anything unparseable returns false and the answer is passed through.
    private static bool TryParseVerdict(string? text, out double score, out IReadOnlyList<string> deficiencies)
    {
        score = 0;
        deficiencies = [];
        if (string.IsNullOrWhiteSpace(text)) return false;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            if (!root.TryGetProperty("score", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetDouble(out score))
                return false;
            if (root.TryGetProperty("deficiencies", out var d) && d.ValueKind == JsonValueKind.Array)
                deficiencies = d.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
