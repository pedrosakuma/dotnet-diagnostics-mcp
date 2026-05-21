using DotnetDiagnosticsMcp.Core.Drilldown;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Jobs;

/// <summary>
/// Legacy compatibility layer for long-running collectors. MCP Tasks are now
/// available via the SDK-backed <c>IMcpTaskStore</c>, but existing clients still use
/// <c>runAsJob=true</c> plus <c>get_collection_status</c>/<c>cancel_collection</c>.
/// This runner preserves that older polling flow without changing the underlying
/// collector contracts.
/// </summary>
public interface ICollectionJobRunner
{
    /// <summary>
    /// Schedules <paramref name="work"/> on a background task. Returns immediately with
    /// the registered handle. When the work completes, the resulting
    /// <see cref="DiagnosticResult{T}"/> is captured on the job for later retrieval.
    /// </summary>
    /// <param name="processId">PID being inspected; used so handles get invalidated when the target dies.</param>
    /// <param name="kind">Short discriminator for the collection (e.g. <c>cpu-sample-job</c>).</param>
    /// <param name="ttl">How long the completed job entry sticks around for the LLM to poll.</param>
    /// <param name="work">Async collection work. Must observe the supplied <see cref="CancellationToken"/>.</param>
    DiagnosticHandle Start<T>(
        int processId,
        string kind,
        TimeSpan ttl,
        Func<CancellationToken, Task<DiagnosticResult<T>>> work);

    /// <summary>Signals the background work to cancel. Returns false when the handle is unknown.</summary>
    bool Cancel(string handle);
}

/// <summary>
/// Default <see cref="ICollectionJobRunner"/>. Persists each job inside the shared
/// <see cref="IDiagnosticHandleStore"/> so we get TTL eviction, per-process invalidation
/// and capacity enforcement for free. The background work is scheduled with
/// <see cref="Task.Run(Func{Task},CancellationToken)"/> against the supplied
/// <see cref="TimeProvider"/> clock.
/// </summary>
public sealed class CollectionJobRunner : ICollectionJobRunner
{
    private readonly IDiagnosticHandleStore _store;
    private readonly ILogger<CollectionJobRunner> _logger;
    private readonly TimeProvider _clock;

    public CollectionJobRunner(
        IDiagnosticHandleStore store,
        ILogger<CollectionJobRunner>? logger = null,
        TimeProvider? clock = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? NullLogger<CollectionJobRunner>.Instance;
        _clock = clock ?? TimeProvider.System;
    }

    public DiagnosticHandle Start<T>(
        int processId,
        string kind,
        TimeSpan ttl,
        Func<CancellationToken, Task<DiagnosticResult<T>>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        var cts = new CancellationTokenSource();
        var job = new CollectionJob(kind, processId, _clock.GetUtcNow(), cts);
        var handle = _store.Register(processId, kind, job, ttl);
        job.AttachHandle(handle.Id);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(cts.Token).ConfigureAwait(false);
                job.MarkCompleted(result, _clock.GetUtcNow());
                _logger.LogDebug("Collection job {Handle} ({Kind}) completed.", handle.Id, kind);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                job.MarkCanceled(_clock.GetUtcNow());
                _logger.LogDebug("Collection job {Handle} ({Kind}) canceled.", handle.Id, kind);
            }
            catch (Exception ex)
            {
                job.MarkFailed(ex, _clock.GetUtcNow());
                _logger.LogWarning(ex, "Collection job {Handle} ({Kind}) failed.", handle.Id, kind);
            }
        }, CancellationToken.None);

        return handle;
    }

    public bool Cancel(string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        var job = _store.TryGet<CollectionJob>(handle);
        if (job is null)
        {
            return false;
        }

        job.RequestCancel();
        return true;
    }
}
