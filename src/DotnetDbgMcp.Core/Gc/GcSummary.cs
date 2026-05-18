namespace DotnetDbgMcp.Core.Gc;

/// <summary>Statistics for a single GC generation across the sample window.</summary>
public sealed record GenerationStats(int Generation, int Count);

/// <summary>A single observed GC event with its computed pause duration.</summary>
public sealed record GcEvent(
    DateTimeOffset Timestamp,
    int Generation,
    string Reason,
    string Type,
    TimeSpan PauseDuration);

/// <summary>Aggregated GC activity over a window.</summary>
public sealed record GcSummary(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalCollections,
    TimeSpan TotalPauseTime,
    TimeSpan MaxPauseTime,
    IReadOnlyList<GenerationStats> Generations,
    IReadOnlyList<GcEvent> Events);
