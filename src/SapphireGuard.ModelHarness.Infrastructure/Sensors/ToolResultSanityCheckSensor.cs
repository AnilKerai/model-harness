using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Infrastructure.Sensors;

/// <summary>
/// Validates tool output at PostToolCall before the model sees it. Prevents
/// the model from confidently reasoning on bad data — empty results, tool
/// errors, suspiciously large payloads, or type violations on tools that
/// should always return a specific shape.
/// </summary>
public sealed class ToolResultSanityCheckSensor(
    int maxResultLength = 10_000,
    IReadOnlyDictionary<string, Func<string, string?>>? toolValidators = null) : ISensor
{
    private readonly IReadOnlyDictionary<string, Func<string, string?>> _toolValidators =
        toolValidators ?? new Dictionary<string, Func<string, string?>>();

    public string Name => "tool-result-sanity";

    public IReadOnlySet<HookPoint> HookPoints { get; } =
        new HashSet<HookPoint> { HookPoint.PostToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ToolCallStep toolStep)
            return Task.FromResult(SensorResult.Pass);

        var result = toolStep.Result;

        if (result.IsError)
            return Task.FromResult(SensorResult.Block(
                $"Tool '{toolStep.Call.ToolName}' returned an error: {result.Content}. " +
                "Do not retry with the same arguments — try a different approach."));

        if (string.IsNullOrWhiteSpace(result.Content))
            return Task.FromResult(SensorResult.Block(
                $"Tool '{toolStep.Call.ToolName}' returned an empty result. " +
                "The tool may be unavailable. Try an alternative approach."));

        if (result.Content.Length > maxResultLength)
            return Task.FromResult(SensorResult.Block(
                $"Tool '{toolStep.Call.ToolName}' returned {result.Content.Length:N0} characters, " +
                $"exceeding the {maxResultLength:N0}-character limit. Summarise from what you already know."));

        if (_toolValidators.TryGetValue(toolStep.Call.ToolName, out var validate))
        {
            var validationError = validate(result.Content);
            if (validationError is not null)
                return Task.FromResult(SensorResult.Block(
                    $"Tool '{toolStep.Call.ToolName}' result failed validation: {validationError}"));
        }

        return Task.FromResult(SensorResult.Pass);
    }
}
