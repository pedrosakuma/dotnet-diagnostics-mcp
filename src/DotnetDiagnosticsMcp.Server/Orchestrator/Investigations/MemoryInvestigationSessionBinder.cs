using System;
using System.Collections.Generic;
using System.Linq;

namespace DotnetDiagnosticsMcp.Server.Orchestrator.Investigations;

/// <summary>
/// Default in-process implementation of <see cref="IInvestigationSessionBinder"/>.
/// Thread-safe via a single lock — orchestrator concurrency keeps the binding map
/// tiny (one entry per active MCP session), so contention is not a concern.
/// </summary>
internal sealed class MemoryInvestigationSessionBinder : IInvestigationSessionBinder
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _bySessionId = new(StringComparer.Ordinal);

    public string? TryGetHandleId(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            return _bySessionId.TryGetValue(sessionId, out var handleId) ? handleId : null;
        }
    }

    public void Bind(string sessionId, string handleId)
    {
        if (string.IsNullOrEmpty(sessionId)) throw new ArgumentException("sessionId must be non-empty.", nameof(sessionId));
        if (string.IsNullOrEmpty(handleId)) throw new ArgumentException("handleId must be non-empty.", nameof(handleId));
        lock (_gate)
        {
            _bySessionId[sessionId] = handleId;
        }
    }

    public string? Unbind(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_gate)
        {
            if (_bySessionId.Remove(sessionId, out var handleId))
            {
                return handleId;
            }
            return null;
        }
    }

    public IReadOnlyCollection<string> UnbindAllForHandle(string handleId)
    {
        if (string.IsNullOrEmpty(handleId)) return Array.Empty<string>();
        lock (_gate)
        {
            var matches = _bySessionId
                .Where(kvp => string.Equals(kvp.Value, handleId, StringComparison.Ordinal))
                .Select(kvp => kvp.Key)
                .ToArray();
            foreach (var sessionId in matches)
            {
                _bySessionId.Remove(sessionId);
            }
            return matches;
        }
    }

    public IReadOnlyCollection<KeyValuePair<string, string>> Snapshot()
    {
        lock (_gate)
        {
            return _bySessionId.ToArray();
        }
    }
}
