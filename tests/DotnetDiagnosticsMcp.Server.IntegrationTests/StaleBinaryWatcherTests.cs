using System.Reflection;
using DotnetDiagnosticsMcp.Server.Hosting;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// Unit tests for the on-disk MVID drift detector (issue #75). The watcher itself
/// is an <c>IHostedService</c> but the business logic is encapsulated in the
/// synchronous <c>CheckOnce</c> + <c>ReadMvid</c> seams so we can exercise it
/// without spinning real hosts or 60-second timers.
/// </summary>
public class StaleBinaryWatcherTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _baselinePath;
    private readonly string _replacementPath;

    public StaleBinaryWatcherTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "dotnet-diag-mcp-watcher-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        // Two distinct managed assemblies built at test runtime so we have known, different
        // MVIDs to compare. We compile to a temp file then copy under a stable name so we
        // can swap the on-disk content without touching the in-memory assembly.
        _baselinePath = Path.Combine(_tempDir, "Probe.dll");
        _replacementPath = Path.Combine(_tempDir, "Probe.replacement.dll");

        // Reuse a known-good assembly from the test host. We only need *any* valid managed
        // PE to read an MVID from; the loaded assembly under test is StaleBinaryWatcher's
        // own (DotnetDiagnosticsMcp.Server), and the on-disk file we swap is the
        // BackgroundService itself's assembly path.
        var seed = typeof(FluentAssertions.AssertionExtensions).Assembly.Location;
        File.Copy(seed, _baselinePath);

        // Distinct second assembly with a different MVID.
        var altSeed = typeof(Xunit.FactAttribute).Assembly.Location;
        File.Copy(altSeed, _replacementPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ReadMvid_ReturnsNullForMissingFile()
    {
        var missing = Path.Combine(_tempDir, "does-not-exist.dll");
        StaleBinaryWatcher.ReadMvid(missing).Should().BeNull();
    }

    [Fact]
    public void ReadMvid_ReturnsStableGuidForSameFile()
    {
        var first = StaleBinaryWatcher.ReadMvid(_baselinePath);
        var second = StaleBinaryWatcher.ReadMvid(_baselinePath);
        first.Should().NotBeNull();
        first.Should().Be(second!.Value, "MVID must be a deterministic function of the file contents");
    }

    [Fact]
    public void ReadMvid_ReturnsDifferentGuidsForDifferentBinaries()
    {
        var baseline = StaleBinaryWatcher.ReadMvid(_baselinePath);
        var replacement = StaleBinaryWatcher.ReadMvid(_replacementPath);
        baseline.Should().NotBeNull();
        replacement.Should().NotBeNull();
        baseline.Should().NotBe(replacement!.Value, "two different assemblies must have distinct MVIDs");
    }

    [Fact]
    public void CheckOnce_DoesNothing_WhenOnDiskMvidMatchesLoaded()
    {
        // Loaded assembly == the binary on disk (this very test assembly works).
        var loaded = typeof(StaleBinaryWatcherTests).Assembly;

        var lifetime = new StubLifetime();
        var watcher = new StaleBinaryWatcher(
            NullLogger<StaleBinaryWatcher>.Instance,
            lifetime,
            loaded,
            TimeSpan.FromMilliseconds(50));

        var requestedStop = watcher.CheckOnce();

        requestedStop.Should().BeFalse();
        lifetime.StopRequested.Should().BeFalse("MVID matches — no warning, no shutdown");
    }

    [Fact]
    public void CheckOnce_WarnsAndDoesNotStop_WhenDriftDetectedAndAutoRestartDisabled()
    {
        // Watcher captures baseline's MVID as "loaded"; then we simulate `dotnet tool update`
        // by overwriting the on-disk file with a different binary.
        var loadedMvid = StaleBinaryWatcher.ReadMvid(_baselinePath)!.Value;
        Environment.SetEnvironmentVariable(StaleBinaryWatcher.AutoRestartEnvVar, null);
        var lifetime = new StubLifetime();
        var watcher = new StaleBinaryWatcher(
            NullLogger<StaleBinaryWatcher>.Instance,
            lifetime,
            _baselinePath,
            loadedMvid,
            TimeSpan.FromMilliseconds(50));
        watcher.AutoRestart.Should().BeFalse();

        File.Copy(_replacementPath, _baselinePath, overwrite: true);

        var requestedStop = watcher.CheckOnce();

        requestedStop.Should().BeFalse("auto-restart is off — warn only");
        lifetime.StopRequested.Should().BeFalse();
    }

    [Fact]
    public void CheckOnce_RequestsLifetimeStop_WhenDriftDetectedAndAutoRestartEnabled()
    {
        Environment.SetEnvironmentVariable(StaleBinaryWatcher.AutoRestartEnvVar, "true");
        try
        {
            var loadedMvid = StaleBinaryWatcher.ReadMvid(_baselinePath)!.Value;
            var lifetime = new StubLifetime();
            var watcher = new StaleBinaryWatcher(
                NullLogger<StaleBinaryWatcher>.Instance,
                lifetime,
                _baselinePath,
                loadedMvid,
                TimeSpan.FromMilliseconds(50));
            watcher.AutoRestart.Should().BeTrue();

            File.Copy(_replacementPath, _baselinePath, overwrite: true);

            var requestedStop = watcher.CheckOnce();

            requestedStop.Should().BeTrue();
            lifetime.StopRequested.Should().BeTrue("AUTO_RESTART=true must trigger graceful shutdown so a supervisor restarts the process");
        }
        finally
        {
            Environment.SetEnvironmentVariable(StaleBinaryWatcher.AutoRestartEnvVar, null);
        }
    }

    private static Assembly LoadAssemblyFrom(string path) => Assembly.LoadFrom(path);

    private sealed class StubLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public bool StopRequested { get; private set; }
        public void StopApplication() => StopRequested = true;
    }
}
