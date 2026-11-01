using System.Diagnostics;
using SapphireGuard.ModelHarness.Framework.State;
using SapphireGuard.ModelHarness.Framework.Tools;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;
using Xunit;

using AnthropicSdkOptions = Anthropic.Core.ClientOptions;

namespace SapphireGuard.ModelHarness.Framework.Tests.Unit.Infrastructure.Anthropic;

public sealed class ClaudeModelClientTests
{
    // ClientOptions is a struct, so a hook that edits its parameter edits a copy. These tests assert on the
    // built SDK client (SdkClient), never on an options value, because a standalone options value would pass
    // even when the client never receives it.
    [Fact]
    public void ConfigureClient_ResultReachesTheSdkClient()
    {
        var client = new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = "test-key",
            ConfigureClient = o => o with { Timeout = TimeSpan.FromSeconds(30), MaxRetries = 0 }
        });

        Assert.Equal(TimeSpan.FromSeconds(30), client.SdkClient.Timeout);
        Assert.Equal(0, client.SdkClient.MaxRetries);
    }

    [Fact]
    public void ConfigureClient_NotSet_LeavesTheSdkDefaults()
    {
        var client = new ClaudeModelClient(new ClaudeClientOptions { ApiKey = "test-key" });

        Assert.Equal("test-key", client.SdkClient.ApiKey);
        Assert.Null(client.SdkClient.Timeout);
        Assert.Null(client.SdkClient.MaxRetries);
    }

    [Fact]
    public void ConfigureClient_ReceivesTheOptionsWithApiKeyAlreadySet()
    {
        string? seen = null;

        var client = new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = "test-key",
            ConfigureClient = o =>
            {
                seen = o.ApiKey;
                return o;
            }
        });

        Assert.Equal("test-key", seen);
        Assert.Equal("test-key", client.SdkClient.ApiKey);
    }

    [Fact]
    public void ConfigureClient_CanOverrideTheApiKeyItReceived()
    {
        var client = new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = "test-key",
            ConfigureClient = o => o with { ApiKey = "other-key" }
        });

        Assert.Equal("other-key", client.SdkClient.ApiKey);
    }

    // The configured Timeout has to reach the transport, not only the client's property: the stub handler
    // never answers, so the call can only end through the SDK's own timeout. The outer token is a backstop that
    // would turn "the timeout was ignored" into a failure of the assertions below rather than a hang.
    [Fact]
    public async Task ConfigureClient_Timeout_EndsACallThatNeverAnswers()
    {
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var http = new HttpClient(new NeverAnswersHandler());
        var client = new ClaudeModelClient(new ClaudeClientOptions
        {
            ApiKey = "test-key",
            ConfigureClient = o => o with
            {
                HttpClient = http,
                Timeout = TimeSpan.FromMilliseconds(300),
                MaxRetries = 0
            }
        });

        var watch = Stopwatch.StartNew();
        var ex = await Record.ExceptionAsync(() => client.CallAsync(
            [new Message(MessageRole.User, "hi")], Array.Empty<ToolDefinition>(), backstop.Token));
        watch.Stop();

        Assert.IsAssignableFrom<OperationCanceledException>(ex);
        Assert.False(backstop.IsCancellationRequested, "the 4 s backstop fired, so the SDK timeout was not applied");
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(3));
    }

    private sealed class NeverAnswersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }
}
