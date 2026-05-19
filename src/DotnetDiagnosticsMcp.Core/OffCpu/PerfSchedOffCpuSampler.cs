using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.OffCpu;

/// <summary>
/// Linux off-CPU sampler driven by <c>perf record -a -e sched:sched_switch --call-graph dwarf -- sleep N</c>.
/// System-wide capture is required because <c>sched_switch</c> fires on the OUTGOING task only; restricting
/// the recorder to one PID would deny us the matching INCOMING event needed to close each off-CPU span.
/// Post-filter by the target's TID set keeps memory bounded and the result attributable.
/// </summary>
/// <remarks>
/// <para>Requirements (validated by <see cref="IsAvailable"/>): Linux host, <c>perf</c> binary in <c>PATH</c>,
/// and either <c>CAP_PERFMON</c> (kernel ≥ 5.8) or <c>perf_event_paranoid &lt;= -1</c>. <c>-a</c> system-wide
/// tracepoint access is broader than the on-CPU sampler's per-PID profile and may need an extra capability
/// on locked-down hosts; we propagate stderr verbatim when <c>perf record</c> fails so the LLM gets the
/// actionable kernel diagnostic.</para>
/// <para>The blocking stack is the kernel+user callchain captured at <c>sched_switch</c> on the outgoing
/// thread — typically <c>schedule → futex_wait_queue → __pthread_cond_wait</c>. We do NOT attempt to merge
/// managed frames here; that lands in sub-slice 2c together with the <c>depth</c> parameter.</para>
/// </remarks>
public sealed class PerfSchedOffCpuSampler : IOffCpuSampler
{
    private readonly ILogger<PerfSchedOffCpuSampler> _logger;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfSchedOffCpuSampler(
        ILogger<PerfSchedOffCpuSampler>? logger = null,
        string perfPath = "perf")
    {
        _logger = logger ?? NullLogger<PerfSchedOffCpuSampler>.Instance;
        _configuredPath = perfPath;
    }

    private string? ResolvePerfPath()
    {
        if (_resolutionAttempted) return _resolvedPath;
        lock (_resolveLock)
        {
            if (_resolutionAttempted) return _resolvedPath;
            _resolvedPath = PerfBinaryResolver.Resolve(
                _configuredPath,
                PerfBinaryResolver.EnumerateDefaultLinuxToolsCandidates,
                PerfBinaryResolver.ProbePerfVersion);
            _resolutionAttempted = true;
            return _resolvedPath;
        }
    }

    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        return ResolvePerfPath() is not null;
    }

    public async Task<OffCpuSampleResult> SampleAsync(
        int processId,
        TimeSpan duration,
        int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be (0, 5min].");
        }
        if (topN <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(topN), "topN must be positive.");
        }
        if (!IsAvailable())
        {
            throw new InvalidOperationException(
                "perf is not available on this host. Install linux-perf and ensure the diagnostics " +
                "container has CAP_PERFMON (or CAP_SYS_ADMIN) plus permission for system-wide tracepoint " +
                "access (perf_event_paranoid <= -1 on locked-down kernels).");
        }

        var targetTids = ReadTargetTids(processId);
        if (targetTids.Count == 0)
        {
            throw new InvalidOperationException(
                $"Could not enumerate any TID under /proc/{processId}/task. The process may have exited.");
        }

        var perfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-offcpu-{processId}-{Guid.NewGuid():N}.data");
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            await RecordAsync(perfDataPath, duration, cancellationToken).ConfigureAwait(false);
            var script = await RunScriptAsync(perfDataPath, cancellationToken).ConfigureAwait(false);
            var (spans, switches) = PerfSchedScriptParser.Parse(script, targetTids);
            return Aggregate(processId, startedAt, duration, spans, switches, topN);
        }
        finally
        {
            TryDelete(perfDataPath);
        }
    }

    private static HashSet<int> ReadTargetTids(int pid)
    {
        var set = new HashSet<int>();
        var taskDir = $"/proc/{pid}/task";
        try
        {
            if (Directory.Exists(taskDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(taskDir))
                {
                    var name = Path.GetFileName(dir);
                    if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tid))
                    {
                        set.Add(tid);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Best effort. PID itself is always added below.
        }
        set.Add(pid);
        return set;
    }

    private async Task RecordAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        // -a: system-wide (mandatory for sched_switch IN-side pairing).
        // -e sched:sched_switch: the only tracepoint we currently parse.
        // --call-graph dwarf: user-space DWARF unwinding so we capture user frames, not just the
        // first kernel frame. Bigger perf.data but accuracy-critical for blocking-stack attribution.
        var args = $"record -a -e sched:sched_switch --call-graph dwarf -o \"{outputPath}\" -- sleep {seconds}";
        _logger.LogDebug("Spawning perf for off-CPU capture: {Bin} {Args}", ResolvePerfPath()!, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

        process.Start();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf record (sched) exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }
    }

    private async Task<string> RunScriptAsync(string perfDataPath, CancellationToken ct)
    {
        var args = $"script -i \"{perfDataPath}\" --no-inline";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ResolvePerfPath()!,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { /* best effort */ }
            throw;
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf script exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }
        return stdout;
    }

    internal static OffCpuSampleResult Aggregate(
        int processId,
        DateTimeOffset startedAt,
        TimeSpan duration,
        IReadOnlyList<OffCpuSpan> spans,
        long schedSwitches,
        int topN)
    {
        // Stack aggregation key = leaf->root string join so identical blocking points collapse.
        var byStack = new Dictionary<string, (long Micros, long Count, Dictionary<string, long> States, List<OffCpuFrame> Frames)>(StringComparer.Ordinal);
        var byThread = new Dictionary<int, (string Comm, long Micros, long Switches, Dictionary<string, long> LeafCounts)>();
        long totalMicros = 0;

        foreach (var span in spans)
        {
            totalMicros += span.DurationMicros;

            // Root→leaf string (perf prints leaf→root; reverse for human-friendly stacks).
            var frames = new List<OffCpuFrame>(span.BlockingStack.Count);
            for (var i = span.BlockingStack.Count - 1; i >= 0; i--) frames.Add(span.BlockingStack[i]);
            var leaf = frames.Count > 0 ? frames[^1] : new OffCpuFrame("", "[no-stack]");
            var key = string.Join('|', frames.Select(f => string.IsNullOrEmpty(f.Module) ? f.Method : $"{f.Module}!{f.Method}"));

            if (!byStack.TryGetValue(key, out var agg))
            {
                agg = (0, 0, new Dictionary<string, long>(StringComparer.Ordinal), frames);
            }
            agg.Micros += span.DurationMicros;
            agg.Count += 1;
            agg.States[span.PrevState] = agg.States.GetValueOrDefault(span.PrevState) + 1;
            byStack[key] = agg;

            if (!byThread.TryGetValue(span.Tid, out var tagg))
            {
                tagg = (span.Comm, 0, 0, new Dictionary<string, long>(StringComparer.Ordinal));
            }
            tagg.Micros += span.DurationMicros;
            tagg.Switches += 1;
            var leafKey = string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}";
            tagg.LeafCounts[leafKey] = tagg.LeafCounts.GetValueOrDefault(leafKey) + 1;
            byThread[span.Tid] = tagg;
        }

        var stacks = byStack
            .Select(kv =>
            {
                var dominant = kv.Value.States.OrderByDescending(s => s.Value).FirstOrDefault().Key ?? "?";
                var leaf = kv.Value.Frames.Count > 0 ? kv.Value.Frames[^1] : new OffCpuFrame("", "[no-stack]");
                return new OffCpuStackHotspot(
                    LeafFrame: string.IsNullOrEmpty(leaf.Module) ? leaf.Method : $"{leaf.Module}!{leaf.Method}",
                    OffCpuMicros: kv.Value.Micros,
                    OccurrenceCount: kv.Value.Count,
                    DominantState: dominant,
                    Stack: kv.Value.Frames);
            })
            .OrderByDescending(s => s.OffCpuMicros)
            .ToList();

        var threads = byThread
            .Select(kv =>
            {
                var topLeaf = kv.Value.LeafCounts.OrderByDescending(p => p.Value).FirstOrDefault().Key ?? "[no-stack]";
                return new OffCpuThreadView(
                    Tid: kv.Key,
                    ThreadName: kv.Value.Comm,
                    OffCpuMicros: kv.Value.Micros,
                    SwitchCount: kv.Value.Switches,
                    TopBlockingLeaf: topLeaf);
            })
            .OrderByDescending(t => t.OffCpuMicros)
            .ToList();

        var summary = new OffCpuSnapshot(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: totalMicros,
            DistinctThreads: byThread.Count,
            TopBlockingStacks: stacks.Take(topN).ToList(),
            SchedSwitches: schedSwitches,
            SymbolSource: "perf-sched-dwarf");

        var artifact = new OffCpuSnapshotArtifact(
            ProcessId: processId,
            StartedAt: startedAt,
            Duration: duration,
            TotalOffCpuMicros: totalMicros,
            SchedSwitches: schedSwitches,
            Stacks: stacks,
            Threads: threads,
            SymbolSource: "perf-sched-dwarf");

        return new OffCpuSampleResult(summary, artifact);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
