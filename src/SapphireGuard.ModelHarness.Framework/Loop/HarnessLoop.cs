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
    ICheckpointStore checkpointStore,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<AgentOutcome> RunAsync(AgentState initial, CancellationToken ct)
    {
        var state = initial;
        var startedAt = _time.GetUtcNow();
        var runId = Guid.NewGuid().ToString("n");
        var turn = 0;
        var consecutiveInterventions = 0;
        var suppressTools = false;
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
                    CreatedAt = _time.GetUtcNow(),
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
                    if (_time.GetUtcNow() + delay > startedAt + state.Budget.MaxWallClock)
                        return await FinaliseOnBudgetAsync(state,
                            $"Rate limit wait ({delay.TotalSeconds:0}s) would exceed MaxWallClock.", ct);
                    await Task.Delay(delay, ct);
                    continue;
                }

                // PreModelCall sensors annotate the trajectory; the model call proceeds
                // regardless so the model can act on the note immediately.
                (state, _, _) = await RunSensorsAsync(state, HookPoint.PreModelCall, triggeringStep: null, ct);

                var prevSuppressTools = suppressTools;
                var (newState, response) = await CallModelAsync(state, forceFinalise: false, ct, suppressTools);
                state = newState;
                suppressTools = false;

                var (postModelState, rejected, suppressToolsOnRetry) = await RunSensorsAsync(state, HookPoint.PostModelCall, state.Trajectory[^1], ct);
                state = postModelState;
                if (rejected)
                {
                    // Preserve suppression from the current turn if the PostModelCall sensor
                    // didn't explicitly request it — a format rejection doesn't restore tools
                    // that a PreReturn challenge already removed.
                    suppressTools = suppressToolsOnRetry || prevSuppressTools;
                    if (++consecutiveInterventions >= maxConsecutiveInterventions)
                        return await FinaliseOnBudgetAsync(state,
                            $"Sensor intervention limit ({maxConsecutiveInterventions}) reached — the model repeatedly produced a response that was rejected.", ct);
                    continue;
                }

                if (response.ToolCalls.Count == 0)
                {
                    var (preReturnState, challenged, suppressToolsOnChallenge) = await RunSensorsAsync(state, HookPoint.PreReturn, state.Trajectory[^1], ct);
                    state = preReturnState;
                    if (challenged)
                    {
                        suppressTools = suppressToolsOnChallenge;
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

                    var suspended = await TrySuspendForHumanAsync(state, runId, turn, ct);
                    if (suspended is not null)
                        return suspended;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return FailOutcome(state, failureReason: "cancelled", traceReason: "cancelled");
        }
        catch (BudgetExceededException ex)
        {
            return FailOutcome(state, $"budget violated by collaborator: {ex.Reason}", ex.Reason);
        }
        catch (Exception ex)
        {
            return FailOutcome(state, ex.Message, ex.Message);
        }
    }

    private AgentOutcome FailOutcome(AgentState state, string failureReason, string traceReason)
    {
        tracer.Complete(state.TaskId, AgentStatus.Failed, traceReason);
        return new AgentOutcome
        {
            TaskId = state.TaskId,
            Status = AgentStatus.Failed,
            FinalAnswer = null,
            FinalState = state with { Status = AgentStatus.Failed },
            FailureReason = failureReason
        };
    }

    // Suspends the run for human input when the last tool returned a pending result; returns the
    // AwaitingHuman outcome to bubble out of the loop, or null to keep going.
    private async Task<AgentOutcome?> TrySuspendForHumanAsync(AgentState state, string runId, int turn, CancellationToken ct)
    {
        var lastTool = state.Trajectory.OfType<ToolCallStep>().LastOrDefault();
        if (lastTool?.Result.IsPending != true)
            return null;

        var pending = new PendingHumanInput(lastTool.Result.CallId, lastTool.Result.Content);
        var suspended = state with
        {
            Status = AgentStatus.AwaitingHuman,
            PendingHumanInput = pending
        };
        await checkpointStore.SaveAsync(new Checkpoint
        {
            CheckpointId = Guid.NewGuid().ToString("n"),
            RunId = runId,
            CreatedAt = _time.GetUtcNow(),
            TurnNumber = turn,
            State = suspended
        }, ct);
        tracer.Complete(suspended.TaskId, AgentStatus.AwaitingHuman, failureReason: null);
        return new AgentOutcome
        {
            TaskId = suspended.TaskId,
            Status = AgentStatus.AwaitingHuman,
            FinalAnswer = null,
            FinalState = suspended,
            PendingHumanInput = pending
        };
    }

    private async Task<(AgentState State, bool Intervened, bool SuppressTools)> RunSensorsAsync(AgentState state, HookPoint hookPoint, Step? triggeringStep, CancellationToken ct)
    {
        var results = await sensorRunner.RunAsync(hookPoint, state, triggeringStep, ct);
        var next = state;
        var intervened = false;
        var suppressTools = false;
        var accruedInputTokens = 0;
        var accruedOutputTokens = 0;
        var accruedCost = 0m;
        foreach (var (sensor, result) in results)
        {
            tracer.LogSensorResult(state.TaskId, hookPoint, sensor.Name, result);
            if (result.Usage is { } usage)
            {
                accruedInputTokens += usage.InputTokens;
                accruedOutputTokens += usage.OutputTokens;
            }
            if (result.Cost is { } cost)
                accruedCost += cost;
            if (result.IsIntervene)
            {
                intervened = true;
                suppressTools |= result.SuppressTools;
                next = next.AppendStep(new SensorInterventionStep(
                    Id: Guid.NewGuid(),
                    Timestamp: _time.GetUtcNow(),
                    HookPoint: hookPoint,
                    SensorName: sensor.Name,
                    Reason: result.Reason ?? "(no reason given)",
                    TriggeringStep: triggeringStep));
            }
        }
        if (accruedInputTokens > 0 || accruedOutputTokens > 0 || accruedCost > 0m)
        {
            next = next with
            {
                SensorUsage = new Usage(
                    next.SensorUsage.InputTokens + accruedInputTokens,
                    next.SensorUsage.OutputTokens + accruedOutputTokens),
                SensorCost = next.SensorCost + accruedCost
            };
        }
        return (next, intervened, suppressTools);
    }

    private async Task<(AgentState State, ModelResponse Response)> CallModelAsync(AgentState state, bool forceFinalise, CancellationToken ct, bool suppressTools = false)
    {
        var buildResult = await contextBuilder.BuildAsync(state, toolRegistry.List(), ct);

        var messages = forceFinalise
            ? [.. buildResult.Messages, new Message(MessageRole.System,
                "Budget is exhausted. Produce your best final answer now using only what you already know. Do not request more tools.")]
            : buildResult.Messages;

        var modelTools = forceFinalise || suppressTools
            ? []
            : (IReadOnlyList<ToolDefinition>)buildResult.SelectedTools
                .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
                .ToArray();

        var response = await modelClient.CallAsync(messages, modelTools, ct);
        tracer.LogModelCall(state.TaskId, messages, modelTools, response);

        var step = new ModelCallStep(
            Id: Guid.NewGuid(),
            Timestamp: _time.GetUtcNow(),
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
            Timestamp: _time.GetUtcNow(),
            Call: call,
            Result: new ToolResult(call.CallId, "(pending)"));

        var (afterPre, toolBlocked, _) = await RunSensorsAsync(state, HookPoint.PreToolCall, preStep, ct);
        if (toolBlocked)
        {
            var lastIntervention = afterPre.Trajectory.OfType<SensorInterventionStep>().Last();
            return afterPre.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: _time.GetUtcNow(),
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
            Timestamp: _time.GetUtcNow(),
            Call: call,
            Result: result);

        var afterExec = afterPre.AppendStep(executed);
        var (afterPost, _, _) = await RunSensorsAsync(afterExec, HookPoint.PostToolCall, executed, ct);
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
