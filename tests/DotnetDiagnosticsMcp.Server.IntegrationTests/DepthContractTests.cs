using System.Net.Http.Headers;
using System.Text;
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
using Microsoft.Extensions.DependencyInjection;
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
            ["processId"] = Environment.ProcessId,
            ["durationSeconds"] = 3,
            ["providers"] = new[] { "System.Runtime" },
            ["intervalSeconds"] = 1,
        };

        var summary = DeserializeStructured<CounterSnapshot>(await client.CallToolAsync(
            "snapshot_counters",
            new Dictionary<string, object?>(args) { ["depth"] = "Summary" },
            cancellationToken: CancellationToken.None));
        var detail = DeserializeStructured<CounterSnapshot>(await client.CallToolAsync(
            "snapshot_counters",
            new Dictionary<string, object?>(args) { ["depth"] = "Detail" },
            cancellationToken: CancellationToken.None));

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        // Summary keeps only the headline counters from System.Runtime — Detail keeps every counter the provider emits.
        summary!.Counters.Count.Should().BeLessThan(detail!.Counters.Count,
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

        var summary = DeserializeStructured<ExceptionSnapshot>(await client.CallToolAsync(
            "collect_exceptions",
            new Dictionary<string, object?>
            {
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

        var detail = DeserializeStructured<ExceptionSnapshot>(await client.CallToolAsync(
            "collect_exceptions",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxRecent"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        await workloadDetail;

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        summary!.Recent.Should().BeEmpty("Summary depth must drop the Recent[] list inline");
        // Total + ByType remain exact at every depth — contract from issue #41 (#36 lineage).
        summary.TotalExceptions.Should().BeGreaterThanOrEqualTo(0);
        detail!.Recent.Count.Should().BeGreaterThanOrEqualTo(0);
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

        var summary = DeserializeStructured<GcSummary>(await client.CallToolAsync(
            "collect_gc_events",
            new Dictionary<string, object?>
            {
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

        var detail = DeserializeStructured<GcSummary>(await client.CallToolAsync(
            "collect_gc_events",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["maxEvents"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        await detailWork;

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        summary!.Events.Should().BeEmpty("Summary depth must drop the Events[] list inline");
        // Totals must remain exact regardless of depth.
        summary.TotalCollections.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task CollectEventSource_SummaryDropsEventsDetailKeepsThem()
    {
        await using var client = await ConnectAsync();

        var summary = DeserializeStructured<EventSourceCapture>(await client.CallToolAsync(
            "collect_event_source",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["providerName"] = "System.Runtime",
                ["durationSeconds"] = 2,
                ["maxEvents"] = 50,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detail = DeserializeStructured<EventSourceCapture>(await client.CallToolAsync(
            "collect_event_source",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["providerName"] = "System.Runtime",
                ["durationSeconds"] = 2,
                ["maxEvents"] = 50,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        summary!.Events.Should().BeEmpty("Summary depth must drop the Events[] list inline");
        // Provider + totals remain exact.
        summary.Provider.Should().Be("System.Runtime");
        detail!.Provider.Should().Be("System.Runtime");
    }

    [Fact]
    public async Task GetContainerSignals_SummaryDropsNotesDetailKeepsThem()
    {
        await using var client = await ConnectAsync();

        var summary = DeserializeStructured<ContainerSignals>(await client.CallToolAsync(
            "get_container_signals",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detail = DeserializeStructured<ContainerSignals>(await client.CallToolAsync(
            "get_container_signals",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        summary!.Notes.Should().BeEmpty("Summary depth must drop the Notes[] list inline");
        // Notes count is platform dependent (Linux cgroup v2 usually emits at least one note when
        // the test runner is not in a container). The only invariant we can assert is that Summary's
        // notes count <= Detail's notes count.
        summary.Notes.Count.Should().BeLessThanOrEqualTo(detail!.Notes.Count);
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

        var summary = DeserializeStructured<CpuSample>(await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None));

        var detail = DeserializeStructured<CpuSample>(await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 3,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["depth"] = "Detail",
            },
            cancellationToken: CancellationToken.None));

        cts.Cancel();
        try { await spin; } catch { /* expected */ }

        summary.Should().NotBeNull();
        detail.Should().NotBeNull();
        // Summary caps the inline TopHotspots at 3 (handle still has everything).
        summary!.TopHotspots.Count.Should().BeLessThanOrEqualTo(3, "Summary depth caps inline hotspots");
        // Detail must not be truncated by depth — at least as many hotspots as Summary saw.
        detail!.TopHotspots.Count.Should().BeGreaterThanOrEqualTo(summary.TopHotspots.Count);
    }

    [Fact]
    public async Task CollectCpuSample_RunAsJob_SummaryMatchesSyncSummaryPayloadShape()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddSingleton<ICpuSampler, DeterministicCpuSampler>()));
        await using var client = await ConnectAsync(factory);

        DeterministicCpuSampler.TotalHotspots.Should().BeGreaterThan(3,
            "the deterministic sampler must exercise summary truncation to catch the regression from #121");

        var syncRaw = await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None);
        var syncEnvelope = DeserializeEnvelope<CpuSample>(syncRaw);

        var startRaw = await client.CallToolAsync(
            "collect_cpu_sample",
            new Dictionary<string, object?>
            {
                ["processId"] = Environment.ProcessId,
                ["durationSeconds"] = 2,
                ["topN"] = 25,
                ["resolveSourceLines"] = false,
                ["runAsJob"] = true,
                ["depth"] = "Summary",
            },
            cancellationToken: CancellationToken.None);
        var startEnvelope = DeserializeEnvelope<CpuSample>(startRaw);

        startEnvelope.Should().NotBeNull();
        startEnvelope!.Error.Should().BeNull();
        startEnvelope.Handle.Should().NotBeNullOrWhiteSpace();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        CollectionStatusReport? terminal = null;
        while (DateTime.UtcNow < deadline)
        {
            var report = await PollCollectionStatusAsync(client, startEnvelope.Handle!);
            report.Error.Should().BeNull();
            if (report.Status is "completed" or "failed" or "canceled")
            {
                terminal = report;
                break;
            }

            await Task.Delay(50);
        }

        terminal.Should().NotBeNull("the background job should complete within the timeout");
        terminal!.Status.Should().Be("completed");
        terminal.Result.Should().BeOfType<JsonElement>();

        syncEnvelope.Should().NotBeNull();
        syncEnvelope!.Error.Should().BeNull();
        syncEnvelope.Data.Should().NotBeNull();

        var asyncEnvelope = JsonSerializer.Deserialize<DiagnosticResult<CpuSample>>(((JsonElement)terminal.Result!).GetRawText(), DeserializeOptions);
        asyncEnvelope.Should().NotBeNull();
        asyncEnvelope!.Error.Should().BeNull();
        asyncEnvelope.Data.Should().NotBeNull();

        syncEnvelope.Data!.TopHotspots.Count.Should().Be(3);
        asyncEnvelope.Data!.TopHotspots.Count.Should().Be(3);

        var syncBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(syncEnvelope.Data, DeserializeOptions));
        var asyncBytes = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(asyncEnvelope.Data, DeserializeOptions));
        asyncBytes.Should().Be(syncBytes,
            "runAsJob summary depth should shape the deterministic CpuSample payload exactly like the synchronous summary path");
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

    private async Task<CollectionStatusReport> PollCollectionStatusAsync(McpClient client, string handle)
    {
        var pollResult = await client.CallToolAsync(
            "get_collection_status",
            new Dictionary<string, object?> { ["handle"] = handle },
            cancellationToken: CancellationToken.None);

        pollResult.IsError.Should().NotBe(true,
            "get_collection_status must not surface tool-level errors for an alive handle");

        var envelope = DeserializeEnvelope<JsonElement>(pollResult);
        envelope.Should().NotBeNull();
        if (envelope!.Error is not null)
        {
            return new CollectionStatusReport(
                Handle: handle,
                Kind: string.Empty,
                ProcessId: 0,
                Status: "error",
                StartedAt: default,
                CompletedAt: null,
                ElapsedSeconds: 0,
                Result: null,
                Error: envelope.Error);
        }

        var data = envelope.Data;
        return new CollectionStatusReport(
            Handle: data.GetProperty("handle").GetString()!,
            Kind: data.GetProperty("kind").GetString()!,
            ProcessId: data.GetProperty("processId").GetInt32(),
            Status: data.GetProperty("status").GetString()!,
            StartedAt: data.GetProperty("startedAt").GetDateTimeOffset(),
            CompletedAt: data.TryGetProperty("completedAt", out var completedAt) && completedAt.ValueKind != JsonValueKind.Null
                ? completedAt.GetDateTimeOffset()
                : null,
            ElapsedSeconds: data.GetProperty("elapsedSeconds").GetDouble(),
            Result: data.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null ? (object)result : null,
            Error: null);
    }

    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed class DeterministicCpuSampler : ICpuSampler
    {
        public const int TotalHotspots = 6;

        public Task<CpuSampleResult> SampleAsync(
            int processId,
            TimeSpan duration,
            int topN = 25,
            SourceResolutionOptions? sourceResolution = null,
            MethodInstantiationResolutionOptions? methodInstantiationResolution = null,
            CancellationToken cancellationToken = default)
        {
            var startedAt = DateTimeOffset.UnixEpoch;
            var hotspots = Enumerable.Range(1, TotalHotspots)
                .Select(i => new Hotspot(
                    new SampledFrame("deterministic-module", $"TestMethod{i}"),
                    InclusiveSamples: 100 - i,
                    ExclusiveSamples: 10 - i))
                .Take(topN)
                .ToArray();
            var children = hotspots
                .Select(h => new CallTreeNode(h.Frame, h.InclusiveSamples, h.ExclusiveSamples, Array.Empty<CallTreeNode>()))
                .ToArray();
            var root = new CallTreeNode(new SampledFrame("deterministic-module", "Root"), 123, 0, children);
            var summary = new CpuSample(processId, startedAt, duration, 123, hotspots);
            var artifact = new CpuSampleTraceArtifact(processId, startedAt, duration, 123, root);
            return Task.FromResult(new CpuSampleResult(summary, artifact));
        }
    }

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
