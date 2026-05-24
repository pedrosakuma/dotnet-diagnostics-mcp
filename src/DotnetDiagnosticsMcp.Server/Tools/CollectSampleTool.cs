using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.Tools.Dispatch;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Tools.Deprecation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// RFC 0002 §4.2 consolidation: single MCP entry-point for the bounded-time sampling
/// family — <c>cpu</c>, <c>off_cpu</c>, <c>allocation</c>. Delegates to the legacy
/// <see cref="DiagnosticTools"/> methods so per-kind behaviour (provider/keyword setup,
/// SSRF guards, ClrMD enrichment, <c>runAsJob</c> semantics) is preserved verbatim —
/// asserted byte-for-byte by the dual-entrypoint compatibility tests.
/// </summary>
/// <remarks>
/// <para>The legacy tools (<c>collect_cpu_sample</c>, <c>collect_off_cpu_sample</c>,
/// <c>collect_allocation_sample</c>) stay registered and functional, stamped with
/// <see cref="DeprecatedToolAttribute"/> for a deprecation window — see issue #210.</para>
/// <para>The MCP-task / <c>runAsJob</c> async cutover is tracked separately by issue #211;
/// this tool preserves the existing <c>runAsJob=true</c> behaviour for <c>kind="cpu"</c>
/// without altering its lifecycle.</para>
/// </remarks>
[McpServerToolType]
public sealed class CollectSampleTool
{
    internal const string ToolName = "collect_sample";
    internal const string KindCpu = "cpu";
    internal const string KindOffCpu = "off_cpu";
    internal const string KindAllocation = "allocation";

    /// <summary>Allowed values for the <c>kind</c> discriminator. Order is preserved when
    /// rendered by <see cref="DiscriminatorDispatch"/> in failure envelopes.</summary>
    internal static readonly IReadOnlyList<string> AllowedKinds = new[]
    {
        KindCpu,
        KindOffCpu,
        KindAllocation,
    };

    [RequireScope("eventpipe")]
    [McpServerTool(
        Name = ToolName,
        Title = "Collect a bounded-time sample (cpu | off_cpu | allocation)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true,
        TaskSupport = ToolTaskSupport.Optional)]
    [Description(
        "Unified sampling collector entry-point (RFC 0002 §4.2). Set 'kind' to choose the sampler: " +
        "'cpu' (on-CPU SampleProfiler / perf — top managed hotspots with MethodIdentity handoff), " +
        "'off_cpu' (where threads are blocked and for how long — Linux sched_switch via perf, Windows " +
        "ContextSwitch via NT Kernel Logger), or 'allocation' (GCAllocationTick rolled up by type — " +
        "TypeName is empty on NativeAOT, surfaced as a caveat in the envelope summary). " +
        "Each kind preserves the parameters and behaviour of its legacy collector tool, including " +
        "the SSRF-guarded `symbolPath` precedence chain, ClrMD generic-instantiation enrichment for " +
        "CPU samples, and the `runAsJob=true` background-job lifecycle for CPU samples (the async / " +
        "MCP Tasks cutover is tracked separately by issue #211). " +
        "Returns a polymorphic envelope with exactly one of {cpu, offCpu, allocation} populated " +
        "alongside the chosen kind, the issued handle, and standard NextActionHints.")]
    public static async Task<DiagnosticResult<CollectSampleEnvelope>> CollectSample(
        ICpuSampler cpuSampler,
        IOffCpuSampler offCpuSampler,
        EventPipeAllocationSampler allocationSampler,
        IDiagnosticHandleStore handles,
        DotnetDiagnosticsMcp.Core.Jobs.ICollectionJobRunner jobs,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description(
            "Which sampler to run. One of: 'cpu', 'off_cpu', 'allocation'. Each kind preserves " +
            "the options of its legacy collector tool; irrelevant options are ignored.")]
        string kind = KindCpu,
        // Shared options.
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")]
        int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")]
        int durationSeconds = 10,
        [Description("Maximum number of items returned (top hotspots, top blocking stacks, or top types depending on kind). Must be >= 1. Defaults to 25.")]
        int topN = 25,
        [Description("Verbosity (summary|detail|raw). Applies to kind='cpu' and kind='off_cpu' — see the legacy collectors for semantics. Ignored by kind='allocation'.")]
        SamplingDepth depth = SamplingDepth.Summary,
        // kind=cpu / kind=off_cpu
        [Description("kind='cpu' or kind='off_cpu'. Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`. Ignored by kind='allocation' and by kind='cpu' when resolveSourceLines=false.")]
        string? symbolPath = null,
        // kind=cpu only
        [Description("kind='cpu' only. If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload. Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")]
        bool resolveSourceLines = true,
        [Description("kind='cpu' only. Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")]
        int? maxResolvedSources = null,
        [Description("kind='cpu' only. If true, performs an opt-in ClrMD attach after sampling to recover closed generic instantiations for the hottest managed frames. CoreCLR only. On Linux requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target. Defaults to false.")]
        bool resolveMethodInstantiations = false,
        [Description("kind='cpu' only. Cap on how many top hotspots get ClrMD generic-instantiation enrichment. Must be >= 1. Defaults to the requested topN.")]
        int? maxResolvedMethodInstantiations = null,
        [Description("kind='cpu' only. If true, runs the collection as a background job. Returns immediately with a job handle; poll get_collection_status(handle) until status='completed'. Defaults to false (synchronous). The MCP-task cutover that retires this flag is tracked by issue #211.")]
        bool runAsJob = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (!DiscriminatorDispatch.TryValidate<CollectSampleEnvelope>(
                kind, AllowedKinds, nameof(kind), out var canonicalKind, out var dispatchFailure))
        {
            return dispatchFailure!;
        }

        return canonicalKind switch
        {
            KindCpu => Project(
                await DiagnosticTools.CollectCpuSample(
                    cpuSampler,
                    handles,
                    jobs,
                    resolver,
                    symbolServerAllowlist,
                    principalAccessor,
                    processId,
                    durationSeconds,
                    topN,
                    resolveSourceLines,
                    symbolPath,
                    maxResolvedSources,
                    resolveMethodInstantiations,
                    maxResolvedMethodInstantiations,
                    runAsJob,
                    depth,
                    deprecation,
                    cancellationToken).ConfigureAwait(false),
                KindCpu,
                (env, data) => env with { Cpu = data }),

            KindOffCpu => Project(
                await DiagnosticTools.CollectOffCpuSample(
                    offCpuSampler,
                    handles,
                    resolver,
                    symbolServerAllowlist,
                    principalAccessor,
                    processId,
                    durationSeconds,
                    topN,
                    symbolPath,
                    depth,
                    deprecation,
                    cancellationToken).ConfigureAwait(false),
                KindOffCpu,
                (env, data) => env with { OffCpu = data }),

            KindAllocation => Project(
                await DiagnosticTools.CollectAllocationSample(
                    allocationSampler,
                    handles,
                    resolver,
                    processId,
                    durationSeconds,
                    topN,
                    cancellationToken).ConfigureAwait(false),
                KindAllocation,
                (env, data) => env with { Allocation = data }),

            // Unreachable — TryValidate narrowed canonicalKind to the AllowedKinds set above.
            _ => DiagnosticResult.Fail<CollectSampleEnvelope>(
                $"Unhandled kind '{canonicalKind}'.",
                new DiagnosticError("InvalidArgument", $"Unhandled kind '{canonicalKind}'.", nameof(kind))),
        };
    }

    /// <summary>
    /// Re-wraps a legacy sampler's <see cref="DiagnosticResult{T}"/> as a
    /// <see cref="CollectSampleEnvelope"/>-shaped result, preserving Summary, Hints, Handle,
    /// HandleExpiresAt, ResolvedProcess and Error so callers see the exact same envelope they
    /// got from the legacy tool — only the typed payload moves into the polymorphic shape.
    /// </summary>
    private static DiagnosticResult<CollectSampleEnvelope> Project<TInner>(
        DiagnosticResult<TInner> inner,
        string kind,
        Func<CollectSampleEnvelope, TInner, CollectSampleEnvelope> populate)
    {
        // Preserve the legacy ack shape: when the legacy collector returns success with a null
        // payload (e.g. runAsJob=true on cpu emits a job-handle ack with Data=null), keep Data
        // null on the unified envelope too. Wrapping null as `CollectSampleEnvelope(kind)` with
        // all kind fields = null would violate the "exactly one populated payload field"
        // contract and silently change the ack JSON shape.
        CollectSampleEnvelope? envelope = inner.Data is null
            ? null
            : populate(new CollectSampleEnvelope(kind), inner.Data);

        return new DiagnosticResult<CollectSampleEnvelope>(inner.Summary, inner.Hints, inner.Error)
        {
            Data = inner.IsError ? null : envelope,
            Handle = inner.Handle,
            HandleExpiresAt = inner.HandleExpiresAt,
            ResolvedProcess = inner.ResolvedProcess,
        };
    }
}

/// <summary>
/// Polymorphic payload returned by <see cref="CollectSampleTool.CollectSample"/>. Exactly one
/// of the kind-specific fields (<see cref="Cpu"/>, <see cref="OffCpu"/>,
/// <see cref="Allocation"/>) is populated, matched by <see cref="Kind"/>. Mirrors the
/// discriminator-envelope convention used by other RFC 0002 consolidated tools
/// (<see cref="CollectEventsEnvelope"/>, <c>get_method_il</c>, …).
/// </summary>
public sealed record CollectSampleEnvelope(
    string Kind,
    CpuSample? Cpu = null,
    OffCpuSnapshot? OffCpu = null,
    AllocationSample? Allocation = null);
