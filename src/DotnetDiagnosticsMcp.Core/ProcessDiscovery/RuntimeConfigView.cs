namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Best-effort runtime configuration snapshot for a managed process. Sections that require a
/// live ClrMD attach degrade to notes instead of failing the whole view.
/// </summary>
public sealed record RuntimeConfigView(
    int ProcessId,
    GcConfig? Gc,
    ThreadPoolConfig? ThreadPool,
    TieredCompilationConfig? TieredCompilation,
    IReadOnlyList<EnvVarEntry> EnvVars,
    IReadOnlyList<AppContextSwitchEntry> AppContextSwitches,
    IReadOnlyList<string> Notes);

public sealed record GcConfig(
    bool IsServerGc,
    bool? IsConcurrent,
    bool? IsBackground,
    int HeapCount,
    string? LargeObjectHeapCompactionMode);

public sealed record ThreadPoolConfig(
    int? MinWorkerThreads,
    int? MaxWorkerThreads,
    int? MinIocpThreads,
    int? MaxIocpThreads,
    bool? HillClimbingEnabled);

public sealed record TieredCompilationConfig(
    bool? Enabled,
    bool? QuickJitEnabled,
    bool? DynamicPgoEnabled);

public sealed record EnvVarEntry(
    string Name,
    string Value);

public sealed record AppContextSwitchEntry(
    string Name,
    string? Value);
