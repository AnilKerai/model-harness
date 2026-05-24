using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Infrastructure;
using SapphireGuard.ModelHarness.Infrastructure.Tools;
using SapphireGuard.ModelHarness.Samples.Common;
using SapphireGuard.ModelHarness.Samples.SkillLearning;

const string SystemPrompt =
    "You are an assistant that reuses skills. Check the available-skills section and load a relevant " +
    "skill with skill_view before improvising; after solving a non-trivial task, save the approach with skill_manage.";

const string TaskText = "Greet the user warmly.";

var skillDir = Path.Combine(Path.GetTempPath(), "model-harness-skills-" + Guid.NewGuid().ToString("n"));

AgentConsoleWriter.PrintHeader(
    "skill-learning",
    "Run 1 captures a skill; run 2 loads it from disk via SkillsGuide and reuses it. Fully scripted — no API key.");

Console.WriteLine($"Skill directory: {skillDir}");

try
{
    Console.WriteLine();
    Console.WriteLine("── Run 1: no skills yet — the agent solves the task and captures a skill ──");
    AgentConsoleWriter.PrintOutcome(await RunOnceAsync());

    Console.WriteLine();
    Console.WriteLine("── Persisted skill file ──");
    var skillFile = Path.Combine(skillDir, "warm-greeting.md");
    if (File.Exists(skillFile))
        Console.WriteLine(await File.ReadAllTextAsync(skillFile));

    Console.WriteLine();
    Console.WriteLine("── Run 2: SkillsGuide surfaces the persisted skill — the agent loads and reuses it ──");
    AgentConsoleWriter.PrintOutcome(await RunOnceAsync());
}
finally
{
    if (Directory.Exists(skillDir))
        Directory.Delete(skillDir, recursive: true);
}

async Task<AgentOutcome> RunOnceAsync()
{
    var services = new ServiceCollection();

    services.AddModelHarness(builder => builder
        .WithSystemPrompt(SystemPrompt)
        .WithConsoleTracer()
        .WithToolRegistry<InMemoryToolRegistry>()
        .WithFileSkillStore(skillDir)
        .WithModel(_ => new SkillScriptedModelClient()));

    await using var provider = services.BuildServiceProvider();
    return await provider.GetRequiredService<Agent>().RunAsync(
        TaskText,
        budget: new Budget { MaxTurns = 4, MaxContextTokens = 100_000, MaxCostUsd = 1m, MaxWallClock = TimeSpan.FromSeconds(30) });
}
