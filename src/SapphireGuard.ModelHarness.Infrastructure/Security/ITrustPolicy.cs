namespace SapphireGuard.ModelHarness.Infrastructure.Security;

/// <summary>
/// Classifies tools by their trust role. Used by <see cref="Sensors.TaintTrackingSensor"/>
/// to determine which tool results taint the trajectory and which actions should be blocked
/// while tainted content is present.
/// </summary>
public interface ITrustPolicy
{
    /// <summary>
    /// Returns <see langword="true"/> if results from <paramref name="toolName"/> should be
    /// treated as untrusted external content — e.g. web fetches, document readers, third-party
    /// API responses.
    /// </summary>
    bool IsUntrustedSource(string toolName);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="toolName"/> represents a privileged
    /// side-effecting action — e.g. sending email, executing code, making payments — that
    /// should not run while tainted content is in the trajectory.
    /// </summary>
    bool IsPrivilegedAction(string toolName);
}
