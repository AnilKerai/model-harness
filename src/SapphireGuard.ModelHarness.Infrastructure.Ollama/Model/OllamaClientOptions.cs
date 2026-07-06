using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

[ExcludeFromCodeCoverage]
public sealed record OllamaClientOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public required string ModelId { get; init; }

    /// <summary>
    /// Optional hook to configure the <see cref="HttpClient"/> backing the Ollama client — most
    /// usefully its <see cref="HttpClient.Timeout"/> (OllamaSharp has no built-in retry or timeout).
    /// <see cref="OllamaClientOptions.BaseUrl"/> is applied first, then this hook runs.
    /// </summary>
    public Action<HttpClient>? ConfigureHttpClient { get; init; }
}
