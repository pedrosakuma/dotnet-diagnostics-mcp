using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.OffCpu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Linux NativeAOT no-ptrace fallback that synthesizes an approximate thread snapshot from a short
/// <c>perf sched:sched_switch</c> window by replaying the last observed switch-out stack per TID.
/// </summary>
public sealed class PerfReplayThreadSnapshotInspector : IThreadSnapshotInspector
{
    internal const int DefaultWindowSeconds = 3;
    private readonly ILogger<PerfReplayThreadSnapshotInspector> _logger;
    private readonly string _configuredPath;
    private string? _resolvedPath;
    private bool _resolutionAttempted;
    private readonly object _resolveLock = new();

    public PerfReplayThreadSnapshotInspector(
        ILogger<PerfReplayThreadSnapshotInspector>? logger = null,
        string perfPath = "perf")
    {
        _logger = logger ?? NullLogger<PerfReplayThreadSnapshotInspector>.Instance;
        _configuredPath = perfPath;
    }

    public bool IsAvailable()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return false;
        return ResolvePerfPath() is not null;
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "PerfReplayThreadSnapshotInspector only supports live-process snapshots.");

    public async Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("PerfReplayThreadSnapshotInspector is only supported on Linux.");
        }

        if (!IsAvailable())
        {
            throw new ExternalToolNotFoundException(
                "perf",
                "perf is not available on this host. Install linux-perf and ensure CAP_PERFMON (or perf_event_paranoid <= -1).");
        }

        var opts = options ?? new ThreadSnapshotOptions();
        Validate(opts);

        var capturedAt = DateTimeOffset.UtcNow;
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var window = TimeSpan.FromSeconds(DefaultWindowSeconds);

        var targetTids = ReadTargetTids(processId);
        if (targetTids.Count == 0)
        {
            throw new InvalidOperationException(
                $"Could not enumerate any TID under /proc/{processId}/task. The process may have exited.");
        }

        var script = await CaptureScriptAsync(window, cancellationToken).ConfigureAwait(false);
        var lastByTid = PerfSchedScriptParser.ParseLastSeenSwitchOut(script, targetTids);
        var threads = BuildApproximateThreads(lastByTid, opts.MaxFramesPerThread);

        warnings.Add(
            $"Approximate snapshot from perf sched replay over a {DefaultWindowSeconds}s window; this is not point-in-time.");
        warnings.Add("Each thread shows the last stack seen at sched_switch OUT within the window (not its current stack).");
        warnings.Add("Threads that never switched off-CPU during the window are omitted.");
        warnings.Add("Wait reason is inferred from sched_switch prev_state (S/D/X), not /proc/.../wchan.");
        if (threads.Count == 0)
        {
            warnings.Add("No switch-out stacks were observed for target threads in this window; increase activity and retry.");
        }

        sw.Stop();
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: sw.Elapsed,
            RuntimeName: "NativeAot",
            RuntimeVersion: string.Empty,
            Threads: threads,
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "perf-replay-thread-snapshot",
            SnapshotKind = "perf-replay-approx",
            WindowSeconds = DefaultWindowSeconds,
            Warnings = warnings,
        };
    }

    internal static IReadOnlyList<ManagedThread> BuildApproximateThreads(
        IReadOnlyDictionary<int, PerfSchedScriptParser.LastSeenSwitchOut> lastByTid,
        int maxFramesPerThread)
    {
        var threads = new List<ManagedThread>(lastByTid.Count);
        foreach (var kv in lastByTid.OrderBy(k => k.Key))
        {
            var replay = kv.Value;
            var frames = replay.Stack
                .Take(maxFramesPerThread)
                .Select(f => new ManagedStackFrame(
                    Kind: "Native",
                    DisplayName: f.Method,
                    TypeFullName: null,
                    ModuleName: string.IsNullOrWhiteSpace(f.Module) ? null : Path.GetFileName(f.Module),
                    InstructionPointer: 0,
                    StackPointer: 0,
                    Identity: null))
                .ToList();

            var (state, isAlive, isLikelyBlocked, reason) = InferThreadStateFromPrevState(replay.PrevState);
            var top = frames.Count > 0 ? frames[0] : null;
            threads.Add(new ManagedThread(
                ManagedThreadId: replay.Tid,
                OSThreadId: unchecked((uint)replay.Tid),
                Address: 0,
                State: state,
                IsAlive: isAlive,
                IsBackground: false,
                IsFinalizer: false,
                IsGc: false,
                IsThreadpoolWorker: false,
                LockCount: 0,
                CurrentExceptionType: null,
                TopFrameMethod: top?.DisplayName,
                Frames: frames)
            {
                IsLikelyBlocked = isLikelyBlocked,
                InferredWaitReason = reason,
            });
        }

        return threads;
    }

    private static void Validate(ThreadSnapshotOptions opts)
    {
        if (opts.MaxFramesPerThread <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(opts), "MaxFramesPerThread must be positive.");
        }
        if (opts.MaxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap)
        {
            throw new ArgumentOutOfRangeException(nameof(opts),
                $"MaxFramesPerThread must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap}.");
        }
    }

    private static (string State, bool IsAlive, bool IsLikelyBlocked, string? WaitReason) InferThreadStateFromPrevState(string prevState)
    {
        return prevState switch
        {
            "S" => ("S", true, true, "BlockedSleeping"),
            "D" => ("D", true, true, "BlockedOnUninterruptibleIO"),
            "X" => ("X", false, false, "Exited"),
            "R" => ("R", true, false, "Runnable"),
            _ => (prevState, true, false, null),
        };
    }

    private async Task<string> CaptureScriptAsync(TimeSpan duration, CancellationToken ct)
    {
        var perfDataPath = Path.Combine(Path.GetTempPath(),
            $"diagnosticsmcp-threadsnap-perf-{Guid.NewGuid():N}.data");
        try
        {
            await RecordAsync(perfDataPath, duration, ct).ConfigureAwait(false);
            return await RunScriptAsync(perfDataPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(perfDataPath);
        }
    }

    private async Task RecordAsync(string outputPath, TimeSpan duration, CancellationToken ct)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds));
        var args = $"record -a -e sched:sched_switch --call-graph dwarf -o \"{outputPath}\" -- sleep {seconds}";
        _logger.LogDebug("Spawning perf for replay thread snapshot: {Bin} {Args}", ResolvePerfPath()!, args);

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
            try { process.Kill(true); } catch { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf record (sched replay) exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
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
            try { process.Kill(true); } catch { }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"perf script (sched replay) exited with code {process.ExitCode}. stderr: {stderr.Trim()}");
        }

        return stdout;
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
            // best effort
        }

        set.Add(pid);
        return set;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
