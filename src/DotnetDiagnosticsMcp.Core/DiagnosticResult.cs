namespace DotnetDiagnosticsMcp.Core;

/// <summary>
/// Discoverability-aware envelope for every diagnostic tool response. Provides a short
/// human-readable <see cref="Summary"/>, a list of suggested <see cref="Hints"/> that the
/// LLM should follow next, and the typed <see cref="Data"/> payload.
/// </summary>
/// <typeparam name="T">Type of the underlying diagnostic payload.</typeparam>
/// <remarks>
/// The envelope is the foundation for handle-based drill-down (see issue #8). Tools that
/// produce large datasets can keep <see cref="Data"/> small (top-N) and use <see cref="Hints"/>
/// to point at follow-up tools that fetch detail by handle.
/// </remarks>
public sealed record DiagnosticResult<T>(
    string Summary,
    IReadOnlyList<NextActionHint> Hints,
    DiagnosticError? Error = null)
{
    /// <summary>The typed diagnostic payload, omitted on failure responses.</summary>
    public T? Data { get; init; }

    /// <summary>True when the call failed and <see cref="Error"/> is populated.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsError => Error is not null;

    /// <summary>
    /// Optional opaque handle that identifies a heavier in-memory artifact (parsed trace,
    /// gcdump, …) the LLM can drill into via a follow-up tool. Omitted when the response is
    /// already self-contained.
    /// </summary>
    public string? Handle { get; init; }

    /// <summary>UTC moment at which <see cref="Handle"/> expires. Use to inform retry/refresh logic.</summary>
    public DateTimeOffset? HandleExpiresAt { get; init; }

    /// <summary>
    /// Per-process digest produced by <see cref="DotnetDiagnosticsMcp.Core.ProcessDiscovery.IProcessContextResolver"/>.
    /// Carried for free on every tool that targets a live process so the LLM never has to
    /// re-run <c>list_dotnet_processes</c>/<c>get_diagnostic_capabilities</c> just to know
    /// which runtime it is talking to. <see cref="DotnetDiagnosticsMcp.Core.ProcessDiscovery.ProcessContext.AutoResolved"/>
    /// signals whether the pid was supplied by the caller or chosen by the server.
    /// </summary>
    public DotnetDiagnosticsMcp.Core.ProcessDiscovery.ProcessContext? ResolvedProcess { get; init; }
}

/// <summary>
/// Non-generic factory helpers for <see cref="DiagnosticResult{T}"/>. Kept separate so the
/// generic type satisfies CA1000 (no static members on generic types).
/// </summary>
public static class DiagnosticResult
{
    /// <summary>Successful response.</summary>
    public static DiagnosticResult<T> Ok<T>(T data, string summary, params NextActionHint[] hints)
        => new(summary, hints) { Data = data };

    /// <summary>Successful response that also publishes a drill-down handle.</summary>
    public static DiagnosticResult<T> OkWithHandle<T>(T data, string summary, string handle, DateTimeOffset expiresAt, params NextActionHint[] hints)
        => new(summary, hints) { Data = data, Handle = handle, HandleExpiresAt = expiresAt };

    /// <summary>Error response with a structured error and at least one recovery hint.</summary>
    public static DiagnosticResult<T> Fail<T>(string summary, DiagnosticError error, params NextActionHint[] hints)
        => new(summary, hints, error);
}

/// <summary>
/// A suggestion to the agent for the next call to make. Surfaced verbatim in
/// <see cref="DiagnosticResult{T}.Hints"/> so a low-context LLM can keep drilling without
/// having to re-read the server instructions on every turn.
/// </summary>
/// <param name="NextTool">Name of the recommended next MCP tool.</param>
/// <param name="Reason">Short human-readable justification (1 sentence).</param>
/// <param name="SuggestedArguments">Optional argument suggestions for the next call.</param>
public sealed record NextActionHint(
    string NextTool,
    string Reason,
    IReadOnlyDictionary<string, object?>? SuggestedArguments = null);

/// <summary>
/// Structured representation of a tool failure. Always carries a <see cref="Kind"/> (machine
/// classification) and an optional <see cref="Detail"/>. Hints describe the recommended recovery.
/// </summary>
/// <param name="Kind">Stable identifier for client-side branching (e.g. "InvalidArgument", "ProcessNotFound", "PermissionDenied").</param>
/// <param name="Message">Human-readable description of the failure.</param>
/// <param name="Detail">Optional extra context (stack trace excerpt, parameter name, etc.).</param>
public sealed record DiagnosticError(
    string Kind,
    string Message,
    string? Detail = null);
