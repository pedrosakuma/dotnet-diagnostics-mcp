using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Runtime-aware dispatcher for thread snapshots. CoreCLR uses ClrMD; NativeAOT on Linux uses
/// <see cref="LinuxNativeThreadSnapshotInspector"/>; NativeAOT on Windows uses
/// <see cref="EtwNativeThreadSnapshotInspector"/>.
/// </summary>
public sealed class RoutingThreadSnapshotInspector : IThreadSnapshotInspector
{
    private readonly ICapabilityDetector _capabilities;
    private readonly IReadOnlyList<IThreadSnapshotBackend> _backends;
    private readonly ILogger<RoutingThreadSnapshotInspector> _logger;

    public RoutingThreadSnapshotInspector(
        ICapabilityDetector capabilities,
        IEnumerable<IThreadSnapshotBackend> backends,
        ILogger<RoutingThreadSnapshotInspector>? logger = null)
    {
        _capabilities = capabilities;
        _backends = backends.OrderBy(b => b.Order).ToArray();
        _logger = logger ?? NullLogger<RoutingThreadSnapshotInspector>.Instance;
    }

    public async Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var caps = await _capabilities.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
        var candidates = _backends
            .Where(b => b.CanHandleLive(caps.Runtime))
            .OrderBy(b => b.Order)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No thread snapshot backend is registered for runtime '{caps.Runtime}' on this host.");
        }

        Exception? lastRetryableFailure = null;
        foreach (var backend in candidates)
        {
            try
            {
                _logger.LogInformation(
                    "Routing thread snapshot for pid {Pid} to backend '{BackendId}'.",
                    processId,
                    backend.BackendId);
                var snapshot = await backend.InspectLiveAsync(processId, options, cancellationToken).ConfigureAwait(false);
                return snapshot with
                {
                    RuntimeName = caps.Runtime.ToString(),
                    RuntimeVersion = string.IsNullOrWhiteSpace(caps.RuntimeVersion) ? snapshot.RuntimeVersion : caps.RuntimeVersion,
                    Source = string.IsNullOrWhiteSpace(snapshot.Source) ? backend.BackendId : snapshot.Source,
                };
            }
            catch (Exception ex) when (IsRetryableFailure(ex))
            {
                lastRetryableFailure = ex;
                _logger.LogWarning(
                    ex,
                    "Thread snapshot backend '{BackendId}' failed for pid {Pid}; trying next fallback backend (if any).",
                    backend.BackendId,
                    processId);
            }
        }

        if (lastRetryableFailure is not null)
        {
            throw lastRetryableFailure;
        }

        throw new InvalidOperationException(
            $"All thread snapshot backends failed for runtime '{caps.Runtime}' on this host.");
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var dumpBackend = _backends
            .Where(b => b.CanHandleDump)
            .OrderBy(b => b.Order)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("No dump-capable thread snapshot backend is registered.");
        return dumpBackend.InspectDumpAsync(dumpFilePath, options, cancellationToken);
    }

    private static bool IsRetryableFailure(Exception ex)
        => ex is UnauthorizedAccessException
           or ExternalToolNotFoundException;
}
