using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

public class CollectionQueryDispatcherTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;

    [Fact]
    public void Counters_SummaryView_ReturnsAllCounters()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean, "%"),
            new("System.Runtime", "gc-heap-size", "Heap", 100, CounterKind.Mean, "MB"),
        },
        new List<MeterInstrumentValue>
        {
            new("MyMeter", "orders.total", null, "Counter", new Dictionary<string, string?>(), 7, 2, null),
        },
        ["note"]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, null, snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.View.Should().Be("summary");
        var payload = outcome.Result.Payload.Should().BeOfType<CountersSummaryView>().Subject;
        payload.Counters.Should().HaveCount(2);
        payload.MeterCount.Should().Be(1);
        payload.Meters.Should().ContainSingle();
        payload.Notes.Should().ContainSingle("note");
    }

    [Fact]
    public void Counters_ByProviderView_GroupsByProvider()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean),
            new("Microsoft-AspNetCore-Server-Kestrel", "current-connections", "Conns", 3, CounterKind.Mean),
        },
        Array.Empty<MeterInstrumentValue>(),
        Array.Empty<string>());

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "byProvider", snap, 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<CountersByProviderView>().Subject;
        payload.Providers.Should().HaveCount(2);
    }

    [Fact]
    public void Exceptions_SummaryView_TopsByTypeWithinTopN()
    {
        var snap = new ExceptionSnapshot(42, At, TimeSpan.FromSeconds(10), 30,
            new List<ExceptionCount>
            {
                new("System.FormatException", 20),
                new("System.InvalidOperationException", 8),
                new("System.NullReferenceException", 2),
            },
            new List<ManagedExceptionEvent>())
        { RecentCap = 100 };

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ExceptionSnapshot, "summary", snap, 2);

        var payload = outcome.Result!.Payload.Should().BeOfType<ExceptionByTypeView>().Subject;
        payload.ByType.Should().HaveCount(2);
        payload.TotalExceptions.Should().Be(30);
    }

    [Fact]
    public void Exceptions_RecentView_TruncatesAndReportsCap()
    {
        var recent = Enumerable.Range(0, 25)
            .Select(i => new ManagedExceptionEvent(At.AddSeconds(i), "T", "msg", "0x1", 1))
            .ToList();
        var snap = new ExceptionSnapshot(42, At, TimeSpan.FromSeconds(10), 25,
            new List<ExceptionCount> { new("T", 25) }, recent) { RecentCap = 100 };

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.ExceptionSnapshot, "recent", snap, 5);

        var payload = outcome.Result!.Payload.Should().BeOfType<ExceptionRecentView>().Subject;
        payload.Returned.Should().Be(5);
        payload.RecentCap.Should().Be(100);
    }

    [Fact]
    public void Gc_PauseHistogram_BucketsBoundariesCorrectly()
    {
        // One event per intended bucket: 0.5ms, 5ms, 50ms, 500ms, 1500ms.
        var events = new[] { 0.5, 5, 50, 500, 1500 }
            .Select(ms => new GcEvent(At, 0, "AllocSmall", "Background", TimeSpan.FromMilliseconds(ms)))
            .ToList();
        var g = new GcSummary(42, At, TimeSpan.FromSeconds(5), events.Count,
            TimeSpan.FromMilliseconds(events.Sum(e => e.PauseDuration.TotalMilliseconds)),
            TimeSpan.FromMilliseconds(1500),
            new List<GenerationStats> { new(0, 5) },
            events);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.GcEvents, "pauseHistogram", g, 50);

        var payload = outcome.Result!.Payload.Should().BeOfType<GcPauseHistogramView>().Subject;
        payload.Buckets.Select(b => b.Count).Should().Equal(1, 1, 1, 1, 1);
    }

    [Fact]
    public void EventSource_ByEventNameView_OrdersByCount()
    {
        var events = new List<CapturedEvent>
        {
            new(At, "P", "Start", "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
            new(At, "P", "Stop",  "Info", new Dictionary<string,string>()),
        };
        var cap = new EventSourceCapture(42, "P", At, TimeSpan.FromSeconds(5), events.Count, events);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.EventSource, "byEventName", cap, 10);

        var payload = outcome.Result!.Payload.Should().BeOfType<EventSourceByEventNameView>().Subject;
        payload.ByEventName[0].EventName.Should().Be("Stop");
        payload.ByEventName[0].Count.Should().Be(3);
        payload.CapturedCount.Should().Be(4);
        payload.Truncated.Should().BeFalse();
    }

    [Fact]
    public void EventSource_ByEventNameView_FlagsTruncationWhenTotalExceedsCaptured()
    {
        // Simulate a collector that observed 1000 events but only stored the first 200
        // because maxEvents=200. ByEventName must surface that mismatch so the LLM doesn't
        // present partial aggregates as if they represented the whole window.
        var captured = Enumerable.Range(0, 200)
            .Select(i => new CapturedEvent(At, "P", i < 50 ? "Start" : "Heartbeat", "Info",
                new Dictionary<string, string>()))
            .ToList();
        var cap = new EventSourceCapture(42, "P", At, TimeSpan.FromSeconds(5), TotalEvents: 1000, captured);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.EventSource, "byEventName", cap, 10);

        var payload = outcome.Result!.Payload.Should().BeOfType<EventSourceByEventNameView>().Subject;
        payload.TotalEvents.Should().Be(1000);
        payload.CapturedCount.Should().Be(200);
        payload.Truncated.Should().BeTrue("collector dropped tail events; the LLM must see that");
    }

    [Fact]
    public void Activities_SummaryView_ReportsTruncation_AndTopGroups()
    {
        var capture = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: new[] { "Demo.*" },
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalActivities: 5,
            CompletedActivities: 4,
            Activities:
            [
                new CapturedActivity("Demo.Service", "GET /a", "1", null, "trace-1", "span-1", null, At, At.AddMilliseconds(12), TimeSpan.FromMilliseconds(12), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "GET /a", "2", null, "trace-1", "span-2", null, At.AddMilliseconds(20), At.AddMilliseconds(40), TimeSpan.FromMilliseconds(20), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "GET /b", "3", "1", "trace-1", "span-3", "span-1", At.AddMilliseconds(25), At.AddMilliseconds(55), TimeSpan.FromMilliseconds(30), new Dictionary<string, string>()),
            ],
            BySource:
            [
                new ActivitySourceSummary("Demo.Service", 3, 3, 20, 30),
            ],
            ByOperation:
            [
                new ActivityOperationSummary("Demo.Service", "GET /a", 2, 2, 16, 20),
                new ActivityOperationSummary("Demo.Service", "GET /b", 1, 1, 30, 30),
            ]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Activities, "summary", capture, 1);

        var payload = outcome.Result!.Payload.Should().BeOfType<ActivitiesSummaryView>().Subject;
        payload.TotalActivities.Should().Be(5);
        payload.CapturedCount.Should().Be(3);
        payload.Truncated.Should().BeTrue();
        payload.BySource.Should().ContainSingle();
        payload.ByOperation.Should().ContainSingle();
        payload.ByOperation[0].OperationName.Should().Be("GET /a");
    }

    [Fact]
    public void Activities_ActivitiesView_TruncatesRawList()
    {
        var capture = new ActivityCapture(
            ProcessId: 42,
            SourceFilters: null,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalActivities: 2,
            CompletedActivities: 2,
            Activities:
            [
                new CapturedActivity("Demo.Service", "outer", "1", null, "trace-1", "span-1", null, At, At.AddMilliseconds(12), TimeSpan.FromMilliseconds(12), new Dictionary<string, string>()),
                new CapturedActivity("Demo.Service", "inner", "2", "1", "trace-1", "span-2", "span-1", At.AddMilliseconds(2), At.AddMilliseconds(6), TimeSpan.FromMilliseconds(4), new Dictionary<string, string>()),
            ],
            BySource:
            [
                new ActivitySourceSummary("Demo.Service", 2, 2, 8, 12),
            ],
            ByOperation:
            [
                new ActivityOperationSummary("Demo.Service", "outer", 1, 1, 12, 12),
                new ActivityOperationSummary("Demo.Service", "inner", 1, 1, 4, 4),
            ]);

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Activities, "activities", capture, 1);

        var payload = outcome.Result!.Payload.Should().BeOfType<ActivitiesListView>().Subject;
        payload.Returned.Should().Be(1);
        payload.Activities[0].OperationName.Should().Be("outer");
    }

    [Fact]
    public void UnknownKind_ReturnsUnknownKind()
    {
        var outcome = CollectionQueryDispatcher.Dispatch("not-a-real-kind", "summary", new object(), 50);
        outcome.UnknownKind.Should().Be("not-a-real-kind");
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void UnknownView_ReturnsAllowedViews()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "bogus", snap, 50);

        outcome.UnknownView.Should().Be("bogus");
        outcome.AllowedViews.Should().Contain(new[] { "summary", "byProvider" });
    }

    [Fact]
    public void InvalidTopN_ReturnsInvalidArgument()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "summary", snap, 0);

        outcome.InvalidArgument.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ViewNames_AreCaseInsensitive()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>(), Array.Empty<MeterInstrumentValue>(), Array.Empty<string>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "BYPROVIDER", snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Payload.Should().BeOfType<CountersByProviderView>();
    }
}
