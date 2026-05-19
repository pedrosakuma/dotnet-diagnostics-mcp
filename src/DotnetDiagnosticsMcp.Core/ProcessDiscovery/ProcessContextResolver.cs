using System.Collections.Concurrent;
using DotnetDiagnosticsMcp.Core.Capabilities;

namespace DotnetDiagnosticsMcp.Core.ProcessDiscovery;

/// <summary>
/// Default <see cref="IProcessContextResolver"/> implementation.
/// </summary>
/// <remarks>
/// Auto-resolution semantics (when <c>requestedProcessId</c> is null/zero):
/// <list type="bullet">
///   <item><b>0 candidates</b> → <c>NoDotnetProcessFound</c>.</item>
///   <item><b>1 candidate</b> → returns it with <see cref="ProcessContext.AutoResolved"/> = <c>true</c>.</item>
///   <item><b>N candidates</b> → <c>AmbiguousDotnetProcess</c> carrying the list inline.</item>
/// </list>
/// Capability digests are cached per-pid for <see cref="DefaultCacheTtl"/> so that follow-up
/// tool calls within an investigation pay the probe cost once. Cache entries are invalidated
/// implicitly when the pid is no longer reachable (next miss re-detects).
/// </remarks>
public sealed class ProcessContextResolver : IProcessContextResolver
{
    public static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(60);

    private readonly IProcessDiscovery _discovery;
    private readonly ICapabilityDetector _detector;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<int, CacheEntry> _cache = new();

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector)
        : this(discovery, detector, TimeProvider.System, DefaultCacheTtl)
    {
    }

    public ProcessContextResolver(IProcessDiscovery discovery, ICapabilityDetector detector, TimeProvider clock, TimeSpan cacheTtl)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _ttl = cacheTtl;
    }

    public async Task<ProcessContextResolution> ResolveAsync(int? requestedProcessId, CancellationToken cancellationToken)
    {
        if (requestedProcessId is { } pid && pid > 0)
        {
            return await ResolveExplicitAsync(pid, autoResolved: false, cancellationToken).ConfigureAwait(false);
        }

        var processes = _discovery.ListProcesses();
        if (processes.Count == 0)
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "NoDotnetProcessFound",
                    "No .NET process exposes a diagnostic IPC endpoint on this host.",
                    "Verify the target is running, that you share its PID namespace (in containers / Kubernetes), and that the sidecar runs as the same UID as the target."));
        }

        if (processes.Count > 1)
        {
            var preview = string.Join(", ", processes.Take(5).Select(p => $"{p.ProcessId}={p.ManagedEntrypointAssemblyName ?? "?"}"));
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "AmbiguousDotnetProcess",
                    $"{processes.Count} .NET processes are visible: {preview}{(processes.Count > 5 ? ", …" : "")}. Pass processId explicitly.",
                    null),
                Candidates: processes);
        }

        return await ResolveExplicitAsync(processes[0].ProcessId, autoResolved: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessContextResolution> ResolveExplicitAsync(int pid, bool autoResolved, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(pid, out var entry) && _clock.GetUtcNow() < entry.ExpiresAt)
        {
            return new ProcessContextResolution(
                entry.Context with { AutoResolved = autoResolved },
                Error: null);
        }

        DiagnosticCapabilities caps;
        try
        {
            caps = await _detector.DetectAsync(pid, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProcessContextResolution(
                Context: null,
                Error: new DiagnosticError(
                    "EndpointUnavailable",
                    $"Could not probe pid {pid}: {ex.Message}",
                    ex.GetType().FullName));
        }

        var context = new ProcessContext(
            ProcessId: pid,
            Runtime: caps.Runtime,
            RuntimeVersion: string.IsNullOrEmpty(caps.RuntimeVersion) ? null : caps.RuntimeVersion,
            CanSampleCpu: caps.CanSampleCpu,
            CanCollectGcDump: caps.CanCollectGcDump,
            AutoResolved: autoResolved);

        _cache[pid] = new CacheEntry(context, _clock.GetUtcNow() + _ttl);
        return new ProcessContextResolution(context, Error: null);
    }

    /// <summary>For tests: drops every cached entry.</summary>
    internal void ClearCache() => _cache.Clear();

    private sealed record CacheEntry(ProcessContext Context, DateTimeOffset ExpiresAt);
}
