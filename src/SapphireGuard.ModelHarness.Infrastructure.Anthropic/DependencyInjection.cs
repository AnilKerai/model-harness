using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Anthropic.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Anthropic;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>
    /// Registers a <see cref="ClaudeModelClient"/> as the model client. Pair with
    /// <c>WithResilientModel</c> from <c>Infrastructure.Resilience</c> to add Polly
    /// retry and circuit-breaker behaviour.
    /// </summary>
    public static ModelHarnessBuilder WithClaudeModel(this ModelHarnessBuilder builder, ClaudeClientOptions options) =>
        builder.WithModel(_ => new ClaudeModelClient(options));
}
