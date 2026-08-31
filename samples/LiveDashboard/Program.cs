using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using SapphireGuard.ModelHarness.Infrastructure.Model;
using SapphireGuard.ModelHarness.Infrastructure.Resilience;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true);

var apiKey = builder.Configuration["Anthropic:ApiKey"];
var usingRealModel = !string.IsNullOrWhiteSpace(apiKey);

// The dashboard endpoints need this exact instance to Subscribe(), so construct it here and hand
// it to the builder (which also registers it as a singleton for anything else that wants it).
var dashboard = new LiveDashboardTracer();

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

// The dashboard is wwwroot/index.html + app.css + app.js — plain static files, served as-is.
app.UseDefaultFiles();
app.UseStaticFiles();

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// Server-Sent Events: replay the backlog, then stream live. `id:` carries the sequence so a browser
// that reconnects resumes cleanly. No SignalR, no WebSocket — SSE is one-way and that is all we need.
app.MapGet("/feed", async (HttpContext ctx, LiveDashboardTracer feed, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers["X-Accel-Buffering"] = "no";
    await foreach (var evt in feed.Subscribe(ct))
    {
        await ctx.Response.WriteAsync($"id: {evt.Seq}\ndata: {JsonSerializer.Serialize(evt, json)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

// Fire-and-forget: kick a run and return immediately — the browser watches it unfold on /feed.
app.MapPost("/run", (RunRequest? req) =>
{
    RunAgent(app.Services, string.IsNullOrWhiteSpace(req?.Task) ? "What is 124 multiplied by 37?" : req!.Task);
    return Results.Accepted();
});

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

internal sealed record RunRequest(string? Task);
