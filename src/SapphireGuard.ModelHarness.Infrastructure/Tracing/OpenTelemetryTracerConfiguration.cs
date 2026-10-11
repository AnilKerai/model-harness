namespace SapphireGuard.ModelHarness.Infrastructure.Tracing;

// Mutable accumulator behind WithOtelTracer. One per harness, so a repeat call (the standard entry points
// already make one) reconfigures the single OpenTelemetryTracer instead of adding a second, duplicate span tree.
internal sealed class OpenTelemetryTracerConfiguration
{
    public bool EnableSensitiveData { get; set; }
    public string? AgentName { get; set; }

    public OpenTelemetryTracer Build() => new(EnableSensitiveData, AgentName);
}
