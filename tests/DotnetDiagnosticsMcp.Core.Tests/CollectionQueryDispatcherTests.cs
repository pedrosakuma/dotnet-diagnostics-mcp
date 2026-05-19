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
        });

        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, null, snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.View.Should().Be("summary");
        var payload = outcome.Result.Payload.Should().BeOfType<CountersSummaryView>().Subject;
        payload.Counters.Should().HaveCount(2);
    }

    [Fact]
    public void Counters_ByProviderView_GroupsByProvider()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean),
            new("Microsoft-AspNetCore-Server-Kestrel", "current-connections", "Conns", 3, CounterKind.Mean),
        });

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
    public void UnknownKind_ReturnsUnknownKind()
    {
        var outcome = CollectionQueryDispatcher.Dispatch("not-a-real-kind", "summary", new object(), 50);
        outcome.UnknownKind.Should().Be("not-a-real-kind");
        outcome.Result.Should().BeNull();
    }

    [Fact]
    public void UnknownView_ReturnsAllowedViews()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "bogus", snap, 50);

        outcome.UnknownView.Should().Be("bogus");
        outcome.AllowedViews.Should().Contain(new[] { "summary", "byProvider" });
    }

    [Fact]
    public void InvalidTopN_ReturnsInvalidArgument()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "summary", snap, 0);

        outcome.InvalidArgument.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ViewNames_AreCaseInsensitive()
    {
        var snap = new CounterSnapshot(42, At, TimeSpan.FromSeconds(5), Array.Empty<CounterValue>());
        var outcome = CollectionQueryDispatcher.Dispatch(CollectionHandleKinds.Counters, "BYPROVIDER", snap, 50);

        outcome.Result.Should().NotBeNull();
        outcome.Result!.Payload.Should().BeOfType<CountersByProviderView>();
    }
}
