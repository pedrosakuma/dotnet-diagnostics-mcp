namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Runtime flavor detected for a target process.
/// </summary>
public enum RuntimeFlavor
{
    Unknown,
    CoreClr,
    NativeAot,
}

/// <summary>
/// Matrix describing what kinds of diagnostic data the MCP server can collect from a given process.
/// </summary>
public sealed record DiagnosticCapabilities(
    int ProcessId,
    RuntimeFlavor Runtime,
    string RuntimeVersion,
    bool CanReadEventCounters,
    bool CanSampleCpu,
    bool CanCollectGcDump,
    bool CanCollectExceptions,
    bool CanCollectHttpActivity,
    bool CanCollectCustomEventSource,
    bool CanCollectProcessDump,
    string Notes)
{
    /// <summary>True when the target process is running inside a container envelope (its
    /// unified-hierarchy cgroup path is not <c>/</c>). Init-only so legacy call sites continue
    /// to compile.</summary>
    public bool InContainer { get; init; }

    /// <summary>True when the host exposes cgroup v2 (and therefore <c>get_container_signals</c>
    /// can be called against this process). False on cgroup v1 hosts and Windows.</summary>
    public bool CgroupV2 { get; init; }

    /// <summary>True when the diagnostics host can resolve a working <c>perf</c> binary from
    /// PATH / linux-tools candidates. This answers the "is perf installed?" part of the
    /// kernel capability matrix separately from the privilege check.</summary>
    public bool PerfInstalled { get; init; }

    /// <summary>True when the Linux sidecar currently holds <c>CAP_PERFMON</c>. False on
    /// non-Linux hosts and when the capability is absent. Off-CPU sampling may still be
    /// possible without it when <c>perf_event_paranoid &lt;= -1</c> (or on Windows via ETW).</summary>
    public bool HasCapPerfmon { get; init; }

    /// <summary>Value of <c>/proc/sys/kernel/perf_event_paranoid</c> when readable on Linux;
    /// null elsewhere. Helpful to explain why perf-based off-CPU capture is blocked even when
    /// <c>perf</c> is installed.</summary>
    public int? PerfEventParanoid { get; init; }

    /// <summary>True when reading <c>cpu.stat</c> / <c>cpu.max</c> is expected to succeed AND a
    /// quota is configured — i.e. CPU throttling is observable. False when the cgroup has no
    /// CPU quota (no quota → no throttling possible) or on non-Linux hosts.</summary>
    public bool CanSeeThrottle { get; init; }

    /// <summary>True when Pressure Stall Information files were readable for the target cgroup
    /// during capability detection. False on old kernels, cgroup v1 hosts, Windows, and any
    /// environment where <c>*.pressure</c> files are absent/unreadable.</summary>
    public bool PsiAvailable { get; init; }

    /// <summary>
    /// True when <c>collect_off_cpu_sample</c> is expected to succeed against this sidecar
    /// before the LLM commits to the (system-wide, privileged) capture. On Linux it requires
    /// <c>perf</c> in <c>PATH</c> plus <c>CAP_PERFMON</c> / <c>CAP_SYS_ADMIN</c> /
    /// <c>perf_event_paranoid &lt;= -1</c>;
    /// on Windows it requires the diagnostics process to be elevated (or hold
    /// <c>SeSystemProfilePrivilege</c>) so the NT Kernel Logger ContextSwitch provider can be
    /// enabled. False on macOS / other and whenever the sidecar lacks the prerequisite.
    /// Mirrors <see cref="CanSampleCpu"/> in spirit but is a property of the sidecar host, not
    /// of the target runtime.
    /// </summary>
    public bool CanSampleOffCpu { get; init; }

    /// <summary>True when the Linux sidecar currently holds <c>CAP_SYS_PTRACE</c>. False on
    /// non-Linux hosts and when the capability is absent. Exposed separately from
    /// <see cref="CanAttachClrMD"/> because <c>ptrace_scope=0</c> also enables attach without
    /// granting the capability.</summary>
    public bool HasCapSysPtrace { get; init; }

    /// <summary>Value of <c>/proc/sys/kernel/yama/ptrace_scope</c> when readable on Linux;
    /// null on non-Linux hosts or when Yama is not enabled.</summary>
    public int? PtraceScope { get; init; }

    /// <summary>
    /// True when the four ClrMD-backed live-attach tools (<c>collect_thread_snapshot</c>,
    /// <c>inspect_live_heap</c>, <c>inspect_dump</c> against a live PID,
    /// <c>collect_process_dump</c>) — plus <c>collect_cpu_sample(resolveMethodInstantiations=true)</c>
    /// when the opt-in closed-generic enrichment is requested — are expected to succeed on this sidecar host
    /// without surfacing a <c>PermissionDenied</c> error. On Linux this requires
    /// either <c>CAP_SYS_PTRACE</c> on the sidecar or <c>kernel.yama.ptrace_scope=0</c>
    /// on the host; on Windows ClrMD attaches via <c>DebugActiveProcess</c> and is
    /// generally allowed; on macOS live attach is not supported.
    /// Like <see cref="CanSampleOffCpu"/> this is a property of the sidecar host,
    /// not of the target runtime.
    /// </summary>
    public bool CanAttachClrMD { get; init; }

    /// <summary>Short human-readable reason matching <see cref="CanAttachClrMD"/> —
    /// surfaced into <see cref="Notes"/> and into the structured error envelope
    /// when a ClrMD attach actually fails. Null only on legacy code paths that
    /// haven't been updated to populate the probe.</summary>
    public string? AttachClrMdReason { get; init; }

    /// <summary>
    /// True when <c>collect_thread_snapshot</c> is expected to succeed for this target/runtime on
    /// the current sidecar host. This may be provided by different backends depending on runtime
    /// and OS (for example ClrMD vs linux-native-stack).
    /// </summary>
    public bool CanCollectThreadSnapshot { get; init; }

    /// <summary>
    /// Identifier of the backend expected to serve <c>collect_thread_snapshot</c> when
    /// <see cref="CanCollectThreadSnapshot"/> is true (for example
    /// <c>clrmd-thread-walk</c> or <c>linux-native-stack</c>).
    /// </summary>
    public string? ThreadSnapshotSource { get; init; }

    /// <summary>
    /// Human-readable prerequisites for thread snapshot collection on this host (tooling,
    /// privileges, OS gates). Useful when <see cref="CanCollectThreadSnapshot"/> is false.
    /// </summary>
    public string? ThreadSnapshotPreconditions { get; init; }

    /// <summary>True when Windows kernel ContextSwitch tracing is available to the diagnostics
    /// host (administrative elevation / <c>SeSystemProfilePrivilege</c>). Mirrors the ETW half
    /// of the kernel capability matrix; false on non-Windows hosts.</summary>
    public bool EtwKernelOk { get; init; }
}
