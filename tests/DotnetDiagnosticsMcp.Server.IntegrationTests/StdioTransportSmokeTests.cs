using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

/// <summary>
/// End-to-end smoke for the <c>--stdio</c> CLI mode (issue #74). Spawns the published
/// server DLL as a subprocess (mirroring how an MCP client like Copilot CLI / Claude
/// Desktop / Cursor would launch it locally), feeds the JSON-RPC initialize handshake
/// plus a tools/list call over stdin, and asserts that:
/// <list type="bullet">
///   <item>stdout carries valid JSON-RPC responses (the wire channel stays clean);</item>
///   <item>logs go to stderr only (so they cannot corrupt the wire);</item>
///   <item>the server shuts down cleanly when stdin is closed (parent-disconnect semantics).</item>
/// </list>
/// The test is gated by the same environment guard the HealthCheckCommand E2E uses —
/// the published DLL must exist on disk (Release build with this same TFM).
/// </summary>
[Collection(nameof(EnvSerial))]
public class StdioTransportSmokeTests
{
    [Fact]
    public async Task StdioTransport_HandlesInitializeAndListsTools_OverChildProcessStdio()
    {
        var serverDll = FindServerDll();
        if (serverDll is null)
        {
            // No Release build on disk — skip rather than fail. CI builds Release before
            // testing, but local `dotnet test --no-build` against a Debug build will skip.
            return;
        }

        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { serverDll, "--stdio" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (stdout) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"stdio-smoke","version":"1"}}}""");
        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await proc.StandardInput.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await proc.StandardInput.FlushAsync();

        // Wait for both responses to drain, then close stdin to trigger graceful exit.
        // Cold-start of `dotnet ServerDll --stdio` on a CI runner with a fresh NuGet cache
        // can take 3-4 s before the first JSON-RPC byte is written; 5 s gives headroom
        // without making the happy path slow.
        await Task.Delay(TimeSpan.FromSeconds(5));
        proc.StandardInput.Close();

        var exited = proc.WaitForExit(TimeSpan.FromSeconds(30));
        if (!exited)
        {
            proc.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException("--stdio process did not exit within 30s after stdin close");
        }
        proc.WaitForExit(); // flush async output collectors

        string capturedStdout, capturedStderr;
        lock (stdout) capturedStdout = stdout.ToString();
        lock (stderr) capturedStderr = stderr.ToString();

        proc.ExitCode.Should().Be(0, "graceful shutdown on stdin EOF must exit cleanly");

        // Wire-cleanness: stdout MUST contain ONLY JSON-RPC envelopes (every non-empty line
        // parses as JSON). A single log statement leaking onto stdout would corrupt the
        // protocol stream for the client.
        var stdoutLines = capturedStdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        stdoutLines.Should().NotBeEmpty("stdout must carry initialize + tools/list responses");
        foreach (var line in stdoutLines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            using var _ = JsonDocument.Parse(trimmed); // throws if anything but valid JSON leaked
        }

        // Both expected response ids must appear.
        capturedStdout.Should().Contain("\"id\":1", "initialize response must be emitted");
        capturedStdout.Should().Contain("\"id\":2", "tools/list response must be emitted");
        capturedStdout.Should().Contain("collect_events", "tools/list response must include the registered tool surface");

        // Logs (if any) MUST land on stderr only. The exact lines depend on Hosting telemetry
        // verbosity; we only assert no JSON-RPC payload accidentally went to stderr.
        capturedStderr.Should().NotContain("\"jsonrpc\"",
            "JSON-RPC envelopes must never appear on stderr — that means stdout/stderr were crossed");
    }

    private static string? FindServerDll()
    {
        var here = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = new DirectoryInfo(here);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "DotnetDiagnosticsMcp.slnx")))
        {
            current = current.Parent;
        }
        if (current is null) return null;

        var tfm = Path.GetFileName(here); // net10.0
        var cfg = Path.GetFileName(Path.GetDirectoryName(here)!); // Release|Debug
        var candidate = Path.Combine(current.FullName, "src", "DotnetDiagnosticsMcp.Server", "bin", cfg, tfm, "DotnetDiagnosticsMcp.Server.dll");
        return File.Exists(candidate) ? candidate : null;
    }
}
