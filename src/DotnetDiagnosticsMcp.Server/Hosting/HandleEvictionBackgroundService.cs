using System.Diagnostics;
using DotnetDiagnosticsMcp.Core.Drilldown;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// Background loop that prunes drill-down handles whose target processes have exited. Keeps the
/// in-memory store from leaking artifacts when the LLM forgets to clean up, and avoids handing
/// the model a handle whose process is gone (it would otherwise time out only on TTL).
/// </summary>
public sealed class HandleEvictionBackgroundService : BackgroundService
{
    private readonly IDiagnosticHandleStore _store;
    private readonly ILogger<HandleEvictionBackgroundService> _logger;
    private readonly TimeSpan _interval;

    public HandleEvictionBackgroundService(
        IDiagnosticHandleStore store,
        ILogger<HandleEvictionBackgroundService>? logger = null,
        TimeSpan? interval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HandleEvictionBackgroundService>.Instance;
        _interval = interval ?? TimeSpan.FromSeconds(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                EvictDeadProcesses();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Handle eviction sweep failed; will retry on the next tick.");
            }
        }
    }

    public int EvictDeadProcesses()
    {
        if (_store is not MemoryDiagnosticHandleStore memoryStore)
        {
            return 0;
        }

        var pids = memoryStore.RegisteredProcessIds();
        var removed = 0;
        foreach (var pid in pids)
        {
            if (IsAlive(pid)) continue;
            var dropped = _store.InvalidateForProcess(pid);
            if (dropped > 0)
            {
                _logger.LogInformation("Invalidated {Count} handle(s) for exited process {Pid}.", dropped, pid);
                removed += dropped;
            }
        }
        return removed;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
