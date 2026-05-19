using System.Runtime.InteropServices;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using FluentAssertions;
using Microsoft.Diagnostics.Tracing.Session;
using Xunit;

namespace DotnetDiagnosticsMcp.Core.Tests;

/// <summary>
/// Unit and integration tests for the Windows ETW NativeAOT CPU sampler.
/// Integration tests require Windows with administrative elevation and are skipped otherwise.
/// </summary>
public class EtwNativeAotCpuSamplerTests
{
    [Fact]
    public void IsAvailable_ReturnsFalse_OnNonWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On Windows, this test is not meaningful — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        sampler.IsAvailable().Should().BeFalse("ETW is a Windows-only technology");
    }

    [Fact]
    public void IsAvailable_RespectsElevation_OnWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Not on Windows — skip.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        var isElevated = TraceEventSession.IsElevated() == true;
        sampler.IsAvailable().Should().Be(isElevated,
            "IsAvailable should match administrative elevation status");
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidDuration()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.Zero));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromMinutes(6)));
    }

    [Fact]
    public async Task SampleAsync_ThrowsOnInvalidTopN()
    {
        var sampler = new EtwNativeAotCpuSampler();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1), topN: 0));
    }

    [Fact]
    public async Task SampleAsync_ThrowsWhenNotAvailable()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && TraceEventSession.IsElevated() == true)
        {
            // Elevated on Windows — this test path does not apply.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => sampler.SampleAsync(1, TimeSpan.FromSeconds(1)));

        ex.Message.Should().Contain("administrative elevation",
            "error message should mention elevation requirement");
    }

    /// <summary>
    /// Live integration test: captures CPU samples from the current process (self-profiling).
    /// Requires Windows + admin. The current process is CoreCLR but that doesn't matter for
    /// the ETW kernel profiler — it captures all processes regardless of runtime.
    /// </summary>
    [Fact]
    public async Task SampleAsync_CapturesFromCurrentProcess_WhenElevated()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || TraceEventSession.IsElevated() != true)
        {
            // Skip: not Windows or not elevated.
            return;
        }

        var sampler = new EtwNativeAotCpuSampler();
        var pid = Environment.ProcessId;

        // Generate some CPU load on the current process.
        var cts = new CancellationTokenSource();
        var loadTask = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                // Busy loop to ensure we appear in CPU samples.
                _ = Enumerable.Range(0, 10000).Sum();
            }
        }, cts.Token);

        try
        {
            var result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(3), topN: 10);

            result.Should().NotBeNull();
            result.Summary.ProcessId.Should().Be(pid);
            result.Summary.TotalSamples.Should().BeGreaterThan(0,
                "should have captured at least one CPU sample");
            result.Summary.TopHotspots.Should().NotBeEmpty(
                "should have identified at least one hotspot");

            // At least one hotspot should have a non-address method name (resolved symbol).
            result.Summary.TopHotspots.Should().Contain(h =>
                !h.Frame.Method.StartsWith("[0x", StringComparison.Ordinal) &&
                !h.Frame.Method.StartsWith("0x", StringComparison.Ordinal),
                "at least one hotspot should have a resolved symbol name");

            result.Artifact.Root.Should().NotBeNull();
            result.Artifact.Root.Children.Should().NotBeEmpty(
                "call tree should have at least one child node");
        }
        finally
        {
            cts.Cancel();
            try { await loadTask; } catch (OperationCanceledException) { }
        }
    }
}
