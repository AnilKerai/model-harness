namespace SapphireGuard.ModelHarness.Framework.Tools;

/// <summary>Carries the details of a human-input request dispatched by <see cref="IHumanNotifier"/>.</summary>
public sealed record HumanInputRequest(string TaskId, string CallId, string Question);
