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
    IReadOnlyList<Hotspot> TopHotspots);
