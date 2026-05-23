using System.Diagnostics.CodeAnalysis;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

[ExcludeFromCodeCoverage]
public sealed record OllamaClientOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";
    public required string ModelId { get; init; }
}
