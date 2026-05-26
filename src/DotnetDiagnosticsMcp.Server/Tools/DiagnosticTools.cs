using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Db;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Logs;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Investigation;
using DotnetDiagnosticsMcp.Core.Jit;
using DotnetDiagnosticsMcp.Core.JitCapture;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Protocol;
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
    private static readonly string[] HostingCounterProviders = ["Microsoft.AspNetCore.Hosting"];

    [RequireScope("read-counters")]
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
                new NextActionHint("inspect_process", "Re-run once the target is up to confirm the runtime exposes a diagnostic endpoint."));
        }

        var preview = string.Join(", ", processes.Take(3).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
        return DiagnosticResult.Ok(
            processes,
            $"Found {processes.Count} .NET process(es): {preview}{(processes.Count > 3 ? ", …" : "")}.",
            new NextActionHint(
                "inspect_process",
                "Probe the target process to confirm which collectors are supported (CoreCLR vs NativeAOT).",
                new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = processes[0].ProcessId }));
    }

    [RequireScope("read-counters")]
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
                new NextActionHint("inspect_process", "List attachable .NET processes and pick a valid pid."));
        }

        var result = DiagnosticResult.Ok(
            process,
            $"Process {process.ProcessId} — {process.ManagedEntrypointAssemblyName ?? "<unknown>"} on .NET {process.RuntimeVersion} ({process.OperatingSystem}/{process.ProcessArchitecture}).",
            new NextActionHint("collect_events", "Cheap first signal: CPU/memory/GC/thread-pool sweep before any sampling.",
                new Dictionary<string, object?> { ["processId"] = process.ProcessId, ["durationSeconds"] = 5 }));
        return WithContext(result, resolved.Context);
    }

    [RequireScope("read-counters")]
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
                ? new NextActionHint("collect_events", "Cheap first signal: CPU/memory/GC/thread-pool. Run before reaching for sampling.",
                    new Dictionary<string, object?> { ["processId"] = resolved.ProcessId, ["durationSeconds"] = 5 })
                : new NextActionHint("collect_events", "NativeAOT: CPU sampling unavailable. Counters + EventSource + dumps still work.",
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
                new NextActionHint("inspect_process", "Re-list processes. Common cause: sidecar UID mismatch with target."));
        }
    }

    [RequireScope("read-counters")]
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
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the verbose Notes (caveats about cgroup v1, missing PSI, etc.) and keeps only the actionable signals. 'detail' / 'raw' include all Notes.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveContextAsync<ContainerSignals>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        var signals = await collector.CollectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);

        var hints = BuildContainerHints(signals);
        var summary = SummariseContainerSignals(signals);

        var inlinePayload = signals;
        if (depth == SamplingDepth.Summary && signals.Notes.Count > 0)
        {
            inlinePayload = signals with { Notes = Array.Empty<string>() };
        }

        var ok = DiagnosticResult.Ok(inlinePayload, summary, hints);
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
                new NextActionHint("collect_sample",
                    $"CPU throttling > 5% ({cpu.ThrottlePercent:F1}% of periods). Sample on-CPU stacks to see which code is hitting the quota.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 10 }),
            };
        }
        if (s.Memory is { UsageFraction: > 0.85 } mem)
        {
            return new[]
            {
                new NextActionHint("inspect_heap",
                    $"Memory at {(mem.UsageFraction ?? 0) * 100:F0}% of limit. Inspect the live heap to identify the dominant retainers before the cgroup OOM-kills.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["topTypes"] = 25 }),
            };
        }
        if (!s.InContainer)
        {
            return new[]
            {
                new NextActionHint("collect_events",
                    "Not in a container envelope — runtime EventCounters remain the cheapest first signal.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
            };
        }
        return new[]
        {
            new NextActionHint("collect_events",
                "No kernel-level pressure detected. Move up the stack to runtime counters.",
                new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
        };
    }

    [RequireScope("read-counters")]
    [Description(
        "Samples OS-level memory metrics (RSS, PSS, anonymous/private pages, page faults) " +
        "at regular intervals over a configurable window, then computes per-second deltas and a " +
        "growth verdict ('growing', 'stable', 'shrinking'). Works on any OS process — CoreCLR, " +
        "NativeAOT, or non-.NET — no EventPipe session required. " +
        "On Linux reads /proc/<pid>/smaps_rollup (Rss, Pss, Anonymous) with an automatic " +
        "fallback to /proc/<pid>/smaps accumulation on kernels < 4.14, and /proc/<pid>/stat " +
        "(minflt/majflt) for page-fault counters. On Windows calls GetProcessMemoryInfo " +
        "(WorkingSetSize, PrivateUsage, PageFaultCount). " +
        "Use this as a lightweight memory-leak signal before reaching for heap dumps — it answers " +
        "'is the process growing and how fast?' without walking the heap. " +
        "When processId is provided it is used directly as the OS pid (no .NET IPC check). " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
    public static async Task<DiagnosticResult<MemoryTrend>> GetMemoryTrend(
        IMemoryTrendCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Duration of the observation window in seconds. Must be >= 2. Defaults to 10.")] int durationSeconds = 10,
        [Description("Interval between consecutive samples in seconds. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 2) return InvalidArg<MemoryTrend>(nameof(durationSeconds), "must be >= 2");
        if (sampleEverySeconds < 1) return InvalidArg<MemoryTrend>(nameof(sampleEverySeconds), "must be >= 1");

        int pid;
        ProcessContext? context = null;

        if (processId is > 0)
        {
            // Explicit OS pid: bypass the .NET diagnostic IPC resolver so the tool works on
            // any process — NativeAOT, non-.NET, or any pid not visible to the IPC socket.
            pid = processId.Value;
        }
        else if (processId is < 0)
        {
            return InvalidArg<MemoryTrend>(nameof(processId), "must be a positive process id");
        }
        else
        {
            // null or 0 → auto-resolve via the .NET diagnostic IPC (finds the lone .NET process).
            var resolved = await ResolveContextAsync<MemoryTrend>(resolver, null, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            pid = resolved.ProcessId;
            context = resolved.Context;
        }

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
                new NextActionHint("inspect_heap",
                    $"RSS growing at {rssMiB:F2} MiB/s — inspect the live heap to identify dominant retainers.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["topTypes"] = 25 }),
                new NextActionHint("inspect_process",
                    "Cross-check memory against cgroup limits before concluding it is a leak.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
            }
            : new[]
            {
                new NextActionHint("collect_events",
                    "Memory looks stable — check runtime counters for CPU/GC pressure.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 5 }),
            };

        var ok = DiagnosticResult.Ok(trend, summary, hints);
        return WithContext(ok, context);
    }

    [RequireScope("read-counters")]
    [Description(
        "Reads a managed process's runtime configuration: best-effort GC and ThreadPool settings from a short ClrMD attach, tiered-compilation overrides from filtered runtime env vars, and a forward-compatible appContextSwitches field that currently stays empty with an explanatory note. " +
        "Environment variables are filtered to the DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_ prefixes as a hard security boundary. " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
    public static async Task<DiagnosticResult<RuntimeConfigView>> GetRuntimeConfig(
        IRuntimeConfigInspector inspector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
    {
        if (processId is < 0)
        {
            return InvalidArg<RuntimeConfigView>(nameof(processId), "must be a positive process id");
        }

        var resolved = await ResolveContextAsync<RuntimeConfigView>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var runtimeConfig = await inspector.InspectAsync(resolved.ProcessId, cancellationToken).ConfigureAwait(false);
        var summary = SummariseRuntimeConfig(runtimeConfig);
        var hints = BuildRuntimeConfigHints(runtimeConfig);
        return WithContext(DiagnosticResult.Ok(runtimeConfig, summary, hints), resolved.Context);
    }

    [RequireScope("read-counters")]
    [Description(
        "Inspects OS-level FD / handle / socket state for a process. On Linux it reads /proc/<pid>/fd, /proc/<pid>/net/tcp{,6} and /proc/<pid>/limits to classify descriptors, aggregate TCP states and compute the nofile usage fraction. " +
        "On Windows it queries GetProcessHandleCount and reports a note that per-handle and per-socket breakdown is not yet implemented. " +
        "durationSeconds=0 returns a single snapshot (default); durationSeconds >= 2 samples a short trend window. " +
        "When processId is provided it is used directly as the OS pid (no .NET IPC check). " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
    public static async Task<DiagnosticResult<ProcessResources>> GetProcessResources(
        IProcessResourcesCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Observation window length in seconds. 0 returns a single snapshot (default); values >= 2 enable trend mode.")] int durationSeconds = 0,
        [Description("Interval between consecutive samples in seconds when trend mode is enabled. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 0 || durationSeconds == 1)
        {
            return InvalidArg<ProcessResources>(nameof(durationSeconds), "must be 0 or >= 2");
        }

        if (sampleEverySeconds < 1)
        {
            return InvalidArg<ProcessResources>(nameof(sampleEverySeconds), "must be >= 1");
        }

        int pid;
        ProcessContext? context = null;

        if (processId is > 0)
        {
            pid = processId.Value;
        }
        else if (processId is < 0)
        {
            return InvalidArg<ProcessResources>(nameof(processId), "must be a positive process id");
        }
        else
        {
            var resolved = await ResolveContextAsync<ProcessResources>(resolver, null, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            pid = resolved.ProcessId;
            context = resolved.Context;
        }

        var resources = await collector.CollectAsync(pid, durationSeconds, sampleEverySeconds, cancellationToken).ConfigureAwait(false);
        var summary = SummariseProcessResources(resources, durationSeconds);
        var hints = BuildProcessResourceHints(resources, durationSeconds);
        return WithContext(DiagnosticResult.Ok(resources, summary, hints), context);
    }

    public static async Task<DiagnosticResult<RequestsNowSnapshot>> GetRequestsNow(
        IRequestsNowCollector collector,
        IProcessContextResolver resolver,
        int? processId = null,
        CancellationToken cancellationToken = default)
    {
        const int windowSeconds = 2;
        const int topFrames = 8;

        if (processId is < 0)
        {
            return InvalidArg<RequestsNowSnapshot>(nameof(processId), "must be a positive process id");
        }

        var resolved = await ResolveContextAsync<RequestsNowSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null)
        {
            return resolved.Failure;
        }

        var snapshot = await collector
            .CollectAsync(resolved.ProcessId, TimeSpan.FromSeconds(windowSeconds), topFrames, cancellationToken)
            .ConfigureAwait(false);
        var summary = SummariseRequestsNow(snapshot, windowSeconds);
        var hints = BuildRequestsNowHints(snapshot);
        return WithContext(DiagnosticResult.Ok(snapshot, summary, hints), resolved.Context);
    }

    private static string SummariseRuntimeConfig(RuntimeConfigView runtimeConfig)
    {
        var parts = new List<string>();
        if (runtimeConfig.Gc is { } gc)
        {
            parts.Add($"GC server={gc.IsServerGc}, concurrent={gc.IsConcurrent?.ToString() ?? "unknown"}, background={gc.IsBackground?.ToString() ?? "unknown"}, heaps={gc.HeapCount}");
        }

        if (runtimeConfig.ThreadPool is { } threadPool)
        {
            parts.Add($"ThreadPool worker={FormatNullableInt(threadPool.MinWorkerThreads)}/{FormatNullableInt(threadPool.MaxWorkerThreads)}, iocp={FormatNullableInt(threadPool.MinIocpThreads)}/{FormatNullableInt(threadPool.MaxIocpThreads)}");
        }

        if (runtimeConfig.TieredCompilation is { } tiered)
        {
            parts.Add($"tiered={tiered.Enabled?.ToString() ?? "unknown"}, quickjit={tiered.QuickJitEnabled?.ToString() ?? "unknown"}, pgo={tiered.DynamicPgoEnabled?.ToString() ?? "unknown"}");
        }

        parts.Add($"env={runtimeConfig.EnvVars.Count}");
        return $"Process {runtimeConfig.ProcessId} runtime-config: {string.Join("; ", parts)}.";
    }

    private static string FormatNullableInt(int? value)
        => value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : "?";

    private static NextActionHint[] BuildRuntimeConfigHints(RuntimeConfigView runtimeConfig)
    {
        if (runtimeConfig.ThreadPool is { HillClimbingEnabled: false })
        {
            return
            [
                new NextActionHint(
                    "collect_events",
                    "ThreadPool hill-climbing is not active; capture runtime counters before investigating starvation symptoms further.",
                    new Dictionary<string, object?> { ["kind"] = "threadpool", ["processId"] = runtimeConfig.ProcessId, ["durationSeconds"] = 6 }),
            ];
        }

        return
        [
            new NextActionHint(
                "collect_events",
                "Use runtime counters as the next cheap signal after confirming the startup configuration.",
                new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = runtimeConfig.ProcessId, ["durationSeconds"] = 5 }),
        ];
    }

    private static string SummariseProcessResources(ProcessResources resources, int durationSeconds)
    {
        var parts = new List<string>();
        if (resources.FdCount is { } fdCount)
        {
            parts.Add($"fd={fdCount}");
        }

        if (resources.HandleCount is { } handleCount)
        {
            parts.Add($"handles={handleCount}");
        }

        if (resources.Sockets is { } sockets)
        {
            parts.Add($"tcp est={sockets.Established}, listen={sockets.Listen}, close_wait={sockets.CloseWait}, time_wait={sockets.TimeWait}");
        }

        if (resources.Limits?.NoFileSoft is { } noFileSoft)
        {
            var limitText = resources.Limits.NoFileUsageFraction is { } usage
                ? $"nofile={resources.FdCount ?? 0}/{noFileSoft} ({usage * 100:F0}%)"
                : $"nofile={noFileSoft}";
            parts.Add(limitText);
        }

        if (parts.Count == 0)
        {
            return resources.Notes.Count > 0
                ? resources.Notes[0]
                : $"Process {resources.ProcessId}: no resource data collected.";
        }

        var prefix = durationSeconds >= 2 && resources.Trend is { Samples.Count: > 0 }
            ? $"Process {resources.ProcessId} resources over {durationSeconds}s ({resources.Trend.Samples.Count} samples): "
            : $"Process {resources.ProcessId} resource snapshot: ";
        return prefix + string.Join("; ", parts) + ".";
    }

    private static string SummariseRequestsNow(RequestsNowSnapshot snapshot, int windowSeconds)
    {
        if (snapshot.Requests.Count == 0)
        {
            return $"Process {snapshot.ProcessId}: no in-flight ASP.NET Core requests observed during the last {windowSeconds}s.";
        }

        var preview = string.Join(", ", snapshot.Requests.Take(3).Select(request =>
            $"{request.Method} {request.Endpoint} ({request.StartedAtMs:F0} ms, tid {request.ThreadId})"));
        return $"Process {snapshot.ProcessId}: {snapshot.Requests.Count} in-flight ASP.NET Core request(s) observed during the last {windowSeconds}s: {preview}{(snapshot.Requests.Count > 3 ? ", …" : string.Empty)}.";
    }

    private static NextActionHint[] BuildRequestsNowHints(RequestsNowSnapshot snapshot)
    {
        if (snapshot.Requests.Count == 0)
        {
            return
            [
                new NextActionHint(
                    "collect_events",
                    "No hanging requests were active in this 2s window — re-run during the incident or cross-check Hosting counters.",
                    new Dictionary<string, object?>
                    {
                        ["kind"] = "counters",
                        ["processId"] = snapshot.ProcessId,
                        ["durationSeconds"] = 5,
                        ["providers"] = HostingCounterProviders,
                    }),
            ];
        }

        var oldest = snapshot.Requests.MaxBy(request => request.StartedAtMs)!;
        return
        [
            new NextActionHint(
                "collect_thread_snapshot",
                $"Oldest in-flight request is {oldest.Method} {oldest.Endpoint} ({oldest.StartedAtMs:F0} ms). Capture a full thread snapshot if you need every thread and lock, not just the matched request thread.",
                new Dictionary<string, object?>
                {
                    ["processId"] = snapshot.ProcessId,
                    ["maxFramesPerThread"] = 64,
                }),
            new NextActionHint(
                "collect_events",
                "Correlate the hang with Hosting counters (current-requests / failed-requests) over a longer window.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = snapshot.ProcessId,
                    ["durationSeconds"] = 5,
                    ["providers"] = HostingCounterProviders,
                }),
        ];
    }

    private static NextActionHint[] BuildProcessResourceHints(ProcessResources resources, int durationSeconds)
    {
        var hints = new List<NextActionHint>();
        var closeWaitGrowing = resources.Trend is { Samples.Count: > 1 } trend &&
                               GetSocketMetric(trend.Samples[^1].Sockets, static sockets => sockets.CloseWait) >
                               GetSocketMetric(trend.Samples[0].Sockets, static sockets => sockets.CloseWait);
        var fdFlat = resources.Trend is { Samples.Count: > 1 } trendSamples &&
                     Math.Abs((trendSamples.Samples[^1].FdCount ?? 0) - (trendSamples.Samples[0].FdCount ?? 0)) <= 5;

        if (resources.Sockets?.CloseWait is > 100 && (durationSeconds == 0 || closeWaitGrowing))
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Likely undisposed HttpResponseMessage / HttpClient — collect System.Net.Http EventSource events to confirm.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = resources.ProcessId,
                    ["providerName"] = "System.Net.Http",
                    ["durationSeconds"] = 10,
                }));
        }

        if (resources.Limits?.NoFileUsageFraction is > 0.85)
        {
            hints.Add(new NextActionHint(
                "collect_process_dump",
                "Approaching the FD ceiling — consider collect_process_dump before the crash to capture state.",
                new Dictionary<string, object?>
                {
                    ["processId"] = resources.ProcessId,
                    ["dumpType"] = "Mini",
                }));
        }

        if (resources.Sockets?.TimeWait is > 100 && fdFlat)
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Connection churn pattern; check connection pooling config and confirm with System.Net.Http EventSource activity.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "event_source",
                    ["processId"] = resources.ProcessId,
                    ["providerName"] = "System.Net.Http",
                    ["durationSeconds"] = 10,
                }));
        }

        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint(
                "collect_events",
                "Resource usage looks unremarkable — move up the stack to runtime counters.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "counters",
                    ["processId"] = resources.ProcessId,
                    ["durationSeconds"] = 5,
                }));
        }

        return hints.ToArray();
    }

    private static int GetSocketMetric(SocketBreakdown? sockets, Func<SocketBreakdown, int> selector)
        => sockets is null ? 0 : selector(sockets);

    [RequireScope("read-counters")]
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
        [Description("Optional list of EventCounter provider names to subscribe to. If null, defaults to System.Runtime, Microsoft.AspNetCore.Hosting and Microsoft-AspNetCore-Server-Kestrel. Pass an empty list to skip legacy EventCounters.")]
        string[]? providers = null,
        [Description("Optional list of Meter names to subscribe to through System.Diagnostics.Metrics. Null/empty disables Meter collection.")]
        string[]? meters = null,
        [Description("Refresh interval (in seconds) requested from each provider. Defaults to 1.")] int intervalSeconds = 1,
        [Description("kind=counters only. Maximum Meter time series (and histograms) retained before the collector caps results. Defaults to 1000.")]
        int maxInstrumentTimeSeries = 1000,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns ~12 headline counters plus http.server.request.duration p95 when available. 'detail' returns the full counter + meter list. 'raw' is equivalent to detail for this tool. The complete snapshot is always retained behind the issued handle — drill in with query_snapshot(handle, view=summary).")]
        SamplingDepth depth = SamplingDepth.Summary,
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
        if (maxInstrumentTimeSeries < 1)
        {
            return InvalidArg<CounterSnapshot>(nameof(maxInstrumentTimeSeries), "must be >= 1");
        }

        var resolved = await ResolveContextAsync<CounterSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            providers,
            meters,
            intervalSeconds,
            maxInstrumentTimeSeries,
            cancellationToken).ConfigureAwait(false);

        var cpu = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
        var heap = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gc-heap-size");
        var timeInGc = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "time-in-gc");
        var queueLength = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "threadpool-queue-length");
        var allocRate = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "alloc-rate");
        var gen2Count = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "gen-2-gc-count");
        var contention = snapshot.Counters.FirstOrDefault(c => c.Provider == "System.Runtime" && c.Name == "monitor-lock-contention-count");

        // Build hints based on counter thresholds (highest priority first).
        var hints = new List<NextActionHint>();

        // CPU > 70% → recommend CPU sampling (existing behavior, highest priority).
        if ((cpu?.Value ?? 0) >= 70)
        {
            hints.Add(new NextActionHint("collect_sample", $"cpu-usage={cpu!.Value:F1}% — investigate the hot path.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10, ["topN"] = 25 }));
        }

        // ThreadPool queue > 50 → potential starvation (Phase 12 Wave 1.1).
        if ((queueLength?.Value ?? 0) > 50)
        {
            hints.Add(new NextActionHint("collect_events", $"threadpool-queue-length={queueLength!.Value:F0} — possible ThreadPool starvation.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "threadpool", ["durationSeconds"] = 10 }));
        }

        // GC time > 15% → GC pressure (Phase 12 Wave 1.2).
        if ((timeInGc?.Value ?? 0) > 15)
        {
            hints.Add(new NextActionHint("collect_events", $"time-in-gc={timeInGc!.Value:F1}% — GC pressure detected.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
            hints.Add(new NextActionHint("inspect_heap", "GC pressure — inspect heap for allocation patterns.",
                new Dictionary<string, object?> { ["processId"] = pid, ["source"] = "live" }));
        }

        // High allocation rate with active Gen2 GCs → allocation pressure (Phase 12 Wave 1.3).
        // gen-2-gc-count is an EventCounter increment that shows the count in the last interval only,
        // not the cumulative total. Any Gen2 GC activity (> 0 in last interval) combined with high
        // alloc-rate (> 50 MB/s) signals an allocation hotspot worth investigating.
        if ((allocRate?.Value ?? 0) > 50_000_000 && (gen2Count?.Value ?? 0) > 0)
        {
            hints.Add(new NextActionHint("collect_sample", $"alloc-rate={allocRate!.Value / 1_000_000:F1} MB/s + gen-2 GCs active — allocation hotspot likely.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "allocation", ["durationSeconds"] = 10 }));
        }

        // High lock contention → investigate contention sources (Phase 12 Wave 1.4).
        // monitor-lock-contention-count > 10/interval suggests lock storms worth investigating.
        if ((contention?.Value ?? 0) > 10)
        {
            hints.Add(new NextActionHint("collect_events", $"monitor-lock-contention-count={contention!.Value:F0} — lock contention detected.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "contention", ["durationSeconds"] = 10 }));
        }

        // I/O bounded pattern: low CPU (< 30%) but queue building up → likely waiting on external I/O.
        // Suggest thread snapshot to see blocking stacks + activities to see what's in-flight.
        if ((cpu?.Value ?? 0) < 30 && (queueLength?.Value ?? 0) > 10)
        {
            hints.Add(new NextActionHint("collect_thread_snapshot", $"cpu-usage={cpu?.Value:F1}% but threadpool-queue-length={queueLength?.Value:F0} — I/O bound likely, inspect blocking stacks.",
                new Dictionary<string, object?> { ["processId"] = pid }));
            hints.Add(new NextActionHint("collect_events", "Low CPU + queue buildup — trace activities to see what's waiting.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "activities", ["durationSeconds"] = 10 }));
        }

        // Fallback: counters look quiet, suggest GC events to confirm.
        if (hints.Count == 0)
        {
            hints.Add(new NextActionHint("collect_events", "Counters look quiet — confirm there are no GC pauses before widening scope.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
        }

        // The handle always carries the FULL snapshot (query_collection drilldown stays cheap),
        // but the inline payload is depth-gated to keep first-look responses small.
        var handle = handles.Register(pid, CollectionHandleKinds.Counters, snapshot, CollectionHandleTtl);

        var inlinePayload = snapshot;
        var droppedCounters = 0;
        var droppedMeters = 0;
        if (depth == SamplingDepth.Summary)
        {
            var filteredCounters = HeadlineCounters.FilterCounters(snapshot.Counters);
            var filteredMeters = HeadlineCounters.FilterMeters(snapshot.Meters);
            droppedCounters = snapshot.Counters.Count - filteredCounters.Count;
            droppedMeters = snapshot.Meters.Count - filteredMeters.Count;
            inlinePayload = snapshot with { Counters = filteredCounters, Meters = filteredMeters };
        }

        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationText = requestDuration?.Histogram is { } histogram
            ? $", http.server.request.duration p95={histogram.P95.ToString("F3", CultureInfo.InvariantCulture)}{requestDuration.Unit}"
            : string.Empty;
        var noteText = snapshot.Notes.Count > 0
            ? $" Notes: {string.Join(" | ", snapshot.Notes)}"
            : string.Empty;

        var summaryText = depth == SamplingDepth.Summary
            ? $"Captured {snapshot.Counters.Count} counter(s) and {snapshot.Meters.Count} meter series over {durationSeconds}s — showing {inlinePayload.Counters.Count} headline counter(s) and {inlinePayload.Meters.Count} headline meter(s) (dropped {droppedCounters} counter(s), {droppedMeters} meter(s); handle has all). cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}{requestDurationText}.{noteText}"
            : $"Captured {snapshot.Counters.Count} counter(s) and {snapshot.Meters.Count} meter series over {durationSeconds}s — cpu-usage={cpu?.Value:F1}%, gc-heap-size={heap?.Value:F1}{requestDurationText}.{noteText}";

        // Always add the drill-down hint at the end.
        hints.Add(new NextActionHint("query_snapshot",
            "Drill into this counter snapshot without re-collecting (views: summary, byProvider).",
            new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byProvider" }));

        var ok = DiagnosticResult.OkWithHandle(
            inlinePayload,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            [.. hints]);
        return WithContext(ok, resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures a CPU sample from the target process and returns the top-N hotspots aggregated by method. " +
        "On CoreCLR uses EventPipe SampleProfiler (managed frames with mvid+token handoff). " +
        "Optionally, resolveMethodInstantiations=true performs a second ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames; on Linux that requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target while the attach runs. " +
        "On NativeAOT (Linux) falls back to 'perf record' when available — frames are native symbols only, MethodIdentity is null. " +
        "Each hotspot reports both inclusive and exclusive sample counts. Run after snapshot_counters shows elevated cpu-usage. " +
        "Spec-compliant clients can call this tool as an MCP Task (tools/call with params.task) and poll via tasks/get + tasks/result, " +
        "or rely on MCP-native notifications/progress + notifications/cancelled on the same tools/call request.")]
    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload (issue #28 — makes dotnet-assembly-mcp.get_method_source optional when PDBs are reachable). Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")] bool resolveSourceLines = true,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols' or 'srv*c:\\symcache*https://msdl.microsoft.com/download/symbols'). Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule/module directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on the `Diagnostics:SymbolServerAllowlist` allowlist or the call is rejected with a `SymbolServerNotAllowed` envelope. Local file paths always pass through. Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")] int? maxResolvedSources = null,
        [Description("If true, performs an opt-in ClrMD attach after sampling to recover closed generic instantiations for the hottest managed frames (displayed on MethodIdentity as ClosedSignature + GenericTypeArguments.Method). CoreCLR only. On Linux this requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target during the attach. Defaults to false to keep the EventPipe-only path lightweight.")] bool resolveMethodInstantiations = false,
        [Description("Cap on how many top hotspots get ClrMD generic-instantiation enrichment. Must be >= 1. Defaults to the requested topN so the enrichment work stays bounded to the hottest frames.")] int? maxResolvedMethodInstantiations = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 hotspots inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full sample is always retained behind the issued handle — drill in with get_call_tree.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        var effectiveMaxResolved = maxResolvedSources ?? topN;
        if (effectiveMaxResolved < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");
        var effectiveMaxResolvedInstantiations = maxResolvedMethodInstantiations ?? topN;
        if (effectiveMaxResolvedInstantiations < 1) return InvalidArg<CpuSample>(nameof(maxResolvedMethodInstantiations), "must be >= 1");

        // B4 / issue #165 / M3: caller-supplied srv*http(s):// symbol paths are an SSRF
        // surface. Default deny; operators allowlist hosts via Diagnostics:SymbolServerAllowlist.
        if (resolveSourceLines)
        {
            var symbolDenial = ValidateSymbolPath<CpuSample>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
            if (symbolDenial is not null) return symbolDenial;
        }

        var resolved = await ResolveContextAsync<CpuSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: effectiveMaxResolved)
            : null;
        var instantiationOpts = resolveMethodInstantiations
            ? new MethodInstantiationResolutionOptions(Enabled: true, MaxResolved: effectiveMaxResolvedInstantiations)
            : null;

        CpuSampleResult result;
        try
        {
            result = await CollectionProgressTicker.RunAsync(
                requestContext,
                "collect_cpu_sample",
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromSeconds(1),
                ct => sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, instantiationOpts, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // MCP-native cancel path (notifications/cancelled). Return a partial envelope so the
            // client knows the operation terminated cleanly.
            return WithContext(
                new DiagnosticResult<CpuSample>(
                    $"CPU sampling cancelled by the client after starting against pid {pid}. " +
                    "No samples were retained — restart the collection to capture data.",
                    Array.Empty<NextActionHint>())
                {
                    Cancelled = true,
                },
                ctx);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (Exception ex) when (resolveMethodInstantiations && ex is not OperationCanceledException)
        {
            return WithContext(ClassifyAttachFailure<CpuSample>("collect_cpu_sample", pid, ex), ctx);
        }

        var handle = handles.Register(pid, "cpu-sample", result.Artifact, CpuSampleHandleTtl);

        var ok = BuildCpuSampleResult(
            result.Summary,
            durationSeconds,
            handle.Id,
            handle.ExpiresAt,
            depth,
            new NextActionHint("query_snapshot", "Walk the merged caller→callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_events", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, ctx);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures allocation samples from the target process and returns the top-N types by total allocated bytes " +
        "and by event count. Uses GCAllocationTick events from Microsoft-Windows-DotNETRuntime (GCKeyword=0x1, Verbose), " +
        "which fire roughly every 100 KB of total managed allocations. " +
        "On CoreCLR, TypeName is fully populated with managed type names. " +
        "On NativeAOT, GCAllocationTick events fire but TypeName is empty — all events roll up under '<unknown>' " +
        "and only the total event count and bytes are meaningful; use collect_cpu_sample for per-site attribution on AOT. " +
        "Returns two ranked lists (TopByBytes, TopByCount) and a handle for call-site drill-down via get_call_tree. " +
        "When managed symbols are available, get_call_tree projects MethodIdentity (MVID + token) onto the returned frames for dotnet-assembly-mcp handoff. " +
        "Run after snapshot_counters shows elevated GC pressure or growing gen0/gen1 heap sizes.")]
    public static async Task<DiagnosticResult<AllocationSample>> CollectAllocationSample(
        EventPipeAllocationSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of types to return in each top-N list (TopByBytes and TopByCount). Must be >= 1. Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<AllocationSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<AllocationSample>(nameof(topN), "must be >= 1");

        var resolved = await ResolveContextAsync<AllocationSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        AllocationSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var handle = handles.Register(pid, "allocation-sample", new AllocationSampleArtifact(sample, result.Artifact), CpuSampleHandleTtl);

        var topType = sample.TopByBytes.Count > 0 ? sample.TopByBytes[0] : null;
        var unknownOnly = topType?.TypeName == "<unknown>" && sample.TopByBytes.Count == 1;
        var summaryText = unknownOnly
            ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s, " +
              $"but TypeName was empty for all events (expected on NativeAOT). " +
              $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\") to see native allocation frames."
            : topType is not null
                ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s. " +
                  $"Top type by bytes: {topType.TypeName} ({topType.TotalBytes:N0} bytes, {topType.EventCount} events). " +
                  $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\")."
                : $"Captured {sample.TotalEvents} allocation events but no type aggregation surfaced — " +
                  $"increase durationSeconds or drive a workload that allocates during the window.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Walk the merged allocation call-site tree to find which code paths are allocating the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_sample", "Cross-reference: identify hot CPU paths that correlate with the top allocating types.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("collect_events", "Observe GC pause frequency and generation distribution caused by this allocation load.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }));
        return WithContext(ok, ctx);
    }

    [RequireScope("investigation-export")]
    [Description(
        "Returns a pruned caller→callee tree from a prior collect_cpu_sample or collect_allocation_sample run, " +
        "addressed by its handle. Frames are enriched with MethodIdentity (MVID + metadata token) when the producer captured one. " +
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

        var artifact = ResolveTraceArtifact(handles, handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<CallTreeView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        var root = CallTreeIdentityProjector.Stamp(artifact.Root, artifact.MethodIdentities);
        if (!string.IsNullOrWhiteSpace(rootMethodFilter))
        {
            var match = FindHighestRankedDescendant(root, rootMethodFilter);
            if (match is null)
            {
                return DiagnosticResult.Fail<CallTreeView>(
                    $"No frame matching '{rootMethodFilter}' in handle '{handle}'.",
                    new DiagnosticError("NotFound", "No frame in the merged call tree contains the supplied substring.", rootMethodFilter),
                    new NextActionHint("query_snapshot", "Re-issue without rootMethodFilter to inspect the full tree first.",
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
            new NextActionHint("query_snapshot", "Drill deeper by anchoring at a specific method.",
                new Dictionary<string, object?> { ["handle"] = handle, ["rootMethodFilter"] = "<method substring>", ["maxDepth"] = 6 }));
    }

    internal static CpuSampleTraceArtifact? ResolveTraceArtifact(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        return handles.TryGet<CpuSampleTraceArtifact>(handle)
            ?? handles.TryGet<AllocationSampleArtifact>(handle)?.TraceArtifact;
    }

    [RequireScope("eventpipe")]
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
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of blocking stacks returned inline (the full set lives behind the handle). Defaults to 25.")] int topN = 25,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 blocking stacks inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full artifact is always retained behind the issued handle — drill in with query_off_cpu_snapshot.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<OffCpuSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<OffCpuSnapshot>(nameof(topN), "must be >= 1");

        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<OffCpuSnapshot>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        var resolved = await ResolveContextAsync<OffCpuSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        OffCpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, symbolPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Confirm which signals are available on this host before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                $"collect_off_cpu_sample could not start NT Kernel Logger capture for pid {pid}: Windows denied access to the ContextSwitch provider.",
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "After granting either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance') to the sidecar account and restarting the Windows service, re-check capabilities before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
                new NextActionHint("collect_sample",
                    "Retry after the sidecar account has one of the two supported Windows paths: BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance').",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds, ["topN"] = topN }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Check capability matrix; install linux-perf and add CAP_PERFMON to the sidecar securityContext.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var summary = result.Summary;
        var handle = handles.Register(pid, OffCpuHandleKind, result.Artifact, CpuSampleHandleTtl);

        var inlineSummary = summary;
        var droppedStacks = 0;
        if (depth == SamplingDepth.Summary && summary.TopBlockingStacks.Count > 3)
        {
            droppedStacks = summary.TopBlockingStacks.Count - 3;
            inlineSummary = summary with { TopBlockingStacks = summary.TopBlockingStacks.Take(3).ToArray() };
        }

        var topStack = summary.TopBlockingStacks.Count > 0 ? summary.TopBlockingStacks[0] : null;
        var summaryText = topStack is not null
            ? (depth == SamplingDepth.Summary && droppedStacks > 0
                ? $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s — showing top {inlineSummary.TopBlockingStacks.Count} of {summary.TopBlockingStacks.Count} blocking stack(s) (dropped {droppedStacks}; handle has all). " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\")."
                : $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s. " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\").")
            : $"Captured {summary.SchedSwitches} switches but no off-CPU spans closed within the window. " +
              "Either no thread blocked, or wakeups landed outside the capture — try a longer durationSeconds.";

        var ok = DiagnosticResult.OkWithHandle(
            inlineSummary,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Drill into per-thread off-CPU view or a specific stack.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byThread" }),
            new NextActionHint("collect_sample", "Cross-reference with on-CPU hotspots to separate compute from wait.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, resolved.Context);
    }

    /// <summary>Canonical kind tag for handles backing an <see cref="OffCpuSnapshotArtifact"/>.</summary>
    public const string OffCpuHandleKind = "off-cpu-snapshot";

    [RequireScope("eventpipe")]
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
                new NextActionHint("collect_sample", "Re-run the off-CPU sampler to issue a fresh handle.",
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
                new NextActionHint("query_snapshot", "List the top stacks first.",
                    new Dictionary<string, object?> { ["view"] = "topStacks" }));
        }
        var s = artifact.Stacks[idx];
        return DiagnosticResult.Ok(
            new OffCpuQueryView("stack", artifact.ProcessId, artifact.TotalOffCpuMicros,
                Stacks: null, Threads: null, Stack: s),
            $"Rank {stackRank}/{artifact.Stacks.Count}: {s.LeafFrame} — {s.OffCpuMicros / 1000.0:F1} ms across {s.OccurrenceCount} switches (state={s.DominantState}).");
    }

    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    private static DiagnosticResult<CpuSample> BuildCpuSampleResult(
        CpuSample sample,
        int durationSeconds,
        string handleId,
        DateTimeOffset handleExpiresAt,
        SamplingDepth depth,
        params NextActionHint[] hints)
    {
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var inlineSample = sample;
        var droppedHotspots = 0;
        if (depth == SamplingDepth.Summary && sample.TopHotspots.Count > 3)
        {
            droppedHotspots = sample.TopHotspots.Count - 3;
            inlineSample = sample with { TopHotspots = sample.TopHotspots.Take(3).ToArray() };
        }

        var summary = top is not null
            ? (depth == SamplingDepth.Summary && droppedHotspots > 0
                ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s — showing top {inlineSample.TopHotspots.Count} of {sample.TopHotspots.Count} hotspot(s) (dropped {droppedHotspots}; handle has all). Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with query_snapshot(handle=\"{handleId}\", view=\"call-tree\")."
                : $"Captured {sample.TotalSamples} samples over {durationSeconds}s. Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with query_snapshot(handle=\"{handleId}\", view=\"call-tree\").")
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";

        return DiagnosticResult.OkWithHandle(inlineSample, summary, handleId, handleExpiresAt, hints);
    }

    /// <summary>
    /// Default TTL applied to handles emitted by every windowed collector (counters, exceptions,
    /// GC events, EventSource captures). Aligned with the other drill-down kinds so the LLM has
    /// a single mental model: collect once, drill many times within ~10 minutes.
    /// </summary>
    private static readonly TimeSpan CollectionHandleTtl = TimeSpan.FromMinutes(10);

    [RequireAnyScope("read-counters", "eventpipe")]
    [Description(
        "Re-projects a previously-collected counter/exception/GC/EventSource/Activity/log/JIT/ThreadPool/db artifact under a " +
        "named view, without re-running the underlying EventPipe session. Use the `handle` " +
        "returned by snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source / collect_activities / collect_events(kind=\"logs\") / collect_events(kind=\"jit\") / collect_events(kind=\"threadpool\") / collect_events(kind=\"db\"). " +
        "Supported views per kind: counters → summary|byProvider; exception-snapshot → " +
        "summary|byType|recent; gc-events → summary|events|pauseHistogram; event-source → " +
        "summary|byEventName|events; activities → summary|bySource|byOperation|activities; " +
        "log-snapshot → summary|byCategory|byLevel|recent|errors; jit-snapshot → summary|topMethods|tierDistribution|reJIT; threadpool-snapshot → summary|timeline|hillClimbing|workItemOrigins; db-snapshot → summary|byCommand|n+1|connectionPool. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CollectionQueryResult> QueryCollection(
        IDiagnosticHandleStore handles,
        IPrincipalAccessor principalAccessor,
        [Description("Handle returned by a prior collection tool (snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source / collect_activities / collect_events(kind=\"logs\") / collect_events(kind=\"jit\") / collect_events(kind=\"threadpool\") / collect_events(kind=\"db\")). ")] string handle,
        [Description("View name (kind-dependent). Defaults to 'summary'.")] string? view = null,
        [Description("Cap on inline items for paginated views (recent / events / byType / byEventName / bySource / byOperation / activities / byCategory / byLevel / errors / topMethods / reJIT / hillClimbing / workItemOrigins / byCommand / n+1 / connectionPool). Must be >= 1. Defaults to 50.")] int topN = 50)
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
                new NextActionHint("collect_events", "Re-run the original collector on the same pid to issue a fresh handle.", null));
        }

        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = entry.Value.Kind == CollectionHandleKinds.Counters ? "read-counters" : "eventpipe";
            if (!principal.HasScope(requiredScope))
            {
                var message = $"forbidden: tool 'query_collection' requires scope '{requiredScope}' for kind '{entry.Value.Kind}'.";
                return DiagnosticResult.Fail<CollectionQueryResult>(
                    message,
                    new DiagnosticError("Forbidden", message, requiredScope));
            }
        }

        var outcome = CollectionQueryDispatcher.Dispatch(entry.Value.Kind, view, entry.Value.Artifact, topN);

        if (outcome.UnknownKind is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is of kind '{outcome.UnknownKind}' which query_collection does not support.",
                new DiagnosticError(
                    "UnsupportedHandleKind",
                    $"query_collection dispatches over kinds: {string.Join(", ", new[] { CollectionHandleKinds.Counters, CollectionHandleKinds.ExceptionSnapshot, CollectionHandleKinds.GcEvents, CollectionHandleKinds.EventSource, CollectionHandleKinds.Activities, CollectionHandleKinds.LogSnapshot, CollectionHandleKinds.JitSnapshot, CollectionHandleKinds.ThreadPoolSnapshot, CollectionHandleKinds.DbSnapshot })}.",
                    outcome.UnknownKind),
                new NextActionHint("query_snapshot", "Use the kind-specific drill-down tool for heap/thread/cpu handles.", null));
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
                new NextActionHint("query_snapshot", "Retry with one of the allowed views.",
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
            new NextActionHint("query_snapshot",
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

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime Exception keyword on Microsoft-Windows-DotNETRuntime and " +
        "captures every managed exception thrown by the target process during the window. " +
        "Returns total count (always exact), breakdown by exception type (always exact), and " +
        "the first maxRecent individual exception details — when TotalExceptions exceeds " +
        "maxRecent the Recent list is truncated to the head of the stream (the cap that was " +
        "applied is echoed back as ExceptionSnapshot.RecentCap). " +
        "Spec-compliant clients can call this tool as an MCP Task and poll via tasks/get + tasks/result. " +
        "IMPORTANT: start this BEFORE the workload you want to observe — exceptions before the session opens are missed.")]
    public static async Task<DiagnosticResult<ExceptionSnapshot>> CollectExceptions(
        IExceptionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Recent[] list inline (keeps Total + ByType, which is what most diagnoses need). 'detail' includes Recent up to maxRecent. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle — drill in with query_collection(handle, view=recent).")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ExceptionSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxRecent < 1) return InvalidArg<ExceptionSnapshot>(nameof(maxRecent), "must be >= 1");

        var resolved = await ResolveContextAsync<ExceptionSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snap = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxRecent, cancellationToken).ConfigureAwait(false);
        var topType = snap.ByType.OrderByDescending(c => c.Count).FirstOrDefault();

        var inlineSnap = snap;
        var droppedRecent = 0;
        if (depth == SamplingDepth.Summary && snap.Recent.Count > 0)
        {
            droppedRecent = snap.Recent.Count;
            inlineSnap = snap with { Recent = Array.Empty<ManagedExceptionEvent>() };
        }

        var summary = snap.TotalExceptions == 0
            ? $"No managed exceptions thrown in {durationSeconds}s. If you expected some, ensure the collection started before the workload."
            : (depth == SamplingDepth.Summary && droppedRecent > 0
                ? $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}). Dropped {droppedRecent} Recent entry(ies) from inline (handle has all)."
                : $"{snap.TotalExceptions} exception(s) over {durationSeconds}s; most common: {topType?.ExceptionType} ({topType?.Count}).");

        var primaryHint = snap.TotalExceptions > 0
            ? new NextActionHint("collect_events", "Subscribe to a domain-specific EventSource to correlate with the exception spikes.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 })
            : new NextActionHint("collect_events", "No exception pressure — sweep GC events next.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.ExceptionSnapshot, snap, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnap,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into this exception snapshot without re-collecting (views: summary, byType, recent).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byType", ["topN"] = 20 })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime GC keyword and pairs GCStart/GCStop events to compute pause " +
        "durations per collection. Returns total collections, total/max pause time, counts per " +
        "generation, and a bounded list of individual GC events. Spec-compliant clients can call " +
        "this tool as an MCP Task and poll via tasks/get + tasks/result.")]
    public static async Task<DiagnosticResult<GcSummary>> CollectGcEvents(
        IGcCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of GC events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Events[] list inline (keeps totals, max pause, per-gen counts). 'detail' includes Events up to maxEvents. 'raw' is equivalent to detail. The full GC summary is always retained behind the issued handle — drill in with query_collection(handle, view=events|pauseHistogram).")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<GcSummary>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<GcSummary>(nameof(maxEvents), "must be >= 1");

        var resolved = await ResolveContextAsync<GcSummary>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var gc = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), maxEvents, cancellationToken).ConfigureAwait(false);

        var inlineGc = gc;
        var droppedEvents = 0;
        if (depth == SamplingDepth.Summary && gc.Events.Count > 0)
        {
            droppedEvents = gc.Events.Count;
            inlineGc = gc with { Events = Array.Empty<GcEvent>() };
        }

        var summary = gc.TotalCollections == 0
            ? $"No GC activity in {durationSeconds}s — heap is quiet or the workload is idle."
            : (depth == SamplingDepth.Summary && droppedEvents > 0
                ? $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms. Dropped {droppedEvents} Event(s) from inline (handle has all)."
                : $"{gc.TotalCollections} collection(s), max pause {gc.MaxPauseTime.TotalMilliseconds:F1}ms, total pause {gc.TotalPauseTime.TotalMilliseconds:F1}ms.");

        var primaryHint = gc.MaxPauseTime.TotalMilliseconds > 100
            ? new NextActionHint("collect_process_dump",
                $"Max GC pause {gc.MaxPauseTime.TotalMilliseconds:F0}ms is high — capture a WithHeap dump for offline heap analysis.",
                new Dictionary<string, object?> { ["processId"] = pid, ["dumpType"] = "WithHeap" })
            : new NextActionHint("collect_events", "GC looks healthy — pivot to a domain EventSource (e.g. System.Net.Http) for application-level signal.",
                new Dictionary<string, object?> { ["processId"] = pid, ["providerName"] = "System.Net.Http", ["durationSeconds"] = 10 });

        var handle = handles.Register(pid, CollectionHandleKinds.GcEvents, gc, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineGc,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into these GC events without re-collecting (views: summary, events, pauseHistogram).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "pauseHistogram" })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes the Microsoft-Extensions-Logging EventSource and returns a curated ILogger view: " +
        "level counts, top categories, a bounded recent ring buffer, and optional exception/scope JSON when depth != summary.")]
    public static async Task<DiagnosticResult<LogSnapshot>> CollectLogs(
        ILogCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Optional case-insensitive glob filters for ILogger categories. Null/empty captures all categories.")] IReadOnlyList<string>? categories = null,
        [Description("Minimum log level to retain (Trace|Debug|Information|Warning|Error|Critical). Defaults to Information.")] string minLevel = "Information",
        [Description("Maximum number of recent log entries to retain in the in-memory ring buffer. Must be >= 1. Defaults to 500.")] int maxEvents = 500,
        [Description("Maximum UTF-8 bytes retained per message/scope/exception string before truncation. Must be >= 16. Defaults to 4096.")] int maxMessageBytes = 4096,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Recent[] list inline and subscribes only FormattedMessage; 'detail' and 'raw' also enable MessageJson so exception detail and scopes are retained.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<LogSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<LogSnapshot>(nameof(maxEvents), "must be >= 1");
        if (maxMessageBytes < 16) return InvalidArg<LogSnapshot>(nameof(maxMessageBytes), "must be >= 16");
        if (!Enum.TryParse<LogLevel>(minLevel, ignoreCase: true, out var parsedMinLevel)
            || parsedMinLevel is LogLevel.None or < LogLevel.Trace or > LogLevel.Critical)
        {
            return InvalidArg<LogSnapshot>(nameof(minLevel), "must be one of Trace|Debug|Information|Warning|Error|Critical");
        }

        var resolved = await ResolveContextAsync<LogSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector
            .CollectAsync(
                pid,
                TimeSpan.FromSeconds(durationSeconds),
                categories,
                parsedMinLevel,
                maxEvents,
                maxMessageBytes,
                includeJsonPayload: depth != SamplingDepth.Summary,
                cancellationToken)
            .ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        var droppedRecent = 0;
        if (depth == SamplingDepth.Summary && snapshot.Recent.Count > 0)
        {
            droppedRecent = snapshot.Recent.Count;
            inlineSnapshot = snapshot with { Recent = Array.Empty<LogEntry>() };
        }

        var topCategory = snapshot.ByCategory.Count > 0 ? snapshot.ByCategory[0] : null;
        var warningPlus = snapshot.EventsByLevelWarning + snapshot.EventsByLevelError + snapshot.EventsByLevelCritical;
        var summary = snapshot.TotalEvents == 0
            ? $"No ILogger events captured in {durationSeconds}s. Widen categories or lower minLevel if you expected logs."
            : (depth == SamplingDepth.Summary && droppedRecent > 0
                ? $"Captured {snapshot.TotalEvents} ILogger event(s) over {durationSeconds}s at minLevel={snapshot.MinimumLevel}; warning+={warningPlus}. Top category: {topCategory?.Category} ({topCategory?.Count}). Dropped {droppedRecent} Recent entry(ies) from inline (handle has all)."
                : $"Captured {snapshot.TotalEvents} ILogger event(s) over {durationSeconds}s at minLevel={snapshot.MinimumLevel}; warning+={warningPlus}. Top category: {topCategory?.Category} ({topCategory?.Count}).");

        var handle = handles.Register(pid, CollectionHandleKinds.LogSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this log snapshot without re-collecting (views: summary, byCategory, byLevel, recent, errors).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "errors", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                warningPlus > 0
                    ? "Correlate warning/error spikes with CPU hotspots from the same window."
                    : "If the app was slow without warning/error logs, pivot to CPU sampling for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures CLR JIT / tiered-compilation activity via the Microsoft-Windows-DotNETRuntime provider. " +
        "Reconstructs inclusive JIT time from MethodJittingStarted→MethodLoadVerbose, tracks Tier0/Tier1/ReadyToRun distribution, R2R hit vs miss-then-jit, ReJIT and OSR counts, and returns a drill-down handle.")]
    public static async Task<DiagnosticResult<JitSnapshot>> CollectJit(
        IJitCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' trims the inline method list to the hottest 10 entries; the handle retains every observed method.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<JitSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<JitSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector
            .CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken)
            .ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        var droppedMethods = 0;
        if (depth == SamplingDepth.Summary && snapshot.Methods.Count > 10)
        {
            droppedMethods = snapshot.Methods.Count - 10;
            var inlineNotes = snapshot.Notes
                .Append($"{droppedMethods} additional method(s) omitted from the inline payload (handle has all).")
                .ToList();
            inlineSnapshot = snapshot with
            {
                Methods = snapshot.Methods.Take(10).ToList(),
                Notes = inlineNotes,
            };
        }

        var summary = snapshot.CompletedCompilations == 0 && snapshot.R2RLookupCount == 0
            ? $"No JIT or ReadyToRun activity captured in {durationSeconds}s. Trigger a cold path during the window and retry."
            : (depth == SamplingDepth.Summary && droppedMethods > 0
                ? $"Observed {snapshot.CompletedCompilations} completed JIT compilation(s) across {snapshot.UniqueMethods} method(s) in {durationSeconds}s. {snapshot.HealthCheck} ReJIT={snapshot.ReJitCount}, OSR={snapshot.OsrCount}. Dropped {droppedMethods} method row(s) from inline (handle has all)."
                : $"Observed {snapshot.CompletedCompilations} completed JIT compilation(s) across {snapshot.UniqueMethods} method(s) in {durationSeconds}s. {snapshot.HealthCheck} ReJIT={snapshot.ReJitCount}, OSR={snapshot.OsrCount}.");

        var handle = handles.Register(pid, CollectionHandleKinds.JitSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this JIT snapshot without re-collecting (views: summary, topMethods, tierDistribution, reJIT).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "topMethods", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                "If cold-start JIT pressure lines up with latency, correlate it with CPU hotspots from the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "cpu", ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime ThreadingKeyword and returns a curated ThreadPool starvation view: worker + IOCP timelines, hill-climbing transitions, work-item origins, and best-effort effective min/max settings.")]
    public static async Task<DiagnosticResult<ThreadPoolEventSnapshot>> CollectThreadPool(
        IThreadPoolCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps the headline counts + top origins inline and relies on query_snapshot(handle, view=timeline|hillClimbing|workItemOrigins) for the full drilldown.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ThreadPoolEventSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<ThreadPoolEventSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                WorkerThreadTimeline = Array.Empty<ThreadPoolCountBucket>(),
                IocpThreadTimeline = Array.Empty<ThreadPoolCountBucket>(),
                HillClimbing = Array.Empty<ThreadPoolHillClimbingSample>(),
                WorkItemOrigins = snapshot.WorkItemOrigins.Take(5).ToList(),
            };
        }

        var peakWorkers = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline.Max(static bucket => bucket.Count) : 0;
        var latestWorkers = snapshot.WorkerThreadTimeline.Count > 0 ? snapshot.WorkerThreadTimeline[^1].Count : 0;
        var starvationEvents = snapshot.HillClimbing.Count(static sample => string.Equals(sample.Reason, "Starvation", StringComparison.OrdinalIgnoreCase));
        var summary = snapshot.HillClimbing.Count == 0 && snapshot.TotalEnqueueEvents == 0
            ? $"No ThreadPool starvation signals were captured in {durationSeconds}s. Start the workload after collection begins if you expected queue growth."
            : $"Captured ThreadPool activity over {durationSeconds}s: workers latest/peak={latestWorkers}/{peakWorkers}, hill-climbing events={snapshot.HillClimbing.Count}, starvation reasons={starvationEvents}, enqueue/dequeue={snapshot.TotalEnqueueEvents}/{snapshot.TotalDequeueEvents}.";

        var handle = handles.Register(pid, CollectionHandleKinds.ThreadPoolSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into the worker + IOCP timelines for this ThreadPool snapshot.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "timeline" }),
            new NextActionHint("query_snapshot",
                "Inspect hill-climbing transitions and starvation reasons without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "hillClimbing" }),
            new NextActionHint("query_snapshot",
                "Inspect top work-item origins without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "workItemOrigins", ["topN"] = 20 })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Collects a curated CLR lock-contention view from the runtime Contention keyword. " +
        "Aggregates monitor waits by call site and owner thread, computes total/p50/p95/max wait duration, and retains the full event slice behind the issued handle for query_snapshot(handle, view=summary|byCallSite|byOwner).")]
    public static async Task<DiagnosticResult<ContentionSnapshot>> CollectContention(
        IContentionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps the headline aggregates inline and relies on query_snapshot(handle, view=byCallSite|byOwner) for grouped drilldown.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ContentionSnapshot>(nameof(durationSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<ContentionSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                Events = Array.Empty<ContentionEventSample>(),
            };
        }

        var handle = handles.Register(pid, CollectionHandleKinds.ContentionSnapshot, snapshot, CollectionHandleTtl);
        var hints = new List<NextActionHint>();

        string summary;
        if (snapshot.TotalEvents == 0)
        {
            if (OperatingSystem.IsLinux())
            {
                // Linux doesn't emit ContentionStart/Stop via EventPipe (nomac exclusion in runtime).
                // Guide the LLM toward alternative diagnostic paths that DO work.
                summary = $"No lock contention events were captured in {durationSeconds}s. " +
                          "Linux runtimes do not emit ContentionStart/Stop over EventPipe (known limitation). " +
                          "Use alternative signals: 'monitor-lock-contention-count' counter + thread snapshots to identify blocked threads.";

                hints.Add(new NextActionHint(
                    "collect_events",
                    "Collect counters to see if monitor-lock-contention-count is rising (confirms contention exists even without events).",
                    new Dictionary<string, object?> { ["kind"] = "counters", ["processId"] = pid, ["durationSeconds"] = 5, ["providers"] = (IReadOnlyList<string>)["System.Runtime"] }));

                hints.Add(new NextActionHint(
                    "collect_thread_snapshot",
                    "Take a thread snapshot to identify threads blocked on Monitor.Enter and inspect the lock-graph for contended SyncBlocks.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
            }
            else
            {
                summary = $"No lock contention events were captured in {durationSeconds}s. Start the collection before the workload and retry if you expected waits.";
            }
        }
        else
        {
            summary = $"Captured {snapshot.TotalEvents} lock-contention event(s) over {durationSeconds}s across {snapshot.DistinctMonitors} contended monitor(s). " +
                      $"Total wait={snapshot.TotalContentionDuration.TotalMilliseconds:F1}ms, p95={snapshot.P95ContentionDuration.TotalMilliseconds:F1}ms, max={snapshot.MaxContentionDuration.TotalMilliseconds:F1}ms.";

            hints.Add(new NextActionHint("query_snapshot",
                "Group this contention window by call site without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byCallSite", ["topN"] = 20 }));
            hints.Add(new NextActionHint("query_snapshot",
                "Group this contention window by owner thread without re-collecting.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOwner", ["topN"] = 20 }));
        }

        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            hints.ToArray()),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Collects a curated DB view by subscribing to EF Core command diagnostics plus SqlClient command/pool signals. " +
        "Aggregates by sanitized command hash + sanitized connection string, computes count/total/max/p95 latency, " +
        "detects N+1 patterns when the same command repeats >10 times in one parent activity, and snapshots SqlClient pool counters. " +
        "The full artifact is always retained behind the issued handle — drill in with query_snapshot(handle, view=summary|byCommand|n+1|connectionPool).")]
    public static async Task<DiagnosticResult<DbSnapshot>> CollectDb(
        IDbCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Refresh interval (in seconds) requested from SqlClient EventCounters. Defaults to 1.")] int intervalSeconds = 1,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps only the top command slice inline; 'detail' and 'raw' return the full by-command and N+1 lists captured in the window.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<DbSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (intervalSeconds < 1) return InvalidArg<DbSnapshot>(nameof(intervalSeconds), "must be >= 1");

        var resolved = await ResolveContextAsync<DbSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            intervalSeconds,
            cancellationToken).ConfigureAwait(false);

        var inlineSnapshot = snapshot;
        if (depth == SamplingDepth.Summary)
        {
            inlineSnapshot = snapshot with
            {
                ByCommand = snapshot.ByCommand.Take(5).ToList(),
                NPlusOne = snapshot.NPlusOne.Take(5).ToList(),
            };
        }

        var topCommand = snapshot.ByCommand.Count > 0 ? snapshot.ByCommand[0] : null;
        var poolExhaustedCount = snapshot.ConnectionPool.Sum(static stats => stats.PoolExhaustedCount);
        var summary = snapshot.TotalCommands == 0
            ? $"No DB commands captured in {durationSeconds}s. Start the collection before the workload and ensure the target emits EF Core or SqlClient diagnostics."
            : $"Captured {snapshot.TotalCommands} DB command(s) over {durationSeconds}s across {snapshot.ByCommand.Count} distinct command shape(s). " +
              $"Top command: {topCommand?.CommandTextHash} ({topCommand?.Count} call(s), p95={topCommand?.P95Ms:F1}ms). " +
              $"N+1 incidents: {snapshot.NPlusOne.Count}. Pool exhaustion signals: {poolExhaustedCount}.";

        var handle = handles.Register(pid, CollectionHandleKinds.DbSnapshot, snapshot, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineSnapshot,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot",
                "Drill into this DB snapshot without re-collecting (views: summary, byCommand, n+1, connectionPool).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "n+1", ["topN"] = 25 }),
            new NextActionHint("collect_sample",
                snapshot.NPlusOne.Count > 0 || snapshot.ByCommand.Any(static command => command.P95Ms > 50)
                    ? "Correlate slow-query hotspots with CPU stacks from the same process."
                    : "If DB latency looks healthy, pivot to CPU sampling or logs for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures completed ActivitySource spans via the Microsoft-Diagnostics-DiagnosticSource EventPipe bridge. " +
        "Enables the runtime provider with FilterAndPayloadSpecs, extracts operation/trace/span ids, parent linkage, tags, and duration from Activity stop events, aggregates them by source and operation, " +
        "and returns a handle for query_collection drilldown.")]
    public static async Task<DiagnosticResult<ActivityCapture>> CollectActivities(
        IActivityCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Optional ActivitySource name filters. Supports '*' and '?' wildcards. Null/empty captures all sources.")]
        IReadOnlyList<string>? sources = null,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of captured activities to retain. Must be >= 1. Defaults to 200.")] int maxActivities = 200,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<ActivityCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxActivities < 1) return InvalidArg<ActivityCapture>(nameof(maxActivities), "must be >= 1");

        var resolved = await ResolveContextAsync<ActivityCapture>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var capture = await collector
            .CollectAsync(pid, TimeSpan.FromSeconds(durationSeconds), sources, maxActivities, cancellationToken)
            .ConfigureAwait(false);

        var truncated = capture.TotalActivities > capture.Activities.Count;
        var topSource = capture.BySource.Count > 0 ? capture.BySource[0] : null;
        var topOperation = capture.ByOperation.Count > 0 ? capture.ByOperation[0] : null;
        var summary = capture.TotalActivities == 0
            ? $"No ActivitySource spans in {durationSeconds}s. Verify the target emits ActivitySource instrumentation or widen the 'sources' filter."
            : $"Captured {capture.Activities.Count} activity record(s) out of {capture.TotalActivities} observed over {durationSeconds}s across {capture.BySource.Count} source(s). " +
              $"Top source: {topSource?.SourceName} ({topSource?.Count}). Top operation: {topOperation?.SourceName}/{topOperation?.OperationName} ({topOperation?.Count})." +
              (truncated ? $" Truncated by maxActivities={maxActivities}; summaries reflect the stored subset." : string.Empty);

        var primaryHint = topOperation is { MaxDurationMs: > 250 }
            ? new NextActionHint("collect_sample",
                $"Correlate the slowest captured operation ({topOperation.SourceName}/{topOperation.OperationName}, max {topOperation.MaxDurationMs:F1} ms) with CPU hotspots in the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 })
            : new NextActionHint("collect_events",
                "Cross-check ActivitySource timing with runtime counters for the same process.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds });

        var handle = handles.Register(pid, CollectionHandleKinds.Activities, capture, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            capture,
            summary,
            handle.Id,
            handle.ExpiresAt,
            primaryHint,
            new NextActionHint("query_snapshot",
                "Drill into these activities without re-collecting (views: summary, bySource, byOperation, activities).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byOperation" })),
            resolved.Context);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Generic EventSource passthrough: opens an EventPipe session for a single EventSource " +
        "by name (e.g. System.Net.Http, Microsoft.AspNetCore.Hosting, Microsoft-AspNetCore-Server-Kestrel, " +
        "or any user-defined source) and returns the events emitted during the window. Use this to " +
        "investigate HTTP activity, hosting events, or domain-specific instrumentation.")]
    public static async Task<DiagnosticResult<EventSourceCapture>> CollectEventSource(
        IEventSourceCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'. Must be on the curated allowlist (see `Diagnostics:EventSourceAllowlist`) unless the bearer principal holds the 'eventsource-any' scope (RFC 0001 §2.3 / B5.4) — or, on legacy deployments, `unsafeProvider=true` AND the server has `Diagnostics:AllowSensitiveHeapValues=true` (issue #165 / M2).")] string providerName,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("EventSource keyword mask. -1 (default) means all keywords. For non-allowlisted providers (when opted in via unsafeProvider=true) this is clamped to a safer default when left at -1; pass an explicit positive mask to override.")] long keywords = -1,
        [Description("Event verbosity level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Informational, 5=Verbose). Defaults to 5. For non-allowlisted providers (when opted in via unsafeProvider=true) this is clamped to Informational unless explicitly set lower.")] int eventLevel = 5,
        [Description("Maximum number of captured events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Events[] list inline (keeps the Total count and metadata). 'detail' includes Events up to maxEvents. 'raw' is equivalent to detail. The full capture is always retained behind the issued handle — drill in with query_collection(handle, view=byEventName|events).")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("Opt-in switch for non-allowlisted EventSource providers (issue #165 / M2). Only honoured when the server has `Diagnostics:AllowSensitiveHeapValues=true`. Defaults to false; deny path returns an `EventSourceProviderNotAllowed` envelope.")] bool unsafeProvider = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(providerName)) return InvalidArg<EventSourceCapture>(nameof(providerName), "is required");
        if (durationSeconds < 1) return InvalidArg<EventSourceCapture>(nameof(durationSeconds), "must be >= 1");
        if (maxEvents < 1) return InvalidArg<EventSourceCapture>(nameof(maxEvents), "must be >= 1");

        // B4 / issue #165 / M2: gate non-curated providers behind the sensitive-value flag.
        // Custom EventSources frequently log user ids, auth-failure context, etc.
        var allowedByDefault = allowlist.IsAllowed(providerName);
        // RFC 0001 §2.3 / B5.4: scope-first predicate is
        // 'principal.HasExplicitScope("eventsource-any") OR allowlist allows'.
        // The principal-side check lets us emit a once-per-process deprecation warning
        // when the allowlist (not the scope) was the bypass mechanism. The allowlist
        // policy itself is retained — only the implicit deployment-wide "every caller
        // can capture allowlisted providers" pattern is deprecated for caller-level
        // distinction.
        var principalAllowsAny = principalAccessor.Current?.HasExplicitScope("eventsource-any") == true;
        if (!allowedByDefault)
        {
            // Caller can use unsafeProvider when EITHER the server has the legacy
            // AllowSensitiveHeapValues flag set, OR their bearer principal holds the
            // 'eventsource-any' modifier scope. Either path bypasses the curated
            // allowlist for THIS call only; the warn-on-allow audit line is emitted
            // by the tool filter.
            if (!unsafeProvider || (!sensitiveGate.IsAllowedByServer && !principalAllowsAny))
            {
                var preview = string.Join(", ", allowlist.AllowedProviders.Take(8));
                return DiagnosticResult.Fail<EventSourceCapture>(
                    $"EventSource provider '{providerName}' is not on the allowlist.",
                    new DiagnosticError(
                        "EventSourceProviderNotAllowed",
                        "Add the provider to `Diagnostics:EventSourceAllowlist` (env: `Diagnostics__EventSourceAllowlist__0=<provider>`), grant the caller the 'eventsource-any' scope (RFC 0001 §2.3), or — on legacy deployments — set `Diagnostics:AllowSensitiveHeapValues=true` on the server AND pass `unsafeProvider=true` per call. Curated allowlist includes: " + preview + (allowlist.AllowedProviders.Count > 8 ? ", …" : "") + ". Tracked by issue #165 (B4); subsumed into the 'eventsource-any' modifier scope by B5.4.",
                        providerName));
            }

            // unsafeProvider path was taken. If the principal lacks the scope, the
            // AllowSensitiveHeapValues flag is the bypass mechanism — surface the
            // sensitive-heap deprecation (that flag is the one truly going away).
            if (deprecation is not null && !principalAllowsAny && sensitiveGate.IsAllowedByServer)
            {
                deprecation.NotifySensitiveHeapValuesFlagBypass();
            }

            // Opt-in path: clamp the dangerous defaults (verbose + every-keyword) so the
            // capture doesn't accidentally pull every payload field at full verbosity.
            if (keywords == -1) keywords = 0;
            if (eventLevel > 4) eventLevel = 4;
        }
        else if (deprecation is not null && !principalAllowsAny)
        {
            // The curated allowlist (not the scope) authorised this call. Fire the
            // once-per-process deprecation telemetry so operators see they should be
            // distinguishing capable callers with the 'eventsource-any' scope rather
            // than relying on the deployment-wide allowlist alone.
            deprecation.NotifyEventSourceAllowlistBypass();
        }

        var resolved = await ResolveContextAsync<EventSourceCapture>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var capture = await collector.CaptureAsync(
            pid, providerName, TimeSpan.FromSeconds(durationSeconds), keywords, eventLevel, maxEvents, cancellationToken).ConfigureAwait(false);

        var inlineCapture = capture;
        var droppedCapEvents = 0;
        if (depth == SamplingDepth.Summary && capture.Events.Count > 0)
        {
            droppedCapEvents = capture.Events.Count;
            inlineCapture = capture with { Events = Array.Empty<CapturedEvent>() };
        }

        var summary = capture.Events.Count == 0
            ? $"No events from '{providerName}' in {durationSeconds}s. Verify the provider name and that it's actually instrumented in the target."
            : (depth == SamplingDepth.Summary && droppedCapEvents > 0
                ? $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s. Dropped {droppedCapEvents} Event(s) from inline (handle has all)."
                : $"Captured {capture.Events.Count} event(s) from '{providerName}' over {durationSeconds}s.");

        var handle = handles.Register(pid, CollectionHandleKinds.EventSource, capture, CollectionHandleTtl);
        return WithContext(DiagnosticResult.OkWithHandle(
            inlineCapture,
            summary,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("collect_events", "Cross-check captured events against runtime counters for the same window.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("query_snapshot",
                "Drill into this capture without re-collecting (views: summary, byEventName, events).",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byEventName" })),
            resolved.Context);
    }

    [RequireScope("dump-write", "ptrace")]
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
        "Heavyweight — use only when live collectors are insufficient. " +
        "**Requires `confirm=true` (defense in depth — RFC 0001 §4 / B5.6).** Without it the tool " +
        "returns a `confirmation_required` envelope describing what would have been written and " +
        "writes nothing to disk; the operator-facing client should surface this preview to a human " +
        "and only retry with `confirm=true` after explicit approval. The `dump-write` + `ptrace` " +
        "scopes are still required on top of `confirm=true`.")]
    public static async Task<DiagnosticResult<DumpToolResult>> CollectProcessDump(
        IProcessDumper dumper,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory = null,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). Dump files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Defense-in-depth confirmation flag. Must be true to actually write a dump file; without it the tool returns a `confirmation_required` envelope describing what would have been written. See RFC 0001 §4.")] bool confirm = false,
        CancellationToken cancellationToken = default)
    {
        if (!confirm)
        {
            // RFC 0001 §4 / §8: confirmation-required is a misuse signal, not an attack.
            // Log at Information level with the token name (never the bearer value), the
            // tool name and the reason as structured properties so audit consumers can
            // filter on the RFC-mandated `tool` / `reason` fields.
            //
            // The confirmation gate runs BEFORE process-context resolution (#187 review):
            // ResolveContextAsync would otherwise open an EventPipe session via
            // CapabilityDetector to probe the target — that already counts as a process
            // attach for the purpose of "writes NOTHING / no process attach". When the
            // caller relied on auto-resolution we therefore echo a null TargetPid in the
            // preview instead of touching the target.
            var logger = loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.CollectProcessDump");
            logger?.LogInformation(
                "collect_process_dump rejected: confirmation_required. tokenName={TokenName} tool={Tool} reason={Reason} requestedPid={RequestedPid} dumpType={DumpType}",
                principalAccessor.Current?.Name ?? "(none)",
                "collect_process_dump",
                "ConfirmationRequired",
                processId,
                dumpType);

            var preview = new DumpToolResult
            {
                Kind = DumpToolResultKinds.ConfirmationRequired,
                Message = processId is null
                    ? "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed. processId was not supplied — the server will auto-select a .NET process when you re-issue with confirm=true."
                    : "collect_process_dump writes a heap dump to disk. Pass confirm=true to proceed.",
                TargetPid = processId,
                DumpType = dumpType,
                OutputDirectory = outputDirectory,
            };

            var retryArgs = new Dictionary<string, object?>
            {
                ["dumpType"] = dumpType.ToString(),
                ["outputDirectory"] = outputDirectory,
                ["confirm"] = true,
            };
            if (processId is not null) retryArgs["processId"] = processId;

            var retryHint = new NextActionHint(
                "collect_process_dump",
                "Re-issue the call with confirm=true after explicit human approval. Required scopes: dump-write + ptrace.",
                retryArgs);

            var summary = processId is null
                ? $"confirmation_required: collect_process_dump would write a {dumpType} dump for the auto-selected .NET process. Pass confirm=true to proceed."
                : $"confirmation_required: collect_process_dump would write a {dumpType} dump for pid {processId}. Pass confirm=true to proceed.";
            return DiagnosticResult.Ok(preview, summary, retryHint);
        }

        var resolved = await ResolveContextAsync<DumpToolResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        return await GuardAttachAsync("collect_process_dump", pid, async () =>
        {
            var dump = await dumper.WriteDumpAsync(pid, dumpType, outputDirectory, cancellationToken).ConfigureAwait(false);
            var hint = dumpType == ProcessDumpType.Mini
                ? new NextActionHint("inspect_heap",
                    "Mini dump captured — heap walk unavailable. Re-capture with dumpType='WithHeap' for full inspection.",
                    new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath })
                : new NextActionHint("inspect_heap",
                    "Inspect the dump's managed heap for top-retained types + handoff payload to dotnet-assembly-mcp.",
                    new Dictionary<string, object?> { ["dumpFilePath"] = dump.FilePath, ["topTypes"] = 20 });
            var payload = new DumpToolResult
            {
                Kind = DumpToolResultKinds.DumpWritten,
                TargetPid = dump.ProcessId,
                DumpType = dump.DumpType,
                OutputDirectory = outputDirectory,
                Dump = dump,
            };
            return WithContext(DiagnosticResult.Ok(
                payload,
                $"Wrote {dumpType} dump for pid {dump.ProcessId} to {dump.FilePath} ({dump.FileSizeBytes:N0} bytes).",
                hint), ctx);
        }, cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("heap-read")]
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
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Absolute path to a previously-captured .dmp file. Required.")] string dumpFilePath,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; adds an extra pass over AppDomains × Modules × Types.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<DumpInspection>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

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
                    IncludeDuplicateStrings: includeDuplicateStrings,
                    SymbolPath: symbolPath),
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
            "query_snapshot",
            "Drill into the snapshot (e.g. richer top-N, retention paths filtered by type) without re-walking the heap.",
            new Dictionary<string, object?>
            {
                ["handle"] = handle,
                ["view"] = "top-types",
            });
    }

    [RequireScope("heap-read", "ptrace")]
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
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower and lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; lengthens the suspend window.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<LiveHeapInspection>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

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
                    IncludeDuplicateStrings: includeDuplicateStrings,
                    SymbolPath: symbolPath),
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

    [RequireScope("heap-read")]
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
        "`duplicate-strings` (duplicate System.String contents ranked by aggregate retained bytes — requires includeDuplicateStrings=true), " +
        "`gchandles` (GCHandle table aggregated by public GCHandleType-compatible buckets with top target types), " +
        "`object` (dump one managed object by address — SOS !do equivalent), " +
        "`gcroot` (find a shortest GC-root chain for one object address — SOS !gcroot equivalent), " +
        "`objsize` (compute the transitive retained size rooted at one object address — SOS !objsize equivalent), " +
        "`async` (pending async state machines reconstructed from the heap — state, awaiter type, and best-effort continuation chain à la SOS DumpAsync). " +

        "Handles expire ~10 minutes after the capture and are invalidated when the target process exits (live origin only).")]
    public static async Task<DiagnosticResult<HeapSnapshotQueryResult>> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'object', 'gcroot', 'objsize' or 'async'.")] string view = "top-types",
        [Description("Maximum entries to return for any ranked view ('top-types', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'async'). Ignored by 'roots-by-kind', 'gchandles', 'retention-paths', 'object', 'gcroot' and 'objsize'.")] int topN = 50,
        [Description("For view='top-types': ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("For view='retention-paths': case-insensitive substring matched against TypeFullName to narrow the returned chains.")] string? typeFullName = null,
        [Description("For view='object', 'gcroot' and 'objsize': managed object address (decimal or 0x-prefixed hex).")] string? address = null,
        [Description("Opt-in to return raw string content / field value previews on the 'duplicate-strings' and 'object' views (issue #165 / H4). Defaults to false — those fields are returned as metadata-only placeholders unless the server enables `Diagnostics:AllowSensitiveHeapValues=true` AND the caller passes `includeSensitiveValues=true`. Any string surfaced even in that mode still runs through the SensitiveDataRedactor (Bearer/PEM/JWT/connection-string/AWS-key patterns).")] bool includeSensitiveValues = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<HeapSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<HeapSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<HeapSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Heap snapshot handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("inspect_heap", "Re-attach and re-walk to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        // RFC 0001 §2.4: the legacy AllowSensitiveHeapValues flag is the deployment-wide gate;
        // a principal holding the 'sensitive-heap-read' scope opts in per-bearer (B5.2). The
        // caller still has to pass includeSensitiveValues=true to actually receive raw content.
        var principalUnlocksSensitive = principalAccessor.Current?.HasExplicitScope("sensitive-heap-read") == true;
        var emitSensitive = sensitiveGate.ShouldEmit(includeSensitiveValues, principalUnlocksSensitive);

        // B5.4: warn once per process if the legacy server flag (rather than the
        // 'sensitive-heap-read' scope) is what unlocked sensitive-value emission.
        if (emitSensitive && !principalUnlocksSensitive && sensitiveGate.IsAllowedByServer)
        {
            deprecation?.NotifySensitiveHeapValuesFlagBypass();
        }

        var normalizedView = view.Trim().ToLowerInvariant();
        switch (normalizedView)
        {
            case "top-types":
                return QueryTopTypes(snapshot, handle, topN, rankBy);
            case "retention-paths":
                return QueryRetentionPaths(snapshot, handle, typeFullName, topN);
            case "roots-by-kind":
                return QueryRootsByKind(snapshot, handle);
            case "finalizer-queue":
                return QueryFinalizerQueue(snapshot, handle, topN);
            case "fragmentation":
                return QueryFragmentation(snapshot, handle, topN);
            case "static-fields":
                return QueryStaticFields(snapshot, handle, topN);
            case "delegate-targets":
                return QueryDelegateTargets(snapshot, handle, topN);
            case "duplicate-strings":
                return QueryDuplicateStrings(snapshot, handle, topN, redactor, emitSensitive);
            case "gchandles":
                return QueryGcHandles(snapshot, handle);
            case "async":
                return QueryAsync(snapshot, handle, topN);
            case "object":
            case "gcroot":
            case "objsize":
                if (string.IsNullOrWhiteSpace(address)) return InvalidArg<HeapSnapshotQueryResult>(nameof(address), $"is required for view='{normalizedView}'");
                if (!TryParseUnsignedHexOrInt(address, out var parsedAddress) || parsedAddress == 0)
                {
                    return InvalidArg<HeapSnapshotQueryResult>(nameof(address), "must be a non-zero address (decimal or 0x-prefixed hex)");
                }

                return await GuardAttachAsync(
                    "query_heap_snapshot",
                    snapshot.Origin == HeapSnapshotOrigin.Live ? snapshot.ProcessId : null,
                    async () => normalizedView switch
                    {
                        "object" => QueryObject(snapshot, handle, await inspector.InspectObjectAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false), redactor, emitSensitive),
                        "gcroot" => QueryGcRoot(snapshot, handle, await inspector.InspectGcRootAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                        _ => QueryObjectSize(snapshot, handle, await inspector.InspectObjectSizeAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                    },
                    cancellationToken).ConfigureAwait(false);
            default:
                return InvalidArg<HeapSnapshotQueryResult>(nameof(view), $"must be 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'object', 'gcroot', 'objsize' or 'async' (got '{view}')");
        }
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObject(
        HeapSnapshotArtifact snapshot, string handle, HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        var sanitized = SanitizeObjectInspection(inspection, redactor, emitSensitive);
        var summary = $"Returning object 0x{sanitized.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{sanitized.TypeFullName}` ({sanitized.Size:N0} bytes, {sanitized.Generation}/{sanitized.SegmentKind}).";
        if (!emitSensitive && (inspection.IsString || (inspection.Fields is { Count: > 0 })))
        {
            summary += " String/field value previews are redacted (issue #165 / H4); pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to opt in.";
        }
        if (snapshot.Origin == HeapSnapshotOrigin.Live)
        {
            summary += " Live-object addresses can move after a GC; re-run inspect_live_heap if this address stops resolving.";
        }

        var result = new HeapSnapshotQueryResult(handle, "object", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = sanitized.Address,
            ObjectDetails = sanitized,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    // B4 / issue #165 / H4: rewrite an inspector-produced HeapObjectInspection so that
    // (a) when the sensitive-value gate is closed, every string preview and field value
    // is replaced with a metadata-only placeholder; (b) when the gate is open, every
    // string still passes through the redactor pattern set.
    private static HeapObjectInspection SanitizeObjectInspection(HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        IReadOnlyList<HeapObjectField>? fields = inspection.Fields;
        if (fields is { Count: > 0 })
        {
            var sanitizedFields = new List<HeapObjectField>(fields.Count);
            foreach (var f in fields)
            {
                var value = emitSensitive ? (redactor.Redact(f.Value) ?? f.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedFields.Add(new HeapObjectField(f.Name, f.TypeFullName, value)
                {
                    ObjectAddress = f.ObjectAddress,
                    ReferencedTypeFullName = f.ReferencedTypeFullName,
                });
            }
            fields = sanitizedFields;
        }

        IReadOnlyList<HeapArrayElement>? array = inspection.ArraySample;
        if (array is { Count: > 0 })
        {
            var sanitizedArray = new List<HeapArrayElement>(array.Count);
            foreach (var a in array)
            {
                var value = emitSensitive ? (redactor.Redact(a.Value) ?? a.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedArray.Add(new HeapArrayElement(a.Index, a.TypeFullName, value)
                {
                    ObjectAddress = a.ObjectAddress,
                    ReferencedTypeFullName = a.ReferencedTypeFullName,
                });
            }
            array = sanitizedArray;
        }

        string? stringValue = inspection.StringValue;
        if (inspection.IsString)
        {
            stringValue = emitSensitive ? redactor.Redact(stringValue) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
        }

        return new HeapObjectInspection(inspection.Address, inspection.TypeFullName, inspection.Size, inspection.SegmentKind, inspection.Generation)
        {
            IsArray = inspection.IsArray,
            ArrayLength = inspection.ArrayLength,
            ArraySample = array,
            IsString = inspection.IsString,
            StringValue = stringValue,
            StringValueTruncated = inspection.StringValueTruncated,
            Fields = fields,
            Warnings = inspection.Warnings,
        };
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryGcRoot(
        HeapSnapshotArtifact snapshot, string handle, HeapGcRootInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning GC-root chain for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` with {inspection.Chain.Count:N0} frame(s).";
        if (inspection.Truncated)
        {
            summary += " Chain is truncated by the BFS/depth safety caps.";
        }

        var result = new HeapSnapshotQueryResult(handle, "gcroot", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            GcRoot = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObjectSize(
        HeapSnapshotArtifact snapshot, string handle, HeapObjectSizeInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning object graph size for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` retains {inspection.RetainedBytes:N0} bytes across {inspection.ObjectCount:N0} object(s).";
        if (inspection.Truncated)
        {
            summary += " Result is truncated by the safety cap and is therefore a lower bound.";
        }

        var result = new HeapSnapshotQueryResult(handle, "objsize", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            ObjectSize = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
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
                new NextActionHint("inspect_heap",
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
        HeapSnapshotArtifact snapshot, string handle, int topN, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DuplicateStrings is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without duplicate-string aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDuplicateStrings=true.", handle));
        }
        // B4 / issue #165 / H4: previews routinely contain secrets. Default to metadata-only
        // (StringLength + InstanceCount + TotalBytes survive — they're enough to identify
        // string-interning opportunities). When the caller opts in and the server gate is
        // open, redact known secret shapes before returning the preview.
        var slice = snapshot.DuplicateStrings.Take(topN).Select(s =>
        {
            string preview;
            if (emitSensitive)
            {
                preview = redactor.Redact(s.Preview) ?? s.Preview;
            }
            else
            {
                preview = SensitiveDataRedactor.MetadataOnlyPlaceholder;
            }
            return new DuplicateStringStat(preview, s.StringLength, s.InstanceCount, s.TotalBytes, s.PreviewTruncated);
        }).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no duplicated System.String contents."
            : (emitSensitive
                ? $"Returning {slice.Length} duplicated string(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Previews pass through the SensitiveDataRedactor (Bearer/PEM/JWT/conn-string patterns) — consider string.Intern() / a cache for the hottest entries."
                : $"Returning {slice.Length} duplicated string(s) (metadata-only — string previews redacted per issue #165 / H4) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to reveal previews.");
        var result = new HeapSnapshotQueryResult(handle, "duplicate-strings", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DuplicateStrings = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryGcHandles(
        HeapSnapshotArtifact snapshot, string handle)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.GcHandles is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without GCHandle aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_heap to capture the GCHandle table for this snapshot.", handle));
        }

        var view = snapshot.GcHandles;
        var busiest = view.ByKind.OrderByDescending(bucket => bucket.Count).ThenBy(bucket => bucket.Kind, StringComparer.Ordinal).FirstOrDefault();
        var summary = view.TotalHandles == 0
            ? $"Snapshot '{handle}' has no GCHandle entries."
            : $"Returning GCHandle aggregation from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — {view.TotalHandles:N0} total handles. Busiest bucket: `{busiest?.Kind ?? "<none>"}` with {busiest?.Count ?? 0:N0} handle(s) retaining {busiest?.RetainedBytes ?? 0:N0} bytes across the immediate target objects.";

        if (view.Notes.Length > 0)
        {
            summary += $" Notes: {view.Notes[0]}";
        }

        var result = new HeapSnapshotQueryResult(handle, "gchandles", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            GcHandles = view,
        };

        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryAsync(
        HeapSnapshotArtifact snapshot, string handle, int topN)
    {
        var origin = snapshot.Origin.ToString();
        var asyncOperations = snapshot.AsyncOperations ?? Array.Empty<AsyncOperationStat>();
        var ordered = asyncOperations
            .OrderBy(op => op.ObservedOrder ?? long.MaxValue)
            .ThenByDescending(op => op.DirectSizeBytes)
            .Take(topN)
            .ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' has no pending async state machines."
            : $"Returning {ordered.Length} pending async operation(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). First pending state machine in heap-walk order: `{ordered[0].StateMachineTypeFullName}` (state {ordered[0].State}, awaiter `{ordered[0].AwaiterTypeFullName ?? "<unknown>"}`, async-stack depth {ordered[0].Stack?.Count ?? 0}).";
        var result = new HeapSnapshotQueryResult(handle, "async", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            AsyncOperations = ordered,
            SortedBy = ordered.Any(op => op.ObservedOrder.HasValue) ? "heap-order" : "direct-size",
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static readonly TimeSpan ThreadSnapshotHandleTtl = TimeSpan.FromMinutes(10);
    internal const string ThreadSnapshotKind = "thread-snapshot";

    [RequireScope("ptrace")]
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
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Maximum stack frames captured per thread. Defaults to 64.")] int maxFramesPerThread = 64,
        [Description("Include runtime frames (PInvoke trampolines, etc.) without an associated managed method. Off by default.")] bool includeRuntimeFrames = false,
        [Description("Include pure native frames where ClrMD cannot resolve a method. Off by default.")] bool includeNativeFrames = false,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns only the top-3 blocked threads inline and drops the SyncBlock lock-graph (use query_thread_snapshot(view=lock-graph) for the full graph). 'detail' returns the historical top-25 threads + top-25 locks. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
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

        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<ThreadSnapshotQueryResult>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

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
            var opts = new ThreadSnapshotOptions(maxFramesPerThread, includeRuntimeFrames, includeNativeFrames, symbolPath);
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
            ThreadSnapshotQueryResult summaryView;
            string summary;
            if (depth == SamplingDepth.Summary)
            {
                var topBlocked = snapshot.Threads
                    .OrderByDescending(t => t.IsLikelyBlocked)
                    .ThenByDescending(t => t.LockCount)
                    .Take(3)
                    .ToArray();
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = topBlocked,
                    Locks = Array.Empty<MonitorLockState>(),
                };
                var droppedThreads = snapshot.Threads.Count - topBlocked.Length;
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Showing top {topBlocked.Length} blocked inline (dropped {droppedThreads} thread(s) and {snapshot.Locks.Count} lock(s); handle has all). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_thread_snapshot(view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }
            else
            {
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = snapshot.Threads.Take(25).ToArray(),
                    Locks = snapshot.Locks.Take(25).ToArray(),
                };
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_thread_snapshot(view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }

            if (snapshot.SnapshotKind is not "exact")
            {
                summary += $" SnapshotKind={snapshot.SnapshotKind}";
                if (snapshot.WindowSeconds is int w)
                {
                    summary += $" over {w}s window";
                }
                summary += ".";
            }
            if (snapshot.Warnings is { Count: > 0 })
            {
                summary += $" Caveats: {string.Join(" ", snapshot.Warnings.Take(3))}";
            }

            var hint = contended > 0
                ? new NextActionHint("query_snapshot",
                    "Check the captured lock graph for wait-for cycles before drilling into individual stacks.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "deadlocks" })
                : blocked > 0
                    ? new NextActionHint("query_snapshot",
                        "Drill into the top blocked threads.",
                        new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-blocked" })
                    : null;

            var result = hint is null
                ? DiagnosticResult.Ok(summaryView, summary)
                : DiagnosticResult.Ok(summaryView, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("ptrace")]
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
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). Defaults to 'method-bytes/{pid}'. MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). .bin files are written with POSIX mode 0600.")] string? outputDirectory = null,
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

    [Description(
        "Streams a PE or PDB for a loaded managed module in repeated CallTool chunks so sibling MCPs can materialise pod-local binaries through the orchestrator proxy. " +
        "Resolve the module by ModuleVersionId (GUID 'D'); asset defaults to 'pe'. For PDBs the tool prefers a sibling .pdb next to the module, then falls back to an embedded portable PDB inside the PE. " +
        "processId is optional — when omitted the server auto-selects a live .NET process via the usual resolver. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetModuleBytes(
        IModuleByteSource moduleByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        [Description("PE module MVID (GUID 'D' format) of the loaded module to stream. Required.")] string moduleVersionId,
        [Description("Artifact to stream: 'pe' (default) or 'pdb'.")] string asset = "pe",
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.GetModuleBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_module_bytes",
            identifierName: "mvid",
            identifierValue: mvid.ToString("D"),
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        var resolved = await ResolveContextAsync<ByteFetchEnvelope>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        return await GuardAttachAsync("get_module_bytes", resolved.ProcessId, async () =>
        {
            try
            {
                var envelope = await moduleByteSource.FetchAsync(resolved.ProcessId, mvid, asset, offset, maxBytes, cancellationToken).ConfigureAwait(false);
                AuditByteFetch(logger, principalAccessor.Current, "get_module_bytes", envelope.Identifier, null, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
                var result = BuildByteFetchResult(
                    envelope,
                    BuildByteFetchSummary(envelope),
                    BuildModuleByteFetchHint(envelope, resolved.ProcessId, asset, maxBytes));
                return WithContext(result, resolved.Context);
            }
            catch (FileNotFoundException ex)
            {
                return ArtifactNotFound<ByteFetchEnvelope>("get_module_bytes", ex.Message, ex.FileName ?? mvid.ToString("D"));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    [Description(
        "Streams a dump file under the artifact root in repeated CallTool chunks so sibling MCPs can materialise pod-local dumps through the orchestrator proxy. dumpFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetDumpBytes(
        IDumpByteSource dumpByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Dump path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string dumpFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath)) return InvalidArg<ByteFetchEnvelope>(nameof(dumpFilePath), "is required");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.GetDumpBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_dump_bytes",
            identifierName: "dumpPath",
            identifierValue: dumpFilePath,
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        try
        {
            var envelope = await dumpByteSource.FetchAsync(dumpFilePath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            AuditByteFetch(logger, principalAccessor.Current, "get_dump_bytes", null, envelope.Identifier, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
            return BuildByteFetchResult(envelope, BuildByteFetchSummary(envelope), BuildDumpByteFetchHint(envelope, maxBytes));
        }
        catch (DotnetDiagnosticsMcp.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint("get_bytes",
                    "Re-issue with a path under the artifact root; absolute paths must still resolve under that root after symlink resolution."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound<ByteFetchEnvelope>("get_dump_bytes", ex.Message, ex.FileName ?? dumpFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
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

    [RequireScope("ptrace")]
    [Description(
        "Returns a slice of a thread snapshot previously captured by collect_thread_snapshot, addressed by its handle. Views: " +
        "`threads-summary` (every managed thread with state + top frame), " +
        "`stack` (full captured frames of one thread — requires `threadId`; for `linux-native-stack` snapshots this is the OS thread id / TID), " +
        "`lock-graph` (every SyncBlock that is held or contended, sorted by waiter count then recursion), " +
        "`deadlocks` (wait-for cycle detection over the captured lock graph, with lock chains and suggested SOS follow-up commands), " +
        "`top-blocked` (threads ranked by IsLikelyBlocked then LockCount — fastest path to spot contention), " +
        "`unique-stacks` (groups threads by identical top-frame signatures and returns representative canonical stacks), " +
        "`async-stalls` (best-effort classification of async-looking stacks such as Task.Result/Wait, Channel waits, TCS waits, SemaphoreSlim.WaitAsync and Task.Delay), " +
        "`threadpool` (SOS !threadpool-style snapshot of worker/IOCP counts plus global/local queue depths and pending work items when the backend captured them). " +
        "Handles expire ~10 minutes after capture; live-origin handles are invalidated when the target PID exits.")]
    public static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by collect_thread_snapshot.")] string handle,
        [Description("Which slice to return: 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls' or 'threadpool'.")] string view = "top-blocked",
        [Description("For view='stack': thread id key to return frames for. CoreCLR snapshots use ManagedThreadId; linux-native-stack snapshots use OSThreadId (TID). Ignored by other views.")] int? threadId = null,
        [Description("Maximum entries returned by ranked-list views ('threads-summary', 'top-blocked', 'lock-graph', 'unique-stacks') or the number of deadlock cycles returned by 'deadlocks'. Defaults to 50.")] int topN = 50,
        [Description("For view='unique-stacks': number of top frames folded into the signature hash. Defaults to 20. Ignored by other views.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("For view='unique-stacks': drop groups with fewer than this many threads. Defaults to 1. Ignored by other views.")] int minCount = 1)
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
            "deadlocks" => QueryDeadlocks(snapshot, handle, origin, topN),
            "top-blocked" => QueryTopBlocked(snapshot, handle, origin, topN),
            "unique-stacks" => QueryUniqueStacks(snapshot, handle, origin, topN, framesToHash, minCount),
            "async-stalls" => QueryAsyncStalls(snapshot, handle, origin, topN),
            "threadpool" => QueryThreadPool(snapshot, handle, origin),
            _ => InvalidArg<ThreadSnapshotQueryResult>(nameof(view), $"must be 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls' or 'threadpool' (got '{view}')"),
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
        var isLinuxNativeStack = string.Equals(snapshot.Source, "linux-native-stack", StringComparison.Ordinal);
        var thread = isLinuxNativeStack
            ? snapshot.Threads.FirstOrDefault(t =>
                threadId.Value > 0 &&
                (uint)threadId.Value == t.OSThreadId)
            : snapshot.Threads.FirstOrDefault(t => t.ManagedThreadId == threadId.Value);
        if (thread is null)
        {
            var threadKind = isLinuxNativeStack ? "OS thread" : "managed thread";
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"{threadKind} {threadId.Value} not present in snapshot '{handle}'.",
                new DiagnosticError("ThreadNotFound", "The captured snapshot does not contain this thread id.", threadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new NextActionHint("query_snapshot",
                    "List the captured threads first.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "threads-summary" }));
        }
        var selectedId = isLinuxNativeStack ? threadId.Value : thread.ManagedThreadId;
        var threadLabel = isLinuxNativeStack ? "OS thread" : "managed thread";
        var summary = $"Stack of {threadLabel} {selectedId} (OS {thread.OSThreadId}, state {thread.State}) from snapshot '{handle}' — {thread.Frames.Count} frame(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "stack", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                Thread = thread,
                ThreadId = selectedId,
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

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryDeadlocks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var deadlocks = ThreadDeadlockDetector.Detect(snapshot, handle, topN);
        var edgeCount = snapshot.Locks.Sum(lockState => lockState.WaitingManagedThreadIds.Count(waiterId => waiterId > 0 && waiterId != lockState.OwnerManagedThreadId));
        var summary = deadlocks.Count == 0
            ? $"No deadlock cycles detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s)."
            : $"Detected {deadlocks.Count} deadlock cycle(s) in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "deadlocks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Deadlocks = deadlocks },
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

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryUniqueStacks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN, int framesToHash, int minCount)
    {
        if (framesToHash < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(framesToHash), "must be >= 1");
        if (minCount < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(minCount), "must be >= 1");

        var allGroups = ThreadSnapshotUniqueStackGrouper.Group(snapshot.Threads, framesToHash, minCount, int.MaxValue);
        var pagedGroups = allGroups.Take(topN).ToArray();
        var summary = pagedGroups.Length == 0
            ? $"Snapshot '{handle}' has no unique stack groups with at least {minCount} thread(s)."
            : $"Returning {pagedGroups.Length}/{allGroups.Count} unique stack group(s) from snapshot '{handle}' hashed over the top {framesToHash} frame(s). Largest group: {pagedGroups[0].ThreadCount}/{snapshot.Threads.Count} thread(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "unique-stacks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                UniqueStacks = pagedGroups,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryAsyncStalls(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var view = AsyncStallClassifier.Classify(snapshot, topN);
        var summary = view.ClassifiedThreads == 0
            ? $"No async-looking parked continuations detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId})."
            : $"Classified {view.ClassifiedThreads} async-looking thread(s) from snapshot '{handle}'. Top bucket: {view.ByBucket[0].Bucket} ({view.ByBucket[0].Count}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "async-stalls", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                AsyncStalls = view,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadPool(
        ThreadSnapshotArtifact snapshot, string handle, string origin)
    {
        if (snapshot.ThreadPool is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Snapshot '{handle}' does not contain ThreadPool counters/queues.",
                new DiagnosticError("ViewNotCaptured", "Re-run collect_thread_snapshot on a ClrMD-backed CoreCLR target; fallback backends may not capture ThreadPool internals.", handle));
        }

        var threadPool = snapshot.ThreadPool;
        var summary = $"ThreadPool from snapshot '{handle}': workers {threadPool.Workers.Current} current ({threadPool.Workers.Active} active, {threadPool.Workers.Idle} idle, min {threadPool.Workers.Min}, max {threadPool.Workers.Max}), IOCP {threadPool.Iocp.Current} current (idle {threadPool.Iocp.Idle}, min {threadPool.Iocp.Min}, max {threadPool.Iocp.Max}), pending work items {threadPool.PendingWorkItems} (global {threadPool.Queues.GlobalQueueLength}, local {threadPool.Queues.LocalQueues.Sum(q => q.QueueLength)} across {threadPool.Queues.LocalQueues.Count} local queue(s)).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threadpool", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                ThreadPool = threadPool,
            },
            summary);
    }


    [RequireScope("investigation-export")]
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

    [RequireScope("investigation-export")]
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
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
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

    [RequireScope("investigation-export")]
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
            new NextActionHint("collect_sample",
                diff.Verdict.StartsWith("regression", StringComparison.Ordinal)
                    ? "Re-sample the regressing process and drill into the new top frame."
                    : "Optional: capture a fresh sample to confirm the improvement is stable.",
                new Dictionary<string, object?> { ["durationSeconds"] = 20 }));
    }


    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<T>? RequireLiteralScope<T>(
        IPrincipalAccessor principalAccessor,
        Microsoft.Extensions.Logging.ILogger? logger,
        string tool,
        string identifierName,
        string identifierValue,
        long offset)
    {
        var principal = principalAccessor.Current;
        if (principal?.HasExplicitScope("module-bytes-read") == true)
        {
            return null;
        }

        logger?.LogWarning(
            "{Tool} denied: explicit module-bytes-read scope required. tokenName={TokenName} {IdentifierName}={IdentifierValue} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            identifierName,
            identifierValue,
            offset,
            0,
            0);

        return DiagnosticResult.Fail<T>(
            $"{tool} requires the literal scope 'module-bytes-read'. Root or wildcard tokens do not auto-grant this modifier scope.",
            new DiagnosticError(
                "Forbidden",
                "Grant the bearer principal the literal scope 'module-bytes-read'. Root ('*') is intentionally insufficient for this modifier scope.",
                principal?.Name),
            new NextActionHint(
                tool,
                "Retry with a bearer token that explicitly includes 'module-bytes-read'."));
    }

    private static void AuditByteFetch(
        Microsoft.Extensions.Logging.ILogger? logger,
        BearerPrincipal? principal,
        string tool,
        string? mvid,
        string? dumpPath,
        long offset,
        int chunkSize,
        long totalSize)
    {
        if (logger is null)
        {
            return;
        }

        if (mvid is not null)
        {
            logger.LogInformation(
                "{Tool} streamed bytes. tokenName={TokenName} mvid={Mvid} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
                tool,
                principal?.Name ?? "(none)",
                mvid,
                offset,
                chunkSize,
                totalSize);
            return;
        }

        logger.LogInformation(
            "{Tool} streamed bytes. tokenName={TokenName} dumpPath={DumpPath} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            dumpPath ?? "(none)",
            offset,
            chunkSize,
            totalSize);
    }

    private static DiagnosticResult<ByteFetchEnvelope> BuildByteFetchResult(
        ByteFetchEnvelope envelope,
        string summary,
        NextActionHint? hint)
        => hint is null
            ? DiagnosticResult.Ok(envelope, summary)
            : DiagnosticResult.Ok(envelope, summary, hint);

    private static string BuildByteFetchSummary(ByteFetchEnvelope envelope)
    {
        var streamed = envelope.ChunkSize == 0
            ? $"No bytes remain at offset {envelope.Offset:N0}"
            : $"Streamed {envelope.ChunkSize:N0} byte(s) from offset {envelope.Offset:N0}";
        var more = envelope.NextOffset is long next
            ? $" Next chunk starts at offset {next:N0}."
            : " Stream complete.";
        return $"{streamed} of {envelope.TotalSize:N0} total byte(s) for {envelope.Kind} {envelope.Identifier} ({envelope.Asset}).{more}";
    }

    private static NextActionHint? BuildModuleByteFetchHint(ByteFetchEnvelope envelope, int processId, string asset, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same module asset.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "module",
                    ["moduleVersionId"] = envelope.Identifier,
                    ["asset"] = string.IsNullOrWhiteSpace(asset) ? envelope.Asset : asset,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                    ["processId"] = processId,
                })
            : null;

    private static NextActionHint? BuildDumpByteFetchHint(ByteFetchEnvelope envelope, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same dump artifact.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "dump",
                    ["dumpFilePath"] = envelope.Identifier,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                })
            : null;

    private static DiagnosticResult<T> ArtifactNotFound<T>(string tool, string message, string detail)
        => DiagnosticResult.Fail<T>(
            $"{tool} could not locate the requested artifact: {message}",
            new DiagnosticError("ArtifactNotFound", message, detail));

    /// <summary>
    /// B4 / issue #165 / M3 helper: returns a denial envelope when the caller-supplied
    /// <paramref name="symbolPath"/> references a remote symbol-server host that is not on
    /// the configured allowlist. Returns <c>null</c> when the path is allowed (local path,
    /// empty/null, or remote host on the allowlist) so the caller can early-return only on
    /// denial. Must be invoked from every tool that forwards a caller-supplied
    /// <c>symbolPath</c> into a SymbolReader / native symbolicator backend. B5.2 layers a
    /// principal-side modifier scope on top: callers holding <c>symbols-remote</c>
    /// (RFC 0001 §2.5) bypass the allowlist entirely. The legacy server-wide allowlist
    /// keeps working byte-for-byte for principals without the scope.
    ///
    /// B5.4 / RFC 0001 §7.3: when the allowlist (not the scope) was the path that allowed
    /// a remote host through, fires a once-per-process deprecation warning via
    /// <paramref name="deprecation"/>. The allowlist policy itself is retained — only the
    /// pattern of relying on a single deployment-wide allowlist for caller-level
    /// distinction is deprecated.
    /// </summary>
    private static DiagnosticResult<T>? ValidateSymbolPath<T>(
        SymbolServerAllowlist allowlist,
        string? symbolPath,
        IPrincipalAccessor? principalAccessor = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null)
    {
        if (principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true)
        {
            return null;
        }
        var validation = allowlist.Validate(symbolPath);
        if (validation.IsAllowed)
        {
            // RFC 0001 §7.3: only emit when a remote host was actually accepted (not for
            // null / empty / pure local paths). Defers to SymbolServerAllowlist's own
            // tokenizer so a local cache directory whose name contains "http://" is not
            // a false positive.
            if (deprecation is not null && SymbolServerAllowlist.ContainsRemoteUrl(symbolPath))
            {
                deprecation.NotifySymbolServerAllowlistBypass();
            }
            return null;
        }
        return DiagnosticResult.Fail<T>(
            $"symbolPath references remote symbol server host '{validation.DeniedHost}' which is not on the allowlist.",
            new DiagnosticError(
                "SymbolServerNotAllowed",
                "Remote symbol servers are denied by default. Add the host to `Diagnostics:SymbolServerAllowlist` (env: `Diagnostics__SymbolServerAllowlist__0=<host>`), grant the caller the `symbols-remote` scope, or drop the `srv*http(s)://…` segment and rely on the local symbol cache. Tracked by issue #165.",
                validation.DeniedSegment));
    }

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
                new NextActionHint("inspect_process", "Re-list processes. Common cause: sidecar UID mismatch with target, or process has exited."));
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
                ? $"{tool} could not attach{pidHint}: ptrace was denied even though the sidecar's static capability probe expected attach to succeed ({ptrace.Reason}). " +
                  "Likely cause: target process exited, runs under a different UID, or containers are in separate PID namespaces (use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true')."
                : $"{tool} could not attach{pidHint}: ptrace was denied — {ptrace.Reason}";

            var hints = new List<NextActionHint>
            {
                new("inspect_process",
                    "Re-check sidecar capabilities (CanAttachClrMD, AttachClrMdReason). " +
                    "If CAP_SYS_PTRACE is granted, containers may be in separate PID namespaces — " +
                    "use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true'.",
                    processId is int pidForCap && pidForCap > 0
                        ? new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = pidForCap }
                        : new Dictionary<string, object?> { ["view"] = "capabilities" }),
                new("collect_process_dump",
                    "Fall back to dump-based workflow (collect_process_dump then inspect_heap/inspect_dump). " +
                    "Dumps use the diagnostic IPC socket (no ptrace) and work across PID namespaces.",
                    processId is int pp && pp > 0 ? new Dictionary<string, object?> { ["processId"] = pp } : null),
            };

            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                hints.Add(new NextActionHint(
                    "collect_off_cpu_sample",
                    "If ptrace cannot be granted, use the perf-replay fallback path tracked in issue #92 (short capture + thread-state inference).",
                    processId is int pidForReplay && pidForReplay > 0 ? new Dictionary<string, object?> { ["processId"] = pidForReplay, ["durationSeconds"] = 5 } : null));
            }

            return DiagnosticResult.Fail<T>(
                headline,
                new DiagnosticError("PermissionDenied", message, typeName),
                hints.ToArray());
        }

        if (ex is UnauthorizedAccessException)
        {
            // Enhanced hints covering both capability and PID namespace issues.
            var accessHints = new List<NextActionHint>
            {
                new("inspect_process",
                    "Verify UID alignment and capabilities. If CAP_SYS_PTRACE is granted but attach still fails, " +
                    "containers may be in separate PID namespaces — use Docker '--pid=container:target' or K8s 'shareProcessNamespace: true'.",
                    processId is int pid && pid > 0
                        ? new Dictionary<string, object?> { ["view"] = "capabilities", ["processId"] = pid }
                        : new Dictionary<string, object?> { ["view"] = "capabilities" }),
                new("collect_process_dump",
                    "Fall back to dump-based workflow: collect_process_dump then inspect_heap(source=\"dump\"). " +
                    "Dumps use the diagnostic IPC socket (no ptrace) and work across PID namespaces.",
                    processId is int pidForDump && pidForDump > 0
                        ? new Dictionary<string, object?> { ["processId"] = pidForDump }
                        : null),
            };

            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                accessHints.Add(new NextActionHint(
                    "collect_sample",
                    "When ptrace cannot be granted, use the perf-replay fallback tracked in issue #92.",
                    processId is int pidForReplay && pidForReplay > 0
                        ? new Dictionary<string, object?> { ["processId"] = pidForReplay, ["durationSeconds"] = 5 }
                        : null));
            }

            return DiagnosticResult.Fail<T>(
                $"{tool} was denied access{pidHint}.",
                new DiagnosticError("PermissionDenied", message, typeName),
                accessHints.ToArray());
        }

        if (ex is ExternalToolNotFoundException missingTool)
        {
            if (string.Equals(tool, "collect_thread_snapshot", StringComparison.Ordinal))
            {
                return DiagnosticResult.Fail<T>(
                    $"{tool} cannot run{pidHint}: required external tool '{missingTool.ToolName}' is missing.",
                    new DiagnosticError("ToolNotFound", message, typeName),
                    new NextActionHint("inspect_process",
                        "Re-check sidecar capabilities after installing elfutils (eu-stack).",
                        processId is int pidForCap && pidForCap > 0 ? new Dictionary<string, object?> { ["processId"] = pidForCap } : null));
            }

            return DiagnosticResult.Fail<T>(
                $"{tool} cannot run{pidHint}: required external tool '{missingTool.ToolName}' is missing.",
                new DiagnosticError("ToolNotFound", message, typeName));
        }

        if (ex is DotnetDiagnosticsMcp.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<T>(
                $"{tool} rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint(tool,
                    "Re-issue with a relative sub-path under the artifact root, or omit outputDirectory to use the default."));
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
