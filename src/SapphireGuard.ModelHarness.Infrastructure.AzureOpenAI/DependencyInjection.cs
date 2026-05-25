using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>
    /// Registers an <see cref="AzureOpenAIModelClient"/> as the model client.
    /// Supports both API key and DefaultAzureCredential authentication — set
    /// <see cref="AzureOpenAIClientOptions.ApiKey"/> to null to use managed identity.
    /// Pair with <c>WithResilientModel</c> from <c>Infrastructure.Resilience</c> to add
    /// Polly retry and circuit-breaker behaviour.
    /// </summary>
    public static ModelHarnessBuilder WithAzureOpenAIModel(
        this ModelHarnessBuilder builder,
        AzureOpenAIClientOptions options) =>
        builder.WithModel(_ => new AzureOpenAIModelClient(options));
}
