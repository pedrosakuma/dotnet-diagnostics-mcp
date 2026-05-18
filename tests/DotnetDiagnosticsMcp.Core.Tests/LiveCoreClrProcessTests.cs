using System.Diagnostics;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// End-to-end tests that spawn the <c>CoreClrSample</c> webapi and exercise the diagnostic
/// pipeline against it. The sample project is built+run via <c>dotnet run</c> so the test only
/// requires the .NET SDK to be on PATH (CI satisfies this).
/// </summary>
[Collection("LiveProcess")]
public class LiveCoreClrProcessTests : IAsyncLifetime
{
    private Process? _sampleProcess;

    private int Pid => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");

    public async Task InitializeAsync()
    {
        var sampleDll = LocateSampleDll();
        if (sampleDll is null)
        {
            return;
        }

        // Execute the published DLL directly with `dotnet <dll>` so the captured PID
        // *is* the application process — `dotnet run` creates a wrapper process whose
        // own EventCounters surface (mostly idle) sometimes returns no payloads
        // before our short collection window ends, which made tests flaky.
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(sampleDll)!,
        };
        psi.ArgumentList.Add(sampleDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add("http://127.0.0.1:0");
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        _sampleProcess = Process.Start(psi);
        if (_sampleProcess is null)
        {
            return;
        }

        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30));
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
                // best-effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Discovery_FindsRunningSample()
    {
        EnsureSampleRunning();

        var discovery = new LocalProcessDiscovery();
        var processes = discovery.ListProcesses();
        processes.Should().Contain(p => p.ProcessId == Pid);

        var info = discovery.TryGetProcess(Pid);
        info.Should().NotBeNull();
        info!.CommandLine.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Capabilities_DetectsCoreClr()
    {
        EnsureSampleRunning();

        var detector = new CapabilityDetector();
        var caps = await detector.DetectAsync(Pid, CancellationToken.None);

        caps.Runtime.Should().Be(RuntimeFlavor.CoreClr);
        caps.CanSampleCpu.Should().BeTrue();
        caps.CanReadEventCounters.Should().BeTrue();
    }

    [Fact]
    public async Task Counters_ReturnsSystemRuntimeMetrics()
    {
        EnsureSampleRunning();

        var collector = new EventPipeCounterCollector();
        var snapshot = await collector.CollectAsync(
            Pid,
            // EventPipe session startup takes ~500ms-1s, then EventCounters are emitted
            // at intervalSeconds boundaries. 6s gives consistent headroom for 3-5 ticks.
            TimeSpan.FromSeconds(6),
            providers: new[] { "System.Runtime" },
            intervalSeconds: 1,
            cancellationToken: CancellationToken.None);

        snapshot.Counters.Should().NotBeEmpty();
        snapshot.Counters.Should().Contain(c => c.Provider == "System.Runtime" && c.Name == "cpu-usage");
    }

    [Fact]
    public async Task CpuSampler_ProducesHotspots()
    {
        EnsureSampleRunning();

        var sampler = new EventPipeCpuSampler();
        var sample = await sampler.SampleAsync(
            Pid,
            TimeSpan.FromSeconds(3),
            topN: 10,
            cancellationToken: CancellationToken.None);

        sample.TotalSamples.Should().BeGreaterThan(0);
        sample.TopHotspots.Should().NotBeEmpty();
    }

    private void EnsureSampleRunning()
    {
        if (_sampleProcess is null || _sampleProcess.HasExited)
        {
            throw SkipException.ForReason("CoreClrSample is not running (could not start the sample process).");
        }
    }

    private static async Task WaitForDiagnosticEndpointAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient
                .GetPublishedProcesses()
                .Contains(pid))
            {
                return;
            }

            await Task.Delay(500);
        }
    }

    private static string? LocateSampleDll()
    {
        // Walk up from the test bin dir until we find the repo root containing samples/.
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", "CoreClrSample");
            if (Directory.Exists(sampleDir))
            {
                // Match the configuration the tests were built with — falls back to whichever exists.
                foreach (var configuration in new[] { "Release", "Debug" })
                {
                    var dll = Path.Combine(sampleDir, "bin", configuration, "net10.0", "CoreClrSample.dll");
                    if (File.Exists(dll))
                    {
                        return dll;
                    }
                }

                return null;
            }

            probe = Path.GetFullPath(Path.Combine(probe, ".."));
        }

        return null;
    }
}

/// <summary>Thrown to skip a test (in lieu of pulling a separate Skippable package).</summary>
public sealed class SkipException : Exception
{
    private SkipException(string reason) : base(reason)
    {
    }

    public static SkipException ForReason(string reason) => new(reason);
}

[CollectionDefinition("LiveProcess", DisableParallelization = true)]
public class LiveProcessCollection;
