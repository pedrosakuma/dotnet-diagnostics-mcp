using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Core.Triage;
using DotnetDiagnosticsMcp.Server.Security;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 / §4.6 — single bootstrap entrypoint that subsumes the five read-only
/// process inspection tools (<c>list_dotnet_processes</c>, <c>get_process_info</c>,
/// <c>get_diagnostic_capabilities</c>, <c>get_container_signals</c>, <c>get_memory_trend</c>)
/// behind one <c>view=</c> discriminator, plus the Phase 10.3 <c>view=resources</c>
/// and Phase 10.4 <c>view=requests-now</c> extensions for FD / handle / socket inspection and
/// in-flight ASP.NET Core request snapshots. RFC 0002 §7.3 #9 / #213 — the five
/// legacy tools have been deleted in the alias removal wave; this is now the sole
/// bootstrap entrypoint.
/// </summary>
/// <remarks>
/// <para>The tool is a thin dispatcher: every view forwards to the matching
/// <see cref="DiagnosticTools"/> static method, then re-projects the resulting
/// <see cref="DiagnosticResult{T}"/> into a unified <see cref="InspectProcessReport"/>
/// envelope where exactly one of the view-specific fields is populated. This preserves
/// behavior bit-for-bit (<see cref="DiagnosticResult{T}.Summary"/>,
/// <see cref="DiagnosticResult{T}.Hints"/>, <see cref="DiagnosticResult{T}.ResolvedProcess"/>,
/// <see cref="DiagnosticResult{T}.Error"/>) so the dual-entrypoint compatibility tests in
/// <c>InspectProcessCompatibilityTests</c> compare equal under
/// <c>CompatibilityEnvelopeAssert</c>.</para>
/// <para>Auto-resolve guardrails (per RFC §4.6):
/// <list type="bullet">
///   <item><description><c>view=list</c> never touches the resolver — it just lists every
///   .NET process visible to the diagnostic IPC and ignores <c>processId</c>.</description></item>
///   <item><description><c>view=memory_trend</c> and <c>view=resources</c> preserve the
///   underlying collector contract: when the caller supplies <c>processId</c> explicitly it is
///   used as a raw OS pid (no .NET IPC check), so both views work on NativeAOT and non-.NET
///   processes.</description></item>
///   <item><description><c>view=requests-now</c> always resolves through the .NET diagnostic IPC
///   because it opens an EventPipe session and a live thread snapshot.</description></item>
///   <item><description>Every other view auto-resolves the lone visible .NET process when
///   <c>processId</c> is omitted, matching the legacy bootstrap-implicit behavior.</description></item>
/// </list></para>
/// </remarks>
[McpServerToolType]
public sealed class InspectProcessTool
{
    /// <summary>List every .NET process visible to the diagnostic IPC. Does not require <c>processId</c>.</summary>
    public const string ListView = "list";

    /// <summary>Fetch metadata for one .NET process. Auto-resolves <c>processId</c> when omitted.</summary>
    public const string InfoView = "info";

    /// <summary>Probe a target's diagnostic capability matrix (CoreCLR vs NativeAOT, CPU sampling, gcdump).</summary>
    public const string CapabilitiesView = "capabilities";

    /// <summary>Read Linux cgroup v2 container signals (CPU throttling, memory, PSI, OOM kills).</summary>
    public const string ContainerView = "container";

    /// <summary>Sample OS-level memory growth over a configurable window. Works on any OS process.</summary>
    public const string MemoryTrendView = "memory_trend";

    /// <summary>Inspect GC / ThreadPool / tiered-comp settings, filtered env vars and AppContext switches.</summary>
    public const string RuntimeConfigView = "runtime-config";

    /// <summary>Inspect FD / handle / socket state, optionally over a short trend window.</summary>
    public const string ResourcesView = "resources";

    /// <summary>Capture in-flight ASP.NET Core requests and enrich them with the current thread stack.</summary>
    public const string RequestsNowView = "requests-now";

    /// <summary>Phase 12 IoT-style triage: collect counters, classify, return verdict + hints.</summary>
    public const string TriageView = "triage";

    private static readonly IReadOnlyList<string> AllowedViews = new[]
    {
        ListView,
        InfoView,
        CapabilitiesView,
        ContainerView,
        MemoryTrendView,
        RuntimeConfigView,
        ResourcesView,
        RequestsNowView,
        TriageView,
    };

    [RequireAnyScope("read-counters", "ptrace")]
    [McpServerTool(
        Name = "inspect_process",
        Title = "Inspect a .NET process (list / info / capabilities / container / memory_trend / runtime-config / resources / requests-now / triage)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "RFC 0002 §4.6 — single bootstrap entrypoint that subsumes list_dotnet_processes, " +
        "get_process_info, get_diagnostic_capabilities, get_container_signals and get_memory_trend, " +
        "plus the Phase 11 runtime-config view, the Phase 10.3 resources view, the Phase 10.4 requests-now view, and the Phase 12 triage view. Pick the projection with view=list|info|capabilities|container|memory_trend|runtime-config|resources|requests-now|triage. " +
        "view=list returns every .NET process visible to the diagnostic IPC and ignores processId. " +
        "All other views auto-resolve the lone visible .NET process when processId is omitted; " +
        "view=memory_trend and view=resources additionally accept any OS pid (NativeAOT / non-.NET) when processId is explicit, " +
        "and use durationSeconds + sampleEverySeconds to shape their observation windows. " +
        "view=runtime-config reads filtered runtime env vars plus best-effort ClrMD GC / ThreadPool settings without adding a new auth scope. " +
        "view=requests-now opens a short EventPipe session on Microsoft.AspNetCore.Hosting and then captures a live thread snapshot, so it requires the ptrace scope. " +
        "view=triage collects counters (5s), classifies the workload (cpu-bound, gc-pressure, threadpool-starvation, lock-contention, io-bound, healthy), and returns actionable hints — the LLM just follows the first hint.")]
    public static async Task<DiagnosticResult<InspectProcessReport>> InspectProcess(
        IProcessDiscovery discovery,
        IProcessContextResolver resolver,
        ICapabilityDetector detector,
        IContainerSignalsCollector containerCollector,
        IMemoryTrendCollector memoryCollector,
        IRuntimeConfigInspector runtimeConfigInspector,
        IProcessResourcesCollector resourcesCollector,
        IRequestsNowCollector requestsNowCollector,
        ICounterCollector counterCollector,
        IPrincipalAccessor principalAccessor,
        [Description("Projection to compute. Allowed: list|info|capabilities|container|memory_trend|runtime-config|resources|requests-now|triage. Defaults to 'list'.")]
        string? view = ListView,
        [Description("Operating system process id of the target. Required by no view: list ignores it, every other view auto-resolves the lone visible .NET process when omitted.")]
        int? processId = null,
        [Description("view=memory_trend only — duration of the observation window in seconds. Must be >= 2 and defaults to 10. view=resources uses 0 for a single sample (default) and >= 2 for trend mode. view=triage uses durationSeconds for counter collection (defaults to 5).")]
        int? durationSeconds = null,
        [Description("view=memory_trend or view=resources only — interval between consecutive samples in seconds. Must be >= 1. Defaults to 2.")]
        int sampleEverySeconds = 2,
        [Description("view=container only — depth knob forwarded to get_container_signals. Summary (default) drops the Notes[] caveats; Detail / Raw keep them.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<InspectProcessReport>(
                view, AllowedViews, parameterName: "view", out var canonical, out var failure))
        {
            return failure!;
        }

        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = canonical == RequestsNowView ? "ptrace" : "read-counters";
            if (!principal.HasScope(requiredScope))
            {
                var message =
                    canonical == RequestsNowView
                        ? $"view='{canonical}' requires the '{requiredScope}' scope because it captures a live thread snapshot."
                        : $"view='{canonical}' requires the '{requiredScope}' scope. inspect_process preserves the legacy authorization boundary of its bootstrap views.";
                return DiagnosticResult.Fail<InspectProcessReport>(
                    message,
                    new DiagnosticError("Forbidden", message, requiredScope),
                    new NextActionHint(
                        "inspect_process",
                        canonical == RequestsNowView
                            ? "Retry with a bearer that also grants 'ptrace', or use one of the read-only bootstrap views."
                            : "Retry with a bearer that grants 'read-counters', or use view='requests-now' with a ptrace-scoped bearer.",
                        new Dictionary<string, object?> { ["view"] = canonical }));
            }
        }

        return canonical switch
        {
            ListView => ProjectList(discovery),
            InfoView => Wrap(
                await DiagnosticTools.GetProcessInfo(discovery, resolver, processId, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { Info = data }),
            CapabilitiesView => Wrap(
                await DiagnosticTools.GetDiagnosticCapabilities(detector, resolver, processId, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { Capabilities = data }),
            ContainerView => Wrap(
                await DiagnosticTools.GetContainerSignals(
                        containerCollector, resolver, processId, depth: depth, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { Container = data }),
            MemoryTrendView => Wrap(
                await DiagnosticTools.GetMemoryTrend(
                        memoryCollector, resolver, processId, durationSeconds ?? 10, sampleEverySeconds, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { MemoryTrend = data }),
            RuntimeConfigView => Wrap(
                await DiagnosticTools.GetRuntimeConfig(
                        runtimeConfigInspector, resolver, processId, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { RuntimeConfig = data }),
            ResourcesView => Wrap(
                await DiagnosticTools.GetProcessResources(
                        resourcesCollector, resolver, processId, durationSeconds ?? 0, sampleEverySeconds, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { Resources = data }),
            RequestsNowView => Wrap(
                await DiagnosticTools.GetRequestsNow(
                        requestsNowCollector, resolver, processId, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { RequestsNow = data?.Requests }),
            TriageView => Wrap(
                await DiagnosticTools.PerformTriage(
                        counterCollector, resolver, processId, durationSeconds ?? 5, cancellationToken)
                    .ConfigureAwait(false),
                canonical,
                (report, data) => report with { Triage = data }),
            _ => throw new InvalidOperationException(
                $"DiscriminatorDispatch accepted unknown view '{canonical}'."),
        };
    }

    private static DiagnosticResult<InspectProcessReport> ProjectList(IProcessDiscovery discovery)
    {
        // view=list deliberately bypasses the resolver and the WithContext stamp — the legacy
        // ListDotnetProcesses tool never set ResolvedProcess on its envelope.
        var inner = DiagnosticTools.ListDotnetProcesses(discovery);
        return Wrap(inner, ListView, (report, data) => report with { List = data });
    }

    private static DiagnosticResult<InspectProcessReport> Wrap<TPayload>(
        DiagnosticResult<TPayload> inner,
        string view,
        Func<InspectProcessReport, TPayload?, InspectProcessReport> populate)
    {
        // Re-project: keep Summary / Hints / Error / Handle / HandleExpiresAt / ResolvedProcess
        // verbatim so the wrapped response is structurally indistinguishable from the legacy
        // tool's envelope under CompatibilityEnvelopeAssert (which serializes both sides with
        // System.Text.Json and DeepEquals the JsonNode trees).
        var report = inner.Data is null
            ? new InspectProcessReport(view)
            : populate(new InspectProcessReport(view), inner.Data);

        return new DiagnosticResult<InspectProcessReport>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = report,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }
}

/// <summary>
/// Unified payload for <c>inspect_process</c>. Exactly one of the view-specific fields is
/// populated on success — the one matching <see cref="View"/>. Failure envelopes leave every
/// field <c>null</c>; the failure surfaces through
/// <see cref="DiagnosticResult{T}.Error"/> as for every other tool.
/// </summary>
/// <param name="View">The projection that was computed (matches the request's <c>view</c>
/// discriminator). Lower-case for stability with the request side.</param>
/// <param name="List">Populated when <c>view=list</c> — every .NET process visible to the IPC.</param>
/// <param name="Info">Populated when <c>view=info</c> — metadata for the resolved process.</param>
/// <param name="Capabilities">Populated when <c>view=capabilities</c> — capability matrix.</param>
/// <param name="Container">Populated when <c>view=container</c> — cgroup v2 signals.</param>
/// <param name="MemoryTrend">Populated when <c>view=memory_trend</c> — OS memory samples + verdict.</param>
/// <param name="RuntimeConfig">Populated when <c>view=runtime-config</c> — GC / ThreadPool / tiered-comp settings plus filtered env vars.</param>
/// <param name="Resources">Populated when <c>view=resources</c> — FD / handle / socket state.</param>
/// <param name="RequestsNow">Populated when <c>view=requests-now</c> — in-flight ASP.NET Core requests with thread stacks.</param>
/// <param name="Triage">Populated when <c>view=triage</c> — Phase 12 IoT-style classification with verdict, severity, evidence, and actionable hints.</param>
public sealed record InspectProcessReport(
    string View,
    IReadOnlyList<DotnetProcess>? List = null,
    DotnetProcess? Info = null,
    DiagnosticCapabilities? Capabilities = null,
    ContainerSignals? Container = null,
    MemoryTrend? MemoryTrend = null,
    RuntimeConfigView? RuntimeConfig = null,
    ProcessResources? Resources = null,
    IReadOnlyList<InFlightHttpRequest>? RequestsNow = null,
    TriageResult? Triage = null);
