using SapphireGuard.ModelHarness.Framework.Sensors;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Fakes;

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
            return Task.FromResult(SensorResult.Intervene(_reason));
        }
        return Task.FromResult(SensorResult.Pass);
    }
}

/// <summary>Throws on every check — verifies the runner fails open instead of faulting the run.</summary>
public sealed class ThrowingSensor(HookPoint hookPoint, string name = "throwing") : ISensor
{
    public string Name => name;
    public IReadOnlySet<HookPoint> HookPoints { get; } = new HashSet<HookPoint> { hookPoint };

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
        => throw new InvalidOperationException("sensor exploded");
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

/// <summary>
/// Reports Usage and Cost on every check. Intervenes for the first N calls
/// (controlled by blockCount), then passes — always with usage.
/// </summary>
public sealed class UsageReportingSensor : ISensor
{
    private readonly Usage _usage;
    private readonly decimal _cost;
    private int _blocksRemaining;

    public string Name { get; }
    public IReadOnlySet<HookPoint> HookPoints { get; }

    public UsageReportingSensor(HookPoint hookPoint, Usage usage, decimal cost, int blockCount = 0, string name = "usage-sensor")
    {
        HookPoints = new HashSet<HookPoint> { hookPoint };
        Name = name;
        _usage = usage;
        _cost = cost;
        _blocksRemaining = blockCount;
    }

    public Task<SensorResult> CheckAsync(HookPoint hookPoint, AgentState state, Step? triggeringStep, CancellationToken ct)
    {
        if (_blocksRemaining > 0)
        {
            _blocksRemaining--;
            return Task.FromResult(SensorResult.InterveneWithUsage("test intervention", _usage, _cost));
        }
        return Task.FromResult(SensorResult.PassWithUsage(_usage, _cost));
    }
}
