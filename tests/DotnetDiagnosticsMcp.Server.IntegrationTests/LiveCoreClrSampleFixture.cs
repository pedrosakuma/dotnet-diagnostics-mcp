using System.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class LiveCoreClrSampleFixture : IAsyncLifetime
{
    private Process? _sampleProcess;

    public int ProcessId => _sampleProcess?.Id ?? throw new InvalidOperationException("Sample not started.");

    public async Task InitializeAsync()
    {
        var sampleDll = LocateSampleDll()
            ?? throw new InvalidOperationException("CoreClrSample.dll not found. Build the sample before running integration tests.");

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

        _sampleProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start CoreClrSample.");

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _sampleProcess.StandardOutput;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { }
            }
            catch
            {
                // best effort
            }
        });

        _ = Task.Run(async () =>
        {
            try
            {
                using var reader = _sampleProcess.StandardError;
                while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { }
            }
            catch
            {
                // best effort
            }
        });

        await WaitForDiagnosticEndpointAsync(_sampleProcess.Id, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
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
            catch
            {
                // best effort
            }
        }

        _sampleProcess?.Dispose();
        return Task.CompletedTask;
    }

    private static async Task WaitForDiagnosticEndpointAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (DiagnosticsClient.GetPublishedProcesses().Contains(processId))
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }

        throw new TimeoutException($"CoreClrSample pid {processId} did not expose a diagnostic endpoint within {timeout}.");
    }

    private static string? LocateSampleDll()
    {
        var probe = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var sampleDir = Path.Combine(probe, "samples", "CoreClrSample");
            if (Directory.Exists(sampleDir))
            {
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
