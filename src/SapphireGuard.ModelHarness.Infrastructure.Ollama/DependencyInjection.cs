using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using SapphireGuard.ModelHarness.Framework;
using SapphireGuard.ModelHarness.Infrastructure.Ollama.Model;

namespace SapphireGuard.ModelHarness.Infrastructure.Ollama;

[ExcludeFromCodeCoverage]
public static class DependencyInjection
{
    public static IServiceCollection AddOllamaModelClient(
        this IServiceCollection services,
        OllamaClientOptions options) =>
        services.AddModelClient(_ => new OllamaModelClient(options));

    public static ModelHarnessBuilder WithOllamaModel(this ModelHarnessBuilder builder, OllamaClientOptions options) =>
        builder.WithModel(_ => new OllamaModelClient(options));
}
