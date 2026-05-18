namespace DotnetDbgMcp.Core.Dump;

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
