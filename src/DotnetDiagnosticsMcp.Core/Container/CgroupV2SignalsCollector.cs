using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Container;

/// <summary>
/// Reads cgroup v2 unified-hierarchy files to compose a <see cref="ContainerSignals"/> snapshot.
/// Pure file-system reads — no diagnostic IPC, no privileged operations. The default constructor
/// uses real <c>/sys/fs/cgroup</c> and <c>/proc</c>; tests pass alternative roots.
/// </summary>
/// <remarks>
/// Strategy: read <c>/proc/{pid}/cgroup</c> to discover the unified path (line prefix <c>0::</c>).
/// All subsequent files are resolved as <c>{cgroupRoot}{path}/{file}</c>. Each file is best-effort:
/// a missing or unreadable file becomes a <c>Notes</c> entry, never a hard failure — old kernels
/// lacking PSI, bare-metal hosts without a memory limit, restricted containers without read
/// access to <c>memory.events</c> are all common and the LLM should still get partial signals.
/// </remarks>
public sealed class CgroupV2SignalsCollector : IContainerSignalsCollector
{
    private readonly string _cgroupRoot;
    private readonly string _procRoot;
    private readonly string _rootFs;
    private readonly ILogger<CgroupV2SignalsCollector> _logger;
    private readonly TimeProvider _clock;

    public CgroupV2SignalsCollector(
        ILogger<CgroupV2SignalsCollector>? logger = null,
        TimeProvider? clock = null,
        string cgroupRoot = "/sys/fs/cgroup",
        string procRoot = "/proc",
        string rootFs = "/")
    {
        _logger = logger ?? NullLogger<CgroupV2SignalsCollector>.Instance;
        _clock = clock ?? TimeProvider.System;
        _cgroupRoot = cgroupRoot;
        _procRoot = procRoot;
        _rootFs = rootFs;
    }

    public Task<ContainerSignals> CollectAsync(int processId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Collect(processId));
    }

    private ContainerSignals Collect(int processId)
    {
        var notes = new List<string>();
        var collectedAt = _clock.GetUtcNow();

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            notes.Add("Container signals are Linux-only. Windows job-object metrics are not implemented yet.");
            return new ContainerSignals(processId, collectedAt, false, CgroupVersion.None, null,
                null, null, null, null, null, notes);
        }

        var version = DetectCgroupVersion(notes);
        if (version != CgroupVersion.V2)
        {
            notes.Add(version == CgroupVersion.V1
                ? "Host is on cgroup v1 (legacy). Only cgroup v2 is parsed today; v1 throttle metrics live in /sys/fs/cgroup/cpu,cpuacct/cpu.stat — file a request if you need it."
                : "Could not detect any cgroup hierarchy at " + _cgroupRoot + ".");
            return new ContainerSignals(processId, collectedAt, false, version, null,
                null, null, null, null, null, notes);
        }

        var cgroupPath = TryReadCgroupPath(processId, notes);
        var dir = string.IsNullOrEmpty(cgroupPath) ? _cgroupRoot : _cgroupRoot + cgroupPath;

        var cpu = ReadCpu(dir, notes);
        var memory = ReadMemory(dir, notes);
        var pressure = ReadPressure(dir, notes);
        var pids = ReadPids(dir, notes);
        var oom = ReadOomScore(processId, notes);

        // Container detection is fuzzy on cgroup v2: with the default Docker --cgroupns=private
        // (and most K8s setups), /proc/<pid>/cgroup reports "0::/" because the process sees its
        // own cgroup namespace root. Falling back to filesystem markers and "limits are set"
        // heuristics catches those cases. See cgroup_namespaces(7).
        var pathLooksContainerised = !string.IsNullOrEmpty(cgroupPath) && cgroupPath != "/";
        var hasContainerMarker = HasContainerMarker();
        var hasLimits = (cpu?.QuotaCores is > 0) || (memory?.MaxBytes is > 0);
        var inContainer = pathLooksContainerised || hasContainerMarker || hasLimits;
        if (!pathLooksContainerised && inContainer)
        {
            notes.Add("cgroup path is \"/\" (private cgroup namespace likely); inferred container from "
                + (hasContainerMarker ? "/.dockerenv or /run/.containerenv marker" : "active cpu/memory limits") + ".");
        }

        return new ContainerSignals(processId, collectedAt, inContainer, version, cgroupPath,
            cpu, memory, pressure, pids, oom, notes);
    }

    private bool HasContainerMarker()
    {
        try
        {
            if (File.Exists(Path.Combine(_rootFs, ".dockerenv"))) return true;
            if (File.Exists(Path.Combine(_rootFs, "run", ".containerenv"))) return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Best-effort; absence is not proof of non-container.
        }
        return false;
    }

    private CgroupVersion DetectCgroupVersion(List<string> notes)
    {
        // cgroup v2 hosts expose a magic file at the mount root.
        if (File.Exists(Path.Combine(_cgroupRoot, "cgroup.controllers")))
        {
            return CgroupVersion.V2;
        }

        if (Directory.Exists(Path.Combine(_cgroupRoot, "cpu,cpuacct")) ||
            Directory.Exists(Path.Combine(_cgroupRoot, "memory")))
        {
            return CgroupVersion.V1;
        }

        return CgroupVersion.None;
    }

    private string? TryReadCgroupPath(int processId, List<string> notes)
    {
        var procCgroup = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture), "cgroup");
        try
        {
            foreach (var line in File.ReadAllLines(procCgroup))
            {
                // Unified hierarchy line is "0::<path>".
                if (line.StartsWith("0::", StringComparison.Ordinal))
                {
                    return line[3..];
                }
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {procCgroup}: {ex.GetType().Name}. Falling back to host-level cgroup root.");
        }

        return null;
    }

    private static ContainerCpuSignals? ReadCpu(string dir, List<string> notes)
    {
        var statPath = Path.Combine(dir, "cpu.stat");
        var stat = ReadKeyedMap(statPath, notes);
        if (stat is null) return null;

        long usage = stat.GetValueOrDefault("usage_usec");
        long nrPeriods = stat.GetValueOrDefault("nr_periods");
        long nrThrottled = stat.GetValueOrDefault("nr_throttled");
        long throttledUsec = stat.GetValueOrDefault("throttled_usec");

        double? throttlePercent = nrPeriods > 0
            ? (double)nrThrottled / nrPeriods * 100.0
            : null;

        double? quotaCores = null;
        var cpuMaxPath = Path.Combine(dir, "cpu.max");
        try
        {
            var text = File.ReadAllText(cpuMaxPath).Trim();
            // Format: "<quota_us> <period_us>"  or "max <period_us>".
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[0] != "max"
                && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var quota)
                && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var period)
                && period > 0)
            {
                quotaCores = (double)quota / period;
            }
            // "max" → unlimited → quotaCores stays null and throttlePercent loses meaning, but
            // we keep it because nr_periods may still be non-zero on hybrid configurations.
            if (parts.Length >= 1 && parts[0] == "max")
            {
                throttlePercent = null;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {cpuMaxPath}: {ex.GetType().Name}. QuotaCores reported as null.");
        }

        return new ContainerCpuSignals(usage, nrPeriods, nrThrottled, throttledUsec, throttlePercent, quotaCores);
    }

    private static ContainerMemorySignals? ReadMemory(string dir, List<string> notes)
    {
        long? current = ReadLong(Path.Combine(dir, "memory.current"), notes);
        if (current is null) return null;

        long? max = ReadLongOrMax(Path.Combine(dir, "memory.max"), notes);
        long? high = ReadLongOrMax(Path.Combine(dir, "memory.high"), notes);

        double? fraction = max is > 0 ? (double)current.Value / max.Value : null;

        long oomKill = 0;
        long maxHit = 0;
        var events = ReadKeyedMap(Path.Combine(dir, "memory.events"), notes);
        if (events is not null)
        {
            oomKill = events.GetValueOrDefault("oom_kill");
            maxHit = events.GetValueOrDefault("max");
        }

        return new ContainerMemorySignals(current.Value, max, high, fraction, oomKill, maxHit);
    }

    private static ContainerPressureSignals? ReadPressure(string dir, List<string> notes)
    {
        var cpu = ReadPsi(Path.Combine(dir, "cpu.pressure"), notes);
        var mem = ReadPsi(Path.Combine(dir, "memory.pressure"), notes);
        var io = ReadPsi(Path.Combine(dir, "io.pressure"), notes);

        if (cpu is null && mem is null && io is null)
        {
            return null;
        }

        return new ContainerPressureSignals(
            CpuSomeAvg10: cpu?.SomeAvg10,
            MemSomeAvg10: mem?.SomeAvg10,
            MemFullAvg10: mem?.FullAvg10,
            IoSomeAvg10: io?.SomeAvg10,
            IoFullAvg10: io?.FullAvg10);
    }

    private static ContainerPidsSignals? ReadPids(string dir, List<string> notes)
    {
        long? current = ReadLong(Path.Combine(dir, "pids.current"), notes);
        if (current is null) return null;
        long? max = ReadLongOrMax(Path.Combine(dir, "pids.max"), notes);
        return new ContainerPidsSignals(current.Value, max);
    }

    private int? ReadOomScore(int processId, List<string> notes)
    {
        var path = Path.Combine(_procRoot, processId.ToString(CultureInfo.InvariantCulture), "oom_score");
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            {
                return v;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}.");
        }
        return null;
    }

    private static Dictionary<string, long>? ReadKeyedMap(string path, List<string> notes)
    {
        try
        {
            var map = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                {
                    map[parts[0]] = v;
                }
            }
            return map;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}.");
            return null;
        }
    }

    private static long? ReadLong(string path, List<string> notes)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}.");
        }
        return null;
    }

    private static long? ReadLongOrMax(string path, List<string> notes)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (text == "max") return null;
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return v;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}.");
        }
        return null;
    }

    private readonly record struct PsiLine(double? SomeAvg10, double? FullAvg10);

    private static PsiLine? ReadPsi(string path, List<string> notes)
    {
        try
        {
            double? some = null, full = null;
            foreach (var line in File.ReadAllLines(path))
            {
                // Format: "some avg10=0.00 avg60=0.00 avg300=0.00 total=12345"
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var kind = parts[0];
                foreach (var kv in parts.Skip(1))
                {
                    var eq = kv.IndexOf('=');
                    if (eq <= 0) continue;
                    if (kv.AsSpan(0, eq).SequenceEqual("avg10"))
                    {
                        if (double.TryParse(kv.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        {
                            if (kind == "some") some = v;
                            else if (kind == "full") full = v;
                        }
                    }
                }
            }
            if (some is null && full is null) return null;
            return new PsiLine(some, full);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or IOException)
        {
            notes.Add($"Could not read {path}: {ex.GetType().Name}. PSI requires kernel >= 4.20 and CONFIG_PSI=y.");
            return null;
        }
    }
}
