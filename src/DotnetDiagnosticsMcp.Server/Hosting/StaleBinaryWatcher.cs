using System.Globalization;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotnetDiagnosticsMcp.Server.Hosting;

/// <summary>
/// Background watcher (issue #75) that periodically compares the ModuleVersionId of the
/// currently-loaded server assembly to the MVID of the file at its on-disk location.
/// When they diverge — e.g. <c>dotnet tool update -g DotnetDiagnosticsMcp.Server</c> ran
/// while the HTTP daemon kept serving the previous build — the watcher logs a warning
/// and, when <c>DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART=true</c>, asks the host to stop so
/// a supervisor (systemd, K8s, docker --restart=always, …) brings up a fresh instance.
/// </summary>
/// <remarks>
/// Only the HTTP transport registers this service. Under <c>--stdio</c> the MCP client
/// owns the process lifecycle, so a stale binary is fixed by the next client reload.
/// </remarks>
internal sealed class StaleBinaryWatcher : BackgroundService
{
    public const string AutoRestartEnvVar = "DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART";

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);

    private readonly ILogger<StaleBinaryWatcher> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string? _assemblyPath;
    private readonly Guid _loadedMvid;
    private readonly bool _autoRestart;
    private readonly TimeSpan _pollInterval;

    public StaleBinaryWatcher(ILogger<StaleBinaryWatcher> logger, IHostApplicationLifetime lifetime)
        : this(logger, lifetime, typeof(StaleBinaryWatcher).Assembly, DefaultPollInterval)
    {
    }

    // Test seam: callers can supply a different assembly + interval to exercise the watcher
    // against a controlled on-disk file without spinning a real 60s poll.
    // The two Assembly.Location reads below are wrapped in a method-level suppression
    // because we already guard against the single-file case at runtime: when Location
    // is empty (PublishSingleFile=true) we pass null forward and ExecuteAsync exits
    // early ("watcher disabled: Assembly.Location is empty"). IL3000 still fires because
    // the analyzer is purely syntactic, hence the suppression.
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "SingleFile",
        "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Empty Location is handled at runtime: the watcher disables itself when no on-disk path is available.")]
    internal StaleBinaryWatcher(
        ILogger<StaleBinaryWatcher> logger,
        IHostApplicationLifetime lifetime,
        Assembly assembly,
        TimeSpan pollInterval)
        : this(
            logger,
            lifetime,
            string.IsNullOrEmpty((assembly ?? throw new ArgumentNullException(nameof(assembly))).Location) ? null : assembly.Location,
            assembly.ManifestModule.ModuleVersionId,
            pollInterval)
    {
    }

    // Lower-level test seam: callers supply the on-disk path and "loaded" MVID directly,
    // sidestepping Assembly.LoadFrom's caching (which makes it impossible to load the same
    // physical file twice with different MVIDs from the same AppDomain).
    internal StaleBinaryWatcher(
        ILogger<StaleBinaryWatcher> logger,
        IHostApplicationLifetime lifetime,
        string? assemblyPath,
        Guid loadedMvid,
        TimeSpan pollInterval)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));

        _assemblyPath = string.IsNullOrEmpty(assemblyPath) ? null : assemblyPath;
        _loadedMvid = loadedMvid;
        _pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : DefaultPollInterval;

        var raw = Environment.GetEnvironmentVariable(AutoRestartEnvVar);
        _autoRestart = bool.TryParse(raw, out var parsed) && parsed;
    }

    internal Guid LoadedMvid => _loadedMvid;
    internal string? AssemblyPath => _assemblyPath;
    internal bool AutoRestart => _autoRestart;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrEmpty(_assemblyPath))
        {
            // Single-file / bundled scenarios surface an empty Assembly.Location; we cannot
            // poll a file that does not exist independently of the process image. Silently
            // disable — the host will simply not warn or restart in that mode.
            _logger.LogDebug(
                "StaleBinaryWatcher disabled: Assembly.Location is empty (single-file or bundled deploy)");
            return;
        }

        _logger.LogInformation(
            "StaleBinaryWatcher active (path={Path}, loadedMvid={Mvid}, interval={Interval}s, autoRestart={AutoRestart})",
            _assemblyPath,
            _loadedMvid.ToString("D", CultureInfo.InvariantCulture),
            _pollInterval.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
            _autoRestart);

        using var timer = new PeriodicTimer(_pollInterval);
        try
        {
            // First check fires immediately so an operator who restarted the supervisor
            // doesn't have to wait one interval to learn the daemon is already stale.
            CheckOnce();
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (CheckOnce())
                {
                    return; // Lifetime stop requested; nothing more to do.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown — exit silently.
        }
    }

    /// <summary>
    /// One MVID comparison. Returns true when a stale-binary signal was emitted AND auto-restart
    /// triggered a lifetime stop (so the caller can break out of the polling loop).
    /// </summary>
    internal bool CheckOnce()
    {
        Guid? onDisk;
        try
        {
            onDisk = ReadMvid(_assemblyPath!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            // Transient read failure (file being replaced mid-poll, permission issue, partial
            // download). Log at Debug — we'll retry on the next tick.
            _logger.LogDebug(ex, "StaleBinaryWatcher could not read MVID of {Path}", _assemblyPath);
            return false;
        }

        if (onDisk is null || onDisk.Value == _loadedMvid)
        {
            return false;
        }

        const string template =
            "On-disk binary MVID has changed since this server was loaded " +
            "(loaded={Loaded}, onDisk={OnDisk}, path={Path}, autoRestart={AutoRestart}). " +
            "The running daemon is serving stale code; restart it (or run under a supervisor with " +
            "DOTNET_DIAGNOSTICS_MCP_AUTO_RESTART=true) to pick up the new build.";

        _logger.LogWarning(
            template,
            _loadedMvid.ToString("D", CultureInfo.InvariantCulture),
            onDisk.Value.ToString("D", CultureInfo.InvariantCulture),
            _assemblyPath,
            _autoRestart);

        if (_autoRestart)
        {
            _lifetime.StopApplication();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Reads the ModuleVersionId from a managed PE file via <see cref="MetadataReader"/>.
    /// Returns null when the file does not exist; rethrows IO / format failures so the
    /// poller can decide how to react.
    /// </summary>
    internal static Guid? ReadMvid(string path)
    {
        if (!File.Exists(path)) return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var mvidHandle = reader.GetModuleDefinition().Mvid;
        return reader.GetGuid(mvidHandle);
    }
}
