using System.ComponentModel;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Investigation;
using DotnetDiagnosticsMcp.Core.JitCapture;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Threads;
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
        "or an error result if the process is not running or does not expose a diagnostic endpoint. " +
        "processId is optional: when exactly one .NET process is reachable on the host the server " +
        "auto-resolves it and stamps a compact capability digest on the response envelope.")]
    public static async Task<DiagnosticResult<DotnetProcess>> GetProcessInfo(
        IProcessDiscovery discovery,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<DotnetProcess>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var process = discovery.TryGetProcess(resolved.ProcessId);
        if (process is null)
        {
            return DiagnosticResult.Fail<DotnetProcess>(
                $"No .NET process with id {resolved.ProcessId} exposes a diagnostic endpoint.",
                new DiagnosticError("ProcessNotFound", $"Process id {resolved.ProcessId} is not visible to the diagnostic IPC."),
                new NextActionHint("list_dotnet_processes", "List attachable .NET processes and pick a valid pid."));
        }

        var result = DiagnosticResult.Ok(
            process,
            $"Process {process.ProcessId} — {process.ManagedEntrypointAssemblyName ?? "<unknown>"} on .NET {process.RuntimeVersion} ({process.OperatingSystem}/{process.ProcessArchitecture}).",
            new NextActionHint("snapshot_counters", "Cheap first signal: CPU/memory/GC/thread-pool sweep before any sampling.",
                new Dictionary<string, object?> { ["processId"] = process.ProcessId, ["durationSeconds"] = 5 }));
        return WithContext(result, resolved.Context);
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
        "Takes up to ~2 seconds while probing the SampleProfiler provider. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it. " +
        "Most callers no longer need this tool first — every other tool already attaches a compact capability digest " +
        "on its response envelope, so call this explicitly only when you need the full matrix.")]
    public static async Task<DiagnosticResult<DiagnosticCapabilities>> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<DiagnosticCapabilities>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        try
        {
            var caps = await detector.DetectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);
            var hint = caps.CanSampleCpu
                ? new NextActionHint("snapshot_counters", "Cheap first signal: CPU/memory/GC/thread-pool. Run before reaching for sampling.",
                    new Dictionary<string, object?> { ["processId"] = resolved.ProcessId, ["durationSeconds"] = 5 })
                : new NextActionHint("snapshot_counters", "NativeAOT: CPU sampling unavailable. Counters + EventSource + dumps still work.",
                    new Dictionary<string, object?> { ["processId"] = resolved.ProcessId, ["durationSeconds"] = 5 });

            var ok = DiagnosticResult.Ok(
                caps,
                $"Runtime: {caps.Runtime} {caps.RuntimeVersion}. CPU sampling: {caps.CanSampleCpu}, gcdump: {caps.CanCollectGcDump}. {caps.Notes}".TrimEnd(),
                hint);
            return WithContext(ok, resolved.Context);
        }
        catch (ServerNotAvailableException ex)
        {
            return DiagnosticResult.Fail<DiagnosticCapabilities>(
                $"Diagnostic socket for process {resolved.ProcessId} is not reachable.",
                new DiagnosticError("EndpointUnavailable", ex.Message, ex.GetType().FullName),
                new NextActionHint("list_dotnet_processes", "Re-list processes. Common cause: sidecar UID mismatch with target."));
        }
    }

    [McpServerTool(
        Name = "get_container_signals",
        Title = "Read cgroup v2 container signals (CPU throttling, memory, PSI)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Reads Linux cgroup v2 files for the target process: cpu.stat (throttling), cpu.max (quota), " +
        "memory.current / memory.max / memory.events (OOM kills), cpu/memory/io.pressure (PSI), pids and " +
        "oom_score. Closes the #1 K8s blind spot — 'app is slow but runtime CPU counters look fine' is usually " +
        "CPU throttling at the cgroup level, completely invisible from the runtime. " +
        "Cheap (file reads only, no privilege, no EventPipe session). Returns partial signals + Notes on " +
        "non-Linux hosts, cgroup v1 hosts and old kernels lacking PSI. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it.")]
    public static async Task<DiagnosticResult<ContainerSignals>> GetContainerSignals(
        IContainerSignalsCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<ContainerSignals>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var signals = await collector.CollectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);

        var hints = BuildContainerHints(signals);
        var summary = SummariseContainerSignals(signals);
        var ok = DiagnosticResult.Ok(signals, summary, hints);
        return WithContext(ok, resolved.Context);
    }

    private static string SummariseContainerSignals(ContainerSignals s)
    {
        if (!s.InContainer && s.CgroupVersion != CgroupVersion.V2)
        {
            return s.Notes.Count > 0 ? s.Notes[0] : "No container envelope detected.";
        }

        var parts = new List<string>();
        if (s.Cpu is { } cpu)
        {
            if (cpu.QuotaCores is { } q) parts.Add($"quota={q:F2} cores");
            if (cpu.ThrottlePercent is { } tp) parts.Add($"throttled {tp:F1}% of periods ({cpu.NrThrottled}/{cpu.NrPeriods})");
        }
        if (s.Memory is { } mem)
        {
            if (mem.MaxBytes is { } max) parts.Add($"mem {mem.CurrentBytes / 1_048_576}/{max / 1_048_576} MiB ({(mem.UsageFraction ?? 0) * 100:F0}%)");
            else parts.Add($"mem {mem.CurrentBytes / 1_048_576} MiB (no limit)");
            if (mem.OomKillCount > 0) parts.Add($"OOM kills: {mem.OomKillCount}");
        }
        if (s.Pressure?.CpuSomeAvg10 is { } psiCpu && psiCpu > 0) parts.Add($"PSI cpu.some.avg10={psiCpu:F2}");
        if (s.Pressure?.MemFullAvg10 is { } psiMem && psiMem > 0) parts.Add($"PSI mem.full.avg10={psiMem:F2}");

        var prefix = s.InContainer ? $"Container ({s.CgroupPath ?? "/"}): " : "Host cgroup root: ";
        return parts.Count == 0
            ? prefix + "no actionable signals."
            : prefix + string.Join("; ", parts) + ".";
    }

    private static NextActionHint[] BuildContainerHints(ContainerSignals s)
    {
        if (s.Cpu is { ThrottlePercent: > 5 } cpu)
        {
            return new[]
            {
                new NextActionHint("collect_cpu_sample",
                    $"CPU throttling > 5% ({cpu.ThrottlePercent:F1}% of periods). Sample on-CPU stacks to see which code is hitting the quota.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 10 }),
            };
        }
        if (s.Memory is { UsageFraction: > 0.85 } mem)
        {
            return new[]
            {
                new NextActionHint("inspect_live_heap",
                    $"Memory at {(mem.UsageFraction ?? 0) * 100:F0}% of limit. Inspect the live heap to identify the dominant retainers before the cgroup OOM-kills.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["topTypes"] = 25 }),
            };
        }
        if (!s.InContainer)
        {
            return new[]
            {
                new NextActionHint("snapshot_counters",
                    "Not in a container envelope — runtime EventCounters remain the cheapest first signal.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
            };
        }
        return new[]
        {
            new NextActionHint("snapshot_counters",
                "No kernel-level pressure detected. Move up the stack to runtime counters.",
                new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
        };
    }

    [McpServerTool(
        Name = "get_memory_trend",
        Title = "Sample process memory growth over a window",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Samples OS-level memory metrics (RSS, PSS, anonymous/private pages, page faults) " +
        "at regular intervals over a configurable window, then computes per-second deltas and a " +
        "growth verdict ('growing', 'stable', 'shrinking'). Works on any runtime — no EventPipe " +
        "session required. " +
        "On Linux reads /proc/<pid>/smaps_rollup (Rss, Pss, Anonymous) and /proc/<pid>/stat " +
        "(minflt/majflt) for each sample. On Windows calls GetProcessMemoryInfo " +
        "(WorkingSetSize, PrivateUsage, PageFaultCount). " +
        "Use this as a lightweight memory-leak signal before reaching for heap dumps — it answers " +
        "'is the process growing and how fast?' without walking the heap. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-selects it.")]
    public static async Task<DiagnosticResult<MemoryTrend>> GetMemoryTrend(
        IMemoryTrendCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the observation window in seconds. Must be >= 2. Defaults to 10.")] int durationSeconds = 10,
        [Description("Interval between consecutive samples in seconds. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 2) return InvalidArg<MemoryTrend>(nameof(durationSeconds), "must be >= 2");
        if (sampleEverySeconds < 1) return InvalidArg<MemoryTrend>(nameof(sampleEverySeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<MemoryTrend>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var trend = await collector.CollectAsync(pid, durationSeconds, sampleEverySeconds, cancellationToken).ConfigureAwait(false);

        const double bytesPerMiB = 1_048_576.0;
        var rssMiB = trend.Deltas.RssBytesPerSec / bytesPerMiB;
        var summary = trend.Samples.Count < 2
            ? $"Process {pid}: could not collect enough samples — check Notes for details."
            : $"Process {pid} memory over {durationSeconds}s ({trend.Samples.Count} samples): " +
              $"verdict={trend.Verdict}, " +
              $"rss={trend.Samples[^1].RssBytes / bytesPerMiB:F1} MiB, " +
              $"Δrss={rssMiB:+0.00;-0.00;0.00} MiB/s.";

        var hints = trend.Verdict == "growing"
            ? new[]
            {
                new NextActionHint("inspect_live_heap",
                    $"RSS growing at {rssMiB:F2} MiB/s — inspect the live heap to identify dominant retainers.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["topTypes"] = 25 }),
                new NextActionHint("get_container_signals",
                    "Cross-check memory against cgroup limits before concluding it is a leak.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
            }
            : new[]
            {
                new NextActionHint("snapshot_counters",
                    "Memory looks stable — check runtime counters for CPU/GC pressure.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 5 }),
            };

        var ok = DiagnosticResult.Ok(trend, summary, hints);
        return WithContext(ok, resolved.Context);
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
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
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

        var resolved = await ResolveContextAsync<CounterSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            providers is { Length: > 0 } ? providers : null,
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var cpu = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
        var heap = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gc-heap-size");
        var hint = (cpu?.Value ?? 0) >= 70
            ? new NextActionHint("collect_cpu_sample", $"cpu-usage={cpu!.Value:F1}% over {durationSeconds}s — investigate the hot path.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10, ["topN"] = 25 })
            : new NextActionHint("collect_gc_events", "Counters look quiet — confirm there are no GC pauses before widening scope.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.Counters, snapshot, CollectionHandleTtl);
        var ok = DiagnosticResult.OkWithHandle(
            snapshot,
            $"Captured {snapshot.Counters.Count} counter(s) over {durationSeconds}s — cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}.",
            handle.Id,
            handle.ExpiresAt,
            hint,
            new NextActionHint("query_collection",
                "Drill into this counter snapshot without re-collecting (views: summary, byProvider).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byProvider" }));
        return WithContext(ok, resolved.Context);
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
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload (issue #28 — makes dotnet-assembly-mcp.get_method_source optional when PDBs are reachable). Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")] bool resolveSourceLines = true,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols;srv*https://msdl.microsoft.com/download/symbols'). Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")] int? maxResolvedSources = null,
        [Description("If true, runs the collection as a background job. Returns immediately with a job handle; poll get_collection_status(handle) until status='completed' to retrieve the CpuSample result. Defaults to false (synchronous).")] bool runAsJob = false,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        var effectiveMaxResolved = maxResolvedSources ?? topN;
        if (effectiveMaxResolved < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");

        var resolved = await ResolveContextAsync<CpuSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: effectiveMaxResolved)
            : null;

        if (runAsJob)
        {
            // Detach: the job outlives the MCP request, so we must not cancel it when the
            // request token trips. The runner's own cancel hook is what stops it.
            var jobTtl = TimeSpan.FromSeconds(durationSeconds) + CpuSampleHandleTtl;
            var jobHandle = jobs.Start(
                pid,
                "cpu-sample-job",
                jobTtl,
                async ct =>
                {
                    var jobResult = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, ct).ConfigureAwait(false);
                    var jobSample = jobResult.Summary;
                    var jobTop = jobSample.TopHotspots.Count > 0 ? jobSample.TopHotspots[0] : null;
                    var dataHandle = handles.Register(pid, "cpu-sample", jobResult.Artifact, CpuSampleHandleTtl);
                    var summaryText = jobTop is not null
                        ? $"Captured {jobSample.TotalSamples} samples over {durationSeconds}s. Top method: {jobTop.Frame.Method} ({jobTop.InclusiveSamples} inclusive / {jobTop.ExclusiveSamples} exclusive). Drill into the full call tree with get_call_tree(handle=\"{dataHandle.Id}\")."
                        : $"Captured {jobSample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";
                    var jobOk = DiagnosticResult.OkWithHandle(
                        jobSample,
                        summaryText,
                        dataHandle.Id,
                        dataHandle.ExpiresAt,
                        new NextActionHint("get_call_tree", "Walk the merged caller→callee tree built from the same samples.",
                            new Dictionary<string, object?> { ["handle"] = dataHandle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }));
                    return WithContext(jobOk, ctx);
                });

            var jobAck = DiagnosticResult.OkWithHandle<CpuSample>(
                data: null!,
                $"CPU sampling job started (handle {jobHandle.Id}). Poll get_collection_status(handle=\"{jobHandle.Id}\") after ~{durationSeconds}s to retrieve the result.",
                jobHandle.Id,
                jobHandle.ExpiresAt,
                new NextActionHint("get_collection_status", "Poll the background CPU sampling job until status='completed'.",
                    new Dictionary<string, object?> { ["handle"] = jobHandle.Id }),
                new NextActionHint("cancel_collection", "Abort the background CPU sampling job if the symptom changed.",
                    new Dictionary<string, object?> { ["handle"] = jobHandle.Id }));
            return WithContext(jobAck, ctx);
        }

        CpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("get_diagnostic_capabilities", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var handle = handles.Register(pid, "cpu-sample", result.Artifact, CpuSampleHandleTtl);
        var summary = top is not null
            ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s. Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with get_call_tree(handle=\"{handle.Id}\")."
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("get_call_tree", "Walk the merged caller→callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_exceptions", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, ctx);
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

    [McpServerTool(
        Name = "collect_off_cpu_sample",
        Title = "Collect off-CPU blocking stacks",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Captures off-CPU stacks for the target process — where threads are blocked, for how long, and on which " +
        "kernel/user frame. Companion to collect_cpu_sample: on-CPU sampling shows hot code, off-CPU shows time " +
        "spent waiting (futex, IO, sleep, lock). Closes the 'latency high, CPU low' diagnostic gap that on-CPU " +
        "samples can't see by definition. " +
        "Backend: Linux only in this release — runs 'perf record -a -e sched:sched_switch --call-graph dwarf' " +
        "for durationSeconds. Requires the perf binary in PATH and CAP_PERFMON (or perf_event_paranoid <= -1). " +
        "Windows ETW kernel CSwitch support tracked in issue #41 sub-slice 2b. " +
        "Returns the top-N blocking stacks inline and a handle for query_off_cpu_snapshot drilldown.")]
    public static async Task<DiagnosticResult<OffCpuSnapshot>> CollectOffCpuSample(
        IOffCpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of blocking stacks returned inline (the full set lives behind the handle). Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<OffCpuSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<OffCpuSnapshot>(nameof(topN), "must be >= 1");

        var resolved = await ResolveContextAsync<OffCpuSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        OffCpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("get_diagnostic_capabilities",
                    "Confirm which signals are available on this host before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("get_diagnostic_capabilities",
                    "Check capability matrix; install linux-perf and add CAP_PERFMON to the sidecar securityContext.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var summary = result.Summary;
        var handle = handles.Register(pid, OffCpuHandleKind, result.Artifact, CpuSampleHandleTtl);

        var topStack = summary.TopBlockingStacks.Count > 0 ? summary.TopBlockingStacks[0] : null;
        var summaryText = topStack is not null
            ? $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s. " +
              $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
              $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
              $"Drill with query_off_cpu_snapshot(handle=\"{handle.Id}\")."
            : $"Captured {summary.SchedSwitches} switches but no off-CPU spans closed within the window. " +
              "Either no thread blocked, or wakeups landed outside the capture — try a longer durationSeconds.";

        var ok = DiagnosticResult.OkWithHandle(
            summary,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_off_cpu_snapshot", "Drill into per-thread off-CPU view or a specific stack.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byThread" }),
            new NextActionHint("collect_cpu_sample", "Cross-reference with on-CPU hotspots to separate compute from wait.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, resolved.Context);
    }

    /// <summary>Canonical kind tag for handles backing an <see cref="OffCpuSnapshotArtifact"/>.</summary>
    public const string OffCpuHandleKind = "off-cpu-snapshot";

    [McpServerTool(
        Name = "query_off_cpu_snapshot",
        Title = "Drill into an off-CPU snapshot",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Re-projects a prior collect_off_cpu_sample artifact under a named view, without re-running perf. " +
        "Views: 'topStacks' (default — blocking stacks ranked by off-CPU micros), 'byThread' (per-TID rollup), " +
        "'stack' (full root→leaf frames of a specific stack rank). Use the handle returned by collect_off_cpu_sample. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<OffCpuQueryView> QueryOffCpuSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_off_cpu_sample call.")] string handle,
        [Description("View name: topStacks (default), byThread, stack.")] string view = "topStacks",
        [Description("Maximum items returned for topStacks/byThread. Defaults to 25.")] int topN = 25,
        [Description("Required when view='stack' — 1-based rank of the stack in the top-stacks list.")] int? stackRank = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<OffCpuQueryView>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<OffCpuQueryView>(nameof(topN), "must be >= 1");

        var artifact = handles.TryGet<OffCpuSnapshotArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Off-CPU handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_off_cpu_sample", "Re-run the off-CPU sampler to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        return view.ToLowerInvariant() switch
        {
            "bythread" => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: null,
                    Threads: artifact.Threads.Take(topN).ToList(),
                    Stack: null),
                $"{Math.Min(topN, artifact.Threads.Count)} of {artifact.Threads.Count} threads ranked by off-CPU micros."),
            "stack" => RenderStack(artifact, stackRank),
            "topstacks" or _ => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: artifact.Stacks.Take(topN).ToList(),
                    Threads: null,
                    Stack: null),
                $"Top {Math.Min(topN, artifact.Stacks.Count)} blocking stacks of {artifact.Stacks.Count} distinct."),
        };
    }

    private static DiagnosticResult<OffCpuQueryView> RenderStack(OffCpuSnapshotArtifact artifact, int? stackRank)
    {
        if (stackRank is null || stackRank < 1)
        {
            return InvalidArg<OffCpuQueryView>(nameof(stackRank), "is required for view='stack' and must be >= 1");
        }
        var idx = stackRank.Value - 1;
        if (idx >= artifact.Stacks.Count)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"stackRank={stackRank} exceeds available {artifact.Stacks.Count} stacks.",
                new DiagnosticError("OutOfRange", "Pick a rank within the topStacks list.", stackRank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new NextActionHint("query_off_cpu_snapshot", "List the top stacks first.",
                    new Dictionary<string, object?> { ["view"] = "topStacks" }));
        }
        var s = artifact.Stacks[idx];
        return DiagnosticResult.Ok(
            new OffCpuQueryView("stack", artifact.ProcessId, artifact.TotalOffCpuMicros,
                Stacks: null, Threads: null, Stack: s),
            $"Rank {stackRank}/{artifact.Stacks.Count}: {s.LeafFrame} — {s.OffCpuMicros / 1000.0:F1} ms across {s.OccurrenceCount} switches (state={s.DominantState}).");
    }

    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Default TTL applied to handles emitted by every windowed collector (counters, exceptions,
    /// GC events, EventSource captures). Aligned with the other drill-down kinds so the LLM has
    /// a single mental model: collect once, drill many times within ~10 minutes.
    /// </summary>
    private static readonly TimeSpan CollectionHandleTtl = TimeSpan.FromMinutes(10);

    [McpServerTool(
        Name = "query_collection",
        Title = "Drill into a previously-collected artifact",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Re-projects a previously-collected counter/exception/GC/EventSource artifact under a " +
        "named view, without re-running the underlying EventPipe session. Use the `handle` " +
        "returned by snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source. " +
        "Supported views per kind: counters → summary|byProvider; exception-snapshot → " +
        "summary|byType|recent; gc-events → summary|events|pauseHistogram; event-source → " +
        "summary|byEventName|events. Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CollectionQueryResult> QueryCollection(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collection tool (snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source).")] string handle,
        [Description("View name (kind-dependent). Defaults to 'summary'.")] string? view = null,
        [Description("Cap on inline items for paginated views (recent / events / byType / byEventName). Must be >= 1. Defaults to 50.")] int topN = 50)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CollectionQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<CollectionQueryResult>(nameof(topN), "must be >= 1");

        var entry = handles.TryGetWithKind(handle);
        if (entry is null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError(
                    "HandleExpired",
                    "Collection handles live ~10min and are invalidated when the target process exits.",
                    handle),
                new NextActionHint("snapshot_counters", "Re-run the original collector on the same pid to issue a fresh handle.", null));
        }

        var outcome = CollectionQueryDispatcher.Dispatch(entry.Value.Kind, view, entry.Value.Artifact, topN);

        if (outcome.UnknownKind is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is of kind '{outcome.UnknownKind}' which query_collection does not support.",
                new DiagnosticError(
                    "UnsupportedHandleKind",
                    $"query_collection dispatches over kinds: {string.Join(", ", new[] { CollectionHandleKinds.Counters, CollectionHandleKinds.ExceptionSnapshot, CollectionHandleKinds.GcEvents, CollectionHandleKinds.EventSource })}.",
                    outcome.UnknownKind),
                new NextActionHint("query_heap_snapshot", "Use the kind-specific drill-down tool for heap/thread/cpu handles.", null));
        }
        if (outcome.UnknownView is not null)
        {
            var allowed = outcome.AllowedViews ?? Array.Empty<string>();
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"View '{outcome.UnknownView}' is not defined for kind '{entry.Value.Kind}'.",
                new DiagnosticError(
                    "UnknownView",
                    $"Allowed views: {string.Join(", ", allowed)}.",
                    outcome.UnknownView),
                new NextActionHint("query_collection", "Retry with one of the allowed views.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = allowed.Count > 0 ? allowed[0] : "summary" }));
        }
        if (outcome.InvalidArgument is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Invalid argument: {outcome.InvalidArgument}.",
                new DiagnosticError("InvalidArgument", outcome.InvalidArgument, nameof(topN)));
        }

        var result = outcome.Result!;
        return DiagnosticResult.Ok(
            result,
            $"Rendered view '{result.View}' for kind '{result.Kind}' (collected {result.Duration.TotalSeconds:F1}s starting {result.StartedAt:HH:mm:ss}Z, pid {result.ProcessId}).",
            new NextActionHint("query_collection",
                $"Switch to another view: {string.Join(" | ", CollectionQueryDispatcher.ViewsFor(result.Kind))}.",
                new Dictionary<string, object?> { ["handle"] = handle }));
    }

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
        "Returns total count (always exact), breakdown by exception type (always exact), and " +
        "the first maxRecent individual exception details — when TotalExceptions exceeds " +
        "maxRecent the Recent list is truncated to the head of the stream (the cap that was " +
        "applied is echoed back as ExceptionSnapshot.RecentCap). " +
        "IMPORTANT: start this BEFORE the workload you want to observe — exceptions before the session opens are missed.")]
    public static async Task<DiagnosticResult<ExceptionSnapshot>> CollectExceptions(
        IExceptionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ExceptionSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxRecent < 1) return InvalidArg<ExceptionSnapshot>(nameof(maxRecent), "must be >= 1");

        var resolved = await ResolveContextAsync<ExceptionSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snap = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken).ConfigureAwait(false);
        var topType = snap.ByType.OrderByDescending(c => c.Count).FirstOrDefault();
        var summary = snap.TotalExceptions == 0
            ? $"No managed exceptions thrown in {durationSeconds}s. If you expected some, ensure the collection started before the workload."
            : $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}).";

        var primaryHint = snap.TotalExceptions > 0
            ? new NextActionHint("collect_event_source", "Subscribe to a domain-specific EventSource to correlate with the exception spikes.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 })
            : new NextActionHint("collect_gc_events", "No exception pressure — sweep GC events next.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.ExceptionSnapshot, snap, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            snap,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_collection",
                "Drill into this exception snapshot without re-collecting (views: summary, byType, recent).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byType", ["topN"] = 20 })),
            resolved.Context);
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
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of GC events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<GcSummary>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<GcSummary>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<GcSummary>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var gc = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken).ConfigureAwait(false);
        var summary = gc.TotalCollections == 0
            ? $"No GC activity in {durationSeconds}s — heap is quiet or the workload is idle."
            : $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms.";

        var primaryHint = gc.MaxPauseTime.TotalMilliseconds > 100
            ? new NextActionHint("collect_process_dump",
                $"Max GC pause {gc.MaxPauseTime.TotalMilliseconds:F0}ms is high — capture a WithHeap dump for offline heap analysis.",
                new Dictionary<string, object?> { ["processId"] = pid, ["dumpType"] = "WithHeap" })
            : new NextActionHint("collect_event_source", "GC looks healthy — pivot to a domain EventSource (e.g. System.Net.Http) for application-level signal.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.GcEvents, gc, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            gc,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_collection",
                "Drill into these GC events without re-collecting (views: summary, events, pauseHistogram).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "pauseHistogram" })),
            resolved.Context);
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
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'.")] string providerName,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("EventSource keyword mask. -1 (default) means all keywords.")] long keywords = -1,
        [Description("Event verbosity level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Informational, 5=Verbose). Defaults to 5.")] int eventLevel = 5,
        [Description("Maximum number of captured events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return InvalidArg<EventSourceCapture>(nameof(providerName), "is required");
        if (durationSeconds < 1) return InvalidArg<EventSourceCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<EventSourceCapture>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<EventSourceCapture>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var capture = await collector.CaptureAsync(
            pid, providerName, TimeSpan.FromSeconds(durationSeconds), keywords, eventLevel, maxEvents, cancellationToken).ConfigureAwait(false);

        var summary = capture.Events.Count == 0
            ? $"No events from '{providerName}' in {durationSeconds}s. Verify the provider name and that it's actually instrumented in the target."
            : $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s.";

        var handle = handles.Register(pid, CollectionHandleKinds.EventSource, capture, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            capture,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("snapshot_counters", "Cross-check captured events against runtime counters for the same window.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("query_collection",
                "Drill into this capture without re-collecting (views: summary, byEventName, events).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byEventName" })),
            resolved.Context);
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
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional output directory. If null, defaults to <temp>/dotnet-diagnostics-mcp.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<DumpResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        return await GuardAttachAsync("collect_process_dump", pid, async () =>
        {
            var dump = await dumper.WriteDumpAsync(pid, dumpType, outputDirectory, cancellationToken).ConfigureAwait(false);
            var hint = dumpType == ProcessDumpType.Mini
                ? new NextActionHint("inspect_dump",
                    "Mini dump captured — heap walk unavailable. Re-capture with dumpType='WithHeap' for full inspection.",
                    new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath })
                : new NextActionHint("inspect_dump",
                    "Inspect the dump's managed heap for top-retained types + handoff payload to dotnet-assembly-mcp.",
                    new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath, ["topTypes"] = 20 });
            return WithContext(DiagnosticResult.Ok(
                dump,
                $"Wrote {dumpType} dump for pid {dump.ProcessId} to {dump.FilePath} ({dump.FileSizeBytes:N0} bytes).",
                hint), ctx);
        }, cancellationToken).ConfigureAwait(false);
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
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; adds an extra pass over AppDomains × Modules × Types.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        CancellationToken cancellationToken = default)
    {
        return await GuardAttachAsync("inspect_dump", processId: null, async () =>
        {
            var snapshot = await inspector.InspectAsync(
                dumpFilePath,
                new DumpInspectionOptions(
                    TopTypes: topTypes,
                    IncludeRetentionPaths: includeRetentionPaths,
                    RetentionPathLimit: retentionPathLimit,
                    IncludeStaticFields: includeStaticFields,
                    IncludeDelegateTargets: includeDelegateTargets,
                    IncludeDuplicateStrings: includeDuplicateStrings),
                cancellationToken).ConfigureAwait(false);

            var handle = handles.Register(snapshot.ProcessId, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl, evictWhenProcessExits: false);
            var inspection = snapshot.ToDumpInspection(topTypes, handle.Id);

            var topByBytes = inspection.TopTypesByBytes;
            var summary = topByBytes.Count == 0
                ? $"Inspected {dumpFilePath} — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
                : $"Inspected {dumpFilePath} — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

            var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
            return hint is null
                ? DiagnosticResult.Ok(inspection, summary)
                : DiagnosticResult.Ok(inspection, summary, hint);
        }, cancellationToken).ConfigureAwait(false);
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
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower and lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; lengthens the suspend window.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<LiveHeapInspection>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        return await GuardAttachAsync("inspect_live_heap", pid, async () =>
        {
            var snapshot = await inspector.InspectLiveAsync(
                pid,
                new DumpInspectionOptions(
                    TopTypes: topTypes,
                    IncludeRetentionPaths: includeRetentionPaths,
                    RetentionPathLimit: retentionPathLimit,
                    IncludeStaticFields: includeStaticFields,
                    IncludeDelegateTargets: includeDelegateTargets,
                    IncludeDuplicateStrings: includeDuplicateStrings),
                cancellationToken).ConfigureAwait(false);

            var handle = handles.Register(pid, HeapSnapshotKind, snapshot, HeapSnapshotHandleTtl);
            var inspection = snapshot.ToLiveHeapInspection(topTypes, handle.Id);

            var topByBytes = inspection.TopTypesByBytes;
            var summary = topByBytes.Count == 0
                ? $"Attached to pid {pid} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — runtime {inspection.Runtime.Name} {inspection.Runtime.Version}, heap walk produced no objects. Snapshot handle: `{handle.Id}`."
                : $"Attached to pid {pid} for {inspection.SuspendDuration.TotalMilliseconds:N0} ms — heap {inspection.Heap.TotalBytes:N0} bytes; top retained type: `{topByBytes[0].TypeFullName}` ({topByBytes[0].TotalBytesPercent}% / {topByBytes[0].InstanceCount:N0} instances). Snapshot handle: `{handle.Id}`.";

            var hint = BuildHeapDrilldownHint(handle.Id, topByBytes);
            var result = hint is null
                ? DiagnosticResult.Ok(inspection, summary)
                : DiagnosticResult.Ok(inspection, summary, hint);
            return WithContext(result, ctx);
        }, cancellationToken).ConfigureAwait(false);
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
        "`fragmentation` (per-segment Gen/Kind/Length/Committed/Free bytes — high FreePercent on Gen2/LOH signals fragmentation), " +
        "`static-fields` (top static reference fields by directly-referenced object size — requires the original inspect call to have set includeStaticFields=true), " +
        "`delegate-targets` (delegate / event-handler subscribers grouped by (target type, method) — requires includeDelegateTargets=true), " +
        "`duplicate-strings` (duplicate System.String contents ranked by aggregate retained bytes — requires includeDuplicateStrings=true). " +
        "Handles expire ~10 minutes after the capture and are invalidated when the target process exits (live origin only).")]
    public static DiagnosticResult<HeapSnapshotQueryResult> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets' or 'duplicate-strings'.")] string view = "top-types",
        [Description("Maximum entries to return for any ranked view ('top-types', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings'). Ignored by 'roots-by-kind' and 'retention-paths'.")] int topN = 50,
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
            "static-fields" => QueryStaticFields(snapshot, handle, topN),
            "delegate-targets" => QueryDelegateTargets(snapshot, handle, topN),
            "duplicate-strings" => QueryDuplicateStrings(snapshot, handle, topN),
            _ => InvalidArg<HeapSnapshotQueryResult>(nameof(view), $"must be 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets' or 'duplicate-strings' (got '{view}')"),
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

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryStaticFields(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.StaticFields is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without static-field walking.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeStaticFields=true.", handle));
        }
        var slice = snapshot.StaticFields.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no static reference fields with directly-referenced objects."
            : $"Returning {slice.Length} static field(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top retainer: `{slice[0].ContainingTypeFullName}.{slice[0].FieldName}` → `{slice[0].ValueTypeFullName ?? "<unknown>"}` ({slice[0].DirectlyReferencedBytes:N0} bytes).";
        var result = new HeapSnapshotQueryResult(handle, "static-fields", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            StaticFields = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryDelegateTargets(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DelegateTargets is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without delegate-target aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDelegateTargets=true.", handle));
        }
        var slice = snapshot.DelegateTargets.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no delegate targets (no MulticastDelegate instances detected)."
            : $"Returning {slice.Length} delegate target group(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top subscriber: `{slice[0].DeclaringTypeFullName}.{slice[0].MethodName}` (target=`{slice[0].TargetTypeFullName ?? "<static>"}`) — {slice[0].SubscriberCount:N0} subscription(s). High subscription counts on long-lived publishers are a classic event-handler-leak signal.";
        var result = new HeapSnapshotQueryResult(handle, "delegate-targets", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DelegateTargets = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryDuplicateStrings(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DuplicateStrings is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without duplicate-string aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDuplicateStrings=true.", handle));
        }
        var slice = snapshot.DuplicateStrings.Take(topN).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no duplicated System.String contents."
            : $"Returning {slice.Length} duplicated string(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: `{slice[0].Preview}` — {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes. Consider string.Intern() / a cache for the hottest entries.";
        var result = new HeapSnapshotQueryResult(handle, "duplicate-strings", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DuplicateStrings = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static readonly TimeSpan ThreadSnapshotHandleTtl = TimeSpan.FromMinutes(10);
    internal const string ThreadSnapshotKind = "thread-snapshot";

    [McpServerTool(
        Name = "collect_thread_snapshot",
        Title = "Capture managed threads + locks from a live process or dump",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Captures a single-point-in-time snapshot of all managed threads (state, stack frames with " +
        "MethodIdentity handoff, inferred wait reason) plus the SyncBlock-based lock graph (object " +
        "address, owning thread, waiter count). Supply at most ONE of processId or dumpFilePath: " +
        "processId attaches via ClrMD with suspend (typically sub-second on ≤100 threads); " +
        "dumpFilePath analyses an already-captured WithHeap/Full dump offline. When both are omitted " +
        "the server auto-selects a live .NET process (live mode). Returns inline threads-summary + " +
        "lock-graph headlines plus a handle (~10min TTL) the LLM can drill into via " +
        "query_thread_snapshot. Dump-origin handles are NOT evicted when the producer PID exits.")]
    public static async Task<DiagnosticResult<ThreadSnapshotQueryResult>> CollectThreadSnapshot(
        IThreadSnapshotInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Maximum stack frames captured per thread. Defaults to 64.")] int maxFramesPerThread = 64,
        [Description("Include runtime frames (PInvoke trampolines, etc.) without an associated managed method. Off by default.")] bool includeRuntimeFrames = false,
        [Description("Include pure native frames where ClrMD cannot resolve a method. Off by default.")] bool includeNativeFrames = false,
        CancellationToken cancellationToken = default)
    {
        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }
        if (maxFramesPerThread < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), "must be >= 1");
        if (maxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), $"must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap} (bounds the live-attach suspend window)");

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            // Live path — auto-resolve when caller omitted processId.
            var resolved = await ResolveContextAsync<ThreadSnapshotQueryResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        return await GuardAttachAsync("collect_thread_snapshot", hasDump ? (int?)null : livePid, async () =>
        {
            var opts = new ThreadSnapshotOptions(maxFramesPerThread, includeRuntimeFrames, includeNativeFrames);
            ThreadSnapshotArtifact snapshot;
            bool evictOnExit;
            if (hasDump)
            {
                snapshot = await inspector.InspectDumpAsync(dumpFilePath!, opts, cancellationToken).ConfigureAwait(false);
                evictOnExit = false;
            }
            else
            {
                snapshot = await inspector.InspectLiveAsync(livePid, opts, cancellationToken).ConfigureAwait(false);
                evictOnExit = true;
            }

            var handle = handles.Register(snapshot.ProcessId, ThreadSnapshotKind, snapshot, ThreadSnapshotHandleTtl, evictWhenProcessExits: evictOnExit);
            var origin = snapshot.Origin.ToString().ToLowerInvariant();
            var blocked = snapshot.Threads.Count(t => t.IsLikelyBlocked);
            var contended = snapshot.Locks.Count(l => l.IsContended);
            var summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_thread_snapshot(view=top-blocked|threads-summary|stack|lock-graph).";
            var summaryView = new ThreadSnapshotQueryResult(handle.Id, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                Threads = snapshot.Threads.Take(25).ToArray(),
                Locks = snapshot.Locks.Take(25).ToArray(),
            };

            var hint = blocked > 0 || contended > 0
                ? new NextActionHint("query_thread_snapshot",
                    "Drill into the top blocked threads or the contended lock graph.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-blocked" })
                : null;

            var result = hint is null
                ? DiagnosticResult.Ok(summaryView, summary)
                : DiagnosticResult.Ok(summaryView, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken).ConfigureAwait(false);
    }

    [McpServerTool(
        Name = "capture_method_bytes",
        Title = "Capture JIT-emitted native code for a managed method",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Reads JIT-emitted machine code for a single managed method out of the runtime's " +
        "code-heap and writes it to disk as a header-less raw blob, then emits a handoff " +
        "hint to dotnet-native-mcp.disassemble(rawBlob=true). Closes the only gap left in " +
        "disasm coverage (NativeAOT and R2R are already covered on-disk by dotnet-native-mcp; " +
        "JIT-emitted code only lives in the live process / dump). Useful for diffing tier " +
        "promotion (Tier0 → Tier1+PGO) of a hot method observed via " +
        "collect_event_source(\"Microsoft-Windows-DotNETRuntime\", keywords=Jit|JitTracing). " +
        "Supply at most ONE of processId or dumpFilePath: processId attaches via ClrMD with " +
        "suspend (sub-second for a single method); dumpFilePath analyses a WithHeap/Full dump " +
        "offline. When both are omitted the server auto-selects a live .NET process. The " +
        "method is identified by its (moduleVersionId, metadataToken) handoff key — the same " +
        "key dotnet-assembly-mcp uses. Optional codeAddress (e.g. from a MethodLoad_V2 event) " +
        "is a fast-path; tier is an informational label echoed back (ClrMD does not expose " +
        "the JIT OptimizationTier directly). NativeAOT and pure ReadyToRun targets are not " +
        "supported — disassemble the on-disk binary with dotnet-native-mcp.disassemble instead.")]
    public static async Task<DiagnosticResult<CapturedMethodBytes>> CaptureMethodBytes(
        IJitMethodCapturer capturer,
        IProcessContextResolver resolver,
        [Description("PE module MVID (D format, e.g. '6f5c9bf0-1e0b-4f3b-9a8e-…') of the assembly that declares the method. Required.")] string moduleVersionId,
        [Description("IL method-def metadata token (table 0x06). Accepts decimal or hex (0x06000142). Required.")] string metadataToken,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Optional fast-path: a code address already observed for this method (e.g. MethodCodeStart from MethodLoad_V2). Hex (with or without 0x prefix) or decimal. Mismatches with (moduleVersionId, metadataToken) surface as a warning, not a hard error.")] string? codeAddress = null,
        [Description("Optional tier label echoed back on the result (e.g. 'Tier0', 'Tier1', 'Tier1OSR'). ClrMD does not expose the JIT OptimizationTier directly; this field is informational. The authoritative MethodCompilationType (None/Jit/Ngen) is always returned.")] string? tier = null,
        [Description("Output directory for the .bin file(s). Defaults to {temp}/dotnet-diagnostics-mcp/method-bytes/{pid}/.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (string.IsNullOrWhiteSpace(metadataToken)) return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "is required");
        if (!TryParseHexOrInt(metadataToken, out var tokenValue) || tokenValue <= 0)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "must be a positive integer (decimal or 0x-prefixed hex)");
        }

        ulong? codeAddressValue = null;
        if (!string.IsNullOrWhiteSpace(codeAddress))
        {
            if (!TryParseUnsignedHexOrInt(codeAddress, out var addr) || addr == 0)
            {
                return InvalidArg<CapturedMethodBytes>(nameof(codeAddress), "must be a non-zero address (decimal or 0x-prefixed hex)");
            }
            codeAddressValue = addr;
        }

        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            var resolved = await ResolveContextAsync<CapturedMethodBytes>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        var request = new MethodCaptureRequest(
            ModuleVersionId: mvid,
            MetadataToken: tokenValue,
            CodeAddress: codeAddressValue,
            Tier: tier,
            OutputDirectory: outputDirectory);

        return await GuardAttachAsync("capture_method_bytes", hasDump ? (int?)null : livePid, async () =>
        {
            var captured = hasDump
                ? await capturer.CaptureFromDumpAsync(dumpFilePath!, request, cancellationToken).ConfigureAwait(false)
                : await capturer.CaptureLiveAsync(livePid, request, cancellationToken).ConfigureAwait(false);

            var summary = BuildCaptureSummary(captured);
            var hint = BuildDisassembleHint(captured);
            var result = hint is null
                ? DiagnosticResult.Ok(captured, summary)
                : DiagnosticResult.Ok(captured, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCaptureSummary(CapturedMethodBytes captured)
    {
        var typeFqn = captured.Method.TypeFullName ?? "<unknown type>";
        var methodName = captured.Method.MethodName;
        var origin = captured.Origin.ToString().ToLowerInvariant();
        if (captured.Regions.Count == 0)
        {
            return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId}: no JIT-emitted code present (method not yet JITted, abstract/extern, or ReadyToRun-only). " +
                   "Check warnings on the payload for details.";
        }
        var sizes = string.Join(", ", captured.Regions.Select(r => $"{r.Region}={r.Size:N0} B"));
        var first = captured.Regions[0];
        return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId} ({captured.Architecture}, {first.CompilationType ?? "?"}): " +
               $"{sizes}. Wrote {captured.Regions.Count} file(s) under {captured.OutputDirectory}.";
    }

    private static NextActionHint? BuildDisassembleHint(CapturedMethodBytes captured)
    {
        if (captured.Regions.Count == 0) return null;
        var hot = captured.Regions.FirstOrDefault(r => r.Region == "Hot") ?? captured.Regions[0];
        return new NextActionHint(
            "dotnet-native-mcp.disassemble",
            $"Disassemble the captured {hot.Region} region. The file is a raw blob with no PE/ELF/Mach-O header — pass rawBlob=true so the disassembler skips header validation.",
            new Dictionary<string, object?>
            {
                ["imagePath"] = hot.FilePath,
                ["rawBlob"] = true,
                ["rva"] = 0,
                ["size"] = hot.Size,
                ["architecture"] = hot.Architecture,
                ["baseAddress"] = hot.BaseAddress,
            });
    }

    private static bool TryParseHexOrInt(string value, out int result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseUnsignedHexOrInt(string value, out ulong result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    [McpServerTool(
        Name = "query_thread_snapshot",
        Title = "Drill into a thread + lock snapshot",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Returns a slice of a thread snapshot previously captured by collect_thread_snapshot, addressed by its handle. Views: " +
        "`threads-summary` (every managed thread with state + top frame), " +
        "`stack` (full captured frames of one thread — requires `threadId`, the managed thread id), " +
        "`lock-graph` (every SyncBlock that is held or contended, sorted by waiter count then recursion), " +
        "`top-blocked` (threads ranked by IsLikelyBlocked then LockCount — fastest path to spot contention). " +
        "Handles expire ~10 minutes after capture; live-origin handles are invalidated when the target PID exits.")]
    public static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by collect_thread_snapshot.")] string handle,
        [Description("Which slice to return: 'threads-summary', 'stack', 'lock-graph' or 'top-blocked'.")] string view = "top-blocked",
        [Description("For view='stack': managed thread id (ManagedThreadId) to return frames for. Ignored by other views.")] int? threadId = null,
        [Description("Maximum entries returned by views that produce a ranked list ('threads-summary', 'top-blocked', 'lock-graph'). Defaults to 50.")] int topN = 50)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ThreadSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Thread snapshot handles live ~10min; live-origin handles are also invalidated when the target PID exits.", handle),
                new NextActionHint("collect_thread_snapshot", "Re-capture to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        var origin = snapshot.Origin.ToString().ToLowerInvariant();
        var normalized = view.Trim().ToLowerInvariant();
        return normalized switch
        {
            "threads-summary" => QueryThreadsSummary(snapshot, handle, origin, topN),
            "stack" => QueryThreadStack(snapshot, handle, origin, threadId),
            "lock-graph" => QueryLockGraph(snapshot, handle, origin, topN),
            "top-blocked" => QueryTopBlocked(snapshot, handle, origin, topN),
            _ => InvalidArg<ThreadSnapshotQueryResult>(nameof(view), $"must be 'threads-summary', 'stack', 'lock-graph' or 'top-blocked' (got '{view}')"),
        };
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadsSummary(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads.Take(topN).ToArray();
        var summary = $"Returning {ordered.Length}/{snapshot.Threads.Count} thread(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadStack(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int? threadId)
    {
        if (threadId is null)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(threadId), "is required for view='stack'");
        }
        var thread = snapshot.Threads.FirstOrDefault(t => t.ManagedThreadId == threadId.Value);
        if (thread is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Managed thread {threadId.Value} not present in snapshot '{handle}'.",
                new DiagnosticError("ThreadNotFound", "The captured snapshot does not contain this managed thread id.", threadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new NextActionHint("query_thread_snapshot",
                    "List the captured threads first.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "threads-summary" }));
        }
        var summary = $"Stack of managed thread {thread.ManagedThreadId} (OS {thread.OSThreadId}, state {thread.State}) from snapshot '{handle}' — {thread.Frames.Count} frame(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "stack", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                Thread = thread,
                ThreadId = thread.ManagedThreadId,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryLockGraph(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Locks.Take(topN).ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' contains no held or contended SyncBlocks."
            : $"Returning {ordered.Length}/{snapshot.Locks.Count} SyncBlock(s) from snapshot '{handle}'. Most contended: object 0x{ordered[0].ObjectAddress:x} ({ordered[0].ObjectTypeFullName ?? "<unknown>"}) — {ordered[0].WaitingThreadCount} waiter(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "lock-graph", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Locks = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryTopBlocked(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads
            .OrderByDescending(t => t.IsLikelyBlocked)
            .ThenByDescending(t => t.LockCount)
            .ThenByDescending(t => t.Frames.Count)
            .Take(topN)
            .ToArray();
        var blocked = ordered.Count(t => t.IsLikelyBlocked);
        var summary = $"Returning {ordered.Length} thread(s) from snapshot '{handle}' ranked by likely-blocked then LockCount — {blocked} flagged as likely blocked.";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
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
    public static async Task<DiagnosticResult<InvestigationPlan>> StartInvestigation(
        IInvestigationPlanner planner,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Plain-language symptom, e.g. 'high latency on /checkout since v2025.10'. Required for cold mode; optional for warm/hypothesis.")] string? symptom = null,
        [Description("Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.")] string? hypothesis = null,
        [Description("Baseline snapshot from a prior investigation (JSON of BaselineHandle). Triggers warm mode.")] BaselineHandle? baseline = null,
        [Description("Optional hard limit on tool calls before forcing summarization. Defaults to 8.")] int maxToolCalls = 8,
        [Description("If true, collect_process_dump steps are marked approval-gated. Defaults to true.")] bool dumpRequiresApproval = true,
        CancellationToken cancellationToken = default)
    {
        if (maxToolCalls < 1) return InvalidArg<InvestigationPlan>(nameof(maxToolCalls), "must be >= 1");

        var resolved = await ResolveContextAsync<InvestigationPlan>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var request = new InvestigationRequest(
            ProcessId: pid,
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
        return WithContext(DiagnosticResult.Ok(
            plan,
            summary,
            new NextActionHint(plan.NextStep.ToolName, plan.NextStep.Rationale, hintParams)),
            resolved.Context);
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
            ElapsedSeconds: snap.ElapsedSeconds,
            CompletedAt: snap.CompletedAt,
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

    /// <summary>
    /// Wraps a tool body that attaches to a live process via ClrMD / EventPipe / dotnet-diagnostics
    /// and translates known failure shapes into a structured <see cref="DiagnosticResult{T}"/>.
    /// Without this, uncaught exceptions hit the MCP SDK envelope and the client only sees
    /// "An error occurred invoking 'X'." — leaving the LLM blind to PTRACE/permission/process-exit
    /// distinctions. See #32.
    /// </summary>
    private static async Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ClassifyAttachFailure<T>(tool, processId, ex);
        }
    }

    private static DiagnosticResult<T> ClassifyAttachFailure<T>(string tool, int? processId, Exception ex)
    {
        var typeName = ex.GetType().FullName ?? ex.GetType().Name;
        var message = ex.Message ?? "(no message)";
        var pidHint = processId is int p && p > 0 ? $" (pid {p})" : string.Empty;

        if (ex is ServerNotAvailableException)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} could not reach the diagnostic socket{pidHint}.",
                new DiagnosticError("EndpointUnavailable", message, typeName),
                new NextActionHint("list_dotnet_processes", "Re-list processes. Common cause: sidecar UID mismatch with target, or process has exited."));
        }

        // ClrMD wraps Linux ptrace failures (errno EPERM/ESRCH) in ClrDiagnosticsException.
        // Match on type-name-suffix to avoid taking a hard reference here. "operation not
        // permitted" is the canonical EPERM wording; also walk inner exceptions because
        // ClrMD often nests a Win32Exception with NativeErrorCode==1.
        var isPtraceFailure = (typeName.EndsWith("ClrDiagnosticsException", StringComparison.Ordinal)
                               && (message.Contains("PTRACE", StringComparison.OrdinalIgnoreCase)
                                   || message.Contains("permission", StringComparison.OrdinalIgnoreCase)
                                   || message.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)))
                              || HasEpermInChain(ex);
        if (isPtraceFailure)
        {
            // Probe the live host now so the error envelope carries the exact mitigation
            // (e.g. "ptrace_scope=1 and sidecar lacks CAP_SYS_PTRACE") rather than the
            // generic "check ptrace_scope/CAP_SYS_PTRACE/UID" boilerplate. The probe is
            // cheap (two /proc reads on Linux) and pure on hot failure paths.
            var ptrace = DotnetDiagnosticsMcp.Core.Capabilities.PtraceProbe.Detect();
            var headline = ptrace.CanAttach
                ? $"{tool} could not attach{pidHint}: ptrace was denied even though the sidecar's static capability probe expected attach to succeed ({ptrace.Reason}). Likely cause: target process exited, or it runs under a different UID."
                : $"{tool} could not attach{pidHint}: ptrace was denied — {ptrace.Reason}";

            return DiagnosticResult.Fail<T>(
                headline,
                new DiagnosticError("PermissionDenied", message, typeName),
                new NextActionHint("get_diagnostic_capabilities", "Re-check sidecar capabilities (CanAttachClrMD, AttachClrMdReason) so the LLM can route around ClrMD-backed tools entirely.",
                    processId is int pidForCap && pidForCap > 0 ? new Dictionary<string, object?> { ["processId"] = pidForCap } : null),
                new NextActionHint("inspect_dump", "Fall back to a dump-based workflow (collect_process_dump then inspect_dump) when ptrace cannot be granted.",
                    processId is int pp && pp > 0 ? new Dictionary<string, object?> { ["processId"] = pp } : null));
        }

        if (ex is UnauthorizedAccessException)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} was denied access{pidHint}.",
                new DiagnosticError("PermissionDenied", message, typeName),
                new NextActionHint("list_dotnet_processes", "Verify the MCP server runs as the same UID as the target process."));
        }

        if (ex is ArgumentException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} rejected the request{pidHint}: {message}",
                new DiagnosticError("InvalidArgument", message, typeName));
        }

        return DiagnosticResult.Fail<T>(
            $"{tool} failed{pidHint}: {message}",
            new DiagnosticError("Internal", message, typeName));
    }

    private static bool HasEpermInChain(Exception ex)
    {
        for (Exception? cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 1 /* EPERM */)
            {
                return true;
            }
            if (cur.Message is string m
                && m.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    // ---- bootstrap implícito helpers (issue #42) ------------------------------------
    //
    // Every tool that targets a live .NET process accepts processId as optional. When the
    // caller omits it the resolver auto-selects the lone candidate; on ambiguity / nothing
    // visible we translate the resolver outcome into a structured DiagnosticResult so the
    // LLM never has to interpret a thrown exception. Successful responses carry the
    // resolved capability digest on DiagnosticResult.ResolvedProcess so the obligatory
    // list_dotnet_processes + get_diagnostic_capabilities opener can be skipped entirely.

    private readonly record struct ResolvedContext<T>(
        int ProcessId,
        ProcessContext? Context,
        DiagnosticResult<T>? Failure);

    private static async Task<ResolvedContext<T>> ResolveContextAsync<T>(
        IProcessContextResolver resolver,
        int? processId,
        CancellationToken cancellationToken)
    {
        var resolution = await resolver.ResolveAsync(processId, cancellationToken).ConfigureAwait(false);
        if (resolution.Error is null)
        {
            var ctx = resolution.Context!;
            return new ResolvedContext<T>(ctx.ProcessId, ctx, Failure: null);
        }

        var failure = BuildResolutionFailure<T>(resolution);
        return new ResolvedContext<T>(ProcessId: 0, Context: null, Failure: failure);
    }

    private static DiagnosticResult<T> BuildResolutionFailure<T>(ProcessContextResolution resolution)
    {
        var error = resolution.Error!;
        return error.Kind switch
        {
            "NoDotnetProcessFound" => DiagnosticResult.Fail<T>(
                "No .NET process is visible to the diagnostic IPC on this host.",
                error,
                new NextActionHint(
                    "list_dotnet_processes",
                    "Confirm the target is running and shares your PID namespace + UID (containers/K8s).")),

            "AmbiguousDotnetProcess" => DiagnosticResult.Fail<T>(
                $"{resolution.Candidates?.Count ?? 0} .NET processes visible — pass processId explicitly.",
                error,
                new NextActionHint(
                    "list_dotnet_processes",
                    "Inspect the candidate list inline below and re-issue the call with the chosen processId.",
                    resolution.Candidates is { Count: > 0 }
                        ? new Dictionary<string, object?> { ["candidates"] = resolution.Candidates.Take(5).Select(c => new { c.ProcessId, c.ManagedEntrypointAssemblyName }).ToArray() }
                        : null)),

            "EndpointUnavailable" => DiagnosticResult.Fail<T>(
                error.Message,
                error,
                new NextActionHint(
                    "list_dotnet_processes",
                    "Re-list processes — the target may have exited or the sidecar UID may not match.")),

            _ => DiagnosticResult.Fail<T>(error.Message, error),
        };
    }

    private static DiagnosticResult<T> WithContext<T>(
        DiagnosticResult<T> result,
        ProcessContext? context)
        => context is null ? result : result with { ResolvedProcess = context };
}

/// <summary>Tool-facing projection of a <see cref="DotnetDiagnosticsMcp.Core.Jobs.CollectionJobSnapshot"/>.</summary>
/// <remarks>
/// Order matters: nullable parameters must have explicit <c>= null</c> defaults so the
/// JSON schema generator (used by the MCP SDK to advertise <c>outputSchema</c>) does NOT
/// mark them as required. The SDK serializes with <c>JsonIgnoreCondition.WhenWritingNull</c>,
/// so a missing default produces "required + omitted" mismatches and clients (Copilot CLI,
/// Claude Code) reject the response with <c>-32602 Structured content does not match the
/// tool's output schema: data/data must have required property 'X'</c>. Regression: issue #61.
/// </remarks>
public sealed record CollectionStatusReport(
    string Handle,
    string Kind,
    int ProcessId,
    string Status,
    DateTimeOffset StartedAt,
    double ElapsedSeconds,
    DateTimeOffset? CompletedAt = null,
    object? Result = null,
    DiagnosticError? Error = null);

/// <summary>Tool-facing acknowledgement of a cancel_collection call.</summary>
public sealed record CancelCollectionReport(string Handle, bool CancellationRequested);
