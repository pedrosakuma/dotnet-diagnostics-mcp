using System.Diagnostics;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Artifacts;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.JitCapture;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// End-to-end tests that spawn the <c>CoreClrSample</c> webapi and exercise the diagnostic
/// pipeline against it. The sample project is built+run via <c>dotnet run</c> so the test only
/// requires the .NET SDK to be on PATH (CI satisfies this).
/// </summary>
[Collection("LiveProcess")]
public class LiveCoreClrProcessTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private readonly TaskCompletionSource<string> _listeningUrlTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int Pid => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");

    public async Task InitializeAsync()
    {
        var sampleDll = LocateSampleDll();
        if (sampleDll is null)
        {
            return;
        }

        // Execute the published DLL directly with `dotnet <dll>` so the captured PID
        // *is* the application process — `dotnet run` creates a wrapper process whose
        // own EventCounters surface (mostly idle) sometimes returns no payloads
        // before our short collection window ends, which made tests flaky.
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi);
        if (_sampleProcess is null)
        {
            return;
        }

        // Consume stdout so the OS pipe buffer never fills (would deadlock the sample),
        // and harvest the "Now listening on: http://127.0.0.1:NNNN" line that Kestrel
        // emits when binding to port 0 picks an ephemeral port.
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _sampleProcess.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    var idx = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                    if (idx >= 0 && !_listeningUrlTcs.Task.IsCompleted)
                    {
                        var url = line[(idx + "Now listening on:".Length)..].Trim();
                        _listeningUrlTcs.TrySetResult(url);
                    }
                }
            }
            catch
            {
                // best-effort; if the read fails the URL TCS is never set and HTTP-driven
                // tests will just skip via EnsureListeningUrlAsync's timeout.
            }
        });
        _ = Task.Run(async () =>
        {
            try { using var err = _sampleProcess.StandardError; while (await err.ReadLineAsync() is not null) { } }
            catch { /* best-effort */ }
        });

        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30));
    }

    public Task DisposeAsync()
    {
        if (_sampleProcess is { HasExited: false })
        {
            try
            {
                _sampleProcess.Kill(entireProcessTree: true);
                _sampleProcess.WaitForExit(5_000);
            }
            catch (Exception)
            {
                // best-effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Discovery_FindsRunningSample()
    {
        EnsureSampleRunning();

        var discovery = new LocalProcessDiscovery();
        var processes = discovery.ListProcesses();
        processes.Should().Contain(p => p.ProcessId == Pid);

        var info = discovery.TryGetProcess(Pid);
        info.Should().NotBeNull();
        info!.CommandLine.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Capabilities_DetectsCoreClr()
    {
        EnsureSampleRunning();

        var detector = new CapabilityDetector();
        var caps = await detector.DetectAsync(Pid, CancellationToken.None);

        caps.Runtime.Should().Be(RuntimeFlavor.CoreClr);
        caps.CanSampleCpu.Should().BeTrue();
        caps.CanReadEventCounters.Should().BeTrue();
    }

    [Fact]
    public async Task Counters_ReturnsSystemRuntimeMetrics()
    {
        EnsureSampleRunning();

        var collector = new EventPipeCounterCollector();
        var snapshot = await collector.CollectAsync(
            Pid,
            // EventPipe session startup takes ~500ms-1s, then EventCounters are emitted
            // at intervalSeconds boundaries. 6s gives consistent headroom for 3-5 ticks.
            TimeSpan.FromSeconds(6),
            providers: new[] { "System.Runtime" },
            intervalSeconds: 1,
            cancellationToken: CancellationToken.None);

        snapshot.Counters.Should().NotBeEmpty();
        snapshot.Counters.Should().Contain(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
    }

    [LinuxOnlyFact]
    public async Task Resources_DetectsFdLeak_FromBadCodeSample()
    {

        await using var sample = await LiveHttpSample.StartAsync("BadCodeSample", "/");
        var collector = new ProcessResourcesCollector();
        var before = await collector.CollectAsync(sample.ProcessId, durationSeconds: 0, sampleEverySeconds: 2, CancellationToken.None);

        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        using var response = await http.GetAsync("/fd-leak?count=64");
        response.EnsureSuccessStatusCode();

        var after = await collector.CollectAsync(sample.ProcessId, durationSeconds: 0, sampleEverySeconds: 2, CancellationToken.None);
        after.FdCount.Should().NotBeNull();
        after.FdCount!.Value.Should().BeGreaterThan((before.FdCount ?? 0) + 50);
    }

    [LinuxOnlyFact]
    public async Task Resources_DetectsCloseWaitGrowth()
    {

        await using var sample = await LiveHttpSample.StartAsync("BadCodeSample", "/");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        using var response = await http.GetAsync("/socket-leak?count=8");
        response.EnsureSuccessStatusCode();
        await Task.Delay(500);

        var collector = new ProcessResourcesCollector();
        var resources = await collector.CollectAsync(sample.ProcessId, durationSeconds: 0, sampleEverySeconds: 2, CancellationToken.None);
        resources.Sockets.Should().NotBeNull();
        resources.Sockets!.CloseWait.Should().BeGreaterThan(0);
    }

    [LinuxOnlyFact]
    public async Task Resources_RlimitFraction_IsCalculated()
    {

        await using var sample = await LiveHttpSample.StartAsync("BadCodeSample", "/");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        using var response = await http.GetAsync("/fd-leak?count=8");
        response.EnsureSuccessStatusCode();

        var collector = new ProcessResourcesCollector();
        var resources = await collector.CollectAsync(sample.ProcessId, durationSeconds: 0, sampleEverySeconds: 2, CancellationToken.None);
        resources.Limits.Should().NotBeNull();
        resources.Limits!.NoFileSoft.Should().NotBeNull();
        resources.Limits.NoFileUsageFraction.Should().NotBeNull();
        resources.Limits.NoFileUsageFraction.Should().BeGreaterThan(0);
        resources.Limits.NoFileUsageFraction.Should().BeLessThan(1);
    }

    [Fact]
    public async Task Resources_TrendModeReturnsSamples()
    {
        EnsureSampleRunning();

        var collector = new ProcessResourcesCollector();
        var resources = await collector.CollectAsync(Pid, durationSeconds: 5, sampleEverySeconds: 2, CancellationToken.None);

        resources.Trend.Should().NotBeNull();
        resources.Trend!.Samples.Should().HaveCountGreaterThanOrEqualTo(2);
        resources.CapturedAt.Should().Be(resources.Trend.Samples[^1].Timestamp);
    }

    [WindowsOnlyFact]
    public async Task Resources_ReturnsHandleCount_OnWindows()
    {

        EnsureSampleRunning();
        var collector = new ProcessResourcesCollector();
        var resources = await collector.CollectAsync(Pid, durationSeconds: 0, sampleEverySeconds: 2, CancellationToken.None);

        resources.HandleCount.Should().NotBeNull();
        resources.HandleCount!.Value.Should().BeGreaterThan(0);
        resources.Fd.Should().BeNull();
        resources.Sockets.Should().BeNull();
    }

    [Fact]
    public async Task MeterApi_ReturnsBusinessCounter_FromBadCodeSample()
    {
        await using var sample = await StartAuxiliarySampleAsync("BadCodeSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var collector = new EventPipeCounterCollector();

        var driver = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var response = await http.GetAsync("/meter-spam?count=8&kind=counter");
            response.EnsureSuccessStatusCode();
        });

        var snapshot = await collector.CollectAsync(
            sample.ProcessId,
            TimeSpan.FromSeconds(6),
            providers: Array.Empty<string>(),
            meters: new[] { "BadCodeSample" },
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 32,
            cancellationToken: CancellationToken.None);

        await driver;

        snapshot.Meters.Any(m =>
            m.Meter == "BadCodeSample" &&
            m.Instrument == "orders.total" &&
            m.LastValue.HasValue &&
            m.LastValue.Value > 0).Should().BeTrue();
    }

    [Fact]
    public async Task MeterApi_ReconstitutesAspNetCoreRequestHistogram()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var collector = new EventPipeCounterCollector();
        using var cts = new CancellationTokenSource();

        var driver = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200), cts.Token);
            while (!cts.IsCancellationRequested)
            {
                using var response = await http.GetAsync("/weatherforecast", cts.Token);
                response.EnsureSuccessStatusCode();
                await Task.Delay(75, cts.Token);
            }
        }, cts.Token);

        try
        {
            var snapshot = await collector.CollectAsync(
                Pid,
                TimeSpan.FromSeconds(6),
                providers: Array.Empty<string>(),
                meters: new[] { "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel" },
                intervalSeconds: 1,
                maxInstrumentTimeSeries: 256,
                cancellationToken: CancellationToken.None);

            var requestDuration = snapshot.Meters.Single(m =>
                m.Instrument == "http.server.request.duration" &&
                m.Histogram != null &&
                m.Histogram.P95 > 0);

            requestDuration.Histogram!.P50.Should().BeGreaterThan(0);
            requestDuration.Histogram!.P99.Should().BeGreaterThanOrEqualTo(requestDuration.Histogram.P95);
        }
        finally
        {
            cts.Cancel();
            try { await driver; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task MeterApi_HonoursCardinalityCap()
    {
        await using var sample = await StartAuxiliarySampleAsync("BadCodeSample");
        using var http = new HttpClient { BaseAddress = new Uri(sample.BaseUrl) };
        var collector = new EventPipeCounterCollector();

        var driver = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var response = await http.GetAsync("/meter-spam?count=25&kind=counter");
            response.EnsureSuccessStatusCode();
        });

        var snapshot = await collector.CollectAsync(
            sample.ProcessId,
            TimeSpan.FromSeconds(6),
            providers: Array.Empty<string>(),
            meters: new[] { "BadCodeSample" },
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 5,
            cancellationToken: CancellationToken.None);

        await driver;

        snapshot.Meters.Count.Should().BeLessOrEqualTo(5);
        snapshot.Notes.Should().Contain(note => note.Contains("TimeSeriesLimitReached", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectActivities_CapturesSampleActivitySourceEvents()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var collector = new EventPipeActivityCollector();

        var driver = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            using var response = await http.GetAsync("/activity?delayMs=40");
            response.EnsureSuccessStatusCode();
        });

        var capture = await collector.CollectAsync(
            Pid,
            TimeSpan.FromSeconds(4),
            sources: new[] { "CoreClrSample.Activities" },
            maxActivities: 20,
            cancellationToken: CancellationToken.None);

        await driver;

        capture.TotalActivities.Should().BeGreaterThanOrEqualTo(2, "the /activity fixture emits a parent + child Activity");
        capture.BySource.Should().Contain(summary => summary.SourceName == "CoreClrSample.Activities");
        capture.ByOperation.Should().Contain(summary => summary.SourceName == "CoreClrSample.Activities" && summary.OperationName == "CoreClrSample.Outer");
        capture.ByOperation.Should().Contain(summary => summary.SourceName == "CoreClrSample.Activities" && summary.OperationName == "CoreClrSample.Inner");

        var outer = capture.Activities.Should().ContainSingle(activity =>
            activity.SourceName == "CoreClrSample.Activities" && activity.OperationName == "CoreClrSample.Outer").Subject;
        outer.Id.Should().NotBeNullOrWhiteSpace();
        outer.TraceId.Should().NotBeNullOrWhiteSpace();
        outer.SpanId.Should().NotBeNullOrWhiteSpace();
        outer.Duration.Should().NotBeNull();
        outer.Duration!.Value.Should().BeGreaterThan(TimeSpan.Zero);
        outer.Tags.Should().ContainKey("endpoint");
        outer.Tags["endpoint"].Should().Be("/activity");

        capture.Activities.Should().Contain(activity =>
            activity.SourceName == "CoreClrSample.Activities" &&
            activity.OperationName == "CoreClrSample.Inner" &&
            activity.ParentId == outer.Id);
    }

    [Fact(Skip = "Quarantined: crashes ubuntu-latest test host (EventPipe SampleProfiler). Tracked in #147.")]
    public async Task CpuSampler_ProducesHotspots()
    {
        EnsureSampleRunning();

        var sampler = new EventPipeCpuSampler();
        var result = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 10,
            cancellationToken: CancellationToken.None);

        result.Summary.TotalSamples.Should().BeGreaterThan(0);
        result.Summary.TopHotspots.Should().NotBeEmpty();
        result.Artifact.Root.Children.Should().NotBeEmpty("the call-tree artifact must capture at least one stack");
    }

    [Fact(Skip = "Quarantined: crashes ubuntu-latest test host (EventPipe SampleProfiler). Tracked in #147.")]
    public async Task CpuSampler_ResolvesSourceLines_WhenEnabled()
    {
        EnsureSampleRunning();

        var sampler = new EventPipeCpuSampler();
        var result = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 25,
            sourceResolution: new SourceResolutionOptions(Enabled: true, SymbolPath: null, MaxResolved: 10),
            cancellationToken: CancellationToken.None);

        // Wiring contract: with resolveSourceLines enabled, the artifact always carries a
        // non-null ResolvedSources map. Whether it's populated depends on the runtime's
        // ability to locate PDBs (env-dependent — framework binaries usually ship without
        // PDBs side-by-side, so an empty dictionary is acceptable degradation, not failure).
        // Asserting NotBeEmpty here would contradict the comment above and racily fail on
        // runners where the sample's PDB lookup path differs (observed on windows-latest CI).
        result.Artifact.ResolvedSources.Should().NotBeNull();
    }

    [Fact(Skip = "Quarantined: crashes ubuntu-latest test host (EventPipe SampleProfiler). Tracked in #147.")]
    public async Task CpuSampler_EmitsMethodIdentities_ForUserCode()
    {
        EnsureSampleRunning();

        var sampleDll = LocateSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found.");

        var expectedMvid = new MvidReader().TryRead(sampleDll);
        expectedMvid.Should().NotBeNull("the published sample DLL must have a readable MVID for the handoff contract test");

        var sampler = new EventPipeCpuSampler();
        var result = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 50,
            cancellationToken: CancellationToken.None);

        result.Artifact.MethodIdentities.Should().NotBeNull();
        result.Artifact.MethodIdentities.Should().NotBeEmpty(
            "the sampler must always emit a handoff payload for ranked hotspots");

        // At least one hotspot should map to a method from CoreClrSample itself.
        var userCodeIdentity = result.Artifact.MethodIdentities.Values
            .FirstOrDefault(id => id.ModuleVersionId == expectedMvid);

        userCodeIdentity.Should().NotBeNull(
            "the sampler must resolve user-code hotspots to the publishing assembly's MVID for the assembly-mcp handoff");
        userCodeIdentity!.MetadataToken.Should().BeGreaterThan(0, "the handoff pair (MVID, token) must round-trip to the assembly-mcp");
        userCodeIdentity.MethodName.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task DumpInspector_ExtractsHeapStats_AndTypeIdentityForUserCode()
    {
        EnsureSampleRunning();

        var sampleDll = LocateSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found.");
        var expectedMvid = new MvidReader().TryRead(sampleDll);
        expectedMvid.Should().NotBeNull();

        // Capture a WithHeap dump (the only kind that supports heap walk).
        var dumpRoot = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dumpRoot);
        try
        {
            var dumper = new DotnetDiagnosticsMcp.Core.Dump.DiagnosticsClientDumper(
                new TestArtifactRootProvider(dumpRoot));
            var dump = await dumper.WriteDumpAsync(Pid, ProcessDumpType.WithHeap, outputDirectory: null, CancellationToken.None);
            File.Exists(dump.FilePath).Should().BeTrue();
            dump.FileSizeBytes.Should().BeGreaterThan(0);

            var inspector = new ClrMdDumpInspector();
            var inspection = await inspector.InspectAsync(
                dump.FilePath,
                new DumpInspectionOptions(TopTypes: 25),
                CancellationToken.None);

            inspection.Runtime.Name.Should().NotBeNullOrEmpty();
            inspection.Runtime.Version.Should().NotBeNullOrEmpty();
            inspection.Heap.TotalBytes.Should().BeGreaterThan(0, "WithHeap dump must contain a walkable managed heap");
            inspection.TopTypesByBytes.Should().NotBeEmpty();
            inspection.TopTypesByInstances.Should().NotBeEmpty();

            // System types (Object[], String, etc.) always dominate; we only need to confirm at
            // least one TypeStat carries an Identity with a non-null MVID — that's the handoff
            // proof. CoreClrSample's own types are tiny by bytes so we don't rely on user code
            // appearing in the top-25.
            var withMvid = inspection.TopTypesByBytes
                .Concat(inspection.TopTypesByInstances)
                .FirstOrDefault(s => s.Identity is { ModuleVersionId: not null, MetadataToken: not null });
            withMvid.Should().NotBeNull(
                "ClrMdDumpInspector must populate TypeIdentity (mvid + token) for handoff to dotnet-assembly-mcp");
        }
        finally
        {
            try { Directory.Delete(dumpRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task DumpInspector_InspectsObjectGcRootAndObjectSize_FromLiveHeapSnapshot()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        for (var i = 0; i < 4; i++)
        {
            var response = await http.GetAsync("/leak");
            response.EnsureSuccessStatusCode();
        }

        var inspector = new ClrMdDumpInspector();
        var snapshot = await inspector.InspectLiveAsync(
            Pid,
            new DumpInspectionOptions(TopTypes: 25, IncludeRetentionPaths: true),
            CancellationToken.None);

        var leakedBufferPath = snapshot.RetentionPaths?
            .FirstOrDefault(path => string.Equals(path.TargetTypeFullName, "System.Byte[]", StringComparison.Ordinal));
        leakedBufferPath.Should().NotBeNull(
            "the /leak workload retains 1 MiB byte[] objects that should surface in the live heap snapshot's retention paths");

        var leakedBufferAddress = leakedBufferPath!.TargetObjectAddress;
        var objectView = await inspector.InspectObjectAsync(snapshot, leakedBufferAddress, CancellationToken.None);
        objectView.TypeFullName.Should().Be("System.Byte[]");
        objectView.IsArray.Should().BeTrue();
        objectView.ArrayLength.Should().Be(1_048_576);
        objectView.ArraySample.Should().NotBeNull();
        objectView.ArraySample!.Should().NotBeEmpty();
        objectView.ArraySample.Should().OnlyContain(e => e.Value == "0",
            "freshly-allocated byte[] entries are zero-initialized");

        var gcrootView = await inspector.InspectGcRootAsync(snapshot, leakedBufferAddress, CancellationToken.None);
        gcrootView.Chain.Should().NotBeEmpty();
        gcrootView.Chain[0].RootKind.Should().NotBeNullOrWhiteSpace(
            "the leaked byte[] must remain reachable from some GC root through the endpoint's retained list");
        gcrootView.Chain[^1].ObjectAddress.Should().Be(leakedBufferAddress);

        var objectSize = await inspector.InspectObjectSizeAsync(snapshot, leakedBufferAddress, CancellationToken.None);
        objectSize.ObjectCount.Should().Be(1, "a byte[] retains only itself in the object graph walk");
        objectSize.RetainedBytes.Should().Be(objectView.Size);
    }

    [Fact(Timeout = 60_000)]
    public async Task DumpInspector_InspectLiveAsync_FindsPendingAsyncStateMachines()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using var response = await http.GetAsync("/async-pending?count=3", CancellationToken.None);
        response.EnsureSuccessStatusCode();
        await Task.Delay(500);

        var inspector = new ClrMdDumpInspector();
        var snapshot = await inspector.InspectLiveAsync(Pid, cancellationToken: CancellationToken.None);

        snapshot.AsyncOperations.Should().NotBeNullOrEmpty(
            "the async fixture roots never-completing tasks so the heap walk can reconstruct their state machines");

        var sampleOperations = snapshot.AsyncOperations!
            .Where(op => op.StateMachineTypeFullName.Contains("AsyncFixture+<", StringComparison.Ordinal))
            .ToList();

        sampleOperations.Should().NotBeEmpty(
            "the /async-pending endpoint launches nested async methods whose compiler-generated state machines must remain on the heap");
        sampleOperations.Should().Contain(op => op.State >= 0,
            "pending async methods should expose a non-completed state via <>1__state");
        sampleOperations.Any(op => op.AwaiterTypeFullName is not null && op.AwaiterTypeFullName.Contains("TaskAwaiter", StringComparison.Ordinal))
            .Should().BeTrue("the fixture awaits a TaskCompletionSource-backed Task so the awaiter must be visible");
        sampleOperations.Any(op => op.Stack is not null && op.Stack.Count >= 2)
            .Should().BeTrue("nested awaits should produce a best-effort continuation chain with at least one parent frame");
    }

    [Fact(Timeout = 60_000)]
    public async Task ThreadSnapshot_InspectLive_EnumeratesManagedThreads()
    {
        EnsureSampleRunning();

        var inspector = new ClrMdThreadSnapshotInspector();
        var snapshot = await inspector.InspectLiveAsync(
            Pid,
            new ThreadSnapshotOptions(MaxFramesPerThread: 32),
            CancellationToken.None);

        snapshot.Origin.Should().Be(ThreadSnapshotOrigin.Live);
        snapshot.ProcessId.Should().Be(Pid);
        snapshot.RuntimeName.Should().NotBeNullOrEmpty();
        snapshot.Threads.Should().NotBeEmpty("a running ASP.NET process has at least the main + GC + finalizer threads");
        snapshot.Threads.Should().Contain(t => t.IsFinalizer, "every CoreCLR process has a finalizer thread");
        snapshot.Threads.Where(t => t.Frames.Count > 0).Should().NotBeEmpty(
            "at least one thread should have a captured managed stack");
        snapshot.Locks.Should().NotBeNull();
    }

    [Fact]
    public async Task JitMapEmitter_EmitsPerfMap_WithManagedSymbols()
    {
        // Slice 2c Eixo B live coverage: against a real CoreClrSample process the emitter
        // must (1) open a rundown session, (2) capture at least one MethodDCStop, (3) write
        // /tmp/perf-<pid>.map in the perf format, (4) populate the symbol→identity dict.
        // Linux-only: macOS does not have /tmp/perf-<pid>.map convention and Windows
        // ETW already attaches managed names to user frames natively.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        EnsureSampleRunning();

        var emitter = new DotnetDiagnosticsMcp.Core.OffCpu.JitMapEmitter();
        var result = await emitter.EmitAsync(Pid, rundownTimeout: TimeSpan.FromSeconds(5));

        result.Should().NotBeNull("EventPipe rundown session against a running CoreCLR app must succeed");
        result!.MapPath.Should().Be(Path.Combine(Path.GetTempPath(), $"perf-{Pid}.map"));
        File.Exists(result.MapPath).Should().BeTrue("perf-<pid>.map file must be on disk for perf to consume");

        try
        {
            result.MethodCount.Should().BeGreaterThan(0,
                "the CLR rundown must enumerate at least a handful of already-JITted framework methods");
            result.Methods.Should().NotBeEmpty("the per-method range list must be populated for parser enrichment");

            var lines = await File.ReadAllLinesAsync(result.MapPath);
            lines.Should().NotBeEmpty();
            // Format: "<hexStart> <hexSize> <symbol>" — assert at least one line parses cleanly.
            var parsed = lines.Where(l => l.Length > 0)
                              .Select(l => l.Split(' ', 3))
                              .Where(p => p.Length == 3)
                              .ToList();
            parsed.Should().NotBeEmpty("at least one entry must follow the perf-map line format");
            parsed[0][0].Should().MatchRegex("^[0-9a-fA-F]+$", "start address is a hex string");
            parsed[0][1].Should().MatchRegex("^[0-9a-fA-F]+$", "size is a hex string");
            parsed[0][2].Should().NotBeNullOrWhiteSpace();

            // At least some methods in the range list should carry an MVID — modules backing
            // System.Private.CoreLib etc. exist on disk so MvidReader will read them.
            result.Methods.Should().Contain(r => r.Identity.ModuleVersionId.HasValue,
                "at least one rundown method should resolve its module MVID on disk");

            // Sanity-check Resolve on the live data: pick the first range, ask for an address
            // inside it, assert we get the same identity back. Address-based lookup is the
            // parser's authoritative path so a broken Resolve would silently drop all enrichment.
            // The end-exclusive boundary case is covered deterministically in
            // JitMapResultResolveTests (an adjacent JIT range starting at sample.StartAddress +
            // sample.Size is legal here and would make a boundary assertion flaky).
            var sample = result.Methods.First(r => r.Size > 0);
            result.Resolve(sample.StartAddress).Should().BeSameAs(sample.Identity,
                "Resolve must return the range's identity for an address at the method start");
            result.Resolve(sample.StartAddress + (sample.Size / 2)).Should().BeSameAs(sample.Identity,
                "Resolve must return the same identity for an address in the middle of the range");
        }
        finally
        {
            try { File.Delete(result.MapPath); } catch { /* best effort */ }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task JitCapture_DumpsHotspotBytesToDisk()
    {
        EnsureSampleRunning();

        var sampler = new EventPipeCpuSampler();
        var sample = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 50,
            cancellationToken: CancellationToken.None);

        var identity = sample.Artifact.MethodIdentities.Values
            .FirstOrDefault(id => id.ModuleVersionId is { } && id.MetadataToken is > 0);
        identity.Should().NotBeNull("the sampler must surface at least one JITted method for the capture handoff");

        var mvid = identity!.ModuleVersionId!.Value;
        var token = identity.MetadataToken!.Value;

        var outDir = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-jitcap-{Guid.NewGuid():N}");
        try
        {
            var capturer = new ClrMdJitMethodCapturer(new TestArtifactRootProvider(outDir));
            var artifact = await capturer.CaptureLiveAsync(
                Pid,
                new MethodCaptureRequest(mvid, token, OutputDirectory: null),
                CancellationToken.None);

            artifact.Origin.Should().Be(CapturedMethodBytesOrigin.Live);
            artifact.ProcessId.Should().Be(Pid);
            artifact.Architecture.Should().NotBeNullOrEmpty();
            artifact.Method.ModuleVersionId.Should().Be(mvid);
            artifact.Method.MetadataToken.Should().Be(token);
            artifact.Regions.Should().NotBeEmpty("a JITted method must expose at least its Hot region");

            var hot = artifact.Regions.FirstOrDefault(r => r.Region == "Hot");
            hot.Should().NotBeNull("JIT-emitted code always has a Hot region");
            hot!.Size.Should().BeGreaterThan(0);
            File.Exists(hot.FilePath).Should().BeTrue("the capturer must materialise the bytes file on disk");
            new FileInfo(hot.FilePath).Length.Should().Be(hot.Size,
                "the file size must match the reported region size for the native-mcp handoff");
        }
        finally
        {
            try { Directory.Delete(outDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Quarantined on Linux CI only: this test reliably segfaults the xunit test host on
    // ubuntu-latest under full-suite load (native crash inside libcoreclr's EventPipe
    // SampleProfiler — see #147). Runs locally on Linux/macOS and on Windows CI so the
    // closed-generic handoff contract from #21 stays covered while we pursue the upstream
    // CoreCLR fix.
    [Trait("Category", "Flaky")]
    [SkipOnLinuxCiFact("Quarantined on Linux CI: crashes test host inside libcoreclr's EventPipe SampleProfiler. Tracked in #147 (dump artifact 7161760638 on run 26290739828). Runnable locally and on Windows CI.", Timeout = 60_000)]
    public async Task CpuSampler_EmitsClosedGenericInstantiations_FromCoreClrSampleFixture()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        var sampleDll = LocateSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found.");
        var expectedMvid = new MvidReader().TryRead(sampleDll);
        expectedMvid.Should().NotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Drive the /generics endpoint in a tight loop on a background task so the CPU
        // sampler's window overlaps with hot Box<T>.Wrap / GenericFixture.Echo<T> frames.
        var driver = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { _ = await http.GetAsync("/generics?iterations=200000", cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* tolerate transient races */ }
            }
        }, cts.Token);

        try
        {
            var sampler = new EventPipeCpuSampler();
            var result = await sampler.SampleAsync(
                Pid,
                TimeSpan.FromSeconds(8),
                topN: 100,
                cancellationToken: CancellationToken.None);

            // Look for at least one hotspot identity that (a) belongs to CoreClrSample
            // and (b) carries a closed instantiation block — either type-level (Box`1)
            // or method-level (Echo<T>).
            var userCode = result.Artifact.MethodIdentities.Values
                .Where(id => id.ModuleVersionId == expectedMvid)
                .ToList();
            var closed = userCode
                .Where(id => id.GenericTypeArguments is { } gi
                          && (gi.Type.Count > 0 || gi.Method.Count > 0))
                .ToList();

            closed.Should().NotBeEmpty(
                "the /generics fixture exercises Box<int>/Box<string> (type-level) and " +
                "Echo<int>/Echo<string> (method-level) closed generics — the sampler " +
                "must surface GenericTypeArguments on at least one user-code hotspot " +
                "per issue #21's acceptance bullet (UserCodeIdentities={0}, TotalSamples={1})",
                userCode.Count, result.Summary.TotalSamples);

            // Tighten the assertion so a regression on either axis fails the test rather
            // than slipping through on a single matching frame. Both axes are part of #21's
            // acceptance bullets: type-level (Box`1) AND method-level (Echo<T>).
            //
            // NOTE on shared generics: the CLR JITs ONE body shared across all reference-
            // type instantiations using System.__Canon (e.g. Box<string> appears as
            // Box<System.__Canon> in the trace). Value-type instantiations are unique
            // (Box<int> → Box<System.Int32>). The handoff contract surfaces whatever the
            // runtime emits — we therefore assert int as the unique value-type arg and
            // accept either System.String or System.__Canon for the reference-type arg.
            // See docs/handoff-contract.md §3.5 / "shared generics" note.
            var typeLevel = closed
                .Where(id => id.GenericTypeArguments!.Type.Count > 0
                          && (id.TypeFullName?.Contains("Box", StringComparison.Ordinal) ?? false))
                .ToList();
            typeLevel.Should().NotBeEmpty(
                "Box<int>/Box<string>.Wrap must surface as type-level closed instantiations");
            var typeArgs = typeLevel
                .SelectMany(id => id.GenericTypeArguments!.Type)
                .ToHashSet(StringComparer.Ordinal);
            typeArgs.Should().Contain(a => a.Contains("System.Int32", StringComparison.Ordinal),
                "Box<int>.Wrap must round-trip its closed type-arg as System.Int32 (value types get unique JIT code)");
            typeArgs.Should().Contain(
                a => a.Contains("System.String", StringComparison.Ordinal)
                  || a.Contains("System.__Canon", StringComparison.Ordinal),
                "Box<string>.Wrap must round-trip as System.String or the runtime's shared System.__Canon (reference-type instantiations share JIT code)");

            var methodLevel = closed
                .Where(id => id.GenericTypeArguments!.Method.Count > 0
                          && string.Equals(id.MethodName, "Echo", StringComparison.Ordinal))
                .ToList();

            // Best-effort method-level assertion: at the time of writing, Linux EventPipe +
            // TraceLog does NOT synthesise the `<T>` suffix on a generic method's
            // FullMethodName — `GenericFixture.Echo<int>` arrives as plain
            // `GenericFixture.Echo` with `GenericArity=0`. Tracked in issue #85; until that
            // closes, we accept either outcome but never both axes missing.
            if (methodLevel.Count > 0)
            {
                var methodArgs = methodLevel
                    .SelectMany(id => id.GenericTypeArguments!.Method)
                    .ToHashSet(StringComparer.Ordinal);
                methodArgs.Should().Contain(a => a.Contains("System.Int32", StringComparison.Ordinal),
                    "Echo<int> must round-trip its closed method-arg as System.Int32 (value types get unique JIT code)");
                methodArgs.Should().Contain(
                    a => a.Contains("System.String", StringComparison.Ordinal)
                      || a.Contains("System.__Canon", StringComparison.Ordinal),
                    "Echo<string> must round-trip as System.String or the runtime's shared System.__Canon");
            }

            // Every emitted instantiation arg must be a CLR reflection-style FQN with
            // no assembly qualification (per docs/handoff-contract.md §3.5).
            foreach (var gi in closed.Select(c => c.GenericTypeArguments!))
            {
                foreach (var arg in gi.Type.Concat(gi.Method))
                {
                    arg.Should().NotBeNullOrWhiteSpace();
                    arg.Should().NotContain(",", "type args must NOT be assembly-qualified " +
                        $"per the handoff contract; got '{arg}'");
                }
            }
        }
        finally
        {
            cts.Cancel();
            try { await driver; } catch { /* expected on cancel */ }
        }
    }

    // Quarantined on Linux CI only (same native libcoreclr crash family as #147; this
    // specific test is tracked in #145). Stays runnable locally and on Windows CI so the
    // ClrMD opt-in method-level instantiation enrichment from #86 keeps coverage.
    [Trait("Category", "Flaky")]
    [SkipOnLinuxCiFact("Quarantined on Linux CI: crashes test host inside libcoreclr's EventPipe SampleProfiler. Tracked in #145 / #147 (dump artifact 7161760638 on run 26290739828). Runnable locally and on Windows CI.", Timeout = 90_000)]
    public async Task CpuSampler_ResolvesMethodLevelClosedGenerics_OnlyWhenOptInEnabled()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        var sampleDll = LocateSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found.");
        var expectedMvid = new MvidReader().TryRead(sampleDll);
        expectedMvid.Should().NotBeNull();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var driver = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { _ = await http.GetAsync("/generics?iterations=200000", cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* tolerate transient races */ }
            }
        }, cts.Token);

        try
        {
            var sampler = new EventPipeCpuSampler();
            var baseline = await sampler.SampleAsync(
                Pid,
                TimeSpan.FromSeconds(6),
                topN: 100,
                cancellationToken: CancellationToken.None);
            var enriched = await sampler.SampleAsync(
                Pid,
                TimeSpan.FromSeconds(6),
                topN: 100,
                methodInstantiationResolution: new MethodInstantiationResolutionOptions(Enabled: true, MaxResolved: 100),
                cancellationToken: CancellationToken.None);

            var withoutMethodArgs = baseline.Artifact.MethodIdentities.Values
                .Where(id => id.ModuleVersionId == expectedMvid)
                .Where(id => string.Equals(id.MethodName, "Echo", StringComparison.Ordinal))
                .Where(id => id.GenericTypeArguments is { Method.Count: > 0 })
                .ToList();
            var withMethodArgs = enriched.Artifact.MethodIdentities.Values
                .Where(id => id.ModuleVersionId == expectedMvid)
                .Where(id => string.Equals(id.MethodName, "Echo", StringComparison.Ordinal))
                .Where(id => id.GenericTypeArguments is { Method.Count: > 0 })
                .ToList();

            if (OperatingSystem.IsLinux())
            {
                withoutMethodArgs.Should().BeEmpty(
                    "Linux EventPipe alone only knows the open MethodDef for Echo<T>; closed method args arrive via the opt-in ClrMD enrichment (issue #86)");
            }

            withMethodArgs.Should().NotBeEmpty(
                "resolveMethodInstantiations=true must recover closed method args for Echo<T> from the hottest sampled frames");
            withMethodArgs.Should().OnlyContain(id => !string.IsNullOrWhiteSpace(id.ClosedSignature),
                "resolved method-level instantiations must also stamp ClosedSignature for operator-facing display");

            var methodArgs = withMethodArgs
                .SelectMany(id => id.GenericTypeArguments!.Method)
                .ToHashSet(StringComparer.Ordinal);
            methodArgs.Should().Contain(a => a.Contains("System.Int32", StringComparison.Ordinal),
                "Echo<int> must round-trip its closed method-arg as System.Int32");
            methodArgs.Should().Contain(
                a => a.Contains("System.String", StringComparison.Ordinal)
                  || a.Contains("System.__Canon", StringComparison.Ordinal),
                "Echo<string> must round-trip as System.String or the runtime's shared System.__Canon");
        }
        finally
        {
            cts.Cancel();
            try { await driver; } catch { /* expected on cancel */ }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ThreadSnapshot_InspectLive_CapturesThreadPoolSnapshot()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 40 && response is null; attempt++)
        {
            try
            {
                response = await http.GetAsync(
                    "/threadpool/queue?globalItems=256&localItems=256&blockMs=4000",
                    CancellationToken.None);
            }
            catch (HttpRequestException) when (attempt < 39)
            {
                EnsureSampleRunning();
                await Task.Delay(250);
            }
        }
        response.Should().NotBeNull();
        using (response!)
        {
            response.EnsureSuccessStatusCode();
        }

        await Task.Delay(250);

        var inspector = new ClrMdThreadSnapshotInspector();
        var snapshot = await inspector.InspectLiveAsync(
            Pid,
            new ThreadSnapshotOptions(MaxFramesPerThread: 32),
            CancellationToken.None);

        snapshot.ThreadPool.Should().NotBeNull("CoreCLR ClrMD snapshots should capture ThreadPool state for view='threadpool'");
        snapshot.ThreadPool!.Initialized.Should().BeTrue();
        snapshot.ThreadPool.Workers.Max.Should().BeGreaterThanOrEqualTo(snapshot.ThreadPool.Workers.Min);
        snapshot.ThreadPool.PendingWorkItems.Should().BeGreaterThanOrEqualTo(0);
        snapshot.ThreadPool.Notes.Should().NotBeNullOrEmpty(
            "the live fallback should explain when it uses lightweight thread-snapshot + static ThreadPool root inspection instead of a heap walk");
        snapshot.ThreadPool.Notes.Should().Contain(note => note.Contains("heap-wide walks", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 60_000)]
    public async Task AllocationSampler_ProducesTopTypes_WhenWorkloadAllocates()
    {
        EnsureSampleRunning();
        var baseUrl = await EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

        // Drive the /render endpoint in a tight loop: each request does O(n²) string concat,
        // producing a heavy stream of System.String allocations visible to GCAllocationTick.
        var driver = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { _ = await http.GetAsync("/render?count=1000", cts.Token); }
                catch (OperationCanceledException) { break; }
                catch { /* tolerate transient races */ }
            }
        }, cts.Token);

        try
        {
            var sampler = new EventPipeAllocationSampler();
            var result = await sampler.SampleAsync(
                Pid,
                TimeSpan.FromSeconds(8),
                topN: 25,
                cancellationToken: CancellationToken.None);

            result.Summary.TotalEvents.Should().BeGreaterThan(0,
                "the /render workload performs heavy string allocations that must surface GCAllocationTick events");
            result.Summary.TopByBytes.Should().NotBeEmpty(
                "at least one type must be aggregated from GCAllocationTick events");
            result.Summary.TopByCount.Should().NotBeEmpty();

            // System.String dominates the /render endpoint's O(n²) concat workload.
            result.Summary.TopByBytes.Should().Contain(t =>
                t.TypeName.Contains("String", StringComparison.OrdinalIgnoreCase) ||
                t.TypeName.Contains("Char", StringComparison.OrdinalIgnoreCase) ||
                t.TypeName.Contains("Object", StringComparison.OrdinalIgnoreCase),
                "the render workload allocates strings heavily — at least one string-related type must appear");

            // Call-tree artifact should have captured call stacks from GCAllocationTick events.
            result.Artifact.Root.Children.Should().NotBeEmpty(
                "the allocation call-tree artifact must capture at least one stack when events were observed");

            var stamped = CallTreeIdentityProjector.Stamp(result.Artifact.Root, result.Artifact.MethodIdentities);
            Flatten(stamped)
                .Any(node => node.Identity is { ModuleVersionId: not null, MetadataToken: not null })
                .Should().BeTrue("allocation drill-down must surface at least one MethodIdentity-backed frame for assembly-mcp handoff");
        }
        finally
        {
            cts.Cancel();
            try { await driver; } catch { /* expected on cancel */ }
        }
    }

    private static IEnumerable<CallTreeNode> Flatten(CallTreeNode root)
    {
        var stack = new Stack<CallTreeNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }
    }

    private async Task<string> EnsureListeningUrlAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var url = await _listeningUrlTcs.Task.WaitAsync(cts.Token);
            await WaitForHttpReadyAsync(url, timeout);
            return url;
        }
        catch (OperationCanceledException)
        {
            throw SkipException.ForReason("CoreClrSample did not advertise an HTTP listening URL within the timeout.");
        }
    }

    internal static async Task WaitForHttpReadyAsync(string baseUrl, TimeSpan timeout, string readinessPath = "/weatherforecast")
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(readinessPath, CancellationToken.None);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // Kestrel sometimes logs the listening URL just before the socket is fully ready.
            }

            await Task.Delay(250);
        }

        throw SkipException.ForReason($"CoreClrSample did not accept HTTP requests on {baseUrl} within the timeout.");
    }

    private void EnsureSampleRunning()
    {
        if (_sampleProcess is null || _sampleProcess.HasExited)
        {
            throw SkipException.ForReason("CoreClrSample is not running (could not start the sample process).");
        }
    }

    internal static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient
                .GetPublishedProcesses()
                .Contains(pid))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private static async Task<AuxiliarySampleHost> StartAuxiliarySampleAsync(string sampleName)
    {
        var sampleDll = LocateSampleDll(sampleName)
            ?? throw SkipException.ForReason($"{sampleName}.dll not found. Build the sample before running this test.");
        return await AuxiliarySampleHost.StartAsync(sampleName, sampleDll);
    }

    private static string? LocateSampleDll(string sampleName = "CoreClrSample")
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", sampleName);
            if (Directory.Exists(sampleDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(sampleDir, "bin", configuration, "net10.0", $"{sampleName}.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }
}

internal sealed class LiveHttpSample : IAsyncDisposable
{
    private readonly TaskCompletionSource<string> _listeningUrlTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;

    internal static string? LocateSampleDll(string sampleName)
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", sampleName);
            if (Directory.Exists(sampleDir))
            {
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(sampleDir, "bin", configuration, "net10.0", $"{sampleName}.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }

    private LiveHttpSample()
    {
    }

    public int ProcessId => _process?.Id ?? throw new InvalidOperationException("Sample not started.");

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<LiveHttpSample> StartAsync(string sampleName, string readyPath)
    {
        var sampleDll = LocateSampleDll(sampleName)
            ?? throw new InvalidOperationException($"{sampleName}.dll not found.");

        var sample = new LiveHttpSample();
        await sample.StartCoreAsync(sampleDll, readyPath, sampleName).ConfigureAwait(false);
        return sample;
    }

    public async ValueTask DisposeAsync()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _process?.Dispose();
    }

    private async Task StartCoreAsync(string sampleDll, string readyPath, string sampleName)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {sampleName}.");
        _ = ConsumeStandardOutputAsync(_process);
        _ = Task.Run(async () =>
        {
            try { using var err = _process.StandardError; while (await err.ReadLineAsync() is not null) { } }
            catch { }
        });

        await LiveCoreClrProcessTests.WaitForDiagnosticEndpointAsync(_process.Id, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        BaseUrl = await EnsureListeningUrlAsync(sampleName, readyPath, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
    }

    private async Task ConsumeStandardOutputAsync(Process process)
    {
        try
        {
            using var reader = process.StandardOutput;
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                var idx = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                if (idx >= 0 && !_listeningUrlTcs.Task.IsCompleted)
                {
                    var url = line[(idx + "Now listening on:".Length)..].Trim();
                    _listeningUrlTcs.TrySetResult(url);
                }
            }
        }
        catch
        {
        }
    }

    private async Task<string> EnsureListeningUrlAsync(string sampleName, string readyPath, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var url = await _listeningUrlTcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            await WaitForHttpReadyAsync(url, readyPath, timeout, sampleName).ConfigureAwait(false);
            return url;
        }
        catch (OperationCanceledException)
        {
            throw SkipException.ForReason($"{sampleName} did not advertise an HTTP listening URL within the timeout.");
        }
    }

    private static async Task WaitForHttpReadyAsync(string baseUrl, string readyPath, TimeSpan timeout, string sampleName)
    {
        using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await http.GetAsync(readyPath, CancellationToken.None).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw SkipException.ForReason($"{sampleName} did not accept HTTP requests on {baseUrl} within the timeout.");
    }
}

internal sealed class AuxiliarySampleHost : IAsyncDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<string> _listeningUrlTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _sampleName;

    private AuxiliarySampleHost(string sampleName, Process process)
    {
        _sampleName = sampleName;
        _process = process;
    }

    public int ProcessId => _process.Id;

    public string BaseUrl { get; private set; } = string.Empty;

    public static async Task<AuxiliarySampleHost> StartAsync(string sampleName, string sampleDll)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var process = Process.Start(psi)
            ?? throw SkipException.ForReason($"Failed to start {sampleName}.");
        var host = new AuxiliarySampleHost(sampleName, process);
        host.PumpOutput();
        await LiveCoreClrProcessTests.WaitForDiagnosticEndpointAsync(process.Id, TimeSpan.FromSeconds(30));
        host.BaseUrl = await host.EnsureListeningUrlAsync(TimeSpan.FromSeconds(30));
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
            catch
            {
                // best-effort
            }
        }

        _process.Dispose();
    }

    private void PumpOutput()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _process.StandardOutput;
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    var idx = line.IndexOf("Now listening on:", StringComparison.Ordinal);
                    if (idx >= 0 && !_listeningUrlTcs.Task.IsCompleted)
                    {
                        _listeningUrlTcs.TrySetResult(line[(idx + "Now listening on:".Length)..].Trim());
                    }
                }
            }
            catch
            {
                // best-effort
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _process.StandardError;
                while (await reader.ReadLineAsync() is not null) { }
            }
            catch
            {
                // best-effort
            }
        });
    }

    private async Task<string> EnsureListeningUrlAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var url = await _listeningUrlTcs.Task.WaitAsync(cts.Token);
            await LiveCoreClrProcessTests.WaitForHttpReadyAsync(url, timeout, _sampleName == "BadCodeSample" ? "/" : "/weatherforecast");
            return url;
        }
        catch (OperationCanceledException)
        {
            throw SkipException.ForReason($"{_sampleName} did not advertise an HTTP listening URL within the timeout.");
        }
    }
}

/// <summary>Thrown to skip a test (in lieu of pulling a separate Skippable package).</summary>
public sealed class SkipException : Exception
{
    private SkipException(string reason) : base(reason)
    {
    }

    public static SkipException ForReason(string reason) => new(reason);
}

[CollectionDefinition("LiveProcess", DisableParallelization = true)]
public class LiveProcessCollection;
