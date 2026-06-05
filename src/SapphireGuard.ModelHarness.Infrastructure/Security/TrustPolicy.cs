namespace SapphireGuard.ModelHarness.Infrastructure.Security;

/// <summary>
/// Composition-root-configured trust policy. The operator declares which tool names are
/// untrusted sources and which are privileged actions; everything else is unconstrained.
/// Tool name comparison is case-insensitive.
/// </summary>
public sealed class TrustPolicy(
    IEnumerable<string> untrustedSources,
    IEnumerable<string> privilegedActions) : ITrustPolicy
{
    private readonly HashSet<string> _untrustedSources =
        new(untrustedSources, StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _privilegedActions =
        new(privilegedActions, StringComparer.OrdinalIgnoreCase);

    public bool IsUntrustedSource(string toolName) => _untrustedSources.Contains(toolName);

    public bool IsPrivilegedAction(string toolName) => _privilegedActions.Contains(toolName);
}
