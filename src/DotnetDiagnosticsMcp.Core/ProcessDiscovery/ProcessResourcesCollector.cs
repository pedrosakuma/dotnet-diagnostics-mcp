using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Cross-platform process resource collector.
/// <list type="bullet">
/// <item><term>Linux</term><description>Reads <c>/proc/&lt;pid&gt;/fd</c>, <c>/proc/&lt;pid&gt;/net/tcp{,6}</c> and <c>/proc/&lt;pid&gt;/limits</c>.</description></item>
/// <item><term>Windows</term><description>Calls <c>GetProcessHandleCount</c>; per-handle/socket breakdown is not yet implemented.</description></item>
/// </list>
/// </summary>
public sealed partial class ProcessResourcesCollector : IProcessResourcesCollector
{
    private const int MaxClassifiedFdEntries = 10_000;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly string _procRoot;
    private readonly ILogger<ProcessResourcesCollector> _logger;
    private readonly TimeProvider _clock;

    public ProcessResourcesCollector(
        ILogger<ProcessResourcesCollector>? logger = null,
        TimeProvider? clock = null,
        string procRoot = "/proc")
    {
        _logger = logger ?? NullLogger<ProcessResourcesCollector>.Instance;
        _clock = clock ?? TimeProvider.System;
        _procRoot = procRoot;
    }

    /// <inheritdoc />
    public async Task<ProcessResources> CollectAsync(
        int processId,
        int durationSeconds,
        int sampleEverySeconds,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var samples = new List<CollectedSnapshot>();

        if (durationSeconds == 0)
        {
            var snapshot = TakeSample(processId, notes);
            return snapshot.ToReport(processId, notes, trend: null);
        }

        var startedAt = _clock.GetUtcNow();
        var deadline = startedAt.AddSeconds(durationSeconds);
        var interval = TimeSpan.FromSeconds(sampleEverySeconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            samples.Add(TakeSample(processId, notes));

            var now = _clock.GetUtcNow();
            if (now >= deadline)
            {
                break;
            }

            var remaining = deadline - now;
            var delay = remaining < interval ? remaining : interval;
            if (delay <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        if (samples.Count == 0)
        {
            samples.Add(new CollectedSnapshot(_clock.GetUtcNow(), null, null, null, null, null));
        }

        var trend = new ProcessResourcesTrend(samples.Select(static sample => sample.ToSample()).ToArray());
        return samples[^1].ToReport(processId, notes, trend);
    }

    private CollectedSnapshot TakeSample(int processId, List<string> notes)
    {
        var timestamp = _clock.GetUtcNow();
        try
        {
            if (OperatingSystem.IsLinux())
            {
                return TakeLinuxSample(processId, timestamp, notes);
            }

            if (OperatingSystem.IsWindows())
            {
                return TakeWindowsSample(processId, timestamp, notes);
            }

            AddNoteOnce(notes, $"Process resource collection is not supported on {RuntimeInformation.OSDescription}. Only Linux and Windows are implemented.");
            return new CollectedSnapshot(timestamp, null, null, null, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect process resources for pid {ProcessId}", processId);
            AddNoteOnce(notes, $"Resource snapshot failed: {ex.GetType().Name}: {ex.Message}");
            return new CollectedSnapshot(timestamp, null, null, null, null, null);
        }
    }

    private CollectedSnapshot TakeLinuxSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        var procDir = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture));
        var fd = ReadFdBreakdown(Path.Combine(procDir, "fd"), notes);
        IReadOnlySet<string>? socketInodes = null;
        if (fd.Success)
        {
            socketInodes = fd.SocketInodes ?? new HashSet<string>(StringComparer.Ordinal);
        }

        var sockets = ReadSocketBreakdown(processId, socketInodes, Path.Combine(procDir, "net", "tcp"), Path.Combine(procDir, "net", "tcp6"), notes);
        var limits = ReadLimits(processId, Path.Combine(procDir, "limits"), fd.FdCount, notes);

        return new CollectedSnapshot(timestamp, fd.FdCount, null, fd.Breakdown, sockets, limits);
    }

    [SupportedOSPlatform("windows")]
    private static CollectedSnapshot TakeWindowsSample(int processId, DateTimeOffset timestamp, List<string> notes)
    {
        uint handleCount = 0;
        IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (processHandle == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            AddNoteOnce(notes, $"OpenProcess({processId}) failed with error {error}; handle count is unavailable.");
        }
        else
        {
            try
            {
                if (!GetProcessHandleCount(processHandle, out handleCount))
                {
                    var error = Marshal.GetLastPInvokeError();
                    AddNoteOnce(notes, $"GetProcessHandleCount({processId}) failed with error {error}.");
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        AddNoteOnce(notes, "Windows handle breakdown is not yet supported.");
        return new CollectedSnapshot(timestamp, null, handleCount == 0 && processHandle == IntPtr.Zero ? null : (int?)handleCount, null, null, null);
    }

    private static FdCollectionResult ReadFdBreakdown(string fdDirectory, List<string> notes)
    {
        if (!Directory.Exists(fdDirectory))
        {
            AddNoteOnce(notes, $"Could not read {fdDirectory}: directory not found or unreadable.");
            return default;
        }

        try
        {
            var total = 0;
            var sockets = 0;
            var regular = 0;
            var pipes = 0;
            var eventfds = 0;
            var other = 0;
            var overflow = 0;
            var socketInodes = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in Directory.EnumerateFileSystemEntries(fdDirectory))
            {
                total++;
                if (total > MaxClassifiedFdEntries)
                {
                    overflow++;
                    other++;
                    continue;
                }

                string? target;
                try
                {
                    target = new FileInfo(entry).LinkTarget;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddNoteOnce(notes, $"Could not resolve one or more fd symlinks under {fdDirectory}: {ex.GetType().Name}.");
                    other++;
                    continue;
                }

                if (string.IsNullOrEmpty(target))
                {
                    other++;
                }
                else if (target.StartsWith("socket:[", StringComparison.Ordinal))
                {
                    sockets++;
                    var inode = ExtractSocketInode(target);
                    if (!string.IsNullOrEmpty(inode))
                    {
                        socketInodes.Add(inode);
                    }
                }
                else if (target.StartsWith('/'))
                {
                    regular++;
                }
                else if (target.StartsWith("pipe:[", StringComparison.Ordinal))
                {
                    pipes++;
                }
                else if (target.StartsWith("anon_inode:[eventfd]", StringComparison.Ordinal))
                {
                    eventfds++;
                }
                else
                {
                    other++;
                }
            }

            if (overflow > 0)
            {
                AddNoteOnce(notes, $"FD enumeration hit the {MaxClassifiedFdEntries} entry cap; {overflow} additional descriptors were counted as Other.");
            }

            return new FdCollectionResult(total, new FdBreakdown(sockets, regular, pipes, eventfds, other), socketInodes, Success: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNoteOnce(notes, $"Could not enumerate {fdDirectory}: {ex.GetType().Name}.");
            return default;
        }
    }

    private static SocketBreakdown? ReadSocketBreakdown(int processId, IReadOnlySet<string>? socketInodes, string tcpPath, string tcp6Path, List<string> notes)
    {
        if (socketInodes is null)
        {
            AddNoteOnce(notes, $"Skipping TCP state attribution for pid {processId}: fd socket inode enumeration was unavailable.");
            return null;
        }

        var established = 0;
        var timeWait = 0;
        var closeWait = 0;
        var listen = 0;
        var other = 0;
        var anyFileRead = false;

        foreach (var path in new[] { tcpPath, tcp6Path })
        {
            try
            {
                if (!File.Exists(path))
                {
                    AddNoteOnce(notes, $"Could not read {path}: file not found.");
                    continue;
                }

                var lines = File.ReadLines(path);
                var isHeader = true;
                foreach (var line in lines)
                {
                    if (isHeader)
                    {
                        isHeader = false;
                        continue;
                    }

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 10)
                    {
                        continue;
                    }

                    anyFileRead = true;
                    var inode = parts[9];
                    if (!socketInodes.Contains(inode))
                    {
                        continue;
                    }

                    switch (parts[3])
                    {
                        case "01":
                            established++;
                            break;
                        case "06":
                            timeWait++;
                            break;
                        case "08":
                            closeWait++;
                            break;
                        case "0A":
                            listen++;
                            break;
                        default:
                            other++;
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AddNoteOnce(notes, $"Could not read TCP state from {path}: {ex.GetType().Name}.");
            }
        }

        if (!anyFileRead)
        {
            AddNoteOnce(notes, $"No TCP state data was readable for pid {processId}.");
            return null;
        }

        return new SocketBreakdown(established, timeWait, closeWait, listen, other);
    }

    private static RLimits? ReadLimits(int processId, string limitsPath, int? fdCount, List<string> notes)
    {
        try
        {
            if (!File.Exists(limitsPath))
            {
                AddNoteOnce(notes, $"Could not read {limitsPath}: file not found.");
                return null;
            }

            foreach (var line in File.ReadLines(limitsPath))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 6)
                {
                    continue;
                }

                if (!parts[0].Equals("Max", StringComparison.Ordinal) ||
                    !parts[1].Equals("open", StringComparison.Ordinal) ||
                    !parts[2].Equals("files", StringComparison.Ordinal))
                {
                    continue;
                }

                var soft = ParseLimitValue(parts[^3]);
                var hard = ParseLimitValue(parts[^2]);
                double? fraction = fdCount is > 0 && soft is > 0
                    ? fdCount.Value / (double)soft.Value
                    : null;
                return new RLimits(soft, hard, fraction);
            }

            AddNoteOnce(notes, $"Could not find 'Max open files' in {limitsPath} for pid {processId}.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddNoteOnce(notes, $"Could not read {limitsPath}: {ex.GetType().Name}.");
            return null;
        }
    }

    private static long? ParseLimitValue(string token)
        => token.Equals("unlimited", StringComparison.OrdinalIgnoreCase)
            ? null
            : long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;

    private static string? ExtractSocketInode(string target)
    {
        const string prefix = "socket:[";
        if (!target.StartsWith(prefix, StringComparison.Ordinal) || !target.EndsWith(']'))
        {
            return null;
        }

        return target.Substring(prefix.Length, target.Length - prefix.Length - 1);
    }

    private static void AddNoteOnce(List<string> notes, string note)
    {
        if (!notes.Contains(note, StringComparer.Ordinal))
        {
            notes.Add(note);
        }
    }

    private readonly record struct FdCollectionResult(int? FdCount, FdBreakdown? Breakdown, IReadOnlySet<string>? SocketInodes, bool Success);

    private sealed record CollectedSnapshot(
        DateTimeOffset Timestamp,
        int? FdCount,
        int? HandleCount,
        FdBreakdown? Fd,
        SocketBreakdown? Sockets,
        RLimits? Limits)
    {
        public ProcessResourcesSample ToSample() => new(Timestamp, FdCount, HandleCount, Fd, Sockets, Limits);

        public ProcessResources ToReport(int processId, IReadOnlyList<string> notes, ProcessResourcesTrend? trend)
            => new(processId, Timestamp, FdCount, HandleCount, Fd, Sockets, Limits, notes.ToArray(), trend);
    }

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessHandleCount(IntPtr hProcess, out uint handleCount);
}
