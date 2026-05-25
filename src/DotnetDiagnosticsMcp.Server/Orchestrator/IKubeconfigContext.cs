using System;
using System.Threading;

namespace DotnetDiagnosticsMcp.Server.Orchestrator;

/// <summary>
/// Ambient (AsyncLocal) propagation of the current kubeconfig handle for the
/// in-flight orchestrator tool call (#234). The orchestrator's existing chain
/// (<see cref="IKubernetesClientFactory"/>, <see cref="IKubernetesPodsApi"/>,
/// <see cref="IPodInventory"/>) routes through a singleton Kubernetes client; we
/// extend it without changing every method signature by letting the tool layer
/// push the handle on entry and pop it on exit.
/// </summary>
/// <remarks>
/// Using AsyncLocal keeps the kubeconfig handle scoped strictly to the call's
/// async task tree — concurrent MCP requests targeting different clusters do not
/// leak handles across each other.
/// </remarks>
public interface IKubeconfigContext
{
    /// <summary>Currently-active handle on this async-local frame, or null when none.</summary>
    string? CurrentHandle { get; }

    /// <summary>
    /// Pushes <paramref name="handle"/> as the active kubeconfig handle for the
    /// duration of the returned <see cref="IDisposable"/>. Nested pushes are
    /// supported (the most recent push wins until disposed).
    /// </summary>
    IDisposable Push(string handle);
}

internal sealed class AsyncLocalKubeconfigContext : IKubeconfigContext
{
    private static readonly AsyncLocal<string?> _current = new();

    public string? CurrentHandle => _current.Value;

    public IDisposable Push(string handle)
    {
        if (string.IsNullOrEmpty(handle))
        {
            throw new ArgumentException("Kubeconfig handle must be a non-empty value.", nameof(handle));
        }
        var previous = _current.Value;
        _current.Value = handle;
        return new Popper(previous);
    }

    private sealed class Popper : IDisposable
    {
        private readonly string? _previous;
        private int _disposed;
        public Popper(string? previous) { _previous = previous; }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _current.Value = _previous;
            }
        }
    }
}
