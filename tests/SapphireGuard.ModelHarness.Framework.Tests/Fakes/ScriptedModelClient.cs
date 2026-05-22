using SapphireGuard.ModelHarness.Framework.Model;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;

namespace SapphireGuard.ModelHarness.Framework.Tests.Fakes;

/// <summary>
/// Returns pre-canned responses in order. Throws if the caller asks for more
/// responses than were scripted — a test that does so has a logic error.
/// </summary>
public sealed class ScriptedModelClient : IModelClient
{
    private readonly Queue<ModelResponse> _responses;
    private int _callCount;

    public int CallCount => _callCount;

    public ScriptedModelClient(params ModelResponse[] responses)
    {
        _responses = new Queue<ModelResponse>(responses);
    }

    public Task<ModelResponse> CallAsync(
        IReadOnlyList<Message> messages,
        IReadOnlyList<ToolDefinition> availableTools,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _callCount);

        if (!_responses.TryDequeue(out var response))
            throw new InvalidOperationException(
                $"ScriptedModelClient ran out of responses after {_callCount} call(s). Add more scripted responses.");

        return Task.FromResult(response);
    }
}
