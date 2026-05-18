namespace DotnetDbgMcp.Core.Dump;

/// <summary>
/// Writes a process dump of a running .NET application to disk via the diagnostic IPC channel.
/// </summary>
public interface IProcessDumper
{
    /// <summary>
    /// Writes a dump and returns its location. If <paramref name="outputDirectory"/> is null,
    /// the implementation picks a temporary directory.
    /// </summary>
    Task<DumpResult> WriteDumpAsync(
        int processId,
        ProcessDumpType dumpType,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default);
}
