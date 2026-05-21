using System.Diagnostics;
using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.Internal;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Default implementation of <see cref="IProcessDiscovery"/> for processes on the local machine.
/// </summary>
public sealed class LocalProcessDiscovery : IProcessDiscovery
{
    private readonly ILogger<LocalProcessDiscovery> _logger;

    public LocalProcessDiscovery(ILogger<LocalProcessDiscovery>? logger = null)
    {
        _logger = logger ?? NullLogger<LocalProcessDiscovery>.Instance;
    }

    public IReadOnlyList<DotnetProcess> ListProcesses()
    {
        var result = new List<DotnetProcess>();

        IEnumerable<int> published;
        try
        {
            published = DiagnosticsClient.GetPublishedProcesses();
        }
        catch (Exception ex)
        {
            // Defensive: never let the diagnostic-socket scanner crash the enumeration.
            // Stale or corrupt socket files under /tmp can throw on some hosts (issue #108).
            _logger.LogWarning(ex, "DiagnosticsClient.GetPublishedProcesses() failed; returning empty list.");
            return result;
        }

        foreach (var pid in published)
        {
            var info = SafeGetProcess(pid);
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return result;
    }

    public DotnetProcess? TryGetProcess(int processId) => SafeGetProcess(processId);

    /// <summary>
    /// Returns true when <paramref name="pid"/> resolves to a live, thread-group-leader OS process.
    /// On Linux this inspects <c>/proc/&lt;pid&gt;/status</c> and verifies that the entry is the
    /// thread-group leader (<c>Tgid == Pid</c>) — a plain <c>/proc/&lt;pid&gt;</c> existence check
    /// would also match worker thread IDs of unrelated processes, which collide with phantom PIDs
    /// surfaced by stale diagnostic sockets in shared <c>/tmp</c> volumes (issue #108). On other
    /// platforms the cross-platform <see cref="Process.GetProcessById(int)"/> is used. Any failure
    /// is treated as not-alive so that callers can safely skip stale entries.
    /// </summary>
    private static bool IsProcessAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (OperatingSystem.IsLinux())
        {
            return IsLinuxProcessLeaderAlive(pid);
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsLinuxProcessLeaderAlive(int pid)
    {
        var pidStr = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var statusPath = "/proc/" + pidStr + "/status";

        if (!File.Exists(statusPath))
        {
            return false;
        }

        try
        {
            // Read line-by-line; status starts with Name/Umask/State/Tgid/Pid — Tgid appears within
            // the first ~10 lines on every kernel since 2.6.x. We can bail as soon as we see it.
            using var reader = new StreamReader(statusPath);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.StartsWith("Tgid:", StringComparison.Ordinal))
                {
                    var rest = line.AsSpan("Tgid:".Length).Trim();
                    return int.TryParse(rest, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var tgid)
                        && tgid == pid;
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private DotnetProcess? SafeGetProcess(int pid)
    {
        if (!IsProcessAlive(pid))
        {
            // PID present in the diagnostic-socket scan (or supplied explicitly) but the OS
            // process is either gone or not a thread-group leader on Linux. Stale sockets
            // accumulate when /tmp is a shared volume across container restarts (issue #108),
            // and on Linux every worker thread also appears at /proc/<tid>, so an explicit
            // PID could resolve to a TID without this guard. Drop silently — debug-only log.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Skipping pid {Pid}: not a live thread-group leader (likely stale diagnostic socket or TID).", pid);
            }
            return null;
        }

        try
        {
            var client = new DiagnosticsClient(pid);
            var snapshot = ProcessInfoReflection.TryGet(client);
            if (snapshot is not null)
            {
                return new DotnetProcess(
                    ProcessId: (int)snapshot.ProcessId,
                    CommandLine: snapshot.CommandLine,
                    OperatingSystem: snapshot.OperatingSystem,
                    ProcessArchitecture: snapshot.ProcessArchitecture,
                    RuntimeVersion: snapshot.ClrProductVersionString,
                    ManagedEntrypointAssemblyName: snapshot.ManagedEntrypointAssemblyName);
            }

            return BuildFallback(pid);
        }
        catch (Exception ex) when (
            ex is ServerNotAvailableException ||
            ex is TimeoutException ||
            ex is UnauthorizedAccessException ||
            ex is IOException)
        {
            LogUnreachable(pid, ex);
            return null;
        }
    }

    private static DotnetProcess? BuildFallback(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DotnetProcess(
                ProcessId: pid,
                CommandLine: SafeReadCommandLine(p),
                OperatingSystem: RuntimeInformation.OSDescription,
                ProcessArchitecture: RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
                RuntimeVersion: string.Empty,
                ManagedEntrypointAssemblyName: p.ProcessName);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string SafeReadCommandLine(Process p)
    {
        try
        {
            return p.MainModule?.FileName ?? p.ProcessName;
        }
        catch (Exception)
        {
            return p.ProcessName;
        }
    }

    private void LogUnreachable(int pid, Exception ex)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(ex, "Process {Pid} does not expose a diagnostic endpoint or is unreachable.", pid);
        }
    }
}
