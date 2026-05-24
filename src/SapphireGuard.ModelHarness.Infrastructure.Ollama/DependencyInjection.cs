using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    /// <summary>
    /// Registers an <see cref="OllamaModelClient"/> as the model client, targeting a
    /// locally running Ollama instance. The model must be pulled with <c>ollama pull</c>
    /// before use.
    /// </summary>
    public static ModelHarnessBuilder WithOllamaModel(this ModelHarnessBuilder builder, OllamaClientOptions options) =>
        builder.WithModel(_ => new OllamaModelClient(options));
}
