namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

using DotnetDiagnosticsMcp.Core;

/// <summary>
/// Resolves the target <c>processId</c> for every diagnostic tool that operates against a
/// live .NET process. When the caller omits the id and exactly one .NET process is reachable,
/// the resolver auto-selects it transparently — saving the LLM the obligatory
/// <c>list_dotnet_processes</c> + <c>get_diagnostic_capabilities</c> opening round-trips.
/// </summary>
/// <remarks>
/// Resolution outcomes are non-throwing: the tool is expected to translate
/// <see cref="ProcessContextResolution.Error"/> into a structured <see cref="DiagnosticResult{T}"/>
/// so the LLM gets a stable <see cref="DiagnosticError.Kind"/> + a <see cref="NextActionHint"/>
/// instead of a thrown exception.
/// </remarks>
public interface IProcessContextResolver
{
    /// <summary>
    /// Resolves the caller-supplied process id (or auto-resolves one when omitted) and
    /// attaches a capability digest for the chosen target.
    /// </summary>
    /// <param name="requestedProcessId">Process id the caller passed, or <c>null</c>/<c>0</c> to request auto-resolution.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken);
}

/// <summary>Outcome of <see cref="IProcessContextResolver.ResolveAsync"/>.</summary>
/// <param name="Context">Resolved context when successful, otherwise <c>null</c>.</param>
/// <param name="Error">Structured error when resolution failed, otherwise <c>null</c>.</param>
/// <param name="Candidates">Candidate list for ambiguous resolutions so the LLM can pick without a second round-trip.</param>
public sealed record ProcessContextResolution(
    ProcessContext? Context,
    DiagnosticError? Error,
    IReadOnlyList<DotnetProcess>? Candidates = null);
