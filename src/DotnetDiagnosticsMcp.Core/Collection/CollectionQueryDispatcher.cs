using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;

namespace DotnetDiagnosticsMcp.Core.Collection;

/// <summary>
/// Renders a previously-collected artifact under a named view. Pure (no I/O, no DI) so the
/// <c>query_collection</c> tool can stay a thin reflection-over-the-store wrapper.
/// </summary>
/// <remarks>
/// Adding a new view = adding one branch to the per-kind switch. Adding a new collector kind =
/// new constant in <see cref="CollectionHandleKinds"/>, new render overload, and new entry in the
/// dispatcher's kind switch.
/// </remarks>
public static class CollectionQueryDispatcher
{
    /// <summary>
    /// Allowed view names per artifact kind, surfaced verbatim in error messages so the LLM can
    /// retry with a valid view without re-reading the tool description.
    /// </summary>
    public static IReadOnlyList<string> ViewsFor(string kind) => kind switch
    {
        CollectionHandleKinds.Counters => new[] { "summary", "byProvider" },
        CollectionHandleKinds.ExceptionSnapshot => new[] { "summary", "byType", "recent" },
        CollectionHandleKinds.GcEvents => new[] { "summary", "events", "pauseHistogram" },
        CollectionHandleKinds.EventSource => new[] { "summary", "byEventName", "events" },
        _ => Array.Empty<string>(),
    };

    /// <summary>Default view for a given kind when the caller doesn't pass one.</summary>
    public static string DefaultViewFor(string kind) => "summary";

    /// <summary>Dispatch outcome union — success carries a <see cref="CollectionQueryResult"/>; the four failure flavors are observable strings.</summary>
    public readonly record struct DispatchOutcome(
        CollectionQueryResult? Result,
        string? UnknownKind,
        string? UnknownView,
        string? InvalidArgument,
        IReadOnlyList<string>? AllowedViews);

    /// <summary>
    /// Renders <paramref name="artifact"/> under <paramref name="view"/>. <paramref name="kind"/>
    /// is the value the artifact was registered with — used to validate the artifact's runtime
    /// type matches what the dispatcher expects.
    /// </summary>
    public static DispatchOutcome Dispatch(string kind, string? view, object artifact, int topN)
    {
        if (topN < 1)
        {
            return new DispatchOutcome(null, null, null, "topN must be >= 1", null);
        }

        var effectiveView = string.IsNullOrWhiteSpace(view) ? DefaultViewFor(kind) : view!;
        var allowed = ViewsFor(kind);
        if (allowed.Count == 0)
        {
            return new DispatchOutcome(null, kind, null, null, null);
        }
        if (!allowed.Contains(effectiveView, StringComparer.OrdinalIgnoreCase))
        {
            return new DispatchOutcome(null, null, effectiveView, null, allowed);
        }

        return kind switch
        {
            CollectionHandleKinds.Counters when artifact is CounterSnapshot c
                => Ok(Render(c, effectiveView)),
            CollectionHandleKinds.ExceptionSnapshot when artifact is ExceptionSnapshot e
                => Ok(Render(e, effectiveView, topN)),
            CollectionHandleKinds.GcEvents when artifact is GcSummary g
                => Ok(Render(g, effectiveView, topN)),
            CollectionHandleKinds.EventSource when artifact is EventSourceCapture es
                => Ok(Render(es, effectiveView, topN)),
            _ => new DispatchOutcome(null, kind, null, null, null),
        };
    }

    private static DispatchOutcome Ok(CollectionQueryResult result) =>
        new(result, null, null, null, null);

    // --- Per-kind render -------------------------------------------------------------

    private static CollectionQueryResult Render(CounterSnapshot c, string view)
    {
        object payload = view.Equals("byProvider", StringComparison.OrdinalIgnoreCase)
            ? new CountersByProviderView(c.Counters
                .GroupBy(v => v.Provider)
                .Select(g => new CountersProviderGroup(g.Key, g.ToList()))
                .ToList())
            : new CountersSummaryView(c.Counters.Count, c.Counters);

        return new CollectionQueryResult(
            CollectionHandleKinds.Counters, view, c.ProcessId, c.StartedAt, c.Duration, payload);
    }

    private static CollectionQueryResult Render(ExceptionSnapshot e, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "bytype" => new ExceptionByTypeView(e.TotalExceptions, e.ByType),
            "recent" => RecentView(e, topN),
            _ /* summary */ => new ExceptionByTypeView(
                e.TotalExceptions,
                e.ByType.Take(topN).ToList()),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.ExceptionSnapshot, view, e.ProcessId, e.StartedAt, e.Duration, payload);
    }

    private static ExceptionRecentView RecentView(ExceptionSnapshot e, int topN)
    {
        var sliced = e.Recent.Take(topN).ToList();
        return new ExceptionRecentView(e.TotalExceptions, e.RecentCap, sliced.Count, sliced);
    }

    private static CollectionQueryResult Render(GcSummary g, string view, int topN)
    {
        object payload = view.ToLowerInvariant() switch
        {
            "events" => new GcEventsView(g.TotalCollections, Math.Min(topN, g.Events.Count), g.Events.Take(topN).ToList()),
            "pausehistogram" => BuildHistogram(g),
            _ /* summary */ => new GcSummaryView(g.TotalCollections, g.TotalPauseTime, g.MaxPauseTime, g.Generations),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.GcEvents, view, g.ProcessId, g.StartedAt, g.Duration, payload);
    }

    private static GcPauseHistogramView BuildHistogram(GcSummary g)
    {
        // Buckets aligned with the rules of thumb the playbook uses (<1ms negligible,
        // 1-10ms typical gen0, 10-100ms gen1/gen2, >100ms problematic, >1s catastrophic).
        var bounds = new (string Label, int UpperBoundMs)[]
        {
            ("<1ms", 1),
            ("1-10ms", 10),
            ("10-100ms", 100),
            ("100-1000ms", 1000),
            (">=1s", int.MaxValue),
        };

        var counts = new int[bounds.Length];
        foreach (var ev in g.Events)
        {
            var ms = ev.PauseDuration.TotalMilliseconds;
            for (var i = 0; i < bounds.Length; i++)
            {
                if (ms < bounds[i].UpperBoundMs)
                {
                    counts[i]++;
                    break;
                }
            }
        }

        var buckets = bounds.Select((b, i) => new GcPauseBucket(b.Label, b.UpperBoundMs, counts[i])).ToList();
        return new GcPauseHistogramView(g.TotalCollections, g.MaxPauseTime, buckets);
    }

    private static CollectionQueryResult Render(EventSourceCapture es, string view, int topN)
    {
        var capturedCount = es.Events.Count;
        var truncated = es.TotalEvents > capturedCount;

        object payload = view.ToLowerInvariant() switch
        {
            "byeventname" => new EventSourceByEventNameView(
                es.Provider,
                es.TotalEvents,
                capturedCount,
                truncated,
                es.Events.GroupBy(e => e.EventName)
                    .Select(g => new EventSourceEventNameGroup(g.Key, g.Count()))
                    .OrderByDescending(g => g.Count)
                    .ToList()),
            "events" => new EventSourceEventsView(
                es.Provider,
                es.TotalEvents,
                Math.Min(topN, es.Events.Count),
                es.Events.Take(topN).ToList()),
            _ /* summary */ => new EventSourceByEventNameView(
                es.Provider,
                es.TotalEvents,
                capturedCount,
                truncated,
                es.Events.GroupBy(e => e.EventName)
                    .Select(g => new EventSourceEventNameGroup(g.Key, g.Count()))
                    .OrderByDescending(g => g.Count)
                    .Take(topN)
                    .ToList()),
        };

        return new CollectionQueryResult(
            CollectionHandleKinds.EventSource, view, es.ProcessId, es.StartedAt, es.Duration, payload);
    }
}
