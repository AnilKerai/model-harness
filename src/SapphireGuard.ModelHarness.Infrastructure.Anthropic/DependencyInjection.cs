using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic;

public static class DependencyInjection
{
    public static IServiceCollection AddClaudeModelClient(
        this IServiceCollection services,
        ClaudeClientOptions options) =>
        services.AddModelClient(_ => new ClaudeModelClient(options));
}
