using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static IServiceCollection AddClaudeModelClient(
        this IServiceCollection services,
        ClaudeClientOptions options) =>
        services.AddModelClient(_ => new ClaudeModelClient(options));
}
