using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Investigation;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-diagnostics-mcp Core diagnostic primitives.
/// Every tool returns a <see cref="DiagnosticResult{T}"/> envelope carrying a short summary,
/// next-action hints, and the typed payload — so a low-context LLM can drill down without
/// re-reading the server instructions on every turn.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    [McpServerTool(
        Name = "list_dotnet_processes",
        Title = "List local .NET processes",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Lists all .NET processes on the local machine that expose a Diagnostic IPC endpoint. " +
        "Returns process id, runtime version, OS, architecture and the managed entrypoint assembly. " +
        "Usually the first tool to call in any investigation.")]
    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListDotnetProcesses(IProcessDiscovery discovery)
    {
        var processes = discovery.ListProcesses();
        if (processes.Count == 0)
        {
            return DiagnosticResult.Ok(
                processes,
                "No attachable .NET processes found. If the target runs in a container, make sure the sidecar shares its PID namespace and runs as the same UID.",
                new NextActionHint("get_diagnostic_capabilities", "Re-run once the target is up to confirm the runtime exposes a diagnostic endpoint."));
        }

        var preview = string.Join(", ", processes.Take(3).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
        return DiagnosticResult.Ok(
            processes,
            $"Found {processes.Count} .NET process(es): {preview}{(processes.Count > 3 ? ", …" : "")}.",
            new NextActionHint(
                "get_diagnostic_capabilities",
                "Probe the target process to confirm which collectors are supported (CoreCLR vs NativeAOT).",
                new Dictionary<string, object?> { ["processId"] = processes[0].ProcessId }));
    }

    [McpServerTool(
        Name = "get_process_info",
        Title = "Get .NET process info",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns metadata for a single .NET process identified by its OS process id, " +
        "or an error result if the process is not running or does not expose a diagnostic endpoint.")]
    public static DiagnosticResult<DotnetProcess> GetProcessInfo(
        IProcessDiscovery discovery,
        [Description("Operating system process id of the target .NET process.")] int processId)
    {
        var process = discovery.TryGetProcess(processId);
        if (process is null)
        {
            return DiagnosticResult.Fail<DotnetProcess>(
                $"No .NET process with id {processId} exposes a diagnostic endpoint.",
                new DiagnosticError("ProcessNotFound", $"Process id {processId} is not visible to the diagnostic IPC."),
                new NextActionHint("list_dotnet_processes", "List attachable .NET processes and pick a valid pid."));
        }

        return DiagnosticResult.Ok(
            process,
            $"Process {process.ProcessId} — {process.ManagedEntrypointAssemblyName ?? "<unknown>"} on .NET {process.RuntimeVersion} ({process.OperatingSystem}/{process.ProcessArchitecture}).",
            new NextActionHint("get_diagnostic_capabilities", "Detect which collectors apply (CoreCLR vs NativeAOT) before sampling.",
                new Dictionary<string, object?> { ["processId"] = process.ProcessId }));
    }

    [McpServerTool(
        Name = "get_diagnostic_capabilities",
        Title = "Detect diagnostic capabilities",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Probes the target process to determine which diagnostic tools the server can use against it. " +
        "Detects CoreCLR vs NativeAOT (NativeAOT lacks CPU sampling and gcdump) and returns a capability matrix. " +
        "Takes up to ~2 seconds while probing the SampleProfiler provider.")]
    public static async Task<DiagnosticResult<DiagnosticCapabilities>> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        CancellationToken cancellationToken)
    {
        try
        {
            var caps = await detector.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
            var hint = caps.CanSampleCpu
                ? new NextActionHint("snapshot_counters", "Cheap first signal: CPU/memory/GC/thread-pool. Run before reaching for sampling.",
                    new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 5 })
                : new NextActionHint("snapshot_counters", "NativeAOT: CPU sampling unavailable. Counters + EventSource + dumps still work.",
                    new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 5 });

            return DiagnosticResult.Ok(
                caps,
                $"Runtime: {caps.Runtime} {caps.RuntimeVersion}. CPU sampling: {caps.CanSampleCpu}, gcdump: {caps.CanCollectGcDump}. {caps.Notes}".TrimEnd(),
                hint);
        }
        catch (ServerNotAvailableException ex)
        {
            return DiagnosticResult.Fail<DiagnosticCapabilities>(
                $"Diagnostic socket for process {processId} is not reachable.",
                new DiagnosticError("EndpointUnavailable", ex.Message, ex.GetType().FullName),
                new NextActionHint("list_dotnet_processes", "Re-list processes. Common cause: sidecar UID mismatch with target."));
        }
    }

    [McpServerTool(
        Name = "snapshot_counters",
        Title = "Snapshot EventCounters",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Collects EventCounters from the target process over a fixed time window and returns the " +
        "latest value seen per counter. Default providers cover the .NET runtime, ASP.NET Core hosting " +
        "and Kestrel; pass a custom list to observe other EventSources. Cheapest first signal — always run " +
        "before sampling or dumps.")]
    public static async Task<DiagnosticResult<CounterSnapshot>> SnapshotCounters(
        ICounterCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 5.")] int durationSeconds = 5,
        [Description("Optional list of EventCounter provider names to subscribe to. " +
                     "If null/empty, defaults to System.Runtime, Microsoft.AspNetCore.Hosting and Microsoft-AspNetCore-Server-Kestrel.")]
        string[]? providers = null,
        [Description("Refresh interval (in seconds) requested from each provider. Defaults to 1.")] int intervalSeconds = 1,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(durationSeconds), "must be >= 1");
        }
        if (intervalSeconds < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(intervalSeconds), "must be >= 1");
        }

        var snapshot = await collector.CollectAsync(
            processId,
            TimeSpan.FromSeconds(durationSeconds),
            providers is { Length: > 0 } ? providers : null,
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var cpu = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
        var heap = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gc-heap-size");
        var hint = (cpu?.Value ?? 0) >= 70
            ? new NextActionHint("collect_cpu_sample", $"cpu-usage={cpu!.Value:F1}% over {durationSeconds}s — investigate the hot path.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 10, ["topN"] = 25 })
            : new NextActionHint("collect_gc_events", "Counters look quiet — confirm there are no GC pauses before widening scope.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 10 });

        return DiagnosticResult.Ok(
            snapshot,
            $"Captured {snapshot.Counters.Count} counter(s) over {durationSeconds}s — cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}.",
            hint);
    }

    [McpServerTool(
        Name = "collect_cpu_sample",
        Title = "Collect CPU sample",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Captures a CPU sample from the target process and returns the top-N hotspots aggregated by method. " +
        "On CoreCLR uses EventPipe SampleProfiler (managed frames with mvid+token handoff). " +
        "On NativeAOT (Linux) falls back to 'perf record' when available — frames are native symbols only, MethodIdentity is null. " +
        "Each hotspot reports both inclusive and exclusive sample counts. Run after snapshot_counters shows elevated cpu-usage. " +
        "Set runAsJob=true to schedule the collection on the server and poll with get_collection_status — useful for long durationSeconds windows that would otherwise hold the MCP request open.")]
    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        DotnetDiagnosticsMcp.Core.Jobs.ICollectionJobRunner jobs,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink. Lazy: only the top-N hotspots are resolved (capped by maxResolvedSources). Off by default — requires PDBs alongside the assemblies or a reachable symbol path.")] bool resolveSourceLines = false,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols;srv*https://msdl.microsoft.com/download/symbols'). Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to 10.")] int maxResolvedSources = 10,
        [Description("If true, runs the collection as a background job. Returns immediately with a job handle; poll get_collection_status(handle) until status='completed' to retrieve the CpuSample result. Defaults to false (synchronous).")] bool runAsJob = false,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        if (maxResolvedSources < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: maxResolvedSources)
            : null;

        if (runAsJob)
        {
            // Detach: the job outlives the MCP request, so we must not cancel it when the
            // request token trips. The runner's own cancel hook is what stops it.
            var jobTtl = TimeSpan.FromSeconds(durationSeconds) + CpuSampleHandleTtl;
            var jobHandle = jobs.Start(
                processId,
                "cpu-sample-job",
                jobTtl,
                async ct =>
                {
                    var jobResult = await sampler.SampleAsync(processId, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, ct).ConfigureAwait(false);
                    var jobSample = jobResult.Summary;
                    var jobTop = jobSample.TopHotspots.Count > 0 ? jobSample.TopHotspots[0] : null;
                    var dataHandle = handles.Register(processId, "cpu-sample", jobResult.Artifact, CpuSampleHandleTtl);
                    var summaryText = jobTop is not null
                        ? $"Captured {jobSample.TotalSamples} samples over {durationSeconds}s. Top method: {jobTop.Frame.Method} ({jobTop.InclusiveSamples} inclusive / {jobTop.ExclusiveSamples} exclusive). Drill into the full call tree with get_call_tree(handle=\"{dataHandle.Id}\")."
                        : $"Captured {jobSample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";
                    return DiagnosticResult.OkWithHandle(
                        jobSample,
                        summaryText,
                        dataHandle.Id,
                        dataHandle.ExpiresAt,
                        new NextActionHint("get_call_tree", "Walk the merged caller→callee tree built from the same samples.",
                            new Dictionary<string, object?> { ["handle"] = dataHandle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }));
                });

            return DiagnosticResult.OkWithHandle<CpuSample>(
                data: null!,
                $"CPU sampling job started (handle {jobHandle.Id}). Poll get_collection_status(handle=\"{jobHandle.Id}\") after ~{durationSeconds}s to retrieve the result.",
                jobHandle.Id,
                jobHandle.ExpiresAt,
                new NextActionHint("get_collection_status", "Poll the background CPU sampling job until status='completed'.",
                    new Dictionary<string, object?> { ["handle"] = jobHandle.Id }),
                new NextActionHint("cancel_collection", "Abort the background CPU sampling job if the symptom changed.",
                    new Dictionary<string, object?> { ["handle"] = jobHandle.Id }));
        }

        var result = await sampler.SampleAsync(processId, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, cancellationToken).ConfigureAwait(false);
        var sample = result.Summary;
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var handle = handles.Register(processId, "cpu-sample", result.Artifact, CpuSampleHandleTtl);
        var summary = top is not null
            ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s. Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with get_call_tree(handle=\"{handle.Id}\")."
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";

        return DiagnosticResult.OkWithHandle(
            sample,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("get_call_tree", "Walk the merged caller→callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_exceptions", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 10 }));
    }

    [McpServerTool(
        Name = "get_call_tree",
        Title = "Drill into CPU sample call tree",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns a pruned caller→callee tree from a prior collect_cpu_sample run, addressed by its handle. " +
        "Use `rootMethodFilter` to anchor the walk at a method substring (case-insensitive). " +
        "`maxDepth` and `maxNodes` bound the response size so the LLM stays under its token budget. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CallTreeView> GetCallTree(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_cpu_sample call.")] string handle,
        [Description("Optional case-insensitive substring; the tree is re-rooted at the highest-ranked frame whose method name contains this text.")] string? rootMethodFilter = null,
        [Description("Maximum tree depth from the root. Must be >= 1. Defaults to 8.")] int maxDepth = 8,
        [Description("Approximate cap on the number of nodes returned (top children at each level). Must be >= 1. Defaults to 200.")] int maxNodes = 200)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CallTreeView>(nameof(handle), "is required");
        if (maxDepth < 1) return InvalidArg<CallTreeView>(nameof(maxDepth), "must be >= 1");
        if (maxNodes < 1) return InvalidArg<CallTreeView>(nameof(maxNodes), "must be >= 1");

        var artifact = handles.TryGet<CpuSampleTraceArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<CallTreeView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_cpu_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        var root = artifact.Root;
        if (!string.IsNullOrWhiteSpace(rootMethodFilter))
        {
            var match = FindHighestRankedDescendant(root, rootMethodFilter);
            if (match is null)
            {
                return DiagnosticResult.Fail<CallTreeView>(
                    $"No frame matching '{rootMethodFilter}' in handle '{handle}'.",
                    new DiagnosticError("NotFound", "No frame in the merged call tree contains the supplied substring.", rootMethodFilter),
                    new NextActionHint("get_call_tree", "Re-issue without rootMethodFilter to inspect the full tree first.",
                        new Dictionary<string, object?> { ["handle"] = handle, ["maxDepth"] = maxDepth, ["maxNodes"] = maxNodes }));
            }
            root = match;
        }

        var (pruned, nodeCount, truncated) = PruneTree(root, maxDepth, maxNodes);
        var view = new CallTreeView(artifact.ProcessId, artifact.TotalSamples, nodeCount, truncated, pruned);
        var summary = truncated
            ? $"Showing {nodeCount} nodes (truncated; raise maxNodes or maxDepth, or narrow with rootMethodFilter). Root: {root.Frame.Method} — {root.InclusiveSamples} inclusive samples."
            : $"Showing the full sub-tree rooted at {root.Frame.Method} ({nodeCount} nodes, {root.InclusiveSamples} inclusive samples).";

        return DiagnosticResult.Ok(
            view,
            summary,
            new NextActionHint("get_call_tree", "Drill deeper by anchoring at a specific method.",
                new Dictionary<string, object?> { ["handle"] = handle, ["rootMethodFilter"] = "<method substring>", ["maxDepth"] = 6 }));
    }

    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    private static CallTreeNode? FindHighestRankedDescendant(CallTreeNode node, string substring)
    {
        CallTreeNode? best = null;
        var stack = new Stack<CallTreeNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current.Frame.Method.Contains(substring, StringComparison.OrdinalIgnoreCase) &&
                (best is null || current.InclusiveSamples > best.InclusiveSamples))
            {
                best = current;
            }

            foreach (var child in current.Children)
            {
                stack.Push(child);
            }
        }

        return best;
    }

    private static (CallTreeNode Pruned, int NodeCount, bool Truncated) PruneTree(CallTreeNode root, int maxDepth, int maxNodes)
    {
        var nodeBudget = maxNodes;
        var truncated = false;
        var pruned = Walk(root, maxDepth);
        return (pruned, maxNodes - nodeBudget, truncated);

        CallTreeNode Walk(CallTreeNode n, int depthRemaining)
        {
            if (nodeBudget <= 0)
            {
                truncated = true;
                return n with { Children = Array.Empty<CallTreeNode>() };
            }
            nodeBudget--;

            if (depthRemaining <= 1 || n.Children.Count == 0)
            {
                if (n.Children.Count > 0) truncated = true;
                return n with { Children = Array.Empty<CallTreeNode>() };
            }

            var kept = new List<CallTreeNode>();
            foreach (var child in n.Children)
            {
                if (nodeBudget <= 0)
                {
                    truncated = true;
                    break;
                }
                kept.Add(Walk(child, depthRemaining - 1));
            }

            return n with { Children = kept };
        }
    }

    [McpServerTool(
        Name = "collect_exceptions",
        Title = "Collect managed exceptions",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Subscribes to the runtime Exception keyword on Microsoft-Windows-DotNETRuntime and " +
        "captures every managed exception thrown by the target process during the window. " +
        "Returns total count, breakdown by exception type, and a bounded list of recent exception details. " +
        "IMPORTANT: start this BEFORE the workload you want to observe — exceptions before the session opens are missed.")]
    public static async Task<DiagnosticResult<ExceptionSnapshot>> CollectExceptions(
        IExceptionCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ExceptionSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxRecent < 1) return InvalidArg<ExceptionSnapshot>(nameof(maxRecent), "must be >= 1");

        var snap = await collector.CollectAsync(processId, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken).ConfigureAwait(false);
        var topType = snap.ByType.OrderByDescending(c => c.Count).FirstOrDefault();
        var summary = snap.TotalExceptions == 0
            ? $"No managed exceptions thrown in {durationSeconds}s. If you expected some, ensure the collection started before the workload."
            : $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}).";

        var hint = snap.TotalExceptions > 0
            ? new NextActionHint("collect_event_source", "Subscribe to a domain-specific EventSource to correlate with the exception spikes.",
                new Dictionary<string, object?> { ["processId"] = processId, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 })
            : new NextActionHint("collect_gc_events", "No exception pressure — sweep GC events next.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 10 });

        return DiagnosticResult.Ok(snap, summary, hint);
    }

    [McpServerTool(
        Name = "collect_gc_events",
        Title = "Collect GC events",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Subscribes to the runtime GC keyword and pairs GCStart/GCStop events to compute pause " +
        "durations per collection. Returns total collections, total/max pause time, counts per " +
        "generation, and a bounded list of individual GC events.")]
    public static async Task<DiagnosticResult<GcSummary>> CollectGcEvents(
        IGcCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of GC events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<GcSummary>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<GcSummary>(nameof(maxEvents), "must be >= 1");

        var gc = await collector.CollectAsync(processId, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken).ConfigureAwait(false);
        var summary = gc.TotalCollections == 0
            ? $"No GC activity in {durationSeconds}s — heap is quiet or the workload is idle."
            : $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms.";

        var hint = gc.MaxPauseTime.TotalMilliseconds > 100
            ? new NextActionHint("collect_process_dump",
                $"Max GC pause {gc.MaxPauseTime.TotalMilliseconds:F0}ms is high — capture a WithHeap dump for offline heap analysis.",
                new Dictionary<string, object?> { ["processId"] = processId, ["dumpType"] = "WithHeap" })
            : new NextActionHint("collect_event_source", "GC looks healthy — pivot to a domain EventSource (e.g. System.Net.Http) for application-level signal.",
                new Dictionary<string, object?> { ["processId"] = processId, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 });

        return DiagnosticResult.Ok(gc, summary, hint);
    }

    [McpServerTool(
        Name = "collect_event_source",
        Title = "Capture custom EventSource",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Generic EventSource passthrough: opens an EventPipe session for a single EventSource " +
        "by name (e.g. System.Net.Http, Microsoft.AspNetCore.Hosting, Microsoft-AspNetCore-Server-Kestrel, " +
        "or any user-defined source) and returns the events emitted during the window. Use this to " +
        "investigate HTTP activity, hosting events, or domain-specific instrumentation.")]
    public static async Task<DiagnosticResult<EventSourceCapture>> CollectEventSource(
        IEventSourceCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'.")] string providerName,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("EventSource keyword mask. -1 (default) means all keywords.")] long keywords = -1,
        [Description("Event verbosity level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Informational, 5=Verbose). Defaults to 5.")] int eventLevel = 5,
        [Description("Maximum number of captured events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return InvalidArg<EventSourceCapture>(nameof(providerName), "is required");
        if (durationSeconds < 1) return InvalidArg<EventSourceCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<EventSourceCapture>(nameof(maxEvents), "must be >= 1");

        var capture = await collector.CaptureAsync(
            processId, providerName, TimeSpan.FromSeconds(durationSeconds), keywords, eventLevel, maxEvents, cancellationToken).ConfigureAwait(false);

        var summary = capture.Events.Count == 0
            ? $"No events from '{providerName}' in {durationSeconds}s. Verify the provider name and that it's actually instrumented in the target."
            : $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s.";

        return DiagnosticResult.Ok(capture, summary,
            new NextActionHint("snapshot_counters", "Cross-check captured events against runtime counters for the same window.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = durationSeconds }));
    }

    [McpServerTool(
        Name = "collect_process_dump",
        Title = "Write process dump",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Writes a process dump for the target .NET application to disk. The dump file remains on the " +
        "server's filesystem (path returned) so it can be analyzed offline with dotnet-dump or WinDbg. " +
        "Dump types in increasing size/cost: Mini < Triage < WithHeap < Full. " +
        "Heavyweight — use only when live collectors are insufficient.")]
    public static async Task<DiagnosticResult<DumpResult>> CollectProcessDump(
        IProcessDumper dumper,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional output directory. If null, defaults to <temp>/dotnet-diagnostics-mcp.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var dump = await dumper.WriteDumpAsync(processId, dumpType, outputDirectory, cancellationToken).ConfigureAwait(false);
        var hint = dumpType == ProcessDumpType.Mini
            ? new NextActionHint("inspect_dump",
                "Mini dump captured — heap walk unavailable. Re-capture with dumpType='WithHeap' for full inspection.",
                new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath })
            : new NextActionHint("inspect_dump",
                "Inspect the dump's managed heap for top-retained types + handoff payload to dotnet-assembly-mcp.",
                new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath, ["topTypes"] = 20 });
        return DiagnosticResult.Ok(
            dump,
            $"Wrote {dumpType} dump for pid {dump.ProcessId} to {dump.FilePath} ({dump.FileSizeBytes:N0} bytes).",
            hint);
    }

    [McpServerTool(
        Name = "inspect_dump",
        Title = "Inspect a process dump's managed heap",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Walks the managed heap of a previously-captured WithHeap/Full dump (produced by " +
        "collect_process_dump or any compatible source) using ClrMD. Returns aggregated runtime/heap " +
        "totals plus top types by retained bytes and instance count. Each TypeStat carries a TypeIdentity " +
        "(ModuleVersionId + MetadataToken) ready to hand off verbatim to dotnet-assembly-mcp's get_type " +
        "(or get_method on the type's members) without name parsing. Offline and read-only — does not " +
        "touch the live process. Mini and Triage dumps return runtime metadata only; for heap inspection " +
        "use WithHeap or Full.")]
    public static async Task<DiagnosticResult<DumpInspection>> InspectDump(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        [Description("Absolute path to a previously-captured .dmp file. Required.")] string dumpFilePath,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await inspector.InspectAsync(
            dumpFilePath,
            new DumpInspectionOptions(TopTypes: topTypes, IncludeRetentionPaths: includeRetentionPaths, RetentionPathLimit: retentionPathLimit),
            cancellationToken).ConfigureAwait(false);

        var handle = handles.Register(snapshot.ProcessId, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl);
        var inspection = snapshot.ToDumpInspection(topTypes, handle.Id);

        var topByBytes = inspection.TopTypesByBytes;
        var summary = topByBytes.Count == 0
            ? $"Inspected {dumpFilePath} — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
            : $"Inspected {dumpFilePath} — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

        var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
        return hint is null
            ? DiagnosticResult.Ok(inspection, summary)
            : DiagnosticResult.Ok(inspection, summary, hint);
    }

    private static readonly TimeSpan HeapSnapshotHandleTtl = TimeSpan.FromMinutes(10);
    internal const string HeapSnapshotKind = "heap-snapshot";

    private static NextActionHint? BuildHeapDrilldownHint(string handle, IReadOnlyList<TypeStat> topByBytes)
    {
        // Prefer the cross-MCP handoff to dotnet-assembly-mcp when a type identity is available —
        // that pivots the LLM from "what is retained" to "what's the type definition / methods".
        var topWithHandoff = topByBytes.FirstOrDefault(t => t.Identity is { ModuleVersionId: not null, MetadataToken: not null });
        if (topWithHandoff is { Identity: { } id })
        {
            return new NextActionHint(
                "dotnet-assembly-mcp.get_method",
                $"Pivot to assembly inspection for the top retained type `{id.TypeFullName}` via the (mvid, token) handoff. " +
                $"Use query_heap_snapshot(handle=\"{handle}\", view=\"retention-paths\") to expand retention without re-walking.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = id.ModuleVersionId,
                    ["metadataToken"] = id.MetadataToken,
                    ["typeFullName"] = id.TypeFullName,
                    ["assemblyPathHint"] = id.ModulePath,
                });
        }

        // Fallback: at least point at the local drilldown tool.
        return new NextActionHint(
            "query_heap_snapshot",
            "Drill into the snapshot (e.g. richer top-N, retention paths filtered by type) without re-walking the heap.",
            new Dictionary<string, object?>
            {
                ["handle"] = handle,
                ["view"] = "top-types",
            });
    }

    [McpServerTool(
        Name = "inspect_live_heap",
        Title = "Inspect a live .NET process's managed heap",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Attaches to a live .NET process via ClrMD and walks its managed heap WITHOUT writing a dump file. " +
        "Returns the same top-N type / retention information as inspect_dump but skips the disk I/O of " +
        "collect_process_dump. The target is suspended for the duration of the walk (typically sub-second " +
        "for small heaps, can reach a few seconds for multi-GB heaps); plan accordingly for latency-sensitive " +
        "workloads. Same UID constraint as the diagnostic socket applies — sidecar must run as the target's UID. " +
        "Each TypeStat carries a TypeIdentity (ModuleVersionId + MetadataToken) ready to hand off verbatim to " +
        "dotnet-assembly-mcp's get_type. Use inspect_dump when you need an artifact to keep, share or re-inspect.")]
    public static async Task<DiagnosticResult<LiveHeapInspection>> InspectLiveHeap(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower and lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await inspector.InspectLiveAsync(
            processId,
            new DumpInspectionOptions(TopTypes: topTypes, IncludeRetentionPaths: includeRetentionPaths, RetentionPathLimit: retentionPathLimit),
            cancellationToken).ConfigureAwait(false);

        var handle = handles.Register(processId, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl);
        var inspection = snapshot.ToLiveHeapInspection(topTypes, handle.Id);

        var topByBytes = inspection.TopTypesByBytes;
        var summary = topByBytes.Count == 0
            ? $"Attached to pid {processId} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
            : $"Attached to pid {processId} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

        var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
        return hint is null
            ? DiagnosticResult.Ok(inspection, summary)
            : DiagnosticResult.Ok(inspection, summary, hint);
    }

    [McpServerTool(
        Name = "query_heap_snapshot",
        Title = "Drill into a heap snapshot",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns a slice of a heap snapshot previously captured by inspect_dump or inspect_live_heap, addressed by its handle. " +
        "Lets the LLM ask for a richer top-N (snapshot retains ~200 types), retention paths filtered by type substring, " +
        "GC roots grouped by kind, the finalizer queue, or per-segment heap layout — without paying the walk cost a second time. Views: " +
        "`top-types` (expand the inline top-N to up to snapshot capacity), " +
        "`retention-paths` (filter the walked retention chains by target type substring; requires the original inspect call to have set includeRetentionPaths=true), " +
        "`roots-by-kind` (GC roots aggregated by ClrRootKind with pinned/interior counts), " +
        "`finalizer-queue` (objects waiting for finalization, top-N by retained bytes), " +
        "`fragmentation` (per-segment Gen/Kind/Length/Committed/Free bytes — high FreePercent on Gen2/LOH signals fragmentation). " +
        "Handles expire ~10 minutes after the capture and are invalidated when the target process exits.")]
    public static DiagnosticResult<HeapSnapshotQueryResult> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue' or 'fragmentation'.")] string view = "top-types",
        [Description("For view='top-types'/'finalizer-queue'/'fragmentation': maximum entries to return. Ignored by 'roots-by-kind' and 'retention-paths'.")] int topN = 50,
        [Description("For view='top-types': ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("For view='retention-paths': case-insensitive substring matched against TypeFullName to narrow the returned chains.")] string? typeFullName = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<HeapSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<HeapSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<HeapSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Heap snapshot handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("inspect_live_heap", "Re-attach and re-walk to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        var normalizedView = view.Trim().ToLowerInvariant();
        return normalizedView switch
        {
            "top-types" => QueryTopTypes(snapshot, handle, topN, rankBy),
            "retention-paths" => QueryRetentionPaths(snapshot, handle, typeFullName, topN),
            "roots-by-kind" => QueryRootsByKind(snapshot, handle),
            "finalizer-queue" => QueryFinalizerQueue(snapshot, handle, topN),
            "fragmentation" => QueryFragmentation(snapshot, handle, topN),
            _ => InvalidArg<HeapSnapshotQueryResult>(nameof(view), $"must be 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue' or 'fragmentation' (got '{view}')"),
        };
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryTopTypes(
        HeapSnapshotArtifact snapshot, string handle, int topN, string rankBy)
    {
        var normalizedRank = rankBy.Trim().ToLowerInvariant();
        IReadOnlyList<TypeStat> source = normalizedRank switch
        {
            "instances" => snapshot.TopTypesByInstances,
            "bytes" or "" => snapshot.TopTypesByBytes,
            _ => Array.Empty<TypeStat>(),
        };
        if (source.Count == 0 && normalizedRank is not ("instances" or "bytes" or ""))
        {
            return InvalidArg<HeapSnapshotQueryResult>(nameof(rankBy), $"must be 'bytes' or 'instances' (got '{rankBy}')");
        }

        var slice = source.Take(topN).ToArray();
        var origin = snapshot.Origin.ToString();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no recorded top types — heap walk produced 0 objects."
            : $"Returning {slice.Length} types ranked by {(normalizedRank == "instances" ? "instance count" : "retained bytes")} from snapshot '{handle}' ({origin}, captured {snapshot.CapturedAt:u}, pid {snapshot.ProcessId}). Top: `{slice[0].TypeFullName}` ({slice[0].TotalBytesPercent}% / {slice[0].InstanceCount:N0} instances).";

        var result = new HeapSnapshotQueryResult(handle, "top-types", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            TopTypes = slice,
            RankBy = normalizedRank.Length == 0 ? "bytes" : normalizedRank,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryRetentionPaths(
        HeapSnapshotArtifact snapshot, string handle, string? typeFullName, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.RetentionPaths is null || snapshot.RetentionPaths.Count == 0)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without retention paths.",
                new DiagnosticError("RetentionPathsMissing",
                    "Re-run inspect_dump or inspect_live_heap with includeRetentionPaths=true to populate the snapshot's retention data.",
                    handle),
                new NextActionHint("inspect_live_heap",
                    "Re-walk with includeRetentionPaths=true to populate retention chains for the top retained types.",
                    new Dictionary<string, object?> { ["processId"] = snapshot.ProcessId, ["includeRetentionPaths"] = true }));
        }

        IEnumerable<RetentionPath> filtered = snapshot.RetentionPaths;
        if (!string.IsNullOrWhiteSpace(typeFullName))
        {
            filtered = filtered.Where(p => p.TargetTypeFullName.Contains(typeFullName, StringComparison.OrdinalIgnoreCase));
        }

        var slice = filtered.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"No retention paths in snapshot '{handle}' match filter '{typeFullName ?? "<none>"}'."
            : $"Returning {slice.Length} retention path(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top target: `{slice[0].TargetTypeFullName}` (chain depth {slice[0].Chain.Count}).";

        var result = new HeapSnapshotQueryResult(handle, "retention-paths", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            RetentionPaths = slice,
            FilterTypeFullName = typeFullName,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryRootsByKind(
        HeapSnapshotArtifact snapshot, string handle)
    {
        var origin = snapshot.Origin.ToString();
        var roots = snapshot.RootsByKind ?? Array.Empty<RootKindStat>();
        var summary = roots.Count == 0
            ? $"Snapshot '{handle}' has no recorded GC roots (heap walk produced 0 objects or root enumeration failed)."
            : $"Returning {roots.Count} root kind(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top: `{roots[0].RootKind}` — {roots[0].RootCount:N0} roots, {roots[0].DistinctTargetObjects:N0} distinct targets, {roots[0].DirectlyReferencedBytes:N0} bytes directly referenced.";
        var result = new HeapSnapshotQueryResult(handle, "roots-by-kind", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            RootsByKind = roots,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryFinalizerQueue(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var finalizable = snapshot.FinalizableObjectsByType ?? Array.Empty<FinalizableTypeStat>();
        var slice = finalizable.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no objects waiting on the finalizer queue."
            : $"Returning {slice.Length} finalizable type(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top: `{slice[0].TypeFullName}` — {slice[0].InstanceCount:N0} instances, {slice[0].TotalBytes:N0} bytes. A growing finalizer queue is a classic memory-pressure smell.";
        var result = new HeapSnapshotQueryResult(handle, "finalizer-queue", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            FinalizableObjects = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryFragmentation(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var segments = snapshot.Segments ?? Array.Empty<SegmentStat>();
        // Most fragmented first — only Gen2/LOH/POH free bytes count as actionable fragmentation;
        // ephemeral generations turn over too fast for it to matter.
        var ordered = segments
            .OrderByDescending(s => s.FreeBytes)
            .ThenByDescending(s => s.FreePercent)
            .Take(topN)
            .ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' has no recorded segments."
            : $"Returning {ordered.Length} segment(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Most fragmented: `{ordered[0].Generation}` segment @ 0x{ordered[0].Start:x} — {ordered[0].FreeBytes:N0}/{ordered[0].Length:N0} bytes free ({ordered[0].FreePercent}%).";
        var result = new HeapSnapshotQueryResult(handle, "fragmentation", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Segments = ordered,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    [McpServerTool(
        Name = "start_investigation",
        Title = "Plan a diagnostic investigation",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns a structured InvestigationPlan: a ready-to-execute decision tree of tool calls with " +
        "rationale, decision branches, early-stop conditions, and constraints (MaxToolCalls, " +
        "dump-requires-approval). Modes are resolved from the arguments: hypothesis present → " +
        "'hypothesis' (routes directly to the relevant evidence collector); baseline present → " +
        "'warm' (skips covered steps, emits MetricComparisons against baseline); otherwise 'cold' " +
        "(USE-style: snapshot_counters first, branch on evidence). Call this BEFORE any other " +
        "collector when the symptom is non-trivial — it pays for itself by preventing loops.")]
    public static DiagnosticResult<InvestigationPlan> StartInvestigation(
        IInvestigationPlanner planner,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Plain-language symptom, e.g. 'high latency on /checkout since v2025.10'. Required for cold mode; optional for warm/hypothesis.")] string? symptom = null,
        [Description("Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.")] string? hypothesis = null,
        [Description("Baseline snapshot from a prior investigation (JSON of BaselineHandle). Triggers warm mode.")] BaselineHandle? baseline = null,
        [Description("Optional hard limit on tool calls before forcing summarization. Defaults to 8.")] int maxToolCalls = 8,
        [Description("If true, collect_process_dump steps are marked approval-gated. Defaults to true.")] bool dumpRequiresApproval = true)
    {
        if (processId <= 0) return InvalidArg<InvestigationPlan>(nameof(processId), "must be a positive OS pid");
        if (maxToolCalls < 1) return InvalidArg<InvestigationPlan>(nameof(maxToolCalls), "must be >= 1");

        var request = new InvestigationRequest(
            ProcessId: processId,
            Symptom: symptom,
            Hypothesis: hypothesis,
            Baseline: baseline,
            Constraints: new InvestigationConstraints(
                MaxToolCalls: maxToolCalls,
                DumpRequiresApproval: dumpRequiresApproval));

        var plan = planner.Plan(request);
        var summary = $"Mode={plan.Mode}. Next step #{plan.NextStep.StepNumber}: {plan.NextStep.ToolName}. " +
                      $"{plan.AllSteps.Count} total step(s), {plan.EarlyStopConditions.Count} early-stop condition(s). " +
                      $"Honor MaxToolCalls={plan.Constraints.MaxToolCalls}.";

        var hintParams = new Dictionary<string, object?>(plan.NextStep.ToolParams);
        return DiagnosticResult.Ok(
            plan,
            summary,
            new NextActionHint(plan.NextStep.ToolName, plan.NextStep.Rationale, hintParams));
    }

    [McpServerTool(
        Name = "export_investigation_summary",
        Title = "Export portable investigation summary",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reads a prior collect_cpu_sample drill-down handle and produces a portable, versioned " +
        "InvestigationSummary (~5-20 KB JSON) ready to paste into a PR, ADR, or ticket. " +
        "Includes build + container provenance harvested from the sidecar environment, stable " +
        "module+methodFullName symbol refs (survive rebuilds where line numbers shift), and " +
        "optional lineage to a previous investigation. Set `format=markdown` for a human-readable " +
        "version. The server is stateless: the LLM owns persistence — paste the JSON into a doc " +
        "and feed it back via `compare_to_baseline` on the next deploy.")]
    public static DiagnosticResult<ExportedInvestigationSummary> ExportInvestigationSummary(
        IInvestigationSummaryExporter exporter,
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_cpu_sample call.")] string handle,
        [Description("Output format: 'json' (default — portable, machine-readable) or 'markdown' (human-readable for PRs).")] SummaryFormat format = SummaryFormat.Json,
        [Description("Max hotspots to include in the summary. Defaults to 10.")] int topHotspots = 10,
        [Description("Optional managed assembly name for the target (from list_dotnet_processes).")] string? buildAssemblyName = null,
        [Description("Optional investigation id from the previous summary, to link lineage.")] string? previousInvestigationId = null,
        [Description("Optional commit SHA being proposed as the fix.")] string? fixCommitSha = null,
        [Description("Optional PR URL being proposed as the fix.")] string? fixPullRequestUrl = null,
        [Description("Optional short description of the proposed fix.")] string? fixDescription = null,
        [Description("Optional free-form notes appended to the summary.")] string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ExportedInvestigationSummary>(nameof(handle), "is required");
        if (topHotspots < 1) return InvalidArg<ExportedInvestigationSummary>(nameof(topHotspots), "must be >= 1");

        var artifact = handles.TryGet<CpuSampleTraceArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_cpu_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        var fix = (fixCommitSha is null && fixPullRequestUrl is null && fixDescription is null)
            ? null
            : new InvestigationFixTarget(fixCommitSha, fixPullRequestUrl, fixDescription);

        var exported = exporter.Export(new ExportRequest(
            Handle: handle,
            Artifact: artifact,
            TopHotspots: topHotspots,
            BuildAssemblyName: buildAssemblyName,
            PreviousInvestigationId: previousInvestigationId,
            TargetsFix: fix,
            Notes: notes,
            Format: format));

        var bytes = exported.Rendered.Length;
        return DiagnosticResult.Ok(
            exported,
            $"Exported investigation {exported.Summary.InvestigationId} ({exported.Summary.Findings.TopHotspots.Count} hotspots, {bytes} chars {format}). Paste `rendered` into your PR/ADR; re-supply this JSON via compare_to_baseline on the next investigation.",
            new NextActionHint("compare_to_baseline", "When you investigate the next deploy, pass this summary as the baseline.",
                new Dictionary<string, object?> { ["baselineSummaryJson"] = "<paste rendered JSON here>" }));
    }

    [McpServerTool(
        Name = "compare_to_baseline",
        Title = "Compare investigation summary to baseline",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Diffs two InvestigationSummary JSON documents (produced by export_investigation_summary) " +
        "and returns a SummaryDiff: provenance delta (image jump, git sha change), new hotspots " +
        "(strongest regression signal — symbol not present in baseline), removed hotspots " +
        "(improvements), and changed hotspots above a ±2 percentage-point threshold. The verdict " +
        "field collapses the diff into one of: no_regression, no_regression_after_deploy, " +
        "regression_new_hotspot, regression_increased_hotspot, improvement.")]
    public static DiagnosticResult<SummaryDiff> CompareToBaseline(
        ISummaryComparer comparer,
        [Description("Baseline summary JSON (from a prior export_investigation_summary).")] string baselineSummaryJson,
        [Description("Current summary JSON (from export_investigation_summary on the new investigation).")] string currentSummaryJson)
    {
        if (string.IsNullOrWhiteSpace(baselineSummaryJson)) return InvalidArg<SummaryDiff>(nameof(baselineSummaryJson), "is required");
        if (string.IsNullOrWhiteSpace(currentSummaryJson)) return InvalidArg<SummaryDiff>(nameof(currentSummaryJson), "is required");

        InvestigationSummary baseline, current;
        try
        {
            baseline = System.Text.Json.JsonSerializer.Deserialize(
                    baselineSummaryJson,
                    DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("baseline summary deserialized to null");
            current = System.Text.Json.JsonSerializer.Deserialize(
                    currentSummaryJson,
                    DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("current summary deserialized to null");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                "Could not parse one of the supplied summary JSON documents.",
                new DiagnosticError("InvalidSummaryJson", ex.Message),
                new NextActionHint("export_investigation_summary", "Re-export the baseline and current summaries and try again."));
        }

        if (!string.Equals(baseline.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal) ||
            !string.Equals(current.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal))
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                $"Unsupported schema. Expected '{InvestigationSummary.SchemaV1}'.",
                new DiagnosticError("UnsupportedSchema", $"baseline='{baseline.Schema}' current='{current.Schema}'"),
                new NextActionHint("export_investigation_summary", "Re-export both summaries with the current server version."));
        }

        if (baseline.Provenance is null || baseline.Findings is null || baseline.Findings.TopHotspots is null ||
            current.Provenance is null || current.Findings is null || current.Findings.TopHotspots is null)
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                "Summary JSON is missing required fields (Provenance / Findings / Findings.TopHotspots).",
                new DiagnosticError("InvalidSummaryJson", "Required fields are null after deserialization."),
                new NextActionHint("export_investigation_summary", "Re-export the summaries from a fresh investigation."));
        }

        var diff = comparer.Compare(baseline, current);
        var summaryLine = $"Verdict: {diff.Verdict}. {diff.NewHotspots.Count} new, " +
                          $"{diff.RemovedHotspots.Count} removed, {diff.ChangedHotspots.Count} changed hotspots. " +
                          $"Provenance: {diff.Provenance.Summary}.";

        return DiagnosticResult.Ok(diff, summaryLine,
            new NextActionHint("collect_cpu_sample",
                diff.Verdict.StartsWith("regression", StringComparison.Ordinal)
                    ? "Re-sample the regressing process and drill into the new top frame."
                    : "Optional: capture a fresh sample to confirm the improvement is stable.",
                new Dictionary<string, object?> { ["durationSeconds"] = 20 }));
    }

    [McpServerTool(
        Name = "get_collection_status",
        Title = "Get background collection status",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Polls the status of a background collection started with runAsJob=true (e.g. collect_cpu_sample). " +
        "Returns the current lifecycle phase (running, completed, failed, canceled), elapsed time, and the " +
        "final DiagnosticResult once the job terminates. Until then poll periodically (the started job's " +
        "response message contains the expected duration).")]
    public static DiagnosticResult<CollectionStatusReport> GetCollectionStatus(
        IDiagnosticHandleStore handles,
        [Description("Job handle returned by the original collect_* call with runAsJob=true.")] string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CollectionStatusReport>(nameof(handle), "is required");

        var job = handles.TryGet<DotnetDiagnosticsMcp.Core.Jobs.CollectionJob>(handle);
        if (job is null)
        {
            return DiagnosticResult.Fail<CollectionStatusReport>(
                $"No collection job found for handle '{handle}'. Either it expired (TTL elapsed), was invalidated when the target process exited, or never existed.",
                new DiagnosticError("HandleNotFound", $"Unknown or expired handle '{handle}'."),
                new NextActionHint("collect_cpu_sample", "Restart the collection — pass runAsJob=true to get a fresh handle.",
                    new Dictionary<string, object?> { ["runAsJob"] = true }));
        }

        var snap = job.Snapshot();
        var report = new CollectionStatusReport(
            Handle: snap.Handle,
            Kind: snap.Kind,
            ProcessId: snap.ProcessId,
            Status: snap.Status.ToString().ToLowerInvariant(),
            StartedAt: snap.StartedAt,
            CompletedAt: snap.CompletedAt,
            ElapsedSeconds: snap.ElapsedSeconds,
            Result: snap.Result,
            Error: snap.Error);

        var summary = snap.Status switch
        {
            DotnetDiagnosticsMcp.Core.Jobs.CollectionJobStatus.Running =>
                $"Job '{snap.Kind}' still running ({snap.ElapsedSeconds:F1}s elapsed). Poll again shortly.",
            DotnetDiagnosticsMcp.Core.Jobs.CollectionJobStatus.Completed =>
                $"Job '{snap.Kind}' completed in {snap.ElapsedSeconds:F1}s. The full DiagnosticResult is embedded in the result field.",
            DotnetDiagnosticsMcp.Core.Jobs.CollectionJobStatus.Failed =>
                $"Job '{snap.Kind}' failed after {snap.ElapsedSeconds:F1}s: {snap.Error?.Message ?? "unknown error"}.",
            DotnetDiagnosticsMcp.Core.Jobs.CollectionJobStatus.Canceled =>
                $"Job '{snap.Kind}' was canceled after {snap.ElapsedSeconds:F1}s.",
            _ => $"Job '{snap.Kind}' status: {snap.Status}.",
        };

        var hints = snap.IsTerminal
            ? Array.Empty<NextActionHint>()
            : new[]
            {
                new NextActionHint("get_collection_status", "Poll again — the job has not finished.",
                    new Dictionary<string, object?> { ["handle"] = snap.Handle }),
                new NextActionHint("cancel_collection", "Abort the job if you no longer need the result.",
                    new Dictionary<string, object?> { ["handle"] = snap.Handle }),
            };

        return DiagnosticResult.Ok(report, summary, hints);
    }

    [McpServerTool(
        Name = "cancel_collection",
        Title = "Cancel background collection",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Signals a background collection job (started with runAsJob=true) to stop. Cancellation is " +
        "cooperative — the underlying collector may take a moment to unwind. The job's status will " +
        "eventually transition to 'canceled'; poll get_collection_status to confirm.")]
    public static DiagnosticResult<CancelCollectionReport> CancelCollection(
        DotnetDiagnosticsMcp.Core.Jobs.ICollectionJobRunner jobs,
        [Description("Job handle returned by the original collect_* call with runAsJob=true.")] string handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CancelCollectionReport>(nameof(handle), "is required");

        var requested = jobs.Cancel(handle);
        var report = new CancelCollectionReport(handle, requested);
        var summary = requested
            ? $"Cancellation requested for job '{handle}'. Poll get_collection_status to confirm the final state."
            : $"No active job found for handle '{handle}'. It may have already completed or expired.";

        return DiagnosticResult.Ok(report, summary,
            new NextActionHint("get_collection_status", "Confirm the job reached a terminal state.",
                new Dictionary<string, object?> { ["handle"] = handle }));
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("get_diagnostic_capabilities", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}

/// <summary>Tool-facing projection of a <see cref="DotnetDiagnosticsMcp.Core.Jobs.CollectionJobSnapshot"/>.</summary>
public sealed record CollectionStatusReport(
    string Handle,
    string Kind,
    int ProcessId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    double ElapsedSeconds,
    object? Result,
    DiagnosticError? Error);

/// <summary>Tool-facing acknowledgement of a cancel_collection call.</summary>
public sealed record CancelCollectionReport(string Handle, bool CancellationRequested);
