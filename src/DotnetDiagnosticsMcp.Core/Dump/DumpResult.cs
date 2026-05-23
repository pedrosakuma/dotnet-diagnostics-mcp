namespace DotnetDiagnosticsMcp.Core.Dump;

/// <summary>Dump kind requested from the runtime. Mirrors <c>Microsoft.Diagnostics.NETCore.Client.DumpType</c>.</summary>
public enum ProcessDumpType
{
    /// <summary>Minimal user-mode dump (smallest).</summary>
    Mini = 1,
    /// <summary>Minimal dump plus the managed heap.</summary>
    WithHeap = 2,
    /// <summary>Triage dump (small, sanitized of strings/PII).</summary>
    Triage = 3,
    /// <summary>Full process dump (largest, most expensive).</summary>
    Full = 4,
}

/// <summary>Result of a <see cref="IProcessDumper"/> request.</summary>
public sealed record DumpResult(
    int ProcessId,
    ProcessDumpType DumpType,
    string FilePath,
    long FileSizeBytes,
    DateTimeOffset CreatedAt);

/// <summary>
/// Discriminated envelope returned by the <c>collect_process_dump</c> MCP tool.
/// Carries either a written <see cref="DumpResult"/> (when the caller passed
/// <c>confirm=true</c> and both required scopes — <c>dump-write</c> + <c>ptrace</c>)
/// or a preview of the dump that would have been written (the
/// <c>confirmation_required</c> defense-in-depth path described in
/// <c>docs/rfcs/0001-per-tool-authorization-scopes.md</c> §4).
/// </summary>
/// <remarks>
/// Wrapping <see cref="DumpResult"/> rather than overloading it keeps the on-success
/// payload byte-identical to the historical shape (just accessed via <see cref="Dump"/>)
/// while letting the confirmation-required path surface the preview fields the LLM
/// needs to decide whether to retry with <c>confirm=true</c>.
/// </remarks>
public sealed record DumpToolResult
{
    /// <summary>Stable discriminator. One of <c>"dump_written"</c> or
    /// <c>"confirmation_required"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Human-readable explanation. Populated on the confirmation-required
    /// path; null on success (the wrapping <c>DiagnosticResult.Summary</c> covers
    /// that case).</summary>
    public string? Message { get; init; }

    /// <summary>Process id the tool would dump (server-resolved when the caller did
    /// not supply <c>processId</c>). Populated on both paths so the LLM can show
    /// the operator exactly which process the dump would target.</summary>
    public int? TargetPid { get; init; }

    /// <summary>Dump type that would be written (mirrors the <c>dumpType</c>
    /// parameter). Populated on both paths.</summary>
    public ProcessDumpType? DumpType { get; init; }

    /// <summary>Relative output sub-path supplied by the caller (verbatim, before
    /// the artifact-root sandbox is applied). Populated on both paths; null when
    /// the caller relied on the default.</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>Populated only when <see cref="Kind"/> is <c>"dump_written"</c>.</summary>
    public DumpResult? Dump { get; init; }
}

/// <summary>Stable string constants for <see cref="DumpToolResult.Kind"/>.</summary>
public static class DumpToolResultKinds
{
    public const string DumpWritten = "dump_written";
    public const string ConfirmationRequired = "confirmation_required";
}
