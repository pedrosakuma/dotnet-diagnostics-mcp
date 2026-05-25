namespace DotnetDiagnosticsMcp.Core.Collection;

/// <summary>
/// Envelope returned by <c>query_collection</c>. Carries the artifact kind, the selected view,
/// the original collection metadata (processId, started-at, duration) and the typed
/// <see cref="Payload"/> produced by the view dispatcher.
/// </summary>
/// <param name="Kind">One of <see cref="CollectionHandleKinds"/>. Distinguishes which view shape <see cref="Payload"/> uses.</param>
/// <param name="View">The view name that was rendered (e.g. <c>summary</c>, <c>byType</c>, <c>events</c>, <c>byProvider</c>).</param>
/// <param name="ProcessId">PID of the target process at the moment of collection. Survives target restarts (handles do not).</param>
/// <param name="StartedAt">UTC timestamp the original collector started its session.</param>
/// <param name="Duration">Wall-clock duration of the original collection window.</param>
/// <param name="Payload">The view-specific data. Serialized polymorphically through <see cref="System.Text.Json"/> reflection.</param>
public sealed record CollectionQueryResult(
    string Kind,
    string View,
    int ProcessId,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    object Payload);

// --- Counters views ---------------------------------------------------------------------------

/// <summary>Default counters view: every EventCounter plus every Meter time series captured in the window.</summary>
public sealed record CountersSummaryView(
    int Count,
    IReadOnlyList<Counters.CounterValue> Counters,
    int MeterCount,
    IReadOnlyList<Counters.MeterInstrumentValue> Meters,
    IReadOnlyList<string> Notes);

/// <summary>Counters grouped by EventSource provider name.</summary>
public sealed record CountersByProviderView(
    IReadOnlyList<CountersProviderGroup> Providers);

/// <summary>One provider entry in <see cref="CountersByProviderView"/>.</summary>
public sealed record CountersProviderGroup(string Provider, IReadOnlyList<Counters.CounterValue> Counters);

// --- Exception snapshot views -----------------------------------------------------------------

/// <summary>Aggregated by-type counts (always exact, no truncation).</summary>
public sealed record ExceptionByTypeView(
    int TotalExceptions,
    IReadOnlyList<Exceptions.ExceptionCount> ByType);

/// <summary>Individual exception events (truncated to <see cref="RecentCap"/>).</summary>
public sealed record ExceptionRecentView(
    int TotalExceptions,
    int RecentCap,
    int Returned,
    IReadOnlyList<Exceptions.ManagedExceptionEvent> Recent);

// --- GC events views --------------------------------------------------------------------------

/// <summary>Aggregated GC pause headline.</summary>
public sealed record GcSummaryView(
    int TotalCollections,
    TimeSpan TotalPauseTime,
    TimeSpan MaxPauseTime,
    IReadOnlyList<Gc.GenerationStats> Generations);

/// <summary>Raw GC events (capped by <c>topN</c>).</summary>
public sealed record GcEventsView(
    int TotalCollections,
    int Returned,
    IReadOnlyList<Gc.GcEvent> Events);

/// <summary>GC pause histogram — buckets by pause duration so the LLM can describe the tail.</summary>
public sealed record GcPauseHistogramView(
    int TotalCollections,
    TimeSpan MaxPauseTime,
    IReadOnlyList<GcPauseBucket> Buckets);

/// <summary>One bucket in <see cref="GcPauseHistogramView"/>.</summary>
/// <param name="Label">Human-readable label (e.g. <c>"&lt;1ms"</c>, <c>"1-10ms"</c>).</param>
/// <param name="UpperBoundMs">Inclusive upper bound of this bucket in milliseconds. <see cref="int.MaxValue"/> = no upper bound.</param>
/// <param name="Count">Number of GC events whose pause falls in this bucket.</param>
public sealed record GcPauseBucket(string Label, int UpperBoundMs, int Count);

// --- EventSource views ------------------------------------------------------------------------

/// <summary>Counts grouped by EventName.</summary>
/// <param name="Provider">EventSource provider name (echoed from the capture).</param>
/// <param name="TotalEvents">Total events observed during the collection window — may exceed
/// <paramref name="CapturedCount"/> because the original collector caps stored events at
/// <c>maxEvents</c>; only captured events contribute to the per-name counts below.</param>
/// <param name="CapturedCount">Events that were actually stored and thus grouped here.</param>
/// <param name="Truncated">True when the collector dropped tail events (<c>TotalEvents &gt; CapturedCount</c>).
/// When true, <paramref name="ByEventName"/> reflects only the captured prefix — re-run
/// <c>collect_event_source</c> with a larger <c>maxEvents</c> to get exact totals.</param>
/// <param name="ByEventName">Per-event-name aggregates over the captured subset only.</param>
public sealed record EventSourceByEventNameView(
    string Provider,
    int TotalEvents,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<EventSourceEventNameGroup> ByEventName);

/// <summary>One group in <see cref="EventSourceByEventNameView"/>.</summary>
public sealed record EventSourceEventNameGroup(string EventName, int Count);

/// <summary>Raw captured events (capped by <c>topN</c>).</summary>
public sealed record EventSourceEventsView(
    string Provider,
    int TotalEvents,
    int Returned,
    IReadOnlyList<EventSources.CapturedEvent> Events);

// --- Activity views ---------------------------------------------------------------------------

/// <summary>Headline view for captured activities; grouping aggregates reflect only the stored subset when truncated.</summary>
public sealed record ActivitiesSummaryView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CompletedActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivitySourceSummary> BySource,
    IReadOnlyList<Activities.ActivityOperationSummary> ByOperation);

/// <summary>Activities grouped by source name.</summary>
public sealed record ActivitiesBySourceView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivitySourceSummary> Sources);

/// <summary>Activities grouped by source+operation.</summary>
public sealed record ActivitiesByOperationView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int CapturedCount,
    bool Truncated,
    IReadOnlyList<Activities.ActivityOperationSummary> Operations);

/// <summary>Raw captured activities (capped by <c>topN</c>).</summary>
public sealed record ActivitiesListView(
    IReadOnlyList<string>? SourceFilters,
    int TotalActivities,
    int Returned,
    IReadOnlyList<Activities.CapturedActivity> Activities);
