using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Drilldown;

using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class CollectCpuSampleSecurityTests
{
    [Fact]
    public async Task SymbolPath_RemoteHost_NotAllowlisted_IsRejected()
    {
        var sampler = new ThrowingCpuSampler();
        var store = new MemoryDiagnosticHandleStore();


        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: @"srv*c:\sym*https://msdl.microsoft.com/download/symbols");

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("SymbolServerNotAllowed");
        sampler.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task SymbolPath_RemoteHost_OnAllowlist_PassesThrough()
    {
        var sampler = new StubCpuSampler();
        var store = new MemoryDiagnosticHandleStore();

        var options = new SecurityOptions { SymbolServerAllowlist = { "msdl.microsoft.com" } };

        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(options),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: @"srv*c:\sym*https://msdl.microsoft.com/download/symbols");

        result.Error.Should().BeNull();
        sampler.Invocations.Should().Be(1);
    }

    [Fact]
    public async Task SymbolPath_LocalPath_PassesThrough()
    {
        var sampler = new StubCpuSampler();
        var store = new MemoryDiagnosticHandleStore();


        var result = await DiagnosticTools.CollectCpuSample(
            sampler, store, ToolGuardTests.EchoResolver(),
            new SymbolServerAllowlist(null),
            TestPrincipalAccessors.Root,
            processId: 4242,
            durationSeconds: 1,
            resolveSourceLines: true,
            symbolPath: "/srv/symbols");

        result.Error.Should().BeNull();
        sampler.Invocations.Should().Be(1);
    }

    private sealed class StubCpuSampler : ICpuSampler
    {
        public int Invocations { get; private set; }

        public Task<CpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, SourceResolutionOptions? sourceResolution = null, MethodInstantiationResolutionOptions? methodInstantiationResolution = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            var summary = new CpuSample(processId, DateTimeOffset.UtcNow, duration, 0, Array.Empty<Hotspot>());
            var root = new CallTreeNode(new SampledFrame("stub", "Root"), 0, 0, Array.Empty<CallTreeNode>());
            var artifact = new CpuSampleTraceArtifact(processId, DateTimeOffset.UtcNow, duration, 0, root);
            return Task.FromResult(new CpuSampleResult(summary, artifact));
        }
    }

    private sealed class ThrowingCpuSampler : ICpuSampler
    {
        public int Invocations { get; private set; }

        public Task<CpuSampleResult> SampleAsync(int processId, TimeSpan duration, int topN = 25, SourceResolutionOptions? sourceResolution = null, MethodInstantiationResolutionOptions? methodInstantiationResolution = null, CancellationToken cancellationToken = default)
        {
            Invocations++;
            throw new InvalidOperationException("should not be reached when symbol path is rejected");
        }
    }

}
