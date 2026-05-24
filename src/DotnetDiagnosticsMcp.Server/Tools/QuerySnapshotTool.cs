using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 §4.1 / issue #207 — single drilldown surface that subsumes the five
/// handle-based query tools (<c>query_heap_snapshot</c>, <c>query_thread_snapshot</c>,
/// <c>query_off_cpu_snapshot</c>, <c>query_collection</c>, <c>get_call_tree</c>) behind
/// one <c>(handle, view)</c> contract. The dispatcher reads the artifact kind recorded
/// against the supplied handle in <see cref="IDiagnosticHandleStore"/> and forwards
/// to the matching legacy implementation so the response envelopes stay byte-identical
/// (asserted by <c>QuerySnapshotCompatibilityTests</c>). The legacy tools remain
/// registered through the deprecation window.
/// </summary>
/// <remarks>
/// <para><b>Authorization (RFC §4.1).</b> The static gate accepts any drilldown-capable
/// bearer (<c>RequireAnyScope</c> over the union of legacy scopes). After resolving the
/// handle kind we re-apply the exact legacy scope at runtime so the
/// <c>(handle family, origin, view)</c> boundary is preserved verbatim:</para>
/// <list type="bullet">
///   <item><description>heap-snapshot → <c>heap-read</c></description></item>
///   <item><description>thread-snapshot → <c>ptrace</c></description></item>
///   <item><description>off-cpu-snapshot → <c>eventpipe</c></description></item>
///   <item><description>cpu-sample / allocation-sample (call-tree view) → <c>investigation-export</c></description></item>
///   <item><description>counters / exception-snapshot / gc-events / event-source / activities → any of <c>read-counters</c> or <c>eventpipe</c> (matches <c>query_collection</c>)</description></item>
/// </list>
/// <para>Unknown handle kinds, unknown views and parameter shape violations all return
/// the structured <c>InvalidArgument</c> / <c>UnsupportedHandleKind</c> envelopes the
/// legacy tools emit — never a 500.</para>
/// </remarks>
[McpServerToolType]
public sealed class QuerySnapshotTool
{
    internal const string ToolName = "query_snapshot";

    // View constants accepted for the cpu-sample / allocation-sample handle kinds.
    // The legacy `get_call_tree` tool exposed no view discriminator (it had exactly one
    // projection); the unified tool exposes that projection as the canonical
    // `call-tree` view so the (handle, view) contract is uniform across kinds.
    internal const string CallTreeView = "call-tree";

    // Legacy default views, mirrored so unified callers can omit `view` and still get
    // the same projection the kind's legacy tool returned by default.
    internal const string DefaultHeapView = "top-types";
    internal const string DefaultThreadView = "top-blocked";
    internal const string DefaultOffCpuView = "topStacks";
    internal const string DefaultCollectionView = "summary";

    // Scopes (mirrored from RFC §4.1 / the legacy [RequireScope] attributes).
    private const string ScopeHeapRead = "heap-read";
    private const string ScopePtrace = "ptrace";
    private const string ScopeEventPipe = "eventpipe";
    private const string ScopeReadCounters = "read-counters";
    private const string ScopeInvestigationExport = "investigation-export";

    [RequireAnyScope(
        ScopeReadCounters,
        ScopeEventPipe,
        ScopeHeapRead,
        ScopePtrace,
        ScopeInvestigationExport)]
    [McpServerTool(
        Name = ToolName,
        Title = "Drill into any drilldown snapshot (heap / thread / off-CPU / collection / call-tree)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Single drilldown verb that dispatches on the handle's recorded artifact kind: " +
        "`heap-snapshot` → heap views (top-types | retention-paths | roots-by-kind | finalizer-queue | " +
        "fragmentation | static-fields | delegate-targets | duplicate-strings | object | gcroot | objsize | async); " +
        "`thread-snapshot` → thread views (threads-summary | stack | lock-graph | deadlocks | top-blocked | " +
        "unique-stacks | threadpool); " +
        "`off-cpu-snapshot` → off-CPU views (topStacks | byThread | stack); " +
        "`counters` / `exception-snapshot` / `gc-events` / `event-source` / `activities` → collection views " +
        "(summary | byProvider | byType | recent | events | pauseHistogram | byEventName | bySource | byOperation | activities); " +
        "`cpu-sample` / `allocation-sample` → `call-tree` (the only view; preserves get_call_tree behaviour with " +
        "rootMethodFilter, maxDepth, maxNodes). " +
        "Unknown handle kinds, unknown views and parameter-shape violations return structured InvalidArgument " +
        "envelopes — never a 500. Authorization is preserved per kind: heap-read for heap, ptrace for thread, " +
        "eventpipe for off-CPU, investigation-export for call-tree, and read-counters|eventpipe for collection. " +
        "Supersedes the deprecated query_heap_snapshot, query_thread_snapshot, query_off_cpu_snapshot, " +
        "query_collection and get_call_tree tools (RFC 0002 §4.1 / #207).")]
    public static async Task<DiagnosticResult<object>> QuerySnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("Drilldown handle returned by a prior collector (inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, snapshot_counters, collect_exceptions, collect_gc_events, collect_event_source, collect_activities).")] string handle,
        [Description("Kind-specific view. Heap: top-types|retention-paths|roots-by-kind|finalizer-queue|fragmentation|static-fields|delegate-targets|duplicate-strings|object|gcroot|objsize|async. Thread: threads-summary|stack|lock-graph|deadlocks|top-blocked|unique-stacks|threadpool. Off-CPU: topStacks|byThread|stack. Collection: summary|byProvider|byType|recent|events|pauseHistogram|byEventName|bySource|byOperation|activities. cpu-sample/allocation-sample: call-tree. Omit to use the kind's default view.")] string? view = null,
        [Description("Maximum entries returned by any ranked-list view. Defaults to 50 (the legacy collection / heap / thread default). Off-CPU callers wanting the legacy `topN=25` must pass it explicitly.")] int topN = 50,
        [Description("Heap view='top-types' only: ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("Heap view='retention-paths' only: case-insensitive substring matched against TypeFullName.")] string? typeFullName = null,
        [Description("Heap view='object'/'gcroot'/'objsize' only: managed object address (decimal or 0x-prefixed hex).")] string? address = null,
        [Description("Heap views 'duplicate-strings' / 'object' only: opt-in to raw string content / field-value previews (gated by `Diagnostics:AllowSensitiveHeapValues` AND `sensitive-heap-read` scope per RFC 0001 §2.4).")] bool includeSensitiveValues = false,
        [Description("Thread view='stack' only: thread id (ManagedThreadId for CoreCLR snapshots, OS TID for linux-native-stack snapshots).")] int? threadId = null,
        [Description("Thread view='unique-stacks' only: number of top frames folded into the signature hash. Defaults to 20.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("Thread view='unique-stacks' only: drop groups with fewer than this many threads. Defaults to 1.")] int minCount = 1,
        [Description("Off-CPU view='stack' only: 1-based rank of the stack in the top-stacks list.")] int? stackRank = null,
        [Description("Call-tree (cpu-sample / allocation-sample) only: optional case-insensitive substring; the tree is re-rooted at the highest-ranked frame whose method name contains this text.")] string? rootMethodFilter = null,
        [Description("Call-tree only: maximum tree depth from the root. Must be >= 1. Defaults to 8.")] int maxDepth = 8,
        [Description("Call-tree only: approximate cap on the number of nodes returned (top children at each level). Must be >= 1. Defaults to 200.")] int maxNodes = 200,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle))
        {
            return InvalidArgument(nameof(handle), "is required");
        }

        var lookup = handles.TryGetWithKind(handle);
        if (lookup is null)
        {
            return DiagnosticResult.Fail<object>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError(
                    "HandleExpired",
                    "Drill-down handles live ~10min and are invalidated when the target process exits.",
                    handle),
                new NextActionHint(ToolName,
                    "Re-run the original collector on the same pid to issue a fresh handle.",
                    null));
        }

        var kind = lookup.Value.Kind;
        var principal = principalAccessor.Current;

        switch (kind)
        {
            case DiagnosticTools.HeapSnapshotKind:
            {
                if (!RequireScope(principal, ScopeHeapRead, out var forbidden))
                {
                    return forbidden!;
                }
                var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultHeapView : view!;
                var heap = await DiagnosticTools.QueryHeapSnapshot(
                    handles,
                    inspector,
                    redactor,
                    sensitiveGate,
                    principalAccessor,
                    handle,
                    resolvedView,
                    topN,
                    rankBy,
                    typeFullName,
                    address,
                    includeSensitiveValues,
                    deprecation,
                    cancellationToken).ConfigureAwait(false);
                return AsObjectEnvelope(heap);
            }

            case DiagnosticTools.ThreadSnapshotKind:
            {
                if (!RequireScope(principal, ScopePtrace, out var forbidden))
                {
                    return forbidden!;
                }
                var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultThreadView : view!;
                var thread = DiagnosticTools.QueryThreadSnapshot(
                    handles,
                    handle,
                    resolvedView,
                    threadId,
                    topN,
                    framesToHash,
                    minCount);
                return AsObjectEnvelope(thread);
            }

            case DiagnosticTools.OffCpuHandleKind:
            {
                if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                {
                    return forbidden!;
                }
                var resolvedView = string.IsNullOrWhiteSpace(view) ? DefaultOffCpuView : view!;
                var offCpu = DiagnosticTools.QueryOffCpuSnapshot(
                    handles,
                    handle,
                    resolvedView,
                    topN,
                    stackRank);
                return AsObjectEnvelope(offCpu);
            }

            case "cpu-sample":
            case "allocation-sample":
            {
                if (!RequireScope(principal, ScopeInvestigationExport, out var forbidden))
                {
                    return forbidden!;
                }
                // get_call_tree exposes a single projection; require either the canonical
                // `call-tree` view or an omitted value, and reject anything else with a
                // structured InvalidArgument envelope so a confused caller sees the same
                // shape it would see from any other kind/view mismatch.
                if (!string.IsNullOrWhiteSpace(view)
                    && !string.Equals(view, CallTreeView, StringComparison.Ordinal))
                {
                    return UnknownView(view!, kind, new[] { CallTreeView });
                }
                var callTree = DiagnosticTools.GetCallTree(
                    handles,
                    handle,
                    rootMethodFilter,
                    maxDepth,
                    maxNodes);
                return AsObjectEnvelope(callTree);
            }

            case CollectionHandleKinds.Counters:
            case CollectionHandleKinds.ExceptionSnapshot:
            case CollectionHandleKinds.GcEvents:
            case CollectionHandleKinds.EventSource:
            case CollectionHandleKinds.Activities:
            {
                if (!RequireAnyOfScope(principal, ScopeReadCounters, ScopeEventPipe, out var forbidden))
                {
                    return forbidden!;
                }
                // Forward null/empty unchanged so query_collection's own default
                // (`summary`) kicks in — guarantees byte-equal envelopes with the legacy
                // call when the caller omits view.
                var resolvedView = string.IsNullOrWhiteSpace(view) ? null : view;
                var collection = DiagnosticTools.QueryCollection(
                    handles,
                    handle,
                    resolvedView,
                    topN);
                return AsObjectEnvelope(collection);
            }

            default:
                return DiagnosticResult.Fail<object>(
                    $"Handle '{handle}' is of kind '{kind}' which query_snapshot does not support.",
                    new DiagnosticError(
                        "UnsupportedHandleKind",
                        $"query_snapshot dispatches over kinds: {string.Join(", ", SupportedKinds)}.",
                        kind),
                    new NextActionHint(ToolName,
                        "Use a handle issued by inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, or any of the EventPipe collectors.",
                        null));
        }
    }

    private static readonly string[] SupportedKinds =
    {
        DiagnosticTools.HeapSnapshotKind,
        DiagnosticTools.ThreadSnapshotKind,
        DiagnosticTools.OffCpuHandleKind,
        "cpu-sample",
        "allocation-sample",
        CollectionHandleKinds.Counters,
        CollectionHandleKinds.ExceptionSnapshot,
        CollectionHandleKinds.GcEvents,
        CollectionHandleKinds.EventSource,
        CollectionHandleKinds.Activities,
    };

    /// <summary>
    /// Projects a typed <see cref="DiagnosticResult{T}"/> into the polymorphic
    /// <c>DiagnosticResult&lt;object&gt;</c> shape <c>query_snapshot</c> exposes, preserving
    /// every envelope field. <c>System.Text.Json</c> serializes polymorphically on
    /// <c>object</c> properties (default since .NET 6), so the resulting JSON is byte-equal
    /// to the legacy envelope — what makes <c>QuerySnapshotCompatibilityTests</c> pass.
    /// </summary>
    private static DiagnosticResult<object> AsObjectEnvelope<T>(DiagnosticResult<T> source) where T : class
        => new(source.Summary, source.Hints, source.Error)
        {
            Data = source.Data,
            Handle = source.Handle,
            HandleExpiresAt = source.HandleExpiresAt,
            ResolvedProcess = source.ResolvedProcess,
        };

    private static bool RequireScope(BearerPrincipal? principal, string scope, out DiagnosticResult<object>? failure)
    {
        if (principal is null || principal.HasScope(scope))
        {
            failure = null;
            return true;
        }

        failure = Forbidden(scope, $"requires scope '{scope}'");
        return false;
    }

    private static bool RequireAnyOfScope(BearerPrincipal? principal, string a, string b, out DiagnosticResult<object>? failure)
    {
        if (principal is null || principal.HasScope(a) || principal.HasScope(b))
        {
            failure = null;
            return true;
        }

        failure = Forbidden($"{a}|{b}", $"requires one of scope '{a}' or '{b}'");
        return false;
    }

    private static DiagnosticResult<object> Forbidden(string requiredScope, string requirement)
    {
        var message = $"forbidden: tool '{ToolName}' {requirement}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("Forbidden", message, requiredScope));
    }

    private static DiagnosticResult<object> InvalidArgument(string parameterName, string requirement)
    {
        var message = $"Argument '{parameterName}' {requirement}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, parameterName),
            new NextActionHint(ToolName,
                "Re-issue query_snapshot with valid arguments — handle is required.",
                null));
    }

    private static DiagnosticResult<object> UnknownView(string view, string kind, string[] allowed)
    {
        var allowedRendered = allowed.Length == 0 ? "(none)" : string.Join(", ", allowed);
        var message = $"View '{view}' is not defined for kind '{kind}'. Allowed: {allowedRendered}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, "view"),
            new NextActionHint(ToolName,
                "Retry with one of the allowed views for this handle kind.",
                new Dictionary<string, object?>
                {
                    ["view"] = allowed.Length > 0 ? allowed[0] : null,
                }));
    }
}
