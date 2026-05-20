using System.Diagnostics;
using AgentHarness.Framework.Budget;
using AgentHarness.Framework.Context;
using AgentHarness.Framework.Model;
using AgentHarness.Framework.Sensors;
using AgentHarness.Framework.State;
using AgentHarness.Framework.Tools;
using AgentHarness.Framework.Tracing;

namespace AgentHarness.Framework.Loop;

/// <summary>
/// The core agent loop. Drives turn-by-turn execution: build context, call
/// model, evaluate sensors, dispatch tools, repeat. Implements the layered
/// recovery model — sensor blocks become <see cref="SensorInterventionStep"/>s
/// rather than terminating, and budget exhaustion triggers a single
/// finalisation turn returning <see cref="AgentStatus.PartialResult"/>.
/// </summary>
public sealed class HarnessLoop(
    IModelClient modelClient,
    IToolRegistry toolRegistry,
    IContextBuilder contextBuilder,
    ISensorRunner sensorRunner,
    IBudgetEnforcer budgetEnforcer,
    ITracer tracer)
{
    public async Task<AgentOutcome> RunAsync(AgentState initial, CancellationToken ct)
    {
        var state = initial;
        var startedAt = DateTimeOffset.UtcNow;
        tracer.StartTrace(state.TaskId, state.TaskText);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var budgetCheck = budgetEnforcer.Check(state, startedAt);
                if (budgetCheck.IsExhausted)
                {
                    return await FinaliseOnBudgetAsync(state, budgetCheck.Reason!, ct);
                }

                state = await RunSensorsAsync(state, HookPoint.PreModelCall, triggeringStep: null, ct);

                var (newState, response) = await CallModelAsync(state, forceFinalise: false, ct);
                state = newState;

                state = await RunSensorsAsync(state, HookPoint.PostModelCall, state.Trajectory[^1], ct);

                if (response.ToolCalls.Count == 0)
                {
                    state = await RunSensorsAsync(state, HookPoint.PreReturn, state.Trajectory[^1], ct);

                    var done = state with { Status = AgentStatus.Done };
                    tracer.Complete(done.TaskId, AgentStatus.Done, failureReason: null);
                    return new AgentOutcome
                    {
                        TaskId = done.TaskId,
                        Status = AgentStatus.Done,
                        FinalAnswer = response.Text,
                        FinalState = done
                    };
                }

                foreach (var call in response.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    state = await ExecuteToolAsync(state, call, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            tracer.Complete(state.TaskId, AgentStatus.Failed, "cancelled");
            return new AgentOutcome
            {
                TaskId = state.TaskId,
                Status = AgentStatus.Failed,
                FinalAnswer = null,
                FinalState = state with { Status = AgentStatus.Failed },
                FailureReason = "cancelled"
            };
        }
        catch (BudgetExceededException ex)
        {
            tracer.Complete(state.TaskId, AgentStatus.Failed, ex.Reason);
            return new AgentOutcome
            {
                TaskId = state.TaskId,
                Status = AgentStatus.Failed,
                FinalAnswer = null,
                FinalState = state with { Status = AgentStatus.Failed },
                FailureReason = $"budget violated by collaborator: {ex.Reason}"
            };
        }
        catch (Exception ex)
        {
            tracer.Complete(state.TaskId, AgentStatus.Failed, ex.Message);
            return new AgentOutcome
            {
                TaskId = state.TaskId,
                Status = AgentStatus.Failed,
                FinalAnswer = null,
                FinalState = state with { Status = AgentStatus.Failed },
                FailureReason = ex.Message
            };
        }
    }

    private async Task<AgentState> RunSensorsAsync(AgentState state, HookPoint hookPoint, Step? triggeringStep, CancellationToken ct)
    {
        var results = await sensorRunner.RunAsync(hookPoint, state, triggeringStep, ct);
        var next = state;
        foreach (var (sensor, result) in results)
        {
            tracer.LogSensorResult(state.TaskId, hookPoint, sensor.Name, result);
            if (result.IsBlock)
            {
                next = next.AppendStep(new SensorInterventionStep(
                    Id: Guid.NewGuid(),
                    Timestamp: DateTimeOffset.UtcNow,
                    HookPoint: hookPoint,
                    SensorName: sensor.Name,
                    Reason: result.Reason ?? "(no reason given)",
                    TriggeringStep: triggeringStep));
            }
        }
        return next;
    }

    private async Task<(AgentState State, ModelResponse Response)> CallModelAsync(AgentState state, bool forceFinalise, CancellationToken ct)
    {
        var buildResult = await contextBuilder.BuildAsync(state, toolRegistry.List(), ct);

        var messages = forceFinalise
            ? [.. buildResult.Messages, new Message(MessageRole.System,
                "Budget is exhausted. Produce your best final answer now using only what you already know. Do not request more tools.")]
            : buildResult.Messages;

        var modelTools = forceFinalise
            ? []
            : (IReadOnlyList<ToolDefinition>)buildResult.SelectedTools
                .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
                .ToArray();

        var response = await modelClient.CallAsync(messages, modelTools, ct);
        tracer.LogModelCall(state.TaskId, messages, modelTools, response);

        var step = new ModelCallStep(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Prompt: messages,
            Response: response,
            Usage: response.Usage,
            Cost: response.Cost);

        return (state.AppendStep(step), response);
    }

    private async Task<AgentState> ExecuteToolAsync(AgentState state, ToolCall call, CancellationToken ct)
    {
        var preStep = new ToolCallStep(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Call: call,
            Result: new ToolResult(call.CallId, "(pending)"));

        var afterPre = await RunSensorsAsync(state, HookPoint.PreToolCall, preStep, ct);
        if (afterPre.Trajectory.Count > state.Trajectory.Count)
        {
            var lastIntervention = afterPre.Trajectory.OfType<SensorInterventionStep>().Last();
            var blockedResult = new ToolResult(call.CallId,
                $"Blocked by sensor '{lastIntervention.SensorName}': {lastIntervention.Reason}",
                IsError: true);
            return afterPre.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Call: call,
                Result: blockedResult));
        }

        var sw = Stopwatch.StartNew();
        var ctx = ToolContext.Empty(state.TaskId, call.CallId);
        var result = await toolRegistry.DispatchAsync(call, ctx, ct);
        sw.Stop();
        tracer.LogToolCall(state.TaskId, call, result, sw.Elapsed);

        var executed = new ToolCallStep(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Call: call,
            Result: result);

        var afterExec = afterPre.AppendStep(executed);
        return await RunSensorsAsync(afterExec, HookPoint.PostToolCall, executed, ct);
    }

    private async Task<AgentOutcome> FinaliseOnBudgetAsync(AgentState state, string reason, CancellationToken ct)
    {
        var (finalState, response) = await CallModelAsync(state, forceFinalise: true, ct);
        var partial = finalState with { Status = AgentStatus.PartialResult };
        tracer.Complete(partial.TaskId, AgentStatus.PartialResult, reason);
        return new AgentOutcome
        {
            TaskId = partial.TaskId,
            Status = AgentStatus.PartialResult,
            FinalAnswer = response.Text,
            FinalState = partial,
            FailureReason = reason
        };
    }
}
