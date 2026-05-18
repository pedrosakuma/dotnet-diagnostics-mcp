using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
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
        "Requires CoreCLR — NativeAOT processes do not implement the SampleProfiler EventSource. " +
        "Each hotspot reports both inclusive and exclusive sample counts. Run after snapshot_counters shows elevated cpu-usage.")]
    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");

        var sample = await sampler.SampleAsync(processId, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken).ConfigureAwait(false);
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var summary = top is not null
            ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s. Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive)."
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";

        return DiagnosticResult.Ok(
            sample,
            summary,
            new NextActionHint("collect_exceptions", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?> { ["processId"] = processId, ["durationSeconds"] = 10 }));
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
        return DiagnosticResult.Ok(
            dump,
            $"Wrote {dumpType} dump for pid {dump.ProcessId} to {dump.FilePath} ({dump.FileSizeBytes:N0} bytes).",
            new NextActionHint("list_dotnet_processes", "No further automated drill-down — analyze the dump offline with dotnet-dump analyze."));
    }

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("get_diagnostic_capabilities", "Re-issue with valid arguments. See tool schema for ranges and defaults."));
}
