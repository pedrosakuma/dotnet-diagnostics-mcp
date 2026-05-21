using System.Globalization;

namespace DotnetDiagnosticsMcp.Core.Capabilities;

/// <summary>
/// Static probe that decides whether the four ClrMD-backed live-attach tools
/// (<c>collect_thread_snapshot</c>, <c>inspect_live_heap</c>, <c>inspect_dump</c>
/// against a live PID, <c>collect_process_dump</c>) — plus the opt-in
/// <c>collect_cpu_sample(resolveMethodInstantiations=true)</c> enrichment path — can attach to a peer process
/// on the host the diagnostics MCP server is running on. The check is a property
/// of the sidecar / host, not of the target runtime — same shape as
/// <c>CanSampleOffCpu</c>.
/// </summary>
/// <remarks>
/// <para>
/// On Linux, attach via <c>ptrace(PTRACE_ATTACH, ...)</c> is gated by two
/// orthogonal mechanisms:
/// </para>
/// <list type="number">
/// <item>The Linux capability <c>CAP_SYS_PTRACE</c> (bit 19 of the effective
/// capability bitmap). A process holding it can attach to any same-UID peer
/// regardless of Yama.</item>
/// <item>The Yama LSM tunable <c>kernel.yama.ptrace_scope</c>:
/// <c>0</c> = classic (same-UID peer attach allowed),
/// <c>1</c> = restricted (only descendants of the tracer; the Debian/Ubuntu/WSL
/// default),
/// <c>2</c> = admin-only,
/// <c>3</c> = no attach at all.</item>
/// </list>
/// <para>
/// We say "can attach" when either CAP_SYS_PTRACE is held, OR ptrace_scope=0.
/// Anything else is a failure with a structured reason string the LLM (and the
/// human reading the error) can act on.
/// </para>
/// <para>
/// On Windows ClrMD attaches via <c>DebugActiveProcess</c>; same-UID is normally
/// enough. We optimistically report support and let <c>ClassifyAttachFailure</c>
/// surface the actual error if Windows refuses at runtime.
/// </para>
/// <para>
/// On macOS ClrMD does not support live attach; we report it as unsupported.
/// </para>
/// </remarks>
public static class PtraceProbe
{
    /// <summary>Bit position of <c>CAP_SYS_PTRACE</c> in the Linux capability bitmap.</summary>
    private const int CapSysPtraceBit = 19;

    /// <summary>Path read by the Linux probe to learn the sidecar's effective capabilities.</summary>
    public const string ProcSelfStatusPath = "/proc/self/status";

    /// <summary>Path read by the Linux probe to learn the host's Yama policy.</summary>
    public const string YamaPtraceScopePath = "/proc/sys/kernel/yama/ptrace_scope";

    /// <summary>
    /// Detects whether the four ClrMD-backed live-attach tools (and the opt-in CPU-sample
    /// generic-instantiation enrichment path) can attach to a same-UID peer
    /// process on the host. Cheap (two file reads on Linux, OS check elsewhere) and
    /// safe to call repeatedly.
    /// </summary>
    public static PtraceProbeResult Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return new PtraceProbeResult(CanAttach: true,
                Reason: "Windows: ClrMD attaches via DebugActiveProcess; same-UID peer attach is allowed by default.");
        }

        if (OperatingSystem.IsMacOS())
        {
            return new PtraceProbeResult(CanAttach: false,
                Reason: "macOS: ClrMD does not support live attach. Use the dump-based workflow (collect_process_dump + inspect_dump).");
        }

        if (!OperatingSystem.IsLinux())
        {
            return new PtraceProbeResult(CanAttach: false,
                Reason: "Unsupported OS: ClrMD-backed live attach is not implemented on this platform.");
        }

        return DetectLinux(File.ReadAllText, File.Exists);
    }

    /// <summary>
    /// Pure Linux probe — exposed internally so unit tests can drive it with
    /// in-memory <c>/proc/self/status</c> + <c>ptrace_scope</c> fixtures.
    /// </summary>
    internal static PtraceProbeResult DetectLinux(Func<string, string> readAllText, Func<string, bool> fileExists)
    {
        var hasCapSysPtrace = TryReadCapSysPtrace(readAllText);
        var scopeResult = TryReadPtraceScope(readAllText, fileExists);
        int? scope = scopeResult.HasValue ? scopeResult.Value : null;

        if (scopeResult is { HasValue: true, Value: 3 })
        {
            return new PtraceProbeResult(CanAttach: false,
                Reason: "Linux: kernel.yama.ptrace_scope=3 (no attach permitted). CAP_SYS_PTRACE cannot override this — relax the host sysctl or use the dump-based workflow (collect_process_dump + inspect_dump).")
            {
                HasCapSysPtrace = hasCapSysPtrace,
                PtraceScope = 3,
            };
        }

        if (hasCapSysPtrace)
        {
            return new PtraceProbeResult(CanAttach: true,
                Reason: $"Linux: CAP_SYS_PTRACE held (ptrace_scope={FormatScope(scopeResult)}). Same-UID peer attach allowed unconditionally.")
            {
                HasCapSysPtrace = true,
                PtraceScope = scope,
            };
        }

        return scopeResult switch
        {
            { HasValue: true, Value: 0 } => new PtraceProbeResult(CanAttach: true,
                Reason: "Linux: kernel.yama.ptrace_scope=0; same-UID peer attach allowed without CAP_SYS_PTRACE.")
            {
                HasCapSysPtrace = false,
                PtraceScope = 0,
            },

            { HasValue: true, Value: 1 } => new PtraceProbeResult(CanAttach: false,
                Reason: "Linux: kernel.yama.ptrace_scope=1 (Debian/Ubuntu/WSL/Codespaces default) and sidecar lacks CAP_SYS_PTRACE — same-UID peer attach is blocked. Grant the capability (container: --cap-add SYS_PTRACE / cap_add: [SYS_PTRACE] / capabilities.add: ['SYS_PTRACE']) or relax the host (sudo sysctl -w kernel.yama.ptrace_scope=0).")
            {
                HasCapSysPtrace = false,
                PtraceScope = 1,
            },

            { HasValue: true, Value: 2 } => new PtraceProbeResult(CanAttach: false,
                Reason: "Linux: kernel.yama.ptrace_scope=2 (admin-only). Grant CAP_SYS_PTRACE to the sidecar or relax the host to ptrace_scope=0.")
            {
                HasCapSysPtrace = false,
                PtraceScope = 2,
            },

            { HasValue: true, Value: 3 } => new PtraceProbeResult(CanAttach: false,
                Reason: "Linux: kernel.yama.ptrace_scope=3 (no attach permitted). CAP_SYS_PTRACE cannot override this — relax the host sysctl or use the dump-based workflow (collect_process_dump + inspect_dump).")
            {
                HasCapSysPtrace = false,
                PtraceScope = 3,
            },

            { HasValue: true } => new PtraceProbeResult(CanAttach: false,
                Reason: $"Linux: kernel.yama.ptrace_scope={scopeResult.Value} (unknown value) and sidecar lacks CAP_SYS_PTRACE — assuming attach is blocked. Grant the capability or set ptrace_scope=0.")
            {
                HasCapSysPtrace = false,
                PtraceScope = scopeResult.Value,
            },

            // No Yama tunable (kernel built without Yama LSM). Without Yama the classic
            // same-UID rule applies, so unprivileged attach is allowed.
            _ => new PtraceProbeResult(CanAttach: true,
                Reason: "Linux: Yama LSM not enabled; classic same-UID ptrace attach is allowed without CAP_SYS_PTRACE.")
            {
                HasCapSysPtrace = false,
                PtraceScope = null,
            },
        };
    }

    private static bool TryReadCapSysPtrace(Func<string, string> readAllText)
    {
        try
        {
            var status = readAllText(ProcSelfStatusPath);
            foreach (var line in status.Split('\n'))
            {
                // CapEff: 00000000a80c25fb (bit 19 set → CAP_SYS_PTRACE held)
                if (!line.StartsWith("CapEff:", StringComparison.Ordinal)) continue;
                var hex = line["CapEff:".Length..].Trim();
                if (!ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask))
                {
                    return false;
                }
                return ((mask >> CapSysPtraceBit) & 1UL) == 1UL;
            }
        }
        catch
        {
            // best-effort probe
        }

        return false;
    }

    private static (bool HasValue, int Value) TryReadPtraceScope(Func<string, string> readAllText, Func<string, bool> fileExists)
    {
        try
        {
            if (!fileExists(YamaPtraceScopePath)) return (false, 0);
            var raw = readAllText(YamaPtraceScopePath).Trim();
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return (true, value);
            }
        }
        catch
        {
            // best-effort probe
        }

        return (false, 0);
    }

    private static string FormatScope((bool HasValue, int Value) scope)
        => scope.HasValue ? scope.Value.ToString(CultureInfo.InvariantCulture) : "<unknown>";
}

/// <summary>Outcome of <see cref="PtraceProbe.Detect"/>.</summary>
/// <param name="CanAttach">Whether the four ClrMD-backed tools are expected to succeed
/// when called against a same-UID peer process on this host.</param>
/// <param name="Reason">Short human-readable reason — surfaced verbatim in
/// <c>DiagnosticCapabilities.Notes</c> and in the structured PermissionDenied envelope
/// when a ClrMD attach fails. Never null.</param>
public sealed record PtraceProbeResult(bool CanAttach, string Reason)
{
    /// <summary>True when the sidecar currently holds <c>CAP_SYS_PTRACE</c>. False on
    /// non-Linux hosts and on Linux when the capability is absent.</summary>
    public bool HasCapSysPtrace { get; init; }

    /// <summary>Value of <c>kernel.yama.ptrace_scope</c> when readable; null when Yama is not
    /// enabled or on non-Linux hosts.</summary>
    public int? PtraceScope { get; init; }
}
