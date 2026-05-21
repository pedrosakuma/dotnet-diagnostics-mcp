using DotnetDiagnosticsMcp.Core.Capabilities;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Runtime/OS-aware backend used by <see cref="RoutingThreadSnapshotInspector"/>. Backends can be
/// ordered and chained so capability-specific fallbacks (for example no-ptrace replay) can be
/// registered without modifying the router.
/// </summary>
public interface IThreadSnapshotBackend
{
    string BackendId { get; }

    int Order { get; }

    string? Preconditions { get; }

    bool CanHandleLive(RuntimeFlavor runtime);

    bool CanHandleDump { get; }

    Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default);

    Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class ClrMdThreadSnapshotBackend : IThreadSnapshotBackend
{
    private readonly ClrMdThreadSnapshotInspector _inspector;

    public ClrMdThreadSnapshotBackend(ClrMdThreadSnapshotInspector inspector) => _inspector = inspector;

    public string BackendId => "clrmd-thread-walk";

    public int Order => 100;

    public string? Preconditions => "Requires ClrMD live attach permissions on the host (ptrace/debug attach).";

    public bool CanHandleLive(RuntimeFlavor runtime) => runtime == RuntimeFlavor.CoreClr;

    public bool CanHandleDump => true;

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inspector.InspectLiveAsync(processId, options, cancellationToken);

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inspector.InspectDumpAsync(dumpFilePath, options, cancellationToken);
}

public sealed class LinuxNativeThreadSnapshotBackend : IThreadSnapshotBackend
{
    private readonly LinuxNativeThreadSnapshotInspector _inspector;

    public LinuxNativeThreadSnapshotBackend(LinuxNativeThreadSnapshotInspector inspector) => _inspector = inspector;

    public string BackendId => "linux-native-stack";

    public int Order => 100;

    public string? Preconditions => "Requires eu-stack (elfutils), same UID as target, and ptrace attach capability.";

    public bool CanHandleLive(RuntimeFlavor runtime) => runtime == RuntimeFlavor.NativeAot && OperatingSystem.IsLinux();

    public bool CanHandleDump => false;

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inspector.InspectLiveAsync(processId, options, cancellationToken);

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Linux-native backend does not support dump snapshots.");
}

public sealed class EtwNativeThreadSnapshotBackend : IThreadSnapshotBackend
{
    private readonly EtwNativeThreadSnapshotInspector _inspector;

    public EtwNativeThreadSnapshotBackend(EtwNativeThreadSnapshotInspector inspector) => _inspector = inspector;

    public string BackendId => "etw-native-stack";

    public int Order => 110;

    public string? Preconditions => "Requires Windows with administrative elevation (or SeSystemProfilePrivilege).";

    public bool CanHandleLive(RuntimeFlavor runtime) => runtime == RuntimeFlavor.NativeAot && OperatingSystem.IsWindows();

    public bool CanHandleDump => false;

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inspector.InspectLiveAsync(processId, options, cancellationToken);

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("ETW-native backend does not support dump snapshots.");
}

public sealed class PerfReplayThreadSnapshotBackend : IThreadSnapshotBackend
{
    private readonly PerfReplayThreadSnapshotInspector _inspector;

    public PerfReplayThreadSnapshotBackend(PerfReplayThreadSnapshotInspector inspector) => _inspector = inspector;

    public string BackendId => PerfReplayThreadSnapshotInspector.BackendId;

    public int Order => 200;

    public string? Preconditions => "Requires Linux perf sched capture (perf in PATH plus CAP_PERFMON or perf_event_paranoid <= -1).";

    public bool CanHandleLive(RuntimeFlavor runtime) => runtime == RuntimeFlavor.NativeAot && OperatingSystem.IsLinux();

    public bool CanHandleDump => false;

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _inspector.InspectLiveAsync(processId, options, cancellationToken);

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Perf-replay backend does not support dump snapshots.");
}
