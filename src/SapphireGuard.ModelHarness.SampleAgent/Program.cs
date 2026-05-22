using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.Framework.Tracing;
using SapphireGuard.ModelHarness.SampleAgent.Scenarios;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── Configuration ────────────────────────────────────────────────────────────

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
    .Build();

var apiKey = config["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

if (!usingRealModel)
    Console.WriteLine(
        "WARNING: Anthropic:ApiKey not set — falling back to FakeModelClient. " +
        "Add appsettings.local.json with { \"Anthropic\": { \"ApiKey\": \"sk-ant-...\" } } to use Claude.");

// ── Base services shared across all scenarios ─────────────────────────────────

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

void ConfigureBase(IServiceCollection services)
{
    services
        .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
        .AddToolRegistry<InMemoryToolRegistry>()
        .AddModelClient(_ =>
        {
            IModelClient inner = usingRealModel
                ? new ClaudeModelClient(new ClaudeClientOptions
                {
                    ApiKey = apiKey!,
                    ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5-20251001"
                })
                : new FakeModelClient();
            return new PollyResilientModelClient(inner);
        });

    services.AddSingleton<ITool, EchoTool>();
    services.AddSingleton<ITool, CalculatorTool>();
}

// ── Run all scenarios ─────────────────────────────────────────────────────────

var runner = new ScenarioRunner(SystemPrompt, ConfigureBase);
using var cts = new CancellationTokenSource();

// To run a single scenario by name, pass it as a CLI argument:
//   dotnet run --project ... -- pii-detection
var filter = args.Length > 0 ? args[0] : null;
var scenarios = filter is null
    ? ScenarioLibrary.All
    : ScenarioLibrary.All.Where(s => s.Name == filter).ToList();

if (scenarios.Count == 0)
{
    Console.WriteLine($"No scenario named '{filter}'. Available: {string.Join(", ", ScenarioLibrary.All.Select(s => s.Name))}");
    return;
}

foreach (var scenario in scenarios)
    await runner.RunAsync(scenario, cts.Token);
