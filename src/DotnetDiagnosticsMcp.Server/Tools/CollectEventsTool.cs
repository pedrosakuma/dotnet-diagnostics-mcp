using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Server.Diagnostics;
using DotnetDiagnosticsMcp.Server.Security;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 §4.5 consolidation: single MCP entry-point for the EventPipe collector family
/// (counters, exceptions, GC, EventSource, ActivitySource). Delegates to the legacy
/// <see cref="DiagnosticTools"/> methods for true behavioral parity — this tool exists
/// only to flatten the discriminator dispatch so the LLM picks one tool instead of five.
/// </summary>
/// <remarks>
/// <para>The tool inherits the union of the per-kind authorization scopes at dispatch time
/// (<c>read-counters</c> ∪ <c>eventpipe</c>) via <see cref="RequireAnyScopeAttribute"/>, then
/// re-checks the kind-specific scope inside the body so a caller holding only
/// <c>read-counters</c> cannot exfiltrate GC/exception/EventSource data through the new entry
/// point. This preserves RFC 0001 §2 boundaries verbatim.</para>
/// <para>RFC 0002 §7.3 #9 / #213 — the legacy collectors have been deleted in the alias
/// removal wave; this is now the sole entry point for the EventPipe collector family.</para>
/// </remarks>
[McpServerToolType]
public sealed class CollectEventsTool
{
    /// <summary>Allowed values for the <c>kind</c> discriminator. Order is preserved when
    /// rendered by <see cref="DiscriminatorDispatch"/> in failure envelopes so the LLM sees a
    /// stable hint list.</summary>
    internal static readonly IReadOnlyList<string> AllowedKinds = new[]
    {
        "counters",
        "exceptions",
        "gc",
        "event_source",
        "activities",
    };

    [RequireAnyScope("read-counters", "eventpipe")]
    [McpServerTool(
        Name = "collect_events",
        Title = "Collect EventPipe events (counters | exceptions | gc | event_source | activities)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Unified EventPipe collector entry-point (RFC 0002 §4.5). Set 'kind' to choose what to " +
        "capture: 'counters' (EventCounter snapshot — cheap first signal), 'exceptions' (managed " +
        "exception stream), 'gc' (GC start/stop pairs and pause durations), 'event_source' " +
        "(generic provider passthrough — requires providerName), or 'activities' (ActivitySource " +
        "spans). Each kind preserves the full behavior of its legacy collector tool, including " +
        "the original authorization scope: 'counters' uses 'read-counters'; all other kinds use " +
        "'eventpipe'. Returns a polymorphic envelope with exactly one of " +
        "{counters, exceptions, gc, eventSource, activities} populated alongside the chosen " +
        "kind, the issued handle, and standard NextActionHints. " +
        "IMPORTANT: for 'exceptions' and 'gc', start collection BEFORE the workload you want to " +
        "observe — EventPipe sessions take ~500 ms–1 s to fully start and events before then are " +
        "missed.")]
    public static async Task<DiagnosticResult<CollectEventsEnvelope>> CollectEvents(
        // DI services (union of every kind's dependencies). The MCP SDK injects these per call;
        // tools that don't need a given collector simply ignore the unused parameter.
        ICounterCollector counterCollector,
        IExceptionCollector exceptionCollector,
        IGcCollector gcCollector,
        IActivityCollector activityCollector,
        IEventSourceCollector eventSourceCollector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description(
            "Which EventPipe family to collect. One of: 'counters', 'exceptions', 'gc', " +
            "'event_source', 'activities'. Each kind preserves the options of its legacy " +
            "collector tool; irrelevant options are ignored.")]
        string kind = "counters",
        // Shared options.
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")]
        int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults differ per kind (counters: 5; all other kinds: 10).")]
        int? durationSeconds = null,
        [Description("Verbosity (summary|detail|raw). Applies to all kinds; semantics match the legacy collectors — 'summary' trims the bulky inline list (Counters, Recent, Events) but keeps it behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        // kind=counters
        [Description("kind=counters only. Optional list of EventCounter provider names to subscribe to. If null/empty, defaults to System.Runtime, Microsoft.AspNetCore.Hosting and Microsoft-AspNetCore-Server-Kestrel.")]
        string[]? providers = null,
        [Description("kind=counters only. Refresh interval (in seconds) requested from each provider. Defaults to 1.")]
        int intervalSeconds = 1,
        // kind=exceptions
        [Description("kind=exceptions only. Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")]
        int maxRecent = 100,
        // kind=gc / kind=event_source
        [Description("kind=gc or kind=event_source. Maximum number of events to return. Must be >= 1. Defaults to 200.")]
        int maxEvents = 200,
        // kind=event_source
        [Description("kind=event_source only. EventSource provider name (e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'). Required when kind='event_source'.")]
        string? providerName = null,
        [Description("kind=event_source only. EventSource keyword mask. -1 (default) means all keywords. Clamped to a safer default for non-allowlisted providers (unsafeProvider path).")]
        long keywords = -1,
        [Description("kind=event_source only. Event verbosity level (0=LogAlways..5=Verbose). Defaults to 5.")]
        int eventLevel = 5,
        [Description("kind=event_source only. Opt-in switch for non-allowlisted EventSource providers (issue #165 / M2). Only honoured when the server has 'Diagnostics:AllowSensitiveHeapValues=true' or the principal holds the 'eventsource-any' scope.")]
        bool unsafeProvider = false,
        // kind=activities
        [Description("kind=activities only. Optional ActivitySource name filters. Supports '*' and '?' wildcards. Null/empty captures all sources.")]
        IReadOnlyList<string>? sources = null,
        [Description("kind=activities only. Maximum number of captured activities to retain. Must be >= 1. Defaults to 200.")]
        int maxActivities = 200,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<CollectEventsEnvelope>(
                kind, AllowedKinds, nameof(kind), out var canonicalKind, out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        // Per-kind authorization re-check. The dispatch-time gate only proved the caller holds
        // one of {read-counters, eventpipe}; we now enforce the precise legacy scope so this
        // unified entry-point cannot widen a narrower bearer's reach. Skipped when no principal
        // is materialized (stdio root accessor returns null — treated as root by the filter).
        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = canonicalKind == "counters" ? "read-counters" : "eventpipe";
            if (!principal.HasScope(requiredScope))
            {
                var message =
                    $"kind='{canonicalKind}' requires the '{requiredScope}' scope. " +
                    "RFC 0002 §4.5: collect_events preserves the per-kind authorization boundary of its legacy collectors.";
                return DiagnosticResult.Fail<CollectEventsEnvelope>(
                    message,
                    new DiagnosticError("InsufficientScope", message, requiredScope));
            }
        }

        // Default durationSeconds per kind matches the legacy tool defaults so callers omitting
        // the parameter see no behavioral change.
        var effectiveDuration = durationSeconds ?? (canonicalKind == "counters" ? 5 : 10);

        // Stage A of RFC 0002 §7.3 #7 / issue #211: emit MCP notifications/progress while the
        // EventPipe session is open, and translate MCP notifications/cancelled into a partial
        // envelope so spec-compliant clients no longer need the legacy job-polling bridge.
        try
        {
            return await CollectionProgressTicker.RunAsync(
                requestContext,
                $"collect_events:{canonicalKind}",
                TimeSpan.FromSeconds(effectiveDuration),
                TimeSpan.FromSeconds(1),
                async ct => canonicalKind switch
                {
                    "counters" => Project(
                        await DiagnosticTools.SnapshotCounters(
                            counterCollector, resolver, handles,
                            processId, effectiveDuration, providers, intervalSeconds, depth,
                            ct).ConfigureAwait(false),
                        "counters",
                        (env, data) => env with { Counters = data }),

                    "exceptions" => Project(
                        await DiagnosticTools.CollectExceptions(
                            exceptionCollector, resolver, handles,
                            processId, effectiveDuration, maxRecent, depth,
                            ct).ConfigureAwait(false),
                        "exceptions",
                        (env, data) => env with { Exceptions = data }),

                    "gc" => Project(
                        await DiagnosticTools.CollectGcEvents(
                            gcCollector, resolver, handles,
                            processId, effectiveDuration, maxEvents, depth,
                            ct).ConfigureAwait(false),
                        "gc",
                        (env, data) => env with { Gc = data }),

                    "event_source" => Project(
                        await DiagnosticTools.CollectEventSource(
                            eventSourceCollector, resolver, handles,
                            allowlist, sensitiveGate, principalAccessor,
                            providerName ?? string.Empty,
                            processId, effectiveDuration, keywords, eventLevel, maxEvents, depth,
                            unsafeProvider, deprecation,
                            ct).ConfigureAwait(false),
                        "event_source",
                        (env, data) => env with { EventSource = data }),

                    "activities" => Project(
                        await DiagnosticTools.CollectActivities(
                            activityCollector, resolver, handles,
                            processId, sources, effectiveDuration, maxActivities,
                            ct).ConfigureAwait(false),
                        "activities",
                        (env, data) => env with { Activities = data }),

                    // Unreachable — TryValidate narrowed canonicalKind to the AllowedKinds set above.
                    _ => DiagnosticResult.Fail<CollectEventsEnvelope>(
                        $"Unhandled kind '{canonicalKind}'.",
                        new DiagnosticError("InvalidArgument", $"Unhandled kind '{canonicalKind}'.", nameof(kind))),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new DiagnosticResult<CollectEventsEnvelope>(
                $"collect_events(kind='{canonicalKind}') cancelled by the client before the {effectiveDuration}s window elapsed. " +
                "No payload was retained — restart the collection to capture data.",
                Array.Empty<NextActionHint>())
            {
                Data = new CollectEventsEnvelope(canonicalKind),
                Cancelled = true,
            };
        }
    }

    /// <summary>
    /// Re-wraps a legacy collector's <see cref="DiagnosticResult{T}"/> as a
    /// <see cref="CollectEventsEnvelope"/>-shaped result, preserving Summary, Hints, Handle,
    /// HandleExpiresAt, ResolvedProcess and Error so callers see the exact same envelope they
    /// got from the legacy tool — only the typed payload moves into the polymorphic shape.
    /// </summary>
    private static DiagnosticResult<CollectEventsEnvelope> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        Func<CollectEventsEnvelope, TInner, CollectEventsEnvelope> populate)
    {
        var envelope = new CollectEventsEnvelope(kind);
        if (inner.Data is not null)
        {
            envelope = populate(envelope, inner.Data);
        }

        return new DiagnosticResult<CollectEventsEnvelope>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = inner.IsError ? null : envelope,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }
}

/// <summary>
/// Polymorphic payload returned by <see cref="CollectEventsTool.CollectEvents"/>. Exactly one
/// of the kind-specific fields (<see cref="Counters"/>, <see cref="Exceptions"/>,
/// <see cref="Gc"/>, <see cref="EventSource"/>, <see cref="Activities"/>) is populated, matched
/// by <see cref="Kind"/>. Mirrors the discriminator-envelope convention used by other
/// consolidated tools (e.g. <c>get_method_il</c>).
/// </summary>
public sealed record CollectEventsEnvelope(
    string Kind,
    CounterSnapshot? Counters = null,
    ExceptionSnapshot? Exceptions = null,
    GcSummary? Gc = null,
    EventSourceCapture? EventSource = null,
    ActivityCapture? Activities = null);
