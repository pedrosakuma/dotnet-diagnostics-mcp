using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// B5.4 — every legacy <c>Diagnostics:Allow*</c> path that unlocks a sensitive
/// operation while the matching RFC 0001 modifier scope is absent on the bearer
/// principal must fire a once-per-process deprecation warning. The allowlist
/// policies themselves remain in place; only the implicit deployment-wide
/// "every caller can do X" pattern is being deprecated.
///
/// For each gate the matrix is the same:
///  - scope-only path: no warning (the scope-first OR-branch authorised the call).
///  - legacy path: warning fires exactly once across N invocations.
///  - neither: tool denies the call (existing B4 behaviour, unchanged).
///  - root principal alone: HasExplicitScope is literal — does NOT auto-grant the
///    modifier scope, so the legacy-path warning still fires when the root token
///    uses an allowlisted resource.
/// </summary>
public sealed class LegacyDiagnosticsFlagDeprecationTests
{
    private const string RemotePath = @"srv*c:\sym*https://msdl.microsoft.com/download/symbols";

    // ---- sensitive-heap-read (QueryHeapSnapshot duplicate-strings) ----

    [Fact]
    public async Task SensitiveHeap_ScopeOnly_DoesNotWarn()
    {
        var (provider, deprecation) = NewDeprecation();
        var (store, handle) = NewSnapshotWithDuplicateStrings();

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, NoopInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null),
            TestPrincipalAccessors.WithScopes("heap-read", "sensitive-heap-read"),
            handle, view: "duplicate-strings", topN: 10,
            includeSensitiveValues: true,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SensitiveHeapValuesWarning).Should().Be(0);
    }

    [Fact]
    public async Task SensitiveHeap_LegacyFlagOnly_WarnsExactlyOnce()
    {
        var (provider, deprecation) = NewDeprecation();
        var (store, handle) = NewSnapshotWithDuplicateStrings();
        var options = new SecurityOptions { AllowSensitiveHeapValues = true };
        // 'heap-read' satisfies the [RequireScope] gate on the tool; the principal
        // deliberately lacks 'sensitive-heap-read', so the legacy flag is the path
        // that unlocks emission.
        var principal = TestPrincipalAccessors.WithScopes("heap-read");

        for (int i = 0; i < 3; i++)
        {
            var result = await DiagnosticTools.QueryHeapSnapshot(
                store, NoopInspector(), new SensitiveDataRedactor(options), new SensitiveValueGate(options),
                principal, handle, view: "duplicate-strings", topN: 10,
                includeSensitiveValues: true,
                deprecation: deprecation);
            result.Error.Should().BeNull();
        }

        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SensitiveHeapValuesWarning).Should().Be(1);
    }

    [Fact]
    public async Task SensitiveHeap_NeitherFlagNorScope_DoesNotWarn()
    {
        // Sensitive values are not emitted at all → no warning fires (the gate is
        // closed, the legacy flag is OFF, the scope is absent).
        var (provider, deprecation) = NewDeprecation();
        var (store, handle) = NewSnapshotWithDuplicateStrings();

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, NoopInspector(), new SensitiveDataRedactor(null), new SensitiveValueGate(null),
            TestPrincipalAccessors.WithScopes("heap-read"),
            handle, view: "duplicate-strings", topN: 10,
            includeSensitiveValues: true,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        result.Data!.DuplicateStrings![0].Preview.Should().Be(SensitiveDataRedactor.MetadataOnlyPlaceholder);
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SensitiveHeapValuesWarning).Should().Be(0);
    }

    [Fact]
    public async Task SensitiveHeap_RootPrincipalAlone_DoesNotAutoGrantModifier()
    {
        // Root principal does NOT carry 'sensitive-heap-read' literally — the
        // HasExplicitScope contract (BearerPrincipal §70-82) is deliberately
        // strict. The legacy flag therefore IS the bypass mechanism and the
        // warning must fire.
        var (provider, deprecation) = NewDeprecation();
        var (store, handle) = NewSnapshotWithDuplicateStrings();
        var options = new SecurityOptions { AllowSensitiveHeapValues = true };

        var result = await DiagnosticTools.QueryHeapSnapshot(
            store, NoopInspector(), new SensitiveDataRedactor(options), new SensitiveValueGate(options),
            TestPrincipalAccessors.Root,
            handle, view: "duplicate-strings", topN: 10,
            includeSensitiveValues: true,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SensitiveHeapValuesWarning).Should().Be(1);
    }

    // ---- eventsource-any (CollectEventSource) ----

    [Fact]
    public async Task EventSourceAllowlist_AllowlistedProvider_ScopeOnly_DoesNotWarn()
    {
        var (provider, deprecation) = NewDeprecation();
        var collector = NewCollector();

        var result = await DiagnosticTools.CollectEventSource(
            collector, ToolGuardTests.EchoResolver(), new MemoryDiagnosticHandleStore(),
            new EventSourceAllowlist(null), new SensitiveValueGate(null),
            TestPrincipalAccessors.WithScopes("event-source-collect", "eventsource-any"),
            providerName: "System.Net.Http", processId: 4242, durationSeconds: 1,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.EventSourceAllowlistWarning).Should().Be(0);
    }

    [Fact]
    public async Task EventSourceAllowlist_AllowlistedProvider_NoScope_WarnsExactlyOnce()
    {
        var (provider, deprecation) = NewDeprecation();

        for (int i = 0; i < 3; i++)
        {
            var collector = NewCollector();
            var result = await DiagnosticTools.CollectEventSource(
                collector, ToolGuardTests.EchoResolver(), new MemoryDiagnosticHandleStore(),
                new EventSourceAllowlist(null), new SensitiveValueGate(null),
                TestPrincipalAccessors.WithScopes("event-source-collect"),
                providerName: "System.Net.Http", processId: 4242, durationSeconds: 1,
                deprecation: deprecation);
            result.Error.Should().BeNull();
        }

        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.EventSourceAllowlistWarning).Should().Be(1);
    }

    [Fact]
    public async Task EventSourceUnsafe_LegacyFlagOnly_WarnsSensitiveHeapDeprecation()
    {
        // unsafeProvider path while the principal lacks 'eventsource-any' →
        // the deprecating flag that unlocked the call is AllowSensitiveHeapValues,
        // so the *sensitive-heap-read* warning is the one that fires (per RFC §7.3
        // — that flag is the only one truly going away).
        var (provider, deprecation) = NewDeprecation();
        var collector = NewCollector();
        var options = new SecurityOptions { AllowSensitiveHeapValues = true };

        var result = await DiagnosticTools.CollectEventSource(
            collector, ToolGuardTests.EchoResolver(), new MemoryDiagnosticHandleStore(),
            new EventSourceAllowlist(null), new SensitiveValueGate(options),
            TestPrincipalAccessors.WithScopes("event-source-collect"),
            providerName: "My.Custom.Source", processId: 4242, durationSeconds: 1,
            unsafeProvider: true,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SensitiveHeapValuesWarning).Should().Be(1);
    }

    [Fact]
    public async Task EventSourceAllowlist_RootPrincipalAlone_DoesNotAutoGrantModifier()
    {
        var (provider, deprecation) = NewDeprecation();
        var collector = NewCollector();

        var result = await DiagnosticTools.CollectEventSource(
            collector, ToolGuardTests.EchoResolver(), new MemoryDiagnosticHandleStore(),
            new EventSourceAllowlist(null), new SensitiveValueGate(null),
            TestPrincipalAccessors.Root,
            providerName: "System.Net.Http", processId: 4242, durationSeconds: 1,
            deprecation: deprecation);

        result.Error.Should().BeNull();
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.EventSourceAllowlistWarning).Should().Be(1);
    }

    // ---- symbols-remote (ValidateSymbolPath via InspectDump) ----

    [Fact]
    public async Task SymbolServerAllowlist_RemoteHost_ScopeOnly_DoesNotWarn()
    {
        var (provider, deprecation) = NewDeprecation();
        var inspector = new StubDumpInspector();

        var result = await DiagnosticTools.InspectDump(
            inspector, new MemoryDiagnosticHandleStore(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.WithScopes("heap-read", "symbols-remote"),
            dumpFilePath: "/does/not/exist.dmp",
            symbolPath: RemotePath,
            deprecation: deprecation);

        // ValidateSymbolPath returns null (scope-first short-circuit). The dump
        // inspector then fails because the file does not exist — that's outside
        // the gate we're asserting on; what matters is that NO warning was emitted
        // and no SymbolServerNotAllowed denial was produced.
        result.Error?.Kind.Should().NotBe("SymbolServerNotAllowed");
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SymbolServerAllowlistWarning).Should().Be(0);
    }

    [Fact]
    public async Task SymbolServerAllowlist_RemoteHost_AllowlistOnly_WarnsExactlyOnce()
    {
        var (provider, deprecation) = NewDeprecation();
        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };

        for (int i = 0; i < 3; i++)
        {
            var inspector = new StubDumpInspector();
            var result = await DiagnosticTools.InspectDump(
                inspector, new MemoryDiagnosticHandleStore(),
                new SymbolServerAllowlist(options),
                TestPrincipalAccessors.WithScopes("heap-read"),
                dumpFilePath: "/does/not/exist.dmp",
                symbolPath: RemotePath,
                deprecation: deprecation);

            result.Error?.Kind.Should().NotBe("SymbolServerNotAllowed");
        }

        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SymbolServerAllowlistWarning).Should().Be(1);
    }

    [Fact]
    public async Task SymbolServerAllowlist_RemoteHost_Denied_DoesNotWarn()
    {
        // Allowlist denies the host outright → tool returns SymbolServerNotAllowed
        // and no warning fires (nothing was bypassed).
        var (provider, deprecation) = NewDeprecation();
        var inspector = new StubDumpInspector();

        var result = await DiagnosticTools.InspectDump(
            inspector, new MemoryDiagnosticHandleStore(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.WithScopes("heap-read"),
            dumpFilePath: "/does/not/exist.dmp",
            symbolPath: RemotePath,
            deprecation: deprecation);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SymbolServerAllowlistWarning).Should().Be(0);
        inspector.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task SymbolServerAllowlist_LocalPath_DoesNotWarn()
    {
        // Pure local path → no remote host to gate, no warning even when the
        // allowlist is permissive.
        var (provider, deprecation) = NewDeprecation();
        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };
        var inspector = new StubDumpInspector();

        var result = await DiagnosticTools.InspectDump(
            inspector, new MemoryDiagnosticHandleStore(),
            new SymbolServerAllowlist(options),
            TestPrincipalAccessors.WithScopes("heap-read"),
            dumpFilePath: "/does/not/exist.dmp",
            symbolPath: "/srv/symbols",
            deprecation: deprecation);

        result.Error?.Kind.Should().NotBe("SymbolServerNotAllowed");
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SymbolServerAllowlistWarning).Should().Be(0);
    }

    [Fact]
    public async Task SymbolServerAllowlist_RemoteHost_RootPrincipalAlone_DoesNotAutoGrantModifier()
    {
        var (provider, deprecation) = NewDeprecation();
        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };
        var inspector = new StubDumpInspector();

        var result = await DiagnosticTools.InspectDump(
            inspector, new MemoryDiagnosticHandleStore(),
            new SymbolServerAllowlist(options),
            TestPrincipalAccessors.Root,
            dumpFilePath: "/does/not/exist.dmp",
            symbolPath: RemotePath,
            deprecation: deprecation);

        result.Error?.Kind.Should().NotBe("SymbolServerNotAllowed");
        WarningsFor(provider, LegacyDiagnosticsFlagDeprecation.SymbolServerAllowlistWarning).Should().Be(1);
    }

    // ---- helpers ----

    private static (ListLoggerProvider Provider, LegacyDiagnosticsFlagDeprecation Service) NewDeprecation()
    {
        var provider = new ListLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(provider).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger<LegacyDiagnosticsFlagDeprecation>();
        return (provider, new LegacyDiagnosticsFlagDeprecation(logger));
    }

    private static int WarningsFor(ListLoggerProvider provider, string message) =>
        provider.Records.Count(r => r.Level == LogLevel.Warning && r.Message == message);

    private static (MemoryDiagnosticHandleStore Store, string Handle) NewSnapshotWithDuplicateStrings()
    {
        var store = new MemoryDiagnosticHandleStore();
        var artifact = new HeapSnapshotArtifact(
            Origin: HeapSnapshotOrigin.Live,
            ProcessId: 123,
            CapturedAt: DateTimeOffset.UtcNow,
            WalkDuration: TimeSpan.FromMilliseconds(50),
            Runtime: new DumpRuntimeInfo("CoreCLR", "10.0.0", "X64", IsServerGC: false, HeapCount: 1),
            Heap: new DumpHeapSummary(1024, 0, 0, 1024, 0, 0, 1024),
            TopTypesByBytes: [],
            TopTypesByInstances: [])
        {
            DuplicateStrings = new[] { new DuplicateStringStat("hello, world", 12, 5, 256, false) },
        };
        var handle = store.Register(123, "heap-snapshot", artifact, TimeSpan.FromMinutes(10));
        return (store, handle.Id);
    }

    private static IDumpInspector NoopInspector() => new StubDumpInspector();

    private static IEventSourceCollector NewCollector() => new RecordingCollector();

    private sealed class RecordingCollector : IEventSourceCollector
    {
        public Task<EventSourceCapture> CaptureAsync(int processId, string providerName, TimeSpan duration, long keywords = -1, int eventLevel = 5, int maxEvents = 200, CancellationToken cancellationToken = default)
            => Task.FromResult(new EventSourceCapture(processId, providerName, DateTimeOffset.UtcNow, duration, 0, Array.Empty<CapturedEvent>()));
    }

    private sealed class StubDumpInspector : IDumpInspector
    {
        public int Invocations { get; private set; }
        public Task<HeapSnapshotArtifact> InspectAsync(string dumpFilePath, DumpInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            // Surface a failure shape consistent with "file not found" — the
            // test only cares whether the symbol-path gate was hit, not whether
            // the dump itself parses.
            throw new FileNotFoundException("stub dump inspector reached", dumpFilePath);
        }
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
