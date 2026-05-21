using System.Globalization;
using DotnetDiagnosticsMcp.Core.CpuSampling;

namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Cheap host-side probe for the Linux perf environment used by kernel-signal tooling.
/// Separates "perf is installed" from "the sidecar can actually trace sched_switch" so
/// <c>get_diagnostic_capabilities</c> can report both facts before the LLM spends a round-trip
/// discovering them via failure.
/// </summary>
public static class PerfHostProbe
{
    private const int CapSysAdminBit = 21;
    private const int CapPerfmonBit = 38;

    public const string ProcSelfStatusPath = "/proc/self/status";
    public const string PerfEventParanoidPath = "/proc/sys/kernel/perf_event_paranoid";

    public static PerfHostProbeResult Detect()
    {
        if (!OperatingSystem.IsLinux())
        {
            return new PerfHostProbeResult(
                PerfInstalled: false,
                HasCapPerfmon: false,
                HasCapSysAdmin: false,
                PerfEventParanoid: null,
                CanTraceSchedSwitch: false);
        }

        return DetectLinux(
            File.ReadAllText,
            File.Exists,
            () => PerfBinaryResolver.Resolve(
                "perf",
                PerfBinaryResolver.EnumerateDefaultLinuxToolsCandidates,
                PerfBinaryResolver.ProbePerfVersion));
    }

    internal static PerfHostProbeResult DetectLinux(
        Func<string, string> readAllText,
        Func<string, bool> fileExists,
        Func<string?> resolvePerfPath)
    {
        var capEff = TryReadCapEff(readAllText);
        var hasCapPerfmon = capEff is { } mask && HasCapability(mask, CapPerfmonBit);
        var hasCapSysAdmin = capEff is { } mask2 && HasCapability(mask2, CapSysAdminBit);
        var perfEventParanoid = TryReadPerfEventParanoid(readAllText, fileExists);
        var perfInstalled = resolvePerfPath() is not null;
        var canTraceSchedSwitch = perfInstalled && (hasCapPerfmon || hasCapSysAdmin || perfEventParanoid is <= -1);

        return new PerfHostProbeResult(
            PerfInstalled: perfInstalled,
            HasCapPerfmon: hasCapPerfmon,
            HasCapSysAdmin: hasCapSysAdmin,
            PerfEventParanoid: perfEventParanoid,
            CanTraceSchedSwitch: canTraceSchedSwitch);
    }

    private static ulong? TryReadCapEff(Func<string, string> readAllText)
    {
        try
        {
            var status = readAllText(ProcSelfStatusPath);
            foreach (var line in status.Split('\n'))
            {
                if (!line.StartsWith("CapEff:", StringComparison.Ordinal))
                {
                    continue;
                }

                var hex = line["CapEff:".Length..].Trim();
                if (ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask))
                {
                    return mask;
                }

                return null;
            }
        }
        catch
        {
            // best-effort probe
        }

        return null;
    }

    private static int? TryReadPerfEventParanoid(Func<string, string> readAllText, Func<string, bool> fileExists)
    {
        try
        {
            if (!fileExists(PerfEventParanoidPath))
            {
                return null;
            }

            var raw = readAllText(PerfEventParanoidPath).Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        catch
        {
            // best-effort probe
        }

        return null;
    }

    private static bool HasCapability(ulong mask, int bit) => ((mask >> bit) & 1UL) == 1UL;
}

public sealed record PerfHostProbeResult(
    bool PerfInstalled,
    bool HasCapPerfmon,
    bool HasCapSysAdmin,
    int? PerfEventParanoid,
    bool CanTraceSchedSwitch);
