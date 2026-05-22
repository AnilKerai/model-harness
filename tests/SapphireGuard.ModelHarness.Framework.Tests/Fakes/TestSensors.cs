using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tests.Fakes;

/// <summary>Blocks N times then passes on all subsequent checks.</summary>
public sealed class CountdownSensor : ISensor
{
    private int _blocksRemaining;
    private readonly string _reason;

    public string Name { get; }
    public IReadOnlySet<HookPoint> HookPoints { get; }

    public CountdownSensor(HookPoint hookPoint, int blockCount = 1, string name = "countdown", string reason = "test block")
    {
        HookPoints = new HashSet<HookPoint> { hookPoint };
        _blocksRemaining = blockCount;
        Name = name;
        _reason = reason;
    }

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (_blocksRemaining > 0)
        {
            _blocksRemaining--;
            return Task.FromResult(SensorResult.Block(_reason));
        }
        return Task.FromResult(SensorResult.Pass);
    }
}

/// <summary>Always blocks — simulates a persistent condition like a cost threshold.</summary>
public sealed class AlwaysBlockSensor : ISensor
{
    private readonly string _reason;

    public string Name { get; }
    public IReadOnlySet<HookPoint> HookPoints { get; }

    public AlwaysBlockSensor(HookPoint hookPoint, string name = "always-block", string reason = "always blocked")
    {
        HookPoints = new HashSet<HookPoint> { hookPoint };
        Name = name;
        _reason = reason;
    }

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        => Task.FromResult(SensorResult.Block(_reason));
}

/// <summary>Always passes. Useful as an explicit no-op in sensor lists.</summary>
public sealed class AlwaysPassSensor : ISensor
{
    public string Name { get; }
    public IReadOnlySet<HookPoint> HookPoints { get; }

    public AlwaysPassSensor(HookPoint hookPoint, string name = "always-pass")
    {
        HookPoints = new HashSet<HookPoint> { hookPoint };
        Name = name;
    }

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        => Task.FromResult(SensorResult.Pass);
}
