using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.Security;

/// <summary>
/// Emits a once-per-process <c>Warning</c>-level log the first time a long-running collector
/// is invoked with <c>runAsJob=true</c> instead of the MCP-native progress / cancellation
/// path (Stage A of RFC 0002 §7.3 item 7 / issue #211). The legacy path stays functional —
/// this class only surfaces the deprecation telemetry so operators can see when their
/// clients still depend on <c>get_collection_status</c> / <c>cancel_collection</c>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LegacyDiagnosticsFlagDeprecation"/>'s once-per-process pattern. The
/// flag is removed (and the polling tools deleted) only in Stage B once the supported MCP
/// client matrix is known to handle <c>notifications/progress</c> + <c>notifications/cancelled</c>
/// natively. See <c>docs/tool-reference.md</c> § "MCP-native progress and cancellation"
/// for the cutover plan.
/// </remarks>
public sealed class LegacyRunAsJobDeprecation
{
    /// <summary>Public for assertions; kept verbatim in tests so any wording drift is caught.</summary>
    public const string RunAsJobWarning =
        "collect_cpu_sample runAsJob=true is deprecated. Spec-compliant MCP clients should rely on " +
        "notifications/progress + notifications/cancelled (Stage A of RFC 0002 §7.3 #7 / issue #211). " +
        "get_collection_status and cancel_collection are scheduled for removal in Stage B once the " +
        "supported client matrix no longer needs the polling bridge.";

    private readonly ILogger<LegacyRunAsJobDeprecation> _logger;
    private int _warned;

    public LegacyRunAsJobDeprecation(ILogger<LegacyRunAsJobDeprecation>? logger = null)
    {
        _logger = logger ?? NullLogger<LegacyRunAsJobDeprecation>.Instance;
    }

    /// <summary>
    /// Records that a tool was invoked with <c>runAsJob=true</c>. Emits the warning exactly
    /// once per process; subsequent calls are no-ops.
    /// </summary>
    public void NotifyRunAsJobUse()
    {
        if (Interlocked.Exchange(ref _warned, 1) == 0)
        {
            _logger.LogWarning(RunAsJobWarning);
        }
    }
}
