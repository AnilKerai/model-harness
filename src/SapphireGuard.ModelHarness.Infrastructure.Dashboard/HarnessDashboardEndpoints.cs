using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SapphireGuard.ModelHarness.Infrastructure.Tracing;

namespace SapphireGuard.ModelHarness.Infrastructure.Dashboard;

/// <summary>
/// Maps the SapphireGuard.ModelHarness live dashboard onto an ASP.NET Core app. The page, styles and
/// script are embedded in this package (no <c>wwwroot</c> to copy); the run/turn grouping happens in
/// the browser over the event stream, so the only server dependency is a <see cref="LiveDashboardTracer"/>
/// registered in DI (wire it with <c>WithLiveDashboardTracer()</c>).
/// </summary>
public static class HarnessDashboardEndpoints
{
    private static readonly Assembly Asm = typeof(HarnessDashboardEndpoints).Assembly;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the live dashboard under <paramref name="prefix"/>: the page at <c>{prefix}</c>, its assets, and
    /// a Server-Sent-Events feed at <c>{prefix}/feed</c> streamed from the registered <see cref="LiveDashboardTracer"/>.
    /// By default the dashboard is a read-only monitor of runs the host triggers elsewhere. Supply
    /// <paramref name="onRun"/> to reveal a "run a task" bar and map <c>POST {prefix}/run</c> to it — handy for a
    /// standalone demo; omit it for a monitor embedded in an app that already starts its own runs.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder (e.g. a <c>WebApplication</c>).</param>
    /// <param name="prefix">Path the dashboard is served under. Default <c>/dashboard</c>; <c>/</c> serves it at the root.</param>
    /// <param name="onRun">Optional handler invoked with the task text a viewer submits. Typically starts an agent run in the background and returns immediately.</param>
    public static IEndpointRouteBuilder MapHarnessDashboard(
        this IEndpointRouteBuilder endpoints,
        string prefix = "/dashboard",
        Func<string, Task>? onRun = null)
    {
        var root = "/" + prefix.Trim('/');          // "/dashboard", or "/" when prefix is empty/"/"
        var at = root == "/" ? "" : root;           // path base for sub-routes ("" so "/" + "feed" works)
        var baseHref = root == "/" ? "/" : root + "/";
        var runEnabled = onRun is not null;

        // <base href> makes the page's relative URLs (app.css, app.js, feed, run) resolve under the prefix;
        // the body class reveals the run bar only when a run handler was supplied.
        endpoints.MapGet(root, () => Results.Content(
            ReadResource("index.html")
                .Replace("<!--base-->", $"<base href=\"{baseHref}\">")
                .Replace("<body>", runEnabled ? "<body class=\"run-enabled\">" : "<body>"),
            "text/html"));

        endpoints.MapGet($"{at}/app.css", () => Results.Content(ReadResource("app.css"), "text/css"));
        endpoints.MapGet($"{at}/app.js", () => Results.Content(ReadResource("app.js"), "text/javascript"));

        endpoints.MapGet($"{at}/feed", async (HttpContext ctx, LiveDashboardTracer feed, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            await foreach (var evt in feed.Subscribe(ct))
            {
                await ctx.Response.WriteAsync($"id: {evt.Seq}\ndata: {JsonSerializer.Serialize(evt, Json)}\n\n", ct);
                await ctx.Response.Body.FlushAsync(ct);
            }
        });

        if (onRun is not null)
            endpoints.MapPost($"{at}/run", async (RunRequest? req) =>
            {
                if (string.IsNullOrWhiteSpace(req?.Task)) return Results.BadRequest();
                await onRun(req.Task);
                return Results.Accepted();
            });

        return endpoints;
    }

    private static string ReadResource(string name)
    {
        var resourceName = Asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".wwwroot." + name, StringComparison.Ordinal));
        using var stream = Asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record RunRequest(string? Task);
}
