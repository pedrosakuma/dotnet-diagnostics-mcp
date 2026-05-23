using System;
using System.Threading;
using System.Threading.Tasks;
using DotnetDiagnosticsMcp.Server.Observability;
using DotnetDiagnosticsMcp.Server.Orchestrator;
using DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// TTL reaper for investigation handles produced by <c>attach_to_pod</c>. Periodically
/// scans <see cref="IInvestigationStore.Snapshot"/> and routes every non-terminal handle
/// whose <see cref="InvestigationHandle.ExpiresAt"/> is in the past through the shared
/// <see cref="InvestigationCloser"/>, transitioning it to <see cref="InvestigationState.Expired"/>.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="HandleEvictionBackgroundService"/>, which prunes drill-down
/// handles (CPU samples, dump inspections, …) when their target <em>process</em> exits.
/// This service is keyed off the orchestrator's wall-clock TTL — the target Pod is
/// reachable for the entire window, the LLM just hit its budget.
/// </para>
/// <para>
/// Stuck-in-Attaching handles also get reaped here: if the readiness wait crashed or
/// the orchestrator was restarted mid-attach, the resulting <see cref="InvestigationState.Attaching"/>
/// row would otherwise pin its target tuple forever via
/// <see cref="IInvestigationStore.FindReusableTarget"/>. Expiring it frees the slot.
/// </para>
/// </remarks>
public sealed class InvestigationHandleReaperBackgroundService : BackgroundService
{
    private readonly IInvestigationStore _store;
    private readonly InvestigationCloser _closer;
    private readonly OrchestratorObservability _observability;
    private readonly ILogger<InvestigationHandleReaperBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public InvestigationHandleReaperBackgroundService(
        IInvestigationStore store,
        InvestigationCloser closer,
        OrchestratorObservability observability,
        ILogger<InvestigationHandleReaperBackgroundService>? logger = null,
        TimeSpan? interval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _closer = closer ?? throw new ArgumentNullException(nameof(closer));
        _observability = observability ?? throw new ArgumentNullException(nameof(observability));
        _logger = logger ?? NullLogger<InvestigationHandleReaperBackgroundService>.Instance;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ReapExpiredAsync(DateTimeOffset.UtcNow).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Investigation reaper sweep failed; will retry on the next tick.");
            }
        }
    }

    /// <summary>
    /// Visible for tests: walks the store snapshot and closes every non-terminal handle
    /// whose ExpiresAt is at or before <paramref name="now"/>. Returns the number of
    /// handles transitioned to <see cref="InvestigationState.Expired"/> on this sweep.
    /// </summary>
    public async Task<int> ReapExpiredAsync(DateTimeOffset now)
    {
        var reaped = 0;
        foreach (var handle in _store.Snapshot())
        {
            if (!IsReapable(handle.State)) continue;
            if (handle.ExpiresAt > now) continue;

            var outcome = await _closer.CloseAsync(
                handle.HandleId,
                InvestigationState.Expired,
                failureReason: $"TTL expired at {handle.ExpiresAt:O} (reaper observed at {now:O}).")
                .ConfigureAwait(false);

            if (outcome.Found && !outcome.AlreadyTerminal)
            {
                reaped++;
                _observability.RecordReaperEviction("ttl");
                if (outcome.CleanupErrorCount > 0)
                {
                    _observability.RecordReaperEviction("cleanup-error");
                }
                _observability.RecordDetach(principal: null, handle.HandleId, "ttl", "success");
                _logger.LogInformation(
                    "Expired investigation {HandleId} ({Namespace}/{Pod} container={Container}); unbound {SessionCount} session(s).",
                    handle.HandleId, handle.Namespace, handle.PodName, handle.TargetContainerName,
                    outcome.UnboundSessionIds.Count);
            }
        }
        return reaped;
    }

    private static bool IsReapable(InvestigationState state)
        => state is InvestigationState.Active or InvestigationState.Attaching;
}
