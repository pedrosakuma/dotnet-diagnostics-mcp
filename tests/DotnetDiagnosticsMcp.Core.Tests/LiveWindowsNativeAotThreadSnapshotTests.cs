using System.Diagnostics;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Threads;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;

namespace DotnetDiagnosticsMcp.Core.Tests;

[Collection("LiveProcess")]
public sealed class LiveWindowsNativeAotThreadSnapshotTests : IAsyncLifetime
{
    private Process? _sampleProcess;
    private string? _publishDir;

    public async Task InitializeAsync()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        {
            return;
        }

        var inspector = new EtwNativeThreadSnapshotInspector();
        if (!inspector.IsAvailable())
        {
            return;
        }

        var publishDir = Path.Combine(Path.GetTempPath(), $"diagnosticsmcp-nativeaot-win-{Guid.NewGuid():N}");
        Directory.CreateDirectory(publishDir);

        try
        {
            var sampleProject = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "samples", "NativeAotSample", "NativeAotSample.csproj"));
            await PublishAsync(sampleProject, publishDir, CancellationToken.None);
            _publishDir = publishDir;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("dotnet publish failed", StringComparison.Ordinal))
        {
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort */ }
            return;
        }
        catch
        {
            try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort */ }
            throw;
        }
    }

    public Task DisposeAsync()
    {
        if (_sampleProcess is { HasExited: false })
        {
            try
            {
                _sampleProcess.Kill(entireProcessTree: true);
                _sampleProcess.WaitForExit(5_000);
            }
            catch (Exception)
            {
                // best effort
            }
        }
        _sampleProcess?.Dispose();

        if (!string.IsNullOrWhiteSpace(_publishDir))
        {
            try { Directory.Delete(_publishDir, recursive: true); } catch { /* best effort */ }
        }

        return Task.CompletedTask;
    }

    // NativeAOT publish on Windows CI can take significantly longer than non-AOT live tests.
    [Fact(Timeout = 180_000)]
    public async Task RoutingInspector_OnWindowsNativeAot_ReturnsEtwNativeThreadSnapshot()
    {
        if (!OperatingSystem.IsWindows() || TraceEventSession.IsElevated() != true)
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(_publishDir))
        {
            return;
        }

        var exePath = Path.Combine(_publishDir, "NativeAotSample.exe");
        if (!File.Exists(exePath))
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _publishDir,
        };
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi);
        _sampleProcess.Should().NotBeNull();

        _ = Task.Run(async () =>
        {
            try { while (await _sampleProcess!.StandardError.ReadLineAsync() is not null) { } }
            catch { /* best effort */ }
        });
        _ = Task.Run(async () =>
        {
            try { while (await _sampleProcess!.StandardOutput.ReadLineAsync() is not null) { } }
            catch { /* best effort */ }
        });

        await WaitForDiagnosticEndpointAsync(_sampleProcess!.Id, TimeSpan.FromSeconds(45));

        var detector = new CapabilityDetector(etwSampler: new DotnetDiagnosticsMcp.Core.CpuSampling.EtwNativeAotCpuSampler());
        var caps = await detector.DetectAsync(_sampleProcess.Id, CancellationToken.None);
        caps.Runtime.Should().Be(RuntimeFlavor.NativeAot);
        caps.CanCollectThreadSnapshot.Should().BeTrue();
        caps.ThreadSnapshotSource.Should().Be("etw-native-stack");

        var inspector = new RoutingThreadSnapshotInspector(
            detector,
            new IThreadSnapshotBackend[]
            {
                new ClrMdThreadSnapshotBackend(new ClrMdThreadSnapshotInspector()),
                new LinuxNativeThreadSnapshotBackend(new LinuxNativeThreadSnapshotInspector()),
                new EtwNativeThreadSnapshotBackend(new EtwNativeThreadSnapshotInspector()),
            });

        var snapshot = await inspector.InspectLiveAsync(
            _sampleProcess.Id,
            new ThreadSnapshotOptions(MaxFramesPerThread: 32),
            CancellationToken.None);

        snapshot.Origin.Should().Be(ThreadSnapshotOrigin.Live);
        snapshot.Source.Should().Be("etw-native-stack");
        snapshot.RuntimeName.Should().Be("NativeAot");
        snapshot.Threads.Should().NotBeEmpty();
        snapshot.Threads.Select(t => t.OSThreadId).Should().OnlyHaveUniqueItems();
        snapshot.Threads.Count.Should().BeGreaterThan(0);
        snapshot.Threads.SelectMany(t => t.Frames).Should().NotBeEmpty();
    }

    private static async Task PublishAsync(string sampleProject, string publishDir, CancellationToken cancellationToken)
    {
        var args = $"publish \"{sampleProject}\" -c Release -r win-x64 -p:PublishAot=true -o \"{publishDir}\" --self-contained true";
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"dotnet publish failed for NativeAotSample: {stderr.Trim()}");
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var discovery = new LocalProcessDiscovery();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var info = discovery.TryGetProcess(pid);
            if (info is not null)
            {
                return;
            }
            await Task.Delay(250);
        }
        throw new TimeoutException($"Timed out waiting for native sample diagnostic endpoint for pid {pid}.");
    }
}
