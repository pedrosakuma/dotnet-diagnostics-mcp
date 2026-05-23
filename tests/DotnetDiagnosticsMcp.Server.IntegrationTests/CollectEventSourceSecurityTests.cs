using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Jobs;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Server.Tools;
using FluentAssertions;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public sealed class CollectEventSourceSecurityTests
{
    [Fact]
    public async Task UnknownProvider_DefaultPolicy_ReturnsNotAllowed()
    {
        var collector = new RecordingCollector();
        var result = await DiagnosticTools.CollectEventSource(
            collector,
            ToolGuardTests.EchoResolver(),
            new MemoryDiagnosticHandleStore(),
            new EventSourceAllowlist(null),
            new SensitiveValueGate(null),
            providerName: "My.Custom.Source",
            processId: 4242,
            durationSeconds: 1);

        result.Error.Should().NotBeNull();
        result.Error!.Kind.Should().Be("EventSourceProviderNotAllowed");
        collector.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task AllowlistedProvider_BypassesGate()
    {
        var collector = new RecordingCollector();
        var result = await DiagnosticTools.CollectEventSource(
            collector,
            ToolGuardTests.EchoResolver(),
            new MemoryDiagnosticHandleStore(),
            new EventSourceAllowlist(null),
            new SensitiveValueGate(null),
            providerName: "System.Net.Http",
            processId: 4242,
            durationSeconds: 1);

        result.Error.Should().BeNull();
        collector.Invocations.Should().Be(1);
        collector.LastProvider.Should().Be("System.Net.Http");
    }

    private sealed class RecordingCollector : IEventSourceCollector
    {
        public int Invocations { get; private set; }
        public string? LastProvider { get; private set; }

        public Task<EventSourceCapture> CaptureAsync(int processId, string providerName, TimeSpan duration, long keywords = -1, int eventLevel = 5, int maxEvents = 200, CancellationToken cancellationToken = default)
        {
            Invocations++;
            LastProvider = providerName;
            return Task.FromResult(new EventSourceCapture(processId, providerName, DateTimeOffset.UtcNow, duration, 0, Array.Empty<CapturedEvent>()));
        }
    }
}
