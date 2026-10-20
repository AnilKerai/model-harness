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
        // Derived from the trajectory, not minted per invocation: a resumed run (HITL, checkpoint
        // reload, new chat turn) re-enters RunAsync, and a fresh GUID would stamp the same task's
        // checkpoints with different RunIds — breaking Checkpoint.RunId's documented contract.
        var runId = state.RunId ?? Guid.NewGuid().ToString("n");
        var consecutiveInterventions = 0;
        var suppressTools = false;
        const int maxConsecutiveInterventions = 3;
        tracer.StartTrace(state.TaskId, state.TaskText);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Captured once per iteration so every event this turn emits shares an index a trace
                // backend can group on. Derived from the trajectory rather than an incrementing
                // counter: an iteration that ends in the rate-limit `continue` below appends no
                // ModelCallStep, so a counter would burn an index no model call ever used and drift
                // +1 from DefaultGuideRunner, which derives the same index the same way. Deriving also
                // subsumes resume seeding for free — a restored trajectory carries its own count, so a
                // HITL suspend/resume or new chat turn continues the numbering instead of restarting.
                var turnIndex = state.Trajectory.OfType<ModelCallStep>().Count();

                var checkpointId = Guid.NewGuid().ToString("n");
                var checkpointStart = _time.GetTimestamp();
                await checkpointStore.SaveAsync(new Checkpoint
                {
                    CheckpointId = checkpointId,
                    RunId = runId,
                    CreatedAt = _time.GetUtcNow(),
                    TurnNumber = turnIndex,
                    State = state
                }, ct);
                tracer.LogCheckpoint(state.TaskId, turnIndex, checkpointId, _time.GetElapsedTime(checkpointStart));

                var budgetCheck = budgetEnforcer.Check(state);
                var runElapsed = _time.GetUtcNow() - (state.RunStartedAt ?? _time.GetUtcNow());
                tracer.LogBudgetSnapshot(state.TaskId, turnIndex, BudgetSnapshot.From(state, runElapsed));
                if (budgetCheck.IsExhausted)
                    return await FinaliseOnBudgetAsync(state, turnIndex, budgetCheck.Reason!, ct);

                var rateLimitCheck = await rateLimiter.CheckAsync(state, ct);
                if (rateLimitCheck.IsLimited)
                {
                    var delay = rateLimitCheck.RetryAfter ?? TimeSpan.FromSeconds(10);
                    // Would sleeping for the backoff blow the wall-clock budget? Ask the enforcer with a
                    // lookahead so the check honours whichever anchor (task-total vs per-turn) is wired.
                    var afterWait = budgetEnforcer.Check(state, delay);
                    if (afterWait.IsExhausted)
                        return await FinaliseOnBudgetAsync(state, turnIndex,
                            $"Rate limit wait ({delay.TotalSeconds:0}s) would exceed budget: {afterWait.Reason}", ct);
                    tracer.LogRateLimit(state.TaskId, turnIndex, delay);
                    await Task.Delay(delay, ct);
                    continue;
                }

                // PreModelCall sensors annotate the trajectory; the model call proceeds
                // regardless so the model can act on the note immediately.
                (state, _, _) = await RunSensorsAsync(state, turnIndex, HookPoint.PreModelCall, triggeringStep: null, ct);

                var prevSuppressTools = suppressTools;
                var (newState, response, offeredTools) = await CallModelAsync(state, turnIndex, forceFinalise: false, ct, suppressTools);
                state = newState;
                suppressTools = false;

                var (postModelState, rejected, suppressToolsOnRetry) = await RunSensorsAsync(state, turnIndex, HookPoint.PostModelCall, state.Trajectory[^1], ct);
                state = postModelState;
                if (rejected)
                {
                    // Preserve suppression from the current turn if the PostModelCall sensor
                    // didn't explicitly request it — a format rejection doesn't restore tools
                    // that a PreReturn challenge already removed.
                    suppressTools = suppressToolsOnRetry || prevSuppressTools;
                    if (++consecutiveInterventions >= maxConsecutiveInterventions)
                        return await FinaliseOnBudgetAsync(state, turnIndex,
                            $"Sensor intervention limit ({maxConsecutiveInterventions}) reached — the model repeatedly produced a response that was rejected.", ct);
                    continue;
                }

                // Allowlist, not denylist: a response is a final answer or a set of calls to dispatch only
                // if it ended normally. Truncated, refused, filtered or unrecognised responses stop here —
                // after PostModelCall (so a content gate still runs) but before tool dispatch (a cut-short
                // tool call may carry partial arguments) and before PreReturn (a validator would challenge
                // a remnant the model cannot repair under the same cap). → ADR-004
                if (CutShortReason(response.StopReason) is { } cutShort)
                    return PartialOutcome(state, response.Text, cutShort);

                if (response.ToolCalls.Count == 0)
                {
                    var (preReturnState, challenged, suppressToolsOnChallenge) = await RunSensorsAsync(state, turnIndex, HookPoint.PreReturn, state.Trajectory[^1], ct);
                    state = preReturnState;
                    if (challenged)
                    {
                        suppressTools = suppressToolsOnChallenge;
                        if (++consecutiveInterventions >= maxConsecutiveInterventions)
                            return await FinaliseOnBudgetAsync(state, turnIndex,
                                $"Sensor intervention limit ({maxConsecutiveInterventions}) reached — the model repeatedly returned an answer that was challenged.", ct);
                        continue;
                    }

                    consecutiveInterventions = 0;
                    var done = state with { Status = AgentStatus.Done };
                    return Finish(new AgentOutcome
                    {
                        TaskId = done.TaskId,
                        Status = AgentStatus.Done,
                        FinalAnswer = response.Text,
                        FinalState = done
                    }, traceReason: null);
                }

                consecutiveInterventions = 0;
                for (var i = 0; i < response.ToolCalls.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    state = await ExecuteToolAsync(state, turnIndex, response.ToolCalls[i], offeredTools, ct);

                    var suspended = await TrySuspendForHumanAsync(state, runId, turnIndex, response.ToolCalls.Skip(i + 1), ct);
                    if (suspended is not null)
                        return suspended;
                }
            }
        }
        // Only genuine cancellation is reported as "cancelled". A collaborator's *own*
        // OperationCanceledException — a model client's request timeout surfaces as
        // TaskCanceledException even though our token is untouched — is a failure like any other,
        // and falls through to the catch-all so the outcome names it.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

    private AgentOutcome FailOutcome(AgentState state, string failureReason, string traceReason) =>
        Finish(new AgentOutcome
        {
            TaskId = state.TaskId,
            Status = AgentStatus.Failed,
            FinalAnswer = null,
            FinalState = state with { Status = AgentStatus.Failed },
            FailureReason = failureReason
        }, traceReason);

    // Every exit path goes through here so the tracer always sees the outcome before the terminal status.
    // traceReason is passed separately because it can differ from the caller-facing FailureReason.
    private AgentOutcome Finish(AgentOutcome outcome, string? traceReason)
    {
        tracer.LogOutcome(outcome);
        tracer.Complete(outcome.TaskId, outcome.Status, traceReason);
        return outcome;
    }

    private const string NotExecutedOnSuspend =
        "Not executed: the run suspended for human input before this call ran. Re-issue it after reading the answer if it is still needed.";

    // Suspends the run for human input when the last tool returned a pending result; returns the
    // AwaitingHuman outcome to bubble out of the loop, or null to keep going. The calls that followed the
    // pending one in the same response are not run — the human may be about to be asked whether one of
    // them is allowed — but each gets an error ToolCallStep after the pending step, so the audit trail
    // and the next prompt still hold a tool_result for every tool_use. The pending step is looked up
    // before those are appended, so they cannot hide it.
    private async Task<AgentOutcome?> TrySuspendForHumanAsync(AgentState state, string runId, int turn, IEnumerable<ToolCall> notRun, CancellationToken ct)
    {
        var lastTool = state.Trajectory.OfType<ToolCallStep>().LastOrDefault();
        if (lastTool?.Result.IsPending != true)
            return null;

        var pending = new PendingHumanInput(lastTool.Result.CallId, lastTool.Result.Content);
        // HarnessAuthored for the same reason: a skipped untrusted tool put nothing into the trajectory.
        foreach (var call in notRun)
            state = state.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: _time.GetUtcNow(),
                Call: call,
                Result: new ToolResult(call.CallId, NotExecutedOnSuspend, IsError: true))
            { HarnessAuthored = true });

        var suspended = state with
        {
            Status = AgentStatus.AwaitingHuman,
            PendingHumanInput = pending
        };
        var checkpointId = Guid.NewGuid().ToString("n");
        var checkpointStart = _time.GetTimestamp();
        await checkpointStore.SaveAsync(new Checkpoint
        {
            CheckpointId = checkpointId,
            RunId = runId,
            CreatedAt = _time.GetUtcNow(),
            TurnNumber = turn,
            State = suspended
        }, ct);
        tracer.LogCheckpoint(suspended.TaskId, turn, checkpointId, _time.GetElapsedTime(checkpointStart));
        return Finish(new AgentOutcome
        {
            TaskId = suspended.TaskId,
            Status = AgentStatus.AwaitingHuman,
            FinalAnswer = null,
            FinalState = suspended,
            PendingHumanInput = pending
        }, traceReason: null);
    }

    private async Task<(AgentState State, bool Intervened, bool SuppressTools)> RunSensorsAsync(AgentState state, int turn, HookPoint hookPoint, Step? triggeringStep, CancellationToken ct)
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
            tracer.LogSensorResult(state.TaskId, turn, hookPoint, sensor.Name, result);
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

    private async Task<(AgentState State, ModelResponse Response, IReadOnlySet<string> OfferedTools)> CallModelAsync(AgentState state, int turn, bool forceFinalise, CancellationToken ct, bool suppressTools = false)
    {
        var buildResult = await contextBuilder.BuildAsync(state, toolRegistry.List(), ct);

        var messages = forceFinalise
            ? WithFinalisationInstruction(buildResult.Messages)
            : buildResult.Messages;

        var modelTools = forceFinalise || suppressTools
            ? []
            : (IReadOnlyList<ToolDefinition>)buildResult.SelectedTools
                .Select(t => new ToolDefinition(t.Name, t.Description, t.InputSchema))
                .ToArray();

        using var scope = tracer.BeginModelCall(state.TaskId, turn, messages, modelTools);
        ModelResponse response;
        try
        {
            response = await modelClient.CallAsync(messages, modelTools, ct);
        }
        // Genuine cancellation is expected control flow, not a call failure. The client's own
        // timeout — a TaskCanceledException with our token untouched — is one, and the span must
        // carry it.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            scope.Fail(ex);
            throw;
        }
        scope.Complete(response);

        var step = new ModelCallStep(
            Id: Guid.NewGuid(),
            Timestamp: _time.GetUtcNow(),
            Prompt: messages,
            Response: response,
            Usage: response.Usage,
            Cost: response.Cost);

        var next = state.AppendStep(step);

        // Commit any compaction the trajectory guide performed this build: persist the rolling
        // summary (null keeps the prior — a view strategy) and accumulate its spend onto the budget.
        if (buildResult.Compaction is { } compaction)
            next = next with
            {
                RollingSummary = compaction.UpdatedSummary ?? next.RollingSummary,
                CompactionUsage = new Usage(
                    next.CompactionUsage.InputTokens + compaction.Usage.InputTokens,
                    next.CompactionUsage.OutputTokens + compaction.Usage.OutputTokens),
                CompactionCost = next.CompactionCost + compaction.Cost
            };

        // Returned rather than stored: the loop is a singleton shared by concurrent runs.
        return (next, response, modelTools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal));
    }

    private async Task<AgentState> ExecuteToolAsync(AgentState state, int turn, ToolCall call, IReadOnlySet<string> offeredTools, CancellationToken ct)
    {
        // The selector and tool suppression decide what the model is shown; this is what makes them bind.
        // The registry knows every tool, so without the check a model that names a withheld tool (by
        // hallucination, from an earlier turn, or on an injected instruction) would still run it.
        // HarnessAuthored: the harness wrote this result and nothing was dispatched, so naming a withheld
        // untrusted tool must not taint the run.
        if (!offeredTools.Contains(call.ToolName))
            return state.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: _time.GetUtcNow(),
                Call: call,
                Result: new ToolResult(call.CallId, $"Tool '{call.ToolName}' is not available on this turn.", IsError: true))
            { HarnessAuthored = true });

        var preStep = new ToolCallStep(
            Id: Guid.NewGuid(),
            Timestamp: _time.GetUtcNow(),
            Call: call,
            Result: new ToolResult(call.CallId, "(pending)"));

        var (afterPre, toolBlocked, _) = await RunSensorsAsync(state, turn, HookPoint.PreToolCall, preStep, ct);
        if (toolBlocked)
        {
            var lastIntervention = afterPre.Trajectory.OfType<SensorInterventionStep>().Last();
            return afterPre.AppendStep(new ToolCallStep(
                Id: Guid.NewGuid(),
                Timestamp: _time.GetUtcNow(),
                Call: call,
                Result: new ToolResult(call.CallId,
                    $"Blocked by sensor '{lastIntervention.SensorName}': {lastIntervention.Reason}",
                    IsError: true))
            { HarnessAuthored = true });
        }

        var ctx = new ToolContext(state.TaskId, call.CallId, state.Metadata);

        var deadline = state.Budget.MaxToolCallDuration;
        using var deadlineCts = deadline is { } d ? new CancellationTokenSource(d, _time) : null;
        using var linkedCts = deadlineCts is null ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct, deadlineCts.Token);

        // Block-scoped so the execute_tool span ends with the tool, before the PostToolCall sensors
        // (which may make model calls of their own) run.
        ToolResult result;
        var harnessAuthored = false;
        using (var scope = tracer.BeginToolCall(state.TaskId, turn, call))
        {
            try
            {
                result = await toolRegistry.DispatchAsync(call, ctx, linkedCts?.Token ?? ct);
            }
            catch (OperationCanceledException) when (deadlineCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
            {
                result = new ToolResult(call.CallId,
                    $"Tool '{call.ToolName}' exceeded the {deadline!.Value.TotalSeconds:0}s tool-call deadline.",
                    IsError: true);
                harnessAuthored = true;
            }
            catch (Exception ex)
            {
                // Not harness-authored: the exception message is tool output (an HttpRequestException can
                // carry a response body), so a taint sensor must still count it.
                result = new ToolResult(call.CallId, $"Tool threw {ex.GetType().Name}: {ex.Message}", IsError: true);
            }
            scope.Complete(result);
        }

        var executed = new ToolCallStep(
            Id: Guid.NewGuid(),
            Timestamp: _time.GetUtcNow(),
            Call: call,
            Result: result)
        { HarnessAuthored = harnessAuthored };

        var afterExec = afterPre.AppendStep(executed);
        if (result.Pins is { Count: > 0 } pins)
            afterExec = afterExec with { Pins = MergePins(afterExec.Pins, pins) };
        var (afterPost, _, _) = await RunSensorsAsync(afterExec, turn, HookPoint.PostToolCall, executed, ct);
        return afterPost;
    }

    // Replace-by-label: existing pins keep their order (minus any the tool re-pinned), new pins appended.
    private static IReadOnlyList<PinnedNote> MergePins(IReadOnlyList<PinnedNote> existing, IReadOnlyList<PinnedNote> incoming)
    {
        var replaced = incoming.Select(p => p.Label).ToHashSet();
        return [.. existing.Where(p => !replaced.Contains(p.Label)), .. incoming];
    }

    private const string FinalisationInstruction =
        "Budget is exhausted. Produce your best final answer now using only what you already know. Do not request more tools.";

    // Every adapter forwards only the first System message and drops the rest, so the instruction is
    // folded into that message rather than appended as a second one — an appended message never reaches
    // the model. A builder that returns no leading System message gets one prepended.
    private static IReadOnlyList<Message> WithFinalisationInstruction(IReadOnlyList<Message> messages) =>
        messages is [{ Role: MessageRole.System } system, ..]
            ? [system with { Content = $"{system.Content}\n\n{FinalisationInstruction}" }, .. messages.Skip(1)]
            : [new Message(MessageRole.System, FinalisationInstruction), .. messages];

    private async Task<AgentOutcome> FinaliseOnBudgetAsync(AgentState state, int turn, string reason, CancellationToken ct)
    {
        var (finalState, response, _) = await CallModelAsync(state, turn, forceFinalise: true, ct);
        // The finalisation call is the likeliest in the run to be truncated — it was just instructed to
        // produce a complete answer in one shot — so name that in the reason rather than handing back a
        // silently clipped answer under a status that only mentions the budget.
        if (CutShortReason(response.StopReason) is { } cutShort)
            reason += $" The finalisation response was also cut short: {cutShort}";

        // The finalisation response is model output like any other, so the PostModelCall content gates
        // (PII redaction) apply to it. Without this the intervention-cap path — which runs exactly when a
        // gate just rejected three responses — would hand a fourth, unchecked one to the caller. A
        // rejection withholds the answer rather than retrying: there is no further model call, and
        // PreReturn is skipped because PartialResult already tells the caller the answer is not final.
        var gated = await RunSensorsAsync(finalState, turn, HookPoint.PostModelCall, finalState.Trajectory[^1], ct);
        if (gated.Intervened)
        {
            // Name the sensor, not its reason: a content gate's reason quotes the flagged content (the PII
            // detector names the PII), and FailureReason goes to the caller, to a parent agent's tool
            // result and ungated into the tracers. The full reason stays on the SensorInterventionStep,
            // which the tracers already gate.
            var withheldBy = gated.State.Trajectory.Skip(finalState.Trajectory.Count).OfType<SensorInterventionStep>().First();
            return PartialOutcome(gated.State, finalAnswer: null,
                $"{reason} The finalisation response was withheld by sensor '{withheldBy.SensorName}'.");
        }

        return PartialOutcome(gated.State, response.Text, reason);
    }

    // Null when the response ended normally; otherwise the FailureReason to report, naming the remedy. The
    // default arm is what makes this an allowlist: a member added later, or a reason an adapter maps to
    // Other, is cut short until someone decides otherwise.
    private static string? CutShortReason(StopReason stopReason) => stopReason switch
    {
        StopReason.EndTurn or StopReason.StopSequence or StopReason.ToolUse => null,
        StopReason.MaxTokens =>
            "Response truncated at the provider's output-token limit — raise the adapter's MaxOutputTokens, or narrow the task.",
        StopReason.ContextWindowExceeded =>
            "Response truncated at the model's context window — the prompt left too little room for the answer. Shrink the prompt: lower CompactionOptions.WindowTokens so less history is kept, or narrow the task. Lowering MaxOutputTokens would only truncate it sooner.",
        StopReason.Refusal =>
            "The model declined to respond — consider retrying on a fallback model, or rephrasing the task.",
        StopReason.ContentFilter =>
            "The provider's content filter withheld or cut the response.",
        _ =>
            $"Response ended with an unrecognised stop reason ({stopReason}), so it was not treated as a final answer."
    };

    // Terminal PartialResult for a run whose best available answer is already in hand. Unlike
    // FinaliseOnBudgetAsync it spends no extra model call — the caller has the text it is going to get.
    private AgentOutcome PartialOutcome(AgentState state, string? finalAnswer, string reason)
    {
        var partial = state with { Status = AgentStatus.PartialResult };
        return Finish(new AgentOutcome
        {
            TaskId = partial.TaskId,
            Status = AgentStatus.PartialResult,
            FinalAnswer = finalAnswer,
            FinalState = partial,
            FailureReason = reason
        }, reason);
    }
}
