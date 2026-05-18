namespace DotnetDbgMcp.Core.Exceptions;

/// <summary>A single managed exception observed via EventPipe.</summary>
public sealed record ManagedExceptionEvent(
    DateTimeOffset Timestamp,
    string ExceptionType,
    string ExceptionMessage,
    string ExceptionHResult,
    int ThreadId);

/// <summary>Aggregated count of a given exception type in the sample window.</summary>
public sealed record ExceptionCount(string ExceptionType, int Count);

/// <summary>Result of an exception collection window.</summary>
public sealed record ExceptionSnapshot(
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalExceptions,
    IReadOnlyList<ExceptionCount> ByType,
    IReadOnlyList<ManagedExceptionEvent> Recent);
