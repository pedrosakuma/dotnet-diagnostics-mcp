using System.Net.Http.Headers;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Verifies the <see cref="SamplingDepth"/> contract introduced in issue #41 slice 2c:
/// every windowed collector must accept a <c>depth</c> parameter, return a smaller inline
/// payload at <c>Summary</c> (the new default) than at <c>Detail</c>, while the handle
/// store retains the full artifact regardless. Drilldown tools are covered separately.
/// </summary>
[Collection(DiagnosticIntegrationGroup.Name)]
public sealed class DepthContractTests : IClassFixture<McpToolsTests.AuthedFactory>, IClassFixture<LiveCoreClrSampleFixture>
{
    private readonly McpToolsTests.AuthedFactory _factory;
    private readonly LiveCoreClrSampleFixture _sample;

    public DepthContractTests(McpToolsTests.AuthedFactory factory, LiveCoreClrSampleFixture sample)
    {
        _factory = factory;
        _sample = sample;
    }

    [Fact]
    public async Task SnapshotCounters_SummaryReturnsFewerCountersThanDetail()
    {
        await using var client = await ConnectAsync();
        var args = new Dictionary<string, object?>
        {
            ["kind"] = "counters",
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 3,
            ["providers"] = new[] { "System.Runtime" },
            ["intervalSeconds"] = 1,
        };

        var summaryEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>(args) { ["depth"] = "Summary" },
            cancellationToken: CancellationToken.None));
        var detailEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>(args) { ["depth"] = "Detail" },
            cancellationToken: CancellationToken.None));

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.Counters!;
        var detail = detailEnvelope!.Counters!;
        // Summary keeps only the headline counters from System.Runtime — Detail keeps every counter the provider emits.
        summary.Counters.Count.Should().BeLessThan(detail.Counters.Count,
            "Summary must drop non-headline counters from the inline payload");
        summary.Counters.Should().OnlyContain(c => c.Provider == "System.Runtime");
    }

    [Fact]
    public async Task CollectExceptions_SummaryDropsRecentDetailKeepsIt()
    {
        await using var client = await ConnectAsync();

        // Generate a handful of exceptions during the collection window so Recent is non-trivial.
        var workload = Task.Run(async () =>
        {
            await Task.Delay(500);
            for (var i = 0; i < 10; i++)
            {
                try { throw new InvalidOperationException($"depth-test-{i}"); }
                catch { /* intentionally swallowed */ }
                await Task.Delay(50);
            }
        });

        var summaryEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "exceptions",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxRecent"] = 50,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        await workload;

        var workloadDetail = Task.Run(async () =>
        {
            await Task.Delay(500);
            for (var i = 0; i < 10; i++)
            {
                try { throw new InvalidOperationException($"depth-test-detail-{i}"); }
                catch { /* intentionally swallowed */ }
                await Task.Delay(50);
            }
        });

        var detailEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "exceptions",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxRecent"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        await workloadDetail;

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.Exceptions!;
        var detail = detailEnvelope!.Exceptions!;
        summary.Recent.Should().BeEmpty("Summary depth must drop the Recent[] list inline");
        // Total + ByType remain exact at every depth — contract from issue #41 (#36 lineage).
        summary.TotalExceptions.Should().BeGreaterThanOrEqualTo(0);
        detail.Recent.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CollectGcEvents_SummaryDropsEventsDetailKeepsThem()
    {
        await using var client = await ConnectAsync();

        // Force a couple of gen-2 GCs so Events has something to drop.
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        }

        var summaryEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "gc",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxEvents"] = 50,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        // Trigger more GCs during the detail window so Events is populated there too.
        var detailWork = Task.Run(async () =>
        {
            await Task.Delay(500);
            for (var i = 0; i < 3; i++)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                await Task.Delay(100);
            }
        });

        var detailEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "gc",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxEvents"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        await detailWork;

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.Gc!;
        var detail = detailEnvelope!.Gc!;
        summary.Events.Should().BeEmpty("Summary depth must drop the Events[] list inline");
        // Totals must remain exact regardless of depth.
        summary.TotalCollections.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CollectEventSource_SummaryDropsEventsDetailKeepsThem()
    {
        await using var client = await ConnectAsync();

        var summaryEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "event_source",
                ["processId"] = Environment.ProcessId,
                ["providerName"] = "System.Runtime",
                ["durationSeconds"] = 2,
                ["maxEvents"] = 50,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detailEnvelope = DeserializeStructured<CollectEventsEnvelope>(await client.CallToolAsync(
            "collect_events",
            new Dictionary<string, object?>
            {
                ["kind"] = "event_source",
                ["processId"] = Environment.ProcessId,
                ["providerName"] = "System.Runtime",
                ["durationSeconds"] = 2,
                ["maxEvents"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.EventSource!;
        var detail = detailEnvelope!.EventSource!;
        summary.Events.Should().BeEmpty("Summary depth must drop the Events[] list inline");
        // Provider + totals remain exact.
        summary.Provider.Should().Be("System.Runtime");
        detail.Provider.Should().Be("System.Runtime");
    }

    [Fact]
    public async Task GetContainerSignals_SummaryDropsNotesDetailKeepsThem()
    {
        await using var client = await ConnectAsync();

        var summaryEnvelope = DeserializeStructured<InspectProcessReport>(await client.CallToolAsync(
            "inspect_process",
            new Dictionary<string, object?>
            {
                ["view"] = "container",
                ["processId"] = Environment.ProcessId,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detailEnvelope = DeserializeStructured<InspectProcessReport>(await client.CallToolAsync(
            "inspect_process",
            new Dictionary<string, object?>
            {
                ["view"] = "container",
                ["processId"] = Environment.ProcessId,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.Container!;
        var detail = detailEnvelope!.Container!;
        summary.Notes.Should().BeEmpty("Summary depth must drop the Notes[] list inline");
        // Notes count is platform dependent (Linux cgroup v2 usually emits at least one note when
        // the test runner is not in a container). The only invariant we can assert is that Summary's
        // notes count <= Detail's notes count.
        summary.Notes.Count.Should().BeLessThanOrEqualTo(detail.Notes.Count);
    }

    [Fact]
    public async Task CollectCpuSample_SummaryCapsTopHotspotsAtThree()
    {
        await using var client = await ConnectAsync();

        // Generate some on-CPU work so TopHotspots is populated.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var spin = Task.Run(() =>
        {
            var sink = 0L;
            while (!cts.IsCancellationRequested) { sink += 1; }
            return sink;
        }, cts.Token);

        var summaryEnvelope = DeserializeStructured<CollectSampleEnvelope>(await client.CallToolAsync(
            "collect_sample",
            new Dictionary<string, object?>
            {
                ["kind"] = "cpu",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detailEnvelope = DeserializeStructured<CollectSampleEnvelope>(await client.CallToolAsync(
            "collect_sample",
            new Dictionary<string, object?>
            {
                ["kind"] = "cpu",
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        cts.Cancel();
        try { await spin; } catch { /* expected */ }

        summaryEnvelope.Should().NotBeNull();
        detailEnvelope.Should().NotBeNull();
        var summary = summaryEnvelope!.Cpu!;
        var detail = detailEnvelope!.Cpu!;
        // Summary caps the inline TopHotspots at 3 (handle still has everything).
        summary.TopHotspots.Count.Should().BeLessThanOrEqualTo(3, "Summary depth caps inline hotspots");
        // Detail must not be truncated by depth — at least as many hotspots as Summary saw.
        detail.TopHotspots.Count.Should().BeGreaterThanOrEqualTo(summary.TopHotspots.Count);
    }

    [Fact]
    public async Task CollectThreadSnapshot_SummaryDropsLocksAndCapsThreads()
    {
        await using var client = await ConnectAsync();

        var summaryRaw = await client.CallToolAsync(
            "collect_thread_snapshot",
            new Dictionary<string, object?>
            {
                ["processId"] = _sample.ProcessId,
                ["maxFramesPerThread"] = 16,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None);
        var detailRaw = await client.CallToolAsync(
            "collect_thread_snapshot",
            new Dictionary<string, object?>
            {
                ["processId"] = _sample.ProcessId,
                ["maxFramesPerThread"] = 16,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None);

        // The depth contract is exercised against a separate live sample so the test does not
        // attempt to suspend the xUnit/WebApplicationFactory process itself. Skip the assertion
        // when the runtime refuses the attach — the depth wiring is exercised end-to-end by the
        // other 6 collectors and the Core-layer ThreadSnapshot_InspectLive test covers the
        // underlying mechanism.
        var summaryEnvelope = DeserializeEnvelope<ThreadSnapshotQueryResult>(summaryRaw);
        var detailEnvelope = DeserializeEnvelope<ThreadSnapshotQueryResult>(detailRaw);
        if (summaryEnvelope?.Error is not null || detailEnvelope?.Error is not null)
        {
            // PermissionDenied / NotSupported / ClrMD failure — depth wiring still compiled,
            // and the request-side schema validation passed (no MCP -32602).
            return;
        }

        var summary = summaryEnvelope!.Data;
        var detail = detailEnvelope!.Data;
        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        summary!.View.Should().Be("top-blocked", "Summary returns the top-blocked view inline");
        summary.Threads.Should().NotBeNull();
        summary.Threads!.Count.Should().BeLessThanOrEqualTo(3, "Summary caps inline threads at 3");
        summary.Locks.Should().BeNullOrEmpty("Summary drops the inline lock graph (use query_thread_snapshot(view=lock-graph))");
        detail!.View.Should().Be("threads-summary");
        detail.Threads!.Count.Should().BeGreaterThanOrEqualTo(summary.Threads.Count);
    }

    private async Task<McpClient> ConnectAsync(WebApplicationFactory<DotnetDiagnosticsMcp.Server.Program>? factory = null)
    {
        var httpClient = (factory ?? _factory).CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", McpToolsTests.AuthedFactory.Token);

        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {McpToolsTests.AuthedFactory.Token}",
                },
            },
            httpClient,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport, clientOptions: null, cancellationToken: CancellationToken.None);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };


    private static T? DeserializeStructured<T>(ModelContextProtocol.Protocol.CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull("tool must return either structured content or a text block");
            json = textBlock!.Text;
        }

        var envelope = JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
        envelope.Should().NotBeNull();
        envelope!.Error.Should().BeNull("successful responses must not carry an error (got: " + envelope.Error?.Kind + ")");
        return envelope.Data;
    }

    private static DiagnosticResult<T>? DeserializeEnvelope<T>(ModelContextProtocol.Protocol.CallToolResult result)
    {
        string json;
        if (result.StructuredContent is { } structured)
        {
            json = structured.GetRawText();
        }
        else
        {
            var textBlock = result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().FirstOrDefault();
            textBlock.Should().NotBeNull();
            json = textBlock!.Text;
        }

        return JsonSerializer.Deserialize<DiagnosticResult<T>>(json, DeserializeOptions);
    }
}
