namespace SapphireGuard.ModelHarness.Framework.Tools;

public interface IHumanChannel
{
    Task<string> AskAsync(string question, CancellationToken ct = default);
}
