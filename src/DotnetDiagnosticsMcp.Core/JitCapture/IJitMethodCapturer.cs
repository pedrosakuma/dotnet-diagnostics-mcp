namespace DotnetDiagnosticsMcp.Core.JitCapture;

/// <summary>
/// Reads JIT-emitted native code (the "code-heap" for a managed method) from a live .NET
/// process or an offline process dump, and writes the raw bytes to disk as a side-channel
/// for <c>dotnet-native-mcp.disassemble</c>.
///
/// Charter: lives in <c>dotnet-diagnostics-mcp</c> (not native-mcp) because process-memory
/// reads require CAP_SYS_PTRACE / SeDebugPrivilege / task_for_pid + UID alignment with the
/// target — privileges this sidecar already carries for EventPipe and ClrMD attach, and
/// which native-mcp deliberately does not. Internally backed by ClrMD's
/// <c>ClrMethod.HotColdInfo</c> + <c>IDataReader.Read</c>, which works uniformly on a live
/// process and on a process dump.
/// </summary>
public interface IJitMethodCapturer
{
    /// <summary>
    /// Attaches to <paramref name="processId"/> (suspending it for the duration of the
    /// read — typically sub-second for a single method), resolves the requested method,
    /// reads its JIT'd Hot/Cold regions out of the code-heap, and returns one
    /// <see cref="MethodBytesRef"/> per non-empty region written to disk.
    /// </summary>
    Task<CapturedMethodBytes> CaptureLiveAsync(
        int processId,
        MethodCaptureRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Offline counterpart: reads the JIT'd code-heap regions for the requested method out
    /// of a previously-captured WithHeap/Full dump file. Same ClrMD APIs, no live attach.
    /// </summary>
    Task<CapturedMethodBytes> CaptureFromDumpAsync(
        string dumpFilePath,
        MethodCaptureRequest request,
        CancellationToken cancellationToken = default);
}
