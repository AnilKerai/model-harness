using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Sensors;
using SapphireGuard.ModelHarness.Infrastructure.Tools;

namespace SapphireGuard.ModelHarness.Infrastructure.MultiAgent;

/// <summary>
/// Creates and manages isolated agents by name. Each agent gets its own
/// <see cref="ServiceCollection"/> and <see cref="ServiceProvider"/> — no services
/// are shared between agents unless explicitly registered in each agent's configure callback.
/// Sub-containers are built lazily on first <see cref="GetAgent"/> call and disposed
/// when the factory is disposed.
/// </summary>
public sealed class AgentFactory : IAsyncDisposable
{
    private readonly Dictionary<string, Action<ModelHarnessBuilder>> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (ServiceProvider Provider, Framework.Agent Agent)> _built = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Registers a named agent with a bare <see cref="ModelHarnessBuilder"/> configure callback.
    /// Use <see cref="AddStandardAgent"/> to pre-wire opinionated defaults first.
    /// </summary>
    public void AddAgent(string name, Action<ModelHarnessBuilder> configure) =>
        _definitions[name] = configure;

    /// <summary>
    /// Registers a named agent with the same opinionated defaults as
    /// <c>AddStandardModelHarness</c> pre-applied before the user's configure callback.
    /// </summary>
    public void AddStandardAgent(string name, Action<ModelHarnessBuilder> configure) =>
        AddAgent(name, builder =>
        {
            builder
                .WithToolRegistry<InMemoryToolRegistry>()
                .WithSensor<StuckDetector>()
                .WithSensor<ProgressCheckSensor>()
                .WithSensor<PromptInjectionSensor>()
                .WithOtelTracer();
            configure(builder);
        });

    /// <summary>
    /// Returns the <see cref="Framework.Agent"/> for the given name, building its isolated
    /// sub-container on first call.
    /// </summary>
    /// <exception cref="InvalidOperationException">No agent with that name is registered.</exception>
    public Framework.Agent GetAgent(string name)
    {
        lock (_lock)
        {
            if (_built.TryGetValue(name, out var cached))
                return cached.Agent;

            if (!_definitions.TryGetValue(name, out var configure))
                throw new InvalidOperationException(
                    $"No agent named '{name}' is registered. Registered agents: {string.Join(", ", _definitions.Keys)}");

            var services = new ServiceCollection();
            services.AddModelHarness(configure);
            var provider = services.BuildServiceProvider();
            var agent = provider.GetRequiredService<Framework.Agent>();
            _built[name] = (provider, agent);
            return agent;
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (provider, _) in _built.Values)
            await provider.DisposeAsync();
    }
}
