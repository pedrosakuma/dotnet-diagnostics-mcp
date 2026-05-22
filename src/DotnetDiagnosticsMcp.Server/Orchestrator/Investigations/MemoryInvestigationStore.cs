using System;
using System.Collections.Generic;
using System.Linq;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Default in-process implementation of <see cref="IInvestigationStore"/>. Thread-safe
/// via a single lock — handle counts are bounded by orchestrator concurrency in
/// practice, so contention isn't a concern.
/// </summary>
internal sealed class MemoryInvestigationStore : IInvestigationStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, InvestigationHandle> _byId = new(StringComparer.Ordinal);

    public void Add(InvestigationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (_byId.ContainsKey(handle.HandleId))
            {
                throw new InvalidOperationException(
                    $"Investigation handle '{handle.HandleId}' is already registered.");
            }
            _byId[handle.HandleId] = handle;
        }
    }

    public bool TryReserveTarget(InvestigationHandle newHandle, bool allowReuse, out InvestigationHandle? existing)
    {
        ArgumentNullException.ThrowIfNull(newHandle);
        lock (_gate)
        {
            if (allowReuse)
            {
                foreach (var h in _byId.Values)
                {
                    if (h.State is InvestigationState.Active or InvestigationState.Attaching &&
                        string.Equals(h.Namespace, newHandle.Namespace, StringComparison.Ordinal) &&
                        string.Equals(h.PodName, newHandle.PodName, StringComparison.Ordinal) &&
                        string.Equals(h.TargetContainerName, newHandle.TargetContainerName, StringComparison.Ordinal))
                    {
                        existing = h;
                        return false;
                    }
                }
            }
            if (_byId.ContainsKey(newHandle.HandleId))
            {
                throw new InvalidOperationException(
                    $"Investigation handle '{newHandle.HandleId}' is already registered.");
            }
            _byId[newHandle.HandleId] = newHandle;
            existing = null;
            return true;
        }
    }

    public void Update(InvestigationHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (!_byId.ContainsKey(handle.HandleId))
            {
                throw new InvalidOperationException(
                    $"Investigation handle '{handle.HandleId}' is not registered.");
            }
            _byId[handle.HandleId] = handle;
        }
    }

    public InvestigationHandle? GetById(string handleId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(handleId, out var h) ? h : null;
        }
    }

    public InvestigationTerminalTransition TryTransitionToTerminal(
        string handleId,
        InvestigationState targetState,
        string? failureReason,
        out InvestigationState? previousState)
    {
        previousState = null;
        if (targetState is not (InvestigationState.Closed or InvestigationState.Expired or InvestigationState.Failed))
        {
            throw new ArgumentException(
                $"TryTransitionToTerminal requires a terminal target state; got {targetState}.",
                nameof(targetState));
        }

        lock (_gate)
        {
            if (!_byId.TryGetValue(handleId, out var current))
            {
                return InvestigationTerminalTransition.NotFound;
            }
            previousState = current.State;
            if (current.State is InvestigationState.Closed
                or InvestigationState.Expired
                or InvestigationState.Failed)
            {
                return InvestigationTerminalTransition.AlreadyTerminal;
            }
            var updated = current with
            {
                State = targetState,
                FailureReason = targetState == InvestigationState.Closed
                    ? current.FailureReason
                    : failureReason ?? current.FailureReason,
            };
            _byId[handleId] = updated;
            return InvestigationTerminalTransition.Transitioned;
        }
    }

    public InvestigationHandle? FindReusableTarget(string podNamespace, string podName, string containerName)
    {
        lock (_gate)
        {
            foreach (var h in _byId.Values)
            {
                if (h.State is InvestigationState.Active or InvestigationState.Attaching &&
                    string.Equals(h.Namespace, podNamespace, StringComparison.Ordinal) &&
                    string.Equals(h.PodName, podName, StringComparison.Ordinal) &&
                    string.Equals(h.TargetContainerName, containerName, StringComparison.Ordinal))
                {
                    return h;
                }
            }
            return null;
        }
    }

    public IReadOnlyCollection<InvestigationHandle> Snapshot()
    {
        lock (_gate)
        {
            return _byId.Values.ToArray();
        }
    }
}
