namespace DotnetDiagnosticsMcp.Core.Artifacts;

/// <summary>
/// Supplies the server-side root directory under which every artifact written by a
/// diagnostic tool (process dumps, JIT-captured method bytes, …) must live. Centralising
/// the root makes sandboxing testable and lets sidecar operators redirect artifacts to a
/// dedicated PVC by setting the <c>MCP_ARTIFACT_ROOT</c> environment variable.
/// </summary>
public interface IArtifactRootProvider
{
    /// <summary>
    /// Absolute, fully-qualified path of the artifact root. Implementations must ensure
    /// the directory exists with restrictive permissions (POSIX 0700) before returning.
    /// </summary>
    string Root { get; }
}
