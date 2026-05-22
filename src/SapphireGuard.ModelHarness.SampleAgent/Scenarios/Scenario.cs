using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework.Loop;
using SapphireGuard.ModelHarness.Framework.State;

namespace SapphireGuard.ModelHarness.SampleAgent.Scenarios;

/// <summary>
/// A self-contained demo scenario. Add new scenarios to <see cref="ScenarioLibrary"/>.
/// </summary>
public sealed record Scenario(
    string Name,
    string Description,
    string TaskText,
    Budget? Budget = null,
    Action<IServiceCollection>? ConfigureSensors = null,
    Func<AgentOutcome, IServiceProvider, CancellationToken, Task>? PostRun = null);
