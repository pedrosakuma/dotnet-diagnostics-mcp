using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Server.Hosting;
using FluentAssertions;
using Xunit;

namespace DotnetDiagnosticsMcp.Server.IntegrationTests;

public class HandleEvictionBackgroundServiceTests
{
    [Fact]
    public void EvictDeadProcesses_RemovesHandlesForExitedPids()
    {
        var store = new MemoryDiagnosticHandleStore();
        // Current process is definitely alive.
        var keep = store.Register(Environment.ProcessId, "cpu-sample", new Payload("alive"), TimeSpan.FromMinutes(5));
        // PID 1_000_000_000 is essentially never a live process on a CI runner.
        var drop = store.Register(1_000_000_000, "cpu-sample", new Payload("dead"), TimeSpan.FromMinutes(5));

        var svc = new HandleEvictionBackgroundService(store);
        var removed = svc.EvictDeadProcesses();

        removed.Should().BeGreaterOrEqualTo(1, "the synthetic dead PID must be invalidated");
        store.TryGet<Payload>(drop.Id).Should().BeNull();
        store.TryGet<Payload>(keep.Id).Should().NotBeNull("the live process handle must survive the sweep");
    }

    private sealed record Payload(string Value);
}
