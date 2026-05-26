using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.CpuSampling;
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
///   <item><description>counters / exception-snapshot / gc-events / event-source / activities / log-snapshot / jit-snapshot / threadpool-snapshot / contention-snapshot / db-snapshot → any of <c>read-counters</c> or <c>eventpipe</c> (matches <c>query_collection</c>)</description></item>
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
    internal const string DiffView = "diff";

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
        "fragmentation | static-fields | delegate-targets | duplicate-strings | gchandles | object | gcroot | objsize | async); " +
        "`thread-snapshot` → thread views (threads-summary | stack | lock-graph | deadlocks | top-blocked | " +
        "unique-stacks | async-stalls | threadpool); " +
        "`off-cpu-snapshot` → off-CPU views (topStacks | byThread | stack); " +
        "`counters` / `exception-snapshot` / `gc-events` / `event-source` / `activities` / `log-snapshot` / `threadpool-snapshot` / `contention-snapshot` / `db-snapshot` → collection views " +
        "(summary | byProvider | byType | recent | events | pauseHistogram | byEventName | bySource | byOperation | activities | byCategory | byLevel | errors | timeline | hillClimbing | workItemOrigins | byCallSite | byOwner | byCommand | n+1 | connectionPool); " +
        "`cpu-sample` / `allocation-sample` → `call-tree` | `diff`; `heap-snapshot` → `diff` in addition to heap views. `diff` compares the current handle against `baselineHandle`; `call-tree` preserves get_call_tree behaviour with " +
        "rootMethodFilter, maxDepth, maxNodes. " +
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
        [Description("Drilldown handle returned by a prior collector (inspect_heap, collect_thread_snapshot, collect_off_cpu_sample, collect_cpu_sample, collect_allocation_sample, snapshot_counters, collect_exceptions, collect_gc_events, collect_event_source, collect_activities, collect_events(kind=\"logs\"), collect_events(kind=\"threadpool\"), collect_events(kind=\"contention\"), collect_events(kind=\"db\")).")] string handle,
        [Description("Kind-specific view. Heap: top-types|retention-paths|roots-by-kind|finalizer-queue|fragmentation|static-fields|delegate-targets|duplicate-strings|gchandles|object|gcroot|objsize|async|diff. Thread: threads-summary|stack|lock-graph|deadlocks|top-blocked|unique-stacks|async-stalls|threadpool. Off-CPU: topStacks|byThread|stack. Collection: summary|byProvider|byType|recent|events|pauseHistogram|byEventName|bySource|byOperation|activities|byCategory|byLevel|errors|timeline|hillClimbing|workItemOrigins|byCallSite|byOwner|byCommand|n+1|connectionPool. cpu-sample/allocation-sample: call-tree|diff. Omit to use the kind's default view.")] string? view = null,
        [Description("Maximum entries returned by any ranked-list view. Omit to use the per-kind legacy default: 50 for heap / thread / collection, 25 for off-CPU. For view=diff, defaults to 25 rows per bucket.")] int? topN = null,
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
        [Description("Diff view only: baseline handle to compare against the current `handle`. Required for view=diff.")] string? baselineHandle = null,
        [Description("Diff view only: minimum absolute delta percentage required for a row to surface in `Changed`. Defaults to 5.0.")] double minDeltaPct = 5.0,
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
            return HandleExpiredError(IsDiffView(view) ? "Current" : null, handle);
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
                    if (IsDiffView(view))
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, minDeltaPct, topN);
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
                        topN ?? 50,
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
                        topN ?? 50,
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
                        topN ?? 25,
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
                    if (IsDiffView(view))
                    {
                        return TryBuildDiff(handles, handle, lookup.Value, baselineHandle, minDeltaPct, topN);
                    }
                    // get_call_tree exposes a single projection; require either the canonical
                    // `call-tree` view or an omitted value, and reject anything else with a
                    // structured InvalidArgument envelope so a confused caller sees the same
                    // shape it would see from any other kind/view mismatch.
                    if (!string.IsNullOrWhiteSpace(view)
                        && !string.Equals(view, CallTreeView, StringComparison.Ordinal))
                    {
                        return UnknownView(view!, kind, new[] { CallTreeView, DiffView });
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
                {
                    if (!RequireAnyOfScope(principal, ScopeReadCounters, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? null : view;
                    var collection = DiagnosticTools.QueryCollection(
                        handles,
                        principalAccessor,
                        handle,
                        resolvedView,
                        topN ?? 50);
                    return AsObjectEnvelope(collection);
                }

                case CollectionHandleKinds.ExceptionSnapshot:
                case CollectionHandleKinds.GcEvents:
                case CollectionHandleKinds.EventSource:
                case CollectionHandleKinds.Activities:
                case CollectionHandleKinds.LogSnapshot:
                case CollectionHandleKinds.JitSnapshot:
                case CollectionHandleKinds.ThreadPoolSnapshot:
                case CollectionHandleKinds.ContentionSnapshot:
                case CollectionHandleKinds.DbSnapshot:
                {
                    if (!RequireScope(principal, ScopeEventPipe, out var forbidden))
                    {
                        return forbidden!;
                    }
                    // Forward null/empty unchanged so query_collection's own default
                    // (`summary`) kicks in — guarantees byte-equal envelopes with the legacy
                    // call when the caller omits view.
                    var resolvedView = string.IsNullOrWhiteSpace(view) ? null : view;
                    var collection = DiagnosticTools.QueryCollection(
                        handles,
                        principalAccessor,
                        handle,
                        resolvedView,
                        topN ?? 50);
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


    private static bool IsDiffView(string? view)
        => string.Equals(view, DiffView, StringComparison.Ordinal);

    private static DiagnosticResult<object> TryBuildDiff(
        IDiagnosticHandleStore handles,
        string handle,
        HandleLookup currentLookup,
        string? baselineHandle,
        double minDeltaPct,
        int? topN)
    {
        if (string.IsNullOrWhiteSpace(baselineHandle))
        {
            return InvalidArgument(nameof(baselineHandle), "is required when view='diff'");
        }

        if (minDeltaPct < 0)
        {
            return InvalidArgument(nameof(minDeltaPct), "must be >= 0");
        }

        var effectiveTopN = topN ?? 25;
        if (effectiveTopN < 1)
        {
            return InvalidArgument(nameof(topN), "must be >= 1 when view='diff'");
        }

        var baselineLookup = handles.TryGetWithKind(baselineHandle);
        if (baselineLookup is null)
        {
            return HandleExpiredError("Baseline", baselineHandle);
        }

        if (!string.Equals(currentLookup.Kind, baselineLookup.Value.Kind, StringComparison.Ordinal))
        {
            return InvalidKindPair(currentLookup.Kind, baselineLookup.Value.Kind);
        }

        return currentLookup.Kind switch
        {
            DiagnosticTools.HeapSnapshotKind when currentLookup.Artifact is HeapSnapshotArtifact current && baselineLookup.Value.Artifact is HeapSnapshotArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle, handle, SampleDiffer.Compare(baseline, baselineHandle, current, handle, minDeltaPct, effectiveTopN)),

            "cpu-sample" when currentLookup.Artifact is CpuSampleTraceArtifact current && baselineLookup.Value.Artifact is CpuSampleTraceArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle, handle, SampleDiffer.Compare(baseline, baselineHandle, current, handle, minDeltaPct, effectiveTopN)),

            "allocation-sample" when currentLookup.Artifact is AllocationSampleArtifact current && baselineLookup.Value.Artifact is AllocationSampleArtifact baseline
                => WrapDiff(currentLookup.Kind, baselineHandle, handle, SampleDiffer.Compare(baseline.Summary, baselineHandle, current.Summary, handle, minDeltaPct, effectiveTopN)),

            _ => DiagnosticResult.Fail<object>(
                $"Handle kind '{currentLookup.Kind}' cannot be diffed via view='diff'.",
                new DiagnosticError("InvalidArgument", $"Handle kind '{currentLookup.Kind}' cannot be diffed via view='diff'.", "view"))
        };
    }

    private static DiagnosticResult<object> WrapDiff<TKey, TMetric>(string kind, string baselineHandle, string currentHandle, SampleDiff<TKey, TMetric> diff)
        => AsObjectEnvelope(DiagnosticResult.Ok(diff, BuildDiffSummary(kind, baselineHandle, currentHandle, diff)));

    private static string BuildDiffSummary<TKey, TMetric>(string kind, string baselineHandle, string currentHandle, SampleDiff<TKey, TMetric> diff)
        => $"Compared {kind} handle '{currentHandle}' against baseline '{baselineHandle}': {diff.TotalAdded} added, {diff.TotalRemoved} removed, {diff.TotalChanged} changed — verdict {diff.Verdict}.";

    private static DiagnosticResult<object> HandleExpiredError(string? side, string handle)
    {
        var prefix = string.IsNullOrWhiteSpace(side) ? "Handle" : $"{side} handle";
        return DiagnosticResult.Fail<object>(
            $"{prefix} '{handle}' is unknown or expired.",
            new DiagnosticError(
                "HandleExpired",
                "Drill-down handles live ~10min and are invalidated when the target process exits.",
                handle),
            new NextActionHint(ToolName,
                "Re-run the original collector on the same pid to issue a fresh handle.",
                null));
    }

    private static DiagnosticResult<object> InvalidKindPair(string currentKind, string baselineKind)
    {
        var message = $"query_snapshot(view='diff') requires baseline/current handles of the same supported kind. Accepted pairs: heap-snapshot×heap-snapshot, cpu-sample×cpu-sample, allocation-sample×allocation-sample. Received baseline={baselineKind}, current={currentKind}.";
        return DiagnosticResult.Fail<object>(
            message,
            new DiagnosticError("InvalidArgument", message, "baselineHandle"),
            new NextActionHint(ToolName, "Retry with two handles issued by the same collector family.", null));
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
        CollectionHandleKinds.LogSnapshot,
        CollectionHandleKinds.JitSnapshot,
        CollectionHandleKinds.ThreadPoolSnapshot,
        CollectionHandleKinds.DbSnapshot,
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
