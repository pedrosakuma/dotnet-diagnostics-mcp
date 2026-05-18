using System.ComponentModel;
using DotnetDbgMcp.Core.Capabilities;
using DotnetDbgMcp.Core.Counters;
using DotnetDbgMcp.Core.CpuSampling;
using DotnetDbgMcp.Core.Dump;
using DotnetDbgMcp.Core.EventSources;
using DotnetDbgMcp.Core.Exceptions;
using DotnetDbgMcp.Core.Gc;
using DotnetDbgMcp.Core.ProcessDiscovery;
using ModelContextProtocol.Server;

namespace DotnetDbgMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-dbg-mcp Core diagnostic primitives.
/// Each tool delegates to a Core service resolved from the request scope.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    [McpServerTool(
        Name = "list_dotnet_processes",
        Title = "List local .NET processes",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Lists all .NET processes on the local machine that expose a Diagnostic IPC endpoint. " +
        "Returns process id, runtime version, OS, architecture and the managed entrypoint assembly.")]
    public static IReadOnlyList<DotnetProcess> ListDotnetProcesses(IProcessDiscovery discovery)
        => discovery.ListProcesses();

    [McpServerTool(
        Name = "get_process_info",
        Title = "Get .NET process info",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Returns metadata for a single .NET process identified by its OS process id, " +
        "or null if the process is not running or does not expose a diagnostic endpoint.")]
    public static DotnetProcess? GetProcessInfo(
        IProcessDiscovery discovery,
        [Description("Operating system process id of the target .NET process.")] int processId)
        => discovery.TryGetProcess(processId);

    [McpServerTool(
        Name = "get_diagnostic_capabilities",
        Title = "Detect diagnostic capabilities",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Probes the target process to determine which diagnostic tools the server can use against it. " +
        "Detects CoreCLR vs NativeAOT (NativeAOT lacks CPU sampling and gcdump) and returns a capability matrix. " +
        "Takes up to ~2 seconds while probing the SampleProfiler provider.")]
    public static Task<DiagnosticCapabilities> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        CancellationToken cancellationToken)
        => detector.DetectAsync(processId, cancellationToken);

    [McpServerTool(
        Name = "snapshot_counters",
        Title = "Snapshot EventCounters",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Collects EventCounters from the target process over a fixed time window and returns the " +
        "latest value seen per counter. Default providers cover the .NET runtime, ASP.NET Core hosting " +
        "and Kestrel; pass a custom list to observe other EventSources.")]
    public static Task<CounterSnapshot> SnapshotCounters(
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
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (intervalSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalSeconds), "intervalSeconds must be >= 1.");
        }

        return collector.CollectAsync(
            processId,
            TimeSpan.FromSeconds(durationSeconds),
            providers is { Length: > 0 } ? providers : null,
            intervalSeconds,
            cancellationToken);
    }

    [McpServerTool(
        Name = "collect_cpu_sample",
        Title = "Collect CPU sample",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Captures a CPU sample from the target process and returns the top-N hotspots aggregated by method. " +
        "Requires CoreCLR — NativeAOT processes do not implement the SampleProfiler EventSource. " +
        "Each hotspot reports both inclusive and exclusive sample counts.")]
    public static Task<CpuSample> CollectCpuSample(
        ICpuSampler sampler,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (topN < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be >= 1.");
        }

        return sampler.SampleAsync(processId, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken);
    }

    [McpServerTool(
        Name = "collect_exceptions",
        Title = "Collect managed exceptions",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Subscribes to the runtime Exception keyword on Microsoft-Windows-DotNETRuntime and " +
        "captures every managed exception thrown by the target process during the window. " +
        "Returns total count, breakdown by exception type, and a bounded list of recent exception details.")]
    public static Task<ExceptionSnapshot> CollectExceptions(
        IExceptionCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (maxRecent < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRecent), "maxRecent must be >= 1.");
        }

        return collector.CollectAsync(processId, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken);
    }

    [McpServerTool(
        Name = "collect_gc_events",
        Title = "Collect GC events",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Subscribes to the runtime GC keyword and pairs GCStart/GCStop events to compute pause " +
        "durations per collection. Returns total collections, total/max pause time, counts per " +
        "generation, and a bounded list of individual GC events.")]
    public static Task<GcSummary> CollectGcEvents(
        IGcCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of GC events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        return collector.CollectAsync(processId, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken);
    }

    [McpServerTool(
        Name = "collect_event_source",
        Title = "Capture custom EventSource",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false)]
    [Description(
        "Generic EventSource passthrough: opens an EventPipe session for a single EventSource " +
        "by name (e.g. System.Net.Http, Microsoft.AspNetCore.Hosting, Microsoft-AspNetCore-Server-Kestrel, " +
        "or any user-defined source) and returns the events emitted during the window. Use this to " +
        "investigate HTTP activity, hosting events, or domain-specific instrumentation.")]
    public static Task<EventSourceCapture> CollectEventSource(
        IEventSourceCollector collector,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'.")] string providerName,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("EventSource keyword mask. -1 (default) means all keywords.")] long keywords = -1,
        [Description("Event verbosity level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Informational, 5=Verbose). Defaults to 5.")] int eventLevel = 5,
        [Description("Maximum number of captured events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("providerName is required.", nameof(providerName));
        }

        if (durationSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds), "durationSeconds must be >= 1.");
        }

        if (maxEvents < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "maxEvents must be >= 1.");
        }

        return collector.CaptureAsync(
            processId,
            providerName,
            TimeSpan.FromSeconds(durationSeconds),
            keywords,
            eventLevel,
            maxEvents,
            cancellationToken);
    }

    [McpServerTool(
        Name = "collect_process_dump",
        Title = "Write process dump",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false)]
    [Description(
        "Writes a process dump for the target .NET application to disk. The dump file remains on the " +
        "server's filesystem (path returned) so it can be analyzed offline with dotnet-dump or WinDbg. " +
        "Dump types in increasing size/cost: Mini < Triage < WithHeap < Full.")]
    public static Task<DumpResult> CollectProcessDump(
        IProcessDumper dumper,
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional output directory. If null, defaults to <temp>/dotnet-dbg-mcp.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
        => dumper.WriteDumpAsync(processId, dumpType, outputDirectory, cancellationToken);
}
