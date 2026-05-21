namespace DotnetDiagnosticsMcp.Core.Activities;

/// <summary>One Activity observed through DiagnosticSource EventPipe bridging.</summary>
public sealed record CapturedActivity(
    string SourceName,
    string OperationName,
    string Id,
    string? ParentId,
    string? TraceId,
    string? SpanId,
    string? ParentSpanId,
    DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt,
    TimeSpan? Duration,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>Aggregated activity counts and duration stats for a single ActivitySource.</summary>
public sealed record ActivitySourceSummary(
    string SourceName,
    int Count,
    int CompletedCount,
    double AverageDurationMs,
    double MaxDurationMs);

/// <summary>Aggregated activity counts and duration stats for a source/operation pair.</summary>
public sealed record ActivityOperationSummary(
    string SourceName,
    string OperationName,
    int Count,
    int CompletedCount,
    double AverageDurationMs,
    double MaxDurationMs);

/// <summary>
/// ActivitySource capture window collected through <c>Microsoft-Diagnostics-DiagnosticSource</c>.
/// When <see cref="TotalActivities"/> exceeds <see cref="Activities"/>' count the capture was truncated by
/// <c>maxActivities</c>; summaries reflect only the stored subset.
/// </summary>
public sealed record ActivityCapture(
    int ProcessId,
    IReadOnlyList<string>? SourceFilters,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    int TotalActivities,
    int CompletedActivities,
    IReadOnlyList<CapturedActivity> Activities,
    IReadOnlyList<ActivitySourceSummary> BySource,
    IReadOnlyList<ActivityOperationSummary> ByOperation);
