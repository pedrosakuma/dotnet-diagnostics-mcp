namespace DotnetDiagnosticsMcp.Core.CpuSampling;

/// <summary>A single resolved frame within a CPU sample stack.</summary>
public sealed record SampledFrame(string Module, string Method);

/// <summary>A hotspot is a frame ranked by how often it appeared in CPU samples.</summary>
public sealed record Hotspot(
    SampledFrame Frame,
    long InclusiveSamples,
    long ExclusiveSamples,
    DotnetDiagnosticsMcp.Core.Memory.MethodIdentity? Identity = null);

/// <summary>Aggregated CPU sample over a window.</summary>
public sealed record CpuSample(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    long TotalSamples,
    IReadOnlyList<Hotspot> TopHotspots)
{
    /// <summary>
    /// Aggregate symbol-resolution quality of <see cref="TopHotspots"/>. Always populated for
    /// NativeAOT samples by <c>PerfNativeAotCpuSampler</c>; <c>null</c> for CoreCLR samples
    /// since the EventPipe path resolves managed methods via TraceEvent and the concept does
    /// not apply uniformly. See #29 / #35 — surfacing this avoids forcing the consumer to
    /// drill into the trace artifact just to know whether demangling succeeded.
    /// </summary>
    public NativeAotSymbolDemangler.SymbolSource? SymbolSource { get; init; }
}
