namespace DotnetDiagnosticsMcp.Core.Artifacts;

/// <summary>
/// Thrown by <see cref="SafeArtifactPath"/> when a caller-supplied path is rejected by
/// the sandbox policy (absolute path, traversal, symlink escape). Surfaced by the MCP
/// tool layer as a structured <c>InvalidArtifactPath</c> error envelope.
/// </summary>
public sealed class ArtifactPathException : Exception
{
    public ArtifactPathException(string parameterName, string reason)
        : base($"Argument '{parameterName}' rejected by artifact sandbox: {reason}")
    {
        ParameterName = parameterName;
        Reason = reason;
    }

    public string ParameterName { get; }

    public string Reason { get; }
}
