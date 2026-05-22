using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Shared cleanup pipeline invoked by <c>detach_from_pod</c> (caller-initiated close)
/// and the TTL reaper (server-initiated eviction). Centralises the order so both
/// paths flip the handle into a terminal state, dispose the cached MCP client, close
/// the port-forward transport, and unbind every MCP session pointed at the handle —
/// in that order — exactly once per handle.
/// </summary>
/// <remarks>
/// <para>
/// Order matters: tearing the proxy MCP client down first lets the SDK send its
/// graceful <c>shutdown</c> over the port-forward before the transport is yanked;
/// closing the transport second collapses any half-open streams; unbinding sessions
/// last guarantees that an in-flight call which observed the binding still has a
/// proxy client to fail through (and the failure is structured rather than a NRE).
/// </para>
/// <para>
/// Ephemeral containers cannot be removed once added (a Kubernetes constraint, see
/// <see cref="InvestigationHandle"/>): close is therefore "stop port-forward + tag
/// terminal", not "delete the diagnostics container". Operators auditing a Pod's
/// <c>ephemeralContainerStatuses</c> after detach will still see the entry.
/// </para>
/// </remarks>
public sealed class InvestigationCloser
{
    private readonly IInvestigationStore _store;
    private readonly IInvestigationProxyClient _proxyClient;
    private readonly IPortForwardManager _portForwardManager;
    private readonly IInvestigationSessionBinder _sessionBinder;
    private readonly ILogger<InvestigationCloser> _logger;

    public InvestigationCloser(
        IInvestigationStore store,
        IInvestigationProxyClient proxyClient,
        IPortForwardManager portForwardManager,
        IInvestigationSessionBinder sessionBinder,
        ILogger<InvestigationCloser>? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _proxyClient = proxyClient ?? throw new ArgumentNullException(nameof(proxyClient));
        _portForwardManager = portForwardManager ?? throw new ArgumentNullException(nameof(portForwardManager));
        _sessionBinder = sessionBinder ?? throw new ArgumentNullException(nameof(sessionBinder));
        _logger = logger ?? NullLogger<InvestigationCloser>.Instance;
    }

    /// <summary>
    /// Closes an investigation. Returns the outcome so the caller (tool / reaper) can
    /// project it into the appropriate response shape.
    /// </summary>
    /// <param name="handleId">Handle id to close. May reference an unknown handle.</param>
    /// <param name="targetState">Terminal state to transition into — <see cref="InvestigationState.Closed"/>
    /// for caller-initiated detach, <see cref="InvestigationState.Expired"/> for TTL eviction,
    /// <see cref="InvestigationState.Failed"/> for attach-failure cleanup.</param>
    /// <param name="failureReason">Optional reason carried onto the handle when transitioning
    /// to <see cref="InvestigationState.Failed"/> or <see cref="InvestigationState.Expired"/>.
    /// Ignored for <see cref="InvestigationState.Closed"/>.</param>
    public async Task<InvestigationCloseOutcome> CloseAsync(
        string handleId,
        InvestigationState targetState,
        string? failureReason = null)
    {
        if (string.IsNullOrEmpty(handleId))
        {
            return new InvestigationCloseOutcome(
                HandleId: handleId ?? string.Empty,
                Found: false,
                AlreadyTerminal: false,
                PreviousState: null,
                NewState: null,
                UnboundSessionIds: Array.Empty<string>());
        }

        var transition = _store.TryTransitionToTerminal(
            handleId,
            targetState,
            failureReason,
            out var previousState);

        if (transition == InvestigationTerminalTransition.NotFound)
        {
            return new InvestigationCloseOutcome(
                HandleId: handleId,
                Found: false,
                AlreadyTerminal: false,
                PreviousState: null,
                NewState: null,
                UnboundSessionIds: Array.Empty<string>());
        }

        // For AlreadyTerminal we still drain the cleanup pipeline idempotently — a
        // partial prior close (process restart, exception mid-pipeline, racing closer
        // that lost) may have left a port-forward or session binding behind.
        await SafeDisposeProxyAsync(handleId).ConfigureAwait(false);
        await SafeClosePortForwardAsync(handleId).ConfigureAwait(false);
        var unbound = _sessionBinder.UnbindAllForHandle(handleId);

        var alreadyTerminal = transition == InvestigationTerminalTransition.AlreadyTerminal;
        return new InvestigationCloseOutcome(
            HandleId: handleId,
            Found: true,
            AlreadyTerminal: alreadyTerminal,
            PreviousState: previousState,
            NewState: alreadyTerminal ? previousState : targetState,
            UnboundSessionIds: unbound);
    }

    private async Task SafeDisposeProxyAsync(string handleId)
    {
        try
        {
            await _proxyClient.DisposeForHandleAsync(handleId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing proxy MCP client for handle {HandleId} threw; continuing close pipeline.", handleId);
        }
    }

    private async Task SafeClosePortForwardAsync(string handleId)
    {
        try
        {
            await _portForwardManager.CloseAsync(handleId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Closing port-forward for handle {HandleId} threw; continuing close pipeline.", handleId);
        }
    }
}

/// <summary>
/// Result of <see cref="InvestigationCloser.CloseAsync"/>. <see cref="Found"/> is false
/// when the handle id was never registered; <see cref="AlreadyTerminal"/> is true when
/// the handle existed but was already Closed/Expired/Failed before this call.
/// </summary>
public sealed record InvestigationCloseOutcome(
    string HandleId,
    bool Found,
    bool AlreadyTerminal,
    InvestigationState? PreviousState,
    InvestigationState? NewState,
    IReadOnlyCollection<string> UnboundSessionIds);
