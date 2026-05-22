using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;
using SapphireGuard.ModelHarness.SampleAgent.Scenarios;

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

const string SystemPrompt =
    "You are a sample arithmetic agent. Use the calculator tool to compute results and then answer the user.";

void ConfigureBase(IServiceCollection services)
{
    services
        .AddTracer(_ => new CompositeTracer(new ConsoleTracer(), new OpenTelemetryTracer()))
        .AddToolRegistry<InMemoryToolRegistry>();

    if (usingRealModel)
        services.AddModelClient(_ => new ResilientModelClientDecorator(
            new ClaudeModelClient(new ClaudeClientOptions
            {
                ApiKey = apiKey!,
                ModelId = config["Anthropic:ModelId"] ?? "claude-haiku-4-5-20251001"
            })));
    else
        services.AddModelClient(_ => new FakeModelClient());

    services.AddSingleton<ITool, EchoTool>();
    services.AddSingleton<ITool, CalculatorTool>();
}

using var cts = new CancellationTokenSource();

var scenarios = new List<(string Name, Func<Task> Run)>
{
    ("happy-path",               () => HappyPath.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
    ("pii-detection",            () => PiiDetection.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
    ("cost-throttle",            () => CostThrottle.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
    ("tool-call-reasonableness", () => ToolCallReasonableness.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
    ("tool-result-sanity",       () => ToolResultSanity.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
    ("checkpoint-resume",        () => CheckpointResume.RunAsync(SystemPrompt, ConfigureBase, cts.Token)),
};

var ollamaModelId = config["Ollama:ModelId"];
if (!string.IsNullOrWhiteSpace(ollamaModelId))
{
    var ollamaOptions = new OllamaClientOptions
    {
        BaseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434",
        ModelId = ollamaModelId
    };
    scenarios.Add(("ollama-tool-call", () => OllamaToolCall.RunAsync(SystemPrompt, ConfigureBase, ollamaOptions, cts.Token)));
}

var filter = args.Length > 0 ? args[0] : null;
var toRun = filter is null
    ? scenarios
    : scenarios.Where(s => s.Name == filter).ToList();

if (toRun.Count == 0)
{
    Console.WriteLine($"No scenario named '{filter}'. Available: {string.Join(", ", scenarios.Select(s => s.Name))}");
    return;
}

foreach (var (_, run) in toRun)
    await run();
