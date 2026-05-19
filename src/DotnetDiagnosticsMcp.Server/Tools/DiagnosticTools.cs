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
        "Each hotspot reports both inclusive and exclusive sample counts. Run after snapshot_counters shows elevated cpu-usage.")]
    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink. Lazy: only the top-N hotspots are resolved (capped by maxResolvedSources). Off by default — requires PDBs alongside the assemblies or a reachable symbol path.")] bool resolveSourceLines = false,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols;srv*https://msdl.microsoft.com/download/symbols'). Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to 10.")] int maxResolvedSources = 10,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        if (maxResolvedSources < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: maxResolvedSources)
            : null;
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
        [Description("Absolute path to a previously-captured .dmp file. Required.")] string dumpFilePath,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for each of the top-5 types by bytes. Off by default — slower.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        CancellationToken cancellationToken = default)
    {
        var inspection = await inspector.InspectAsync(
            dumpFilePath,
            new DumpInspectionOptions(TopTypes: topTypes, IncludeRetentionPaths: includeRetentionPaths, RetentionPathLimit: retentionPathLimit),
            cancellationToken).ConfigureAwait(false);

        var topByBytes = inspection.TopTypesByBytes;
        var summary = topByBytes.Count == 0
            ? $"Inspected {dumpFilePath} — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects."
            : $"Inspected {dumpFilePath} — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances).";

        NextActionHint? hint = null;
        var topWithHandoff = topByBytes.FirstOrDefault(t => t.Identity is { ModuleVersionId: not null, MetadataToken: not null });
        if (topWithHandoff is { Identity: { } id })
        {
            hint = new NextActionHint(
                "dotnet-assembly-mcp.get_method",
                $"Pivot to assembly inspection for the top retained type `{id.TypeFullName}` via the (mvid, token) handoff.",
                new Dictionary<string, object?>
                {
                    ["moduleVersionId"] = id.ModuleVersionId,
                    ["metadataToken"] = id.MetadataToken,
                    ["typeFullName"] = id.TypeFullName,
                    ["assemblyPathHint"] = id.ModulePath,
                });
        }

        return hint is null
            ? DiagnosticResult.Ok(inspection, summary)
            : DiagnosticResult.Ok(inspection, summary, hint);
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

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("get_diagnostic_capabilities", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
