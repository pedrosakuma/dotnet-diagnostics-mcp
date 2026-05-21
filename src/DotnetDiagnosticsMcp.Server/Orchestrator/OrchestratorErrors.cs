using System;
using System.Threading;
using System.Threading.Tasks;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Stable error kinds the orchestrator surfaces. Mirrors the <c>error.kind</c> values
/// documented in docs/central-orchestrator-design.md §3.4–§3.7.
/// </summary>
/// <remarks>
/// Listed as constants (not an enum) so the tool layer can serialize them verbatim into
/// <see cref="DotnetDiagnosticsMcp.Core.DiagnosticError.Kind"/> without conversion.
/// </remarks>
public static class OrchestratorErrorKinds
{
    public const string InvalidArgument = "InvalidArgument";
    public const string NamespaceNotAllowed = "NamespaceNotAllowed";
    public const string SelectorRejected = "SelectorRejected";
    public const string TooManyResults = "TooManyResults";
    public const string PermissionDenied = "PermissionDenied";
    public const string KubeApiUnavailable = "KubeApiUnavailable";
    public const string OrchestratorDisabled = "OrchestratorDisabled";
}

/// <summary>
/// Base exception for orchestrator policy violations and Kubernetes-call failures. The
/// MCP tool layer catches these and maps them to <see cref="DotnetDiagnosticsMcp.Core.DiagnosticResult{T}"/>
/// failure envelopes with the embedded <see cref="ErrorKind"/>.
/// </summary>
public class OrchestratorException : Exception
{
    public OrchestratorException(string errorKind, string message) : base(message)
    {
        ErrorKind = errorKind;
    }

    public OrchestratorException(string errorKind, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
    }

    public string ErrorKind { get; }
}
