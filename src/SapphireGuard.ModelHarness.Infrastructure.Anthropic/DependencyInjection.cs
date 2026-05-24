using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static ModelHarnessBuilder WithClaudeModel(this ModelHarnessBuilder builder, ClaudeClientOptions options) =>
        builder.WithModel(_ => new ClaudeModelClient(options));
}
