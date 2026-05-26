namespace DotnetDiagnosticsMcp.Core.Contention;

public sealed record ContentionSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalEvents,
    int DistinctMonitors,
    TimeSpan TotalContentionDuration,
    TimeSpan P50ContentionDuration,
    TimeSpan P95ContentionDuration,
    TimeSpan MaxContentionDuration,
    IReadOnlyList<ContentionEventSample> Events,
    IReadOnlyList<string> Notes);

public sealed record ContentionEventSample(
    DateTimeOffset StartedAt,
    DateTimeOffset StoppedAt,
    TimeSpan Duration,
    int ContendingThreadId,
    int? OwnerManagedThreadId,
    ulong LockId,
    ulong AssociatedObjectId,
    string CallSiteMethod,
    string CallSiteModule);
