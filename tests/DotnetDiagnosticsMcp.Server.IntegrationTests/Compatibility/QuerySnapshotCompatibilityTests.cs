using System.Collections.ObjectModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests.Compatibility;

/// <summary>
/// RFC 0002 §4.1 / issue #207 — dual-entrypoint compatibility tests for
/// <see cref="QuerySnapshotTool"/>. Each test pre-registers an artifact under a stable
/// handle id in a <see cref="FixedHandleStore"/>, invokes both the legacy query/get-call-tree
/// tool and <c>query_snapshot</c> with equivalent parameters, then asserts the rendered
/// envelopes are structurally identical (see <see cref="CompatibilityEnvelopeAssert"/>).
/// Because the deterministic store returns the same artifact instance to both sides, summary
/// strings that embed the handle id remain byte-equal without JSON masking.
/// </summary>
public sealed class QuerySnapshotCompatibilityTests
{
    private const string HandleId = "test-handle-querysnapshot";
    private const int Pid = 4242;
    private static readonly DateTimeOffset At = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    // ---- heap ----

    [Fact]
    public async Task QuerySnapshot_Heap_TopTypes_MatchesLegacyEnvelope()
    {
        var artifact = BuildHeapArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var (store, inspector, redactor, gate) = BuildHeapContext(artifact);
                return await DiagnosticTools.QueryHeapSnapshot(
                    store, inspector, redactor, gate, TestPrincipalAccessors.Root,
                    HandleId, view: "top-types", topN: 5);
            },
            successor: async () =>
            {
                var (store, inspector, redactor, gate) = BuildHeapContext(artifact);
                return await QuerySnapshotTool.QuerySnapshot(
                    store, inspector, redactor, gate, TestPrincipalAccessors.Root,
                    HandleId, view: "top-types", topN: 5);
            });
    }

    [Fact]
    public async Task QuerySnapshot_Heap_DefaultView_MatchesLegacyDefault()
    {
        // Legacy query_heap_snapshot defaults to view="top-types"; the unified tool falls
        // back to the same constant when view is omitted.
        var artifact = BuildHeapArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: async () =>
            {
                var (store, inspector, redactor, gate) = BuildHeapContext(artifact);
                return await DiagnosticTools.QueryHeapSnapshot(
                    store, inspector, redactor, gate, TestPrincipalAccessors.Root, HandleId);
            },
            successor: async () =>
            {
                var (store, inspector, redactor, gate) = BuildHeapContext(artifact);
                return await QuerySnapshotTool.QuerySnapshot(
                    store, inspector, redactor, gate, TestPrincipalAccessors.Root,
                    HandleId, view: null);
            });
    }

    // ---- thread ----

    [Fact]
    public async Task QuerySnapshot_Thread_TopBlocked_MatchesLegacyEnvelope()
    {
        var artifact = BuildThreadArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.QueryThreadSnapshot(
                BuildStore(DiagnosticTools.ThreadSnapshotKind, artifact),
                HandleId, view: "top-blocked", topN: 10)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore(DiagnosticTools.ThreadSnapshotKind, artifact),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: "top-blocked", topN: 10));
    }

    [Fact]
    public async Task QuerySnapshot_Thread_DefaultView_MatchesLegacyDefault()
    {
        var artifact = BuildThreadArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.QueryThreadSnapshot(
                BuildStore(DiagnosticTools.ThreadSnapshotKind, artifact), HandleId)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore(DiagnosticTools.ThreadSnapshotKind, artifact),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root, HandleId));
    }

    // ---- off-cpu ----

    [Fact]
    public async Task QuerySnapshot_OffCpu_TopStacks_MatchesLegacyEnvelope()
    {
        var artifact = BuildOffCpuArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.QueryOffCpuSnapshot(
                BuildStore(DiagnosticTools.OffCpuHandleKind, artifact),
                HandleId, view: "topStacks", topN: 25)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore(DiagnosticTools.OffCpuHandleKind, artifact),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: "topStacks", topN: 25));
    }

    // ---- collection (counters / GC) ----

    [Fact]
    public async Task QuerySnapshot_Collection_Counters_Summary_MatchesLegacyEnvelope()
    {
        var snap = new CounterSnapshot(Pid, At, TimeSpan.FromSeconds(5), new List<CounterValue>
        {
            new("System.Runtime", "cpu-usage", "CPU", 12.5, CounterKind.Mean, "%"),
            new("System.Runtime", "gc-heap-size", "Heap", 100, CounterKind.Mean, "MB"),
        });
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.QueryCollection(
                BuildStore(CollectionHandleKinds.Counters, snap), HandleId, view: null, topN: 50)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore(CollectionHandleKinds.Counters, snap),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: null, topN: 50));
    }

    [Fact]
    public async Task QuerySnapshot_Collection_GcEvents_PauseHistogram_MatchesLegacyEnvelope()
    {
        var events = new[] { 0.5, 5, 50, 500, 1500 }
            .Select(ms => new GcEvent(At, 0, "AllocSmall", "Background", TimeSpan.FromMilliseconds(ms)))
            .ToList();
        var snap = new GcSummary(
            ProcessId: Pid,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(5),
            TotalCollections: events.Count,
            TotalPauseTime: TimeSpan.FromMilliseconds(events.Sum(e => e.PauseDuration.TotalMilliseconds)),
            MaxPauseTime: events.Max(e => e.PauseDuration),
            Generations: new[] { new GenerationStats(0, events.Count) },
            Events: events);
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.QueryCollection(
                BuildStore(CollectionHandleKinds.GcEvents, snap), HandleId, view: "pauseHistogram", topN: 50)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore(CollectionHandleKinds.GcEvents, snap),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: "pauseHistogram", topN: 50));
    }

    // ---- call-tree ----

    [Fact]
    public async Task QuerySnapshot_CallTree_MatchesLegacyEnvelope()
    {
        var artifact = BuildCpuSampleArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.GetCallTree(
                BuildStore("cpu-sample", artifact), HandleId, rootMethodFilter: null, maxDepth: 8, maxNodes: 200)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore("cpu-sample", artifact),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: "call-tree", maxDepth: 8, maxNodes: 200));
    }

    [Fact]
    public async Task QuerySnapshot_CallTree_DefaultView_MatchesLegacyEnvelope()
    {
        // Legacy get_call_tree had no view discriminator; the unified tool accepts both
        // view="call-tree" and view=null and forwards identical args either way.
        var artifact = BuildCpuSampleArtifact();
        await CompatibilityEnvelopeAssert.AssertEnvelopesEqualAsync(
            legacy: () => Task.FromResult(DiagnosticTools.GetCallTree(
                BuildStore("allocation-sample", artifact), HandleId)),
            successor: async () => await QuerySnapshotTool.QuerySnapshot(
                BuildStore("allocation-sample", artifact),
                NoopInspector(), Redactor(), Gate(),
                TestPrincipalAccessors.Root,
                HandleId, view: null));
    }

    // ---- structured-error path ----

    [Fact]
    public async Task QuerySnapshot_UnknownHandle_ReturnsHandleExpired()
    {
        var store = new MemoryDiagnosticHandleStore();
        var result = await QuerySnapshotTool.QuerySnapshot(
            store, NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.Root,
            "no-such-handle", view: "top-types");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("HandleExpired");
    }

    [Fact]
    public async Task QuerySnapshot_EmptyHandle_ReturnsInvalidArgument()
    {
        var store = new MemoryDiagnosticHandleStore();
        var result = await QuerySnapshotTool.QuerySnapshot(
            store, NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.Root,
            handle: " ", view: "top-types");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("handle");
    }

    [Fact]
    public async Task QuerySnapshot_CallTree_UnknownView_ReturnsInvalidArgumentEnvelope()
    {
        var result = await QuerySnapshotTool.QuerySnapshot(
            BuildStore("cpu-sample", BuildCpuSampleArtifact()),
            NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.Root,
            HandleId, view: "bytype");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("InvalidArgument");
        result.Error.Detail.Should().Be("view");
        result.Summary.Should().Contain("call-tree");
    }

    [Fact]
    public async Task QuerySnapshot_UnsupportedHandleKind_ReturnsStructuredEnvelope()
    {
        var result = await QuerySnapshotTool.QuerySnapshot(
            BuildStore("brand-new-collector-kind", new SomeOpaquePayload("not-supported")),
            NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.Root,
            HandleId, view: "summary");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("UnsupportedHandleKind");
    }

    [Fact]
    public async Task QuerySnapshot_Thread_PrincipalMissingPtrace_ReturnsForbidden()
    {
        // Principal carries 'heap-read' only — dispatcher must refuse the thread view.
        var result = await QuerySnapshotTool.QuerySnapshot(
            BuildStore(DiagnosticTools.ThreadSnapshotKind, BuildThreadArtifact()),
            NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.WithScopes("heap-read"),
            HandleId, view: "top-blocked");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Detail.Should().Be("ptrace");
    }

    [Fact]
    public async Task QuerySnapshot_Collection_PrincipalMissingBothScopes_ReturnsForbidden()
    {
        var snap = new CounterSnapshot(Pid, At, TimeSpan.FromSeconds(1), new List<CounterValue>());
        var result = await QuerySnapshotTool.QuerySnapshot(
            BuildStore(CollectionHandleKinds.Counters, snap),
            NoopInspector(), Redactor(), Gate(),
            TestPrincipalAccessors.WithScopes("heap-read"),
            HandleId, view: "summary");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("Forbidden");
        result.Error.Detail.Should().Be("read-counters|eventpipe");
    }

    // ---- helpers ----

    private static (FixedHandleStore Store, StubDumpInspector Inspector, SensitiveDataRedactor Redactor, SensitiveValueGate Gate)
        BuildHeapContext(HeapSnapshotArtifact artifact)
        => (BuildStore(DiagnosticTools.HeapSnapshotKind, artifact), new StubDumpInspector(), Redactor(), Gate());

    private static FixedHandleStore BuildStore(string kind, object artifact)
        => new(HandleId, Pid, kind, artifact);

    private static IDumpInspector NoopInspector() => new StubDumpInspector();
    private static SensitiveDataRedactor Redactor() => new(null);
    private static SensitiveValueGate Gate() => new(null);

    private static HeapSnapshotArtifact BuildHeapArtifact()
    {
        var runtime = new DumpRuntimeInfo(".NETCoreApp", "10.0.0", "x64", IsServerGC: false, HeapCount: 1);
        var heap = new DumpHeapSummary(1024, 128, 256, 512, 64, 32, 2048);
        var stat = new TypeStat("System.String", "System.Private.CoreLib", 10, 1024, 100.0);
        IReadOnlyList<TypeStat> by = new ReadOnlyCollection<TypeStat>(new[] { stat });
        return new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Dump,
            ProcessId: Pid,
            CapturedAt: At,
            WalkDuration: TimeSpan.FromMilliseconds(42),
            Runtime: runtime,
            Heap: heap,
            TopTypesByBytes: by,
            TopTypesByInstances: by);
    }

    private static ThreadSnapshotArtifact BuildThreadArtifact()
    {
        var frame = new ManagedStackFrame(
            Kind: "Managed",
            DisplayName: "Lib.Method",
            TypeFullName: "Lib.Class",
            ModuleName: "Lib",
            InstructionPointer: 0x6000UL,
            StackPointer: 0UL);
        var thread = new ManagedThread(
            ManagedThreadId: 1,
            OSThreadId: 100u,
            Address: 0x1000UL,
            State: "Wait",
            IsAlive: true,
            IsBackground: false,
            IsFinalizer: false,
            IsGc: false,
            IsThreadpoolWorker: false,
            LockCount: 0u,
            CurrentExceptionType: null,
            TopFrameMethod: frame.DisplayName,
            Frames: new[] { frame })
        {
            IsLikelyBlocked = true,
        };
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: Pid,
            CapturedAt: At,
            WalkDuration: TimeSpan.FromMilliseconds(10),
            RuntimeName: "CoreClr",
            RuntimeVersion: "10.0.0",
            Threads: new[] { thread },
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "clrmd-thread-walk",
        };
    }

    private static OffCpuSnapshotArtifact BuildOffCpuArtifact()
    {
        var frame = new OffCpuFrame("kernel", "futex_wait");
        var stack = new OffCpuStackHotspot(
            LeafFrame: "futex_wait",
            OffCpuMicros: 1000,
            OccurrenceCount: 3,
            DominantState: "S",
            Stack: new[] { frame });
        var threadView = new OffCpuThreadView(
            Tid: 100,
            ThreadName: "dotnet",
            OffCpuMicros: 1000,
            SwitchCount: 3,
            TopBlockingLeaf: "futex_wait");
        return new OffCpuSnapshotArtifact(
            ProcessId: Pid,
            StartedAt: At,
            Duration: TimeSpan.FromSeconds(10),
            TotalOffCpuMicros: 1000,
            SchedSwitches: 3,
            Stacks: new[] { stack },
            Threads: new[] { threadView },
            SymbolSource: "stub");
    }

    private static CpuSampleTraceArtifact BuildCpuSampleArtifact()
    {
        var leaf = new CallTreeNode(new SampledFrame("Lib", "Lib.Leaf"), 10, 10, Array.Empty<CallTreeNode>());
        var root = new CallTreeNode(new SampledFrame("Lib", "Lib.Root"), 10, 0, new[] { leaf });
        return new CpuSampleTraceArtifact(Pid, At, TimeSpan.FromSeconds(10), 10, root);
    }

    private sealed record SomeOpaquePayload(string Label);

    /// <summary>
    /// Single-slot deterministic store. Lets both the legacy and successor invocations
    /// dereference exactly the same artifact under a known handle id so summary strings
    /// embedding the id stay byte-equal across the dual entrypoints.
    /// </summary>
    private sealed class FixedHandleStore : IDiagnosticHandleStore
    {
        private readonly DiagnosticHandle _handle;
        private readonly object _artifact;

        public FixedHandleStore(string id, int pid, string kind, object artifact)
        {
            _handle = new DiagnosticHandle(id, At.AddYears(50), pid, kind);
            _artifact = artifact;
        }

        public DiagnosticHandle Register(int processId, string kind, object artifact, TimeSpan ttl, bool evictWhenProcessExits = true, HandleOrigin? origin = null)
            => throw new NotSupportedException("FixedHandleStore exposes one preconfigured slot only.");

        public T? TryGet<T>(string handle) where T : class
            => handle == _handle.Id ? _artifact as T : null;

        public HandleLookup? TryGetWithKind(string handle)
            => handle == _handle.Id ? new HandleLookup(_handle, _artifact) : null;

        public bool Invalidate(string handle) => false;

        public int InvalidateForProcess(int processId) => 0;
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapSnapshotArtifact> InspectLiveAsync(int processId, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapObjectInspection> InspectObjectAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapGcRootInspection> InspectGcRootAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<HeapObjectSizeInspection> InspectObjectSizeAsync(HeapSnapshotArtifact snapshot, ulong address, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
