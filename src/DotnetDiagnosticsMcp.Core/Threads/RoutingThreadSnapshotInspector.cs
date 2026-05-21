using DotnetDiagnosticsMcp.Core.Capabilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Core.Threads;

/// <summary>
/// Runtime-aware dispatcher for thread snapshots. CoreCLR uses ClrMD; NativeAOT on Linux uses
/// <see cref="LinuxNativeThreadSnapshotInspector"/>; NativeAOT on Windows is tracked in #93.
/// </summary>
public sealed class RoutingThreadSnapshotInspector : IThreadSnapshotInspector
{
    private readonly ICapabilityDetector _capabilities;
    private readonly ClrMdThreadSnapshotInspector _clrMd;
    private readonly LinuxNativeThreadSnapshotInspector _linuxNative;
    private readonly ILogger<RoutingThreadSnapshotInspector> _logger;

    public RoutingThreadSnapshotInspector(
        ICapabilityDetector capabilities,
        ClrMdThreadSnapshotInspector clrMd,
        LinuxNativeThreadSnapshotInspector linuxNative,
        ILogger<RoutingThreadSnapshotInspector>? logger = null)
    {
        _capabilities = capabilities;
        _clrMd = clrMd;
        _linuxNative = linuxNative;
        _logger = logger ?? NullLogger<RoutingThreadSnapshotInspector>.Instance;
    }

    public async Task<ThreadSnapshotArtifact> InspectLiveAsync(
        int processId,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var caps = await _capabilities.DetectAsync(processId, cancellationToken).ConfigureAwait(false);
        if (caps.Runtime == RuntimeFlavor.NativeAot)
        {
            if (OperatingSystem.IsLinux())
            {
                _logger.LogInformation("Routing thread snapshot for pid {Pid} to Linux NativeAOT fallback.", processId);
                var native = await _linuxNative.InspectLiveAsync(processId, options, cancellationToken).ConfigureAwait(false);
                return native with
                {
                    RuntimeName = RuntimeFlavor.NativeAot.ToString(),
                    RuntimeVersion = caps.RuntimeVersion,
                    Source = "linux-native-stack",
                };
            }

            if (OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    $"Process {processId} is NativeAOT on Windows. Native thread snapshot routing is tracked in issue #93.");
            }
        }

        var snapshot = await _clrMd.InspectLiveAsync(processId, options, cancellationToken).ConfigureAwait(false);
        return snapshot with
        {
            RuntimeName = caps.Runtime.ToString(),
            RuntimeVersion = string.IsNullOrWhiteSpace(caps.RuntimeVersion) ? snapshot.RuntimeVersion : caps.RuntimeVersion,
        };
    }

    public Task<ThreadSnapshotArtifact> InspectDumpAsync(
        string dumpFilePath,
        ThreadSnapshotOptions? options = null,
        CancellationToken cancellationToken = default)
        => _clrMd.InspectDumpAsync(dumpFilePath, options, cancellationToken);
}
