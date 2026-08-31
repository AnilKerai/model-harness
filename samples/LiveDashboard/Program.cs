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

var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

app.MapGet("/", () => Results.Content(DashboardPage.Html, "text/html"));

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

internal static class DashboardPage
{
    // ponytail: the whole UI is one inlined HTML string — no wwwroot, no static-file middleware,
    // no build step, no npm. Vanilla JS + EventSource is the entire client.
    public const string Html = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>Model Harness — Live</title>
        <style>
          :root { color-scheme: dark; }
          * { box-sizing: border-box; }
          body { margin: 0; font: 14px/1.5 ui-monospace, SFMono-Regular, Menlo, monospace;
                 background: #0d1117; color: #c9d1d9; }
          header { position: sticky; top: 0; background: #161b22; border-bottom: 1px solid #30363d;
                   padding: 12px 16px; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
          h1 { font-size: 15px; margin: 0; color: #58a6ff; font-weight: 600; }
          .grow { flex: 1; }
          input { background: #0d1117; border: 1px solid #30363d; color: #c9d1d9; padding: 6px 10px;
                  border-radius: 6px; min-width: 240px; }
          button { background: #238636; border: 0; color: #fff; padding: 6px 14px; border-radius: 6px;
                   cursor: pointer; font: inherit; }
          button:hover { background: #2ea043; }
          .stats { display: flex; gap: 18px; font-size: 13px; }
          .stats b { color: #e6edf3; }
          #log { padding: 8px 16px; }
          .row { display: grid; grid-template-columns: 46px 92px 1fr; gap: 12px; padding: 3px 0;
                 border-bottom: 1px solid #21262d; align-items: baseline; }
          .turn { color: #6e7681; text-align: right; }
          .kind { font-size: 11px; text-transform: uppercase; letter-spacing: .04em; }
          .k-run, .k-done { color: #58a6ff; } .k-model, .k-modelstart { color: #d2a8ff; }
          .k-tool { color: #7ee787; } .k-sensor { color: #f0883e; }
          .k-budget { color: #6e7681; } .k-warn, .k-error { color: #ff7b72; }
          .k-compaction, .k-ratelimit, .k-checkpoint { color: #79c0ff; }
          .msg { white-space: pre-wrap; word-break: break-word; }
        </style>
        </head>
        <body>
        <header>
          <h1>◐ Model Harness</h1>
          <input id="task" placeholder="Ask the agent something…" value="What is 124 multiplied by 37?">
          <button onclick="run()">Run</button>
          <span class="grow"></span>
          <div class="stats">
            <span>turns <b id="turns">–</b></span>
            <span>tokens <b id="tokens">–</b></span>
            <span>cost <b id="cost">–</b></span>
            <span id="dot">●</span>
          </div>
        </header>
        <div id="log"></div>
        <script>
          const log = document.getElementById('log');
          function add(e) {
            if (e.kind === 'budget' && e.detail) {
              document.getElementById('turns').textContent = e.detail.turnsUsed + '/' + e.detail.maxTurns;
              document.getElementById('tokens').textContent = e.detail.tokensUsed;
              document.getElementById('cost').textContent = '$' + Number(e.detail.costUsed).toFixed(4);
              return;
            }
            const row = document.createElement('div');
            row.className = 'row';
            const k = e.kind.replace(':', '');
            row.innerHTML =
              '<span class="turn">' + (e.turn + 1) + '</span>' +
              '<span class="kind k-' + k + '">' + e.kind + '</span>' +
              '<span class="msg">' + escapeHtml(e.summary) + '</span>';
            log.appendChild(row);
            window.scrollTo(0, document.body.scrollHeight);
          }
          function escapeHtml(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; }
          function run() {
            fetch('/run', { method: 'POST', headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ task: document.getElementById('task').value }) });
          }
          const es = new EventSource('/feed');
          es.onmessage = ev => add(JSON.parse(ev.data));
          es.onopen = () => document.getElementById('dot').style.color = '#3fb950';
          es.onerror = () => document.getElementById('dot').style.color = '#ff7b72';
        </script>
        </body>
        </html>
        """;
}
