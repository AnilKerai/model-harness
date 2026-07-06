using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;
using Xunit;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Sensors;

public sealed class DefaultSensorRunnerTests
{
    private static AgentState State() => AgentState.NewTask("t", new Framework.State.Budget
    {
        MaxTurns = 10, MaxTotalTokens = 100_000, MaxCost = 10m,
        MaxWallClock = TimeSpan.FromMinutes(1)
    }, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RunAsync_NoSensors_ReturnsEmpty()
    {
        var runner = new DefaultSensorRunner([]);
        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAsync_SensorNotRegisteredAtHookpoint_NotCalled()
    {
        var sensor = new CountdownSensor(HookPoint.PostModelCall, blockCount: 1);
        var runner = new DefaultSensorRunner([sensor]);

        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task RunAsync_SensorRegisteredAtHookpoint_Called()
    {
        var sensor = new CountdownSensor(HookPoint.PreModelCall, blockCount: 0);
        var runner = new DefaultSensorRunner([sensor]);

        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(sensor, results[0].Sensor);
        Assert.False(results[0].Result.IsIntervene);
    }

    [Fact]
    public async Task RunAsync_MultipleSensorsAtSameHookpoint_AllCalled()
    {
        var s1 = new CountdownSensor(HookPoint.PostModelCall, blockCount: 0, name: "s1");
        var s2 = new CountdownSensor(HookPoint.PostModelCall, blockCount: 0, name: "s2");
        var runner = new DefaultSensorRunner([s1, s2]);

        var results = await runner.RunAsync(HookPoint.PostModelCall, State(), null, CancellationToken.None);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task RunAsync_SensorIntervenes_ResultReflectsIntervention()
    {
        var sensor = new CountdownSensor(HookPoint.PreModelCall, blockCount: 1, reason: "stop it");
        var runner = new DefaultSensorRunner([sensor]);

        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);

        Assert.Single(results);
        Assert.True(results[0].Result.IsIntervene);
        Assert.Equal("stop it", results[0].Result.Reason);
    }

    [Fact]
    public async Task RunAsync_SensorThrows_FailsOpenWithErrorResult_OtherSensorsUnaffected()
    {
        // One sensor throwing must not fault the Task.WhenAll batch and take down the run: it is
        // surfaced as an error result (IsError, not an intervention) and the other sensor still runs.
        var throwing = new ThrowingSensor(HookPoint.PreModelCall, name: "boom");
        var healthy = new CountdownSensor(HookPoint.PreModelCall, blockCount: 0, name: "ok");
        var runner = new DefaultSensorRunner([throwing, healthy]);

        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);

        Assert.Equal(2, results.Count);

        var failed = results.Single(r => r.Sensor.Name == "boom").Result;
        Assert.True(failed.IsError);
        Assert.False(failed.IsIntervene);
        Assert.Contains("boom threw", failed.Reason);

        var ok = results.Single(r => r.Sensor.Name == "ok").Result;
        Assert.False(ok.IsError);
        Assert.False(ok.IsIntervene);
    }

    [Fact]
    public async Task RunAsync_SensorAtDifferentHookpoint_NotInResults()
    {
        var preModel = new CountdownSensor(HookPoint.PreModelCall, blockCount: 0);
        var postModel = new CountdownSensor(HookPoint.PostModelCall, blockCount: 0);
        var runner = new DefaultSensorRunner([preModel, postModel]);

        var results = await runner.RunAsync(HookPoint.PreModelCall, State(), null, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(preModel, results[0].Sensor);
    }
}
