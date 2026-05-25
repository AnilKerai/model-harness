using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Infrastructure.AzureOpenAI.Model;

[ExcludeFromCodeCoverage]
public sealed record AzureOpenAIClientOptions
{
    public required Uri Endpoint { get; init; }

    /// <summary>
    /// The name of the model deployment in Azure AI Foundry / Azure OpenAI Service.
    /// This is the deployment name, not the underlying model name (e.g. "my-gpt4o-deployment").
    /// </summary>
    public required string DeploymentName { get; init; }

    /// <summary>
    /// API key for authentication. When null, DefaultAzureCredential is used —
    /// recommended for production where managed identity is available.
    /// </summary>
    public string? ApiKey { get; init; }
}
