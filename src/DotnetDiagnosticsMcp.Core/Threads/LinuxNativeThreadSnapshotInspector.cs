using System.Diagnostics;
using System.Globalization;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Linux NativeAOT fallback for <see cref="IThreadSnapshotInspector"/>. Uses <c>eu-stack -p &lt;pid&gt;</c>
/// to capture one native stack per OS thread and maps Linux wait state from
/// <c>/proc/&lt;pid&gt;/task/&lt;tid&gt;/status</c> + <c>wchan</c>.
/// </summary>
public sealed class LinuxNativeThreadSnapshotInspector : IThreadSnapshotInspector
{
    private readonly ILogger<LinuxNativeThreadSnapshotInspector> _logger;
    private readonly string _euStackPath;

    public LinuxNativeThreadSnapshotInspector(
        ILogger<LinuxNativeThreadSnapshotInspector>? logger = null,
        string euStackPath = "eu-stack")
    {
        _logger = logger ?? NullLogger<LinuxNativeThreadSnapshotInspector>.Instance;
        _euStackPath = euStackPath;
    }

    public bool IsAvailable() => OperatingSystem.IsLinux() && ResolveEuStackPath() is not null;

    public Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), "Process id must be positive.");
        }

        var opts = options ?? new ThreadSnapshotOptions();
        Validate(opts);
        return CaptureLiveAsync(processId, opts, cancellationToken);
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "LinuxNativeThreadSnapshotInspector only supports live-process snapshots. Dump snapshots use ClrMdThreadSnapshotInspector.");

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

    private async Task<ThreadSnapshotArtifact> CaptureLiveAsync(int processId, ThreadSnapshotOptions options, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("LinuxNativeThreadSnapshotInspector is only supported on Linux.");
        }

        EnsureAttachPermissions(processId);

        var capturedAt = DateTimeOffset.UtcNow;
        var warnings = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        var euStackResult = await RunEuStackAsync(processId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(euStackResult.PartialWarning))
        {
            warnings.Add(euStackResult.PartialWarning!);
        }
        var parsedThreads = ParseEuStackOutput(euStackResult.Stdout, options.MaxFramesPerThread);

        var threads = new List<ManagedThread>(parsedThreads.Count);
        foreach (var t in parsedThreads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (state, isAlive, likelyBlocked, reason) = ReadThreadState(processId, t.Tid, warnings);
            var frames = t.Frames
                .Select(f => new ManagedStackFrame(
                    Kind: "Native",
                    DisplayName: f.DisplayName,
                    TypeFullName: null,
                    ModuleName: f.ModuleName,
                    InstructionPointer: f.InstructionPointer,
                    StackPointer: 0,
                    Identity: null))
                .ToList();

            threads.Add(BuildManagedThread(t, frames, state, isAlive, likelyBlocked, reason));
        }

        stopwatch.Stop();
        return new ThreadSnapshotArtifact(
            Origin: ThreadSnapshotOrigin.Live,
            ProcessId: processId,
            CapturedAt: capturedAt,
            WalkDuration: stopwatch.Elapsed,
            RuntimeName: "NativeAot",
            RuntimeVersion: string.Empty,
            Threads: threads,
            Locks: Array.Empty<MonitorLockState>())
        {
            Source = "linux-native-stack",
            Warnings = warnings.Count > 0 ? warnings : null,
        };
    }

    /// <summary>
    /// Builds the <see cref="ManagedThread"/> view for one parsed native thread. The Linux TID is
    /// used both as <see cref="ManagedThread.OSThreadId"/> and as <see cref="ManagedThread.ManagedThreadId"/>
    /// so <c>query_thread_snapshot(view="stack")</c> can address each native thread by its TID — without
    /// it every native thread would collide on <c>ManagedThreadId = -1</c> and only the first would be
    /// reachable through the drilldown API.
    /// </summary>
    internal static ManagedThread BuildManagedThread(
        ParsedNativeThread parsed,
        IReadOnlyList<ManagedStackFrame> frames,
        string state,
        bool isAlive,
        bool isLikelyBlocked,
        string? inferredWaitReason)
    {
        var top = frames.Count > 0 ? frames[0] : null;
        return new ManagedThread(
            ManagedThreadId: parsed.Tid,
            OSThreadId: unchecked((uint)parsed.Tid),
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
            InferredWaitReason = inferredWaitReason,
        };
    }

    private static void EnsureAttachPermissions(int processId)
    {
        var ptrace = PtraceProbe.Detect();
        if (!ptrace.CanAttach)
        {
            throw new UnauthorizedAccessException(
                $"Cannot collect native thread snapshot for pid {processId}: {ptrace.Reason} " +
                "A perf-replay fallback is planned in issue #92 but not implemented in this slice.");
        }

        var selfUid = TryReadUid("/proc/self/status");
        var targetUid = TryReadUid($"/proc/{processId}/status");
        if (selfUid is null || targetUid is null)
        {
            return;
        }

        if (selfUid.Value != targetUid.Value)
        {
            throw new UnauthorizedAccessException(
                $"Cannot collect native thread snapshot for pid {processId}: sidecar UID ({selfUid.Value}) " +
                $"differs from target UID ({targetUid.Value}). Run the sidecar as the same UID.");
        }
    }

    private static int? TryReadUid(string statusPath)
    {
        try
        {
            foreach (var line in File.ReadLines(statusPath))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var tokens = line["Uid:".Length..]
                    .Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0) return null;
                if (int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid))
                {
                    return uid;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Result of an <c>eu-stack</c> invocation. When eu-stack exits non-zero but still emits
    /// stack frames on stdout (a common pattern on NativeAOT, where libdwfl fails to unwind
    /// past the AOT entrypoint frame of TID 1), the stdout is returned alongside a
    /// <see cref="PartialWarning"/> that explains why the exit was non-zero. See issue #105.
    /// </summary>
    internal readonly record struct EuStackResult(string Stdout, string? PartialWarning);

    private async Task<EuStackResult> RunEuStackAsync(int processId, CancellationToken cancellationToken)
    {
        var path = ResolveEuStackPath();
        if (path is null)
        {
            throw new ExternalToolNotFoundException(
                "eu-stack",
                "eu-stack is not available on this host. Install elfutils in the diagnostics sidecar.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                Arguments = $"-p {processId}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
            EnableRaisingEvents = true,
        };

        _logger.LogDebug("Spawning eu-stack for pid {Pid}: {Path} -p {Pid}", processId, path, processId);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return new EuStackResult(stdout, PartialWarning: null);
        }

        // Permission failures are surfaced as UnauthorizedAccessException so the router can
        // fall through to PerfReplayThreadSnapshotInspector (issue #92).
        if (LooksLikePermissionFailure(stderr))
        {
            throw new UnauthorizedAccessException(
                $"eu-stack could not attach to pid {processId}: {stderr.Trim()} " +
                "A perf-replay fallback is planned in issue #92 but not implemented in this slice.");
        }

        // On NativeAOT, eu-stack commonly emits valid frames for every thread and then fails to
        // unwind the last frame of the AOT entrypoint on TID 1 ("dwfl_thread_getframes ...
        // Callback returned failure"), exiting with code 1. Treat that as a partial success
        // (issue #105): hand back the stdout we collected and surface the stderr warning as a
        // snapshot-level note so the caller still gets thread visibility.
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            var note = $"eu-stack exited with code {process.ExitCode} but produced frames for the live threads; " +
                $"partial unwind warning: {stderr.Trim()}";
            _logger.LogDebug(
                "eu-stack returned partial output for pid {Pid} (exit={Exit}). stderr: {Stderr}",
                processId,
                process.ExitCode,
                stderr.Trim());
            return new EuStackResult(stdout, PartialWarning: note);
        }

        throw new InvalidOperationException(
            $"eu-stack exited with code {process.ExitCode} while collecting pid {processId}. stderr: {stderr.Trim()}");
    }

    private string? ResolveEuStackPath()
    {
        if (Path.IsPathRooted(_euStackPath) && File.Exists(_euStackPath))
        {
            return _euStackPath;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVar))
        {
            return null;
        }

        foreach (var segment in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(segment, _euStackPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool LooksLikePermissionFailure(string stderr)
        => stderr.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
           || stderr.Contains("ptrace", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<ParsedNativeThread> ParseEuStackOutput(string output, int maxFramesPerThread)
    {
        var threads = new List<ParsedNativeThread>();
        var lines = output.Split('\n');
        var currentTid = 0;
        var currentFrames = new List<ParsedNativeFrame>();

        void FlushCurrent()
        {
            if (currentTid <= 0) return;
            threads.Add(new ParsedNativeThread(currentTid, currentFrames.ToArray()));
            currentTid = 0;
            currentFrames = new List<ParsedNativeFrame>();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (TryParseThreadHeader(line, out var tid))
            {
                FlushCurrent();
                currentTid = tid;
                continue;
            }

            if (currentTid <= 0 || currentFrames.Count >= maxFramesPerThread)
            {
                continue;
            }

            if (!TryParseFrame(line, out var frame))
            {
                continue;
            }

            currentFrames.Add(frame);
        }

        FlushCurrent();
        return threads;
    }

    private static bool TryParseThreadHeader(string line, out int tid)
    {
        tid = 0;
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("TID ", StringComparison.Ordinal) &&
            !trimmed.StartsWith("Thread ", StringComparison.Ordinal))
        {
            return false;
        }

        var firstDigit = -1;
        for (var i = 0; i < trimmed.Length; i++)
        {
            if (char.IsDigit(trimmed[i]))
            {
                firstDigit = i;
                break;
            }
        }
        if (firstDigit < 0) return false;

        var end = firstDigit;
        while (end < trimmed.Length && char.IsDigit(trimmed[end]))
        {
            end++;
        }

        return int.TryParse(
            trimmed[firstDigit..end],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out tid);
    }

    private static bool TryParseFrame(string line, out ParsedNativeFrame frame)
    {
        frame = new ParsedNativeFrame(0, string.Empty, null);
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('#'))
        {
            return false;
        }

        var cursor = 1;
        while (cursor < trimmed.Length && char.IsDigit(trimmed[cursor])) cursor++;
        while (cursor < trimmed.Length && char.IsWhiteSpace(trimmed[cursor])) cursor++;
        if (cursor >= trimmed.Length) return false;

        var tokenEnd = cursor;
        while (tokenEnd < trimmed.Length && !char.IsWhiteSpace(trimmed[tokenEnd])) tokenEnd++;
        var ipToken = trimmed[cursor..tokenEnd];
        ulong ip = 0;
        if (ipToken.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            _ = ulong.TryParse(ipToken[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ip);
        }

        var rest = tokenEnd < trimmed.Length ? trimmed[tokenEnd..].Trim() : string.Empty;
        if (string.IsNullOrEmpty(rest))
        {
            return false;
        }

        string? modulePath = null;
        var moduleStart = rest.LastIndexOf(" (", StringComparison.Ordinal);
        if (moduleStart >= 0 && rest.EndsWith(')'))
        {
            modulePath = rest[(moduleStart + 2)..^1];
            rest = rest[..moduleStart].TrimEnd();
        }

        var atIndex = rest.IndexOf(" at ", StringComparison.Ordinal);
        if (atIndex > 0)
        {
            rest = rest[..atIndex];
        }
        var paramsIndex = rest.IndexOf(" (", StringComparison.Ordinal);
        if (paramsIndex > 0)
        {
            rest = rest[..paramsIndex];
        }

        var display = DemanglePreservingOffset(rest.Trim());
        if (string.IsNullOrEmpty(display))
        {
            display = "<unknown>";
        }

        var moduleName = string.IsNullOrWhiteSpace(modulePath) ? null : Path.GetFileName(modulePath);
        frame = new ParsedNativeFrame(
            InstructionPointer: ip,
            DisplayName: display,
            ModuleName: moduleName);
        return true;
    }

    private static string DemanglePreservingOffset(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return symbol;
        var plus = symbol.LastIndexOf("+0x", StringComparison.OrdinalIgnoreCase);
        if (plus < 0)
        {
            return NativeAotSymbolDemangler.Demangle(symbol);
        }

        var baseName = symbol[..plus];
        var suffix = symbol[plus..];
        return NativeAotSymbolDemangler.Demangle(baseName) + suffix;
    }

    private static (string State, bool IsAlive, bool IsLikelyBlocked, string? Reason) ReadThreadState(
        int processId,
        int tid,
        List<string> warnings)
    {
        var statusPath = $"/proc/{processId}/task/{tid}/status";
        var wchanPath = $"/proc/{processId}/task/{tid}/wchan";
        try
        {
            string? stateLine = null;
            foreach (var line in File.ReadLines(statusPath))
            {
                if (line.StartsWith("State:", StringComparison.Ordinal))
                {
                    stateLine = line["State:".Length..].Trim();
                    break;
                }
            }

            var stateCode = stateLine is { Length: > 0 } ? stateLine[0] : '?';
            var wchan = File.Exists(wchanPath) ? File.ReadAllText(wchanPath).Trim() : string.Empty;

            return stateCode switch
            {
                'R' => (stateLine ?? "R", true, false, "Running"),
                'S' when wchan.StartsWith("futex", StringComparison.OrdinalIgnoreCase)
                    => (stateLine ?? "S", true, true, "BlockedOnLock"),
                'S' when string.Equals(wchan, "do_select", StringComparison.Ordinal)
                       || string.Equals(wchan, "poll_schedule_timeout", StringComparison.Ordinal)
                    => (stateLine ?? "S", true, true, "BlockedOnIO"),
                'D' => (stateLine ?? "D", true, true, "BlockedOnUninterruptibleIO"),
                'T' => (stateLine ?? "T", true, true, "Stopped"),
                'Z' => (stateLine ?? "Z", false, false, null),
                'S' => ReportUnknownSleepingState(stateLine, wchan, tid, warnings),
                _ => (stateLine ?? "Unknown", true, false, null),
            };
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not read wait state for tid {tid}: {ex.GetType().Name}.");
            return ("Unknown", true, false, null);
        }
    }

    private static (string State, bool IsAlive, bool IsLikelyBlocked, string? Reason) ReportUnknownSleepingState(
        string? stateLine,
        string wchan,
        int tid,
        List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(wchan) &&
            !string.Equals(wchan, "0", StringComparison.Ordinal) &&
            !string.Equals(wchan, "unknown", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"tid {tid} is sleeping in wchan '{wchan}' (unmapped); wait reason left as unknown.");
        }

        return (stateLine ?? "S", true, false, null);
    }
}

internal sealed record ParsedNativeThread(int Tid, IReadOnlyList<ParsedNativeFrame> Frames);

internal sealed record ParsedNativeFrame(ulong InstructionPointer, string DisplayName, string? ModuleName);
