using System.Diagnostics.CodeAnalysis;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static ModelHarnessBuilder WithOllamaModel(this ModelHarnessBuilder builder, OllamaClientOptions options) =>
        builder.WithModel(_ => new OllamaModelClient(options));
}
