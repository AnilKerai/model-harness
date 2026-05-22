using System.Text.Json;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Samples.ToolCallReasonableness;

public sealed class ToolCallReasonablenessSensor : ISensor
{
    public string Name => "tool-call-reasonableness";

    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { HookPoint.PreToolCall };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (triggeringStep is not ToolCallStep toolStep)
            return Task.FromResult(SensorResult.Pass);

        var call = toolStep.Call;
        var args = call.Arguments;

        if (!args.EnumerateObject().Any())
            return Task.FromResult(SensorResult.Intervene(
                $"Tool '{call.ToolName}' was called with no arguments. Provide the required parameters."));

        if (call.ToolName == "calculator")
        {
            var reason = CheckCalculatorArgs(args);
            if (reason is not null)
                return Task.FromResult(SensorResult.Intervene(reason));
        }

        return Task.FromResult(SensorResult.Pass);
    }

    private static string? CheckCalculatorArgs(JsonElement args)
    {
        if (!args.TryGetProperty("lhs", out var lhsEl) || !args.TryGetProperty("rhs", out var rhsEl))
            return null;

        var lhs = lhsEl.GetDouble();
        var rhs = rhsEl.GetDouble();

        if (!double.IsFinite(lhs) || !double.IsFinite(rhs))
            return $"Calculator operands must be finite numbers (got lhs={lhs}, rhs={rhs}). Revise your inputs.";

        if (args.TryGetProperty("op", out var opEl) && opEl.GetString() == "div" && rhs == 0)
            return "Division by zero is not allowed. Use a non-zero divisor.";

        return null;
    }
}
