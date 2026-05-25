using System.Diagnostics;
using SapphireGuard.ModelHarness.Framework.Budget;
using SapphireGuard.ModelHarness.Framework.Context;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Persistence;
using SapphireGuard.ModelHarness.Framework.RateLimiting;
using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Framework.Tracing;

namespace SapphireGuard.ModelHarness.Framework.Loop;

/// <summary>
/// The core agent loop. Drives turn-by-turn execution: build context, call
/// model, evaluate sensors, dispatch tools, repeat. Implements the layered
/// recovery model — sensor interventions become <see cref="SensorInterventionStep"/>s
/// rather than terminating, and budget exhaustion triggers a single
/// finalisation turn returning <see cref="AgentStatus.PartialResult"/>.
/// </summary>
public sealed class HarnessLoop(
    IModelClient modelClient,
    IToolRegistry toolRegistry,
    IContextBuilder contextBuilder,
    ISensorRunner sensorRunner,
    IBudgetEnforcer budgetEnforcer,
    IRateLimiter rateLimiter,
    ITracer tracer,
    ICheckpointStore checkpointStore)
{
    public async Task<AgentOutcome> RunAsync(AgentState initial, CancellationToken ct)
    {
        var state = initial;
        var startedAt = DateTimeOffset.UtcNow;
        var runId = Guid.NewGuid().ToString("n");
        var turn = 0;
        var consecutiveInterventions = 0;
        const int maxConsecutiveInterventions = 3;
        tracer.StartTrace(state.TaskId, state.TaskText);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                await checkpointStore.SaveAsync(new Checkpoint
                {
                    CheckpointId = Guid.NewGuid().ToString("n"),
                    RunId = runId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    TurnNumber = turn++,
                    State = state
                }, ct);

                var budgetCheck = budgetEnforcer.Check(state, startedAt);
                if (budgetCheck.IsExhausted)
                    return await FinaliseOnBudgetAsync(state, budgetCheck.Reason!, ct);

                var rateLimitCheck = await rateLimiter.CheckAsync(state, ct);
                if (rateLimitCheck.IsLimited)
                {
                    var delay = rateLimitCheck.RetryAfter ?? TimeSpan.FromSeconds(10);
                    if (DateTimeOffset.UtcNow + delay > startedAt + state.Budget.MaxWallClock)
                        return await FinaliseOnBudgetAsync(state,
                            $"Rate limit wait ({delay.TotalSeconds:0}s) would exceed MaxWallClock.", ct);
                    await Task.Delay(delay, ct);
                    continue;
                }

                // PreModelCall sensors annotate the trajectory; the model call proceeds
                // regardless so the model can act on the note immediately.
                (state, _) = await RunSensorsAsync(state, HookPoint.PreModelCall, triggeringStep: null, ct);

                var (newState, response) = await CallModelAsync(state, forceFinalise: false, ct);
                state = newState;

                var (postModelState, rejected) = await RunSensorsAsync(state, HookPoint.PostModelCall, state.Trajectory[^1], ct);
                state = postModelState;
                if (rejected)
                {
                    if (++consecutiveInterventions >= maxConsecutiveInterventions)
                        return await FinaliseOnBudgetAsync(state,
                            $"Sensor intervention limit ({maxConsecutiveInterventions}) reached — the model repeatedly produced a response that was rejected.", ct);
                    continue;
                }

                if (response.ToolCalls.Count == 0)
                {
                    var (preReturnState, challenged) = await RunSensorsAsync(state, HookPoint.PreReturn, state.Trajectory[^1], ct);
                    state = preReturnState;
                    if (challenged)
                    {
                        if (++consecutiveInterventions >= maxConsecutiveInterventions)
                            return await FinaliseOnBudgetAsync(state,
                                $"Sensor intervention limit ({maxConsecutiveInterventions}) reached — the model repeatedly returned an answer that was challenged.", ct);
                        continue;
                    }

                    consecutiveInterventions = 0;
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

                consecutiveInterventions = 0;
                foreach (var call in response.ToolCalls)
                {
                    ct.ThrowIfCancellationRequested();
                    state = await ExecuteToolAsync(state, call, ct);

                    var lastTool = state.Trajectory.OfType<ToolCallStep>().LastOrDefault();
                    if (lastTool?.Result.IsPending == true)
                    {
                        await checkpointStore.SaveAsync(new Checkpoint
                        {
                            CheckpointId = Guid.NewGuid().ToString("n"),
                            RunId = runId,
                            CreatedAt = DateTimeOffset.UtcNow,
                            TurnNumber = turn,
                            State = state
                        }, ct);
                        var suspended = state with { Status = AgentStatus.AwaitingHuman };
                        tracer.Complete(suspended.TaskId, AgentStatus.AwaitingHuman, failureReason: null);
                        return new AgentOutcome
                        {
                            TaskId = suspended.TaskId,
                            Status = AgentStatus.AwaitingHuman,
                            FinalAnswer = null,
                            FinalState = suspended,
                            PendingHumanInput = new PendingHumanInput(lastTool.Result.CallId, lastTool.Result.Content)
                        };
                    }
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

    private async Task<(AgentState State, bool Intervened)> RunSensorsAsync(AgentState state, HookPoint hookPoint, Step? triggeringStep, CancellationToken ct)
    {
        var results = await sensorRunner.RunAsync(hookPoint, state, triggeringStep, ct);
        var next = state;
        var intervened = false;
        foreach (var (sensor, result) in results)
        {
            tracer.LogSensorResult(state.TaskId, hookPoint, sensor.Name, result);
            if (result.IsIntervene)
            {
                intervened = true;
                next = next.AppendStep(new SensorInterventionStep(
                    Id: Guid.NewGuid(),
                    Timestamp: DateTimeOffset.UtcNow,
                    HookPoint: hookPoint,
                    SensorName: sensor.Name,
                    Reason: result.Reason ?? "(no reason given)",
                    TriggeringStep: triggeringStep));
            }
        }
        return (next, intervened);
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

        var (afterPre, toolBlocked) = await RunSensorsAsync(state, HookPoint.PreToolCall, preStep, ct);
        if (toolBlocked)
        {
            var lastIntervention = afterPre.Trajectory.OfType<SensorInterventionStep>().Last();
            return afterPre.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Call: call,
                Result: new ToolResult(call.CallId,
                    $"Blocked by sensor '{lastIntervention.SensorName}': {lastIntervention.Reason}",
                    IsError: true)));
        }

        var sw = Stopwatch.StartNew();
        var ctx = ToolContext.Empty(state.TaskId, call.CallId);
        ToolResult result;
        try
        {
            result = await toolRegistry.DispatchAsync(call, ctx, ct);
        }
        catch (Exception ex)
        {
            result = new ToolResult(call.CallId, $"Tool threw {ex.GetType().Name}: {ex.Message}", IsError: true);
        }
        sw.Stop();
        tracer.LogToolCall(state.TaskId, call, result, sw.Elapsed);

        var executed = new ToolCallStep(
            Id: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            Call: call,
            Result: result);

        var afterExec = afterPre.AppendStep(executed);
        var (afterPost, _) = await RunSensorsAsync(afterExec, HookPoint.PostToolCall, executed, ct);
        return afterPost;
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
