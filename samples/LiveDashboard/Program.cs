using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Dashboard;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);

var apiKey = builder.Configuration["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

// enableSensitiveData: this is a local console, so include model response text — that's the run's result.
var dashboard = new LiveDashboardTracer(enableSensitiveData: true);

builder.Services.AddStandardModelHarness(h =>
{
    h.WithSystemPrompt("You are a sample arithmetic agent. Use the calculator tool to compute results, then answer.")
        .WithLiveDashboardTracer(dashboard)
        .WithTool<EchoTool>()
        .WithTool<CalculatorTool>();

    if (usingRealModel)
        h.WithResilientModel(_ => new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = apiKey!,
            ModelId = builder.Configuration["Anthropic:ModelId"] ?? "claude-haiku-4-5"
        }));
    else
        h.WithModel(_ => new FakeModelClient());
});

var app = builder.Build();

// The whole dashboard — page, assets, and SSE feed — comes from the Dashboard package. `onRun` wires
// the demo "Run" bar to a background agent run; a monitor-only host (one that starts its own runs)
// would just call app.MapHarnessDashboard() with no handler.
app.MapHarnessDashboard("/", onRun: task => { RunAgent(app.Services, task); return Task.CompletedTask; });

// Run one task on startup so opening the page shows immediate activity (the ring buffer means you
// still catch it even if you open the browser after it finishes).
app.Lifetime.ApplicationStarted.Register(() => RunAgent(app.Services, "What is 124 multiplied by 37?"));

app.Run();

// A fresh DI scope per run keeps this correct regardless of the Agent's registered lifetime; the
// scope is captured by the task and disposed only when the run ends.
static void RunAgent(IServiceProvider root, string task) => _ = Task.Run(async () =>
{
    using var scope = root.CreateScope();
    var agent = scope.ServiceProvider.GetRequiredService<Agent>();
    await agent.RunAsync(task);
});
